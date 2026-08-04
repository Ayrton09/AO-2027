using CoverAO.Protocol;
using Microsoft.Data.Sqlite;

namespace CoverAO.Server.Data;

/// <summary>
/// Raw storage backend for account/character records, keyed by (uppercased) name. Records are the
/// INI text produced by CharacterFile/AccountFile — so both backends reuse the exact same
/// serialization; only WHERE the bytes live differs. This is the seam the strategy wanted: swap the
/// datastore (files → SQLite) behind one interface without touching the domain or serialization.
/// </summary>
public interface IStore
{
    bool AccountExists(string name);
    string? ReadAccount(string name);
    void WriteAccount(string name, string iniText);
    /// <summary>Todos los nombres de cuenta (para la gestión de bans del panel: unban-all / lista de baneados).</summary>
    IEnumerable<string> AllAccountNames();
    bool CharacterExists(string name);
    string? ReadCharacter(string name);
    void WriteCharacter(string name, string iniText);
    /// <summary>Reads/writes a free-form blob by key (guild registry, world state, …).</summary>
    string? ReadMisc(string key);
    void WriteMisc(string key, string text);

    /// <summary>Copia consistente del almacén entero dentro de <paramref name="destDir"/>, con el servidor
    /// ANDANDO — para el que hostea, la diferencia entre perder una tarde y perder el servidor. Devuelve la
    /// ruta creada (un archivo o un directorio, según el backend). <paramref name="stem"/> es el nombre base,
    /// sin extensión.</summary>
    string Backup(string destDir, string stem);
}

/// <summary>Flat-file backend: Charfile/&lt;NAME&gt;.chr and Cuentas/&lt;NAME&gt;.cnt (the original layout).</summary>
public sealed class FileStore : IStore
{
    private readonly string _charDir;
    private readonly string _accountDir;
    private readonly string _miscDir;

    public FileStore(string dataRoot)
    {
        _charDir = Path.Combine(dataRoot, "Charfile");
        _accountDir = Path.Combine(dataRoot, "Cuentas");
        _miscDir = Path.Combine(dataRoot, "Misc");
        Directory.CreateDirectory(_charDir);
        Directory.CreateDirectory(_accountDir);
        Directory.CreateDirectory(_miscDir);
    }

    /// <summary>Escritura ATÓMICA: a un temporal y después rename sobre el destino.
    ///
    /// <para><c>File.WriteAllText</c> TRUNCA y después escribe. Si el proceso muere en esa ventana —corte de
    /// luz, OOM-killer, un <c>docker stop</c> que vence su plazo— el archivo queda cortado o en 0 bytes. Y el
    /// cargador no lo detecta: <c>CharacterFile.FromIni</c> resuelve cada clave con un fallback, así que un
    /// <c>.chr</c> vacío devuelve un personaje VÁLIDO de nivel 1 con el inventario en cero. El jugador entra
    /// reseteado y el primer autosave escribe encima el cadáver. Peor aún, el orden de las secciones hace que
    /// un corte a mitad conserve nivel, oro y posición y pierda inventario, banco, hechizos y skills: no
    /// parece corrupción, parece un robo.</para>
    ///
    /// <para>El rename sobre un archivo existente es atómico en NTFS y en ext4: o queda el nuevo entero, o
    /// queda el viejo entero. Nunca uno a medias.</para></summary>
    private static void EscribirAtomico(string destino, string texto, System.Text.Encoding enc)
    {
        string tmp = destino + ".tmp";
        File.WriteAllText(tmp, texto, enc);
        File.Move(tmp, destino, overwrite: true);
    }

    private string MiscPath(string k) => Path.Combine(_miscDir, k.ToUpperInvariant() + ".json");
    public string? ReadMisc(string k) => File.Exists(MiscPath(k)) ? File.ReadAllText(MiscPath(k)) : null;
    public void WriteMisc(string k, string t) => EscribirAtomico(MiscPath(k), t, System.Text.Encoding.UTF8);

    // Naming.SafeFileStem = backstop de path traversal: rechaza separadores/'..' antes de tocar el disco
    // (los handlers ya validan el nombre, pero esto impide que un nombre sin validar escriba fuera del dir).
    private string CharPath(string n) => Path.Combine(_charDir, Naming.SafeFileStem(n) + ".chr");
    private string AccountPath(string n) => Path.Combine(_accountDir, Naming.SafeFileStem(n) + ".cnt");

    public bool AccountExists(string n) => File.Exists(AccountPath(n));
    public string? ReadAccount(string n) => File.Exists(AccountPath(n)) ? File.ReadAllText(AccountPath(n), Codec.Ansi) : null;
    public void WriteAccount(string n, string t) => EscribirAtomico(AccountPath(n), t, Codec.Ansi);
    public IEnumerable<string> AllAccountNames() => Directory.Exists(_accountDir)
        ? Directory.EnumerateFiles(_accountDir, "*.cnt").Select(f => Path.GetFileNameWithoutExtension(f)) : Enumerable.Empty<string>();
    public bool CharacterExists(string n) => File.Exists(CharPath(n));
    public string? ReadCharacter(string n) => File.Exists(CharPath(n)) ? File.ReadAllText(CharPath(n), Codec.Ansi) : null;
    public void WriteCharacter(string n, string t) => EscribirAtomico(CharPath(n), t, Codec.Ansi);

    /// <summary>Copia los tres directorios a <c>destDir/stem/</c>.</summary>
    public string Backup(string destDir, string stem)
    {
        string root = Path.Combine(destDir, stem);
        foreach (var (src, sub) in new[] { (_charDir, "Charfile"), (_accountDir, "Cuentas"), (_miscDir, "Misc") })
        {
            if (!Directory.Exists(src)) continue;
            string dst = Path.Combine(root, sub);
            Directory.CreateDirectory(dst);
            // Try POR ARCHIVO: sin esto, UN solo .chr tomado por el antivirus o por el backup del hosting
            // tiraba IOException a mitad del recorrido y abortaba la copia entera — pero el directorio ya
            // quedaba creado con la mitad de los personajes, y Backups.Listar lo contaba igual para la
            // retención. A las 48 rotaciones no quedaba ninguna copia completa.
            foreach (var f in Directory.EnumerateFiles(src))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // temporal de EscribirAtomico
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine($"[backup] no se pudo copiar {Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        Directory.CreateDirectory(root);   // por si el almacén estaba vacío: el backup existe igual
        return root;
    }
}

/// <summary>SQLite backend: two tables (accounts, characters), each an (name, ini-text) row.</summary>
public sealed class SqliteStore : IStore, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Exec("CREATE TABLE IF NOT EXISTS accounts(name TEXT PRIMARY KEY, ini TEXT NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS characters(name TEXT PRIMARY KEY, ini TEXT NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS misc(name TEXT PRIMARY KEY, ini TEXT NOT NULL);");
    }

    private void Exec(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private bool Exists(string table, string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"SELECT 1 FROM {table} WHERE name = $n";
        c.Parameters.AddWithValue("$n", name.ToUpperInvariant());
        return c.ExecuteScalar() is not null;
    }

    private string? Read(string table, string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"SELECT ini FROM {table} WHERE name = $n";
        c.Parameters.AddWithValue("$n", name.ToUpperInvariant());
        return c.ExecuteScalar() as string;
    }

    private void Write(string table, string name, string ini)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"INSERT INTO {table}(name, ini) VALUES($n, $i) ON CONFLICT(name) DO UPDATE SET ini = $i";
        c.Parameters.AddWithValue("$n", name.ToUpperInvariant());
        c.Parameters.AddWithValue("$i", ini);
        c.ExecuteNonQuery();
    }

    public bool AccountExists(string n) => Exists("accounts", n);
    public string? ReadAccount(string n) => Read("accounts", n);
    public void WriteAccount(string n, string t) => Write("accounts", n, t);
    public IEnumerable<string> AllAccountNames()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT name FROM accounts";
        using var r = c.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }
    public bool CharacterExists(string n) => Exists("characters", n);
    public string? ReadCharacter(string n) => Read("characters", n);
    public void WriteCharacter(string n, string t) => Write("characters", n, t);
    public string? ReadMisc(string k) => Read("misc", k);
    public void WriteMisc(string k, string t) => Write("misc", k, t);

    /// <summary>`VACUUM INTO`: snapshot transaccionalmente consistente sin cerrar la conexión ni frenar el
    /// servidor. Copiar el .db a mano mientras corre puede llevarse un archivo a medio escribir; esto no.</summary>
    public string Backup(string destDir, string stem)
    {
        Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, stem + ".db");
        if (File.Exists(dest)) File.Delete(dest);   // VACUUM INTO falla si el destino ya existe
        using var c = _conn.CreateCommand();
        c.CommandText = "VACUUM INTO $dest";
        c.Parameters.AddWithValue("$dest", dest);
        c.ExecuteNonQuery();
        return dest;
    }

    public void Dispose() => _conn.Dispose();
}
