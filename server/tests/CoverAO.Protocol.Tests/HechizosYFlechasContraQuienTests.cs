using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS AGRESIONES A UNA CRIATURA pasan por <c>PuedeAtacarNPC</c> (SistemaCombate.bas:1729): el modo combate antes
/// que nada. Eso vale para el daño y la parálisis con hechizos y para la flecha; y a la flecha, además, la regla
/// del Armada. Si no puede, no se cobra el maná y no se gasta la flecha.
/// </summary>
public class HechizosYFlechasContraQuienTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_contra_quien_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;                       // Desierto Rinkel: pk
    private const short ProyectilMagico = 2, Paralizar = 9;
    private const short ArcoSimple = 989, Flecha = 480;   // Arco Simple: Proyectil 1, Municiones 1
    private const byte X = 50, Y = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public HechizosYFlechasContraQuienTests()
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

    /// <summary>Un mago con todo de sobra: skill, maná y energía. NO es GM: así se ve si se cobra el maná.</summary>
    private (Player Mago, ClientSession Sesion) Mago(bool modoCombate, short hechizo = 0)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Mapa, X, Y), Heading = 2, Hp = 100, MaxHp = 100, Level = 30, Class = 2,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, CombatMode = modoCombate,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Skills[GameLimits.SkillProyectiles] = 100;
        if (hechizo > 0) p.Spells[1] = hechizo;
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private Npc Criatura(byte x)
    {
        var ficha = new NpcInfo(Index: 900, Name: "Lobo de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 10, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(Mapa, x, Y) };
        _world.AddNpc(npc);
        return npc;
    }

    private async Task Castear(ClientSession sesion, byte x)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, Y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    private async Task Disparar(ClientSession sesion, byte x)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, Y, (byte)GameLimits.SkillProyectiles }));
        await Task.Delay(300);
    }

    [Fact]
    public async Task Sin_modo_combate_un_hechizo_no_le_pega_a_una_criatura()
    {
        var (mago, sesion) = Mago(modoCombate: false, ProyectilMagico);
        var lobo = Criatura(X + 2);
        await Castear(sesion, X + 2);
        Assert.Equal(1000, lobo.Hp);
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Sin_modo_combate_no_se_paraliza_a_una_criatura()
    {
        var (mago, sesion) = Mago(modoCombate: false, Paralizar);
        var lobo = Criatura(X + 2);
        await Castear(sesion, X + 2);
        Assert.Equal(0, lobo.ParalyzedUntilTick);
        Assert.Equal(5000, mago.Mana);
    }

    /// <summary>Y con el modo combate, el mismo hechizo SÍ le pega: el control de que el test mira algo.</summary>
    [Fact]
    public async Task Con_modo_combate_el_hechizo_le_pega_a_la_criatura()
    {
        var (mago, sesion) = Mago(modoCombate: true, ProyectilMagico);
        var lobo = Criatura(X + 2);
        await Castear(sesion, X + 2);
        Assert.True(lobo.Hp < 1000, "con modo combate el hechizo tenía que pegar");
        Assert.True(mago.Mana < 5000);
    }

    private (Player Arquero, ClientSession Sesion) Arquero(bool modoCombate)
    {
        var (p, sesion) = Mago(modoCombate);
        p.Inventory[1] = new InventorySlot { ObjIndex = ArcoSimple, Amount = 1, Equipped = true };
        p.Inventory[2] = new InventorySlot { ObjIndex = Flecha, Amount = 50, Equipped = true };
        return (p, sesion);
    }

    [Fact]
    public async Task Sin_modo_combate_la_flecha_no_le_pega_a_una_criatura_ni_se_gasta()
    {
        var (p, sesion) = Arquero(modoCombate: false);
        var lobo = Criatura(X + 2);
        await Disparar(sesion, X + 2);
        Assert.Equal(1000, lobo.Hp);
        Assert.Equal(50, p.Inventory[2].Amount);
    }

    [Fact]
    public async Task El_armada_no_le_tira_flechas_a_una_criatura_sin_duenio()
    {
        var (p, sesion) = Arquero(modoCombate: true);
        p.FactionStatus = Player.StatusArmada;
        var lobo = Criatura(X + 2);
        await Disparar(sesion, X + 2);
        Assert.Equal(1000, lobo.Hp);
        Assert.Equal(50, p.Inventory[2].Amount);                             // el 272 no gasta la flecha
    }

    /// <summary>El control: un renegado con modo combate le tira y la flecha se gasta.</summary>
    [Fact]
    public async Task Con_modo_combate_la_flecha_se_tira_y_se_gasta()
    {
        var (p, sesion) = Arquero(modoCombate: true);
        p.FactionStatus = Player.StatusCriminal;
        Criatura(X + 2);
        await Disparar(sesion, X + 2);
        Assert.Equal(49, p.Inventory[2].Amount);
    }
}
