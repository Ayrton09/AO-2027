using System.Collections.Immutable;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Lo de la base SQLite que la batería de contrato (<see cref="ContratoDelAlmacen"/>) no le puede pedir a los archivos: las
/// versiones del esquema, las claves foráneas, el nombre como lo escribió el jugador, el backup que es una base.
/// </summary>
public class AlmacenSqliteTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_sqlite_" + Guid.NewGuid().ToString("N"));
    private string Archivo => Path.Combine(_raiz, "coverao.db");

    public void Dispose() { try { Directory.Delete(_raiz, recursive: true); } catch { } }

    private static Account Cuenta(string nombre) => new() { Name = nombre, PasswordHash = "pbkdf2$1$a$b" };

    private static long Escalar(string archivo, string sql)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = archivo, Pooling = false }.ToString());
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = sql;
        return (long)c.ExecuteScalar()!;
    }

    [Fact]
    public void Lo_guardado_sobrevive_a_cerrar_y_volver_a_abrir()
    {
        var ficha = FichasDePrueba.Llena("Duradero");
        using (var almacen = new AlmacenSqlite(Archivo))
        {
            var cuenta = Cuenta("Duena");
            almacen.GuardarCuenta(cuenta);
            Assert.True(almacen.CrearPersonaje(cuenta, ficha));
        }
        using (var almacen = new AlmacenSqlite(Archivo))
        {
            FichasDePrueba.AssertIgual(ficha, almacen.LeerPersonaje("Duradero")!);
            Assert.Equal(new[] { "Duradero" }, almacen.LeerCuenta("Duena")!.Characters);
        }
    }

    /// <summary>El nombre vuelve como lo escribió el jugador, lo pida quien lo pida. El <c>.chr</c> no lo guardaba —el archivo
    /// va en mayúsculas— y había que ir a buscarlo a la lista de la cuenta.</summary>
    [Fact]
    public void El_nombre_vuelve_como_se_escribio()
    {
        using var almacen = AlmacenSqlite.EnMemoria();
        var cuenta = Cuenta("MiCuenta");
        almacen.GuardarCuenta(cuenta);
        Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Rugura")));

        Assert.Equal("Rugura", almacen.LeerPersonaje("RUGURA")!.Nombre);
        Assert.Equal("Rugura", almacen.LeerResumen("rugura")!.Name);
        Assert.Equal("MiCuenta", almacen.LeerCuenta("micuenta")!.Name);
        Assert.Equal("Rugura", Assert.Single(almacen.FilasDeRanking()).Nombre);
    }

    [Fact]
    public void Un_nombre_que_ya_existe_no_entra_dos_veces()
    {
        using var almacen = AlmacenSqlite.EnMemoria();
        var cuenta = Cuenta("Duena");
        almacen.GuardarCuenta(cuenta);
        Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Unico")));

        Assert.ThrowsAny<SqliteException>(() => almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("UNICO")));
        Assert.Single(almacen.LeerCuenta("Duena")!.Characters);
    }

    /// <summary>Guardar a uno que no existe es un error, no un alta: no se sabría de qué cuenta es.</summary>
    [Fact]
    public void Guardar_a_un_personaje_que_no_existe_es_un_error()
    {
        using var almacen = AlmacenSqlite.EnMemoria();
        Assert.Throws<InvalidOperationException>(() => almacen.GuardarPersonaje(FichasDePrueba.Llena("Fantasma")));
        Assert.False(almacen.ExistePersonaje("Fantasma"));
    }

    /// <summary>El personaje apartado sale de la lista de su cuenta, y la fila queda.</summary>
    [Fact]
    public void Apartar_saca_al_personaje_de_su_cuenta_y_deja_la_fila()
    {
        using (var almacen = new AlmacenSqlite(Archivo))
        {
            var cuenta = Cuenta("Duena");
            almacen.GuardarCuenta(cuenta);
            Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Uno")));
            Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Dos")));
            Assert.True(almacen.ApartarPersonaje("Uno"));
            Assert.Equal(new[] { "Dos" }, almacen.LeerCuenta("Duena")!.Characters);
        }
        Assert.Equal(1, Escalar(Archivo, "SELECT count(*) FROM personajes WHERE nombre = 'Uno' AND borrado_en IS NOT NULL"));
    }

    [Fact]
    public void Las_claves_foraneas_estan_prendidas_en_la_conexion_que_escribe()
    {
        using var almacen = new AlmacenSqlite(Archivo);
        Assert.True(almacen.ClavesForaneasPrendidas());
    }

    [Fact]
    public void La_version_del_esquema_queda_anotada()
    {
        int ultima = AlmacenSqlite.Versiones()[^1].Version;
        using (new AlmacenSqlite(Archivo)) { }
        Assert.Equal(ultima, Escalar(Archivo, "SELECT max(version) FROM migraciones"));
        using (new AlmacenSqlite(Archivo)) { }   // abrirla de nuevo no vuelve a aplicar nada
        Assert.Equal(ultima, Escalar(Archivo, "SELECT count(*) FROM migraciones"));
    }

    /// <summary>
    /// UNA BASE DE LA VERSIÓN 1 PASA SU MUNDO A TABLAS al abrirla. La 1 guardaba los clanes, el correo, el suelo, las llaves,
    /// los rechazos y el récord como el JSON de <c>Misc/</c> en <c>mundo_json</c>; la 2 los pasa a sus tablas en la misma
    /// transacción que las crea. Si esto se rompe, el que actualiza pierde el mundo.
    /// </summary>
    [Fact]
    public void Una_base_de_la_version_1_pasa_su_mundo_a_tablas()
    {
        var clanes = new[] { ContratoDelAlmacen.ClanLleno("Sur", 1), ContratoDelAlmacen.ClanLleno("Norte", 2) };
        var correo = new Dictionary<string, IReadOnlyList<MailEntry>>
        {
            ["Aldo"] = new[] { new MailEntry("Bruma", "hola", 2, 1), new MailEntry("Ciro", "chau", 0, 0, 1) },
        };
        var suelo = new[] { new ObjetoEnElSuelo(new WorldPos(1, 50, 50), 12, 3) };
        var llaves = new[] { new LlaveVendida(40, 3, 53) };
        var rechazos = new[] { new KeyValuePair<string, string>("Dana", "no") };

        // La base como la dejaba la versión 1: su esquema y nada más, con el mundo en JSON.
        Directory.CreateDirectory(_raiz);
        using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Archivo, Pooling = false }.ToString()))
        {
            con.Open();
            void Sql(string sql, params (string, object)[] ps)
            {
                using var c = con.CreateCommand();
                c.CommandText = sql;
                foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
                c.ExecuteNonQuery();
            }
            Sql("CREATE TABLE migraciones (version INTEGER PRIMARY KEY, descripcion TEXT NOT NULL, aplicada_en TEXT NOT NULL) STRICT;");
            Sql(AlmacenSqlite.Versiones().Single(v => v.Version == 1).Sql);
            Sql("INSERT INTO migraciones VALUES (1, 'inicial', '2026-09-28')");
            foreach (var (clave, texto) in new[]
                     {
                         ("guilds", JsonDelMundo.Clanes(clanes)), ("mailboxes", JsonDelMundo.Correo(correo)),
                         ("suelo", JsonDelMundo.Suelo(suelo)), ("propiedades", JsonDelMundo.Llaves(llaves)),
                         ("rechazos-de-clan", JsonDelMundo.Rechazos(rechazos)), ("record", JsonDelMundo.Record(9)),
                     })
                Sql("INSERT INTO mundo_json (clave, texto) VALUES (@k, @t)", ("@k", clave), ("@t", texto));
        }

        using (var almacen = new AlmacenSqlite(Archivo))
        {
            var leidos = almacen.LeerClanes();
            Assert.Equal(2, leidos.Count);
            ContratoDelAlmacen.AssertIgual(clanes[0], leidos[0]);
            ContratoDelAlmacen.AssertIgual(clanes[1], leidos[1]);
            Assert.Equal(correo["Aldo"], almacen.LeerCorreo()["Aldo"]);
            Assert.Equal(suelo, almacen.LeerSuelo());
            Assert.Equal(llaves, almacen.LeerLlavesVendidas());
            Assert.Equal(rechazos, almacen.LeerRechazosDeClan());
            Assert.Equal(9, almacen.LeerRecord());
        }
        Assert.Equal(0, Escalar(Archivo, "SELECT count(*) FROM sqlite_master WHERE name = 'mundo_json'"));
        Assert.Equal(2, Escalar(Archivo, "SELECT max(version) FROM migraciones"));
    }

    /// <summary>Una base con un esquema MÁS NUEVO que el del servidor no se abre: escribirle con el esquema viejo la rompería.</summary>
    [Fact]
    public void Una_base_de_un_servidor_mas_nuevo_no_se_abre()
    {
        using (new AlmacenSqlite(Archivo)) { }
        Escalar(Archivo, "INSERT INTO migraciones (version, descripcion, aplicada_en) VALUES (999, 'del futuro', 'x') RETURNING version");

        var error = Assert.Throws<InvalidOperationException>(() => new AlmacenSqlite(Archivo));
        Assert.Contains("999", error.Message);
    }

    /// <summary>La <c>coverao.db</c> del SQLite viejo tiene el mismo nombre de archivo: no se le crean tablas al lado.</summary>
    [Fact]
    public void El_sqlite_viejo_no_se_abre_como_si_fuera_la_base_nueva()
    {
        using (var viejo = new SqliteStore(Archivo)) viejo.WriteCharacter("Rugura", "[INIT]\nGenero=1\n");
        SqliteConnection.ClearAllPools();

        Assert.Throws<InvalidOperationException>(() => new AlmacenSqlite(Archivo));
        Assert.Equal(0, Escalar(Archivo, "SELECT count(*) FROM sqlite_master WHERE name = 'personajes'"));
    }

    [Fact]
    public void El_backup_es_una_base_que_se_abre()
    {
        var ficha = FichasDePrueba.Llena("Respaldado");
        string ruta;
        using (var almacen = new AlmacenSqlite(Archivo))
        {
            var cuenta = Cuenta("Duena");
            almacen.GuardarCuenta(cuenta);
            Assert.True(almacen.CrearPersonaje(cuenta, ficha));
            ruta = almacen.Backup(Path.Combine(_raiz, "backups"), "coverao-prueba");
        }
        Assert.EndsWith(".db", ruta);
        using var restaurada = new AlmacenSqlite(ruta);
        FichasDePrueba.AssertIgual(ficha, restaurada.LeerPersonaje("Respaldado")!);
    }

    /// <summary>El backup copia además los archivos SUELTOS de <c>data/</c> —la lista de IPs baneadas—, pero no la base
    /// viva: su copia es el <c>.db</c> consistente, y el archivo vivo y su diario serían una base a medio escribir.</summary>
    [Fact]
    public void El_backup_no_se_lleva_la_base_viva_como_archivo_suelto()
    {
        string data = Path.Combine(_raiz, "data");
        using var almacen = new AlmacenSqlite(Path.Combine(data, "coverao.db"));
        File.WriteAllText(Path.Combine(data, "banned-ips.txt"), "1.2.3.4");
        var cuando = new DateTime(2026, 9, 28, 13, 0, 0);

        string ruta = Backups.Crear(almacen, Path.Combine(data, "backups"), cuando, data);

        string sueltos = Path.Combine(data, "backups", Backups.Stem(cuando));
        Assert.True(File.Exists(ruta));
        Assert.True(File.Exists(Path.Combine(sueltos, "banned-ips.txt")));
        Assert.False(File.Exists(Path.Combine(sueltos, "coverao.db")));
        Assert.False(File.Exists(Path.Combine(sueltos, "coverao.db-wal")));
    }

    /// <summary>La tabla de posiciones lee con su propia conexión mientras la que escribe está en una transacción.</summary>
    [Fact]
    public void La_tabla_de_posiciones_no_espera_al_que_escribe()
    {
        using var almacen = new AlmacenSqlite(Archivo);
        var cuenta = Cuenta("Duena");
        almacen.GuardarCuenta(cuenta);
        Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Lector")));

        using var otra = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Archivo, Pooling = false }.ToString());
        otra.Open();
        using var tx = otra.BeginTransaction();
        using (var c = otra.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "UPDATE personajes SET nivel = 99";
            c.ExecuteNonQuery();
        }
        // Con la escritura abierta y sin confirmar, la lectura ve lo de antes y no se queda esperando.
        var fila = Assert.Single(almacen.FilasDeRanking());
        Assert.NotEqual(99, fila.Nivel);
        tx.Rollback();
    }
}

/// <summary>Qué almacén abre el servidor según <c>[BASEDEDATOS]</c> y lo que ya hay en <c>data/</c>.</summary>
public class AlmacenesTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_almacenes_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_raiz, recursive: true); } catch { } }

    private static ServerIni Ini(string texto) => ServerIni.Desde(IniFile.Parse(texto));

    private IAlmacen Abrir(string ini)
    {
        var (almacen, _) = Almacenes.Abrir(Ini(ini), _raiz);
        return almacen;
    }

    [Fact]
    public void Una_instalacion_nueva_arranca_en_sqlite()
    {
        var almacen = Abrir("");
        try { Assert.IsType<AlmacenSqlite>(almacen); }
        finally { (almacen as IDisposable)?.Dispose(); }
        Assert.True(File.Exists(Path.Combine(_raiz, "coverao.db")));
    }

    /// <summary>Un mundo que ya está en archivos no puede desaparecer porque cambió el motor por defecto.</summary>
    [Fact]
    public void Con_personajes_en_archivos_y_sin_base_sigue_con_los_archivos()
    {
        Directory.CreateDirectory(Path.Combine(_raiz, "Charfile"));
        File.WriteAllText(Path.Combine(_raiz, "Charfile", "RUGURA.chr"), "[INIT]\r\nGenero=1\r\n");

        Assert.IsType<AlmacenDeTexto>(Abrir(""));
        Assert.False(File.Exists(Path.Combine(_raiz, "coverao.db")));
    }

    [Fact]
    public void El_tipo_del_ini_manda()
    {
        Directory.CreateDirectory(Path.Combine(_raiz, "Charfile"));
        File.WriteAllText(Path.Combine(_raiz, "Charfile", "RUGURA.chr"), "[INIT]\r\nGenero=1\r\n");

        var sqlite = Abrir("[BASEDEDATOS]\r\nTipo=SQLite\r\nArchivo=otra.db\r\n");
        try { Assert.IsType<AlmacenSqlite>(sqlite); }
        finally { (sqlite as IDisposable)?.Dispose(); }
        Assert.True(File.Exists(Path.Combine(_raiz, "otra.db")));

        Assert.IsType<AlmacenDeTexto>(Abrir("[BASEDEDATOS]\r\nTipo=archivos\r\n"));
    }

    [Fact]
    public void Un_tipo_que_no_existe_no_arranca()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Abrir("[BASEDEDATOS]\r\nTipo=mysql\r\n"));
        Assert.Contains("mysql", error.Message);
    }
}
