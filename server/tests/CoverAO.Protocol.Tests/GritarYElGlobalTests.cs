using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS TRES MODOS DEL CHAT (VB6 HandleTalk, Protocol.bas:1394-1412). 1, la charla: el globo al área. 2, gritar
/// (`-texto`): vivo, a todo el MAPA el globo y "[Nombre] texto" con la fuente 25. 3, el global (`;texto`): vivo, a
/// TODO el server "[Nombre] texto" con la fuente 23 y el globo. El grito y el global llegan con su marca adelante y el
/// server la saca. Otro modo no hace nada. Acá cualquier modo salía como charla al área.
/// </summary>
public class GritarYElGlobalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_gritar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const byte Charla = 1, Grito = 2, Global = 3;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
        public int Indice(byte[] paquete) => Recibidos.ToList().FindIndex(x => x.SequenceEqual(paquete));
    }

    public GritarYElGlobalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player, ClientSession, Transporte) Conectado(string nombre, WorldPos pos)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = pos,
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Hablar(ClientSession sesion, string texto, byte modo)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.Talk).WriteAsciiString(texto).WriteByte(modo).ToArray()));
        await Task.Delay(300);
    }

    private static WorldPos En(int x, int y, short mapa = 1) => new(mapa, (byte)x, (byte)y);
    private static readonly byte[] EstasMuerto = ServerMessages.LocaleMsg(77, "", 0, 12);

    /// <summary>Tres testigos: uno al lado, uno en el mismo mapa pero lejos, y uno en otro mapa.</summary>
    private (Transporte cerca, Transporte lejos, Transporte otroMapa) Testigos() =>
        (Conectado("Cerca", En(52, 50)).Item3, Conectado("Lejos", En(50, 90)).Item3,
         Conectado("OtroMapa", En(50, 50, mapa: 34)).Item3);

    [Fact]
    public async Task La_charla_va_al_area()
    {
        var (yo, sesion, mio) = Conectado("Merlin", En(50, 50));
        var (cerca, lejos, otroMapa) = Testigos();

        await Hablar(sesion, "hola", Charla);

        var globo = ServerMessages.ChatOverHead("hola", yo.CharIndex, 1);
        Assert.True(mio.Recibio(globo));
        Assert.True(cerca.Recibio(globo));
        Assert.Empty(lejos.Recibidos);
        Assert.Empty(otroMapa.Recibidos);
    }

    [Fact]
    public async Task La_charla_de_un_bot_llega_al_jugador_y_no_a_otro_bot()
    {
        var (humano, sesionHumana, receptorHumano) = Conectado("Merlin", En(50, 50));
        var (bruma, emisorBot, _) = ConectarBot("Bruma", En(51, 50));
        var (_, _, receptorBot) = ConectarBot("Quim", En(52, 50));

        await Hablar(emisorBot, "Quim, hola", Charla);
        var globoDelBot = ServerMessages.ChatOverHead("Quim, hola", bruma.CharIndex, Charla);
        Assert.True(receptorHumano.Recibio(globoDelBot));
        Assert.Empty(receptorBot);

        await Hablar(sesionHumana, "Quim, hola", Charla);
        var globoHumano = ServerMessages.ChatOverHead("Quim, hola", humano.CharIndex, Charla);
        Assert.Contains(receptorBot, p => p.SequenceEqual(globoHumano));
    }

    private (Player, ClientSession, ConcurrentQueue<byte[]>) ConectarBot(string nombre, WorldPos pos)
    {
        var jugador = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant() + "_bot",
            Pos = pos, Hp = 100, MaxHp = 100, Level = 30,
        };
        var transporte = new TransporteDeBot(nombre);
        var recibidos = new ConcurrentQueue<byte[]>();
        transporte.Recibido += recibidos.Enqueue;
        var sesion = new ClientSession(transporte, _dispatcher) { Player = jugador };
        jugador.Connection = sesion;
        _world.Add(jugador);
        return (jugador, sesion, recibidos);
    }

    [Fact]
    public async Task El_grito_llega_a_todo_el_mapa_sin_la_marca()
    {
        var (yo, sesion, mio) = Conectado("Merlin", En(50, 50));
        var (cerca, lejos, otroMapa) = Testigos();

        await Hablar(sesion, "-auxilio", Grito);

        var globo = ServerMessages.ChatOverHead("auxilio", yo.CharIndex, 2);
        var linea = ServerMessages.ConsoleMsg("[Merlin] auxilio", 25);
        foreach (var t in new[] { mio, cerca, lejos })
        {
            Assert.True(t.Recibio(globo), "no le llegó el globo del grito");
            Assert.True(t.Recibio(linea), "no le llegó el renglón del grito");
            Assert.True(t.Indice(globo) < t.Indice(linea), "el globo va antes que el renglón");
        }
        Assert.Empty(otroMapa.Recibidos);
    }

    [Fact]
    public async Task El_global_llega_a_todo_el_server_sin_la_marca()
    {
        var (yo, sesion, mio) = Conectado("Merlin", En(50, 50));
        var (cerca, lejos, otroMapa) = Testigos();

        await Hablar(sesion, ";vendo espada", Global);

        var linea = ServerMessages.ConsoleMsg("[Merlin] vendo espada", 23);
        var globo = ServerMessages.ChatOverHead("vendo espada", yo.CharIndex, 3);
        foreach (var t in new[] { mio, cerca, lejos, otroMapa })
        {
            Assert.True(t.Recibio(linea), "no le llegó el renglón del global");
            Assert.True(t.Recibio(globo), "no le llegó el globo del global");
            Assert.True(t.Indice(linea) < t.Indice(globo), "el renglón va antes que el globo");
        }
    }

    [Theory]
    [InlineData(Grito, "-auxilio")]
    [InlineData(Global, ";vendo espada")]
    public async Task El_muerto_no_grita_ni_habla_en_el_global(byte modo, string texto)
    {
        var (yo, sesion, mio) = Conectado("Merlin", En(50, 50));
        yo.Hp = 0;
        var (cerca, lejos, otroMapa) = Testigos();

        await Hablar(sesion, texto, modo);

        Assert.Equal(new[] { EstasMuerto }, mio.Recibidos.ToArray());
        Assert.Empty(cerca.Recibidos);
        Assert.Empty(lejos.Recibidos);
        Assert.Empty(otroMapa.Recibidos);
    }

    /// <summary>La charla no mira si está muerto: el original no le pone el DeadCheck.</summary>
    [Fact]
    public async Task El_muerto_charla_igual()
    {
        var (yo, sesion, _) = Conectado("Merlin", En(50, 50));
        yo.Hp = 0;
        var (cerca, _, _) = Testigos();

        await Hablar(sesion, "hola", Charla);

        Assert.True(cerca.Recibio(ServerMessages.ChatOverHead("hola", yo.CharIndex, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Otro_modo_no_hace_nada(byte modo)
    {
        var (_, sesion, mio) = Conectado("Merlin", En(50, 50));
        var (cerca, lejos, otroMapa) = Testigos();

        await Hablar(sesion, "hola", modo);

        Assert.Empty(mio.Recibidos);
        Assert.Empty(cerca.Recibidos);
        Assert.Empty(lejos.Recibidos);
        Assert.Empty(otroMapa.Recibidos);
    }
}
