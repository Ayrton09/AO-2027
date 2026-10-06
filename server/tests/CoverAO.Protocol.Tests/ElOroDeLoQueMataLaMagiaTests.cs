using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ORO DE LO QUE MATA UN HECHIZO, con la tasa al tope (el tester, 06/10: un Mago Gnomo mató un Lobo con magia y Oro=32767 y "no le
/// dio oro"). El Lobo de los mapas (ficha 501) da 50: con x32767 son 1.638.350.
/// </summary>
public class ElOroDeLoQueMataLaMagiaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_oro_magia_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private const short Mapa = 15, ProyectilMagico = 2;
    private const byte X = 50, Y = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ElOroDeLoQueMataLaMagiaTests()
    {
        Directory.CreateDirectory(_root);
        string ruta = Path.Combine(_root, "Server.ini");
        File.WriteAllText(ruta, "[INIT]\r\nStartPort=7666\r\nOro=32767\r\nExp=32767\r\n");
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling,
            serverIni: ServerIni.Cargar(ruta));
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task El_Mago_que_mata_un_Lobo_con_un_hechizo_cobra_el_oro_por_la_tasa()
    {
        if (_datos.Npc(501) is not { Name: "Lobo", GiveGld: 50 } lobo || _datos.Spell(ProyectilMagico) is null) return;
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Gnomito", Account = "gnomito", Pos = new WorldPos(Mapa, X, Y), Heading = 2,
            Hp = 100, MaxHp = 100, Level = 30, Class = 2, CombatMode = true, Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = ProyectilMagico;
        _world.Add(p);
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p; p.Connection = sesion;
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = lobo, Hp = 1, Pos = new WorldPos(Mapa, X + 2, Y), Hostile = true };
        _world.AddNpc(npc);

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)(X + 2), Y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(400);

        Assert.Null(_world.NpcByIndex(npc.CharIndex));
        Assert.Equal(50L * 32767, p.Gold);
    }
}
