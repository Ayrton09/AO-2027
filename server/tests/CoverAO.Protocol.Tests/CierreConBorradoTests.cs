using System.Threading.Channels;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class CierreConBorradoTests : IDisposable
{
    private static readonly GameData Datos = GameData.Load();
    private const string Clave = "clave_larga_de_prueba";
    private readonly AlmacenSqlite _real = AlmacenSqlite.EnMemoria();
    private readonly AlmacenQueFalla _fallas;
    private readonly Persistence _db;
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly EntregaManual _entregas = new();
    private readonly ManualResetEventSlim _soltarBorrado = new(false);
    private short _indice;

    private sealed class EntregaManual : TaskScheduler
    {
        private readonly Channel<Task> _listas = Channel.CreateUnbounded<Task>();
        protected override IEnumerable<Task> GetScheduledTasks() => Array.Empty<Task>();
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override void QueueTask(Task task) => _listas.Writer.TryWrite(task);
        public Task EsperarUna() => _listas.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        public async Task EntregarUna()
        {
            var tarea = await _listas.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(TryExecuteTask(tarea));
        }
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "tcp:10.0.0.7:2222";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public CierreConBorradoTests()
    {
        var leveling = new Leveling(Datos.LevelExp);
        (var almacen, _fallas) = AlmacenQueFalla.Crear(_real, _ => null);
        _db = new Persistence(almacen, leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, Datos, leveling)
        {
            PlanificadorDeRespuestas = _entregas,
        };
        _corriendo = _loop.RunAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _soltarBorrado.Set();
        _fallas.Falla(_ => null);
        _fallas.FallaDespues((_, _) => null);
        _loop.Stop();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _db.Dispose();
        _real.Dispose();
        _soltarBorrado.Dispose();
    }

    private (Player P, ClientSession S) Personaje(string nombre, bool conectado)
    {
        var cuenta = _db.CreateAccount(nombre.ToLowerInvariant(), Clave);
        var p = _db.CreateCharacter(cuenta, nombre, ++_indice, 1, 1, 1, 1, 1);
        p.Bank = nombre == "Emisor" ? 5_000 : 0;
        _db.SaveCharacter(p);
        var s = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = s;
        if (conectado) _world.Add(p);
        _db.EsperarEscriturasPendientes();
        return (p, s);
    }

    private async Task<T> EnElLoop<T>(Func<T> hacer)
    {
        var lista = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.EnqueueContinuacion(() => lista.SetResult(hacer()));
        return await lista.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_cierre_libera_el_borrado_rechazado_y_devuelve_la_transferencia(bool baneado)
    {
        var (p, s) = Personaje("Emisor", conectado: true);
        Personaje("Ausente", conectado: false);
        int acreditaciones = 0;
        _fallas.Falla(m => m == nameof(IAlmacen.AcreditarBoveda) && Interlocked.Increment(ref acreditaciones) == 2
            ? new InvalidOperationException("la transferencia no entró") : null);
        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.TransferGOLD)
            .WriteAsciiString("Ausente").WriteLong(1_000).ToArray()));
        await _entregas.EsperarUna();   // la base falló; retenemos su devolución antes de que llegue al loop

        await EnElLoop(() =>
        {
            _world.Remove(p.CharIndex);
            p.Banned = baneado;
            _db.SaveCharacter(p);   // el emisor se fue con 4000; falta devolver los 1000 de la transferencia fallida
            return true;
        });
        _db.EsperarEscriturasPendientes();

        var leyendoBorrado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int retener = 1;
        _fallas.FallaDespues((metodo, _) =>
        {
            if (metodo != nameof(IAlmacen.LeerPersonaje) || Interlocked.Exchange(ref retener, 0) != 1) return null;
            leyendoBorrado.TrySetResult();
            if (!_soltarBorrado.Wait(TimeSpan.FromSeconds(15))) return new TimeoutException("el test no soltó el borrado");
            // En un caso la lectura confirma que está baneado; en el otro falla antes de archivar. Ninguno borra al emisor.
            return baneado ? null : new InvalidOperationException("no se pudo leer para borrar");
        });
        _dispatcher.HandleOne(new ClientSession(new Transporte(), _dispatcher), new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.BorrarPersonaje).WriteAsciiString("emisor").WriteAsciiString(Clave)
            .WriteAsciiString("Emisor").ToArray()));
        await leyendoBorrado.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(await EnElLoop(() =>
        {
            _loop.PuedeProcesar = () => false;
            return _world.EstaEntrando("Emisor");
        }));
        await _entregas.EntregarUna();
        await _loop.SincronizarContinuacionesAsync();   // la devolución encontró la reserva y quedó reintentándose
        // La primera entrega ya fue controlada. Las lecturas que inicie la devolución usan el scheduler normal.
        _dispatcher.PlanificadorDeRespuestas = TaskScheduler.Default;
        _soltarBorrado.Set();

        var cierre = _dispatcher.CompletarRespuestasDeLaBaseAsync();
        // El cierre corregido debe esperar también la lectura del emisor que inicia el reintento, no sólo la primera entrega.
        await cierre.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(await EnElLoop(() => _world.EstaEntrando("Emisor")));
        Assert.True(_db.CharacterExists("Emisor"));
        Assert.Equal(5_000, _db.LoadCharacter("Emisor", 0, "emisor")!.Bank);
        Assert.Equal(0, _db.LoadCharacter("Ausente", 0, "ausente")!.Bank);
    }
}
