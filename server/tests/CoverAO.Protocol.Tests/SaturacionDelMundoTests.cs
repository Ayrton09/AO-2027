using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class SaturacionDelMundoTests
{
    [Fact]
    public async Task El_limite_termina_la_accion_actual_y_solo_deja_pasar_continuaciones()
    {
        using var almacen = AlmacenSqlite.EnMemoria();
        using var tomado = new ManualResetEventSlim();
        using var soltar = new ManualResetEventSlim();
        using var cola = new ColaDeLaBase(almacen, new[] { TimeSpan.FromMilliseconds(5) }, topeDePedidos: 3);
        cola.Escribir("ocupado", _ => { tomado.Set(); soltar.Wait(); });
        Assert.True(tomado.Wait(TimeSpan.FromSeconds(5)));
        var loop = new GameLoop { PuedeProcesar = () => !cola.Saturada };
        int accionesNuevas = 0, ticks = 0, terminadas = 0;
        loop.OnTickHandler = _ => ticks++;
        loop.Enqueue(() =>
        {
            cola.Escribir("primera mitad", _ => Interlocked.Increment(ref terminadas));
            cola.Escribir("segunda mitad", _ => Interlocked.Increment(ref terminadas));
            // La misma acción conserva sus escrituras restantes aunque ya haya disparado el límite.
            cola.Escribir("cierre de la acción", _ => Interlocked.Increment(ref terminadas));
        });
        loop.Enqueue(() => accionesNuevas++);
        var continuada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.EnqueueContinuacion(() => continuada.SetResult());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var corriendo = loop.RunAsync(cts.Token);
        try
        {
            await continuada.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(cola.Saturada);
            Assert.Equal(0, accionesNuevas);
            Assert.Equal(0, ticks);
        }
        finally
        {
            soltar.Set();
            cts.Cancel();
            await corriendo;
        }
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, terminadas);
    }

    private sealed class Transporte : ITransport
    {
        public readonly TaskCompletionSource Devuelto = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string RemoteEndpoint => "tcp:10.0.0.7:1111";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data)
        {
            if (data.Length == 0 || data[0] != (byte)ServerPacketId.ConsoleMsg) return;
            var r = new ByteQueueReader(data);
            r.ReadByte();
            if (r.ReadAsciiString().Contains("No se pudo hacer la transferencia.")) Devuelto.TrySetResult();
        }
        public void Close(string reason) { }
    }

    [Fact]
    public async Task Una_transferencia_fallida_devuelve_el_oro_aunque_haya_saturado_la_cola()
    {
        var datos = GameData.Load();
        using var real = AlmacenSqlite.EnMemoria();
        var (almacen, control) = AlmacenQueFalla.Crear(real, _ => null);
        var niveles = new Leveling(datos.LevelExp);
        using var db = new Persistence(almacen, niveles);
        var cuenta = db.CreateAccount("cuenta", "clave_larga_de_prueba");
        var emisor = db.CreateCharacter(cuenta, "Emisor", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        db.CreateCharacter(cuenta, "Ausente", 2, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        emisor.Bank = 5_000;
        db.SaveCharacter(emisor);
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        var mundo = new World();
        mundo.Add(emisor);
        var loop = new GameLoop { PuedeProcesar = () => !db.Cola.Saturada };
        var dispatcher = new PacketDispatcher(mundo, loop, db, datos, niveles);
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, dispatcher) { Player = emisor };
        emisor.Connection = sesion;
        using var tomado = new ManualResetEventSlim();
        using var soltar = new ManualResetEventSlim();
        db.Cola.Escribir("ocupado", _ => { tomado.Set(); soltar.Wait(); });
        var saturada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.Cola.LimiteAlcanzado += () => saturada.TrySetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task? corriendo = null;
        try
        {
            Assert.True(tomado.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < ColaDeLaBase.TopeDePedidos - 2; i++) db.Cola.Escribir("operación admitida", _ => { });
            control.Falla(m => m == nameof(IAlmacen.AcreditarBoveda) ? new InvalidOperationException("fallo permanente de prueba") : null);
            dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.TransferGOLD)
                .WriteAsciiString("Ausente").WriteLong(1_000).ToArray()));
            corriendo = loop.RunAsync(cts.Token);
            await saturada.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4_000, emisor.Bank);
            soltar.Set();
            await transporte.Devuelto.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(5_000, emisor.Bank);
            db.SaveCharacter(emisor); // la misma foto final que escribe el apagado, después de devolver lo fallido
            Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
            Assert.Equal(5_000, real.LeerPersonaje("Emisor")!.OroEnBanco);
        }
        finally
        {
            soltar.Set();
            cts.Cancel();
            if (corriendo is not null) await corriendo;
            sesion.Close("fin de prueba");
        }
    }
}
