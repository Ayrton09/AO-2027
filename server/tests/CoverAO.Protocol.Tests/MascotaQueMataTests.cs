using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE MATA UNA MASCOTA LO COBRA SU AMO. El original, cuando una criatura con dueño mata a otra, llama a
/// <c>MuereNpc(Victima, MaestroUser)</c> —en el golpe (SistemaCombate.bas:812) y en el hechizo
/// (modHechizos.bas:164)—, así que el amo se lleva la experiencia que le quedaba a la criatura, el aviso, el
/// conteo, el oro y el botín, igual que si la hubiera matado él. Acá la criatura sólo salía del mundo.
///
/// <para>Los dos caminos van por separado porque son dos llamadas distintas, y arreglar una sola deja la otra
/// igual de rota.</para>
/// </summary>
public class MascotaQueMataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_mascota_mata_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly NpcSystem _npcs;
    private readonly PacketDispatcher _dispatcher;

    private const short Campo = 14;   // Campos Abiertos: inseguro y despejado alrededor de (40,40)

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public MascotaQueMataTests()
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

    private Player AmoConectado()
    {
        var amo = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Campo, 40, 40), Hp = 100, MaxHp = 100, Level = 1,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = amo;
        amo.Connection = sesion;
        _world.Add(amo);
        return amo;
    }

    /// <summary>Una criatura salvaje quieta, con su experiencia entera en el pozo.</summary>
    private Npc Presa(int vida, int defensa, int evasion)
    {
        var ficha = new NpcInfo(Index: 900, Name: "Presa", Body: 1, Head: 1, Heading: 3, MaxHp: vida, MinHit: 1,
            MaxHit: 1, Def: defensa, GiveExp: 500, GiveGld: 0, PoderEvasion: evasion, PoderAtaque: 0, Movement: 1);
        _npcs.AddSpawn(ficha, new WorldPos(Campo, 42, 40), hostile: true, movement: 1);
        return _world.Npcs.Single(n => n.Template.Name == "Presa");
    }

    /// <summary>La mascota del amo, pegada a la presa y con ella de blanco.</summary>
    private Npc MascotaContra(Player amo, Npc presa, int ataque, IReadOnlyList<int>? hechizos = null)
    {
        var ficha = new NpcInfo(Index: 901, Name: "Mascota", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 5,
            MaxHit: 5, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: ataque) { Spells = hechizos };
        var mascota = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 100,
            Pos = new WorldPos(Campo, 41, 40), OwnerCharIndex = amo.CharIndex,
            Movement = GameLimits.TipoAiNpcAtacaNpc, TargetNpcCharIndex = presa.CharIndex,
        };
        _world.AddNpc(mascota);
        return mascota;
    }

    private void GirarHasta(Func<bool> listo, long vueltas)
    {
        for (long t = 1; t <= vueltas && !listo(); t++) _npcs.Update(t);
    }

    /// <summary>A GOLPES: la presa muere y el amo cobra la experiencia y la cuenta en sus criaturas muertas.</summary>
    [Fact]
    public void Lo_que_la_mascota_mata_a_golpes_lo_cobra_su_amo()
    {
        var amo = AmoConectado();
        var presa = Presa(vida: 1, defensa: 0, evasion: 0);
        MascotaContra(amo, presa, ataque: 1000);
        double expAntes = amo.Exp;

        GirarHasta(() => _world.NpcByIndex(presa.CharIndex) is null, 3000);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        Assert.Equal(1, amo.NpcsKilled);
        Assert.True(amo.Exp > expAntes || amo.Level > 1, "el amo no cobró la experiencia de lo que mató su mascota");
    }

    /// <summary>CON UN HECHIZO: el familiar que sabe Misil mágico la remata a distancia, y cobra igual su amo. La
    /// presa tiene defensa de sobra para que el golpe haga de a uno y no alcance a matarla en las vueltas del
    /// test: la única forma de que muera es el hechizo.</summary>
    [Fact]
    public void Lo_que_el_familiar_mata_con_un_hechizo_lo_cobra_su_amo()
    {
        var misil = _datos.Spell(Familiar.HabMisil);
        Assert.True(misil is { SubeHp: 2, MinHp: >= 20 }, "premisa: el Misil mágico ya no hace 20 o más de daño");
        var amo = AmoConectado();
        var presa = Presa(vida: 20, defensa: 1000, evasion: 1000);
        var familiar = MascotaContra(amo, presa, ataque: 0, hechizos: new[] { (int)Familiar.HabMisil });
        amo.Familiar.Tipo = Familiar.FuegoFatuo;
        amo.Familiar.CharIndexEnElMundo = familiar.CharIndex;
        double expAntes = amo.Exp;

        // 600 vueltas son 8 intentos de golpe como mucho: 8 de daño contra 20 de vida.
        GirarHasta(() => _world.NpcByIndex(presa.CharIndex) is null, 600);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        Assert.Equal(1, amo.NpcsKilled);
        Assert.True(amo.Exp > expAntes || amo.Level > 1, "el amo no cobró la experiencia de lo que mató su familiar");
    }
}
