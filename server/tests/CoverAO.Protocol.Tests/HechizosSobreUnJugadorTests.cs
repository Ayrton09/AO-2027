using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// UN HECHIZO SOBRE UN JUGADOR, COMO EL ORIGINAL (HechizoEstadoUsuario, modHechizos.bas:956-1529): una secuencia de
/// bloques, uno por efecto, donde cada uno decide si el hechizo quedó lanzado. De ahí salen reglas que el port no
/// tenía: ayudar pide la facción (salvo en un duelo de arena, y la sanación ni ahí), curar veneno a un sano o
/// remover la parálisis de quien no está quieto no cobra, curar veneno a quien no se puede ayudar cobra sin curar,
/// paralizar al que ya está quieto no hace nada, y un hechizo con varios efectos los aplica todos.
/// </summary>
public class HechizosSobreUnJugadorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_sobre_jugador_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15, IslaBeleta = 47;          // pk; y el mapa con arena
    private const short CurarVeneno = 1, CurarHeridasLeves = 3, Paralizar = 9, RemoverParalisis = 10, Sanar = 32,
                        LluviaDeMosquitos = 35, HonorDelPaladin = 89, PrevencionDelMal = 90;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public HechizosSobreUnJugadorTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private Player Jugador(string nombre, short mapa, byte x, byte y, byte faccion)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, y), Heading = 2, Hp = 50, MaxHp = 100, Level = 30, FactionStatus = faccion,
        };
        _world.Add(p);
        return p;
    }

    /// <summary>Un mago con todo de sobra y en modo combate. NO es GM: así se ve si se cobra el maná.</summary>
    private (Player Mago, ClientSession Sesion) Mago(short hechizo, byte faccion = Player.StatusCiudadano, short mapa = Desierto,
        byte x = 50, byte y = 50)
    {
        var p = Jugador("Aldo", mapa, x, y, faccion);
        p.Class = 2; p.CombatMode = true;
        p.Mana = 5000; p.MaxMana = 5000; p.Stamina = 5000; p.MaxStamina = 5000;
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = hechizo;
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        return (p, sesion);
    }

    /// <summary>Castea clickeando la CABEZA del blanco (la baldosa de arriba de sus pies).</summary>
    private async Task Castear(ClientSession sesion, Player blanco)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, blanco.Pos.X, (byte)(blanco.Pos.Y - 1), (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    private long Ahora => _loop.Tick;

    // ---- curar veneno ------------------------------------------------------------------------------------

    [Fact]
    public async Task Curar_veneno_a_un_sano_no_cobra()
    {
        var (mago, sesion) = Mago(CurarVeneno);
        var sano = Jugador("Bruno", Desierto, 52, 50, Player.StatusCiudadano);
        await Castear(sesion, sano);
        Assert.Equal(5000, mago.Mana);                                         // el 451, sin cobrar
    }

    [Fact]
    public async Task Curar_veneno_a_quien_no_se_puede_ayudar_cobra_y_no_cura()
    {
        var (mago, sesion) = Mago(CurarVeneno);                               // ciudadano
        var criminal = Jugador("Bruno", Desierto, 52, 50, Player.StatusCriminal);
        criminal.PoisonedUntilTick = long.MaxValue;
        await Castear(sesion, criminal);
        Assert.True(criminal.IsPoisoned(Ahora), "no se lo puede ayudar: sigue envenenado");
        Assert.True(mago.Mana < 5000, "y el original cobra igual (HechizoCasteado = True)");
    }

    [Fact]
    public async Task Curar_veneno_a_un_aliado_lo_cura()
    {
        var (mago, sesion) = Mago(CurarVeneno);
        var aliado = Jugador("Bruno", Desierto, 52, 50, Player.StatusCiudadano);
        aliado.PoisonedUntilTick = long.MaxValue;
        await Castear(sesion, aliado);
        Assert.False(aliado.IsPoisoned(Ahora));
        Assert.True(mago.Mana < 5000);
    }

    // ---- remover parálisis -------------------------------------------------------------------------------

    [Fact]
    public async Task Remover_paralisis_a_quien_no_esta_quieto_no_cobra()
    {
        var (mago, sesion) = Mago(RemoverParalisis);
        var aliado = Jugador("Bruno", Desierto, 52, 50, Player.StatusCiudadano);
        await Castear(sesion, aliado);
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Remover_paralisis_a_otra_faccion_no_libera_ni_cobra()
    {
        var (mago, sesion) = Mago(RemoverParalisis);
        var criminal = Jugador("Bruno", Desierto, 52, 50, Player.StatusCriminal);
        criminal.ParalyzedUntilTick = long.MaxValue;
        await Castear(sesion, criminal);
        Assert.Equal(long.MaxValue, criminal.ParalyzedUntilTick);
        Assert.Equal(5000, mago.Mana);
    }

    // ---- curar, y la arena ----------------------------------------------------------------------------------

    [Fact]
    public async Task Curar_a_otra_faccion_fuera_de_la_arena_no_cura()
    {
        var (mago, sesion) = Mago(CurarHeridasLeves);
        var criminal = Jugador("Bruno", Desierto, 52, 50, Player.StatusCriminal);
        await Castear(sesion, criminal);
        Assert.Equal(50, criminal.Hp);
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public async Task Curar_a_otra_faccion_en_un_duelo_de_arena_si_cura()
    {
        Assert.True(_datos.IsArena(IslaBeleta, 80, 32) && _datos.IsArena(IslaBeleta, 82, 32));   // premisa
        var (mago, sesion) = Mago(CurarHeridasLeves, mapa: IslaBeleta, x: 80, y: 32);
        var criminal = Jugador("Bruno", IslaBeleta, 82, 32, Player.StatusCriminal);
        await Castear(sesion, criminal);
        Assert.True(criminal.Hp > 50, "en la arena se cura a cualquiera");
    }

    [Fact]
    public async Task La_sanacion_no_ayuda_a_otra_faccion_ni_en_la_arena()
    {
        var (mago, sesion) = Mago(Sanar, mapa: IslaBeleta, x: 80, y: 32);
        var criminal = Jugador("Bruno", IslaBeleta, 82, 32, Player.StatusCriminal);
        await Castear(sesion, criminal);
        Assert.Equal(50, criminal.Hp);                                         // el 449 de la sanación, primero
        Assert.Equal(5000, mago.Mana);
    }

    // ---- varios efectos -----------------------------------------------------------------------------------

    [Fact]
    public async Task Honor_del_paladin_sube_agilidad_y_fuerza_ademas_de_curar()
    {
        var (mago, sesion) = Mago(HonorDelPaladin);
        int agilidad = mago.Agility, fuerza = mago.Strength;
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));   // autolanzado
        await Task.Delay(300);
        Assert.True(mago.Hp > 50, "tenía que curar");
        Assert.True(mago.Agility > agilidad, "tenía que subir la agilidad");
        Assert.True(mago.Strength > fuerza, "tenía que subir la fuerza");
    }

    [Fact]
    public async Task Paralizar_al_que_ya_esta_quieto_no_hace_nada_ni_cobra()
    {
        var (mago, sesion) = Mago(Paralizar, faccion: Player.StatusCriminal);
        var otro = Jugador("Bruno", Desierto, 52, 50, Player.StatusCriminal);
        otro.ParalyzedUntilTick = long.MaxValue;
        await Castear(sesion, otro);
        Assert.Equal(5000, mago.Mana);
    }

    /// <summary>La Lluvia de Mosquitos paraliza Y ciega: sobre alguien ya quieto no paraliza, pero la ceguera igual
    /// entra y el hechizo se cobra.</summary>
    [Fact]
    public async Task La_lluvia_sobre_alguien_ya_quieto_igual_ciega_y_cobra()
    {
        var (mago, sesion) = Mago(LluviaDeMosquitos, faccion: Player.StatusCriminal);
        var otro = Jugador("Bruno", Desierto, 52, 50, Player.StatusCriminal);
        otro.ParalyzedUntilTick = long.MaxValue;
        await Castear(sesion, otro);
        Assert.True(otro.BlindedUntilTick > Ahora, "la ceguera tenía que entrar");
        Assert.True(mago.Mana < 5000);
    }

    /// <summary>Re-lanzar la invisibilidad estando invisible: el 68 del original no sale nunca (compara un Byte con
    /// True), así que se vuelve a aplicar y se cobra, sin estirar el vencimiento.</summary>
    [Fact]
    public async Task Re_lanzar_la_invisibilidad_se_cobra_y_no_estira_el_vencimiento()
    {
        const short Invisibilidad = 14;
        Assert.True(_datos.Spell(Invisibilidad)!.EsAutoLanzado);                // premisa
        var (mago, sesion) = Mago(Invisibilidad);
        mago.Invisible = true;
        mago.InvisibleUntilTick = _loop.Tick + 5000;
        long vence = mago.InvisibleUntilTick;
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(300);
        Assert.True(mago.Mana < 5000, "re-lanzarla se cobra");
        Assert.Equal(vence, mago.InvisibleUntilTick);
    }

    /// <summary>Prevención del mal sobre otro: la invisibilidad es del que lo tira (el bloque escribe sus flags) y
    /// la agilidad, del blanco.</summary>
    [Fact]
    public async Task Prevencion_del_mal_sobre_otro_hace_invisible_al_que_lo_tira_y_agil_al_otro()
    {
        var (mago, sesion) = Mago(PrevencionDelMal);
        var aliado = Jugador("Bruno", Desierto, 52, 50, Player.StatusCiudadano);
        int agilidadMago = mago.Agility, agilidadAliado = aliado.Agility;
        await Castear(sesion, aliado);
        Assert.True(mago.Invisible, "el que lo tira queda invisible");
        Assert.True(aliado.Agility > agilidadAliado, "el blanco gana la agilidad");
        Assert.Equal(agilidadMago, mago.Agility);
    }
}
