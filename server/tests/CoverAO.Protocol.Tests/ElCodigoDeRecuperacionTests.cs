using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL CÓDIGO DE RECUPERACIÓN Y SU TOPE POR CUENTA (auditoría externa del 23/10). <c>ProcesosLogin</c> trae un byte de tipo: 0
/// recuperar, 1 cambiar la clave, 2 borrar. El bloqueo por cuenta de los códigos equivocados miraba sólo el 0, y un 3 entraba por
/// la misma verificación del código sin anotarse ni frenarse: se probaban códigos sin tope por cuenta.
/// </summary>
public class ElCodigoDeRecuperacionTests : IDisposable
{
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;

    private const string Clave = "clave_larga_de_prueba", Codigo = "1234", Nueva = "otra_clave_larga";

    private sealed class Transporte(string ip = "10.0.0.9") : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => $"tcp:{ip}:2222";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public ElCodigoDeRecuperacionTests()
    {
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        // El freno por IP alto, para que lo único que frene sea el de la cuenta.
        _dispatcher = new PacketDispatcher(_world, _loop, _db, datos, leveling,
            throttle: new LoginThrottle(maxFails: 1000, windowMs: 3_600_000, blockMs: 3_600_000));
        _corriendo = _loop.RunAsync(_cts.Token);
        _db.CreateAccount("cuenta", Clave, Codigo);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        _db.Dispose();
    }

    private void Enviar(ClientSession sesion, string pin, byte tipo, string cuenta)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.ProcesosLogin).WriteAsciiString(cuenta).WriteAsciiString(pin).WriteAsciiString(Nueva)
            .WriteByte(1).WriteByte(4).WriteByte(5).WriteByte(tipo).ToArray()));
    }

    /// <summary>Manda un ProcesosLogin y espera el EjecutarAccion(5) que cierra cada respuesta; devuelve su "1" o "0".</summary>
    private async Task<string> Procesar(string pin, byte tipo, string cuenta = "cuenta", string ip = "10.0.0.9")
    {
        var t = new Transporte(ip);
        Enviar(new ClientSession(t, _dispatcher), pin, tipo, cuenta);
        long fin = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < fin)
        {
            if (t.Recibidos.FirstOrDefault(x => x.Length >= 4 && x[0] == (byte)ServerPacketId.EjecutarAccion && x[1] == 5) is { } r)
                return Encoding.Latin1.GetString(r, 4, BitConverter.ToInt16(r, 2));
            await Task.Delay(25);
        }
        throw new TimeoutException("no llegó el EjecutarAccion(5)");
    }

    private bool ClaveEs(string clave) => _db.LoadAccount("cuenta")!.VerifyPassword(clave);

    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    public async Task Un_tipo_desconocido_no_cambia_la_clave_aunque_el_codigo_sea_bueno(byte tipo)
    {
        Assert.Equal("0", await Procesar(Codigo, tipo));
        Assert.True(ClaveEs(Clave));
    }

    [Fact]
    public async Task Recuperar_con_el_codigo_bueno_cambia_la_clave()
    {
        Assert.Equal("1", await Procesar(Codigo, 0));
        Assert.True(ClaveEs(Nueva));
    }

    [Fact]
    public async Task Cambiar_con_la_clave_actual_la_cambia()
    {
        Assert.Equal("1", await Procesar(Clave, 1));
        Assert.True(ClaveEs(Nueva));
    }

    [Fact]
    public async Task La_cuenta_bloqueada_por_codigos_no_se_destraba_con_otro_tipo()
    {
        for (int i = 0; i < 5; i++) Assert.Equal("0", await Procesar("9999", 0));
        Assert.Equal("0", await Procesar(Codigo, 0));   // el tope de la cuenta: ni con el bueno
        Assert.Equal("0", await Procesar(Codigo, 3));
        Assert.Equal("0", await Procesar(Codigo, 200));
        Assert.True(ClaveEs(Clave));
    }

    [Fact]
    public async Task Desconectarse_durante_la_verificacion_no_borra_los_fallos()
    {
        for (int i = 0; i < 5; i++)
        {
            using var liberar = new ManualResetEventSlim();
            var entro = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bloqueo = _db.Cola.PedirAsync("retener la lectura", _ =>
            {
                entro.SetResult();
                return liberar.Wait(TimeSpan.FromSeconds(20));
            });
            var sesion = new ClientSession(new Transporte(), _dispatcher);
            try
            {
                await entro.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Enviar(sesion, "9999", 0, "cuenta");
                await _loop.SincronizarContinuacionesAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(sesion.TomarVerificacion());
                if (i == 0)
                    Assert.Equal("0", await Procesar(Codigo, 0, "CUENTA", "10.0.0.10"));
                sesion.Cortar("cliente desconectado");
            }
            finally { liberar.Set(); }
            Assert.True(await bloqueo.WaitAsync(TimeSpan.FromSeconds(5)));

            using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!sesion.TomarVerificacion()) await Task.Delay(20, plazo.Token);
            sesion.SoltarVerificacion();
            await _loop.SincronizarContinuacionesAsync().WaitAsync(plazo.Token);
        }
        Assert.Equal("0", await Procesar(Codigo, 0, "CUENTA", "10.0.0.11"));
        Assert.True(ClaveEs(Clave));
    }
}
