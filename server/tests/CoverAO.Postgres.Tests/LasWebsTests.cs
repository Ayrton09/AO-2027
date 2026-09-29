using CoverAO.Protocol.Tests;
using CoverAO.Server.Data;
using Npgsql;

namespace CoverAO.Postgres.Tests;

/// <summary>
/// LO QUE VE UNA PÁGINA WEB (fase5/postgres/DISENO.md, sección 8): las vistas de <c>publico</c> muestran lo público —sin el
/// staff, sin los borrados, sin nada de la cuenta— y el usuario de la web lee esas vistas y nada más.
/// </summary>
public sealed class LasWebsTests : IDisposable
{
    private readonly BaseDePrueba _base = new();
    private readonly AlmacenPostgres _almacen;

    private static readonly CatalogoParaLasWebs Catalogo = new(
        new[] { (1, "Espada Larga", 2) }, new[] { (1, "Dardo mágico") },
        new[] { (1, "Mago"), (2, "Clérigo") }, new[] { (1, "Humano"), (2, "Elfo") }, new[] { (1, "Ullathorpe") });

    public LasWebsTests()
    {
        _almacen = new AlmacenPostgres(_base.Cadena);
        var cuenta = new Account { Name = "Duena", PasswordHash = "pbkdf2$1$a$b", Salt = "" };
        _almacen.GuardarCuenta(cuenta);
        foreach (var (nombre, nivel, clase) in new[] { ("Jugador", 20, 1), ("Otro", 30, 2), ("ElDios", 50, 1), ("Borrado", 40, 1) })
            Assert.True(_almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena(nombre) with { Nivel = (byte)nivel, Clase = (byte)clase, Raza = 2 }));
        Assert.True(_almacen.ApartarPersonaje("Borrado"));
        _almacen.GuardarClanes(new[] { ContratoDelAlmacen.ClanLleno("Sur", 1) with
        {
            Miembros = System.Collections.Immutable.ImmutableArray.Create("Jugador", "Otro", "ElDios"),
        } });
        _almacen.PrepararParaLasWebs(new[] { "eldios" }, Catalogo, "9.9");
        _almacen.MarcarConectados(new[] { "Jugador", "ElDios" });
    }

    public void Dispose()
    {
        _almacen.Dispose();
        _base.Dispose();
    }

    private List<object?[]> Consulta(string sql, string? cadena = null)
    {
        using var con = new NpgsqlConnection(cadena ?? _base.Cadena);
        con.Open();
        using var c = new NpgsqlCommand(sql, con);
        using var r = c.ExecuteReader();
        var filas = new List<object?[]>();
        while (r.Read())
        {
            var fila = new object?[r.FieldCount];
            for (int i = 0; i < fila.Length; i++) fila[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            filas.Add(fila);
        }
        return filas;
    }

    [Fact]
    public void Las_vistas_muestran_lo_publico_y_nada_mas()
    {
        // Sin el staff y sin los borrados; con los nombres del catálogo.
        var personajes = Consulta("SELECT nombre, clase_nombre, raza_nombre, conectado FROM publico.personajes ORDER BY nombre");
        Assert.Equal(new object?[][] { new object?[] { "Jugador", "Mago", "Elfo", true }, new object?[] { "Otro", "Clérigo", "Elfo", false } }, personajes);

        var ranking = Consulta("SELECT puesto, nombre FROM publico.ranking ORDER BY puesto");
        Assert.Equal(new object?[][] { new object?[] { 1L, "Otro" }, new object?[] { 2L, "Jugador" } }, ranking);

        Assert.Equal(new[] { "Jugador" }, Consulta("SELECT nombre FROM publico.conectados").Select(f => (string)f[0]!));
        Assert.Equal(new object?[] { "9.9", 1L }, Consulta("SELECT version, conectados FROM publico.servidor").Single());
        Assert.Equal(3L, Consulta("SELECT miembros FROM publico.clanes WHERE nombre = 'Sur'").Single()[0]);
        Assert.Equal(new[] { "Jugador", "Otro" },
            Consulta("SELECT nombre FROM publico.miembros_de_clan ORDER BY nombre").Select(f => (string)f[0]!));
        Assert.Equal("Espada Larga", Consulta("SELECT nombre FROM publico.catalogo_objetos WHERE id = 1").Single()[0]);

        // Lo que no se muestra no está: ni el oro, ni la posición, ni nada de la cuenta.
        foreach (var columna in new[] { "oro", "oro_en_banco", "mapa", "cuenta_id" })
            Assert.Equal("42703", Assert.Throws<PostgresException>(() => Consulta($"SELECT {columna} FROM publico.personajes")).SqlState);
    }

    /// <summary>Los conectados: el que sigue conectado conserva su hora de entrada, el que salió se desmarca.</summary>
    [Fact]
    public void Los_conectados_se_marcan_y_se_desmarcan()
    {
        var desde = Consulta("SELECT desde FROM publico.conectados WHERE nombre = 'Jugador'").Single()[0];
        Thread.Sleep(20);
        _almacen.MarcarConectados(new[] { "Jugador", "Otro" });
        Assert.Equal(desde, Consulta("SELECT desde FROM publico.conectados WHERE nombre = 'Jugador'").Single()[0]);
        Assert.Equal(new[] { "Jugador", "Otro" }, Consulta("SELECT nombre FROM publico.conectados ORDER BY nombre").Select(f => (string)f[0]!));

        _almacen.MarcarConectados(Array.Empty<string>());
        Assert.Empty(Consulta("SELECT nombre FROM publico.conectados"));
    }

    /// <summary>EL USUARIO DE LA WEB lee las vistas de <c>publico</c> y NADA MÁS: ni las tablas de los personajes ni las de las
    /// cuentas. Es lo que hace segura una web que se conecta a la base del juego.</summary>
    [Fact]
    public void El_usuario_de_la_web_lee_las_vistas_y_nada_mas()
    {
        string rol = "web_" + Guid.NewGuid().ToString("N")[..10];
        var datos = new NpgsqlConnectionStringBuilder(_base.Cadena);
        var ini = ServerIni.Desde(IniFile.Parse($"[BASEDEDATOS]\nTipo=postgres\nServidor={datos.Host}\nPuerto={datos.Port}\n" +
                                                $"Base={datos.Database}\nUsuario={datos.Username}\nClave={datos.Password}\n"));
        try
        {
            string clave = AlmacenPostgres.CrearUsuarioWeb(ini, rol, datos.Username!, datos.Password ?? "");
            Assert.Matches("^[0-9a-f]{36}$", clave);
            string comoLaWeb = new NpgsqlConnectionStringBuilder(_base.Cadena) { Username = rol, Password = clave, Pooling = false }.ToString();

            Assert.Equal(2L, Consulta("SELECT count(*) FROM publico.personajes", comoLaWeb).Single()[0]);
            Assert.Single(Consulta("SELECT * FROM publico.servidor", comoLaWeb));
            foreach (var tabla in new[] { "personajes", "cuentas", "inventario", "correo", "ips_baneadas" })
                Assert.Equal("42501", Assert.Throws<PostgresException>(() => Consulta($"SELECT * FROM {tabla}", comoLaWeb)).SqlState);

            // Correrlo de nuevo le cambia la clave.
            Assert.NotEqual(clave, AlmacenPostgres.CrearUsuarioWeb(ini, rol, datos.Username!, datos.Password ?? ""));
        }
        finally
        {
            // El rol es de todo el Postgres: se va después de la base, que se lleva sus permisos.
            _almacen.Dispose();
            _base.Dispose();
            using var con = new NpgsqlConnection(PostgresDePrueba.Admin);
            con.Open();
            using var c = new NpgsqlCommand($"DROP ROLE IF EXISTS {rol}", con);
            c.ExecuteNonQuery();
        }
    }
}
