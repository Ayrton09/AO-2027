using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CoverAO.Server.Data;

/// <summary>
/// EL ALMACÉN EN SQLITE (fase5/postgres/DISENO.md): el motor por defecto, un archivo —<c>data/coverao.db</c>— y nada que
/// instalar. El SQL es el de <see cref="AlmacenRelacional"/>; acá va lo de SQLite: el archivo, el modo WAL, las fechas como
/// texto y el backup con <c>VACUUM INTO</c>.
///
/// <para>Con el modo WAL, la tabla de posiciones de la página pública y el backup leen en su propia conexión sin frenar al
/// que escribe.</para>
/// </summary>
public sealed class AlmacenSqlite : AlmacenRelacional
{
    private readonly string _cadena;
    private readonly bool _enMemoria;
    private readonly SqliteConnection _con;
    private readonly FileStream? _candado;

    /// <summary>Abre la base de <paramref name="archivo"/> —o la crea— y le aplica las versiones del esquema que falten.</summary>
    public AlmacenSqlite(string archivo) : this(archivo, paraImportar: false) { }

    /// <param name="paraImportar">Una base TEMPORAL que se llena de una vez y recién después pasa a ser la de verdad
    /// (<see cref="Importador"/>): sin diario en disco ni sincronización por transacción, que con miles de personajes es la
    /// diferencia entre segundos y minutos. Si algo falla, el archivo se tira entero.</param>
    internal AlmacenSqlite(string archivo, bool paraImportar)
        : this(new SqliteConnectionStringBuilder
        {
            DataSource = archivo, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false,
        }.ToString(), archivo, paraImportar)
    {
    }

    /// <summary>Una base en memoria, que vive lo que viva el almacén. Para los tests.</summary>
    public static AlmacenSqlite EnMemoria() => new(new SqliteConnectionStringBuilder
    {
        DataSource = "coverao-" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory,
        Cache = SqliteCacheMode.Shared, ForeignKeys = true, Pooling = false,
    }.ToString(), archivo: null, paraImportar: false);

    // Pooling apagado a propósito: una conexión del pool deja el archivo abierto después de cerrarla, y ni el backup
    // restaurado ni la carpeta de un test se pueden mover o borrar mientras tanto.
    private AlmacenSqlite(string cadena, string? archivo, bool paraImportar)
    {
        _cadena = cadena;
        _enMemoria = archivo is null;
        if (archivo is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivo))!);
        // La temporal de una importación es sólo del importador, que ya tiene el candado de la de verdad.
        if (archivo is not null && !paraImportar) _candado = Candado(archivo);
        _con = new SqliteConnection(cadena);
        try
        {
            _con.Open();
            // WAL: el que lee no frena al que escribe. FULL: cada transacción confirmada está en el disco, la misma
            // garantía que daba la escritura atómica de los archivos.
            if (paraImportar)
            {
                Ejecutar("PRAGMA journal_mode = MEMORY;");   // el diario sigue: el alta que no entra se deshace
                Ejecutar("PRAGMA synchronous = OFF;");
            }
            else
            {
                if (archivo is not null) Ejecutar("PRAGMA journal_mode = WAL;");
                Ejecutar("PRAGMA synchronous = FULL;");
            }
            RechazarElFormatoViejo(archivo);
            Migrar();
        }
        catch
        {
            _con.Dispose();
            _candado?.Dispose();
            throw;
        }
    }

    public override void Dispose()
    {
        lock (DeAUno)
        {
            _con.Dispose();
            _candado?.Dispose();
        }
    }

    /// <summary>
    /// UNA BASE, UN PROCESO. Dos servidores sobre el mismo archivo —uno que quedó andando de fondo y otro que se prende, dos
    /// instalaciones apuntando a la misma carpeta— escribirían cada uno su mundo encima del otro: el último en guardar pisa,
    /// que es la forma de duplicar o perder ítems. SQLite lo deja pasar sin quejarse, así que el candado es propio: un archivo
    /// al lado de la base, <c>coverao.db.lock</c>, tomado en exclusiva mientras el almacén está abierto. También lo toman los
    /// comandos de la base (importar, restaurar), que van con el servidor apagado. El sistema lo suelta si el proceso muere:
    /// uno que quedó de un corte no traba nada.
    /// </summary>
    internal static FileStream Candado(string archivo)
    {
        string ruta = Path.GetFullPath(archivo) + ".lock";
        try { return new FileStream(ruta, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Otro proceso ya usa {Path.GetFileName(archivo)} —otro servidor andando, o un comando de la base—, y la base es de uno " +
                $"solo a la vez. ({ex.Message})", ex);
        }
    }

    protected override string Motor => "sqlite";
    protected override DbConnection Conexion => _con;

    protected override DbConnection OtraConexion()
    {
        var con = new SqliteConnection(_cadena);
        con.Open();
        return con;
    }

    protected override string TablaDeMigraciones =>
        "CREATE TABLE IF NOT EXISTS migraciones (version INTEGER PRIMARY KEY, descripcion TEXT NOT NULL, aplicada_en TEXT NOT NULL) STRICT;";

    /// <summary>Las fechas van en UTC, como texto ISO 8601: ordenan igual como texto que como fecha.</summary>
    protected override object ValorDeFecha(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    protected override DateTime LeerFecha(DbDataReader r, int columna)
        => DateTime.Parse(r.GetString(columna), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    protected override object ValorDeIp(string ip) => ip;
    protected override string LeerIp(DbDataReader r, int columna) => r.GetString(columna);

    /// <summary>Las versiones del esquema de SQLite que trae este servidor.</summary>
    internal static IReadOnlyList<(int Version, string Nombre, string Sql)> Versiones() => Versiones("sqlite");

    /// <summary>¿La conexión que escribe tiene prendidas las claves foráneas? SQLite las ignora EN SILENCIO si no se las
    /// prende en cada conexión, y ahí un personaje borrado dejaría sus ranuras huérfanas sin que nadie se entere.</summary>
    internal bool ClavesForaneasPrendidas()
    {
        lock (DeAUno) return Convert.ToInt64(Escalar("PRAGMA foreign_keys")) == 1;
    }

    /// <summary>La <c>coverao.db</c> del SQLite VIEJO —el texto del <c>.chr</c> en una columna, el que se prendía con
    /// <c>COVERAO_DB=sqlite</c>— tiene el mismo nombre de archivo. Crearle las tablas nuevas al lado dejaría dos mundos en
    /// un archivo, y el viejo invisible: el servidor no arranca y lo dice.</summary>
    private void RechazarElFormatoViejo(string? archivo)
    {
        if (Convert.ToInt64(Escalar("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('accounts', 'characters')")) > 0)
            throw new InvalidOperationException(
                $"{archivo ?? "La base"} es del SQLite viejo (el texto de cada personaje en una columna). " +
                "Movela a otra carpeta e importala a la base nueva.");
    }

    /// <summary>La base entera, a <paramref name="archivo"/> —que no puede existir—, con <c>VACUUM INTO</c>: todo lo que tiene,
    /// también lo que la ficha de un personaje no lleva (las fechas, los apartados).</summary>
    internal void CopiarA(string archivo)
    {
        lock (DeAUno)
        {
            using var c = _con.CreateCommand();
            c.CommandText = "VACUUM INTO @ruta";
            c.Parameters.AddWithValue("@ruta", Path.GetFullPath(archivo));
            c.ExecuteNonQuery();
        }
    }

    /// <summary><c>VACUUM INTO</c>: una copia consistente con el servidor andando, en una conexión propia. Se arma aparte y
    /// recién entera toma su nombre (<see cref="Backups.PrefijoTemporal"/>). Devuelve la ruta del <c>.db</c> creado.</summary>
    public override string Backup(string destDir, string stem)
    {
        Directory.CreateDirectory(destDir);
        string ruta = Path.GetFullPath(Path.Combine(destDir, stem + ".db"));
        string temporal = Backups.Temporal(destDir, stem + ".db");   // VACUUM INTO falla si el destino ya existe: no existe
        try
        {
            using (var con = new SqliteConnection(_cadena))
            {
                con.Open();
                if (_enMemoria)
                {
                    // Una base en memoria haría el VACUUM INTO también en memoria: se copia con la API de backup de SQLite.
                    using var destino = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporal, Pooling = false }.ToString());
                    destino.Open();
                    con.BackupDatabase(destino);
                }
                else
                {
                    using var c = con.CreateCommand();
                    c.CommandText = "VACUUM INTO @ruta";
                    c.Parameters.AddWithValue("@ruta", temporal);
                    c.ExecuteNonQuery();
                }
            }
            File.Move(temporal, ruta, overwrite: true);
            return ruta;
        }
        catch
        {
            try { File.Delete(temporal); } catch { }
            throw;
        }
    }
}
