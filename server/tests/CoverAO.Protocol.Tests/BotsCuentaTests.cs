using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA CUENTA DE UN BOT NO SE ABRE DESDE AFUERA, aunque se sepa la contraseña.
///
/// <para>La contraseña de un bot la sortea el servidor y queda escrita en <c>data/bots.txt</c>, que además se
/// copia en cada backup. O sea que es un secreto que se propaga solo: a una copia, a otra máquina, a quien le
/// pidan una mano al que hostea. La marca <see cref="Account.EsBot"/> hace que ese archivo no alcance para
/// entrar, y esto lo comprueba con las dos mitades — que de afuera no entra, y que con TODO igual salvo el
/// transporte sí entra. Sin la segunda mitad, un password mal escrito en el test daría verde por el motivo
/// equivocado.</para>
/// </summary>
public class BotsCuentaTests : IDisposable
{
    private const string CuentaDeBot = "Aldo_bot", CuentaDePersona = "Rugura", Clave = "clave_larga_de_prueba";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_bots_" + Guid.NewGuid().ToString("N"));
    private readonly PacketDispatcher _dispatcher;
    private readonly Persistence _db;
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    /// <summary>Una conexión de AFUERA: la que tendría una persona con un cliente. Guarda lo que le mandan.</summary>
    private sealed class TransporteDeAfuera : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "tcp:10.0.0.9:1234";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public BotsCuentaTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(new Dictionary<int, long>());
        _db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(new World(), _loop, _db, GameData.Load(), leveling);
        // El login verifica la contraseña FUERA del loop y vuelve por él, así que acá el loop tiene que girar
        // de verdad: sin esto la respuesta nunca llega y los tres tests darían el mismo timeout.
        _loopTask = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        _cts.Dispose();
    }

    // ── Los tres casos ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_cuenta_de_un_bot_no_se_abre_desde_afuera()
    {
        _db.CreateAccount(CuentaDeBot, Clave, esBot: true);

        var afuera = new TransporteDeAfuera();
        Abrir(new ClientSession(afuera, _dispatcher), CuentaDeBot, Clave);

        Assert.Null(await Esperar(afuera.Recibidos, ServerPacketId.AddPJ));
        var rechazo = Buscar(afuera.Recibidos, ServerPacketId.ConsoleMsg);
        Assert.NotNull(rechazo);
        // El mismo texto que una contraseña equivocada, a propósito: un mensaje propio le confirmaría a quien
        // prueba que la cuenta existe y que es de un bot.
        Assert.Contains("invalida", Texto(rechazo!), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>EL CONTROL. Todo igual —la misma cuenta, la misma contraseña, el mismo paquete— salvo el
    /// transporte. Si esto no entrara, el test de arriba estaría dando verde por un password mal escrito.</summary>
    [Fact]
    public async Task La_misma_cuenta_con_la_misma_clave_se_abre_desde_el_bot()
    {
        _db.CreateAccount(CuentaDeBot, Clave, esBot: true);

        var recibidos = new ConcurrentQueue<byte[]>();
        var transporte = new TransporteDeBot("Aldo");
        transporte.Recibido += p => recibidos.Enqueue(p);
        Abrir(new ClientSession(transporte, _dispatcher), CuentaDeBot, Clave);

        Assert.NotNull(await Esperar(recibidos, ServerPacketId.AddPJ));
    }

    /// <summary>Y al revés: un bot tampoco abre la cuenta de una persona. Es la misma comparación, y cierra el
    /// caso de un bot al que le hubieran puesto por error las credenciales de alguien.</summary>
    [Fact]
    public async Task Un_bot_no_abre_la_cuenta_de_una_persona()
    {
        _db.CreateAccount(CuentaDePersona, Clave);

        var recibidos = new ConcurrentQueue<byte[]>();
        var transporte = new TransporteDeBot("Rugura");
        transporte.Recibido += p => recibidos.Enqueue(p);
        Abrir(new ClientSession(transporte, _dispatcher), CuentaDePersona, Clave);

        Assert.Null(await Esperar(recibidos, ServerPacketId.AddPJ));
    }

    /// <summary>La marca vive en el <c>.cnt</c>, así que tiene que sobrevivir a guardar y volver a leer: si se
    /// perdiera al releer, la cuenta quedaría abierta desde afuera al primer reinicio del servidor.</summary>
    [Fact]
    public void La_marca_sobrevive_al_archivo()
    {
        _db.CreateAccount(CuentaDeBot, Clave, esBot: true);
        _db.CreateAccount(CuentaDePersona, Clave);

        Assert.True(_db.LoadAccount(CuentaDeBot)!.EsBot);
        Assert.False(_db.LoadAccount(CuentaDePersona)!.EsBot);
    }

    // ── Ayudas ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Manda un <c>ConnectAccount</c>, que es el paquete que pide la lista de personajes: alcanza para
    /// saber si la cuenta se abrió y no necesita que exista ningún personaje.</summary>
    private void Abrir(ClientSession sesion, string cuenta, string clave)
    {
        var paquete = new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.ConnectAccount)
            .WriteAsciiString(cuenta).WriteAsciiString(clave)
            .WriteByte(1).WriteByte(0).WriteByte(0)
            .WriteAsciiString("00-00-00-00-00-00").WriteLong(0)
            .ToArray();
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
    }

    /// <summary>Espera hasta que aparezca ese paquete, o hasta que se acabe el plazo. El plazo es largo porque
    /// del otro lado hay un PBKDF2 de 210.000 vueltas más una ida y vuelta por el game loop.</summary>
    private static async Task<byte[]?> Esperar(ConcurrentQueue<byte[]> cola, ServerPacketId id, int topeMs = 15_000)
    {
        long fin = Environment.TickCount64 + topeMs;
        while (Environment.TickCount64 < fin)
        {
            if (Buscar(cola, id) is { } p) return p;
            await Task.Delay(25);
        }
        return null;
    }

    private static byte[]? Buscar(ConcurrentQueue<byte[]> cola, ServerPacketId id) =>
        cola.FirstOrDefault(p => p.Length > 0 && p[0] == (byte)id);

    /// <summary>El texto de un <c>ConsoleMsg</c>: id, Int16 de largo, y los bytes.</summary>
    private static string Texto(byte[] p) =>
        p.Length < 3 ? "" : Codec.Ansi.GetString(p, 3, Math.Min(p[1] | (p[2] << 8), p.Length - 3));
}
