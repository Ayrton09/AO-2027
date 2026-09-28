using System.Collections.Immutable;
using System.Globalization;
using CoverAO.Server.Game;
using Microsoft.Data.Sqlite;

namespace CoverAO.Server.Data;

/// <summary>
/// EL ALMACÉN EN SQLITE, CON TABLAS DE VERDAD (fase5/postgres/DISENO.md): una tabla por cosa —cuentas, personajes,
/// inventario, bóveda, hechizos, skills, amigos, penas, familiar, y del mundo los clanes, el correo, el suelo, las llaves
/// vendidas y el récord— con el esquema de <c>Data/Esquema/</c>. Es el motor por defecto: un archivo, <c>data/coverao.db</c>,
/// y nada que instalar.
///
/// <para><b>Una conexión escribe; las lecturas de afuera abren la suya.</b> Lo del juego llega por la cola de la base
/// (<see cref="ColaDeLaBase"/>): un solo hilo y una sola conexión. La tabla de posiciones de la página pública y el backup
/// corren en otros hilos y abren otra conexión; con el modo WAL leen sin frenar al que escribe.</para>
///
/// <para><b>Cada guardado es una transacción.</b> El personaje y sus ranuras entran juntos o no entra nada: un corte a la
/// mitad no deja el inventario de un guardado con las stats del anterior.</para>
///
/// <para><b>Los nombres no distinguen mayúsculas</b> (<c>upper(nombre)</c>, con índice único) y se guardan como los escribió
/// el jugador. Un personaje borrado se APARTA: la fila queda con <c>borrado_en</c> y el nombre queda libre.</para>
/// </summary>
public sealed class AlmacenSqlite : IAlmacen, IDisposable
{
    private readonly string _cadena;
    private readonly bool _enMemoria;
    private readonly SqliteConnection _con;
    private readonly object _deAUno = new();

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
        _con = new SqliteConnection(cadena);
        _con.Open();
        try
        {
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
            throw;
        }
    }

    public void Dispose()
    {
        lock (_deAUno) _con.Dispose();
    }

    /// <summary>¿La conexión que escribe tiene prendidas las claves foráneas? SQLite las ignora EN SILENCIO si no se las
    /// prende en cada conexión, y ahí un personaje borrado dejaría sus ranuras huérfanas sin que nadie se entere.</summary>
    internal bool ClavesForaneasPrendidas()
    {
        lock (_deAUno)
        {
            using var c = Comando("PRAGMA foreign_keys");
            return (long)c.ExecuteScalar()! == 1;
        }
    }

    // ── El esquema ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Las versiones del esquema que trae este servidor, en orden: <c>Esquema/NNN_nombre.sqlite.sql</c>.</summary>
    internal static IReadOnlyList<(int Version, string Nombre, string Sql)> Versiones()
    {
        var asm = typeof(AlmacenSqlite).Assembly;
        var versiones = new List<(int, string, string)>();
        foreach (var recurso in asm.GetManifestResourceNames())
        {
            const string Prefijo = "Esquema/", Sufijo = ".sqlite.sql";
            if (!recurso.StartsWith(Prefijo, StringComparison.Ordinal) || !recurso.EndsWith(Sufijo, StringComparison.Ordinal)) continue;
            string nombre = recurso[Prefijo.Length..^Sufijo.Length];
            int corte = nombre.IndexOf('_');
            if (corte <= 0 || !int.TryParse(nombre[..corte], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                throw new InvalidOperationException($"versión del esquema sin número: {recurso}");
            using var s = asm.GetManifestResourceStream(recurso)!;
            using var lector = new StreamReader(s);
            versiones.Add((version, nombre[(corte + 1)..], lector.ReadToEnd()));
        }
        return versiones.OrderBy(v => v.Item1).ToList();
    }

    /// <summary>
    /// Aplica las versiones que falten, cada una en su transacción, y las anota en <c>migraciones</c>. Una base con un
    /// esquema MÁS NUEVO que el de este servidor no se abre: es la misma regla que el sello del mundo —si venís de una
    /// versión más nueva, pará y restaurá un backup—, porque escribirle encima con un esquema viejo la rompería.
    /// </summary>
    private void Migrar()
    {
        Ejecutar("CREATE TABLE IF NOT EXISTS migraciones (version INTEGER PRIMARY KEY, descripcion TEXT NOT NULL, " +
                 "aplicada_en TEXT NOT NULL) STRICT;");
        long actual;
        using (var c = Comando("SELECT coalesce(max(version), 0) FROM migraciones")) actual = (long)c.ExecuteScalar()!;
        var versiones = Versiones();
        int ultima = versiones.Count == 0 ? 0 : versiones[^1].Version;
        if (actual > ultima)
            throw new InvalidOperationException(
                $"La base es de un servidor más nuevo: tiene el esquema {actual} y éste conoce hasta el {ultima}. " +
                "Actualizá el servidor o restaurá un backup.");
        foreach (var (version, nombre, sql) in versiones.Where(v => v.Version > actual))
        {
            using var tx = _con.BeginTransaction();
            using (var c = Comando(sql, tx)) c.ExecuteNonQuery();
            if (version == 2) PasarElMundoATablas(tx);   // lo que la versión 1 tenía como JSON
            using (var c = Comando("INSERT INTO migraciones (version, descripcion, aplicada_en) VALUES (@v, @d, @t)", tx))
            {
                c.Parameters.AddWithValue("@v", version);
                c.Parameters.AddWithValue("@d", nombre);
                c.Parameters.AddWithValue("@t", Ahora());
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>La <c>coverao.db</c> del SQLite VIEJO —el texto del <c>.chr</c> en una columna, el que se prendía con
    /// <c>COVERAO_DB=sqlite</c>— tiene el mismo nombre de archivo. Crearle las tablas nuevas al lado dejaría dos mundos en
    /// un archivo, y el viejo invisible: el servidor no arranca y lo dice.</summary>
    private void RechazarElFormatoViejo(string? archivo)
    {
        using var c = Comando("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('accounts', 'characters')");
        if ((long)c.ExecuteScalar()! > 0)
            throw new InvalidOperationException(
                $"{archivo ?? "La base"} es del SQLite viejo (el texto de cada personaje en una columna). " +
                "Movela a otra carpeta e importala a la base nueva.");
    }

    // ── Cuentas ─────────────────────────────────────────────────────────────────────────────────────────────────

    public bool ExisteCuenta(string nombre)
    {
        lock (_deAUno)
        {
            using var c = Comando("SELECT 1 FROM cuentas WHERE upper(nombre) = upper(@n)");
            c.Parameters.AddWithValue("@n", nombre);
            return c.ExecuteScalar() is not null;
        }
    }

    /// <summary>La cuenta con sus personajes vivos, en el orden en que se crearon (el de la lista <c>[PJS]</c>).</summary>
    public Account? LeerCuenta(string nombre)
    {
        lock (_deAUno)
        {
            Account cuenta;
            long id;
            using (var c = Comando("SELECT id, nombre, clave_hash, clave_sal, codigo_hash, baneada, es_bot, trampas, donador, " +
                                   "ultimo_ingreso_en, ultima_ip FROM cuentas WHERE upper(nombre) = upper(@n)"))
            {
                c.Parameters.AddWithValue("@n", nombre);
                using var r = c.ExecuteReader();
                if (!r.Read()) return null;
                id = r.GetInt64(0);
                cuenta = new Account
                {
                    Name = r.GetString(1),
                    PasswordHash = r.GetString(2),
                    Salt = r.GetString(3),
                    UserCodeHash = r.GetString(4),
                    Banned = r.GetInt64(5) != 0,
                    EsBot = r.GetInt64(6) != 0,
                    Trampas = (int)r.GetInt64(7),
                    Donador = r.GetInt64(8) != 0,
                    UltimoIngreso = r.IsDBNull(9) ? null : Fecha(r.GetString(9)),
                    UltimaIp = r.IsDBNull(10) ? "" : r.GetString(10),
                };
            }
            using (var c = Comando("SELECT nombre FROM personajes WHERE cuenta_id = @id AND borrado_en IS NULL ORDER BY id"))
            {
                c.Parameters.AddWithValue("@id", id);
                using var r = c.ExecuteReader();
                while (r.Read()) cuenta.Characters.Add(r.GetString(0));
            }
            return cuenta;
        }
    }

    /// <summary>Guarda los datos de la cuenta. La lista de personajes NO: sale de los personajes mismos
    /// (<c>personajes.cuenta_id</c>), que se suman con <see cref="CrearPersonaje"/> y se sacan con
    /// <see cref="ApartarPersonaje"/>.</summary>
    public void GuardarCuenta(Account cuenta)
    {
        lock (_deAUno)
        {
            using var tx = _con.BeginTransaction();
            GuardarCuenta(cuenta, tx);
            tx.Commit();
        }
    }

    private void GuardarCuenta(Account cuenta, SqliteTransaction tx)
    {
        using var c = Comando(
            "UPDATE cuentas SET clave_hash = @h, clave_sal = @s, codigo_hash = @c, baneada = @b, es_bot = @bot, " +
            "trampas = @t, donador = @d, ultimo_ingreso_en = @ui, ultima_ip = @ip WHERE upper(nombre) = upper(@n)", tx);
        ParametrosDeCuenta(c, cuenta);
        if (c.ExecuteNonQuery() > 0) return;
        c.CommandText = "INSERT INTO cuentas (nombre, clave_hash, clave_sal, codigo_hash, baneada, es_bot, trampas, donador, " +
                        "creada_en, ultimo_ingreso_en, ultima_ip) VALUES (@n, @h, @s, @c, @b, @bot, @t, @d, @ahora, @ui, @ip)";
        c.Parameters.AddWithValue("@ahora", Ahora());
        c.ExecuteNonQuery();
    }

    private static void ParametrosDeCuenta(SqliteCommand c, Account cuenta)
    {
        c.Parameters.AddWithValue("@n", cuenta.Name);
        c.Parameters.AddWithValue("@h", cuenta.PasswordHash);
        c.Parameters.AddWithValue("@s", cuenta.Salt);
        c.Parameters.AddWithValue("@c", cuenta.UserCodeHash);
        c.Parameters.AddWithValue("@b", cuenta.Banned);
        c.Parameters.AddWithValue("@bot", cuenta.EsBot);
        c.Parameters.AddWithValue("@t", cuenta.Trampas);
        c.Parameters.AddWithValue("@d", cuenta.Donador);
        c.Parameters.AddWithValue("@ui", cuenta.UltimoIngreso is { } f ? Texto(f) : (object)DBNull.Value);
        c.Parameters.AddWithValue("@ip", string.IsNullOrEmpty(cuenta.UltimaIp) ? DBNull.Value : (object)cuenta.UltimaIp);
    }

    public IEnumerable<string> NombresDeCuentas() => Nombres("SELECT nombre FROM cuentas");

    // ── Personajes ──────────────────────────────────────────────────────────────────────────────────────────────

    public bool ExistePersonaje(string nombre)
    {
        lock (_deAUno) return IdDePersonaje(nombre, null) is not null;
    }

    public IEnumerable<string> NombresDePersonajes() => Nombres("SELECT nombre FROM personajes WHERE borrado_en IS NULL");

    /// <summary>
    /// EL ALTA: el personaje se crea adentro de su cuenta, en una transacción que mira el tope de personajes por cuenta
    /// (<see cref="AccountFile.MaxCharacters"/>). Si la cuenta todavía no está en la base se la guarda con los datos que
    /// vienen, como hacía el alta de los archivos. Devuelve false si la cuenta ya está llena; un nombre repetido lo frena el
    /// índice único y sale como error.
    /// </summary>
    public bool CrearPersonaje(Account cuenta, FichaDePersonaje ficha)
    {
        lock (_deAUno)
        {
            using var tx = _con.BeginTransaction();
            long? cuentaId = IdDeCuenta(cuenta.Name, tx);
            if (cuentaId is null)
            {
                GuardarCuenta(cuenta, tx);
                cuentaId = IdDeCuenta(cuenta.Name, tx);
            }
            using (var c = Comando("SELECT count(*) FROM personajes WHERE cuenta_id = @c AND borrado_en IS NULL", tx))
            {
                c.Parameters.AddWithValue("@c", cuentaId);
                if ((long)c.ExecuteScalar()! >= AccountFile.MaxCharacters) return false;   // sin Commit: se deshace
            }
            long id;
            using (var c = Comando(
                $"INSERT INTO personajes (cuenta_id, nombre, creado_en, guardado_en, {ListaDeColumnas}) " +
                $"VALUES (@cuenta, @nombre, @ahora, @ahora, {ListaDeParametros}) RETURNING id", tx))
            {
                c.Parameters.AddWithValue("@cuenta", cuentaId);
                c.Parameters.AddWithValue("@nombre", ficha.Nombre);
                c.Parameters.AddWithValue("@ahora", Ahora());
                ParametrosDeFicha(c, ficha);
                id = (long)c.ExecuteScalar()!;
            }
            EscribirRanuras(id, ficha, tx);
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Guarda a un personaje que ya existe: su fila y todas sus ranuras, en una transacción. Uno que no existe NO se crea
    /// acá —no se sabría de qué cuenta es—: el alta es <see cref="CrearPersonaje"/>, y guardar a uno inexistente es un
    /// error que la cola anota.
    /// </summary>
    public void GuardarPersonaje(FichaDePersonaje ficha)
    {
        lock (_deAUno)
        {
            using var tx = _con.BeginTransaction();
            long id = IdDePersonaje(ficha.Nombre, tx)
                ?? throw new InvalidOperationException($"no hay un personaje '{ficha.Nombre}' para guardar: el alta es CrearPersonaje");
            using (var c = Comando($"UPDATE personajes SET {ListaDeAsignaciones}, guardado_en = @ahora WHERE id = @id", tx))
            {
                c.Parameters.AddWithValue("@id", id);
                c.Parameters.AddWithValue("@ahora", Ahora());
                ParametrosDeFicha(c, ficha);
                c.ExecuteNonQuery();
            }
            EscribirRanuras(id, ficha, tx);
            tx.Commit();
        }
    }

    public FichaDePersonaje? LeerPersonaje(string nombre)
    {
        lock (_deAUno)
        {
            FichaDePersonaje ficha;
            long id;
            using (var c = Comando("SELECT * FROM personajes WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL"))
            {
                c.Parameters.AddWithValue("@n", nombre);
                using var r = c.ExecuteReader();
                if (!r.Read()) return null;
                id = r.GetInt64(r.GetOrdinal("id"));
                ficha = FichaDeLaFila(r);
            }
            return LeerRanuras(id, ficha);
        }
    }

    /// <summary>Se APARTA: la fila queda —como el <c>Charfile/Borrados/</c> de los archivos— y el nombre queda libre, porque
    /// el índice único cuenta sólo a los vivos.</summary>
    public bool ApartarPersonaje(string nombre)
    {
        lock (_deAUno)
        {
            using var c = Comando("UPDATE personajes SET borrado_en = @ahora WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL");
            c.Parameters.AddWithValue("@n", nombre);
            c.Parameters.AddWithValue("@ahora", Ahora());
            return c.ExecuteNonQuery() > 0;
        }
    }

    public CharacterSummary? LeerResumen(string nombre)
    {
        lock (_deAUno)
        {
            using var c = Comando("SELECT nombre, cabeza, cuerpo, anim_casco, anim_arma, anim_escudo, nivel, clase, mapa " +
                                  "FROM personajes WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL");
            c.Parameters.AddWithValue("@n", nombre);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new CharacterSummary(Name: r.GetString(0), Head: (short)r.GetInt64(1), Body: (short)r.GetInt64(2),
                Helmet: (short)r.GetInt64(3), Weapon: (short)r.GetInt64(4), Shield: (short)r.GetInt64(5),
                Level: (byte)r.GetInt64(6), Class: (byte)r.GetInt64(7), Map: (short)r.GetInt64(8), Color: 0, GameMaster: false);
        }
    }

    /// <summary>Un cambio puntual: sólo el saldo, sin leer ni reescribir el resto del personaje.</summary>
    public long? AcreditarBoveda(string nombre, long monto)
    {
        lock (_deAUno)
        {
            using var c = Comando("UPDATE personajes SET oro_en_banco = oro_en_banco + @m " +
                                  "WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL RETURNING oro_en_banco");
            c.Parameters.AddWithValue("@m", monto);
            c.Parameters.AddWithValue("@n", nombre);
            return c.ExecuteScalar() is long saldo ? saldo : null;
        }
    }

    /// <summary>La tabla de posiciones, con una consulta y en una conexión propia: la pide la página pública desde su hilo,
    /// y no puede frenar al que escribe (ni esperarlo).</summary>
    public IReadOnlyList<CharacterFile.FilaDeRanking> FilasDeRanking()
    {
        using var con = new SqliteConnection(_cadena);
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = "SELECT nombre, nivel, exp, clase, clan, usuarios_matados, criaturas_matadas, veces_muerto " +
                        "FROM personajes WHERE borrado_en IS NULL";
        using var r = c.ExecuteReader();
        var filas = new List<CharacterFile.FilaDeRanking>();
        while (r.Read())
            filas.Add(new CharacterFile.FilaDeRanking(r.GetString(0), (int)r.GetInt64(1), r.GetDouble(2), (byte)r.GetInt64(3),
                r.GetString(4), Math.Max(0, (int)r.GetInt64(5)), Math.Max(0, (int)r.GetInt64(6)), Math.Max(0, (int)r.GetInt64(7))));
        return filas;
    }

    // ── Lo que guarda el mundo ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Un clan mientras se lo arma con las filas de sus tablas.</summary>
    private sealed class ClanArmandose(ClanGuardado datos)
    {
        public readonly ClanGuardado Datos = datos;
        public readonly List<string> Miembros = new(), Aliados = new(), Enemigos = new();
        public readonly List<KeyValuePair<string, string>> Alianza = new(), Paz = new(), Aspirantes = new(), Votos = new();
    }

    public IReadOnlyList<ClanGuardado> LeerClanes()
    {
        lock (_deAUno)
        {
            var clanes = new Dictionary<long, ClanArmandose>();
            var orden = new List<long>();
            using (var c = Comando("SELECT id, nombre, lider, descripcion, noticias, alineacion, url, codex, elecciones_abiertas " +
                                   "FROM clanes ORDER BY id"))
            using (var r = c.ExecuteReader())
            {
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    orden.Add(id);
                    clanes[id] = new ClanArmandose(new ClanGuardado
                    {
                        Nombre = r.GetString(1), Lider = r.GetString(2), Descripcion = r.GetString(3), Noticias = r.GetString(4),
                        Alineacion = (byte)r.GetInt64(5), Url = r.GetString(6), Codex = r.GetString(7),
                        EleccionesAbiertas = r.GetInt64(8) != 0,
                    });
                }
            }
            Hijos("SELECT clan_id, nombre FROM clanes_miembros ORDER BY rowid",
                (r, clan) => clan.Miembros.Add(r.GetString(1)));
            Hijos("SELECT clan_id, otro_clan, relacion FROM clanes_relaciones ORDER BY rowid",
                (r, clan) => (r.GetString(2) == "aliado" ? clan.Aliados : clan.Enemigos).Add(r.GetString(1)));
            Hijos("SELECT clan_id, de_clan, tipo, texto FROM clanes_propuestas ORDER BY rowid",
                (r, clan) => (r.GetString(2) == "alianza" ? clan.Alianza : clan.Paz).Add(new(r.GetString(1), r.GetString(3))));
            Hijos("SELECT clan_id, nombre, solicitud FROM clanes_aspirantes ORDER BY rowid",
                (r, clan) => clan.Aspirantes.Add(new(r.GetString(1), r.GetString(2))));
            Hijos("SELECT clan_id, votante, candidato FROM clanes_votos ORDER BY rowid",
                (r, clan) => clan.Votos.Add(new(r.GetString(1), r.GetString(2))));
            return orden.Select(id => clanes[id]).Select(c => c.Datos with
            {
                Miembros = c.Miembros.ToImmutableArray(), Aliados = c.Aliados.ToImmutableArray(),
                Enemigos = c.Enemigos.ToImmutableArray(), PropuestasDeAlianza = c.Alianza.ToImmutableArray(),
                PropuestasDePaz = c.Paz.ToImmutableArray(), Aspirantes = c.Aspirantes.ToImmutableArray(),
                Votos = c.Votos.ToImmutableArray(),
            }).ToList();

            void Hijos(string sql, Action<SqliteDataReader, ClanArmandose> fila)
            {
                using var c = Comando(sql);
                using var r = c.ExecuteReader();
                while (r.Read())
                    if (clanes.TryGetValue(r.GetInt64(0), out var clan)) fila(r, clan);
            }
        }
    }

    public void GuardarClanes(IReadOnlyList<ClanGuardado> clanes) => EnTransaccion(tx => EscribirClanes(clanes, tx));

    /// <summary>Se reescriben TODOS: el que no viene, se cerró. Son pocos, y la cascada se lleva los hijos del borrado.</summary>
    private void EscribirClanes(IReadOnlyList<ClanGuardado> clanes, SqliteTransaction tx)
    {
        using (var c = Comando("DELETE FROM clanes", tx)) c.ExecuteNonQuery();
        using var clan = Comando("INSERT INTO clanes (nombre, lider, descripcion, noticias, alineacion, url, codex, elecciones_abiertas) " +
                                 "VALUES (@n, @l, @d, @no, @a, @u, @c, @e) RETURNING id", tx);
        using var miembro = Comando("INSERT INTO clanes_miembros (clan_id, nombre) VALUES (@id, @n)", tx);
        using var relacion = Comando("INSERT INTO clanes_relaciones (clan_id, otro_clan, relacion) VALUES (@id, @o, @r)", tx);
        using var propuesta = Comando("INSERT INTO clanes_propuestas (clan_id, de_clan, tipo, texto) VALUES (@id, @de, @t, @x)", tx);
        using var aspirante = Comando("INSERT INTO clanes_aspirantes (clan_id, nombre, solicitud) VALUES (@id, @n, @s)", tx);
        using var voto = Comando("INSERT INTO clanes_votos (clan_id, votante, candidato) VALUES (@id, @v, @c)", tx);
        foreach (var g in clanes)
        {
            long id = (long)Valores(clan, ("@n", g.Nombre), ("@l", g.Lider), ("@d", g.Descripcion), ("@no", g.Noticias),
                ("@a", g.Alineacion), ("@u", g.Url), ("@c", g.Codex), ("@e", g.EleccionesAbiertas)).ExecuteScalar()!;
            foreach (var m in g.Miembros) Valores(miembro, ("@id", id), ("@n", m)).ExecuteNonQuery();
            foreach (var a in g.Aliados) Valores(relacion, ("@id", id), ("@o", a), ("@r", "aliado")).ExecuteNonQuery();
            foreach (var e in g.Enemigos) Valores(relacion, ("@id", id), ("@o", e), ("@r", "enemigo")).ExecuteNonQuery();
            foreach (var (de, texto) in g.PropuestasDeAlianza)
                Valores(propuesta, ("@id", id), ("@de", de), ("@t", "alianza"), ("@x", texto)).ExecuteNonQuery();
            foreach (var (de, texto) in g.PropuestasDePaz)
                Valores(propuesta, ("@id", id), ("@de", de), ("@t", "paz"), ("@x", texto)).ExecuteNonQuery();
            foreach (var (nombre, solicitud) in g.Aspirantes)
                Valores(aspirante, ("@id", id), ("@n", nombre), ("@s", solicitud)).ExecuteNonQuery();
            foreach (var (votante, candidato) in g.Votos)
                Valores(voto, ("@id", id), ("@v", votante), ("@c", candidato)).ExecuteNonQuery();
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> LeerRechazosDeClan()
        => Leer("SELECT personaje, motivo FROM rechazos_de_clan ORDER BY rowid", r => new KeyValuePair<string, string>(r.GetString(0), r.GetString(1)));

    public void GuardarRechazosDeClan(IReadOnlyList<KeyValuePair<string, string>> rechazos) => EnTransaccion(tx => EscribirRechazos(rechazos, tx));

    private void EscribirRechazos(IReadOnlyList<KeyValuePair<string, string>> rechazos, SqliteTransaction tx)
    {
        using (var c = Comando("DELETE FROM rechazos_de_clan", tx)) c.ExecuteNonQuery();
        using var fila = Comando("INSERT INTO rechazos_de_clan (personaje, motivo) VALUES (@p, @m)", tx);
        foreach (var (personaje, motivo) in rechazos) Valores(fila, ("@p", personaje), ("@m", motivo)).ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> LeerCorreo()
    {
        var casillas = new Dictionary<string, List<MailEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (destinatario, carta) in Leer("SELECT destinatario, remitente, mensaje, objeto, cantidad, leida FROM correo ORDER BY id",
                     r => (r.GetString(0), new MailEntry(r.GetString(1), r.GetString(2), (short)r.GetInt64(3), (short)r.GetInt64(4),
                         (byte)r.GetInt64(5)))))
        {
            if (!casillas.TryGetValue(destinatario, out var cartas)) casillas[destinatario] = cartas = new List<MailEntry>();
            cartas.Add(carta);
        }
        return casillas.ToDictionary(c => c.Key, c => (IReadOnlyList<MailEntry>)c.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sólo las casillas que vienen: se borran sus cartas y se escriben las de ahora. Las demás no se tocan.</summary>
    public void GuardarCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas) => EnTransaccion(tx => EscribirCasillas(casillas, tx));

    private void EscribirCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas, SqliteTransaction tx)
    {
        using var borrar = Comando("DELETE FROM correo WHERE upper(destinatario) = upper(@d)", tx);
        using var carta = Comando("INSERT INTO correo (destinatario, remitente, mensaje, objeto, cantidad, leida) " +
                                  "VALUES (@d, @r, @m, @o, @c, @l)", tx);
        foreach (var (destinatario, cartas) in casillas)
        {
            Valores(borrar, ("@d", destinatario)).ExecuteNonQuery();
            foreach (var m in cartas)
                Valores(carta, ("@d", destinatario), ("@r", m.Sender), ("@m", m.Message), ("@o", m.ObjIndex), ("@c", m.Amount),
                    ("@l", m.Leida)).ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ObjetoEnElSuelo> LeerSuelo()
        => Leer("SELECT mapa, x, y, objeto, cantidad FROM suelo ORDER BY mapa, x, y",
            r => new ObjetoEnElSuelo(new WorldPos((short)r.GetInt64(0), (byte)r.GetInt64(1), (byte)r.GetInt64(2)),
                (short)r.GetInt64(3), (short)r.GetInt64(4)));

    public void GuardarSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo) => EnTransaccion(tx => EscribirSuelo(suelo, tx));

    private void EscribirSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo, SqliteTransaction tx)
    {
        using (var c = Comando("DELETE FROM suelo", tx)) c.ExecuteNonQuery();
        using var fila = Comando("INSERT INTO suelo (mapa, x, y, objeto, cantidad) VALUES (@m, @x, @y, @o, @c)", tx);
        foreach (var o in suelo)
            Valores(fila, ("@m", o.Posicion.Map), ("@x", o.Posicion.X), ("@y", o.Posicion.Y), ("@o", o.Objeto), ("@c", o.Cantidad))
                .ExecuteNonQuery();
    }

    public IReadOnlyList<LlaveVendida> LeerLlavesVendidas()
        => Leer("SELECT mercader, ranura, objeto FROM llaves_vendidas ORDER BY mercader, ranura",
            r => new LlaveVendida((int)r.GetInt64(0), (int)r.GetInt64(1), (short)r.GetInt64(2)));

    public void GuardarLlavesVendidas(IReadOnlyList<LlaveVendida> llaves) => EnTransaccion(tx => EscribirLlaves(llaves, tx));

    private void EscribirLlaves(IReadOnlyList<LlaveVendida> llaves, SqliteTransaction tx)
    {
        using (var c = Comando("DELETE FROM llaves_vendidas", tx)) c.ExecuteNonQuery();
        using var fila = Comando("INSERT INTO llaves_vendidas (mercader, ranura, objeto) VALUES (@m, @r, @o)", tx);
        foreach (var l in llaves) Valores(fila, ("@m", l.Mercader), ("@r", l.Ranura), ("@o", l.Objeto)).ExecuteNonQuery();
    }

    public int LeerRecord() => Leer("SELECT record_usuarios FROM mundo WHERE id = 1", r => (int)r.GetInt64(0)).FirstOrDefault();

    public void GuardarRecord(int record) => EnTransaccion(tx => EscribirRecord(record, tx));

    private void EscribirRecord(int record, SqliteTransaction tx)
    {
        using var c = Comando("UPDATE mundo SET record_usuarios = @r WHERE id = 1", tx);
        Valores(c, ("@r", record)).ExecuteNonQuery();
    }

    /// <summary>
    /// LA VERSIÓN 2 PASA EL MUNDO A TABLAS. Lo que la 1 guardaba como JSON en <c>mundo_json</c> —el mismo formato de los
    /// archivos de <c>Misc/</c>— se lee con <see cref="JsonDelMundo"/> y se escribe en sus tablas, en la misma transacción que
    /// las crea, y la tabla vieja se va. Una base nueva pasa por acá con <c>mundo_json</c> vacía.
    /// </summary>
    private void PasarElMundoATablas(SqliteTransaction tx)
    {
        var viejo = new Dictionary<string, string>();
        using (var c = Comando("SELECT clave, texto FROM mundo_json", tx))
        using (var r = c.ExecuteReader())
            while (r.Read()) viejo[r.GetString(0)] = r.GetString(1);
        string? Guardado(string clave) => viejo.GetValueOrDefault(clave);
        EscribirClanes(JsonDelMundo.Clanes(Guardado("guilds")), tx);
        EscribirRechazos(JsonDelMundo.Rechazos(Guardado("rechazos-de-clan")), tx);
        EscribirCasillas(JsonDelMundo.Correo(Guardado("mailboxes")), tx);
        EscribirSuelo(JsonDelMundo.Suelo(Guardado("suelo")), tx);
        EscribirLlaves(JsonDelMundo.Llaves(Guardado("propiedades")), tx);
        EscribirRecord(JsonDelMundo.Record(Guardado("record")), tx);
        using (var c = Comando("DROP TABLE mundo_json", tx)) c.ExecuteNonQuery();
    }

    // ── Backup ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>VACUUM INTO</c>: una copia consistente con el servidor andando, en una conexión propia. Devuelve la ruta
    /// del <c>.db</c> creado.</summary>
    public string Backup(string destDir, string stem)
    {
        Directory.CreateDirectory(destDir);
        string ruta = Path.GetFullPath(Path.Combine(destDir, stem + ".db"));
        if (File.Exists(ruta)) File.Delete(ruta);   // VACUUM INTO falla si el destino ya existe
        using var con = new SqliteConnection(_cadena);
        con.Open();
        if (_enMemoria)
        {
            // Una base en memoria haría el VACUUM INTO también en memoria: se copia con la API de backup de SQLite.
            using var destino = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta, Pooling = false }.ToString());
            destino.Open();
            con.BackupDatabase(destino);
            return ruta;
        }
        using var c = con.CreateCommand();
        c.CommandText = "VACUUM INTO @ruta";
        c.Parameters.AddWithValue("@ruta", ruta);
        c.ExecuteNonQuery();
        return ruta;
    }

    // ── La ficha contra la fila ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// LAS COLUMNAS DE <c>personajes</c> QUE SALEN DE LA FICHA, cada una con su valor. Una sola lista para el alta y para
    /// el guardado, así no pueden quedar distintos. Leer va aparte (<see cref="FichaDeLaFila"/>): un campo nuevo tiene que
    /// entrar en los dos lugares, y lo cuida el test que guarda y relee una ficha con todos los campos distintos.
    /// </summary>
    private static readonly (string Columna, Func<FichaDePersonaje, object> Valor)[] Columnas =
    {
        ("genero", f => f.Genero), ("raza", f => f.Raza), ("clase", f => f.Clase), ("tierra_natal", f => f.TierraNatal),
        ("ciudad_hogar", f => f.CiudadHogar), ("minutos_de_carcel", f => f.MinutosDeCarcel),
        ("descripcion", f => f.Descripcion), ("rumbo", f => f.Rumbo), ("cabeza", f => f.Cabeza), ("cuerpo", f => f.Cuerpo),
        ("anim_arma", f => f.AnimArma), ("anim_escudo", f => f.AnimEscudo), ("anim_casco", f => f.AnimCasco),
        ("segundos_jugados", f => f.SegundosJugados),
        ("mapa", f => f.Posicion.Map), ("x", f => f.Posicion.X), ("y", f => f.Posicion.Y),
        ("oro", f => f.Oro), ("oro_en_banco", f => f.OroEnBanco), ("vida", f => f.Vida), ("vida_max", f => f.VidaMaxima),
        ("energia", f => f.Energia), ("energia_max", f => f.EnergiaMaxima), ("mana", f => f.Mana),
        ("mana_max", f => f.ManaMaximo), ("golpe_min", f => f.GolpeMinimo), ("golpe_max", f => f.GolpeMaximo),
        ("sed", f => f.Sed), ("sed_max", f => f.SedMaxima), ("hambre", f => f.Hambre), ("hambre_max", f => f.HambreMaxima),
        ("skill_pts_libres", f => f.SkillPtsLibres), ("nivel", f => f.Nivel), ("exp", f => f.Exp),
        ("exp_para_subir", f => f.ExpParaSubir),
        ("fuerza", f => Atributo(f, 1)), ("agilidad", f => Atributo(f, 2)), ("inteligencia", f => Atributo(f, 3)),
        ("carisma", f => Atributo(f, 4)), ("constitucion", f => Atributo(f, 5)),
        ("privilegios", f => f.Privilegios), ("baneado", f => f.Baneado), ("muerto", f => f.Muerto),
        ("navegando", f => f.Navegando), ("montando", f => f.Montando), ("veneno", f => f.Veneno),
        ("veces_muerto", f => f.VecesMuerto),
        ("bando", f => f.Bando), ("rango", f => f.Rango), ("renegados_matados", f => f.RenegadosMatados),
        ("ciudadanos_matados", f => f.CiudadanosMatados), ("republicanos_matados", f => f.RepublicanosMatados),
        ("armada_matados", f => f.ArmadaMatados), ("milicianos_matados", f => f.MilicianosMatados),
        ("caos_matados", f => f.CaosMatados), ("usuarios_matados", f => f.UsuariosMatados),
        ("criaturas_matadas", f => f.CriaturasMatadas),
        ("pareja", f => f.Pareja), ("clan", f => f.Clan), ("donador", f => f.Donador),
        ("creditos_donador", f => f.CreditosDonador), ("ranura_del_barco", f => f.RanuraDelBarco),
    };

    private static readonly string ListaDeColumnas = string.Join(", ", Columnas.Select(c => c.Columna));
    private static readonly string ListaDeParametros = string.Join(", ", Columnas.Select((_, i) => "@c" + i));
    private static readonly string ListaDeAsignaciones = string.Join(", ", Columnas.Select((c, i) => $"{c.Columna} = @c{i}"));

    private static byte Atributo(FichaDePersonaje f, int i) => i < f.Atributos.Length ? f.Atributos[i] : (byte)0;

    private static void ParametrosDeFicha(SqliteCommand c, FichaDePersonaje ficha)
    {
        for (int i = 0; i < Columnas.Length; i++) c.Parameters.AddWithValue("@c" + i, Columnas[i].Valor(ficha));
    }

    private static FichaDePersonaje FichaDeLaFila(SqliteDataReader r)
    {
        long L(string columna) => r.GetInt64(r.GetOrdinal(columna));
        string S(string columna) => r.GetString(r.GetOrdinal(columna));
        return new FichaDePersonaje
        {
            Nombre = S("nombre"),
            Genero = (byte)L("genero"),
            Raza = (byte)L("raza"),
            Clase = (byte)L("clase"),
            TierraNatal = (byte)L("tierra_natal"),
            CiudadHogar = (byte)L("ciudad_hogar"),
            MinutosDeCarcel = (int)L("minutos_de_carcel"),
            Descripcion = S("descripcion"),
            Rumbo = (byte)L("rumbo"),
            Cabeza = (short)L("cabeza"),
            Cuerpo = (short)L("cuerpo"),
            AnimArma = (short)L("anim_arma"),
            AnimEscudo = (short)L("anim_escudo"),
            AnimCasco = (short)L("anim_casco"),
            SegundosJugados = L("segundos_jugados"),
            Posicion = new WorldPos((short)L("mapa"), (byte)L("x"), (byte)L("y")),
            Oro = (int)L("oro"),
            OroEnBanco = L("oro_en_banco"),
            Vida = (int)L("vida"),
            VidaMaxima = (int)L("vida_max"),
            Energia = (int)L("energia"),
            EnergiaMaxima = (int)L("energia_max"),
            Mana = (int)L("mana"),
            ManaMaximo = (int)L("mana_max"),
            GolpeMinimo = (int)L("golpe_min"),
            GolpeMaximo = (int)L("golpe_max"),
            Sed = (byte)L("sed"),
            SedMaxima = (byte)L("sed_max"),
            Hambre = (byte)L("hambre"),
            HambreMaxima = (byte)L("hambre_max"),
            SkillPtsLibres = (short)L("skill_pts_libres"),
            Nivel = (byte)L("nivel"),
            Exp = r.GetDouble(r.GetOrdinal("exp")),
            ExpParaSubir = L("exp_para_subir"),
            Atributos = ImmutableArray.Create(new byte[]
            {
                0, (byte)L("fuerza"), (byte)L("agilidad"), (byte)L("inteligencia"), (byte)L("carisma"), (byte)L("constitucion"),
            }),
            Privilegios = (byte)L("privilegios"),
            Baneado = L("baneado") != 0,
            Muerto = L("muerto") != 0,
            Navegando = L("navegando") != 0,
            Montando = L("montando") != 0,
            Veneno = (byte)L("veneno"),
            VecesMuerto = (int)L("veces_muerto"),
            Bando = (byte)L("bando"),
            Rango = (byte)L("rango"),
            RenegadosMatados = (int)L("renegados_matados"),
            CiudadanosMatados = (int)L("ciudadanos_matados"),
            RepublicanosMatados = (int)L("republicanos_matados"),
            ArmadaMatados = (int)L("armada_matados"),
            MilicianosMatados = (int)L("milicianos_matados"),
            CaosMatados = (int)L("caos_matados"),
            UsuariosMatados = (int)L("usuarios_matados"),
            CriaturasMatadas = (int)L("criaturas_matadas"),
            Pareja = S("pareja"),
            Clan = S("clan"),
            Donador = L("donador") != 0,
            CreditosDonador = L("creditos_donador"),
            RanuraDelBarco = (byte)L("ranura_del_barco"),
        };
    }

    /// <summary>Las ranuras se reescriben enteras: se borran las del personaje y se insertan las ocupadas. Una ranura con
    /// objeto 0 es una ranura vacía, y vacía no tiene fila.</summary>
    private void EscribirRanuras(long id, FichaDePersonaje ficha, SqliteTransaction tx)
    {
        using (var c = Comando("DELETE FROM inventario WHERE personaje_id = @id; DELETE FROM boveda WHERE personaje_id = @id; " +
                               "DELETE FROM hechizos WHERE personaje_id = @id; DELETE FROM skills WHERE personaje_id = @id; " +
                               "DELETE FROM amigos WHERE personaje_id = @id; DELETE FROM penas WHERE personaje_id = @id; " +
                               "DELETE FROM familiares WHERE personaje_id = @id;", tx))
        {
            c.Parameters.AddWithValue("@id", id);
            c.ExecuteNonQuery();
        }

        using (var c = Comando("INSERT INTO inventario (personaje_id, ranura, objeto, cantidad, equipado) VALUES (@id, @r, @o, @n, @e)", tx))
        {
            var (r, o, n, e) = (Param(c, "@r"), Param(c, "@o"), Param(c, "@n"), Param(c, "@e"));
            c.Parameters.AddWithValue("@id", id);
            for (int i = 1; i < ficha.Inventario.Length && i <= GameLimits.MaxInventorySlots; i++)
            {
                var s = ficha.Inventario[i];
                if (s.Vacia) continue;
                r.Value = i; o.Value = s.Objeto; n.Value = s.Cantidad; e.Value = s.Equipado;
                c.ExecuteNonQuery();
            }
        }
        using (var c = Comando("INSERT INTO boveda (personaje_id, ranura, objeto, cantidad) VALUES (@id, @r, @o, @n)", tx))
        {
            var (r, o, n) = (Param(c, "@r"), Param(c, "@o"), Param(c, "@n"));
            c.Parameters.AddWithValue("@id", id);
            for (int i = 1; i < ficha.Boveda.Length && i <= GameLimits.MaxBankSlots; i++)
            {
                var s = ficha.Boveda[i];
                if (s.Vacia) continue;
                r.Value = i; o.Value = s.Objeto; n.Value = s.Cantidad;
                c.ExecuteNonQuery();
            }
        }
        using (var c = Comando("INSERT INTO hechizos (personaje_id, ranura, hechizo) VALUES (@id, @r, @h)", tx))
        {
            var (r, h) = (Param(c, "@r"), Param(c, "@h"));
            c.Parameters.AddWithValue("@id", id);
            for (int i = 1; i < ficha.Hechizos.Length && i <= GameLimits.MaxUserSpells; i++)
            {
                if (ficha.Hechizos[i] == 0) continue;
                r.Value = i; h.Value = ficha.Hechizos[i];
                c.ExecuteNonQuery();
            }
        }
        using (var c = Comando("INSERT INTO skills (personaje_id, skill, valor) VALUES (@id, @s, @v)", tx))
        {
            var (s, v) = (Param(c, "@s"), Param(c, "@v"));
            c.Parameters.AddWithValue("@id", id);
            for (int i = 1; i <= GameLimits.NumSkills; i++)
            {
                s.Value = i; v.Value = i < ficha.Skills.Length ? ficha.Skills[i] : (byte)0;
                c.ExecuteNonQuery();
            }
        }
        EscribirLista("amigos", "nombre", id, ficha.Amigos, tx);
        EscribirLista("penas", "texto", id, ficha.Penas, tx);
        if (ficha.Familiar is { } f)
        {
            using var c = Comando(
                "INSERT INTO familiares (personaje_id, tipo, nombre, nivel, exp, exp_para_subir, vida, vida_max, golpe_min, golpe_max, " +
                "muerto, habilidad_1, habilidad_2, habilidad_3, habilidad_4) " +
                "VALUES (@id, @tipo, @nombre, @nivel, @exp, @elu, @vida, @vidamax, @gmin, @gmax, @muerto, @h1, @h2, @h3, @h4)", tx);
            c.Parameters.AddWithValue("@id", id);
            c.Parameters.AddWithValue("@tipo", f.Tipo);
            c.Parameters.AddWithValue("@nombre", f.Nombre);
            c.Parameters.AddWithValue("@nivel", f.Nivel);
            c.Parameters.AddWithValue("@exp", f.Exp);
            c.Parameters.AddWithValue("@elu", f.Elu);
            c.Parameters.AddWithValue("@vida", f.Vida);
            c.Parameters.AddWithValue("@vidamax", f.VidaMaxima);
            c.Parameters.AddWithValue("@gmin", f.GolpeMinimo);
            c.Parameters.AddWithValue("@gmax", f.GolpeMaximo);
            c.Parameters.AddWithValue("@muerto", f.Muerto);
            for (int i = 0; i < Familiar.CantidadDeHabilidades; i++)
                c.Parameters.AddWithValue("@h" + (i + 1), i < f.Habilidades.Length ? f.Habilidades[i] : 0);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Los amigos y las penas: en orden, sin los renglones en blanco (que el cargador tampoco levanta).</summary>
    private void EscribirLista(string tabla, string columna, long id, ImmutableArray<string> valores, SqliteTransaction tx)
    {
        using var c = Comando($"INSERT INTO {tabla} (personaje_id, orden, {columna}) VALUES (@id, @o, @v)", tx);
        var (o, v) = (Param(c, "@o"), c.Parameters.Add("@v", SqliteType.Text));
        c.Parameters.AddWithValue("@id", id);
        int orden = 0;
        foreach (var valor in valores)
        {
            if (string.IsNullOrWhiteSpace(valor)) continue;
            o.Value = ++orden; v.Value = valor;
            c.ExecuteNonQuery();
        }
    }

    private FichaDePersonaje LeerRanuras(long id, FichaDePersonaje ficha)
    {
        var inventario = new RanuraGuardada[GameLimits.MaxInventorySlots + 1];
        foreach (var (ranura, objeto, cantidad, equipado) in Filas(id,
                     "SELECT ranura, objeto, cantidad, equipado FROM inventario WHERE personaje_id = @id",
                     r => ((int)r.GetInt64(0), (short)r.GetInt64(1), (short)r.GetInt64(2), r.GetInt64(3) != 0)))
            inventario[ranura] = new RanuraGuardada(objeto, cantidad, equipado);

        var boveda = new RanuraGuardada[GameLimits.MaxBankSlots + 1];
        foreach (var (ranura, objeto, cantidad) in Filas(id, "SELECT ranura, objeto, cantidad FROM boveda WHERE personaje_id = @id",
                     r => ((int)r.GetInt64(0), (short)r.GetInt64(1), (short)r.GetInt64(2))))
            boveda[ranura] = new RanuraGuardada(objeto, cantidad, false);

        var hechizos = new short[GameLimits.MaxUserSpells + 1];
        foreach (var (ranura, hechizo) in Filas(id, "SELECT ranura, hechizo FROM hechizos WHERE personaje_id = @id",
                     r => ((int)r.GetInt64(0), (short)r.GetInt64(1))))
            hechizos[ranura] = hechizo;

        var skills = new byte[GameLimits.NumSkills + 1];
        foreach (var (skill, valor) in Filas(id, "SELECT skill, valor FROM skills WHERE personaje_id = @id",
                     r => ((int)r.GetInt64(0), (byte)r.GetInt64(1))))
            skills[skill] = valor;

        var amigos = Filas(id, "SELECT nombre FROM amigos WHERE personaje_id = @id ORDER BY orden", r => r.GetString(0));
        var penas = Filas(id, "SELECT texto FROM penas WHERE personaje_id = @id ORDER BY orden", r => r.GetString(0));

        var familiar = Filas(id,
            "SELECT tipo, nombre, nivel, exp, exp_para_subir, vida, vida_max, golpe_min, golpe_max, muerto, " +
            "habilidad_1, habilidad_2, habilidad_3, habilidad_4 FROM familiares WHERE personaje_id = @id",
            r => new FichaDeFamiliar((byte)r.GetInt64(0), r.GetString(1), (byte)r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9) != 0,
                ImmutableArray.Create((int)r.GetInt64(10), (int)r.GetInt64(11), (int)r.GetInt64(12), (int)r.GetInt64(13))))
            .FirstOrDefault();

        return ficha with
        {
            Inventario = ImmutableArray.Create(inventario),
            Boveda = ImmutableArray.Create(boveda),
            Hechizos = ImmutableArray.Create(hechizos),
            Skills = ImmutableArray.Create(skills),
            Amigos = amigos.ToImmutableArray(),
            Penas = penas.ToImmutableArray(),
            Familiar = familiar,
        };
    }

    // ── Ayudantes ───────────────────────────────────────────────────────────────────────────────────────────────

    private SqliteCommand Comando(string sql, SqliteTransaction? tx = null)
    {
        var c = _con.CreateCommand();
        c.CommandText = sql;
        c.Transaction = tx;
        return c;
    }

    private void Ejecutar(string sql)
    {
        using var c = Comando(sql);
        c.ExecuteNonQuery();
    }

    private static SqliteParameter Param(SqliteCommand c, string nombre) => c.Parameters.Add(nombre, SqliteType.Integer);

    /// <summary>Carga los parámetros de un comando que se usa muchas veces y lo devuelve, listo para ejecutar.</summary>
    private static SqliteCommand Valores(SqliteCommand c, params (string Nombre, object Valor)[] valores)
    {
        c.Parameters.Clear();
        foreach (var (nombre, valor) in valores) c.Parameters.AddWithValue(nombre, valor);
        return c;
    }

    private void EnTransaccion(Action<SqliteTransaction> escritura)
    {
        lock (_deAUno)
        {
            using var tx = _con.BeginTransaction();
            escritura(tx);
            tx.Commit();
        }
    }

    private List<T> Leer<T>(string sql, Func<SqliteDataReader, T> fila)
    {
        lock (_deAUno)
        {
            using var c = Comando(sql);
            using var r = c.ExecuteReader();
            var filas = new List<T>();
            while (r.Read()) filas.Add(fila(r));
            return filas;
        }
    }

    private List<T> Filas<T>(long id, string sql, Func<SqliteDataReader, T> fila)
    {
        using var c = Comando(sql);
        c.Parameters.AddWithValue("@id", id);
        using var r = c.ExecuteReader();
        var filas = new List<T>();
        while (r.Read()) filas.Add(fila(r));
        return filas;
    }

    private List<string> Nombres(string sql)
    {
        lock (_deAUno)
        {
            using var c = Comando(sql);
            using var r = c.ExecuteReader();
            var nombres = new List<string>();
            while (r.Read()) nombres.Add(r.GetString(0));
            return nombres;
        }
    }

    private long? IdDeCuenta(string nombre, SqliteTransaction? tx)
    {
        using var c = Comando("SELECT id FROM cuentas WHERE upper(nombre) = upper(@n)", tx);
        c.Parameters.AddWithValue("@n", nombre);
        return c.ExecuteScalar() is long id ? id : null;
    }

    private long? IdDePersonaje(string nombre, SqliteTransaction? tx)
    {
        using var c = Comando("SELECT id FROM personajes WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL", tx);
        c.Parameters.AddWithValue("@n", nombre);
        return c.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Las fechas van en UTC, como texto ISO 8601: ordenan igual como texto que como fecha.</summary>
    private static string Ahora() => Texto(DateTime.UtcNow);
    private static string Texto(DateTime fecha) => fecha.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private static DateTime Fecha(string texto) => DateTime.Parse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
