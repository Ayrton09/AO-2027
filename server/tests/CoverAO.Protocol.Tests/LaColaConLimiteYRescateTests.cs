using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

public sealed class LaColaConLimiteYRescateTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_cola_limite_" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenSqlite _almacen = AlmacenSqlite.EnMemoria();
    private static readonly TimeSpan[] Rapido = { TimeSpan.FromMilliseconds(20) };

    public void Dispose()
    {
        _almacen.Dispose();
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }

    [Fact]
    public void Veinte_mil_guardados_superados_se_sacan_realmente_de_la_cola()
    {
        using var tomado = new ManualResetEventSlim();
        using var soltar = new ManualResetEventSlim();
        using var cola = new ColaDeLaBase(_almacen, Rapido, topeDePedidos: 8);
        cola.Escribir("ocupado", _ => { tomado.Set(); soltar.Wait(); });
        try
        {
            Assert.True(tomado.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 1; i <= 20_000; i++)
            {
                int valor = i;
                cola.Escribir("récord", a => a.GuardarRecord(valor), new RescateDelRecord(valor), clave: "record");
            }
            Assert.Equal(2, cola.Pendientes); // el que corre y el último; no veinte mil marcas esperando al escritor
            Assert.Equal(19_999, cola.Superados);
            Assert.False(cola.Saturada);
        }
        finally { soltar.Set(); }
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(5)));
        Assert.Equal(20_000, _almacen.LeerRecord());
    }

    [Fact]
    public void El_limite_avisa_una_vez_y_conserva_las_escrituras_de_acciones_en_vuelo()
    {
        using var tomado = new ManualResetEventSlim();
        using var soltar = new ManualResetEventSlim();
        using var cola = new ColaDeLaBase(_almacen, Rapido, topeDePedidos: 5);
        int avisos = 0, aplicadas = 0;
        cola.LimiteAlcanzado += () => Interlocked.Increment(ref avisos);
        cola.Escribir("ocupado", _ => { tomado.Set(); soltar.Wait(); });
        try
        {
            Assert.True(tomado.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 12; i++) cola.Escribir("operación en vuelo", _ => Interlocked.Increment(ref aplicadas));
            Assert.True(cola.Saturada);
            Assert.Equal(1, avisos);
        }
        finally { soltar.Set(); }
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(5)));
        Assert.Equal(12, aplicadas);
        Assert.Equal(1, avisos);
    }

    [Fact]
    public void Si_no_se_puede_reescribir_el_rescate_no_se_borra_ni_se_ejecuta_la_suma()
    {
        using var entrar = new ManualResetEventSlim();
        using var intentada = new ManualResetEventSlim();
        bool caida = true;
        int sumas = 0;
        using var cola = new ColaDeLaBase(_almacen, Rapido, carpetaDelRescate: _raiz, cadaVolcado: TimeSpan.FromMilliseconds(20));
        cola.Escribir("suma", _ =>
        {
            entrar.Wait();
            if (Volatile.Read(ref caida)) throw new LaBaseNoResponde("caída de prueba");
            intentada.Set();
            cola.LaBaseResponde();
            Interlocked.Increment(ref sumas);
        }, new RescateDelRecord(10));
        cola.Escribir("pendiente detrás", a => a.GuardarRecord(20), new RescateDelRecord(20));
        entrar.Set();
        Assert.True(SpinWait.SpinUntil(() => cola.Volcado is not null, TimeSpan.FromSeconds(5)));
        string archivo = cola.Volcado!;
        string anterior = File.ReadAllText(archivo);
        string temporalBloqueado = archivo + ".tmp";
        // Un directorio en el lugar del temporal impide escribir, sin depender de permisos del SO o del usuario de tests.
        Directory.CreateDirectory(temporalBloqueado);
        try
        {
            Volatile.Write(ref caida, false);
            Assert.True(intentada.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(100);
            Assert.Equal(0, Volatile.Read(ref sumas));
            Assert.True(File.Exists(archivo));
            Assert.Equal(anterior, File.ReadAllText(archivo));
        }
        finally { Directory.Delete(temporalBloqueado); }
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, sumas);
        Assert.Equal(20, _almacen.LeerRecord());
        Assert.True(SpinWait.SpinUntil(() => !File.Exists(archivo), TimeSpan.FromSeconds(5)),
            "el escritor todavía no retiró el rescate después de completar el vaciado");
    }

    [Fact]
    public async Task La_foto_y_su_limite_excluyen_juntos_los_pedidos_que_entran_despues()
    {
        using var foto = new ManualResetEventSlim();
        using var terminarFoto = new ManualResetEventSlim();
        bool caida = true;
        using var cola = new ColaDeLaBase(_almacen, Rapido, carpetaDelRescate: _raiz);
        var primero = cola.PedirAsync("primero", a =>
        {
            if (Volatile.Read(ref caida)) throw new LaBaseNoResponde("caída de prueba");
            a.GuardarRecord(1);
            return 1;
        }, rescate: _ =>
        {
            foto.Set();
            terminarFoto.Wait();
            return new RescateDelRecord(1);
        });
        Assert.True(foto.Wait(TimeSpan.FromSeconds(5)));
        var posterior = Task.Run(() => cola.Escribir("posterior", a => a.GuardarRecord(2), new RescateDelRecord(2)));
        try
        {
            // Mientras se toma la foto ningún productor puede avanzar el número sin quedar dentro de esa misma foto.
            Assert.NotSame(posterior, await Task.WhenAny(posterior, Task.Delay(100)));
        }
        finally { terminarFoto.Set(); }
        await posterior.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref caida, false);
        Assert.Equal(1, await primero.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, _almacen.LeerRecord());
    }

    [Fact]
    public void Un_rescate_aplicado_que_no_se_puede_borrar_frena_los_guardados_posteriores()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix permite borrar un archivo abierto; el bloqueo es del hosting Windows.
        bool caida = true;
        int posteriores = 0;
        using var cola = new ColaDeLaBase(_almacen, Rapido, carpetaDelRescate: _raiz);
        cola.Escribir("primero", a =>
        {
            if (Volatile.Read(ref caida)) throw new LaBaseNoResponde("caída de prueba");
            a.GuardarRecord(1);
        }, new RescateDelRecord(1));
        Assert.True(SpinWait.SpinUntil(() => cola.Volcado is not null, TimeSpan.FromSeconds(5)));
        string archivo = cola.Volcado!;
        using (var abierto = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            cola.Escribir("posterior a la foto", a =>
            {
                a.GuardarRecord(2);
                Interlocked.Increment(ref posteriores);
            }, new RescateDelRecord(2));
            Volatile.Write(ref caida, false);
            Assert.True(SpinWait.SpinUntil(() => _almacen.LeerRecord() == 1, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => cola.Pendientes == 1 && cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref posteriores));
            Assert.True(File.Exists(archivo));
        }
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, posteriores);
        Assert.Equal(2, _almacen.LeerRecord());
        Assert.True(SpinWait.SpinUntil(() => !File.Exists(archivo), TimeSpan.FromSeconds(5)),
            "el escritor todavía no retiró el rescate después de completar el vaciado");
    }

    [Fact]
    public void El_apagado_con_un_rescate_aplicado_abierto_guarda_lo_pendiente_despues_del_viejo()
    {
        if (!OperatingSystem.IsWindows()) return;
        bool caida = true;
        using var cola = new ColaDeLaBase(_almacen, Rapido, carpetaDelRescate: _raiz);
        cola.Escribir("primero", a =>
        {
            if (Volatile.Read(ref caida)) throw new LaBaseNoResponde("caída de prueba");
            a.GuardarRecord(1);
        }, new RescateDelRecord(1));
        Assert.True(SpinWait.SpinUntil(() => cola.Volcado is not null, TimeSpan.FromSeconds(5)));
        string viejo = cola.Volcado!;
        string? nuevo;
        using (var abierto = new FileStream(viejo, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            cola.Escribir("posterior a la foto", a => a.GuardarRecord(2), new RescateDelRecord(2));
            Volatile.Write(ref caida, false);
            Assert.True(SpinWait.SpinUntil(() => _almacen.LeerRecord() == 1, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => cola.Pendientes == 1 && cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
            var pendientes = cola.Cerrar(TimeSpan.FromSeconds(5));
            Assert.Single(pendientes);
            nuevo = cola.EscribirElRescate(_raiz, pendientes);
            Assert.NotNull(nuevo);
            Assert.NotEqual(viejo, nuevo);
            Assert.Equal(new[] { viejo, nuevo }, Directory.GetFiles(_raiz, "rescate-*.json").Order(StringComparer.Ordinal));
        }
        using var reinicio = AlmacenSqlite.EnMemoria();
        Assert.Equal(2, Rescates.AplicarLosPendientes(_raiz, reinicio));
        Assert.Equal(2, reinicio.LeerRecord());
    }

    [Fact]
    public void Las_fichas_finales_tras_rendirse_van_al_rescate_sin_volver_a_tocar_la_base()
    {
        int llamadasTardias = 0;
        using var cola = new ColaDeLaBase(_almacen, Rapido, carpetaDelRescate: _raiz);
        cola.Escribir("primero", _ => throw new LaBaseNoResponde("caída de prueba"), new RescateDelRecord(1));
        Assert.True(SpinWait.SpinUntil(() => cola.CaidaDesde is not null, TimeSpan.FromSeconds(5)));
        Assert.Single(cola.Cerrar(TimeSpan.Zero));
        cola.Escribir("ficha final", a =>
        {
            Interlocked.Increment(ref llamadasTardias);
            a.GuardarRecord(2);
        }, new RescateDelRecord(2));
        var todo = cola.Cerrar(TimeSpan.Zero);
        Assert.Equal(0, llamadasTardias);
        Assert.Equal(2, todo.Count);
        cola.EscribirElRescate(_raiz, todo);
        using var reinicio = AlmacenSqlite.EnMemoria();
        Assert.Equal(2, Rescates.AplicarLosPendientes(_raiz, reinicio));
        Assert.Equal(2, reinicio.LeerRecord());
    }
}
