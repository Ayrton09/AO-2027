using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// EL ALMACÉN EN UNA BASE CON TABLAS DE VERDAD (fase5/postgres/DISENO.md): una tabla por cosa —cuentas, personajes,
/// inventario, bóveda, hechizos, skills, amigos, penas, familiar, y del mundo los clanes, el correo, el suelo, las llaves
/// vendidas y el récord— con el esquema de <c>Data/Esquema/</c>.
///
/// <para><b>Un solo SQL para los dos motores.</b> Lo que cambia de SQLite a Postgres —cómo se abre la conexión, cómo van una
/// fecha y una IP, el backup— lo pone cada uno (<see cref="AlmacenSqlite"/>, <see cref="AlmacenPostgres"/>); todo lo demás
/// está acá, y la batería de contrato corre contra los dos.</para>
///
/// <para><b>Una conexión escribe; las lecturas de afuera abren la suya.</b> Lo del juego llega por la cola de la base
/// (<see cref="ColaDeLaBase"/>): un solo hilo y una sola conexión. La tabla de posiciones de la página pública y el backup
/// corren en otros hilos y abren otra conexión, que no frena al que escribe.</para>
///
/// <para><b>Cada guardado es una transacción.</b> El personaje y sus ranuras entran juntos o no entra nada: un corte a la
/// mitad no deja el inventario de un guardado con las stats del anterior.</para>
///
/// <para><b>Los nombres no distinguen mayúsculas</b> (<c>upper(nombre)</c>, con índice único) y se guardan como los escribió
/// el jugador. Un personaje borrado se APARTA: la fila queda con <c>borrado_en</c> y el nombre queda libre.</para>
/// </summary>
public abstract class AlmacenRelacional : IAlmacen, IDisposable
{
    /// <summary>De a uno: la conexión que escribe no se usa desde dos hilos.</summary>
    protected readonly object DeAUno = new();
    private DbTransaction? _grande;

    // ── Lo que pone cada motor ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>El de <c>Esquema/NNN_nombre.{motor}.sql</c>: <c>sqlite</c> o <c>postgres</c>.</summary>
    protected abstract string Motor { get; }

    /// <summary>La conexión que escribe.</summary>
    protected abstract DbConnection Conexion { get; }

    /// <summary>Otra conexión, ya abierta, para lo que corre fuera de la cola: la tabla de posiciones y el backup.</summary>
    protected abstract DbConnection OtraConexion();

    /// <summary>Antes de cada operación. La de Postgres reabre la conexión si se cortó.</summary>
    protected virtual void Preparar() { }

    /// <summary>La tabla <c>migraciones</c> en el dialecto del motor, si no está.</summary>
    protected abstract string TablaDeMigraciones { get; }

    /// <summary>El valor de una columna de fecha (en UTC).</summary>
    protected abstract object ValorDeFecha(DateTime utc);
    protected abstract DateTime LeerFecha(DbDataReader r, int columna);

    /// <summary>El valor de la columna de la IP, o <see cref="DBNull"/> si no se la puede guardar.</summary>
    protected abstract object ValorDeIp(string ip);
    protected abstract string LeerIp(DbDataReader r, int columna);

    public abstract string Backup(string destDir, string stem);
    public abstract void Dispose();

    /// <summary>¿Hay una operación de <see cref="EnUnaSolaTransaccion{T}"/> en curso? Ahí una conexión cortada no se reabre:
    /// lo que siguiera iría fuera de la transacción.</summary>
    protected bool EnUnaTransaccionGrande => _grande is not null;

    // ── El esquema ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Las versiones del esquema que trae este servidor para <paramref name="motor"/>, en orden:
    /// <c>Esquema/NNN_nombre.{motor}.sql</c>.</summary>
    internal static IReadOnlyList<(int Version, string Nombre, string Sql)> Versiones(string motor)
    {
        var asm = typeof(AlmacenRelacional).Assembly;
        var versiones = new List<(int, string, string)>();
        string prefijo = "Esquema/", sufijo = $".{motor}.sql";
        foreach (var recurso in asm.GetManifestResourceNames())
        {
            if (!recurso.StartsWith(prefijo, StringComparison.Ordinal) || !recurso.EndsWith(sufijo, StringComparison.Ordinal)) continue;
            string nombre = recurso[prefijo.Length..^sufijo.Length];
            int corte = nombre.IndexOf('_');
            if (corte <= 0 || !int.TryParse(nombre[..corte], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                throw new InvalidOperationException($"versión del esquema sin número: {recurso}");
            using var s = asm.GetManifestResourceStream(recurso)!;
            using var lector = new StreamReader(s);
            versiones.Add((version, nombre[(corte + 1)..], lector.ReadToEnd()));
        }
        return versiones.OrderBy(v => v.Item1).ToList();
    }

    /// <summary>La versión del esquema que tiene la base.</summary>
    internal long VersionDelEsquema() => Operacion(() => Convert.ToInt64(Escalar("SELECT coalesce(max(version), 0) FROM migraciones")));

    /// <summary>
    /// Aplica las versiones que falten, cada una en su transacción, y las anota en <c>migraciones</c>. Una base con un
    /// esquema MÁS NUEVO que el de este servidor no se abre: es la misma regla que el sello del mundo —si venís de una
    /// versión más nueva, pará y restaurá un backup—, porque escribirle encima con un esquema viejo la rompería. Lo llama el
    /// constructor de cada motor, con la conexión ya abierta.
    /// </summary>
    protected void Migrar()
    {
        Ejecutar(TablaDeMigraciones);
        long actual = Convert.ToInt64(Escalar("SELECT coalesce(max(version), 0) FROM migraciones"));
        var versiones = Versiones(Motor);
        int ultima = versiones.Count == 0 ? 0 : versiones[^1].Version;
        if (actual > ultima)
            throw new InvalidOperationException(
                $"La base es de un servidor más nuevo: tiene el esquema {actual} y éste conoce hasta el {ultima}. " +
                "Actualizá el servidor o restaurá un backup.");
        foreach (var (version, nombre, sql) in versiones.Where(v => v.Version > actual))
        {
            using var tx = Conexion.BeginTransaction();
            using (var c = Comando(sql, tx)) c.ExecuteNonQuery();
            if (version == 2) PasarElMundoATablas(tx);   // lo que la versión 1 tenía como JSON
            using (var c = Comando("INSERT INTO migraciones (version, descripcion, aplicada_en) VALUES (@v, @d, @t)", tx))
            {
                Valores(c, ("@v", version), ("@d", nombre), ("@t", ValorDeFecha(DateTime.UtcNow)));
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ── Todo junto ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Corre <paramref name="cuerpo"/> entero en UNA transacción: lo que escriba entra todo junto, y sólo si
    /// <paramref name="vale"/> dice que sí; si no —o si algo falla— no entra nada. Es el todo o nada de importar a una base
    /// que no se puede llenar aparte y después poner en su lugar, como el archivo de SQLite: la de Postgres. Adentro, cada
    /// operación es un punto de guardado de la grande (ver <see cref="Tramo"/>).
    /// </summary>
    internal T EnUnaSolaTransaccion<T>(Func<T> cuerpo, Func<T, bool> vale)
    {
        lock (DeAUno)
        {
            Preparar();
            using var tx = Conexion.BeginTransaction();
            _grande = tx;
            try
            {
                var resultado = cuerpo();
                if (vale(resultado)) tx.Commit();   // si no, el Dispose la deshace
                return resultado;
            }
            finally { _grande = null; }
        }
    }

    /// <summary>¿No hay nadie? Ni cuentas ni personajes: una base recién creada, o una a la que se puede importar.</summary>
    internal bool EstaVacia()
        => Operacion(() => Escalar("SELECT 1 FROM cuentas LIMIT 1") is null && Escalar("SELECT 1 FROM personajes LIMIT 1") is null);

    /// <summary>Borra todo lo guardado —cuentas, personajes, el mundo— y deja el esquema. Para <c>importar --reemplazar</c>,
    /// adentro de su transacción y después de un backup.</summary>
    internal void Vaciar() => Operacion(() =>
    {
        using var tramo = new Tramo(Conexion, _grande);
        foreach (var sql in new[]
                 {
                     "DELETE FROM personajes", "DELETE FROM cuentas", "DELETE FROM clanes", "DELETE FROM rechazos_de_clan",
                     "DELETE FROM correo", "DELETE FROM suelo", "DELETE FROM llaves_vendidas", "UPDATE mundo SET record_usuarios = 0",
                 })
        {
            using var c = Comando(sql, tramo.Tx);
            c.ExecuteNonQuery();
        }
        tramo.Confirmar();
    });

    /// <summary>
    /// La transacción de una operación: propia, o —adentro de <see cref="EnUnaSolaTransaccion{T}"/>— un punto de guardado de
    /// la grande, así lo que se deshace es sólo lo de esta operación. Sin <see cref="Confirmar"/>, se deshace: un alta que
    /// no entra, o una que falla a la mitad.
    /// </summary>
    private sealed class Tramo : IDisposable
    {
        private const string Punto = "tramo";
        private readonly DbTransaction? _propia;
        private readonly DbTransaction? _grande;
        private bool _confirmado;

        public DbTransaction Tx { get; }

        public Tramo(DbConnection conexion, DbTransaction? grande)
        {
            if (grande is null) Tx = _propia = conexion.BeginTransaction();
            else
            {
                Tx = _grande = grande;
                grande.Save(Punto);
            }
        }

        public void Confirmar()
        {
            if (_propia is not null) _propia.Commit();
            else _grande!.Release(Punto);
            _confirmado = true;
        }

        public void Dispose()
        {
            if (_propia is not null) _propia.Dispose();
            else if (!_confirmado)
            {
                _grande!.Rollback(Punto);
                _grande.Release(Punto);
            }
        }
    }

    // ── Cuentas ─────────────────────────────────────────────────────────────────────────────────────────────────

    public bool ExisteCuenta(string nombre) => Operacion(() =>
    {
        using var c = Comando("SELECT 1 FROM cuentas WHERE upper(nombre) = upper(@n)");
        Valores(c, ("@n", nombre));
        return c.ExecuteScalar() is not null;
    });

    /// <summary>La cuenta con sus personajes vivos, en el orden en que se crearon (el de la lista <c>[PJS]</c>).</summary>
    public Account? LeerCuenta(string nombre) => Operacion(() =>
    {
        Account cuenta;
        long id;
        using (var c = Comando("SELECT id, nombre, clave_hash, clave_sal, codigo_hash, baneada, es_bot, trampas, donador, " +
                               "ultimo_ingreso_en, ultima_ip FROM cuentas WHERE upper(nombre) = upper(@n)"))
        {
            Valores(c, ("@n", nombre));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            id = Entero(r, 0);
            cuenta = new Account
            {
                Name = r.GetString(1),
                PasswordHash = r.GetString(2),
                Salt = r.GetString(3),
                UserCodeHash = r.GetString(4),
                Banned = r.GetBoolean(5),
                EsBot = r.GetBoolean(6),
                Trampas = (int)Entero(r, 7),
                Donador = r.GetBoolean(8),
                UltimoIngreso = r.IsDBNull(9) ? null : LeerFecha(r, 9),
                UltimaIp = r.IsDBNull(10) ? "" : LeerIp(r, 10),
            };
        }
        using (var c = Comando("SELECT nombre FROM personajes WHERE cuenta_id = @id AND borrado_en IS NULL ORDER BY id"))
        {
            Valores(c, ("@id", id));
            using var r = c.ExecuteReader();
            while (r.Read()) cuenta.Characters.Add(r.GetString(0));
        }
        return (Account?)cuenta;
    });

    /// <summary>Guarda los datos de la cuenta. La lista de personajes NO: sale de los personajes mismos
    /// (<c>personajes.cuenta_id</c>), que se suman con <see cref="CrearPersonaje"/> y se sacan con
    /// <see cref="ApartarPersonaje"/>.</summary>
    public void GuardarCuenta(Account cuenta) => Operacion(() =>
    {
        using var tramo = new Tramo(Conexion, _grande);
        GuardarCuenta(cuenta, tramo.Tx);
        tramo.Confirmar();
    });

    private void GuardarCuenta(Account cuenta, DbTransaction tx)
    {
        using var c = Comando(
            "UPDATE cuentas SET clave_hash = @h, clave_sal = @s, codigo_hash = @c, baneada = @b, es_bot = @bot, " +
            "trampas = @t, donador = @d, ultimo_ingreso_en = @ui, ultima_ip = @ip WHERE upper(nombre) = upper(@n)", tx);
        ParametrosDeCuenta(c, cuenta);
        if (c.ExecuteNonQuery() > 0) return;
        c.CommandText = "INSERT INTO cuentas (nombre, clave_hash, clave_sal, codigo_hash, baneada, es_bot, trampas, donador, " +
                        "creada_en, ultimo_ingreso_en, ultima_ip) VALUES (@n, @h, @s, @c, @b, @bot, @t, @d, @ahora, @ui, @ip)";
        Parametro(c, "@ahora", Ahora());
        c.ExecuteNonQuery();
    }

    private void ParametrosDeCuenta(DbCommand c, Account cuenta)
    {
        Valores(c, ("@n", cuenta.Name), ("@h", cuenta.PasswordHash), ("@s", cuenta.Salt), ("@c", cuenta.UserCodeHash),
            ("@b", cuenta.Banned), ("@bot", cuenta.EsBot), ("@t", cuenta.Trampas), ("@d", cuenta.Donador),
            ("@ui", cuenta.UltimoIngreso is { } f ? ValorDeFecha(f.ToUniversalTime()) : DBNull.Value),
            ("@ip", string.IsNullOrEmpty(cuenta.UltimaIp) ? DBNull.Value : ValorDeIp(cuenta.UltimaIp)));
    }

    public IEnumerable<string> NombresDeCuentas() => Nombres("SELECT nombre FROM cuentas");

    // ── Personajes ──────────────────────────────────────────────────────────────────────────────────────────────

    public bool ExistePersonaje(string nombre) => Operacion(() => IdDePersonaje(nombre, null) is not null);

    public IEnumerable<string> NombresDePersonajes() => Nombres("SELECT nombre FROM personajes WHERE borrado_en IS NULL");

    /// <summary>
    /// EL ALTA: el personaje se crea adentro de su cuenta, en una transacción que mira el tope de personajes por cuenta
    /// (<see cref="AccountFile.MaxCharacters"/>). Si la cuenta todavía no está en la base se la guarda con los datos que
    /// vienen, como hacía el alta de los archivos. Devuelve false si la cuenta ya está llena; un nombre repetido lo frena el
    /// índice único y sale como error.
    /// </summary>
    public bool CrearPersonaje(Account cuenta, FichaDePersonaje ficha) => Operacion(() =>
    {
        using var tramo = new Tramo(Conexion, _grande);
        long? cuentaId = IdDeCuenta(cuenta.Name, tramo.Tx);
        if (cuentaId is null)
        {
            GuardarCuenta(cuenta, tramo.Tx);
            cuentaId = IdDeCuenta(cuenta.Name, tramo.Tx);
        }
        using (var c = Comando("SELECT count(*) FROM personajes WHERE cuenta_id = @c AND borrado_en IS NULL", tramo.Tx))
        {
            Valores(c, ("@c", cuentaId));
            if (Convert.ToInt64(c.ExecuteScalar()) >= AccountFile.MaxCharacters) return false;   // sin Confirmar: se deshace
        }
        long id;
        using (var c = Comando(
            $"INSERT INTO personajes (cuenta_id, nombre, creado_en, guardado_en, {ListaDeColumnas}) " +
            $"VALUES (@cuenta, @nombre, @ahora, @ahora, {ListaDeParametros}) RETURNING id", tramo.Tx))
        {
            Valores(c, ("@cuenta", cuentaId), ("@nombre", ficha.Nombre), ("@ahora", Ahora()));
            ParametrosDeFicha(c, ficha);
            id = Convert.ToInt64(c.ExecuteScalar());
        }
        EscribirRanuras(id, ficha, tramo.Tx);
        tramo.Confirmar();
        return true;
    });

    /// <summary>
    /// Guarda a un personaje que ya existe: su fila y todas sus ranuras, en una transacción. Uno que no existe NO se crea
    /// acá —no se sabría de qué cuenta es—: el alta es <see cref="CrearPersonaje"/>, y guardar a uno inexistente es un
    /// error que la cola anota.
    /// </summary>
    public void GuardarPersonaje(FichaDePersonaje ficha) => Operacion(() =>
    {
        using var tramo = new Tramo(Conexion, _grande);
        long id = IdDePersonaje(ficha.Nombre, tramo.Tx)
            ?? throw new InvalidOperationException($"no hay un personaje '{ficha.Nombre}' para guardar: el alta es CrearPersonaje");
        using (var c = Comando($"UPDATE personajes SET {ListaDeAsignaciones}, guardado_en = @ahora WHERE id = @id", tramo.Tx))
        {
            Valores(c, ("@id", id), ("@ahora", Ahora()));
            ParametrosDeFicha(c, ficha);
            c.ExecuteNonQuery();
        }
        EscribirRanuras(id, ficha, tramo.Tx);
        tramo.Confirmar();
    });

    public FichaDePersonaje? LeerPersonaje(string nombre) => Operacion(() =>
    {
        FichaDePersonaje ficha;
        long id;
        using (var c = Comando("SELECT * FROM personajes WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL"))
        {
            Valores(c, ("@n", nombre));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            id = Entero(r, r.GetOrdinal("id"));
            ficha = FichaDeLaFila(r);
        }
        return (FichaDePersonaje?)LeerRanuras(id, ficha);
    });

    /// <summary>Se APARTA: la fila queda —como el <c>Charfile/Borrados/</c> de los archivos— y el nombre queda libre, porque
    /// el índice único cuenta sólo a los vivos.</summary>
    public bool ApartarPersonaje(string nombre) => Operacion(() =>
    {
        using var c = Comando("UPDATE personajes SET borrado_en = @ahora WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL");
        Valores(c, ("@n", nombre), ("@ahora", Ahora()));
        return c.ExecuteNonQuery() > 0;
    });

    public CharacterSummary? LeerResumen(string nombre) => Operacion(() =>
    {
        using var c = Comando("SELECT nombre, cabeza, cuerpo, anim_casco, anim_arma, anim_escudo, nivel, clase, mapa " +
                              "FROM personajes WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL");
        Valores(c, ("@n", nombre));
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return (CharacterSummary?)new CharacterSummary(Name: r.GetString(0), Head: (short)Entero(r, 1), Body: (short)Entero(r, 2),
            Helmet: (short)Entero(r, 3), Weapon: (short)Entero(r, 4), Shield: (short)Entero(r, 5),
            Level: (byte)Entero(r, 6), Class: (byte)Entero(r, 7), Map: (short)Entero(r, 8), Color: 0, GameMaster: false);
    });

    /// <summary>Un cambio puntual: sólo el saldo, sin leer ni reescribir el resto del personaje.</summary>
    public long? AcreditarBoveda(string nombre, long monto) => Operacion(() =>
    {
        using var c = Comando("UPDATE personajes SET oro_en_banco = oro_en_banco + @m " +
                              "WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL RETURNING oro_en_banco");
        Valores(c, ("@m", monto), ("@n", nombre));
        return c.ExecuteScalar() is { } saldo and not DBNull ? Convert.ToInt64(saldo) : (long?)null;
    });

    /// <summary>La tabla de posiciones, con una consulta y en una conexión propia: la pide la página pública desde su hilo,
    /// y no puede frenar al que escribe (ni esperarlo).</summary>
    public IReadOnlyList<CharacterFile.FilaDeRanking> FilasDeRanking()
    {
        using var con = OtraConexion();
        using var c = con.CreateCommand();
        c.CommandText = "SELECT nombre, nivel, exp, clase, clan, usuarios_matados, criaturas_matadas, veces_muerto " +
                        "FROM personajes WHERE borrado_en IS NULL";
        using var r = c.ExecuteReader();
        var filas = new List<CharacterFile.FilaDeRanking>();
        while (r.Read())
            filas.Add(new CharacterFile.FilaDeRanking(r.GetString(0), (int)Entero(r, 1), r.GetDouble(2), (byte)Entero(r, 3),
                r.GetString(4), Math.Max(0, (int)Entero(r, 5)), Math.Max(0, (int)Entero(r, 6)), Math.Max(0, (int)Entero(r, 7))));
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

    /// <summary>Los clanes con sus listas, cada una en el orden en que se escribió (el de su <c>id</c>).</summary>
    public IReadOnlyList<ClanGuardado> LeerClanes() => Operacion(() =>
    {
        var clanes = new Dictionary<long, ClanArmandose>();
        var orden = new List<long>();
        using (var c = Comando("SELECT id, nombre, lider, descripcion, noticias, alineacion, url, codex, elecciones_abiertas " +
                               "FROM clanes ORDER BY id"))
        using (var r = c.ExecuteReader())
        {
            while (r.Read())
            {
                long id = Entero(r, 0);
                orden.Add(id);
                clanes[id] = new ClanArmandose(new ClanGuardado
                {
                    Nombre = r.GetString(1), Lider = r.GetString(2), Descripcion = r.GetString(3), Noticias = r.GetString(4),
                    Alineacion = (byte)Entero(r, 5), Url = r.GetString(6), Codex = r.GetString(7),
                    EleccionesAbiertas = r.GetBoolean(8),
                });
            }
        }
        Hijos("SELECT clan_id, nombre FROM clanes_miembros ORDER BY id",
            (r, clan) => clan.Miembros.Add(r.GetString(1)));
        Hijos("SELECT clan_id, otro_clan, relacion FROM clanes_relaciones ORDER BY id",
            (r, clan) => (r.GetString(2) == "aliado" ? clan.Aliados : clan.Enemigos).Add(r.GetString(1)));
        Hijos("SELECT clan_id, de_clan, tipo, texto FROM clanes_propuestas ORDER BY id",
            (r, clan) => (r.GetString(2) == "alianza" ? clan.Alianza : clan.Paz).Add(new(r.GetString(1), r.GetString(3))));
        Hijos("SELECT clan_id, nombre, solicitud FROM clanes_aspirantes ORDER BY id",
            (r, clan) => clan.Aspirantes.Add(new(r.GetString(1), r.GetString(2))));
        Hijos("SELECT clan_id, votante, candidato FROM clanes_votos ORDER BY id",
            (r, clan) => clan.Votos.Add(new(r.GetString(1), r.GetString(2))));
        return (IReadOnlyList<ClanGuardado>)orden.Select(id => clanes[id]).Select(c => c.Datos with
        {
            Miembros = c.Miembros.ToImmutableArray(), Aliados = c.Aliados.ToImmutableArray(),
            Enemigos = c.Enemigos.ToImmutableArray(), PropuestasDeAlianza = c.Alianza.ToImmutableArray(),
            PropuestasDePaz = c.Paz.ToImmutableArray(), Aspirantes = c.Aspirantes.ToImmutableArray(),
            Votos = c.Votos.ToImmutableArray(),
        }).ToList();

        void Hijos(string sql, Action<DbDataReader, ClanArmandose> fila)
        {
            using var c = Comando(sql);
            using var r = c.ExecuteReader();
            while (r.Read())
                if (clanes.TryGetValue(Entero(r, 0), out var clan)) fila(r, clan);
        }
    });

    public void GuardarClanes(IReadOnlyList<ClanGuardado> clanes) => EnTransaccion(tx => EscribirClanes(clanes, tx));

    /// <summary>Se reescriben TODOS: el que no viene, se cerró. Son pocos, y la cascada se lleva los hijos del borrado.</summary>
    private void EscribirClanes(IReadOnlyList<ClanGuardado> clanes, DbTransaction tx)
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
            long id = Convert.ToInt64(Valores(clan, ("@n", g.Nombre), ("@l", g.Lider), ("@d", g.Descripcion), ("@no", g.Noticias),
                ("@a", g.Alineacion), ("@u", g.Url), ("@c", g.Codex), ("@e", g.EleccionesAbiertas)).ExecuteScalar());
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
        => Leer("SELECT personaje, motivo FROM rechazos_de_clan ORDER BY id", r => new KeyValuePair<string, string>(r.GetString(0), r.GetString(1)));

    public void GuardarRechazosDeClan(IReadOnlyList<KeyValuePair<string, string>> rechazos) => EnTransaccion(tx => EscribirRechazos(rechazos, tx));

    private void EscribirRechazos(IReadOnlyList<KeyValuePair<string, string>> rechazos, DbTransaction tx)
    {
        using (var c = Comando("DELETE FROM rechazos_de_clan", tx)) c.ExecuteNonQuery();
        using var fila = Comando("INSERT INTO rechazos_de_clan (personaje, motivo) VALUES (@p, @m)", tx);
        foreach (var (personaje, motivo) in rechazos) Valores(fila, ("@p", personaje), ("@m", motivo)).ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> LeerCorreo()
    {
        var casillas = new Dictionary<string, List<MailEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (destinatario, carta) in Leer("SELECT destinatario, remitente, mensaje, objeto, cantidad, leida FROM correo ORDER BY id",
                     r => (r.GetString(0), new MailEntry(r.GetString(1), r.GetString(2), (short)Entero(r, 3), (short)Entero(r, 4),
                         (byte)Entero(r, 5)))))
        {
            if (!casillas.TryGetValue(destinatario, out var cartas)) casillas[destinatario] = cartas = new List<MailEntry>();
            cartas.Add(carta);
        }
        return casillas.ToDictionary(c => c.Key, c => (IReadOnlyList<MailEntry>)c.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sólo las casillas que vienen: se borran sus cartas y se escriben las de ahora. Las demás no se tocan.</summary>
    public void GuardarCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas) => EnTransaccion(tx => EscribirCasillas(casillas, tx));

    private void EscribirCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas, DbTransaction tx)
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
            r => new ObjetoEnElSuelo(new WorldPos((short)Entero(r, 0), (byte)Entero(r, 1), (byte)Entero(r, 2)),
                (short)Entero(r, 3), (short)Entero(r, 4)));

    public void GuardarSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo) => EnTransaccion(tx => EscribirSuelo(suelo, tx));

    private void EscribirSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo, DbTransaction tx)
    {
        using (var c = Comando("DELETE FROM suelo", tx)) c.ExecuteNonQuery();
        using var fila = Comando("INSERT INTO suelo (mapa, x, y, objeto, cantidad) VALUES (@m, @x, @y, @o, @c)", tx);
        foreach (var o in suelo)
            Valores(fila, ("@m", o.Posicion.Map), ("@x", o.Posicion.X), ("@y", o.Posicion.Y), ("@o", o.Objeto), ("@c", o.Cantidad))
                .ExecuteNonQuery();
    }

    public IReadOnlyList<LlaveVendida> LeerLlavesVendidas()
        => Leer("SELECT mercader, ranura, objeto FROM llaves_vendidas ORDER BY mercader, ranura",
            r => new LlaveVendida((int)Entero(r, 0), (int)Entero(r, 1), (short)Entero(r, 2)));

    public void GuardarLlavesVendidas(IReadOnlyList<LlaveVendida> llaves) => EnTransaccion(tx => EscribirLlaves(llaves, tx));

    private void EscribirLlaves(IReadOnlyList<LlaveVendida> llaves, DbTransaction tx)
    {
        using (var c = Comando("DELETE FROM llaves_vendidas", tx)) c.ExecuteNonQuery();
        using var fila = Comando("INSERT INTO llaves_vendidas (mercader, ranura, objeto) VALUES (@m, @r, @o)", tx);
        foreach (var l in llaves) Valores(fila, ("@m", l.Mercader), ("@r", l.Ranura), ("@o", l.Objeto)).ExecuteNonQuery();
    }

    public int LeerRecord() => Leer("SELECT record_usuarios FROM mundo WHERE id = 1", r => (int)Entero(r, 0)).FirstOrDefault();

    public void GuardarRecord(int record) => EnTransaccion(tx => EscribirRecord(record, tx));

    private void EscribirRecord(int record, DbTransaction tx)
    {
        using var c = Comando("UPDATE mundo SET record_usuarios = @r WHERE id = 1", tx);
        Valores(c, ("@r", record)).ExecuteNonQuery();
    }

    /// <summary>
    /// LA VERSIÓN 2 PASA EL MUNDO A TABLAS. Lo que la 1 guardaba como JSON en <c>mundo_json</c> —el mismo formato de los
    /// archivos de <c>Misc/</c>— se lee con <see cref="JsonDelMundo"/> y se escribe en sus tablas, en la misma transacción que
    /// las crea, y la tabla vieja se va. Una base nueva pasa por acá con <c>mundo_json</c> vacía.
    /// </summary>
    private void PasarElMundoATablas(DbTransaction tx)
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

    private static void ParametrosDeFicha(DbCommand c, FichaDePersonaje ficha)
    {
        for (int i = 0; i < Columnas.Length; i++) Parametro(c, "@c" + i, Columnas[i].Valor(ficha));
    }

    private static FichaDePersonaje FichaDeLaFila(DbDataReader r)
    {
        long L(string columna) => Entero(r, r.GetOrdinal(columna));
        bool B(string columna) => r.GetBoolean(r.GetOrdinal(columna));
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
            Baneado = B("baneado"),
            Muerto = B("muerto"),
            Navegando = B("navegando"),
            Montando = B("montando"),
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
            Donador = B("donador"),
            CreditosDonador = L("creditos_donador"),
            RanuraDelBarco = (byte)L("ranura_del_barco"),
        };
    }

    /// <summary>Las ranuras se reescriben enteras: se borran las del personaje y se insertan las ocupadas. Una ranura con
    /// objeto 0 es una ranura vacía, y vacía no tiene fila.</summary>
    private void EscribirRanuras(long id, FichaDePersonaje ficha, DbTransaction tx)
    {
        using (var c = Comando("DELETE FROM inventario WHERE personaje_id = @id; DELETE FROM boveda WHERE personaje_id = @id; " +
                               "DELETE FROM hechizos WHERE personaje_id = @id; DELETE FROM skills WHERE personaje_id = @id; " +
                               "DELETE FROM amigos WHERE personaje_id = @id; DELETE FROM penas WHERE personaje_id = @id; " +
                               "DELETE FROM familiares WHERE personaje_id = @id;", tx))
        {
            Valores(c, ("@id", id));
            c.ExecuteNonQuery();
        }

        using (var c = Comando("INSERT INTO inventario (personaje_id, ranura, objeto, cantidad, equipado) VALUES (@id, @r, @o, @n, @e)", tx))
        {
            var (r, o, n, e) = (Param(c, "@r"), Param(c, "@o"), Param(c, "@n"), Param(c, "@e"));
            Parametro(c, "@id", id);
            for (int i = 1; i < ficha.Inventario.Length && i <= GameLimits.MaxInventorySlots; i++)
            {
                var s = ficha.Inventario[i];
                if (s.Vacia) continue;
                r.Value = (short)i; o.Value = s.Objeto; n.Value = s.Cantidad; e.Value = s.Equipado;
                c.ExecuteNonQuery();
            }
        }
        using (var c = Comando("INSERT INTO boveda (personaje_id, ranura, objeto, cantidad) VALUES (@id, @r, @o, @n)", tx))
        {
            var (r, o, n) = (Param(c, "@r"), Param(c, "@o"), Param(c, "@n"));
            Parametro(c, "@id", id);
            for (int i = 1; i < ficha.Boveda.Length && i <= GameLimits.MaxBankSlots; i++)
            {
                var s = ficha.Boveda[i];
                if (s.Vacia) continue;
                r.Value = (short)i; o.Value = s.Objeto; n.Value = s.Cantidad;
                c.ExecuteNonQuery();
            }
        }
        using (var c = Comando("INSERT INTO hechizos (personaje_id, ranura, hechizo) VALUES (@id, @r, @h)", tx))
        {
            var (r, h) = (Param(c, "@r"), Param(c, "@h"));
            Parametro(c, "@id", id);
            for (int i = 1; i < ficha.Hechizos.Length && i <= GameLimits.MaxUserSpells; i++)
            {
                if (ficha.Hechizos[i] == 0) continue;
                r.Value = (short)i; h.Value = ficha.Hechizos[i];
                c.ExecuteNonQuery();
            }
        }
        using (var c = Comando("INSERT INTO skills (personaje_id, skill, valor) VALUES (@id, @s, @v)", tx))
        {
            var (s, v) = (Param(c, "@s"), Param(c, "@v"));
            Parametro(c, "@id", id);
            for (int i = 1; i <= GameLimits.NumSkills; i++)
            {
                s.Value = (short)i; v.Value = i < ficha.Skills.Length ? ficha.Skills[i] : (byte)0;
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
            Valores(c, ("@id", id), ("@tipo", f.Tipo), ("@nombre", f.Nombre), ("@nivel", f.Nivel), ("@exp", f.Exp), ("@elu", f.Elu),
                ("@vida", f.Vida), ("@vidamax", f.VidaMaxima), ("@gmin", f.GolpeMinimo), ("@gmax", f.GolpeMaximo),
                ("@muerto", f.Muerto));
            for (int i = 0; i < Familiar.CantidadDeHabilidades; i++)
                Parametro(c, "@h" + (i + 1), i < f.Habilidades.Length ? f.Habilidades[i] : 0);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Los amigos y las penas: en orden, sin los renglones en blanco (que el cargador tampoco levanta).</summary>
    private void EscribirLista(string tabla, string columna, long id, ImmutableArray<string> valores, DbTransaction tx)
    {
        using var c = Comando($"INSERT INTO {tabla} (personaje_id, orden, {columna}) VALUES (@id, @o, @v)", tx);
        var (o, v) = (Param(c, "@o"), Param(c, "@v"));
        Parametro(c, "@id", id);
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
                     r => ((int)Entero(r, 0), (short)Entero(r, 1), (short)Entero(r, 2), r.GetBoolean(3))))
            inventario[ranura] = new RanuraGuardada(objeto, cantidad, equipado);

        var boveda = new RanuraGuardada[GameLimits.MaxBankSlots + 1];
        foreach (var (ranura, objeto, cantidad) in Filas(id, "SELECT ranura, objeto, cantidad FROM boveda WHERE personaje_id = @id",
                     r => ((int)Entero(r, 0), (short)Entero(r, 1), (short)Entero(r, 2))))
            boveda[ranura] = new RanuraGuardada(objeto, cantidad, false);

        var hechizos = new short[GameLimits.MaxUserSpells + 1];
        foreach (var (ranura, hechizo) in Filas(id, "SELECT ranura, hechizo FROM hechizos WHERE personaje_id = @id",
                     r => ((int)Entero(r, 0), (short)Entero(r, 1))))
            hechizos[ranura] = hechizo;

        var skills = new byte[GameLimits.NumSkills + 1];
        foreach (var (skill, valor) in Filas(id, "SELECT skill, valor FROM skills WHERE personaje_id = @id",
                     r => ((int)Entero(r, 0), (byte)Entero(r, 1))))
            skills[skill] = valor;

        var amigos = Filas(id, "SELECT nombre FROM amigos WHERE personaje_id = @id ORDER BY orden", r => r.GetString(0));
        var penas = Filas(id, "SELECT texto FROM penas WHERE personaje_id = @id ORDER BY orden", r => r.GetString(0));

        var familiar = Filas(id,
            "SELECT tipo, nombre, nivel, exp, exp_para_subir, vida, vida_max, golpe_min, golpe_max, muerto, " +
            "habilidad_1, habilidad_2, habilidad_3, habilidad_4 FROM familiares WHERE personaje_id = @id",
            r => new FichaDeFamiliar((byte)Entero(r, 0), r.GetString(1), (byte)Entero(r, 2), Entero(r, 3), Entero(r, 4),
                Entero(r, 5), Entero(r, 6), Entero(r, 7), Entero(r, 8), r.GetBoolean(9),
                ImmutableArray.Create((int)Entero(r, 10), (int)Entero(r, 11), (int)Entero(r, 12), (int)Entero(r, 13))))
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

    /// <summary>Una operación de la interfaz: de a una, con la conexión lista.</summary>
    private T Operacion<T>(Func<T> operacion)
    {
        lock (DeAUno)
        {
            Preparar();
            return operacion();
        }
    }

    private void Operacion(Action operacion)
    {
        lock (DeAUno)
        {
            Preparar();
            operacion();
        }
    }

    /// <summary>Un comando en la conexión que escribe. Sin transacción dada va en la grande, si hay una: SQLite no deja
    /// correr un comando fuera de la transacción abierta de su conexión.</summary>
    protected DbCommand Comando(string sql, DbTransaction? tx = null)
    {
        var c = Conexion.CreateCommand();
        c.CommandText = sql;
        c.Transaction = tx ?? _grande;
        return c;
    }

    protected void Ejecutar(string sql)
    {
        using var c = Comando(sql);
        c.ExecuteNonQuery();
    }

    protected object? Escalar(string sql)
    {
        using var c = Comando(sql);
        return c.ExecuteScalar();
    }

    /// <summary>Un entero de la fila, del tamaño que lo haya guardado el motor (SQLite los da todos de 64 bits; Postgres,
    /// del tamaño de la columna).</summary>
    protected static long Entero(DbDataReader r, int columna) => Convert.ToInt64(r.GetValue(columna), CultureInfo.InvariantCulture);

    /// <summary>Un parámetro sin tipo: el motor lo toma del valor, en cada ejecución.</summary>
    private static DbParameter Param(DbCommand c, string nombre)
    {
        var p = c.CreateParameter();
        p.ParameterName = nombre;
        c.Parameters.Add(p);
        return p;
    }

    private static void Parametro(DbCommand c, string nombre, object? valor) => Param(c, nombre).Value = valor ?? DBNull.Value;

    /// <summary>Carga los parámetros de un comando —el que se usa muchas veces, también— y lo devuelve, listo para ejecutar.</summary>
    private static DbCommand Valores(DbCommand c, params (string Nombre, object? Valor)[] valores)
    {
        c.Parameters.Clear();
        foreach (var (nombre, valor) in valores) Parametro(c, nombre, valor);
        return c;
    }

    private void EnTransaccion(Action<DbTransaction> escritura) => Operacion(() =>
    {
        using var tramo = new Tramo(Conexion, _grande);
        escritura(tramo.Tx);
        tramo.Confirmar();
    });

    private List<T> Leer<T>(string sql, Func<DbDataReader, T> fila) => Operacion(() =>
    {
        using var c = Comando(sql);
        using var r = c.ExecuteReader();
        var filas = new List<T>();
        while (r.Read()) filas.Add(fila(r));
        return filas;
    });

    private List<T> Filas<T>(long id, string sql, Func<DbDataReader, T> fila)
    {
        using var c = Comando(sql);
        Valores(c, ("@id", id));
        using var r = c.ExecuteReader();
        var filas = new List<T>();
        while (r.Read()) filas.Add(fila(r));
        return filas;
    }

    private List<string> Nombres(string sql) => Leer(sql, r => r.GetString(0));

    private long? IdDeCuenta(string nombre, DbTransaction? tx)
    {
        using var c = Comando("SELECT id FROM cuentas WHERE upper(nombre) = upper(@n)", tx);
        Valores(c, ("@n", nombre));
        return c.ExecuteScalar() is { } id and not DBNull ? Convert.ToInt64(id) : null;
    }

    private long? IdDePersonaje(string nombre, DbTransaction? tx)
    {
        using var c = Comando("SELECT id FROM personajes WHERE upper(nombre) = upper(@n) AND borrado_en IS NULL", tx);
        Valores(c, ("@n", nombre));
        return c.ExecuteScalar() is { } id and not DBNull ? Convert.ToInt64(id) : null;
    }

    private object Ahora() => ValorDeFecha(DateTime.UtcNow);
}
