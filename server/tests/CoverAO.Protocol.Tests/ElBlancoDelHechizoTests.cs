using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// A QUIÉN LE PEGA UN HECHIZO. En el original el blanco es el que dejó <c>LookatTile</c> —primero la baldosa de
/// ABAJO del clic, y en la misma baldosa la criatura le gana al jugador (GameLogic.bas:733-752)—, y
/// <c>LanzarHechizo</c> lo filtra por el <c>Target</c> del hechizo (modHechizos.bas:891-938): 1 sólo jugadores, 2
/// sólo criaturas, 3 cualquiera. Sobre una criatura manda el Tipo (HandleHechizoNPC); sobre un jugador muerto, el
/// 7. Acá se leía la baldosa cruda del clic, y el hechizo que no encontraba a nadie "caía" sobre el que lo tiraba.
/// </summary>
public class ElBlancoDelHechizoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_blanco_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;                                   // Desierto Rinkel: pk
    private const short ProyectilMagico = 2, CurarHeridasLeves = 3, LluviaDeMosquitos = 35, HonorDelPaladin = 89;
    private const byte X = 50, Y = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ElBlancoDelHechizoTests()
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

    /// <summary>Un mago con todo de sobra y en modo combate. NO es GM: así se ve si se cobra el maná.</summary>
    private (Player Mago, ClientSession Sesion) Mago(short hechizo)
    {
        var p = Jugador("Aldo", X, Y);
        p.Level = 30; p.Class = 2; p.CombatMode = true;
        p.Mana = 5000; p.MaxMana = 5000; p.Stamina = 5000; p.MaxStamina = 5000;
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = hechizo;
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        return (p, sesion);
    }

    private Player Jugador(string nombre, byte x, byte y)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(Mapa, x, y), Heading = 2, Hp = 50, MaxHp = 100, Level = 30,
            FactionStatus = Player.StatusCriminal,
        };
        _world.Add(p);
        return p;
    }

    private Npc Criatura(byte x, byte y, bool hostil)
    {
        var ficha = new NpcInfo(Index: 904, Name: "Bicho de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 10, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: hostil, Attackable: true, Movement: 1);
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 500, Pos = new WorldPos(Mapa, x, y), Hostile = hostil };
        _world.AddNpc(npc);
        return npc;
    }

    private async Task Castear(ClientSession sesion, byte x, byte y)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    // ---- el Target ------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_proyectil_magico_no_le_pega_a_un_jugador()
    {
        Assert.Equal(2, _datos.Spell(ProyectilMagico)!.Target);                  // premisa: "Sólo afecta NPCs"
        var (mago, sesion) = Mago(ProyectilMagico);
        var victima = Jugador("Bruno", X + 2, Y);
        await Castear(sesion, X + 2, Y);                                         // los pies: LookatTile la elige igual
        Assert.Equal(50, victima.Hp);
        Assert.Equal(5000, mago.Mana);                                           // el 224 sale sin cobrar
    }

    [Fact]
    public async Task La_lluvia_de_mosquitos_no_paraliza_criaturas()
    {
        Assert.Equal(1, _datos.Spell(LluviaDeMosquitos)!.Target);                // premisa: "Sólo afecta usuarios"
        var (mago, sesion) = Mago(LluviaDeMosquitos);
        var bicho = Criatura(X + 2, Y, hostil: true);
        await Castear(sesion, X + 2, Y);
        Assert.Equal(0, bicho.ParalyzedUntilTick);
        Assert.Equal(5000, mago.Mana);                                           // el 223 sale sin cobrar
    }

    // ---- LookatTile: la baldosa de abajo -----------------------------------------------------------------

    [Fact]
    public async Task Clickear_la_cabeza_de_una_criatura_la_elige()
    {
        var (_, sesion) = Mago(ProyectilMagico);
        var bicho = Criatura(X + 2, Y, hostil: true);
        await Castear(sesion, X + 2, Y - 1);                                     // la cabeza, no los pies
        Assert.True(bicho.Hp < 500, "clickear la cabeza tenía que elegir a la criatura de abajo");
    }

    [Fact]
    public async Task Castear_sobre_una_criatura_le_borra_el_objetivo()
    {
        var (mago, sesion) = Mago(ProyectilMagico);
        Criatura(X + 2, Y, hostil: true);
        await Castear(sesion, X + 2, Y - 1);
        Assert.True(mago.Mana < 5000, "premisa: el hechizo se lanzó");
        Assert.Equal(0, mago.TargetNpc);                                         // HandleHechizoNPC, TargetNPC = 0
    }

    // ---- nada cae sobre el que lo tira -------------------------------------------------------------------

    [Fact]
    public async Task Curar_un_cadaver_da_el_7_y_no_cura_al_que_lo_tira()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        mago.Hp = 50;
        var muerto = Jugador("Bruno", X + 2, Y);
        muerto.Hp = 0;
        await Castear(sesion, X + 2, Y - 1);
        Assert.Equal(50, mago.Hp);
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Curar_al_aire_no_cura_al_que_lo_tira()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        mago.Hp = 50;
        await Castear(sesion, X + 3, Y - 3);                                     // nadie ahí
        Assert.Equal(50, mago.Hp);
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Curar_la_propia_cabeza_cura_al_que_lo_tira()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        mago.Hp = 50;
        await Castear(sesion, X, Y - 1);
        Assert.True(mago.Hp > 50, "clickearse la cabeza tenía que curarlo");
    }

    /// <summary>La premisa del bot que se cura: clickearse la cabeza lo elige a él aunque haya otro parado justo al
    /// sur (clickearse los pies elegiría a ése).</summary>
    [Fact]
    public async Task La_propia_cabeza_elige_a_uno_mismo_aunque_haya_alguien_al_sur()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        mago.Hp = 50;
        var vecino = Jugador("Bruno", X, Y + 1);
        await Castear(sesion, X, Y - 1);
        Assert.True(mago.Hp > 50, "clickearse la cabeza tenía que curarlo a él");
        Assert.Equal(50, vecino.Hp);
    }

    /// <summary>La premisa del bot que ataca: la cabeza de una criatura la elige a ella aunque haya otra al sur.</summary>
    [Fact]
    public async Task La_cabeza_de_una_criatura_la_elige_aunque_haya_otra_al_sur()
    {
        var (_, sesion) = Mago(ProyectilMagico);
        var blanco = Criatura(X + 2, Y, hostil: true);
        var otra = Criatura(X + 2, Y + 1, hostil: true);
        await Castear(sesion, X + 2, Y - 1);
        Assert.True(blanco.Hp < 500, "la de la cabeza clickeada tenía que recibir el hechizo");
        Assert.Equal(500, otra.Hp);
    }

    /// <summary>Clickearse la cabeza con Paralizar se elige a uno mismo, y un estado ofensivo sobre uno mismo no se
    /// lanza: el 298, sin cobrar (modHechizos.bas:150-154).</summary>
    [Fact]
    public async Task Paralizarse_a_uno_mismo_no_se_lanza()
    {
        const short Paralizar = 9;
        var (mago, sesion) = Mago(Paralizar);
        await Castear(sesion, X, Y - 1);
        Assert.Equal(0, mago.ParalyzedUntilTick);
        Assert.Equal(5000, mago.Mana);
    }

    // ---- sobre una criatura manda el Tipo --------------------------------------------------------------

    [Fact]
    public async Task Curar_a_una_criatura_mansa_la_cura_a_ella()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        mago.Hp = 50;
        var mansa = Criatura(X + 2, Y, hostil: false);
        await Castear(sesion, X + 2, Y - 1);
        Assert.True(mansa.Hp > 500, "la criatura mansa tenía que curarse");
        Assert.Equal(50, mago.Hp);
    }

    [Fact]
    public async Task Curar_a_una_criatura_hostil_no_hace_nada_ni_cobra()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        var hostil = Criatura(X + 2, Y, hostil: true);
        await Castear(sesion, X + 2, Y - 1);
        Assert.Equal(500, hostil.Hp);
        Assert.Equal(5000, mago.Mana);
    }

    // ---- el autolanzar ------------------------------------------------------------------------------------

    /// <summary>El autolanzar no pasa por LookatTile: con alguien parado justo al sur, el que se cura es el que lo
    /// tira.</summary>
    [Fact]
    public async Task El_autolanzado_cae_sobre_uno_mismo_aunque_haya_alguien_al_sur()
    {
        Assert.True(_datos.Spell(HonorDelPaladin)!.EsAutoLanzado);               // premisa
        var (mago, sesion) = Mago(HonorDelPaladin);
        mago.Hp = 50;
        var vecino = Jugador("Bruno", X, Y + 1);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(300);
        Assert.True(mago.Hp > 50, "el autolanzado tenía que curar al que lo tira");
        Assert.Equal(50, vecino.Hp);
    }
}
