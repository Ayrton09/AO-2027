using System.IO.Compression;
using CoverAO.Protocol.Tests;
using CoverAO.Server.Data;
using Npgsql;

namespace CoverAO.Postgres.Tests;

/// <summary>Lo propio de Postgres, que la batería de contrato no mira: el esquema, la conexión que se corta, el backup, el todo o
/// nada de importar y el paso de SQLite a Postgres.</summary>
public sealed class AlmacenPostgresTests : IDisposable
{
    private readonly BaseDePrueba _base = new();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_pg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _base.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }

    private static Account Cuenta(string nombre) => new() { Name = nombre, PasswordHash = "pbkdf2$1$a$b", Salt = "" };

    private long Escalar(string sql)
    {
        using var con = new NpgsqlConnection(_base.Cadena);
        con.Open();
        using var c = new NpgsqlCommand(sql, con);
        return Convert.ToInt64(c.ExecuteScalar());
    }

    [Fact]
    public void La_version_del_esquema_queda_anotada_y_abrirla_de_nuevo_no_repite_nada()
    {
        int ultima = AlmacenRelacional.Versiones("postgres")[^1].Version;
        using (new AlmacenPostgres(_base.Cadena)) { }
        Assert.Equal(ultima, Escalar("SELECT max(version) FROM migraciones"));
        using (new AlmacenPostgres(_base.Cadena)) { }
        Assert.Equal(ultima, Escalar("SELECT count(*) FROM migraciones"));
    }

    [Fact]
    public void Una_base_de_un_servidor_mas_nuevo_no_se_abre()
    {
        using (new AlmacenPostgres(_base.Cadena)) { }
        Escalar("INSERT INTO migraciones (version, descripcion, aplicada_en) VALUES (999, 'del futuro', now()) RETURNING version");

        var error = Assert.Throws<InvalidOperationException>(() => new AlmacenPostgres(_base.Cadena));
        Assert.Contains("999", error.Message);
    }

    /// <summary>Postgres se reinicia —o se corta la red— con el servidor andando: la operación que encuentra la conexión
    /// cortada puede fallar, y la siguiente la reabre y anda.</summary>
    [Fact]
    public void Si_se_corta_la_conexion_la_siguiente_operacion_la_reabre()
    {
        using var almacen = new AlmacenPostgres(_base.Cadena);
        var cuenta = Cuenta("Duena");
        almacen.GuardarCuenta(cuenta);
        Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Resiste")));

        PostgresDePrueba.CortarConexiones(_base.Nombre);

        try { almacen.ExisteCuenta("Duena"); }
        catch (Exception) { /* la que encuentra la conexión cortada puede fallar */ }
        Assert.True(almacen.ExisteCuenta("Duena"));
        FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Resiste"), almacen.LeerPersonaje("Resiste")!);
    }

    [Fact]
    public void El_backup_es_un_zip_con_cada_tabla_y_el_manifiesto()
    {
        using var almacen = new AlmacenPostgres(_base.Cadena);
        var cuenta = Cuenta("Duena");
        almacen.GuardarCuenta(cuenta);
        Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Respaldado") with { Descripcion = "dos\nrenglones" }));

        string ruta = almacen.Backup(_raiz, "coverao-prueba");

        Assert.EndsWith(".zip", ruta);
        Assert.False(File.Exists(ruta + ".escribiendo"));
        using var zip = ZipFile.OpenRead(ruta);
        var entradas = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var tabla in new[] { "cuentas.csv", "personajes.csv", "inventario.csv", "skills.csv", "clanes.csv", "correo.csv", "migraciones.csv" })
            Assert.Contains(tabla, entradas);
        using var manifiesto = new StreamReader(zip.GetEntry("manifiesto.txt")!.Open());
        string texto = manifiesto.ReadToEnd();
        Assert.Contains($"esquema={AlmacenRelacional.Versiones("postgres")[^1].Version}", texto);
        Assert.Contains("tabla=personajes filas=1", texto);
        Assert.Contains("tabla=skills filas=27", texto);
        // El salto de línea de adentro de un texto llega tal cual, entre comillas.
        using var personajes = new StreamReader(zip.GetEntry("personajes.csv")!.Open());
        Assert.Contains("\"dos\nrenglones\"", personajes.ReadToEnd());
    }

    /// <summary>EL TODO O NADA DE IMPORTAR A POSTGRES: lo escrito adentro de la transacción grande entra sólo si la
    /// verificación dice que sí; si dice que no, o si algo falla a la mitad, la base queda como estaba.</summary>
    [Fact]
    public void Lo_de_una_sola_transaccion_entra_entero_o_no_entra()
    {
        using var almacen = new AlmacenPostgres(_base.Cadena);

        almacen.EnUnaSolaTransaccion(() =>
        {
            almacen.GuardarCuenta(Cuenta("Rechazada"));
            Assert.True(almacen.ExisteCuenta("Rechazada"));   // adentro se ve
            return 0;
        }, _ => false);
        Assert.False(almacen.ExisteCuenta("Rechazada"));

        Assert.Throws<InvalidOperationException>(() => almacen.EnUnaSolaTransaccion<int>(() =>
        {
            almacen.GuardarCuenta(Cuenta("Rota"));
            throw new InvalidOperationException("a la mitad");
        }, _ => true));
        Assert.False(almacen.ExisteCuenta("Rota"));

        almacen.EnUnaSolaTransaccion(() =>
        {
            var cuenta = Cuenta("Entra");
            almacen.GuardarCuenta(cuenta);
            // Un alta que no entra se deshace sola, sin llevarse lo demás.
            for (int i = 0; i < AccountFile.MaxCharacters; i++) Assert.True(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Pj" + (char)('A' + i))));
            Assert.False(almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Sobrante")));
            return 0;
        }, _ => true);
        Assert.Equal(AccountFile.MaxCharacters, almacen.LeerCuenta("Entra")!.Characters.Count);
        Assert.False(almacen.ExistePersonaje("Sobrante"));
    }

    /// <summary>Importar a una base con cuentas pide <c>--reemplazar</c>; con eso, lo que había queda en un backup y la base
    /// queda con lo importado, y nada más.</summary>
    [Fact]
    public void Importar_a_una_base_con_cuentas_pide_reemplazar_y_deja_un_backup()
    {
        string origen = Path.Combine(_raiz, "origen.db");
        using (var sqlite = new AlmacenSqlite(origen))
        {
            var cuenta = Cuenta("Nueva");
            sqlite.GuardarCuenta(cuenta);
            Assert.True(sqlite.CrearPersonaje(cuenta, FichasDePrueba.Llena("Importado")));
        }
        using var almacen = new AlmacenPostgres(_base.Cadena);
        var vieja = Cuenta("Vieja");
        almacen.GuardarCuenta(vieja);
        Assert.True(almacen.CrearPersonaje(vieja, FichasDePrueba.Llena("DeAntes")));
        string backups = Path.Combine(_raiz, "backups");

        Assert.Throws<InvalidOperationException>(() => Importador.ImportarAPostgres(origen, almacen, reemplazar: false, backups));
        Assert.True(almacen.ExistePersonaje("DeAntes"));

        var informe = Importador.ImportarAPostgres(origen, almacen, reemplazar: true, backups);

        Assert.True(informe.SalioBien, informe.Texto());
        Assert.False(almacen.ExistePersonaje("DeAntes"));
        Assert.False(almacen.ExisteCuenta("Vieja"));
        FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Importado"), almacen.LeerPersonaje("Importado")!);
        Assert.Single(Directory.GetFiles(backups, "antes-de-importar-*.zip"));
    }

    /// <summary>EL PASO DE SQLITE A POSTGRES CAMBIANDO EL TIPO: la base de Postgres está vacía y en <c>data/</c> está la
    /// <c>coverao.db</c> de cuando el servidor andaba con SQLite. Se importa sola, y el archivo queda renombrado.</summary>
    [Fact]
    public void El_primer_arranque_con_postgres_importa_la_base_de_sqlite()
    {
        string archivo = Path.Combine(_raiz, "coverao.db");
        var clan = ContratoDelAlmacen.ClanLleno("Sur", 1);
        using (var sqlite = new AlmacenSqlite(archivo))
        {
            var cuenta = Cuenta("Duena");
            sqlite.GuardarCuenta(cuenta);
            Assert.True(sqlite.CrearPersonaje(cuenta, FichasDePrueba.Llena("Viajero")));
            sqlite.GuardarClanes(new[] { clan });
        }
        using var almacen = new AlmacenPostgres(_base.Cadena);

        var informe = Importador.ImportarAPostgresSiHaceFalta(_raiz, archivo, almacen);

        Assert.NotNull(informe);
        Assert.Equal(1, informe!.Personajes);
        FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Viajero"), almacen.LeerPersonaje("Viajero")!);
        ContratoDelAlmacen.AssertIgual(clan, Assert.Single(almacen.LeerClanes()));
        Assert.False(File.Exists(archivo));
        Assert.True(File.Exists(archivo + ".importado"));
        Assert.Single(Directory.GetFiles(_raiz, "importacion-*.txt"));
        // Ya importado, no se vuelve a hacer.
        Assert.Null(Importador.ImportarAPostgresSiHaceFalta(_raiz, archivo, almacen));
    }

    /// <summary><c>pasar-a-postgres</c> desde la línea de comandos, con el Server.ini todavía en SQLite: copia la base de
    /// SQLite a la de Postgres de <c>[BASEDEDATOS]</c> y deja la de SQLite como estaba.</summary>
    [Fact]
    public void Pasar_a_postgres_desde_la_linea_de_comandos()
    {
        string archivo = Path.Combine(_raiz, "coverao.db");
        using (var sqlite = new AlmacenSqlite(archivo))
        {
            var cuenta = Cuenta("Duena");
            sqlite.GuardarCuenta(cuenta);
            Assert.True(sqlite.CrearPersonaje(cuenta, FichasDePrueba.Llena("Mudado")));
        }
        var datos = new NpgsqlConnectionStringBuilder(_base.Cadena);
        var ini = ServerIni.Desde(IniFile.Parse(
            $"[BASEDEDATOS]\nTipo=sqlite\nServidor={datos.Host}\nPuerto={datos.Port}\nBase={datos.Database}\n" +
            $"Usuario={datos.Username}\nClave={datos.Password}\n"));

        Assert.Equal(0, ComandosDeLaBase.Correr(new[] { "pasar-a-postgres" }, _raiz, ini));

        Assert.True(File.Exists(archivo));
        using var almacen = new AlmacenPostgres(_base.Cadena);
        FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Mudado"), almacen.LeerPersonaje("Mudado")!);
        // Otra vez no: la base ya tiene cuentas.
        Assert.Equal(1, ComandosDeLaBase.Correr(new[] { "pasar-a-postgres" }, _raiz, ini));
    }

    /// <summary>La columna de la IP es <c>inet</c>: lo que no se lea como IP queda vacío, sin voltear el guardado de la cuenta.</summary>
    [Fact]
    public void Una_ip_que_no_es_ip_no_voltea_el_guardado()
    {
        using var almacen = new AlmacenPostgres(_base.Cadena);
        var rara = Cuenta("Rara");
        rara.UltimaIp = "desconocida";
        var seis = Cuenta("Seis");
        seis.UltimaIp = "2001:db8::7";
        almacen.GuardarCuenta(rara);
        almacen.GuardarCuenta(seis);

        Assert.Equal("", almacen.LeerCuenta("Rara")!.UltimaIp);
        Assert.Equal("2001:db8::7", almacen.LeerCuenta("Seis")!.UltimaIp);
    }
}
