namespace CoverAO.Server.Data;

/// <summary>
/// Reads/writes the account .cnt file (INI). The account-data section is named after the account
/// (uppercase); the [PJS] section holds NumPjs + PJ1..PJ10 (Cuentas.bas:100-131).
/// </summary>
public static class AccountFile
{
    public const int MaxCharacters = 10;

    public static Account Load(string path, string accountName) => FromIni(IniFile.Load(path), accountName);

    public static Account FromIni(IniFile ini, string accountName)
    {
        var section = accountName.ToUpperInvariant();
        var acc = new Account
        {
            Name = accountName,
            PasswordHash = ini.GetString(section, "Password"),
            Salt = ini.GetString(section, "Salt"),
            Banned = ini.GetBool(section, "Ban"),
            EsBot = ini.GetBool(section, "Bot"),
            Trampas = ini.GetInt(section, "Trampas"),
            Donador = ini.GetBool(section, "Donador"),   // VB6 Cuentas.bas:117 — flag premium de la cuenta
            UserCodeHash = ini.GetString(section, "UserCodigo"),   // código de recuperación (HandleProcesosLogin Tipo 0)
            UltimoIngreso = LeerFecha(ini.GetString(section, "UltimoAcceso")),   // VB6 Cuentas.bas:443
            UltimaIp = ini.GetString(section, "UltimaIP"),                        // VB6 Cuentas.bas:444
        };
        int n = ini.GetInt("PJS", "NumPjs");
        for (int i = 1; i <= Math.Min(n, MaxCharacters); i++)
        {
            var name = ini.GetString("PJS", "PJ" + i);
            if (!string.IsNullOrWhiteSpace(name)) acc.Characters.Add(name);
        }
        return acc;
    }

    public static void Save(Account acc, string path) => ToIni(acc).Save(path);

    /// <summary>
    /// La fecha del último ingreso. La nuestra va en ISO 8601 y en UTC; la del ORIGINAL es <c>Date &amp; " " &amp; Time</c>
    /// (Cuentas.bas:443), o sea el formato regional de la máquina que lo corría —"28/09/2026 10:15:00" en una en
    /// castellano— y en hora local. Se prueba primero la nuestra y después las del original; lo que no se entiende queda
    /// en null, que es "no se sabe", y no un error.
    /// </summary>
    private static DateTime? LeerFecha(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(texto, "yyyy-MM-ddTHH:mm:ssZ", inv,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var iso))
            return iso;
        // Con patrones explícitos y la cultura invariante: pedir la cultura "es-AR" tira en un servidor con la
        // globalización invariante (las imágenes chicas de Docker).
        string[] delOriginal = { "d/M/yyyy H:mm:ss", "d/M/yyyy HH:mm:ss", "M/d/yyyy h:mm:ss tt", "d/M/yyyy" };
        if (DateTime.TryParseExact(texto.Trim(), delOriginal, inv,
                System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var local))
            return local;
        return null;
    }

    public static IniFile ToIni(Account acc)
    {
        var ini = new IniFile();
        var section = acc.Name.ToUpperInvariant();
        ini.Set(section, "Cuenta", section);
        ini.Set(section, "Password", acc.PasswordHash);
        ini.Set(section, "Salt", acc.Salt);
        ini.Set(section, "Ban", acc.Banned ? "1" : "0");
        ini.Set(section, "Bot", acc.EsBot ? "1" : "0");   // cuenta de un bot: sólo se abre desde adentro del proceso
        ini.Set(section, "Trampas", acc.Trampas.ToString());
        ini.Set(section, "Donador", acc.Donador ? "1" : "0");   // VB6 Protocol.bas:16127 — /DONADOR persiste acá
        ini.Set(section, "UserCodigo", acc.UserCodeHash);
        ini.Set(section, "UltimoAcceso", acc.UltimoIngreso is { } f
            ? f.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) : "");
        ini.Set(section, "UltimaIP", acc.UltimaIp);

        ini.Set("PJS", "NumPjs", acc.Characters.Count.ToString());
        for (int i = 1; i <= MaxCharacters; i++)
            ini.Set("PJS", "PJ" + i, i <= acc.Characters.Count ? acc.Characters[i - 1] : "");
        return ini;
    }
}
