using System.Threading.Channels;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class DevolucionDetrasDeRescateTests : IDisposable
{
    private static readonly GameData Datos = GameData.Load();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_devolucion_rescate_" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenSqlite _real = AlmacenSqlite.EnMemoria();
    private readonly AlmacenQueFalla _fallas;
    private readonly Persistence _db;
    private readonly World _mundo = new();
    private readonly GameLoop _loop = new();
    private readonly PacketDispatcher _dispatcher;
    private readonly EntregaManual _entregas = new();
    private readonly Task _corriendo;
    private short _indice;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "tcp:10.0.0.7:2222";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

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

    public DevolucionDetrasDeRescateTests()
    {
        var niveles = new Leveling(Datos.LevelExp);
        (var almacen, _fallas) = AlmacenQueFalla.Crear(_real, _ => null);
        _db = new Persistence(almacen, niveles);
        _dispatcher = new PacketDispatcher(_mundo, _loop, _db, Datos, niveles)
        {
            PlanificadorDeRespuestas = _entregas,
        };
        _corriendo = _loop.RunAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _fallas.Falla(_ => null);
        _loop.Stop();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _db.Dispose();
        _real.Dispose();
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }

    private (Player P, ClientSession S) Personaje(string nombre, long saldo, bool conectado)
    {
        var cuenta = _db.CreateAccount(nombre.ToLowerInvariant(), "clave_larga_de_prueba");
        var p = _db.CreateCharacter(cuenta, nombre, ++_indice, 1, 1, 1, 1, 1);
        p.Bank = saldo;
        _db.SaveCharacter(p);
        var s = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = s;
        if (conectado) _mundo.Add(p);
        _db.EsperarEscriturasPendientes();
        return (p, s);
    }

    private async Task EnElLoop(Action hacer)
    {
        var listo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.EnqueueContinuacion(() => { hacer(); listo.SetResult(); });
        await listo.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private void Transferir(ClientSession s, string destino, int monto) => _dispatcher.HandleOne(s,
        new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.TransferGOLD)
            .WriteAsciiString(destino).WriteLong(monto).ToArray()));

    private long Saldo(string nombre) => _real.LeerPersonaje(nombre)!.OroEnBanco;

    private static IEnumerable<Rescate> Partes(Rescate rescate) => rescate is RescateJunto junto
        ? junto.Partes.SelectMany(Partes) : new[] { rescate };

    [Fact]
    public async Task Una_devolucion_detras_de_otra_transferencia_rescatada_se_conserva_y_no_se_duplica()
    {
        var (emisor, sesionEmisor) = Personaje("Emisor", 5_000, conectado: true);
        var (donante, sesionDonante) = Personaje("Donante", 3_000, conectado: true);
        Personaje("Ausente", 0, conectado: false);

        // A falla de manera definitiva. Su devolución queda retenida mientras el emisor se desconecta.
        int acreditaciones = 0;
        _fallas.Falla(m => m == nameof(IAlmacen.AcreditarBoveda) && Interlocked.Increment(ref acreditaciones) == 2
            ? new InvalidOperationException("la transferencia A no entró") : null);
        Transferir(sesionEmisor, "Ausente", 1_000);
        await _entregas.EsperarUna();
        await EnElLoop(() =>
        {
            _mundo.Remove(emisor.CharIndex);
            _db.SaveCharacter(emisor);
        });
        Assert.True(_db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        Assert.Equal(4_000, Saldo("Emisor"));

        // B tiene como destinatario al mismo Emisor ya desconectado. La caída deja el cambio reservado para el rescate:
        // su Task no se completa y nunca ejecutará SoltarCambio durante este cierre.
        var seCayo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fallas.Falla(m =>
        {
            if (m != nameof(IAlmacen.AcreditarBoveda)) return null;
            seCayo.TrySetResult();
            return new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        });
        Transferir(sesionDonante, "Emisor", 700);
        await seCayo.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await EnElLoop(() => _loop.PuedeProcesar = () => false);
        var apartadas = _db.Cola.Cerrar(TimeSpan.Zero);
        Assert.Contains(apartadas.Where(p => p.Rescate is not null).SelectMany(p => Partes(p.Rescate!)),
            p => p is RescateDeAcreditacion { Nombre: "Emisor", Monto: 700 });
        Assert.Equal(2_300, donante.Bank);

        // La devolución A se encuentra con B todavía reservado. El cierre debe terminar, sin esperar indefinidamente B,
        // y conservar A para el despacho final después de las fichas de los conectados.
        await _entregas.EntregarUna();
        await _dispatcher.CompletarRespuestasDeLaBaseAsync().WaitAsync(TimeSpan.FromSeconds(15));
        _loop.Stop();
        await _corriendo.WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var p in _mundo.Players.ToArray()) _db.SaveCharacter(p);
        _dispatcher.VolcarPersistenciaDiferida(forzar: true);
        _dispatcher.DespacharDevolucionesPendientesAlApagar();
        var todo = _db.Cola.Cerrar(TimeSpan.Zero);
        var creditos = todo.Where(p => p.Rescate is not null).SelectMany(p => Partes(p.Rescate!))
            .OfType<RescateDeAcreditacion>().Where(p => p.Nombre == "Emisor").ToArray();
        Assert.Single(creditos, p => p.Monto == 700);
        Assert.Single(creditos, p => p.Monto == 1_000);
        _db.Cerrar(_raiz);

        _fallas.Falla(_ => null);
        Rescates.AplicarLosPendientes(_raiz, _real);
        Assert.Equal(5_700, Saldo("Emisor"));
        Assert.Equal(2_300, Saldo("Donante"));
        Assert.Equal(0, Saldo("Ausente"));
        Assert.Equal(8_000, Saldo("Emisor") + Saldo("Donante") + Saldo("Ausente"));

        // Otro corte justo antes de renombrar el rescate obliga a reproducirlo. Ninguna de las dos sumas entra dos veces.
        foreach (var aplicado in Directory.GetFiles(_raiz, "rescate-*.json.aplicado"))
            File.Move(aplicado, aplicado[..^".aplicado".Length]);
        Rescates.AplicarLosPendientes(_raiz, _real);
        Assert.Equal(5_700, Saldo("Emisor"));
        Assert.Equal(2_300, Saldo("Donante"));
        Assert.Equal(0, Saldo("Ausente"));
        Assert.Equal(8_000, Saldo("Emisor") + Saldo("Donante") + Saldo("Ausente"));
    }
}
