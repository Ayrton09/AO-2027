using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ORO DE UNA CRIATURA CON LA TASA AL TOPE (el tester, 06/10: "mató un lobo con Oro=32767 y no le dio oro"). El Lobo que nace
/// en los mapas (ficha 501) da 50: con x32767 son 1.638.350, que entran de sobra.
/// </summary>
public class ElOroConTasaAltaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_oro_tasa_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly NpcSystem _npcs;
    private readonly PacketDispatcher _dispatcher;
    private const short Campo = 14;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ElOroConTasaAltaTests()
    {
        Directory.CreateDirectory(_root);
        string ruta = Path.Combine(_root, "Server.ini");
        File.WriteAllText(ruta, "[INIT]\r\nStartPort=7666\r\nOro=32767\r\nExp=32767\r\n");
        var leveling = new Leveling(_datos.LevelExp);
        _npcs = new NpcSystem(_world, _datos);
        _dispatcher = new PacketDispatcher(_world, new GameLoop(), new Persistence(AlmacenSqlite.EnMemoria(), leveling),
            _datos, leveling, npcs: _npcs, serverIni: ServerIni.Cargar(ruta));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void El_Lobo_paga_su_oro_por_la_tasa_aunque_sea_32767()
    {
        if (_datos.Npc(501) is not { Name: "Lobo", GiveGld: 50 } lobo) return;
        var amo = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Campo, 40, 40), Hp = 100, MaxHp = 100, Level = 1,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = amo; amo.Connection = sesion;
        _world.Add(amo);
        _npcs.AddSpawn(lobo with { MaxHp = 1, MinHit = 0, MaxHit = 0, Def = 0, PoderEvasion = 0 }, new WorldPos(Campo, 42, 40), hostile: true, movement: 1);
        var presa = _world.Npcs.Single(n => n.Template.Index == 501);
        var ficha = new NpcInfo(Index: 901, Name: "Mascota", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 5,
            MaxHit: 5, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 1000);
        _world.AddNpc(new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 100, Pos = new WorldPos(Campo, 41, 40),
            OwnerCharIndex = amo.CharIndex, Movement = GameLimits.TipoAiNpcAtacaNpc, TargetNpcCharIndex = presa.CharIndex,
        });

        for (long t = 1; t <= 3000 && _world.NpcByIndex(presa.CharIndex) is not null; t++) _npcs.Update(t);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        Assert.Equal(50L * 32767, amo.Gold);
    }
}
