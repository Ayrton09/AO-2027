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
    /// <summary>APARTA el personaje en vez de borrarlo. Lo pide el borrado de personaje (1b): perder un
    /// personaje es irreversible para el jugador, así que el archivo se mueve a un rincón —no se destruye— y
    /// el nombre queda libre. Si algún día alguien se arrepiente, el `.chr` está. Devuelve false si no había
    /// nada que apartar.</summary>
    bool ArchivarCharacter(string name);
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
    /// queda el viejo entero. Nunca uno a medias.</para>
    ///
    /// <para>Y SERIALIZADA, que es lo que faltaba. El temporal se llama siempre igual (<c>destino + ".tmp"</c>),
    /// así que dos escrituras del MISMO archivo abren el mismo temporal: la segunda lo trunca mientras la
    /// primera lo escribe, y después las dos lo quieren renombrar. Medido con veinte hilos sobre una cuenta:
    /// dieciséis tiraron <c>IOException</c> y su contenido se perdió — y el que las pidió no se entera, porque
    /// los llamadores atrapan la excepción y siguen. No hace falta ser administrador para provocarlo: las
    /// escrituras de cuenta salen de TRES hilos (el game loop, el pool de autenticación con sus <c>Task.Run</c>,
    /// y el HTTP del panel), y el pool admite una operación en vuelo <b>por conexión</b>, así que veinte
    /// conexiones pidiendo cambiar la misma contraseña son veinte escrituras simultáneas del mismo <c>.cnt</c>.
    /// <c>SqliteStore</c> ya serializaba con su propio candado desde siempre; éste, que es el almacén POR
    /// DEFECTO, no. El costo es nulo: son archivos de unos pocos KB y las escrituras son raras.</para></summary>
    private readonly object _puerta = new();

    private void EscribirAtomico(string destino, string texto, System.Text.Encoding enc)
    {
        // La FORMA de la escritura (temporal + rename + reintento) vive en EscrituraAtomica, que la comparte
        // con el Server.ini y con la lista de baneados. El CANDADO se queda ACA: es el que excluye al backup
        // mientras copia, y moverlo afuera cambiaria esa exclusion sin que se note.
        lock (_puerta) EscrituraAtomica.Escribir(destino, texto, enc);
    }

    private string MiscPath(string k) => Path.Combine(_miscDir, k.ToUpperInvariant() + ".json");
    public string? ReadMisc(string k) => File.Exists(MiscPath(k)) ? File.ReadAllText(MiscPath(k)) : null;
    public void WriteMisc(string k, string t) => EscribirAtomico(MiscPath(k), t, System.Text.Encoding.UTF8);

    // Naming.SafeFileStem = backstop de path traversal: rechaza separadores/'..' antes de tocar el disco
    // (los handlers ya validan el nombre, pero esto impide que un nombre sin validar escriba fuera del dir).
    private string CharPath(string n) => Path.Combine(_charDir, Naming.SafeFileStem(n) + ".chr");
    private string AccountPath(string n) => Path.Combine(_accountDir, Naming.SafeFileStem(n) + ".cnt");

    /// <summary>Los personajes borrados: `data/Charfile/Borrados/NOMBRE-<sello>.chr`. El sello es la fecha en
    /// UTC, así que borrar dos veces el mismo nombre no pisa el archivo anterior.</summary>
    public bool ArchivarCharacter(string n)
    {
        lock (_puerta)
        {
            string origen = CharPath(n);
            if (!File.Exists(origen)) return false;
            string dir = Path.Combine(_charDir, "Borrados");
            Directory.CreateDirectory(dir);
            string destino = Path.Combine(dir,
                $"{Naming.SafeFileStem(n)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.chr");
            File.Move(origen, destino, overwrite: true);
            return true;
        }
    }

    public bool AccountExists(string n) => File.Exists(AccountPath(n));
    public string? ReadAccount(string n) => File.Exists(AccountPath(n)) ? File.ReadAllText(AccountPath(n), Codec.Ansi) : null;
    public void WriteAccount(string n, string t) => EscribirAtomico(AccountPath(n), t, Codec.Ansi);
    public IEnumerable<string> AllAccountNames() => Directory.Exists(_accountDir)
        ? Directory.EnumerateFiles(_accountDir, "*.cnt").Select(f => Path.GetFileNameWithoutExtension(f)) : Enumerable.Empty<string>();
    public bool CharacterExists(string n) => File.Exists(CharPath(n));
    public string? ReadCharacter(string n) => File.Exists(CharPath(n)) ? File.ReadAllText(CharPath(n), Codec.Ansi) : null;
    public void WriteCharacter(string n, string t) => EscribirAtomico(CharPath(n), t, Codec.Ansi);

    /// <summary>Copia los tres directorios a <c>destDir/stem/</c>.
    ///
    /// <para>El candado de las escrituras se toma POR ARCHIVO, no una vez para toda la recorrida. Eso es lo que
    /// permite que el backup haya salido del hilo del game loop: si un guardado cae justo encima, espera lo que
    /// tarda UNA copia —medio milisegundo— en vez de los cinco segundos del barrido entero.</para>
    ///
    /// <para>Y hace falta de verdad, no es prolijidad. En Windows, el <c>File.Move</c> con <c>overwrite</c> con
    /// el que cierra <see cref="EscribirAtomico"/> se cae con <c>UnauthorizedAccessException</c> si el destino
    /// está abierto por alguien, y <b>no hay <c>FileShare</c> que lo evite</b> — probadas las cinco
    /// combinaciones, ni siquiera <c>FileShare.Delete</c>, que en teoría es exactamente para esto. Sin el
    /// candado el que sale lastimado no es la copia sino el JUEGO: el guardado del personaje se pierde mientras
    /// el backup pasa por su archivo, y el que hostea lo ve como alguien a quien "se le borró lo de la última
    /// media hora", cada tanto y sin patrón. Lo encontró un test que mide la ESCRITURA, no la copia.</para>
    ///
    /// <para>Se arma bajo <c>parcial-</c> y recién al final se renombra al nombre bueno. Antes se escribía
    /// derecho sobre el definitivo, así que un backup cortado a la mitad —el proceso que se cierra, el disco que
    /// se llena— quedaba con el nombre de uno completo, y <see cref="Backups.Podar"/> lo contaba para la
    /// retención igual que a los buenos: a las N rotaciones no quedaba ninguna copia entera. El prefijo tiene
    /// que ser OTRO y no una extensión, porque la retención lista con <c>coverao-*</c> y un
    /// <c>coverao-….parcial</c> le entra igual.</para></summary>
    public string Backup(string destDir, string stem)
    {
        string root = Path.Combine(destDir, stem);
        string enObra = Path.Combine(destDir, "parcial-" + stem);
        Directory.CreateDirectory(destDir);
        // Sobras de intentos cortados (el proceso murió a mitad de copiar): no las mira nadie y ocupan disco.
        foreach (var viejo in Directory.EnumerateDirectories(destDir, "parcial-*"))
            try { Directory.Delete(viejo, recursive: true); } catch (Exception ex) { Console.Error.WriteLine($"[backup] no se pudo limpiar {Path.GetFileName(viejo)}: {ex.Message}"); }
        foreach (var (src, sub) in new[] { (_charDir, "Charfile"), (_accountDir, "Cuentas"), (_miscDir, "Misc") })
        {
            if (!Directory.Exists(src)) continue;
            string dst = Path.Combine(enObra, sub);
            Directory.CreateDirectory(dst);
            // Try POR ARCHIVO: sin esto, UN solo .chr tomado por el antivirus o por el backup del hosting
            // tiraba IOException a mitad del recorrido y abortaba la copia entera.
            foreach (var f in Directory.EnumerateFiles(src))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // temporal de EscribirAtomico
                try { lock (_puerta) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine($"[backup] no se pudo copiar {Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        Directory.CreateDirectory(enObra);   // por si el almacén estaba vacío: el backup existe igual
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);   // mismo segundo, backup repetido
        Directory.Move(enObra, root);
        return root;
    }
}

/// <summary>SQLite backend: two tables (accounts, characters), each an (name, ini-text) row.</summary>
public sealed class SqliteStore : IStore, IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>
    /// UNA conexión compartida y varios hilos que la usan: el del game loop (autosave, guardado al salir) y los
    /// del pool (el hasheo de login/creación de cuenta vuelve con el resultado, y el backup del panel). Una
    /// <c>SqliteConnection</c> NO es segura para eso: dos comandos a la vez sobre la misma conexión pueden
    /// devolver datos de la consulta del otro o tirar en medio de una escritura.
    ///
    /// Se serializa acá y no abriendo una conexión por operación porque el volumen es bajísimo —unas pocas
    /// escrituras por minuto— y porque una conexión por llamada haría que el backup (`VACUUM INTO`) deje de
    /// ver la misma base. El candado es por operación, no por transacción: cada método ya es atómico de por sí.
    /// </summary>
    private readonly object _puerta = new();

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
        lock (_puerta)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }

    private bool Exists(string table, string name)
    {
        lock (_puerta)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"SELECT 1 FROM {table} WHERE name = $n";
            c.Parameters.AddWithValue("$n", name.ToUpperInvariant());
            return c.ExecuteScalar() is not null;
        }
    }

    private string? Read(string table, string name)
    {
        lock (_puerta)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"SELECT ini FROM {table} WHERE name = $n";
            c.Parameters.AddWithValue("$n", name.ToUpperInvariant());
            return c.ExecuteScalar() as string;
        }
    }

    private void Write(string table, string name, string ini)
    {
        lock (_puerta)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"INSERT INTO {table}(name, ini) VALUES($n, $i) ON CONFLICT(name) DO UPDATE SET ini = $i";
            c.Parameters.AddWithValue("$n", name.ToUpperInvariant());
            c.Parameters.AddWithValue("$i", ini);
            c.ExecuteNonQuery();
        }
    }

    public bool AccountExists(string n) => Exists("accounts", n);
    public string? ReadAccount(string n) => Read("accounts", n);
    public void WriteAccount(string n, string t) => Write("accounts", n, t);
    public IEnumerable<string> AllAccountNames()
    {
        lock (_puerta)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT name FROM accounts";
            using var r = c.ExecuteReader();
            var names = new List<string>();
            while (r.Read()) names.Add(r.GetString(0));
            return names;   // se materializa DENTRO del candado: devolver el lector sería soltarlo a mitad
        }
    }
    public bool CharacterExists(string n) => Exists("characters", n);
    public string? ReadCharacter(string n) => Read("characters", n);
    public void WriteCharacter(string n, string t) => Write("characters", n, t);

    /// <summary>Igual que en el almacén de archivos: se APARTA, no se destruye. Acá el rincón es una fila con
    /// la clave prefijada, en la misma tabla — así el backup se lo lleva junto con todo lo demás.</summary>
    public bool ArchivarCharacter(string n)
    {
        lock (_puerta)
        {
            string? contenido = Read("characters", n);
            if (contenido is null) return false;
            Write("characters", $"@borrado/{DateTime.UtcNow:yyyyMMdd-HHmmss}/{n}", contenido);
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM characters WHERE name = $n";
            c.Parameters.AddWithValue("$n", n.ToUpperInvariant());
            c.ExecuteNonQuery();
            return true;
        }
    }
    public string? ReadMisc(string k) => Read("misc", k);
    public void WriteMisc(string k, string t) => Write("misc", k, t);

    /// <summary>`VACUUM INTO`: snapshot transaccionalmente consistente sin cerrar la conexión ni frenar el
    /// servidor. Copiar el .db a mano mientras corre puede llevarse un archivo a medio escribir; esto no.</summary>
    public string Backup(string destDir, string stem)
    {
        Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, stem + ".db");
        if (File.Exists(dest)) File.Delete(dest);   // VACUUM INTO falla si el destino ya existe
        lock (_puerta)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "VACUUM INTO $dest";
            c.Parameters.AddWithValue("$dest", dest);
            c.ExecuteNonQuery();
        }
        return dest;
    }

    public void Dispose() => _conn.Dispose();
}
