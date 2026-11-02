using System.Threading.Channels;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class CierreDeRespuestasDeLaBaseTests : IDisposable
{
    private static readonly GameData Datos = GameData.Load();
    private readonly AlmacenSqlite _real = AlmacenSqlite.EnMemoria();
    private readonly AlmacenQueFalla _fallas;
    private readonly Persistence _db;
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly EntregaManual _entregas = new();
    private readonly string _rescate = Path.Combine(Path.GetTempPath(), "coverao_devolucion_" + Guid.NewGuid().ToString("N"));
    private short _indice;

    // Retiene sólo las entregas de este dispatcher. No bloquea hilos ni cambia el scheduler global de los tests.
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

        public async Task EntregarHasta(Task operacion)
        {
            using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!operacion.IsCompleted)
            {
                while (_listas.Reader.TryRead(out var tarea)) Assert.True(TryExecuteTask(tarea));
                if (!operacion.IsCompleted)
                    await Task.WhenAny(operacion, _listas.Reader.WaitToReadAsync(plazo.Token).AsTask()).WaitAsync(plazo.Token);
            }
            await operacion;
        }
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "tcp:10.0.0.7:2222";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public CierreDeRespuestasDeLaBaseTests()
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
        _fallas.Falla(_ => null);
        _loop.Stop();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _db.Dispose();
        _real.Dispose();
        if (Directory.Exists(_rescate)) Directory.Delete(_rescate, recursive: true);
    }

    private (Player P, ClientSession S) Personaje(string nombre, bool conectado)
    {
        var cuenta = _db.CreateAccount(nombre.ToLowerInvariant(), "clave_larga_de_prueba");
        var p = _db.CreateCharacter(cuenta, nombre, ++_indice, 1, 1, 1, 1, 1);
        p.Bank = nombre == "Emisor" ? 5_000 : 0;
        _db.SaveCharacter(p);
        var s = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = s;
        if (conectado) _world.Add(p);
        _db.EsperarEscriturasPendientes();
        return (p, s);
    }

    private async Task EnElLoop(Action hacer)
    {
        var lista = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.EnqueueContinuacion(() => { hacer(); lista.SetResult(); });
        await lista.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private void FallaLaTransferencia()
    {
        int n = 0;
        _fallas.Falla(m => m == nameof(IAlmacen.AcreditarBoveda) && Interlocked.Increment(ref n) == 2
            ? new InvalidOperationException("la transferencia no entró") : null);
    }

    private void Transferir(ClientSession s) => _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.TransferGOLD).WriteAsciiString("Ausente").WriteLong(1_000).ToArray()));

    [Fact]
    public async Task El_cierre_espera_la_devolucion_aunque_su_entrega_se_demore()
    {
        var (p, s) = Personaje("Emisor", conectado: true);
        Personaje("Ausente", conectado: false);
        FallaLaTransferencia();
        Transferir(s);
        await _entregas.EsperarUna();   // la base falló, pero el callback todavía no llegó al loop
        _loop.PuedeProcesar = () => false;
        Assert.Equal(4_000, p.Bank);

        var cierre = _dispatcher.CompletarRespuestasDeLaBaseAsync();
        Assert.False(cierre.IsCompleted);
        await _entregas.EntregarUna();
        await cierre.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(5_000, p.Bank);
        _db.SaveCharacter(p);   // la foto final del apagado ya incluye la devolución
        _db.EsperarEscriturasPendientes();
        Assert.Equal(5_000, _db.LoadCharacter("Emisor", 0, "emisor")!.Bank);
        Assert.Equal(0, _db.LoadCharacter("Ausente", 0, "ausente")!.Bank);
    }

    [Fact]
    public async Task La_devolucion_al_emisor_desconectado_termina_su_acreditacion()
    {
        var (p, s) = Personaje("Emisor", conectado: true);
        Personaje("Ausente", conectado: false);
        FallaLaTransferencia();
        Transferir(s);
        await _entregas.EsperarUna();
        await EnElLoop(() =>
        {
            _world.Remove(p.CharIndex);
            _db.SaveCharacter(p);   // se desconectó mientras la devolución esperaba
            _loop.PuedeProcesar = () => false;
        });

        var cierre = _dispatcher.CompletarRespuestasDeLaBaseAsync();
        await _entregas.EntregarUna();   // acredita la bóveda del emisor ya desconectado
        await _entregas.EsperarUna();
        Assert.False(cierre.IsCompleted);
        await _entregas.EntregarUna();
        await cierre.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(5_000, _db.LoadCharacter("Emisor", 0, "emisor")!.Bank);
        Assert.Equal(0, _db.LoadCharacter("Ausente", 0, "ausente")!.Bank);
    }

    [Fact]
    public async Task Lo_apartado_para_el_rescate_no_se_espera_ni_se_devuelve_dos_veces()
    {
        var (p, s) = Personaje("Emisor", conectado: true);
        Personaje("Ausente", conectado: false);
        var fallo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fallas.Falla(m =>
        {
            if (m != nameof(IAlmacen.AcreditarBoveda)) return null;
            fallo.TrySetResult();
            return new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        });
        Transferir(s);
        await fallo.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _loop.PuedeProcesar = () => false;
        var rescate = _db.Cola.Cerrar(TimeSpan.Zero);
        Assert.Contains(rescate, r => r.Rescate is RescateJunto);

        await _dispatcher.CompletarRespuestasDeLaBaseAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(4_000, p.Bank);   // lo acreditará el rescate; no corresponde devolverlo
    }

    [Fact]
    public async Task La_devolucion_al_conectado_se_guarda_sin_esperar_otro_autosave()
    {
        var (p, s) = Personaje("Emisor", conectado: true);
        Personaje("Ausente", conectado: false);
        FallaLaTransferencia();
        Transferir(s);
        await _entregas.EsperarUna();
        await EnElLoop(() => _db.SaveCharacter(p));   // una salida/autosave ya dejó persistido el descuento
        Assert.True(_db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        Assert.Equal(4_000, _real.LeerPersonaje("Emisor")!.OroEnBanco);

        await _entregas.EntregarHasta(_dispatcher.CompletarRespuestasDeLaBaseAsync());

        Assert.Equal(5_000, p.Bank);
        Assert.Equal(5_000, _real.LeerPersonaje("Emisor")!.OroEnBanco);
        Assert.Equal(0, _real.LeerPersonaje("Ausente")!.OroEnBanco);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_devolucion_al_desconectado_con_la_cola_rendida_se_rescata_una_sola_vez(bool salidaPendiente)
    {
        var (p, s) = Personaje("Emisor", conectado: true);
        Personaje("Ausente", conectado: false);
        FallaLaTransferencia();
        Transferir(s);
        await _entregas.EsperarUna();
        await EnElLoop(() =>
        {
            _world.Remove(p.CharIndex);
            if (!salidaPendiente) _db.SaveCharacter(p);
            _loop.PuedeProcesar = () => false;
        });
        Assert.True(_db.Cola.Vaciar(TimeSpan.FromSeconds(10)));

        var caida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fallas.Falla(_ =>
        {
            caida.TrySetResult();
            return new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        });
        _db.SaveCharacter(p);   // cubre también la ficha de salida todavía pendiente al caer la base
        await caida.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _db.Cola.Cerrar(TimeSpan.Zero);

        await _entregas.EntregarHasta(_dispatcher.CompletarRespuestasDeLaBaseAsync());
        var pendientes = _db.Cola.Cerrar(TimeSpan.Zero);
        Assert.Contains(pendientes, r => r.Rescate is RescateDeAcreditacion { Nombre: "Emisor", Monto: 1_000 });
        _loop.Stop();
        await _corriendo;
        _db.Cerrar(_rescate, TimeSpan.Zero);

        string archivo = Assert.Single(Directory.GetFiles(_rescate, "rescate-*.json"));
        Rescates.AplicarLosPendientes(_rescate, _real);
        Assert.Equal(5_000, _real.LeerPersonaje("Emisor")!.OroEnBanco);
        Assert.Equal(0, _real.LeerPersonaje("Ausente")!.OroEnBanco);
        // Simula que se cortó el arranque antes de dar el archivo por aplicado.
        File.Move(archivo + ".aplicado", archivo);
        Rescates.AplicarLosPendientes(_rescate, _real);
        Assert.Equal(5_000, _real.LeerPersonaje("Emisor")!.OroEnBanco);
        Assert.Equal(0, _real.LeerPersonaje("Ausente")!.OroEnBanco);
    }
}
