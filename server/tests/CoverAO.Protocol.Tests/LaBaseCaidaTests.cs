using System.Diagnostics;
using System.Reflection;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>Un almacén que falla a pedido: reenvía todo al de verdad, salvo que <see cref="Falla"/> diga que ese llamado tira.</summary>
public class AlmacenQueFalla : DispatchProxy
{
    private IAlmacen _real = null!;
    private Func<string, Exception?> _falla = _ => null;
    private Func<string, object?[], Exception?> _despues = (_, _) => null;
    public int Llamados;

    public static (IAlmacen Almacen, AlmacenQueFalla Control) Crear(IAlmacen real, Func<string, Exception?> falla)
    {
        var proxy = Create<IAlmacen, AlmacenQueFalla>();
        var control = (AlmacenQueFalla)(object)proxy;
        control._real = real;
        control._falla = falla;
        return (proxy, control);
    }

    public void Falla(Func<string, Exception?> falla) => _falla = falla;

    /// <summary>Que el llamado SE HAGA y después tire: la base guardó y la respuesta se perdió, un corte justo ahí.</summary>
    public void FallaDespues(Func<string, object?[], Exception?> falla) => _despues = falla;

    protected override object? Invoke(MethodInfo? metodo, object?[]? args)
    {
        Interlocked.Increment(ref Llamados);
        if (_falla(metodo!.Name) is { } ex) throw ex;
        object? resultado;
        try { resultado = metodo.Invoke(_real, args); }
        catch (TargetInvocationException envuelta) { throw envuelta.InnerException!; }
        if (_despues(metodo.Name, args ?? Array.Empty<object?>()) is { } despues) throw despues;
        return resultado;
    }
}

/// <summary>
/// LA BASE QUE SE CAE (fase5/postgres/DISENO.md, 3.5): la cola reintenta lo que falla porque la base no responde, con lo
/// demás esperando en orden; al apagar con la base caída devuelve lo que no se aplicó, y el archivo de rescate lo lleva al
/// próximo arranque.
/// </summary>
public sealed class LaBaseCaidaTests : IDisposable
{
    private static readonly TimeSpan[] Rapido = { TimeSpan.FromMilliseconds(5) };
    private readonly AlmacenSqlite _real = AlmacenSqlite.EnMemoria();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_caida_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _real.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }

    /// <summary>La base trabada: SQLite con el archivo ocupado (SQLITE_BUSY), una de las fallas que se reintentan.</summary>
    private static Exception Trabada() => new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);

    private static Account Cuenta(string nombre) => new() { Name = nombre, PasswordHash = "pbkdf2$1$a$b", Salt = "" };

    private static Leveling Niveles() => new(new Dictionary<int, long> { [1] = 300, [2] = 450 });

    private Account ConPersonajes(params string[] nombres)
    {
        var cuenta = Cuenta("Duena");
        _real.GuardarCuenta(cuenta);
        foreach (var n in nombres) Assert.True(_real.CrearPersonaje(cuenta, FichasDePrueba.Llena(n)));
        return cuenta;
    }

    [Fact]
    public async Task Si_la_base_no_responde_se_reintenta_y_lo_demas_espera_en_orden()
    {
        ConPersonajes("Uno", "Dos");
        int fallas = 3;
        var (almacen, _) = AlmacenQueFalla.Crear(_real,
            m => m == nameof(IAlmacen.GuardarPersonaje) && Interlocked.Decrement(ref fallas) >= 0 ? Trabada() : null);
        using var cola = new ColaDeLaBase(almacen, Rapido);

        var uno = FichasDePrueba.Llena("Uno", semilla: 7);
        var dos = FichasDePrueba.Llena("Dos", semilla: 8);
        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(uno));
        // La lectura va DETRÁS del guardado que falla: tiene que ver lo guardado, no lo de antes.
        var leida = cola.PedirAsync("leer a Uno", a => a.LeerPersonaje("Uno"));
        cola.Escribir("guardar a Dos", a => a.GuardarPersonaje(dos));

        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(10)));
        FichasDePrueba.AssertIgual(uno, (await leida)!);
        FichasDePrueba.AssertIgual(dos, _real.LeerPersonaje("Dos")!);
        Assert.Null(cola.CaidaDesde);
    }

    [Fact]
    public void Mientras_la_base_no_responde_se_sabe_desde_cuando_y_cuantos_esperan()
    {
        ConPersonajes("Uno");
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => Trabada());
        using var cola = new ColaDeLaBase(almacen, Rapido);

        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(FichasDePrueba.Llena("Uno", 3)));
        cola.Escribir("guardar la cuenta", a => a.GuardarCuenta(Cuenta("Duena")));
        Assert.True(SpinWait.SpinUntil(() => cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, cola.Pendientes);
        Assert.NotNull(cola.EsperandoDesde);

        control.Falla(_ => null);   // vuelve
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(10)));
        Assert.Null(cola.CaidaDesde);
        // `Vaciar` vuelve cuando se aplicó su marca, y la marca deja de contar como pendiente un instante DESPUÉS, en el
        // `finally` del escritor: leerlo de una fallaba con la máquina cargada (30/09, una vez en la suite, cinco de cinco sola).
        Assert.True(SpinWait.SpinUntil(() => cola.Pendientes == 0, TimeSpan.FromSeconds(5)), $"quedan {cola.Pendientes} pendientes");
    }

    /// <summary>Un error que no es de la base —un dato que no entra, un bug— no se reintenta: se anota y la cola sigue.</summary>
    [Fact]
    public void Un_error_que_no_es_de_la_base_no_se_reintenta()
    {
        ConPersonajes("Uno");
        var (almacen, control) = AlmacenQueFalla.Crear(_real,
            m => m == nameof(IAlmacen.GuardarCuenta) ? new InvalidOperationException("no entra") : null);
        using var cola = new ColaDeLaBase(almacen, Rapido);

        cola.Escribir("guardar la cuenta", a => a.GuardarCuenta(Cuenta("Duena")));
        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(FichasDePrueba.Llena("Uno", 4)));

        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, control.Llamados);
        FichasDePrueba.AssertIgual(FichasDePrueba.Llena("Uno", 4), _real.LeerPersonaje("Uno")!);
    }

    /// <summary>LA LECTURA ABANDONADA SALE DE LA COLA con la base todavía caída, y las que esperan tienen tope: el ingreso deja
    /// de esperar a los 10 s, y cada intento con la base caída dejaba su lectura en la cola, reintentándose sin fin.</summary>
    [Fact]
    public void Una_lectura_abandonada_sale_de_la_cola_y_las_que_esperan_tienen_tope()
    {
        var (almacen, _) = AlmacenQueFalla.Crear(_real, _ => Trabada());
        using var cola = new ColaDeLaBase(almacen, Rapido, topeDeLecturas: 2);
        using var abandono = new CancellationTokenSource();

        var una = cola.PedirAsync("leer una cuenta (Una)", a => a.LeerCuenta("Una"), abandono.Token);
        cola.PedirAsync("leer una cuenta (Otra)", a => a.LeerCuenta("Otra"), abandono.Token);
        var deMas = cola.PedirAsync("leer una cuenta (Tercera)", a => a.LeerCuenta("Tercera"), abandono.Token);
        Assert.IsType<LaBaseNoResponde>(deMas.Exception?.InnerException);
        Assert.True(SpinWait.SpinUntil(() => cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));

        abandono.Cancel();

        Assert.True(SpinWait.SpinUntil(() => cola.Pendientes == 0, TimeSpan.FromSeconds(5)), $"quedan {cola.Pendientes} pendientes");
        Assert.True(SpinWait.SpinUntil(() => una.IsCanceled, TimeSpan.FromSeconds(5)));
        using var otro = new CancellationTokenSource();
        Assert.False(cola.PedirAsync("leer una cuenta (Cuarta)", a => a.LeerCuenta("Cuarta"), otro.Token).IsFaulted);
    }

    /// <summary>AL APAGAR CON LA BASE CAÍDA la cola se rinde y devuelve lo que no se aplicó, en orden: lo que tiene rescate, con
    /// su rescate; lo demás, anotado. El que esperaba una respuesta se entera.</summary>
    [Fact]
    public void Al_cerrar_con_la_base_caida_devuelve_lo_que_no_se_aplico_en_orden()
    {
        var (almacen, _) = AlmacenQueFalla.Crear(_real, _ => Trabada());
        var cola = new ColaDeLaBase(almacen, Rapido);
        var ficha = FichasDePrueba.Llena("Uno");
        var cuenta = Cuenta("Duena");

        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(ficha), new RescateDePersonaje(ficha));
        var lectura = cola.PedirAsync("leer a Uno", a => a.LeerPersonaje("Uno"));
        cola.Escribir("anotar el ingreso", a => a.GuardarCuenta(cuenta));
        cola.Escribir("guardar la cuenta", a => a.GuardarCuenta(cuenta), new RescateDeCuenta(cuenta));

        var sinAplicar = cola.Cerrar(TimeSpan.FromMilliseconds(300));

        Assert.Equal(new[] { "guardar a Uno", "leer a Uno", "anotar el ingreso", "guardar la cuenta" }, sinAplicar.Select(p => p.Que));
        Assert.IsType<RescateDePersonaje>(sinAplicar[0].Rescate);
        Assert.Null(sinAplicar[1].Rescate);
        Assert.Null(sinAplicar[2].Rescate);
        Assert.IsType<RescateDeCuenta>(sinAplicar[3].Rescate);
        Assert.True(lectura.IsFaulted);
        Assert.Equal(0, cola.Pendientes);
    }

    /// <summary>Si el escritor quedó TRABADO adentro de la base —una consulta que no vuelve—, el apagado no lo espera: aparta
    /// lo suyo y junta lo que esperaba detrás, en orden.</summary>
    [Fact]
    public void Al_cerrar_con_el_escritor_trabado_igual_devuelve_lo_encolado()
    {
        ConPersonajes("Uno");
        using var suelta = new ManualResetEventSlim(false);
        var (almacen, _) = AlmacenQueFalla.Crear(_real, m =>
        {
            if (m == nameof(IAlmacen.GuardarPersonaje)) suelta.Wait();
            return null;
        });
        var cola = new ColaDeLaBase(almacen, Rapido);
        var uno = FichasDePrueba.Llena("Uno", semilla: 2);
        var cuenta = Cuenta("Duena");

        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(uno), new RescateDePersonaje(uno));
        Assert.True(SpinWait.SpinUntil(() => cola.EsperandoDesde is not null, TimeSpan.FromSeconds(5)));
        cola.Escribir("guardar la cuenta", a => a.GuardarCuenta(cuenta), new RescateDeCuenta(cuenta));

        var sinAplicar = cola.Cerrar(TimeSpan.FromMilliseconds(300));

        Assert.Equal(new[] { "guardar a Uno", "guardar la cuenta" }, sinAplicar.Select(p => p.Que));
        suelta.Set();
    }

    [Fact]
    public void Cada_rescate_va_y_vuelve_igual_por_el_archivo()
    {
        var ficha = FichasDePrueba.Llena("Completo");
        var cuenta = Cuenta("Cuentita");
        cuenta.UltimoIngreso = new DateTime(2026, 9, 28, 13, 15, 0, DateTimeKind.Utc);
        cuenta.UltimaIp = "10.1.2.3";
        cuenta.Characters.Add("Completo");

        FichasDePrueba.AssertIgual(ficha, Assert.IsType<RescateDePersonaje>(Rescates.IdaYVuelta(new RescateDePersonaje(ficha))).Ficha);
        var alta = Assert.IsType<RescateDeAlta>(Rescates.IdaYVuelta(new RescateDeAlta(cuenta, ficha)));
        FichasDePrueba.AssertIgual(ficha, alta.Ficha);
        var vuelta = Assert.IsType<RescateDeCuenta>(Rescates.IdaYVuelta(new RescateDeCuenta(cuenta))).Cuenta;
        Assert.Equal((cuenta.Name, cuenta.PasswordHash, cuenta.UltimoIngreso, cuenta.UltimaIp), (vuelta.Name, vuelta.PasswordHash, vuelta.UltimoIngreso, vuelta.UltimaIp));
        Assert.Equal(cuenta.Characters, vuelta.Characters);

        var clan = ContratoDelAlmacen.ClanLleno("Sur", 1);
        ContratoDelAlmacen.AssertIgual(clan, Assert.Single(Assert.IsType<RescateDeClanes>(Rescates.IdaYVuelta(new RescateDeClanes(new[] { clan }))).Clanes));
        var rechazos = new[] { new KeyValuePair<string, string>("Aldo", "no") };
        Assert.Equal(rechazos, Assert.IsType<RescateDeRechazos>(Rescates.IdaYVuelta(new RescateDeRechazos(rechazos))).Rechazos);
        var casillas = new Dictionary<string, IReadOnlyList<MailEntry>> { ["Aldo"] = new[] { new MailEntry("Bruma", "hola", 2, 1, 1) } };
        Assert.Equal(casillas["Aldo"], Assert.IsType<RescateDeCorreo>(Rescates.IdaYVuelta(new RescateDeCorreo(casillas))).Casillas["Aldo"]);
        var suelo = new[] { new ObjetoEnElSuelo(new WorldPos(1, 50, 50), 12, 3) };
        Assert.Equal(suelo, Assert.IsType<RescateDelSuelo>(Rescates.IdaYVuelta(new RescateDelSuelo(suelo))).Suelo);
        var llaves = new[] { new LlaveVendida(40, 3, 53) };
        Assert.Equal(llaves, Assert.IsType<RescateDeLlaves>(Rescates.IdaYVuelta(new RescateDeLlaves(llaves))).Llaves);
        Assert.Equal(9, Assert.IsType<RescateDelRecord>(Rescates.IdaYVuelta(new RescateDelRecord(9))).Record);
        var ips = new[] { "10.0.0.1", "2001:db8::7" };
        Assert.Equal(ips, Assert.IsType<RescateDeIpsBaneadas>(Rescates.IdaYVuelta(new RescateDeIpsBaneadas(ips))).Ips);
    }

    /// <summary>LAS IPS BANEADAS DE ANTES, las de <c>data/banned-ips.txt</c>: se suman a las de la base sin repetir, una sola
    /// vez, y el archivo queda renombrado.</summary>
    [Fact]
    public void Las_ips_del_archivo_viejo_se_suman_a_la_base_una_vez()
    {
        var db = new Persistence(_real, new Leveling(new Dictionary<int, long> { [1] = 300, [2] = 450 }));
        _real.GuardarIpsBaneadas(new[] { "10.0.0.1" });
        Directory.CreateDirectory(_raiz);
        string archivo = Path.Combine(_raiz, "banned-ips.txt");
        EscrituraAtomica.Escribir(archivo, "10.0.0.1\n 10.0.0.2 \n\n10.0.0.3\n", System.Text.Encoding.UTF8);

        Assert.Equal(2, db.SumarIpsDelArchivoViejo(archivo));

        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" }, db.LoadIpsBaneadas());
        Assert.False(File.Exists(archivo));
        Assert.True(File.Exists(archivo + ".importado"));
        Assert.Equal(0, db.SumarIpsDelArchivoViejo(archivo));
        db.Dispose();
    }

    /// <summary>EL PRÓXIMO ARRANQUE APLICA EL RESCATE antes de cargar el mundo, y deja el archivo como aplicado: una segunda vez
    /// no aplica nada.</summary>
    [Fact]
    public void El_arranque_aplica_el_rescate_y_lo_deja_aplicado()
    {
        var cuenta = ConPersonajes("Viejo");
        var viejo = FichasDePrueba.Llena("Viejo", semilla: 9);
        var nuevo = FichasDePrueba.Llena("Nuevo", semilla: 4);
        var clan = ContratoDelAlmacen.ClanLleno("Sur", 1);
        string? ruta = Rescates.Escribir(_raiz, new (string, Rescate?)[]
        {
            ("guardar a Viejo", new RescateDePersonaje(viejo)),
            ("alta de Nuevo", new RescateDeAlta(cuenta, nuevo)),
            ("anotar el ingreso de Duena", null),
            ("guardar los clanes", new RescateDeClanes(new[] { clan })),
        });
        Assert.NotNull(ruta);

        Assert.Equal(3, Rescates.AplicarLosPendientes(_raiz, _real));

        FichasDePrueba.AssertIgual(viejo, _real.LeerPersonaje("Viejo")!);
        FichasDePrueba.AssertIgual(nuevo, _real.LeerPersonaje("Nuevo")!);
        Assert.Equal(new[] { "Viejo", "Nuevo" }, _real.LeerCuenta("Duena")!.Characters);
        ContratoDelAlmacen.AssertIgual(clan, Assert.Single(_real.LeerClanes()));
        Assert.False(File.Exists(ruta));
        Assert.True(File.Exists(ruta + ".aplicado"));
        Assert.Equal(0, Rescates.AplicarLosPendientes(_raiz, _real));
    }

    /// <summary>Los guardados del juego llevan su rescate: con la base caída al apagar, el personaje, la cuenta y el récord
    /// quedan en el archivo y el próximo arranque los aplica; el último ingreso —un cambio sobre lo guardado— queda anotado
    /// como perdido.</summary>
    [Fact]
    public void Con_la_base_caida_al_apagar_los_guardados_del_juego_quedan_en_el_rescate()
    {
        var cuenta = ConPersonajes("Uno");
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        var db = new Persistence(almacen, new Leveling(new Dictionary<int, long> { [1] = 300, [2] = 450 }));
        control.Falla(_ => Trabada());   // se cae con el servidor andando

        var ficha = FichasDePrueba.Llena("Uno", semilla: 3);
        db.GuardarFicha(ficha);
        db.SaveAccount(cuenta);
        db.AnotarIngreso("Duena", "10.0.0.1");
        db.SaveRecordUsuarios(7);
        db.Cerrar(_raiz, TimeSpan.FromMilliseconds(300));

        var archivo = Assert.Single(Directory.GetFiles(_raiz, "rescate-*.json"));
        Assert.Contains("anotar el ingreso de Duena", File.ReadAllText(archivo));
        Assert.Equal(3, Rescates.AplicarLosPendientes(_raiz, _real));
        FichasDePrueba.AssertIgual(ficha, _real.LeerPersonaje("Uno")!);
        Assert.Equal(7, _real.LeerRecord());
    }

    /// <summary>Un alta que llegó a la base antes de rendirse no se vuelve a crear: se guarda.</summary>
    [Fact]
    public void Un_alta_rescatada_que_ya_habia_entrado_se_guarda()
    {
        var cuenta = ConPersonajes("Nuevo");
        var nuevo = FichasDePrueba.Llena("Nuevo", semilla: 6);

        new RescateDeAlta(cuenta, nuevo).Aplicar(_real);

        FichasDePrueba.AssertIgual(nuevo, _real.LeerPersonaje("Nuevo")!);
        Assert.Single(_real.LeerCuenta("Duena")!.Characters);
    }

    /// <summary>
    /// CON LA BASE YA CAÍDA, EL APAGADO NO LA ESPERA. Cerrar la ventana en Windows le da al proceso unos 5 s antes de matarlo:
    /// esperar los 30 del plazo era morir sin escribir el rescate, y perder todo lo que esperaba desde que se cayó.
    /// </summary>
    [Fact]
    public void Con_la_base_caida_el_apagado_no_espera_el_plazo()
    {
        var (almacen, _) = AlmacenQueFalla.Crear(_real, _ => Trabada());
        var cola = new ColaDeLaBase(almacen, Rapido);
        var ficha = FichasDePrueba.Llena("Uno");
        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(ficha), new RescateDePersonaje(ficha));
        Assert.True(SpinWait.SpinUntil(() => cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));

        var reloj = Stopwatch.StartNew();
        var sinAplicar = cola.Cerrar(TimeSpan.FromSeconds(30));

        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3), $"esperó {reloj.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("guardar a Uno", Assert.Single(sinAplicar).Que);
    }

    /// <summary>Y si se cae MIENTRAS el apagado espera —estaba andando, lenta—, se rinde en ese momento.</summary>
    [Fact]
    public void Si_la_base_se_cae_mientras_el_apagado_espera_se_rinde_enseguida()
    {
        ConPersonajes("Uno");
        var (almacen, _) = AlmacenQueFalla.Crear(_real, m =>
        {
            if (m != nameof(IAlmacen.GuardarPersonaje)) return null;
            Thread.Sleep(300);
            return Trabada();
        });
        var cola = new ColaDeLaBase(almacen, Rapido);
        var ficha = FichasDePrueba.Llena("Uno", semilla: 2);
        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(ficha), new RescateDePersonaje(ficha));
        Assert.True(SpinWait.SpinUntil(() => cola.EsperandoDesde is not null, TimeSpan.FromSeconds(5)));
        Assert.Null(cola.CaidaDesde);   // todavía anda: el primer intento está en curso

        var reloj = Stopwatch.StartNew();
        var sinAplicar = cola.Cerrar(TimeSpan.FromSeconds(30));

        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3), $"esperó {reloj.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("guardar a Uno", Assert.Single(sinAplicar).Que);
    }

    /// <summary>El apagado del servidor, con el plazo de siempre: con la base caída el rescate queda escrito enseguida.</summary>
    [Fact]
    public void Con_la_base_caida_el_rescate_queda_escrito_sin_esperar_el_plazo()
    {
        ConPersonajes("Uno");
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        var db = new Persistence(almacen, Niveles());
        control.Falla(_ => Trabada());
        db.GuardarFicha(FichasDePrueba.Llena("Uno", semilla: 3));
        Assert.True(SpinWait.SpinUntil(() => db.Cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));

        var reloj = Stopwatch.StartNew();
        db.Cerrar(_raiz);

        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3), $"esperó {reloj.Elapsed.TotalSeconds:0.0} s");
        Assert.Single(Directory.GetFiles(_raiz, "rescate-*.json"));
    }

    /// <summary>
    /// LO QUE NO SE PUEDE APLICAR NO FRENA EL ARRANQUE: un personaje que ya no está —se borró, o se restauró un backup de
    /// antes—, un alta a una cuenta que ya se llenó. Queda en el log y lo demás se aplica. Si tirara, el servidor no
    /// arrancaría nunca más, porque el archivo sigue ahí en cada intento.
    /// </summary>
    [Fact]
    public void Un_rescate_que_no_se_puede_aplicar_no_frena_el_arranque()
    {
        var cuenta = ConPersonajes("Uno", "Dos", "Tres", "Cuatro", "Cinco", "Seis", "Siete", "Ocho", "Nueve", "Diez");
        var uno = FichasDePrueba.Llena("Uno", semilla: 5);
        string? ruta = Rescates.Escribir(_raiz, new (string, Rescate?)[]
        {
            ("guardar a Fantasma", new RescateDePersonaje(FichasDePrueba.Llena("Fantasma", semilla: 6))),
            ("alta de Once", new RescateDeAlta(cuenta, FichasDePrueba.Llena("Once", semilla: 7))),
            ("guardar a Uno", new RescateDePersonaje(uno)),
        });

        Assert.Equal(1, Rescates.AplicarLosPendientes(_raiz, _real));

        FichasDePrueba.AssertIgual(uno, _real.LeerPersonaje("Uno")!);
        Assert.False(_real.ExistePersonaje("Once"));
        Assert.True(File.Exists(ruta + ".aplicado"));
    }

    /// <summary>Con la base todavía caída al arrancar, el rescate no se da por aplicado: el servidor no arranca y el archivo
    /// queda para el próximo intento.</summary>
    [Fact]
    public void Con_la_base_caida_al_arrancar_el_rescate_queda_para_el_proximo_intento()
    {
        ConPersonajes("Uno");
        string? ruta = Rescates.Escribir(_raiz, new (string, Rescate?)[] { ("guardar a Uno", new RescateDePersonaje(FichasDePrueba.Llena("Uno", 8))) });
        var (almacen, _) = AlmacenQueFalla.Crear(_real, _ => Trabada());

        Assert.ThrowsAny<Exception>(() => Rescates.AplicarLosPendientes(_raiz, almacen));

        Assert.True(File.Exists(ruta));
    }

    /// <summary>UNA SUMA QUE SE REINTENTA NO SE SUMA DOS VECES: la base acreditó y la respuesta se perdió; el reintento lo ve y
    /// no vuelve a sumar.</summary>
    [Fact]
    public async Task Una_acreditacion_que_entro_sin_respuesta_no_se_suma_dos_veces()
    {
        ConPersonajes("Uno");
        long antes = _real.LeerPersonaje("Uno")!.OroEnBanco;
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        var db = new Persistence(almacen, Niveles());
        int una = 1;
        control.FallaDespues((m, args) => m == nameof(IAlmacen.AcreditarBoveda) && (long)args[1]! != 0
                                          && Interlocked.Exchange(ref una, 0) == 1 ? Trabada() : null);

        Assert.Equal(antes + 300, await db.AcreditarBovedaAsync("Uno", 300));

        Assert.Equal(antes + 300, _real.LeerPersonaje("Uno")!.OroEnBanco);
        db.Dispose();
    }

    /// <summary>LA TRANSFERENCIA A UN DESCONECTADO NO PIERDE EL ORO SI LA BASE NO RESPONDE AL APAGAR: la suma va al rescate y el
    /// que esperaba no se entera (devolverle el oro al que la mandó lo duplicaría). Y aplicar el mismo rescate otra vez —la
    /// base se cayó antes de dejarlo como aplicado— no suma de nuevo.</summary>
    [Fact]
    public void Con_la_base_caida_al_apagar_la_acreditacion_queda_en_el_rescate()
    {
        ConPersonajes("Uno");
        long antes = _real.LeerPersonaje("Uno")!.OroEnBanco;
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        var db = new Persistence(almacen, Niveles());
        control.Falla(_ => Trabada());

        var credito = db.AcreditarBovedaAsync("Uno", 300);
        db.Cerrar(_raiz, TimeSpan.FromMilliseconds(300));

        Assert.False(credito.IsCompleted);
        string archivo = Assert.Single(Directory.GetFiles(_raiz, "rescate-*.json"));
        Assert.Equal(1, Rescates.AplicarLosPendientes(_raiz, _real));
        Assert.Equal(antes + 300, _real.LeerPersonaje("Uno")!.OroEnBanco);

        File.Move(archivo + ".aplicado", archivo);
        Assert.Equal(1, Rescates.AplicarLosPendientes(_raiz, _real));
        Assert.Equal(antes + 300, _real.LeerPersonaje("Uno")!.OroEnBanco);
    }

    /// <summary>Y la suma que llegó a la base, con la respuesta perdida justo antes de rendirse, el rescate la reconoce por el
    /// saldo de antes y no la repite.</summary>
    [Fact]
    public void Una_acreditacion_que_entro_antes_de_apagar_no_se_repite_con_el_rescate()
    {
        ConPersonajes("Uno");
        long antes = _real.LeerPersonaje("Uno")!.OroEnBanco;
        int caida = 0;
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => Volatile.Read(ref caida) == 1 ? Trabada() : null);
        var db = new Persistence(almacen, Niveles());
        control.FallaDespues((m, args) => m == nameof(IAlmacen.AcreditarBoveda) && (long)args[1]! != 0
                                          && Interlocked.Exchange(ref caida, 1) == 0 ? Trabada() : null);

        db.AcreditarBovedaAsync("Uno", 300);
        Assert.True(SpinWait.SpinUntil(() => db.Cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
        db.Cerrar(_raiz, TimeSpan.FromMilliseconds(300));

        Assert.Equal(antes + 300, _real.LeerPersonaje("Uno")!.OroEnBanco);
        Assert.Equal(1, Rescates.AplicarLosPendientes(_raiz, _real));
        Assert.Equal(antes + 300, _real.LeerPersonaje("Uno")!.OroEnBanco);
    }

    /// <summary>Lo mismo con un cambio de cuenta: la trampa que se suma, el donador que se da vuelta.</summary>
    [Fact]
    public async Task Un_cambio_de_cuenta_que_entro_sin_respuesta_no_se_aplica_dos_veces()
    {
        ConPersonajes("Uno");
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        var db = new Persistence(almacen, Niveles());
        int una = 1;
        control.FallaDespues((m, _) => m == nameof(IAlmacen.GuardarCuenta) && Interlocked.Exchange(ref una, 0) == 1 ? Trabada() : null);

        var cambiada = await db.CambiarCuentaAsync("Duena", c => { c.Trampas++; c.Donador = !c.Donador; });

        Assert.Equal((1, true), (cambiada!.Trampas, cambiada.Donador));
        Assert.Equal((1, true), (_real.LeerCuenta("Duena")!.Trampas, _real.LeerCuenta("Duena")!.Donador));
        db.Dispose();
    }

    /// <summary>Y con un alta: el reintento de una que entró —en la cuenta que quedó con diez— no la da por rechazada, ni le
    /// saca el nombre al índice.</summary>
    [Fact]
    public void Un_alta_que_entro_sin_respuesta_no_se_da_por_rechazada()
    {
        var cuenta = ConPersonajes("Uno", "Dos", "Tres", "Cuatro", "Cinco", "Seis", "Siete", "Ocho", "Nueve");
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        var db = new Persistence(almacen, Niveles());
        int una = 1;
        control.FallaDespues((m, _) => m == nameof(IAlmacen.CrearPersonaje) && Interlocked.Exchange(ref una, 0) == 1 ? Trabada() : null);
        var diez = new Player { CharIndex = 1, Name = "Diez", Account = cuenta.Name, Pos = new WorldPos(1, 50, 50) };
        cuenta.Characters.Add("Diez");

        db.AltaDe(diez, cuenta);
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));

        Assert.True(_real.ExistePersonaje("Diez"));
        Assert.True(db.CharacterExists("Diez"), "el reintento le sacó el nombre al índice");
        db.Dispose();
    }

    [Fact]
    public void Una_conexion_que_no_se_pudo_volver_a_abrir_es_que_la_base_no_responde()
    {
        Assert.True(ColaDeLaBase.EsQueLaBaseNoResponde(new LaBaseNoResponde("no se pudo volver a conectar")));
        Assert.False(ColaDeLaBase.EsQueLaBaseNoResponde(new InvalidOperationException("un dato que no entra")));
    }
}
