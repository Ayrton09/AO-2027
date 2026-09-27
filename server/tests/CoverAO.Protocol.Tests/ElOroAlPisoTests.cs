using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// TIRAR ORO, COMO EL ORIGINAL (VB6 TirarOro, InvUsuario.bas:154): en montones de a <c>OroPorMonton</c> (10.000 por defecto, el
/// <c>MAX_INVENTORY_OBJS</c> del original), cada uno en una baldosa libre de alrededor —nunca debajo del jugador— y apilando sobre
/// el oro que ya haya. Reporte del tester, 2026-09-27: se tiraba un solo montón y de 50.000 caían 10.000.
/// </summary>
public class ElOroAlPisoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_oro_piso_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly List<(GameLoop Loop, CancellationTokenSource Cts, Task Corriendo)> _loops = new();

    private const short Oro = 12;
    private const short Mapa = 15;   // Desierto Rinkel: despejado alrededor de (50,50)
    private const byte X = 50, Y = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public void Dispose()
    {
        foreach (var (_, cts, corriendo) in _loops)
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    /// <summary>Un mundo con un jugador con oro en (50,50) del 15, y el Server.ini con las claves que se pidan.</summary>
    private (World Mundo, Player Jugador, ClientSession Sesion, PacketDispatcher Dispatcher) Armar(int oro, string claves = "")
    {
        string carpeta = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        string ini = Path.Combine(carpeta, "Server.ini");
        File.WriteAllText(ini, "[INIT]\r\nStartPort=7666\r\n" + claves + "\r\n");
        var world = new World();
        var loop = new GameLoop();
        var leveling = new Leveling(_datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(new FileStore(carpeta), leveling), _datos, leveling,
            serverIni: ServerIni.Cargar(ini));
        var cts = new CancellationTokenSource();
        _loops.Add((loop, cts, loop.RunAsync(cts.Token)));
        var p = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Mapa, X, Y), Heading = 2,
            Hp = 100, MaxHp = 100, Level = 20, Stamina = 100, MaxStamina = 100, Gold = oro,
        };
        var sesion = new ClientSession(new Transporte(), dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        world.Add(p);
        return (world, p, sesion, dispatcher);
    }

    private static async Task TirarOro(PacketDispatcher dispatcher, ClientSession sesion, int cantidad)
    {
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.Drop)
            .WriteByte((byte)(GameLimits.MaxInventorySlots + 1)).WriteLong(cantidad);
        dispatcher.HandleOne(sesion, new ByteQueueReader(w.ToArray()));
        await Task.Delay(250);
    }

    /// <summary>Los montones de oro que hay a cinco baldosas del jugador, con su baldosa.</summary>
    private static List<(byte X, byte Y, int Cantidad)> Montones(World world)
    {
        var hay = new List<(byte, byte, int)>();
        for (int y = Y - 5; y <= Y + 5; y++)
            for (int x = X - 5; x <= X + 5; x++)
                if (world.TryGetGround(new WorldPos(Mapa, (byte)x, (byte)y), out var g) && g.ObjIndex == Oro)
                    hay.Add(((byte)x, (byte)y, g.Amount));
        return hay;
    }

    [Fact]
    public async Task Se_tira_en_montones_de_diez_mil_alrededor_del_jugador()
    {
        var (world, p, sesion, dispatcher) = Armar(oro: 50_000);

        await TirarOro(dispatcher, sesion, 35_000);

        var montones = Montones(world);
        Assert.Equal(15_000, p.Gold);
        Assert.Equal(new[] { 5_000, 10_000, 10_000, 10_000 }, montones.Select(m => m.Cantidad).OrderBy(c => c));
        Assert.DoesNotContain(montones, m => m.X == X && m.Y == Y);   // debajo del jugador no: ahí está él (Tilelibre)
    }

    [Fact]
    public async Task El_tamanio_del_monton_sale_del_Server_ini()
    {
        var (world, p, sesion, dispatcher) = Armar(oro: 50_000, claves: "OroPorMonton=3000");

        await TirarOro(dispatcher, sesion, 10_000);

        Assert.Equal(40_000, p.Gold);
        Assert.Equal(new[] { 1_000, 3_000, 3_000, 3_000 }, Montones(world).Select(m => m.Cantidad).OrderBy(c => c));
    }

    [Fact]
    public async Task Lo_que_cae_se_apila_sobre_el_oro_que_ya_estaba_hasta_el_tope()
    {
        var (world, p, sesion, dispatcher) = Armar(oro: 50_000);
        // Oro en las ocho baldosas de alrededor, casi lleno; en la primera que mira Tilelibre, con lugar para 600.
        for (int y = Y - 1; y <= Y + 1; y++)
            for (int x = X - 1; x <= X + 1; x++)
                if (x != X || y != Y) world.SetGround(new WorldPos(Mapa, (byte)x, (byte)y), Oro, 9_900);
        world.SetGround(new WorldPos(Mapa, X - 1, Y - 1), Oro, 9_400);

        await TirarOro(dispatcher, sesion, 500);

        Assert.Equal(49_500, p.Gold);
        Assert.True(world.TryGetGround(new WorldPos(Mapa, X - 1, Y - 1), out var g) && g.Amount == 9_900,
            "los 500 tenían que apilarse sobre los 9.400 de la primera baldosa");
    }

    [Fact]
    public async Task Mas_de_cien_mil_no_se_tira()
    {
        var (world, p, sesion, dispatcher) = Armar(oro: 200_000);

        await TirarOro(dispatcher, sesion, 100_001);

        Assert.Equal(200_000, p.Gold);
        Assert.Empty(Montones(world));
    }
}
