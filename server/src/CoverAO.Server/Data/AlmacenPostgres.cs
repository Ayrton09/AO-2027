using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Text;
using Npgsql;

namespace CoverAO.Server.Data;

/// <summary>
/// EL ALMACÉN EN POSTGRES (fase5/postgres/DISENO.md): para el servidor con páginas web, que leen la misma base. El SQL es
/// el de <see cref="AlmacenRelacional"/>; acá va lo de Postgres: la conexión con los datos de <c>[BASEDEDATOS]</c>, las
/// fechas en <c>timestamptz</c>, la IP en <c>inet</c> y el backup como copia lógica.
///
/// <para><b>La base la crea el que hostea</b> (vacía, en UTF8); el esquema lo pone el servidor al arrancar, como en SQLite.</para>
///
/// <para><b>Si se corta la conexión</b> —Postgres se reinició, se cayó la red—, la operación que la encontró cortada falla y
/// se anota, y la siguiente la vuelve a abrir.</para>
/// </summary>
public sealed class AlmacenPostgres : AlmacenRelacional
{
    private readonly string _cadena;
    private readonly NpgsqlConnection _con;

    /// <summary>Abre la base de <paramref name="cadena"/> y le aplica las versiones del esquema que falten.</summary>
    public AlmacenPostgres(string cadena)
    {
        // Sin pool: una conexión del pool que quedó de antes de un reinicio de Postgres está muerta, y reabrir la que
        // escribe tiene que dar una nueva de verdad.
        _cadena = new NpgsqlConnectionStringBuilder(cadena) { Pooling = false }.ToString();
        _con = new NpgsqlConnection(_cadena);
        _con.Open();
        try { Migrar(); }
        catch
        {
            _con.Dispose();
            throw;
        }
    }

    /// <summary>La cadena de conexión de los datos de <c>[BASEDEDATOS]</c>.</summary>
    public static string Cadena(ServerIni ini) => new NpgsqlConnectionStringBuilder
    {
        Host = ini.ServidorDeBase, Port = ini.PuertoDeBase, Database = ini.NombreDeBase, Username = ini.UsuarioDeBase,
        Password = ini.ClaveDeBase, ApplicationName = "CoverAO",
    }.ToString();

    /// <summary>Dónde está, para el log: sin la clave.</summary>
    public static string Donde(ServerIni ini) => $"{ini.NombreDeBase} en {ini.ServidorDeBase}:{ini.PuertoDeBase}";

    public override void Dispose()
    {
        lock (DeAUno) _con.Dispose();
    }

    protected override string Motor => "postgres";
    protected override DbConnection Conexion => _con;

    protected override DbConnection OtraConexion()
    {
        var con = new NpgsqlConnection(_cadena);
        con.Open();
        return con;
    }

    protected override void Preparar()
    {
        if (_con.State == ConnectionState.Open) return;
        // Adentro de la transacción grande, lo que siguiera en una conexión nueva quedaría fuera de ella.
        if (EnUnaTransaccionGrande) throw new InvalidOperationException("se cortó la conexión con Postgres en medio de la transacción");
        _con.Close();
        _con.Open();
    }

    protected override string TablaDeMigraciones =>
        "CREATE TABLE IF NOT EXISTS migraciones (version integer PRIMARY KEY, descripcion text NOT NULL, aplicada_en timestamptz NOT NULL);";

    protected override object ValorDeFecha(DateTime utc) => DateTime.SpecifyKind(utc.ToUniversalTime(), DateTimeKind.Utc);
    protected override DateTime LeerFecha(DbDataReader r, int columna) => r.GetFieldValue<DateTime>(columna);

    /// <summary>Una IP de verdad va a <c>inet</c>; lo que no se lea como IP no se puede guardar ahí y queda vacío.</summary>
    protected override object ValorDeIp(string ip)
        => (ip.Contains('.') || ip.Contains(':')) && IPAddress.TryParse(ip, out var direccion) ? direccion : DBNull.Value;

    protected override string LeerIp(DbDataReader r, int columna) => r.GetFieldValue<IPAddress>(columna).ToString();

    /// <summary>
    /// RESTAURAR UN BACKUP (el zip de <see cref="Backup"/>) en esta base, que tiene que estar vacía —el que llama se ocupa de
    /// eso—. Todo en UNA transacción: se tiran las tablas, se rehacen con el esquema del backup —su versión, que puede ser más
    /// vieja que la de este servidor—, se cargan las filas de cada tabla tal como salieron, se ponen al día los contadores de
    /// los <c>id</c> y se aplican las versiones que falten, como al abrir una base vieja. Si las filas de alguna tabla no son
    /// las del manifiesto, no entra nada. Devuelve cuántas filas entraron por tabla.
    /// </summary>
    internal IReadOnlyList<(string Tabla, long Filas)> Restaurar(string zip)
    {
        using var archivo = ZipFile.OpenRead(zip);
        var (version, esperadas) = LeerManifiesto(archivo);
        var versiones = Versiones(Motor);
        if (version < 1 || version > versiones[^1].Version)
            throw new InvalidOperationException(
                $"el backup tiene el esquema {version} y este servidor conoce hasta el {versiones[^1].Version}: restauralo con un servidor de esa versión o más nuevo.");

        lock (DeAUno)
        {
            Preparar();
            using var tx = _con.BeginTransaction();
            var tablas = new List<string>();
            using (var c = new NpgsqlCommand("SELECT table_name FROM information_schema.tables " +
                                             "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'", _con, tx))
            using (var r = c.ExecuteReader())
                while (r.Read()) tablas.Add(r.GetString(0));
            if (tablas.Count > 0)
                using (var c = new NpgsqlCommand("DROP TABLE " + string.Join(", ", tablas.Select(t => $"\"{t}\"")) + " CASCADE", _con, tx))
                    c.ExecuteNonQuery();

            using (var c = new NpgsqlCommand(TablaDeMigraciones, _con, tx)) c.ExecuteNonQuery();
            foreach (var v in versiones.Where(v => v.Version <= version)) AplicarVersion(v, tx);

            var cargadas = new List<(string, long)>();
            foreach (var tabla in EnOrdenDeCarga(tx))
            {
                if (tabla == "migraciones") continue;
                var entrada = archivo.GetEntry(tabla + ".csv")
                    ?? throw new InvalidOperationException($"al backup le falta {tabla}.csv: está incompleto, no se restauró nada.");
                using (var c = new NpgsqlCommand($"DELETE FROM \"{tabla}\"", _con, tx)) c.ExecuteNonQuery();   // la fila del récord
                using (var lector = new StreamReader(entrada.Open(), Encoding.UTF8))
                {
                    string encabezado = lector.ReadLine() ?? throw new InvalidOperationException($"{tabla}.csv está vacío");
                    string columnas = string.Join(", ", encabezado.Split(',').Select(col => $"\"{col.Trim()}\""));
                    using var escritor = _con.BeginTextImport($"COPY \"{tabla}\" ({columnas}) FROM STDIN (FORMAT csv)");
                    var bloque = new char[64 * 1024];
                    int leidos;
                    while ((leidos = lector.Read(bloque, 0, bloque.Length)) > 0) escritor.Write(bloque, 0, leidos);
                }
                long filas;
                using (var c = new NpgsqlCommand($"SELECT count(*) FROM \"{tabla}\"", _con, tx)) filas = Convert.ToInt64(c.ExecuteScalar());
                if (esperadas.TryGetValue(tabla, out long debia) && debia != filas)
                    throw new InvalidOperationException($"{tabla}: el manifiesto dice {debia} filas y entraron {filas}; no se restauró nada.");
                cargadas.Add((tabla, filas));
            }
            PonerAlDiaLosId(tx);
            foreach (var v in versiones.Where(v => v.Version > version)) AplicarVersion(v, tx);
            tx.Commit();
            return cargadas;
        }
    }

    /// <summary>La versión del esquema y las filas de cada tabla, del <c>manifiesto.txt</c> del backup.</summary>
    private static (int Version, Dictionary<string, long> Filas) LeerManifiesto(ZipArchive archivo)
    {
        var entrada = archivo.GetEntry("manifiesto.txt") ?? throw new InvalidOperationException("no es un backup de la base: le falta el manifiesto.txt");
        using var lector = new StreamReader(entrada.Open(), Encoding.UTF8);
        int version = 0;
        var filas = new Dictionary<string, long>();
        string? linea;
        while ((linea = lector.ReadLine()) is not null)
        {
            if (linea.StartsWith("esquema=", StringComparison.Ordinal)) version = int.Parse(linea["esquema=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            else if (linea.StartsWith("tabla=", StringComparison.Ordinal))
            {
                var partes = linea.Split(' ');
                filas[partes[0]["tabla=".Length..]] = long.Parse(partes[1]["filas=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return (version, filas);
    }

    /// <summary>Las tablas en un orden en que cada una va después de las que referencia: las cuentas antes que los
    /// personajes, los personajes antes que sus ranuras, los clanes antes que sus listas.</summary>
    private List<string> EnOrdenDeCarga(NpgsqlTransaction tx)
    {
        var tablas = new List<string>();
        using (var c = new NpgsqlCommand("SELECT table_name FROM information_schema.tables " +
                                         "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name", _con, tx))
        using (var r = c.ExecuteReader())
            while (r.Read()) tablas.Add(r.GetString(0));
        var depende = tablas.ToDictionary(t => t, _ => new HashSet<string>());
        using (var c = new NpgsqlCommand("SELECT conrelid::regclass::text, confrelid::regclass::text FROM pg_constraint " +
                                         "WHERE contype = 'f' AND connamespace = current_schema()::regnamespace", _con, tx))
        using (var r = c.ExecuteReader())
            while (r.Read())
                if (depende.TryGetValue(r.GetString(0).Trim('"'), out var de) && r.GetString(1).Trim('"') != r.GetString(0).Trim('"'))
                    de.Add(r.GetString(1).Trim('"'));
        var orden = new List<string>();
        var puestas = new HashSet<string>();
        while (orden.Count < tablas.Count)
        {
            var listas = tablas.Where(t => !puestas.Contains(t) && depende[t].All(puestas.Contains)).ToList();
            if (listas.Count == 0) throw new InvalidOperationException("las tablas se referencian en círculo: no hay orden de carga");
            foreach (var t in listas) { orden.Add(t); puestas.Add(t); }
        }
        return orden;
    }

    /// <summary>Después de cargar filas con su <c>id</c>, el contador de cada identidad sigue desde el más alto: si no, la
    /// próxima cuenta nueva chocaría con una restaurada.</summary>
    private void PonerAlDiaLosId(NpgsqlTransaction tx)
    {
        var identidades = new List<(string Tabla, string Columna)>();
        using (var c = new NpgsqlCommand("SELECT table_name, column_name FROM information_schema.columns " +
                                         "WHERE table_schema = current_schema() AND is_identity = 'YES'", _con, tx))
        using (var r = c.ExecuteReader())
            while (r.Read()) identidades.Add((r.GetString(0), r.GetString(1)));
        foreach (var (tabla, columna) in identidades)
            using (var c = new NpgsqlCommand($"SELECT setval(pg_get_serial_sequence('\"{tabla}\"', '{columna}'), " +
                                             $"coalesce(max(\"{columna}\"), 0) + 1, false) FROM \"{tabla}\"", _con, tx))
                c.ExecuteNonQuery();
    }

    /// <summary>
    /// EL BACKUP ES UNA COPIA LÓGICA (DISENO.md, sección 4): cada tabla en CSV, con encabezado, adentro de un <c>.zip</c> con
    /// un manifiesto que dice la versión del esquema. Todo en una transacción <c>REPEATABLE READ READ ONLY</c>, que ve la
    /// base congelada en un instante mientras el servidor sigue escribiendo. No necesita <c>pg_dump</c>: con Docker vive en
    /// el contenedor de la base, no en el del juego, y su versión tiene que coincidir con la de Postgres.
    /// </summary>
    public override string Backup(string destDir, string stem)
    {
        Directory.CreateDirectory(destDir);
        string ruta = Path.GetFullPath(Path.Combine(destDir, stem + ".zip"));
        string temporal = ruta + ".escribiendo";
        if (File.Exists(temporal)) File.Delete(temporal);
        try
        {
            using (var con = (NpgsqlConnection)OtraConexion())
            using (var tx = con.BeginTransaction(IsolationLevel.RepeatableRead))
            {
                using (var c = new NpgsqlCommand("SET TRANSACTION READ ONLY", con, tx)) c.ExecuteNonQuery();
                var tablas = new List<string>();
                using (var c = new NpgsqlCommand("SELECT table_name FROM information_schema.tables " +
                                                 "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name", con, tx))
                using (var r = c.ExecuteReader())
                    while (r.Read()) tablas.Add(r.GetString(0));
                long version;
                using (var c = new NpgsqlCommand("SELECT coalesce(max(version), 0) FROM migraciones", con, tx))
                    version = Convert.ToInt64(c.ExecuteScalar());

                using var zip = ZipFile.Open(temporal, ZipArchiveMode.Create);
                var sinMarca = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var manifiesto = new StringBuilder()
                    .AppendLine("CoverAO: backup de la base (Postgres, copia logica)")
                    .AppendLine($"esquema={version}")
                    .AppendLine($"fecha={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}")
                    .AppendLine($"servidor={Build.Version}");
                foreach (var tabla in tablas)
                {
                    // Tal cual sale, en bloques: por líneas se cambiarían los saltos de línea de un texto que los tenga adentro.
                    using (var escritor = new StreamWriter(zip.CreateEntry(tabla + ".csv").Open(), sinMarca))
                    using (var lector = con.BeginTextExport($"COPY \"{tabla}\" TO STDOUT (FORMAT csv, HEADER)"))
                    {
                        var bloque = new char[64 * 1024];
                        int leidos;
                        while ((leidos = lector.Read(bloque, 0, bloque.Length)) > 0) escritor.Write(bloque, 0, leidos);
                    }
                    long filas;
                    using (var c = new NpgsqlCommand($"SELECT count(*) FROM \"{tabla}\"", con, tx)) filas = Convert.ToInt64(c.ExecuteScalar());
                    manifiesto.AppendLine($"tabla={tabla} filas={filas}");
                }
                using (var escritor = new StreamWriter(zip.CreateEntry("manifiesto.txt").Open(), sinMarca))
                    escritor.Write(manifiesto.ToString());
                tx.Commit();
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
