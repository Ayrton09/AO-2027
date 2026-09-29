using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// RESUCITAR TIENE ESPERA. En el original ningún camino revive en el acto: el sacerdote, /RESUCITAR, el hechizo y
/// la muerte en la arena llaman a <c>RevivirUsuario</c>, que sólo prende <c>Resucitando</c>; el bucle levanta al
/// jugador con <c>DarVida</c> a los ~2,5 s (Modulo_UsUaRiOs.bas:33-119, mMainLoop.bas:235). Acá sólo la arena
/// esperaba. Y cada camino tiene sus reglas: el sacerdote no atiende al que ya espera, el hechizo mira la facción y
/// no hace nada sobre un vivo, /RESUCITAR deja que a un novato lo levante cualquier criatura común, el #54 no
/// revive a nadie, y el /REVIVIR del GM sí es instantáneo.
/// </summary>
public class ResucitarConEsperaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_resucitar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly NpcSystem _npcs;

    private const short Mapa = 1;
    private const short Resucitar = 11, Resurreccion = 54;
    private const int EsperaEnVueltas = 63;           // 2500 ms del original en vueltas de 40 ms

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ResucitarConEsperaTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _npcs = new NpcSystem(_world, _datos);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player Jugador, ClientSession Sesion) Jugador(string nombre, byte x, byte y, bool muerto = false, short mapa = Mapa)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, y), Heading = 2, Hp = muerto ? 0 : 100, MaxHp = 100, Level = 30, Class = 2,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000,
            FactionStatus = Player.StatusCiudadano, Thirst = 10, Hunger = 10,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        await Task.Delay(250);
    }

    /// <summary>Corre la espera en el bucle: la primera vuelta fija el reloj, y a las 63 lo levanta.</summary>
    private void PasarLaEspera(long desde = 1000)
    {
        _npcs.Update(desde);
        _npcs.Update(desde + EsperaEnVueltas);
    }

    private Npc Criatura(byte x, byte y, bool sacerdote, int tipo = 0)
    {
        var ficha = new NpcInfo(Index: 903, Name: sacerdote ? "Sacerdote" : "Rata", Body: 1, Head: 1, Heading: 3, MaxHp: 100,
            MinHit: 0, MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, NpcType: tipo);
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 100, Pos = new WorldPos(Mapa, x, y), IsReviver = sacerdote };
        _world.AddNpc(npc);
        return npc;
    }

    // ---- el sacerdote ------------------------------------------------------------------------------------------

    [Fact]
    public async Task El_sacerdote_arranca_la_espera_y_el_bucle_lo_levanta_despues()
    {
        var (p, sesion) = Jugador("Muerto", 50, 50, muerto: true);
        Criatura(51, 50, sacerdote: true, tipo: 1);
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 50);
        Assert.False(p.IsAlive);                                               // todavía no: está esperando
        Assert.True(p.Resucitando);
        Assert.Equal(22, p.ParticulaFx);                                       // la partícula de la espera
        _npcs.Update(1000);
        _npcs.Update(1000 + EsperaEnVueltas - 1);
        Assert.False(p.IsAlive);                                               // una vuelta antes, sigue muerto
        _npcs.Update(1000 + EsperaEnVueltas);
        Assert.True(p.IsAlive);
        Assert.False(p.Resucitando);
        Assert.Equal(0, p.ParticulaFx);                                        // y se apaga al volver
    }

    [Fact]
    public async Task Mientras_espera_el_sacerdote_no_lo_vuelve_a_atender()
    {
        var (p, sesion) = Jugador("Muerto", 50, 50, muerto: true);
        Criatura(51, 50, sacerdote: true, tipo: 1);
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 50);
        _npcs.Update(1000);                                                    // el reloj queda fijado
        long reloj = p.ReviveAtTick;
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 50);
        Assert.Equal(reloj, p.ReviveAtTick);                                   // no la volvió a armar
    }

    // ---- /RESUCITAR --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, true)]      // novato: lo levanta hasta una criatura común
    [InlineData(30, false)]     // no novato: sólo el sacerdote
    public async Task Resucitar_con_una_criatura_comun_solo_levanta_al_novato(int nivel, bool levanta)
    {
        var (p, sesion) = Jugador("Muerto", 50, 50, muerto: true);
        p.Level = (byte)nivel;
        var rata = Criatura(51, 50, sacerdote: false, tipo: 0);
        p.TargetNpc = (short)rata.CharIndex;
        await Mandar(sesion, (byte)ClientPacketId.Resucitate);
        Assert.Equal(levanta, p.Resucitando);
    }

    // ---- el hechizo ----------------------------------------------------------------------------------------------

    private async Task<(Player Mago, Player Muerto)> Castear(short hechizo, bool muerto = true, byte faccionDelMuerto = Player.StatusCiudadano)
    {
        var (mago, sesion) = Jugador("Mago", 50, 50);
        mago.CombatMode = true;
        mago.Skills[GameLimits.SkillMagia] = 100;
        mago.Spells[1] = hechizo;
        var (otro, _) = Jugador("Otro", 52, 50, muerto);
        otro.FactionStatus = faccionDelMuerto;
        await Mandar(sesion, (byte)ClientPacketId.CastSpell, 1);
        await Mandar(sesion, (byte)ClientPacketId.WorkLeftClick, 52, 50, (byte)GameLimits.SkillMagia);
        return (mago, otro);
    }

    [Fact]
    public async Task El_hechizo_arranca_la_espera_llena_la_comida_y_cobra()
    {
        var (mago, muerto) = await Castear(Resucitar);
        Assert.False(muerto.IsAlive);                                          // espera, como con el sacerdote
        Assert.True(muerto.Resucitando);
        Assert.Equal(muerto.MaxThirst, muerto.Thirst);                         // la bebida y la comida, llenas
        Assert.Equal(muerto.MaxHunger, muerto.Hunger);
        Assert.True(mago.Mana < 5000);
        PasarLaEspera();
        Assert.True(muerto.IsAlive);
    }

    [Fact]
    public async Task El_hechizo_no_revive_a_quien_no_se_puede_ayudar()
    {
        var (mago, muerto) = await Castear(Resucitar, faccionDelMuerto: Player.StatusCriminal);
        Assert.False(muerto.Resucitando);                                      // el 449
        Assert.Equal(5000, mago.Mana);                                         // sin cobrar
    }

    [Fact]
    public async Task El_hechizo_sobre_un_vivo_no_hace_nada_ni_cobra()
    {
        var (mago, vivo) = await Castear(Resucitar, muerto: false);
        Assert.False(vivo.Resucitando);
        Assert.Equal(5000, mago.Mana);
    }

    /// <summary>El #54 es de terreno y no de área: el original sólo pinta el efecto sobre la baldosa.</summary>
    [Fact]
    public async Task La_resurreccion_de_terreno_no_revive_ni_cobra()
    {
        Assert.Equal(4, _datos.Spell(Resurreccion)!.Target);                   // premisa: es de terreno
        var (mago, muerto) = await Castear(Resurreccion);
        Assert.False(muerto.Resucitando);
        Assert.False(muerto.IsAlive);
        Assert.Equal(5000, mago.Mana);
    }

    // ---- la arena y el GM ----------------------------------------------------------------------------------------

    [Fact]
    public void Morir_en_la_arena_arranca_la_espera_y_apaga_la_particula()
    {
        const short IslaBeleta = 47;
        Assert.True(_datos.IsArena(IslaBeleta, 80, 30));
        var (p, _) = Jugador("Duelista", 80, 30, mapa: IslaBeleta);
        p.ParticulaFx = 23;                                                    // una poción de Nhareth, por ejemplo
        p.Hp = 0;
        PacketDispatcher.KillPlayer(_world, _datos, p, "test");
        Assert.True(p.Resucitando);
        Assert.Equal(0, p.ParticulaFx);
        PasarLaEspera();
        Assert.True(p.IsAlive);
    }

    [Fact]
    public async Task El_gm_revive_en_el_acto()
    {
        var (gm, sesion) = Jugador("Gm", 50, 50);
        gm.Privileges = 8;                                                     // Dios
        var (muerto, _) = Jugador("Muerto", 52, 50, muerto: true);
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(25).WriteAsciiString("Muerto");
        await Mandar(sesion, w.ToArray());
        Assert.True(muerto.IsAlive);
        Assert.False(muerto.Resucitando);
    }
}
