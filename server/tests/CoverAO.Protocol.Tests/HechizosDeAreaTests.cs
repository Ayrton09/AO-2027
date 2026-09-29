using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// UN HECHIZO DE ÁREA SOBRE CRIATURAS, como el original (HechizoTerrenoEstado, modHechizos.bas:382-483): cada
/// criatura del 3×3 pasa por HechizoPropNPC —con PuedeAtacarNPC entero en el daño— y el `b` del área, por
/// referencia, decide si se cobra: sin criaturas se cobra siempre; con criaturas, manda la última. Acá el área
/// cobraba siempre, no pasaba por PuedeAtacarNPC (el Armada lastimaba criaturas sin dueño con una Bomba mágica) y
/// no goteaba experiencia.
/// </summary>
public class HechizosDeAreaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_area_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;                       // Desierto Rinkel: pk
    private const short BombaMagica = 8, CirculoCurativo = 75;
    private const byte X = 50, Y = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public HechizosDeAreaTests()
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

    private (Player Mago, ClientSession Sesion) Mago(short hechizo, byte faccion = Player.StatusCriminal)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Mapa, X, Y), Heading = 2, Hp = 100, MaxHp = 100, Level = 30, Class = 2,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, CombatMode = true, FactionStatus = faccion,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = hechizo;
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private Npc Criatura(byte x, byte y, bool hostil = true, bool atacable = true)
    {
        var ficha = new NpcInfo(Index: 905, Name: "Bicho de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 1000, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: hostil, Attackable: atacable, Movement: 1);
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 500, Pos = new WorldPos(Mapa, x, y), Hostile = hostil, Attackable = atacable };
        _world.AddNpc(npc);
        return npc;
    }

    private async Task CastearEn(ClientSession sesion, byte x, byte y)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    [Fact]
    public async Task El_area_le_pega_a_una_criatura_cobra_y_gotea_experiencia()
    {
        Assert.True(_datos.Spell(BombaMagica)!.IsArea);                          // premisa
        var (mago, sesion) = Mago(BombaMagica);
        var bicho = Criatura(X + 3, Y);
        double exp = mago.Exp;
        await CastearEn(sesion, X + 3, Y);
        Assert.True(bicho.Hp < 500, "la bomba tenía que pegarle");
        Assert.True(mago.Mana < 5000);
        Assert.True(mago.Exp > exp, "el golpe de área gotea experiencia, como el de un solo blanco");
    }

    [Fact]
    public async Task El_armada_no_le_pega_con_un_area_a_una_criatura_sin_duenio_ni_cobra()
    {
        var (mago, sesion) = Mago(BombaMagica, Player.StatusArmada);
        var bicho = Criatura(X + 3, Y);
        await CastearEn(sesion, X + 3, Y);
        Assert.Equal(500, bicho.Hp);                                             // el 272
        Assert.Equal(5000, mago.Mana);                                           // la última criatura apagó `b`
    }

    [Fact]
    public async Task El_area_sobre_una_criatura_que_no_se_ataca_no_cobra()
    {
        var (mago, sesion) = Mago(BombaMagica);
        var quieta = Criatura(X + 3, Y, hostil: false, atacable: false);
        await CastearEn(sesion, X + 3, Y);
        Assert.Equal(500, quieta.Hp);                                            // el 298
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Curar_en_area_a_una_criatura_hostil_no_cobra()
    {
        var (mago, sesion) = Mago(CirculoCurativo);
        var hostil = Criatura(X + 3, Y);
        await CastearEn(sesion, X + 3, Y);
        Assert.Equal(500, hostil.Hp);
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Un_area_sin_criaturas_se_cobra_siempre()
    {
        var (mago, sesion) = Mago(CirculoCurativo);
        await CastearEn(sesion, X + 3, Y);                                       // nadie en el 3×3
        Assert.True(mago.Mana < 5000);
    }
}
