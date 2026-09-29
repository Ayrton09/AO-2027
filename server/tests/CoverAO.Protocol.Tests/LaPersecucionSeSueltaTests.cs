using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA CRIATURA QUE TE PERSEGUÍA TE SUELTA. El original anota en el jugador la primera criatura que lo ataca
/// (`AtacadoPorNpc`, SistemaCombate.bas:719) y al salir del mapa, morir o desconectarse la devuelve al movimiento de su
/// ficha y la hace olvidarse del agresor (GameLogic.bas:117, Modulo_UsUaRiOs.bas:1272, TCP.bas:1579). Sin el bug de
/// allá (`Attackable = OldHostil`, que dejaba inatacable a una pacífica provocada): vuelve su hostilidad, por decisión
/// del autor. Acá no existía.
/// </summary>
public class LaPersecucionSeSueltaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_persecucion_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    private sealed class Conexion : IConnection
    {
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) { }
        public void Close(string reason) { }
    }

    public LaPersecucionSeSueltaTests()
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

    private static NpcInfo Ficha(string nombre) => new(Index: 1, Name: nombre, Body: 1, Head: 1, Heading: 4, MaxHp: 1000,
        MinHit: 1, MaxHit: 1, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);

    /// <summary>Una pacífica que el jugador provocó y que lo está persiguiendo.</summary>
    private static Npc Provocada(World world, WorldPos pos, Player agresor)
    {
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = Ficha("Aldeano"), Hp = 1000, Pos = pos,
            Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = false, AttackedBy = agresor.Name,
        };
        world.AddNpc(npc);
        agresor.AtacadoPorNpc = npc.CharIndex;
        return npc;
    }

    private static void AssertSuelta(Npc npc, Player p)
    {
        Assert.Equal(0, p.AtacadoPorNpc);
        Assert.False(npc.Hostile, "vuelve a la hostilidad de su ficha");
        Assert.Null(npc.AttackedBy);
        Assert.True(npc.Attackable, "sin el bug del original: se la sigue pudiendo atacar");
    }

    [Fact]
    public void La_primera_criatura_que_ataca_queda_anotada()
    {
        var world = new World();
        var p = new Player { CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Connection = new Conexion() };
        p.Pos = new WorldPos(1, 10, 10);
        p.MaxHp = p.Hp = 1_000_000;
        world.Add(p);
        Npc Hostil(byte x) { var n = new Npc { CharIndex = world.AllocateCharIndex(), Template = Ficha("Lobo"), Hp = 1000,
            Pos = new WorldPos(1, x, 10), Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = true, Heading = 4 };
            world.AddNpc(n); return n; }
        var primera = Hostil(11);
        var sistema = new NpcSystem(world, _datos);
        for (int vuelta = 1; vuelta <= 750 && p.AtacadoPorNpc == 0; vuelta++) sistema.Update(vuelta);
        Assert.Equal(primera.CharIndex, p.AtacadoPorNpc);

        Hostil(9);                                                                 // otra del otro lado
        for (int vuelta = 751; vuelta <= 1500; vuelta++) sistema.Update(vuelta);
        Assert.Equal(primera.CharIndex, p.AtacadoPorNpc);                          // se queda la primera
    }

    [Fact]
    public void Al_morir_la_suelta()
    {
        var p = new Player { CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Connection = new Conexion() };
        p.Pos = new WorldPos(15, 50, 50);
        p.MaxHp = 100; p.Hp = 0;
        _world.Add(p);
        var npc = Provocada(_world, new WorldPos(15, 51, 50), p);
        PacketDispatcher.KillPlayer(_world, _datos, p, "test");
        AssertSuelta(npc, p);
    }

    [Fact]
    public async Task Al_desconectarse_la_suelta()
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(1, 50, 50),
            Hp = 100, MaxHp = 100,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        var npc = Provocada(_world, new WorldPos(1, 51, 50), p);
        Assert.False(_datos.IsPkMap(1));                                           // premisa: se va en el acto
        sesion.Close("test");
        await Task.Delay(300);
        Assert.DoesNotContain(p, _world.Players);
        AssertSuelta(npc, p);
    }

    [Fact]
    public async Task Al_pisar_una_salida_la_suelta()
    {
        // Una salida de la data con una baldosa libre al oeste, para llegar caminando al este.
        var (mapa, x, y) = _datos.MapExits
            .Where(e => e.Value.DestMap > 0 && e.Key.X > 2)
            .Select(e => e.Key)
            .First(k => !_datos.IsBlockedTile(k.Map, k.X, k.Y) && !_datos.IsBlockedTile(k.Map, k.X - 1, k.Y)
                        && !_datos.IsWater(k.Map, k.X - 1, k.Y) && !_datos.IsWater(k.Map, k.X, k.Y)
                        && !_datos.MapExits.ContainsKey((k.Map, k.X - 1, k.Y)));
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos((short)mapa, (byte)(x - 1), (byte)y),
            Heading = 2, Hp = 100, MaxHp = 100, Level = 1, Stamina = 100, MaxStamina = 100,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        var npc = Provocada(_world, new WorldPos((short)mapa, (byte)(x - 1), (byte)(y - 1)), p);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.Walk, (byte)2 }));
        await Task.Delay(300);
        AssertSuelta(npc, p);
    }
}
