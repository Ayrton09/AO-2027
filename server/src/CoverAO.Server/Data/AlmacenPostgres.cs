using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

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
        // escribe tiene que dar una nueva de verdad. Sin GSS (Kerberos), que CoverAO no usa: en Linux, intentarlo busca
        // libgssapi_krb5 —la imagen de Docker no la trae— y el intento fallido sale en la consola como "Error:".
        _cadena = new NpgsqlConnectionStringBuilder(cadena) { Pooling = false, GssEncryptionMode = GssEncryptionMode.Disable }.ToString();
        _con = new NpgsqlConnection(_cadena);
        _con.Open();
        try
        {
            if (!ProbarElCandado())
                throw new InvalidOperationException(
                    "Otro proceso ya usa esta base de Postgres —otro servidor andando, o un comando de la base—, y la base es de uno " +
                    "solo a la vez.");
            Migrar();
        }
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
        Password = ini.ClaveDeBase, ApplicationName = "CoverAO", GssEncryptionMode = GssEncryptionMode.Disable,
    }.ToString();

    /// <summary>
    /// EL USUARIO DE LA WEB (DISENO.md, sección 8): uno de Postgres que sólo puede leer las vistas de <c>publico</c> —las vistas
    /// corren con los permisos de su dueño, el usuario del juego, así que lee lo que muestran sin poder tocar las tablas—.
    /// Lo crea, o le cambia la clave si ya estaba, un ADMINISTRADOR de Postgres: el servidor del juego no necesita permisos
    /// para crear usuarios. Devuelve la clave, nueva y al azar.
    /// </summary>
    public static (string Clave, bool Existia) CrearUsuarioWeb(ServerIni ini, string usuario, string adminUsuario, string adminClave)
    {
        if (usuario.Length == 0 || !usuario.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            throw new InvalidOperationException($"'{usuario}' no sirve de nombre: minúsculas, números y guion bajo.");
        // EL DEL JUEGO NO: cambiarle la clave dejaría al servidor sin poder volver a conectarse.
        if (string.Equals(usuario, ini.UsuarioDeBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{usuario}' es el usuario del juego: el de la web tiene que ser otro.");
        using (new AlmacenPostgres(Cadena(ini))) { }   // el esquema al día, con sus vistas, antes de dar permisos sobre ellas
        string clave = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
        var admin = new NpgsqlConnectionStringBuilder(Cadena(ini)) { Username = adminUsuario, Password = adminClave, Pooling = false };
        using var con = new NpgsqlConnection(admin.ToString());
        con.Open();
        using var tx = con.BeginTransaction();
        void Ejecutar(string sql)
        {
            using var c = new NpgsqlCommand(sql, con, tx);
            c.ExecuteNonQuery();
        }
        bool existe;
        using (var c = new NpgsqlCommand("SELECT rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls FROM pg_roles WHERE rolname = @u", con, tx))
        {
            c.Parameters.AddWithValue("@u", usuario);
            var administra = c.ExecuteScalar();
            existe = administra is not null;
            // UNO QUE ADMINISTRA TAMPOCO: la clave de la web va a una página, y con ella se podría todo.
            if (administra is true)
                throw new InvalidOperationException($"'{usuario}' ya existe y administra Postgres: el de la web tiene que ser uno sin permisos.");
        }
        // CREATE ROLE no acepta parámetros: la clave va escrita, y es hexadecimal, sin nada que escapar.
        Ejecutar($"{(existe ? "ALTER" : "CREATE")} ROLE \"{usuario}\" LOGIN PASSWORD '{clave}'");
        Ejecutar($"GRANT CONNECT ON DATABASE \"{ini.NombreDeBase}\" TO \"{usuario}\"");
        Ejecutar($"GRANT USAGE ON SCHEMA publico TO \"{usuario}\"");
        Ejecutar($"GRANT SELECT ON ALL TABLES IN SCHEMA publico TO \"{usuario}\"");
        // Las vistas que traiga una versión nueva del esquema —las crea el usuario del juego— también.
        Ejecutar($"ALTER DEFAULT PRIVILEGES FOR ROLE \"{ini.UsuarioDeBase}\" IN SCHEMA publico GRANT SELECT ON TABLES TO \"{usuario}\"");
        tx.Commit();
        return (clave, existe);
    }

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
        // Adentro de la transacción grande, lo que siguiera en una conexión nueva quedaría fuera de ella. Es la base que no
        // responde: la transacción se deshace entera y la cola la reintenta desde el principio (Persistence.GuardarJuntos).
        if (EnUnaTransaccionGrande) throw new LaBaseNoResponde("se cortó la conexión con Postgres en medio de la transacción");
        _con.Close();
        try { _con.Open(); }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // NO PODER VOLVER A CONECTARSE ES QUE LA BASE NO RESPONDE, sea por lo que sea —Postgres que no levanta, la clave que
            // cambió, la base que no está—: lo que se guarda espera en la cola, reintentándose, y no se descarta de a uno como si
            // fuera un error del dato. El panel muestra la base caída, y si se apaga así va al rescate.
            throw new LaBaseNoResponde($"no se pudo volver a conectar con Postgres: {ex.Message}", ex);
        }
        // El candado es de la conexión: se fue con la que se cortó, y hay que volver a tomarlo. Si en el medio lo tomó otro
        // proceso, no se escribe nada: se cierra y se reintenta, como con la base caída.
        if (!ProbarElCandado())
        {
            _con.Close();
            throw new LaBaseNoResponde("otro proceso tomó la base mientras la conexión estaba cortada: no se escribe hasta que la suelte");
        }
    }

    /// <summary>La llave del candado: "CoverAO". Vale por base, así que dos servidores en bases distintas del mismo Postgres
    /// no se traban.</summary>
    private const long LlaveDelCandado = 0x436F766572414F;

    /// <summary>
    /// UNA BASE, UN PROCESO (ver <see cref="AlmacenSqlite.Candado"/>): un candado de Postgres (<c>pg_try_advisory_lock</c>) en
    /// la conexión que escribe, mientras dure. Postgres lo suelta solo cuando la conexión se cierra o se corta. False si lo
    /// tiene otro.
    /// </summary>
    private bool ProbarElCandado()
    {
        using var c = new NpgsqlCommand("SELECT pg_try_advisory_lock(@llave)", _con);
        c.Parameters.AddWithValue("@llave", LlaveDelCandado);
        return c.ExecuteScalar() is true;
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
            // SÓLO LAS DEL JUEGO: una tabla que otro programa —una web— tenga en la misma base no es del backup, y tirarla la
            // perdería sin que ningún zip la devuelva.
            var delJuego = TablasDelJuego();
            var tablas = new List<string>();
            using (var c = new NpgsqlCommand("SELECT table_name FROM information_schema.tables " +
                                             "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'", _con, tx))
            using (var r = c.ExecuteReader())
                while (r.Read()) if (delJuego.Contains(r.GetString(0))) tablas.Add(r.GetString(0));
            if (tablas.Count > 0)
                using (var c = new NpgsqlCommand("DROP TABLE " + string.Join(", ", tablas.Select(t => $"\"{t}\"")) + " CASCADE", _con, tx))
                    c.ExecuteNonQuery();

            using (var c = new NpgsqlCommand(TablaDeMigraciones, _con, tx)) c.ExecuteNonQuery();
            foreach (var v in versiones.Where(v => v.Version <= version)) AplicarVersion(v, tx);

            var cargadas = new List<(string, long)>();
            foreach (var tabla in EnOrdenDeCarga(tx))
            {
                if (tabla == "migraciones" || !delJuego.Contains(tabla)) continue;
                var entrada = archivo.GetEntry(tabla + ".csv")
                    ?? throw new InvalidOperationException($"al backup le falta {tabla}.csv: está incompleto, no se restauró nada.");
                using (var c = new NpgsqlCommand($"DELETE FROM \"{tabla}\"", _con, tx)) c.ExecuteNonQuery();   // la fila del récord
                using (var lector = new StreamReader(entrada.Open(), Encoding.UTF8))
                {
                    string encabezado = lector.ReadLine() ?? throw new InvalidOperationException($"{tabla}.csv está vacío");
                    // LAS COLUMNAS SALEN DEL ZIP y van al COPY: sólo las que la tabla tiene de verdad. Un encabezado armado a mano
                    // —una comilla, un punto y coma— metería SQL propio, que correría con el usuario del juego.
                    var deLaTabla = ColumnasDe(tabla, tx);
                    var delCsv = encabezado.Split(',').Select(col => col.Trim()).ToList();
                    if (delCsv.FirstOrDefault(col => !deLaTabla.Contains(col)) is { } rara)
                        throw new InvalidOperationException($"{tabla}.csv trae la columna '{rara}', que {tabla} no tiene: no es un backup de esta base, no se restauró nada.");
                    string columnas = string.Join(", ", delCsv.Select(col => $"\"{col}\""));
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

    /// <summary>
    /// UNA BASE DE SQLITE, TABLA POR TABLA (fase5/postgres/DISENO.md, sección 6). Pasar un servidor de SQLite a Postgres —o
    /// restaurar en Postgres un backup de SQLite— es la misma base en otro motor, así que va entera: las fechas de alta de
    /// cuentas y personajes y los personajes apartados, que se pueden devolver también después de pasarse. Copiarla personaje
    /// por personaje, como a los archivos —que no tienen nada de eso—, lo perdía. Va adentro de la transacción grande
    /// (<see cref="AlmacenRelacional.EnUnaSolaTransaccion{T}"/>), con los tipos de Postgres: en SQLite los booleanos son 0/1,
    /// las fechas texto ISO y la IP texto. Después se verifica: las filas de cada tabla, y cada personaje releído igual.
    /// </summary>
    internal InformeDeCopia CopiarTablasDe(AlmacenSqlite origen)
    {
        var tx = Grande as NpgsqlTransaction
            ?? throw new InvalidOperationException("la copia de tablas va adentro de la transacción grande");
        if (origen.VersionDelEsquema() != VersionDelEsquema())
            throw new InvalidOperationException("las dos bases tienen versiones del esquema distintas");
        var informe = new InformeDeCopia();
        lock (DeAUno)
        {
            var delJuego = TablasDelJuego();
            var tablas = EnOrdenDeCarga(tx).Where(t => delJuego.Contains(t) && t != "migraciones").ToList();
            // Lo que ya tenga la de destino —la fila del mundo, lo de las webs—, fuera: de las hijas a las madres.
            foreach (var tabla in Enumerable.Reverse(tablas))
                using (var c = new NpgsqlCommand($"DELETE FROM \"{tabla}\"", _con, tx)) c.ExecuteNonQuery();
            foreach (var tabla in tablas)
            {
                var columnas = ColumnasConTipo(tabla, tx);
                long copiadas = 0;
                using (var importador = _con.BeginBinaryImport(
                           $"COPY \"{tabla}\" ({string.Join(", ", columnas.Select(c => $"\"{c.Nombre}\""))}) FROM STDIN (FORMAT BINARY)"))
                {
                    origen.Recorrer(tabla, columnas.Select(c => c.Nombre).ToList(), fila =>
                    {
                        importador.StartRow();
                        for (int i = 0; i < columnas.Count; i++) Escribir(importador, fila.IsDBNull(i) ? null : fila.GetValue(i), columnas[i].Tipo);
                        copiadas++;
                    });
                    importador.Complete();
                }
                long entraron;
                using (var c = new NpgsqlCommand($"SELECT count(*) FROM \"{tabla}\"", _con, tx)) entraron = Convert.ToInt64(c.ExecuteScalar());
                if (entraron != copiadas) informe.Distintos.Add($"{tabla}: {copiadas} filas en el origen y {entraron} en el destino");
            }
            PonerAlDiaLosId(tx);
        }
        CopiaDeAlmacen.Contar(this, informe);
        CopiaDeAlmacen.Verificar(origen, this, informe);
        return informe;
    }

    /// <summary>Las columnas de <paramref name="tabla"/> con su tipo, en el orden de la tabla.</summary>
    private List<(string Nombre, string Tipo)> ColumnasConTipo(string tabla, NpgsqlTransaction tx)
    {
        var columnas = new List<(string, string)>();
        using var c = new NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns " +
                                        "WHERE table_schema = current_schema() AND table_name = @t ORDER BY ordinal_position", _con, tx);
        c.Parameters.AddWithValue("@t", tabla);
        using var r = c.ExecuteReader();
        while (r.Read()) columnas.Add((r.GetString(0), r.GetString(1)));
        return columnas;
    }

    /// <summary>Un valor de SQLite, con el tipo de la columna de Postgres.</summary>
    private void Escribir(NpgsqlBinaryImporter importador, object? valor, string tipo)
    {
        if (valor is null) { importador.WriteNull(); return; }
        switch (tipo)
        {
            case "boolean": importador.Write(Convert.ToInt64(valor, CultureInfo.InvariantCulture) != 0, NpgsqlDbType.Boolean); break;
            case "smallint": importador.Write(Convert.ToInt16(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint); break;
            case "integer": importador.Write(Convert.ToInt32(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Integer); break;
            case "bigint": importador.Write(Convert.ToInt64(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint); break;
            case "double precision": importador.Write(Convert.ToDouble(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Double); break;
            case "text": importador.Write(Convert.ToString(valor, CultureInfo.InvariantCulture)!, NpgsqlDbType.Text); break;
            case "timestamp with time zone":
                importador.Write(DateTime.Parse((string)valor, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), NpgsqlDbType.TimestampTz);
                break;
            case "inet":
                // Como al guardar: lo que no se lea como IP queda vacío (ValorDeIp).
                if (ValorDeIp((string)valor) is IPAddress ip) importador.Write(ip, NpgsqlDbType.Inet);
                else importador.WriteNull();
                break;
            default: throw new InvalidOperationException($"una columna de tipo {tipo}: la copia de tablas no sabe pasarla");
        }
    }

    /// <summary>Las tablas del juego: las que crean las versiones del esquema, y la de las versiones.</summary>
    private static HashSet<string> TablasDelJuego()
        => Versiones("postgres")
            .SelectMany(v => Regex.Matches(v.Sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(\w+)", RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value))
            .Append("migraciones")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las columnas de <paramref name="tabla"/>, del catálogo.</summary>
    private HashSet<string> ColumnasDe(string tabla, NpgsqlTransaction tx)
    {
        var columnas = new HashSet<string>(StringComparer.Ordinal);
        using var c = new NpgsqlCommand("SELECT column_name FROM information_schema.columns " +
                                        "WHERE table_schema = current_schema() AND table_name = @t", _con, tx);
        c.Parameters.AddWithValue("@t", tabla);
        using var r = c.ExecuteReader();
        while (r.Read()) columnas.Add(r.GetString(0));
        return columnas;
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
        string temporal = Backups.Temporal(destDir, stem + ".zip");
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
