using System.Security.Cryptography;
using System.Text;
using CoverAO.Protocol;

namespace CoverAO.Server.Data;

/// <summary>An account and its character roster, mapped to the .cnt file (Cuentas.bas).</summary>
public sealed class Account
{
    public required string Name { get; init; }
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public bool Banned { get; set; }
    /// <summary>Flag donador a nivel CUENTA (VB6 .cnt "Donador"). Al loguear fuerza Donador=1 en el char
    /// (TCP.bas:1863), así todos los personajes de una cuenta premium heredan el beneficio.</summary>
    public bool Donador { get; set; }
    public List<string> Characters { get; } = new();

    public bool VerifyPassword(string password) => VerifyPassword(password, out _);

    /// <summary>Verifies the password. Modern (PBKDF2) hashes verify directly; on a successful LEGACY
    /// (SHA-256) verify the stored hash is upgraded to PBKDF2 in place and <paramref name="upgraded"/>
    /// is set so the caller persists it (rehash-on-login). This migrates old .cnt hashes to a slow KDF
    /// transparently the first time each user logs in.</summary>
    public bool VerifyPassword(string password, out bool upgraded)
    {
        upgraded = false;
        if (string.IsNullOrEmpty(PasswordHash)) return false;
        if (PasswordHasher.IsModern(PasswordHash))
            return PasswordHasher.VerifyModern(password, PasswordHash);
        // Legacy SHA-256(password+salt): constant-time compare, then migrate on success.
        bool ok = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(PasswordHasher.Hash(password, Salt)),
            Encoding.ASCII.GetBytes(PasswordHash));
        if (ok)
        {
            PasswordHash = PasswordHasher.HashModern(password);   // rehash to the slow KDF
            Salt = "";                                            // salt now lives inside the modern hash
            upgraded = true;
        }
        return ok;
    }
}

/// <summary>
/// Password hashing. NEW accounts (and any legacy account on its next login) use PBKDF2-HMAC-SHA256, a
/// slow KDF, stored self-describing as <c>pbkdf2$&lt;iter&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>. The
/// original scheme (SHA-256 of password+salt, 10-char salt — Cuentas.bas:76-83) is kept only to VERIFY
/// pre-existing .cnt hashes so they can be migrated (see <see cref="Account.VerifyPassword(string,out bool)"/>).
/// PBKDF2 is in the BCL (no external dependency); bcrypt/argon2 would need a NuGet package.
/// </summary>
public static class PasswordHasher
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string ModernPrefix = "pbkdf2$";
    private const int Iterations = 210_000;   // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    private const int SaltBytes = 16, HashBytes = 32;

    public static string NewSalt()
    {
        var chars = new char[10];
        Span<byte> rnd = stackalloc byte[10];
        RandomNumberGenerator.Fill(rnd);
        for (int i = 0; i < 10; i++) chars[i] = Alphabet[rnd[i] % Alphabet.Length];
        return new string(chars);
    }

    /// <summary>Legacy SHA-256(password + salt) as lowercase hex (64 chars). Verify-only (migration).</summary>
    public static string Hash(string password, string salt)
    {
        var bytes = SHA256.HashData(Codec.Ansi.GetBytes(password + salt));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>True if <paramref name="stored"/> is a modern PBKDF2 hash (vs a legacy SHA-256 hex).</summary>
    public static bool IsModern(string? stored) => stored is not null && stored.StartsWith(ModernPrefix, StringComparison.Ordinal);

    /// <summary>PBKDF2-HMAC-SHA256 with a random salt, encoded as pbkdf2$&lt;iter&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;.</summary>
    public static string HashModern(string password)
    {
        Span<byte> salt = stackalloc byte[SaltBytes];
        RandomNumberGenerator.Fill(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Codec.Ansi.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{ModernPrefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Quema el mismo tiempo de CPU que una verificación real (mismo PBKDF2) sin comparar contra
    /// nada. Se llama cuando la CUENTA NO EXISTE, para que el tiempo de respuesta no delate si una cuenta
    /// existe o no (canal lateral de timing → enumeración). El login usa además el mismo mensaje genérico.</summary>
    public static void DummyVerify(string password)
        => Rfc2898DeriveBytes.Pbkdf2(Codec.Ansi.GetBytes(password), DummySalt, Iterations, HashAlgorithmName.SHA256, HashBytes);
    private static readonly byte[] DummySalt = new byte[SaltBytes];

    /// <summary>Verifies a password against a modern (PBKDF2) hash string. Constant-time comparison.</summary>
    public static bool VerifyModern(string password, string stored)
    {
        var parts = stored.Split('$');   // ["pbkdf2", iter, saltB64, hashB64] — base64 never contains '$'
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iter) || iter < 1) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Codec.Ansi.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
