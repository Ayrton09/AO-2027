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
    /// <summary>La cuenta es de un BOT de este servidor, no de una persona. Se marca al crearla y no se saca
    /// nunca; lo que hace es que la cuenta SÓLO se pueda abrir desde el transporte de bot
    /// (<see cref="Net.TransporteDeBot"/>), o sea desde adentro de este mismo proceso.
    ///
    /// <para>Existe por dónde vive el secreto: la contraseña de un bot la sortea el servidor y queda escrita
    /// en <c>data/bots.txt</c>, que además se copia en cada backup. Sin esta marca, ese archivo alcanzaría
    /// para entrar a jugar con el personaje de un bot, y sería un secreto que se propaga solo — a un backup,
    /// a otra máquina, a quien le pidan ayuda al hosteador. Con la marca, la contraseña no vale nada fuera
    /// del proceso.</para></summary>
    public bool EsBot { get; set; }
    /// <summary>Trampas acumuladas por el anticheat (PacketDispatcher.RegistrarTrampa). Persiste en el .cnt.</summary>
    public int Trampas { get; set; }
    /// <summary>Flag donador a nivel CUENTA (VB6 .cnt "Donador"). Al loguear fuerza Donador=1 en el char
    /// (TCP.bas:1863), así todos los personajes de una cuenta premium heredan el beneficio.</summary>
    public bool Donador { get; set; }
    /// <summary>Hash del CÓDIGO DE RECUPERACIÓN de la cuenta (VB6 .cnt "UserCodigo"). Es el segundo secreto
    /// que pide la creación de cuenta y lo único que permite recuperarla si se pierde la contraseña
    /// (<c>HandleProcesosLogin</c> con Tipo 0, Protocol.bas:15748). Se guarda hasheado igual que la clave: el
    /// que hostea lee el .cnt, y en texto plano sería una segunda contraseña regalada.</summary>
    public string UserCodeHash { get; set; } = "";
    /// <summary>El último ingreso con la contraseña bien, en UTC (VB6 .cnt "UltimoAcceso", Cuentas.bas:443). null = nunca,
    /// o una cuenta importada sin el dato.</summary>
    public DateTime? UltimoIngreso { get; set; }
    /// <summary>Desde qué IP fue ese ingreso (VB6 .cnt "UltimaIP", Cuentas.bas:444). No se muestra en ninguna web.</summary>
    public string UltimaIp { get; set; } = "";
    public List<string> Characters { get; } = new();

    /// <summary>Una copia para escribir desde otro hilo: la cuenta viva puede cambiar mientras la cola de la base la guarda
    /// (<see cref="Persistence.SaveAccount"/>). Un campo nuevo tiene que entrar acá; lo cuida un test que las compara
    /// propiedad por propiedad.</summary>
    public Account Copia()
    {
        var copia = new Account
        {
            Name = Name, PasswordHash = PasswordHash, Salt = Salt, Banned = Banned, EsBot = EsBot, Trampas = Trampas,
            Donador = Donador, UserCodeHash = UserCodeHash, UltimoIngreso = UltimoIngreso, UltimaIp = UltimaIp,
        };
        copia.Characters.AddRange(Characters);
        return copia;
    }

    public bool VerifyPassword(string password) => VerifyPassword(password, out _);

    /// <summary>Verifica el código de recuperación. Sin código guardado devuelve false: una cuenta vieja
    /// —creada antes de que esto existiera— no se recupera, pero tampoco la abre cualquiera mandando "".</summary>
    public bool VerifyUserCode(string code)
    {
        if (string.IsNullOrEmpty(UserCodeHash) || string.IsNullOrEmpty(code)) return false;
        return PasswordHasher.IsModern(UserCodeHash)
            ? PasswordHasher.VerifyModern(code, UserCodeHash)
            : CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(PasswordHasher.Hash(code, Salt)),
                Encoding.ASCII.GetBytes(UserCodeHash));
    }

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
