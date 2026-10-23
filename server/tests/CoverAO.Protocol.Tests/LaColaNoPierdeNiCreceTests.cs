using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA COLA DE LA BASE CON LA BASE CAÍDA (auditoría externa del 23/10): el volcado de rescate se queda hasta que se aplica todo lo
/// que cubre, los guardados viejos de lo mismo no se acumulan, y lo que va junto entra entero o no entra.
/// </summary>
public sealed class LaColaNoPierdeNiCreceTests : IDisposable
{
    private static readonly TimeSpan[] Rapido = { TimeSpan.FromMilliseconds(5) };
    private readonly AlmacenSqlite _real = AlmacenSqlite.EnMemoria();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_cola_" + Guid.NewGuid().ToString("N"));
    private volatile bool _caida;

    public void Dispose()
    {
        _real.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }

    private static Exception Trabada() => new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
    private static Leveling Niveles() => new(new Dictionary<int, long> { [1] = 300, [2] = 450 });

    private void ConPersonajes(params string[] nombres)
    {
        var cuenta = new Account { Name = "Duena", PasswordHash = "pbkdf2$1$a$b", Salt = "" };
        _real.GuardarCuenta(cuenta);
        foreach (var n in nombres) Assert.True(_real.CrearPersonaje(cuenta, FichasDePrueba.Llena(n)));
    }

    private IAlmacen QueSeCae() => AlmacenQueFalla.Crear(_real, _ => _caida ? Trabada() : null).Almacen;

    private string[] Volcados() => Directory.Exists(_raiz) ? Directory.GetFiles(_raiz, "rescate-*.json") : Array.Empty<string>();

    private static string Leer(string archivo)
    {
        try { return File.ReadAllText(archivo); }
        catch (IOException) { return ""; }
    }

    /// <summary>
    /// UNA SEGUNDA CAÍDA, O UN CORTE, DESPUÉS DE RECUPERAR SÓLO UNA OPERACIÓN. El volcado cubría dos guardados; la base volvió,
    /// entró el primero y el segundo todavía no. Antes el volcado se borraba con el primero, y si el proceso moría en ese rato el
    /// segundo, que estaba sólo en memoria, se perdía.
    /// </summary>
    [Fact]
    public void El_volcado_se_queda_hasta_que_entra_lo_ultimo_que_cubre()
    {
        ConPersonajes("Uno", "Dos");
        var uno = FichasDePrueba.Llena("Uno", semilla: 5);
        var dos = FichasDePrueba.Llena("Dos", semilla: 6);
        using var soltar = new ManualResetEventSlim(false);
        _caida = true;
        using var cola = new ColaDeLaBase(QueSeCae(), Rapido, carpetaDelRescate: _raiz, cadaVolcado: TimeSpan.FromMilliseconds(20));
        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(uno), new RescateDePersonaje(uno));
        cola.Escribir("guardar a Dos", a => { soltar.Wait(); a.GuardarPersonaje(dos); }, new RescateDePersonaje(dos));
        Assert.True(SpinWait.SpinUntil(() => Volcados() is [var v] && Leer(v).Contains("\"Dos\""), TimeSpan.FromSeconds(5)));

        _caida = false;   // vuelve: entra Uno, y Dos se queda esperando
        Assert.True(SpinWait.SpinUntil(() => cola.Pendientes == 1 && cola.CaidaDesde is null, TimeSpan.FromSeconds(5)));
        var volcado = Assert.Single(Volcados());
        Assert.Contains("\"Dos\"", Leer(volcado));

        // El proceso muere acá: el próximo arranque aplica el volcado, y Dos no se pierde. Uno, aplicado de nuevo, queda igual.
        string otra = Path.Combine(_raiz, "arranque");
        Directory.CreateDirectory(otra);
        File.Copy(volcado, Path.Combine(otra, Path.GetFileName(volcado)));
        using (var despues = AlmacenSqlite.EnMemoria())
        {
            var cuenta = new Account { Name = "Duena", PasswordHash = "pbkdf2$1$a$b", Salt = "" };
            despues.GuardarCuenta(cuenta);
            despues.CrearPersonaje(cuenta, uno);
            despues.CrearPersonaje(cuenta, FichasDePrueba.Llena("Dos"));
            Assert.Equal(2, Rescates.AplicarLosPendientes(otra, despues));
            FichasDePrueba.AssertIgual(dos, despues.LeerPersonaje("Dos")!);
            FichasDePrueba.AssertIgual(uno, despues.LeerPersonaje("Uno")!);
        }

        soltar.Set();
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => Volcados().Length == 0, TimeSpan.FromSeconds(5)), "aplicado todo, el volcado tenía que irse");
        FichasDePrueba.AssertIgual(dos, _real.LeerPersonaje("Dos")!);
    }

    /// <summary>
    /// CON LA BASE CAÍDA, LOS GUARDADOS DE UN MISMO PERSONAJE NO SE ACUMULAN: cada autoguardado sumaba una ficha por conectado,
    /// sin tope, mientras durara la caída. Queda el que está reintentándose y el último; al volver, entra el último.
    /// </summary>
    [Fact]
    public void Con_la_base_caida_los_guardados_viejos_de_lo_mismo_no_se_acumulan()
    {
        ConPersonajes("Uno");
        using var db = new Persistence(QueSeCae(), Niveles());   // el arranque lee la base: se cae después
        _caida = true;
        FichaDePersonaje ultima = null!;
        for (int i = 1; i <= 200; i++) db.GuardarFicha(ultima = FichasDePrueba.Llena("Uno", semilla: i));
        Assert.True(SpinWait.SpinUntil(() => db.Cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
        Assert.True(db.Cola.Superados >= 198, $"superados: {db.Cola.Superados}");

        _caida = false;
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        FichasDePrueba.AssertIgual(ultima, _real.LeerPersonaje("Uno")!);
    }

    /// <summary>Un guardado que una lectura tiene que ver no se descarta: entre el viejo y el nuevo hay alguien leyendo.</summary>
    [Fact]
    public async Task Lo_que_una_lectura_tiene_que_ver_no_se_descarta()
    {
        ConPersonajes("Uno");
        using var db = new Persistence(QueSeCae(), Niveles());   // el arranque lee la base: se cae después
        _caida = true;
        db.GuardarFicha(FichasDePrueba.Llena("Uno", semilla: 1));   // el que se queda reintentando
        // Ya tomado por el escritor: si no, el v2 lo supera (y está bien), y el conteo de abajo mediría eso.
        Assert.True(SpinWait.SpinUntil(() => db.Cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
        var v2 = FichasDePrueba.Llena("Uno", semilla: 2);
        db.GuardarFicha(v2);
        var leida = db.LeerPersonajeAsync("Uno");
        var v3 = FichasDePrueba.Llena("Uno", semilla: 3);
        db.GuardarFicha(v3);

        _caida = false;
        FichasDePrueba.AssertIgual(v2, (await leida)!);
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        FichasDePrueba.AssertIgual(v3, _real.LeerPersonaje("Uno")!);
        Assert.Equal(0, db.Cola.Superados);
    }

    /// <summary>Lo que va junto no se descarta por un guardado más nuevo de una de sus partes: la otra quedaría sin guardar.</summary>
    [Fact]
    public void Lo_que_va_junto_no_lo_descarta_un_guardado_mas_nuevo()
    {
        ConPersonajes("Uno", "Dos");
        using var db = new Persistence(QueSeCae(), Niveles());   // el arranque lee la base: se cae después
        _caida = true;
        db.GuardarFicha(FichasDePrueba.Llena("Dos", semilla: 9));   // el que se queda reintentando
        Assert.True(SpinWait.SpinUntil(() => db.Cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
        var unoJunto = FichasDePrueba.Llena("Uno", semilla: 4);
        var dosJunto = FichasDePrueba.Llena("Dos", semilla: 4);
        db.GuardarJuntos("los dos", new RescateDePersonaje(unoJunto), new RescateDePersonaje(dosJunto));
        var unoDespues = FichasDePrueba.Llena("Uno", semilla: 7);
        db.GuardarFicha(unoDespues);

        _caida = false;
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        FichasDePrueba.AssertIgual(unoDespues, _real.LeerPersonaje("Uno")!);
        FichasDePrueba.AssertIgual(dosJunto, _real.LeerPersonaje("Dos")!);
    }

    /// <summary>LO QUE VA JUNTO ENTRA ENTERO O NO ENTRA: si la segunda parte falla, la primera no queda guardada.</summary>
    [Fact]
    public void Lo_que_va_junto_y_falla_a_la_mitad_no_deja_nada()
    {
        ConPersonajes("Uno", "Dos");
        var antesUno = _real.LeerPersonaje("Uno")!;
        int n = 0;
        var (almacen, _) = AlmacenQueFalla.Crear(_real,
            m => m == nameof(IAlmacen.GuardarPersonaje) && Interlocked.Increment(ref n) == 2 ? new InvalidOperationException("no entra") : null);
        using var db = new Persistence(almacen, Niveles());
        db.GuardarJuntos("los dos", new RescateDePersonaje(FichasDePrueba.Llena("Uno", semilla: 4)),
            new RescateDePersonaje(FichasDePrueba.Llena("Dos", semilla: 4)));
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        FichasDePrueba.AssertIgual(antesUno, _real.LeerPersonaje("Uno")!);
    }

    /// <summary>Si la base se cae en el medio, se reintenta entero: la transacción se deshizo y la segunda vez entra todo.</summary>
    [Fact]
    public void Lo_que_va_junto_y_se_cae_a_la_mitad_se_reintenta_entero()
    {
        ConPersonajes("Uno", "Dos");
        int n = 0;
        var (almacen, _) = AlmacenQueFalla.Crear(_real,
            m => m == nameof(IAlmacen.GuardarPersonaje) && Interlocked.Increment(ref n) == 2 ? Trabada() : null);
        using var db = new Persistence(almacen, Niveles());
        var uno = FichasDePrueba.Llena("Uno", semilla: 4);
        var dos = FichasDePrueba.Llena("Dos", semilla: 4);
        db.GuardarJuntos("los dos", new RescateDePersonaje(uno), new RescateDePersonaje(dos));
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        FichasDePrueba.AssertIgual(uno, _real.LeerPersonaje("Uno")!);
        FichasDePrueba.AssertIgual(dos, _real.LeerPersonaje("Dos")!);
    }

    /// <summary>LA TRANSFERENCIA A UN AUSENTE QUE SE CORTA DESPUÉS DE SUMAR se reintenta y suma una sola vez: la transacción se
    /// deshace entera con el corte.</summary>
    [Fact]
    public async Task La_transferencia_que_se_corta_despues_de_sumar_suma_una_sola_vez()
    {
        ConPersonajes("Uno", "Dos");
        long antes = _real.LeerPersonaje("Dos")!.OroEnBanco;
        var (almacen, control) = AlmacenQueFalla.Crear(_real, _ => null);
        int cortes = 0;
        control.FallaDespues((m, args) =>
            m == nameof(IAlmacen.AcreditarBoveda) && (long)args[1]! != 0 && Interlocked.Increment(ref cortes) == 1 ? Trabada() : null);
        using var db = new Persistence(almacen, Niveles());
        var emisor = FichasDePrueba.Llena("Uno", semilla: 8).CrearPlayer(0, "duena");
        emisor.Bank = 1_234;
        Assert.Equal(antes + 500, await db.TransferirABovedaAsync(emisor, "Dos", 500));
        Assert.Equal(antes + 500, _real.LeerPersonaje("Dos")!.OroEnBanco);
        Assert.Equal(1_234, _real.LeerPersonaje("Uno")!.OroEnBanco);
    }

    /// <summary>El rescate de lo que va junto viaja entero por el archivo, y el arranque le anota el saldo a la suma.</summary>
    [Fact]
    public void El_rescate_de_lo_que_va_junto_va_y_vuelve_por_el_archivo()
    {
        ConPersonajes("Uno", "Dos");
        long antes = _real.LeerPersonaje("Dos")!.OroEnBanco;
        var junto = new RescateJunto(new Rescate[]
        {
            new RescateDePersonaje(FichasDePrueba.Llena("Uno", semilla: 3)), new RescateDeAcreditacion("Dos", 700, null),
        });
        var vuelta = Assert.IsType<RescateJunto>(Rescates.IdaYVuelta(junto));
        Assert.Equal(2, vuelta.Partes.Count);
        Rescates.Escribir(_raiz, new[] { ("los dos", (Rescate?)vuelta) });
        Assert.Equal(1, Rescates.AplicarLosPendientes(_raiz, _real));
        Assert.Equal(antes + 700, _real.LeerPersonaje("Dos")!.OroEnBanco);
        // Aplicado otra vez (el arranque murió después de aplicarlo y antes de renombrarlo): la suma no entra de nuevo.
        foreach (var f in Directory.GetFiles(_raiz, "*.aplicado")) File.Move(f, f[..^".aplicado".Length]);
        Rescates.AplicarLosPendientes(_raiz, _real);
        Assert.Equal(antes + 700, _real.LeerPersonaje("Dos")!.OroEnBanco);
    }
}
