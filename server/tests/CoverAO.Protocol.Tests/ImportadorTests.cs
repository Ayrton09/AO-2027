using System.Collections.Immutable;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// IMPORTAR Y EXPORTAR (fase5/postgres/DISENO.md, sección 6): de una carpeta nuestra, de una del ORIGINAL y del SQLite
/// viejo a la base, y de la base a una carpeta. Todo o nada, con informe y verificación campo por campo.
/// </summary>
public class ImportadorTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_importar_" + Guid.NewGuid().ToString("N"));
    private string Datos => Path.Combine(_raiz, "data");
    private string Base => Path.Combine(_raiz, "coverao.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }

    private static Account Cuenta(string nombre) => new() { Name = nombre, PasswordHash = "pbkdf2$1$a$b" };

    /// <summary>SQL directo sobre un archivo de SQLite: para lo que el almacén no escribe a pedido, como una fecha de alta.</summary>
    private static object? Sql(string archivo, string sql)
    {
        using var con = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = archivo, Pooling = false }.ToString());
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar();
    }

    /// <summary>Una carpeta de datos NUESTRA, armada con el almacén de archivos: dos cuentas, tres personajes y el mundo.</summary>
    private (FichaDePersonaje[] Fichas, ClanGuardado Clan) CarpetaNuestra()
    {
        var archivos = new AlmacenDeTexto(new FileStore(Datos));
        var fichas = new[]
        {
            FichasDePrueba.Llena("Aldo", 1) with { Clan = "Sur", Amigos = ImmutableArray.Create("Bruma"), Pareja = "" },
            FichasDePrueba.Llena("Bruma", 2) with { Clan = "Sur", Amigos = ImmutableArray.Create("Aldo"), Pareja = "" },
            FichasDePrueba.Llena("Ciro", 3) with { Clan = "", Amigos = ImmutableArray<string>.Empty, Pareja = "" },
        };
        var uno = Cuenta("CuentaUno");
        archivos.GuardarCuenta(uno);
        Assert.True(archivos.CrearPersonaje(uno, fichas[0]));
        Assert.True(archivos.CrearPersonaje(uno, fichas[1]));
        var dos = Cuenta("CuentaDos");
        archivos.GuardarCuenta(dos);
        Assert.True(archivos.CrearPersonaje(dos, fichas[2]));
        var clan = ContratoDelAlmacen.ClanLleno("Sur", 1) with
        {
            Lider = "Aldo", Miembros = ImmutableArray.Create("Aldo", "Bruma"), Votos = ImmutableArray<KeyValuePair<string, string>>.Empty,
        };
        archivos.GuardarClanes(new[] { clan });
        archivos.GuardarCasillas(new Dictionary<string, IReadOnlyList<MailEntry>> { ["Ciro"] = new[] { new MailEntry("Aldo", "hola", 12, 3) } });
        archivos.GuardarSuelo(new[] { new ObjetoEnElSuelo(new WorldPos(1, 50, 50), 12, 3) });
        archivos.GuardarLlavesVendidas(new[] { new LlaveVendida(40, 3, 53) });
        archivos.GuardarRecord(17);
        return (fichas, clan);
    }

    /// <summary>
    /// UNA CUENTA DE MÁS DE 20 LETRAS —la 1.0.0 dejaba crear hasta 30— no cabe en la base, y copiarla cortaba la importación
    /// entera: el servidor no arrancaba. Esa cuenta ya no puede entrar al juego (el ingreso corta en 20, como el original), así
    /// que se saltea con sus personajes, se dice, y lo demás se importa.
    /// </summary>
    [Fact]
    public void Una_cuenta_de_mas_de_20_letras_se_saltea_y_lo_demas_se_importa()
    {
        CarpetaNuestra();
        const string Larga = "CuentaDeVeintitantasLetras";
        File.WriteAllText(Path.Combine(Datos, "Cuentas", Larga.ToUpperInvariant() + ".cnt"),
            $"[{Larga}]\r\nCuenta={Larga}\r\nPassword=abc\r\nSalt=xyz\r\n[PJS]\r\nNumPjs=1\r\nPJ1=Dario\r\n", CoverAO.Protocol.Codec.Ansi);
        File.WriteAllText(Path.Combine(Datos, "Charfile", "DARIO.chr"), "[INIT]\r\nGenero=1\r\nRaza=1\r\nClase=3\r\n", CoverAO.Protocol.Codec.Ansi);

        var informe = Importador.Importar(Datos, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        Assert.Equal((2, 3), (informe.Cuentas, informe.Personajes));
        Assert.Contains(informe.Avisos, a => a.Contains(Larga, StringComparison.OrdinalIgnoreCase) && a.Contains("Dario"));
        Assert.DoesNotContain(informe.Avisos, a => a.StartsWith("Dario no está en ninguna cuenta"));
        using var base_ = new AlmacenSqlite(Base);
        Assert.Null(base_.LeerCuenta(Larga));
        Assert.False(base_.ExistePersonaje("Dario"));
    }

    /// <summary>
    /// LO RESCATADO NO VA ENCIMA DE UN BACKUP. Un rescate que quedó de un apagado con la base caída se escribió sobre la base de
    /// antes; restaurar es volver a otro punto, y aplicado encima mezclaría los dos. Se aparta, y el arranque no lo aplica.
    /// </summary>
    [Fact]
    public void Restaurar_aparta_los_rescates_que_esperaban()
    {
        CarpetaNuestra();
        string data = Path.Combine(_raiz, "srv");
        Directory.CreateDirectory(data);
        string backup = Path.Combine(_raiz, "backup.db");
        Importador.Importar(Datos, backup);
        string? rescate = Rescates.Escribir(data, new (string, Rescate?)[] { ("guardar a Aldo", new RescateDePersonaje(FichasDePrueba.Llena("Aldo", 9))) });

        Assert.Equal(0, ComandosDeLaBase.Correr(new[] { "restaurar", backup }, data, new ServerIni()));

        Assert.False(File.Exists(rescate));
        Assert.True(File.Exists(rescate + ".sin-aplicar"));
        using var base_ = new AlmacenSqlite(Path.Combine(data, "coverao.db"));
        Assert.Equal(0, Rescates.AplicarLosPendientes(data, base_));
    }

    /// <summary>
    /// UN BACKUP DE SQLITE SE RESTAURA ENTERO: es la misma base, así que vuelve con lo que la ficha de un personaje no lleva —las
    /// fechas de alta, los personajes apartados, que se pueden devolver—. Pasado personaje por personaje, eso se perdía.
    /// </summary>
    [Fact]
    public void Un_backup_de_sqlite_vuelve_con_las_fechas_y_los_apartados()
    {
        string backup = Path.Combine(_raiz, "backup.db");
        using (var origen = new AlmacenSqlite(backup))
        {
            var cuenta = Cuenta("Duena");
            origen.GuardarCuenta(cuenta);
            Assert.True(origen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Vivo")));
            Assert.True(origen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Borrado", 2)));
            Assert.True(origen.ApartarPersonaje("Borrado"));
        }
        Sql(backup, "UPDATE cuentas SET creada_en = '2026-01-02T03:04:05.000Z'");

        var informe = Importador.Importar(backup, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        Assert.Equal((1, 1, 1), (informe.Personajes, informe.Apartados, informe.Equivalentes));
        Assert.Equal(1L, Sql(Base, "SELECT count(*) FROM personajes WHERE nombre = 'Borrado' AND borrado_en IS NOT NULL"));
        Assert.Equal("2026-01-02T03:04:05.000Z", Sql(Base, "SELECT creada_en FROM cuentas WHERE nombre = 'Duena'"));
        using var base_ = new AlmacenSqlite(Base);
        FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Vivo"), base_.LeerPersonaje("Vivo")!);
    }

    /// <summary>UNA BASE, UN PROCESO: con un servidor andando sobre la base, importar encima no la toca.</summary>
    [Fact]
    public void Con_la_base_abierta_por_otro_no_se_importa_encima()
    {
        CarpetaNuestra();
        using (new AlmacenSqlite(Base))
        {
            var error = Assert.Throws<InvalidOperationException>(() => Importador.Importar(Datos, Base, reemplazar: true));
            Assert.Contains("Otro proceso ya usa", error.Message);
        }
        Assert.True(Importador.Importar(Datos, Base, reemplazar: true).SalioBien);
    }

    [Fact]
    public void Una_carpeta_nuestra_se_importa_entera_y_verificada()
    {
        var (fichas, clan) = CarpetaNuestra();

        var informe = Importador.Importar(Datos, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        Assert.Equal(2, informe.Cuentas);
        Assert.Equal(3, informe.Personajes);
        Assert.Equal(3, informe.Equivalentes);
        Assert.Empty(informe.Avisos);
        using var base_ = new AlmacenSqlite(Base);
        foreach (var f in fichas) FichasDePrueba.AssertIgual(f, base_.LeerPersonaje(f.Nombre)!);
        Assert.Equal(new[] { "Aldo", "Bruma" }, base_.LeerCuenta("CuentaUno")!.Characters);
        ContratoDelAlmacen.AssertIgual(clan, Assert.Single(base_.LeerClanes()));
        Assert.Equal(new[] { new MailEntry("Aldo", "hola", 12, 3) }, base_.LeerCorreo()["Ciro"]);
        Assert.Equal(17, base_.LeerRecord());
        Assert.Single(base_.LeerSuelo());
        Assert.Single(base_.LeerLlavesVendidas());
    }

    /// <summary>El <c>.chr</c> del ORIGINAL escribe varias cosas con otras claves que el nuestro (TCP.bas:1840-1990): la pena
    /// en <c>[COUNTERS]</c>, los contadores de facción abreviados, los amigos como <c>NombreN</c> con huecos "Vacío", y el
    /// correo ADENTRO. Leído como uno nuestro, todo eso daba cero.</summary>
    [Fact]
    public void Un_chr_del_original_se_lee_con_sus_claves()
    {
        var ini = IniFile.Parse(
            "[INIT]\r\nGenero=1\r\nRaza=1\r\nClase=3\r\nHogar=1\r\nPosition=1-50-50\r\n" +
            "[COUNTERS]\r\nPena=30\r\n" +
            "[FACCIONES]\r\nStatus=1\r\nCiudMatados=5\r\nReneMatados=6\r\nRepuMatados=7\r\nCaosMatados=8\r\nArmiMatados=9\r\nMiliMatados=10\r\n" +
            "[FLAGS]\r\nCantidadAmigos=3\r\nCorreos=2\r\n" +
            "[AMIGOS]\r\nNombre1=Aldo\r\nNombre2=Vacío\r\nNombre3=Bruma\r\n" +
            "[CORREO]\r\nEmisor1=Ciro\r\nCarta1=hola\r\nLeida1=1\r\nObjeto1=12-3-100-Espada\r\n" +
            "Emisor2=CoverAO Staff\r\nCarta2=bienvenido\r\nLeida2=0\r\nObjeto2=0-0-0-\r\n");

        var ficha = CharacterFile.FichaDesdeIni(ini, "Viejo");

        Assert.Equal(30, ficha.MinutosDeCarcel);
        Assert.Equal(5, ficha.CiudadanosMatados);
        Assert.Equal(6, ficha.RenegadosMatados);
        Assert.Equal(7, ficha.RepublicanosMatados);
        Assert.Equal(8, ficha.CaosMatados);
        Assert.Equal(9, ficha.ArmadaMatados);
        Assert.Equal(10, ficha.MilicianosMatados);
        Assert.Equal(new[] { "Aldo", "Bruma" }, ficha.Amigos.AsEnumerable());
        Assert.Equal(new[] { new MailEntry("Ciro", "hola", 12, 3, 1), new MailEntry("CoverAO Staff", "bienvenido", 0, 0) },
            CharacterFile.CorreoDelOriginal(ini));
    }

    [Fact]
    public void Una_carpeta_del_original_se_importa_con_su_correo()
    {
        Directory.CreateDirectory(Path.Combine(Datos, "Charfile"));
        Directory.CreateDirectory(Path.Combine(Datos, "Cuentas"));
        File.WriteAllText(Path.Combine(Datos, "Cuentas", "VIEJA.cnt"),
            "[VIEJA]\r\nCuenta=VIEJA\r\nPassword=abc\r\nSalt=xyz\r\nBan=0\r\nUltimoAcceso=15/8/2026 13:08:07\r\nUltimaIP=10.0.0.1\r\n" +
            "[PJS]\r\nNumPjs=1\r\nPJ1=Viejo\r\n", CoverAO.Protocol.Codec.Ansi);
        File.WriteAllText(Path.Combine(Datos, "Charfile", "VIEJO.chr"),
            "[INIT]\r\nGenero=1\r\nRaza=1\r\nClase=3\r\nHogar=1\r\nPosition=1-50-50\r\n[STATS]\r\nELV=12\r\n" +
            "[COUNTERS]\r\nPena=30\r\n[FACCIONES]\r\nStatus=1\r\nCiudMatados=5\r\n" +
            "[FLAGS]\r\nCantidadAmigos=0\r\nCorreos=1\r\n[CORREO]\r\nEmisor1=Ciro\r\nCarta1=hola\r\nLeida1=0\r\nObjeto1=12-3-100-Espada\r\n",
            CoverAO.Protocol.Codec.Ansi);
        // Las IPs baneadas del original, una por renglón; el "0" que trae su instalación no es una IP.
        Directory.CreateDirectory(Path.Combine(Datos, "Dat"));
        File.WriteAllText(Path.Combine(Datos, "Dat", "BanIps.dat"), "0\r\n10.9.8.7\r\n", CoverAO.Protocol.Codec.Ansi);

        var informe = Importador.Importar(Datos, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        using var base_ = new AlmacenSqlite(Base);
        var viejo = base_.LeerPersonaje("Viejo")!;
        Assert.Equal(12, viejo.Nivel);
        Assert.Equal(30, viejo.MinutosDeCarcel);
        Assert.Equal(5, viejo.CiudadanosMatados);
        Assert.Equal(new[] { new MailEntry("Ciro", "hola", 12, 3) }, base_.LeerCorreo()["Viejo"]);
        var cuenta = base_.LeerCuenta("vieja")!;
        Assert.Equal("10.0.0.1", cuenta.UltimaIp);
        Assert.NotNull(cuenta.UltimoIngreso);
        Assert.Equal(new[] { "10.9.8.7" }, base_.LeerIpsBaneadas());
        Assert.False(Directory.Exists(Path.Combine(Datos, "Misc")), "leer la carpeta de origen no puede crearle nada");
    }

    /// <summary>Una segunda importación —se borró la base y quedaban archivos— con los <c>*.importado</c> de la primera al lado:
    /// no los pisa ni falla por ellos, aparta lo nuevo con la fecha.</summary>
    [Fact]
    public void Una_segunda_importacion_no_pisa_lo_que_aparto_la_primera()
    {
        var archivos = new AlmacenDeTexto(new FileStore(Datos));
        var cuenta = Cuenta("Cuenta");
        archivos.GuardarCuenta(cuenta);
        Assert.True(archivos.CrearPersonaje(cuenta, FichasDePrueba.Llena("Segundo")));
        string viejo = Path.Combine(Datos, "Charfile.importado");
        Directory.CreateDirectory(viejo);
        File.WriteAllText(Path.Combine(viejo, "PRIMERO.chr"), "[INIT]\r\nGenero=1\r\n");

        var informe = Importador.ImportarSiHaceFalta(Datos, Base);

        Assert.NotNull(informe);
        Assert.True(File.Exists(Path.Combine(viejo, "PRIMERO.chr")));   // lo de la primera, intacto
        Assert.False(Directory.Exists(Path.Combine(Datos, "Charfile")));
        Assert.Single(Directory.GetDirectories(Datos, "Charfile.importado-*"));
    }

    /// <summary>Nada se descarta en silencio: lo que no cierra en el origen va al informe.</summary>
    [Fact]
    public void El_informe_dice_lo_que_no_cierra()
    {
        var archivos = new AlmacenDeTexto(new FileStore(Datos));
        var cuenta = Cuenta("Cuenta");
        cuenta.Characters.Add("Fantasma");   // la cuenta lista a uno que no existe
        archivos.GuardarCuenta(cuenta);
        Assert.True(archivos.CrearPersonaje(cuenta, FichasDePrueba.Llena("Aldo") with
        {
            Clan = "Inexistente", Amigos = ImmutableArray.Create("Nadie"), Pareja = "Nadita",
        }));
        archivos.GuardarPersonaje(FichasDePrueba.Llena("Huerfano"));   // un .chr que no está en ninguna cuenta
        archivos.GuardarClanes(new[] { new ClanGuardado { Nombre = "Norte", Lider = "Zeta", Miembros = ImmutableArray.Create("Zeta") } });
        archivos.GuardarCasillas(new Dictionary<string, IReadOnlyList<MailEntry>> { ["Nadie"] = new[] { new MailEntry("Aldo", "?", 0, 0) } });

        var informe = Importador.Importar(Datos, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        string todo = string.Join("\n", informe.Avisos);
        Assert.Contains("lista a Fantasma, que no existe", todo);
        Assert.Contains("HUERFANO no está en ninguna cuenta", todo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clan Inexistente, que no existe", todo);
        Assert.Contains("de amigo a Nadie", todo);
        Assert.Contains("casado con Nadita", todo);
        Assert.Contains("el clan Norte lista a Zeta, que no existe", todo);
        Assert.Contains("carta(s) para Nadie", todo);
    }

    /// <summary>Un almacén que cambia un campo al leer: la verificación tiene que verlo.</summary>
    private sealed class AlmacenQueCambiaElOro(IAlmacen real) : IAlmacen
    {
        public FichaDePersonaje? LeerPersonaje(string nombre) => real.LeerPersonaje(nombre) is { } f ? f with { Oro = f.Oro + 1 } : null;
        public bool ExisteCuenta(string nombre) => real.ExisteCuenta(nombre);
        public Account? LeerCuenta(string nombre) => real.LeerCuenta(nombre);
        public void GuardarCuenta(Account cuenta) => real.GuardarCuenta(cuenta);
        public IEnumerable<string> NombresDeCuentas() => real.NombresDeCuentas();
        public bool ExistePersonaje(string nombre) => real.ExistePersonaje(nombre);
        public void GuardarPersonaje(FichaDePersonaje ficha) => real.GuardarPersonaje(ficha);
        public bool CrearPersonaje(Account cuenta, FichaDePersonaje ficha) => real.CrearPersonaje(cuenta, ficha);
        public bool ApartarPersonaje(string nombre) => real.ApartarPersonaje(nombre);
        public IEnumerable<string> NombresDePersonajes() => real.NombresDePersonajes();
        public CharacterSummary? LeerResumen(string nombre) => real.LeerResumen(nombre);
        public long? AcreditarBoveda(string nombre, long monto) => real.AcreditarBoveda(nombre, monto);
        public IReadOnlyList<CharacterFile.FilaDeRanking> FilasDeRanking() => real.FilasDeRanking();
        public IReadOnlyList<ClanGuardado> LeerClanes() => real.LeerClanes();
        public void GuardarClanes(IReadOnlyList<ClanGuardado> clanes) => real.GuardarClanes(clanes);
        public IReadOnlyList<KeyValuePair<string, string>> LeerRechazosDeClan() => real.LeerRechazosDeClan();
        public void GuardarRechazosDeClan(IReadOnlyList<KeyValuePair<string, string>> rechazos) => real.GuardarRechazosDeClan(rechazos);
        public IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> LeerCorreo() => real.LeerCorreo();
        public void GuardarCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas) => real.GuardarCasillas(casillas);
        public IReadOnlyList<ObjetoEnElSuelo> LeerSuelo() => real.LeerSuelo();
        public void GuardarSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo) => real.GuardarSuelo(suelo);
        public IReadOnlyList<LlaveVendida> LeerLlavesVendidas() => real.LeerLlavesVendidas();
        public void GuardarLlavesVendidas(IReadOnlyList<LlaveVendida> llaves) => real.GuardarLlavesVendidas(llaves);
        public SubastasGuardadas LeerSubastas() => real.LeerSubastas();
        public void GuardarSubastas(SubastasGuardadas subastas) => real.GuardarSubastas(subastas);
        public int LeerRecord() => real.LeerRecord();
        public void GuardarRecord(int record) => real.GuardarRecord(record);
        public IReadOnlyList<string> LeerIpsBaneadas() => real.LeerIpsBaneadas();
        public void GuardarIpsBaneadas(IReadOnlyList<string> ips) => real.GuardarIpsBaneadas(ips);
        public string Backup(string destDir, string stem) => real.Backup(destDir, stem);
    }

    [Fact]
    public void La_verificacion_ve_un_campo_que_no_llego_igual()
    {
        CarpetaNuestra();
        using var destino = AlmacenSqlite.EnMemoria();

        var informe = CopiaDeAlmacen.Copiar(new AlmacenDeTexto(new FileStore(Datos, crearCarpetas: false)), new AlmacenQueCambiaElOro(destino));

        Assert.False(informe.SalioBien);
        Assert.Equal(3, informe.Distintos.Count);
        Assert.Contains("Oro", informe.Distintos[0]);
    }

    [Fact]
    public void Una_base_que_ya_existe_no_se_pisa_sin_reemplazar()
    {
        CarpetaNuestra();
        Assert.True(Importador.Importar(Datos, Base).SalioBien);

        Assert.Throws<InvalidOperationException>(() => Importador.Importar(Datos, Base));

        Assert.True(Importador.Importar(Datos, Base, reemplazar: true).SalioBien);
        Assert.Single(Directory.GetFiles(_raiz, "coverao.db.antes-de-importar-*"));
    }

    /// <summary>El primer arranque de la versión con base, en una instalación con archivos: importa solo, y lo viejo queda al
    /// lado, renombrado, con el informe en <c>data/</c>.</summary>
    [Fact]
    public void El_primer_arranque_importa_los_archivos_y_los_aparta()
    {
        var (fichas, _) = CarpetaNuestra();

        var (almacen, dondeSeGuarda) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        try
        {
            var sqlite = Assert.IsType<AlmacenSqlite>(almacen);
            FichasDePrueba.AssertIgual(fichas[0], sqlite.LeerPersonaje("Aldo")!);
            Assert.Contains("importada", dondeSeGuarda);
        }
        finally { (almacen as IDisposable)?.Dispose(); }
        Assert.True(Directory.Exists(Path.Combine(Datos, "Charfile.importado")));
        Assert.False(Directory.Exists(Path.Combine(Datos, "Charfile")));
        Assert.Single(Directory.GetFiles(Datos, "importacion-*.txt"));

        // Y el segundo arranque ya no importa nada: abre la base.
        var (otra, dice) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        (otra as IDisposable)?.Dispose();
        Assert.DoesNotContain("importada", dice);
    }

    /// <summary>Un arranque sobre la carpeta limpia deja la base creada y vacía; los archivos copiados después se importan igual
    /// en el arranque siguiente, y la base vacía queda al lado.</summary>
    [Fact]
    public void Los_archivos_copiados_despues_de_crear_la_base_vacia_se_importan()
    {
        Directory.CreateDirectory(Datos);
        var (vacia, _) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        (vacia as IDisposable)?.Dispose();
        var (fichas, _) = CarpetaNuestra();

        var (almacen, dondeSeGuarda) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        try
        {
            FichasDePrueba.AssertIgual(fichas[0], Assert.IsType<AlmacenSqlite>(almacen).LeerPersonaje("Aldo")!);
            Assert.Contains("importada", dondeSeGuarda);
        }
        finally { (almacen as IDisposable)?.Dispose(); }
        Assert.Single(Directory.GetFiles(Datos, "coverao.db.antes-de-importar-*"), f => char.IsDigit(f[^1]));   // y su diario al lado
        Assert.True(Directory.Exists(Path.Combine(Datos, "Charfile.importado")));
    }

    /// <summary>LA BASE SIN NADIE PUEDE TENER MUNDO: las IPs baneadas desde el panel se suman a la base importada, y lo demás
    /// que tenía —acá, una llave vendida— queda en el informe. Antes se iban las dos cosas sin aviso.</summary>
    [Fact]
    public void Importar_sobre_la_base_sin_nadie_suma_sus_ips_y_avisa_lo_demas()
    {
        Directory.CreateDirectory(Datos);
        var (vacia, _) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        vacia.GuardarIpsBaneadas(new[] { "10.4.4.4" });
        vacia.GuardarLlavesVendidas(new[] { new LlaveVendida(41, 2, 53) });
        (vacia as IDisposable)?.Dispose();
        CarpetaNuestra();

        var (almacen, dondeSeGuarda) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        try
        {
            Assert.Contains("importada", dondeSeGuarda);
            Assert.Equal(new[] { "10.4.4.4" }, almacen.LeerIpsBaneadas());
        }
        finally { (almacen as IDisposable)?.Dispose(); }
        Assert.Contains("llaves vendidas (1)", File.ReadAllText(Assert.Single(Directory.GetFiles(Datos, "importacion-*.txt"))));
    }

    /// <summary>UN <c>.chr</c> CORTADO —el original lo reescribe entero, sin temporal, y un corte en el medio lo deja a medias— se
    /// importa con lo que le falta vacío o en cero, y la verificación no lo ve porque relee lo mismo: queda en el informe.</summary>
    [Fact]
    public void Un_chr_cortado_queda_en_el_informe()
    {
        CarpetaNuestra();
        string ciro = Directory.GetFiles(Path.Combine(Datos, "Charfile"), "*.chr")
            .Single(f => Path.GetFileNameWithoutExtension(f).Equals("Ciro", StringComparison.OrdinalIgnoreCase));
        string texto = File.ReadAllText(ciro, CoverAO.Protocol.Codec.Ansi);
        File.WriteAllText(ciro, texto[..texto.IndexOf("[Inventory]", StringComparison.Ordinal)], CoverAO.Protocol.Codec.Ansi);

        var informe = Importador.Importar(Datos, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        var aviso = Assert.Single(informe.Avisos, a => a.StartsWith("Ciro", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("[Inventory]", aviso);
        Assert.Contains("[HECHIZOS]", aviso);
    }

    /// <summary>UN CORTE ADENTRO DE LA ÚLTIMA SECCIÓN —las habilidades, en los nuestros— deja todas las secciones: lo dicen las
    /// claves que faltan.</summary>
    [Fact]
    public void Un_chr_cortado_adentro_de_la_ultima_seccion_queda_en_el_informe()
    {
        CarpetaNuestra();
        string ciro = Directory.GetFiles(Path.Combine(Datos, "Charfile"), "*.chr")
            .Single(f => Path.GetFileNameWithoutExtension(f).Equals("Ciro", StringComparison.OrdinalIgnoreCase));
        string texto = File.ReadAllText(ciro, CoverAO.Protocol.Codec.Ansi);
        int skills = texto.IndexOf("[SKILLS]", StringComparison.Ordinal);
        Assert.DoesNotContain("[", texto[(skills + 1)..]);
        File.WriteAllText(ciro, texto[..texto.IndexOf("SK20=", skills, StringComparison.Ordinal)], CoverAO.Protocol.Codec.Ansi);

        var informe = Importador.Importar(Datos, Base);

        Assert.True(informe.SalioBien, informe.Texto());
        var aviso = Assert.Single(informe.Avisos, a => a.StartsWith("Ciro", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("[SKILLS] SK20", aviso);
        Assert.Contains("[SKILLS] SK27", aviso);
        Assert.DoesNotContain("SK19", aviso);
    }

    /// <summary>EN EL DEL ORIGINAL, [AMIGOS] y [CORREO] van últimas: falta lo que el archivo dice que hay y no está. Las
    /// casillas vacías que se lleva el corte no cuentan: no se leen.</summary>
    [Fact]
    public void En_el_del_original_faltan_los_amigos_y_las_cartas_que_dice_y_no_estan()
    {
        string skills = "[SKILLS]\n" + string.Concat(Enumerable.Range(1, 27).Select(i => $"SK{i}=0\n"));
        string entero = "[FLAGS]\nCorreos=1\nCantidadAmigos=2\n" + skills +
                        "[AMIGOS]\nNOMBRE1=Ana\nNOMBRE2=Beto\nNOMBRE3=Vacío\n[CORREO]\nCarta1=hola\nEmisor1=Ana\nLeida1=0\nObjeto1=0-0\n";
        Assert.Empty(CharacterFile.ClavesQueFaltan(IniFile.Parse(entero)));
        Assert.Empty(CharacterFile.ClavesQueFaltan(IniFile.Parse(entero[..entero.IndexOf("Objeto1", StringComparison.Ordinal)])));
        Assert.Equal(new[] { "[CORREO] Emisor1", "[CORREO] Leida1" },
            CharacterFile.ClavesQueFaltan(IniFile.Parse(entero[..entero.IndexOf("Emisor1", StringComparison.Ordinal)])));
        Assert.Equal(new[] { "[CORREO]" },
            CharacterFile.ClavesQueFaltan(IniFile.Parse(entero[..entero.IndexOf("[CORREO]", StringComparison.Ordinal)])));
        Assert.Equal(new[] { "[AMIGOS] Nombre2", "[CORREO]" },
            CharacterFile.ClavesQueFaltan(IniFile.Parse(entero[..entero.IndexOf("NOMBRE2", StringComparison.Ordinal)])));
    }

    /// <summary>Con cuentas en la base, los archivos de al lado no se importan encima: se avisa y quedan donde están.</summary>
    [Fact]
    public void Con_cuentas_en_la_base_los_archivos_de_al_lado_no_se_importan()
    {
        Directory.CreateDirectory(Datos);
        var (conGente, _) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        conGente.GuardarCuenta(Cuenta("YaEstaba"));
        (conGente as IDisposable)?.Dispose();
        CarpetaNuestra();

        var (almacen, dondeSeGuarda) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        try
        {
            Assert.DoesNotContain("importada", dondeSeGuarda);
            Assert.NotNull(almacen.LeerCuenta("YaEstaba"));
            Assert.Null(almacen.LeerPersonaje("Aldo"));
        }
        finally { (almacen as IDisposable)?.Dispose(); }
        Assert.True(Directory.Exists(Path.Combine(Datos, "Charfile")));
    }

    [Fact]
    public void El_primer_arranque_importa_el_sqlite_viejo()
    {
        Directory.CreateDirectory(Datos);
        string viejo = Path.Combine(Datos, "coverao.db");
        using (var store = new SqliteStore(viejo))
        {
            var texto = new AlmacenDeTexto(store);
            var cuenta = Cuenta("DelViejo");
            texto.GuardarCuenta(cuenta);
            Assert.True(texto.CrearPersonaje(cuenta, FichasDePrueba.Llena("Antiguo")));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (almacen, _) = Almacenes.Abrir(ServerIni.Desde(IniFile.Parse("")), Datos);
        try { FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Antiguo"), Assert.IsType<AlmacenSqlite>(almacen).LeerPersonaje("Antiguo")!); }
        finally { (almacen as IDisposable)?.Dispose(); }
        Assert.True(File.Exists(viejo + ".importado"));
    }

    /// <summary>Exportar a archivos y volver a importar da lo mismo: es la vuelta atrás.</summary>
    [Fact]
    public void Exportar_y_volver_a_importar_da_lo_mismo()
    {
        var (fichas, _) = CarpetaNuestra();
        Assert.True(Importador.Importar(Datos, Base).SalioBien);
        string exportado = Path.Combine(_raiz, "exportado");
        using (var base_ = new AlmacenSqlite(Base))
            Assert.True(Importador.Exportar(base_, exportado).SalioBien);

        string otraBase = Path.Combine(_raiz, "otra.db");
        var informe = Importador.Importar(exportado, otraBase);

        Assert.True(informe.SalioBien, informe.Texto());
        using var otra = new AlmacenSqlite(otraBase);
        foreach (var f in fichas) FichasDePrueba.AssertIgual(f, otra.LeerPersonaje(f.Nombre)!);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var b = new AlmacenSqlite(Base);
            Importador.Exportar(b, exportado);   // a una carpeta con cosas, no
        });
    }
}
