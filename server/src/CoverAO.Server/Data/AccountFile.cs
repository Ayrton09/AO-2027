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

        ini.Set("PJS", "NumPjs", acc.Characters.Count.ToString());
        for (int i = 1; i <= MaxCharacters; i++)
            ini.Set("PJS", "PJ" + i, i <= acc.Characters.Count ? acc.Characters[i - 1] : "");
        return ini;
    }
}
