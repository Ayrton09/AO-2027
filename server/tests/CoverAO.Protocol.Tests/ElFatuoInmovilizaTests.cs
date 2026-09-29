using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL INMOVILIZAR DEL FUEGO FATUO INMOVILIZA. El familiar aprende Inmovilizar (24) y sobre otra criatura lo aplicaba
/// como PARÁLISIS: la dejaba sin IA. El del jugador la deja sin caminar pero pegando (CastStateOnNpc), y el autor
/// decidió igualarlos (22/09): cada hechizo prende su marca.
/// </summary>
public class ElFatuoInmovilizaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_fatuo_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly NpcSystem _npcs;
    private readonly PacketDispatcher _dispatcher;

    private const short Campo = 14;   // Campos Abiertos: inseguro y despejado alrededor de (40,40)
    private const short Inmovilizar = 24;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ElFatuoInmovilizaTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _npcs = new NpcSystem(_world, _datos);
        _dispatcher = new PacketDispatcher(_world, new GameLoop(), new Persistence(AlmacenSqlite.EnMemoria(), leveling),
            _datos, leveling, npcs: _npcs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    [Fact]
    public void El_inmovilizar_del_fatuo_deja_a_la_criatura_inmovilizada_y_no_paralizada()
    {
        Assert.True(_datos.Spell(Inmovilizar) is { IsImmobilize: true, IsParalyze: false });   // premisa: la data
        var amo = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Campo, 40, 40), Hp = 100, MaxHp = 100, Level = 1,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = amo;
        amo.Connection = sesion;
        _world.Add(amo);

        var fichaPresa = new NpcInfo(Index: 900, Name: "Presa", Body: 1, Head: 1, Heading: 3, MaxHp: 100_000, MinHit: 1,
            MaxHit: 1, Def: 1000, GiveExp: 0, GiveGld: 0, PoderEvasion: 1000, PoderAtaque: 0, Movement: 1);
        _npcs.AddSpawn(fichaPresa, new WorldPos(Campo, 42, 40), hostile: true, movement: 1);
        var presa = _world.Npcs.Single(n => n.Template.Name == "Presa");

        var fichaFatuo = new NpcInfo(Index: 901, Name: "Fatuo", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0) { Spells = new[] { (int)Inmovilizar } };
        var fatuo = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = fichaFatuo, Hp = 100,
            Pos = new WorldPos(Campo, 41, 40), OwnerCharIndex = amo.CharIndex,
            Movement = GameLimits.TipoAiNpcAtacaNpc, TargetNpcCharIndex = presa.CharIndex,
        };
        _world.AddNpc(fatuo);
        amo.Familiar.Tipo = Familiar.FuegoFatuo;
        amo.Familiar.CharIndexEnElMundo = fatuo.CharIndex;

        long t = 1;
        for (; t <= 6000 && !presa.EstaQuieta(t); t++) _npcs.Update(t);
        Assert.True(presa.EstaQuieta(t), "premisa: el fatuo llegó a tirarle el Inmovilizar");
        Assert.True(presa.EstaInmovilizada(t), "inmovilizada: sin caminar, pero con IA");
        Assert.False(presa.EstaParalizada(t));
    }
}
