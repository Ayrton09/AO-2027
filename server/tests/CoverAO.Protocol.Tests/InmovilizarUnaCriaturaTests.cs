using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// INMOVILIZAR A UNA CRIATURA, como el original. No es la parálisis: la paralizada se queda sin IA (mMainLoop.bas:88-91),
/// la inmovilizada no camina pero pega y castea según su rutina (HostilBuenoAI sólo al frente, HostilMalvadoAI a los
/// cuatro lados porque su chequeo está comentado, IrUsuarioCercano sólo en línea recta al frente). Las dos comparten el
/// contador. Acá inmovilizar a una criatura contestaba 298, y paralizarla avisaba con un texto que el original no manda.
/// </summary>
public class InmovilizarUnaCriaturaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_inmovilizar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;                       // Desierto Rinkel: pk
    private const short Paralizar = 9, Inmovilizar = 24, RemoverParalisis = 10;
    private const int Vueltas = 750;                     // 30 segundos de loop
    private const long Siempre = 1_000_000_000;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public InmovilizarUnaCriaturaTests()
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

    // ---- el hechizo ------------------------------------------------------------------------------------------

    private (Player Mago, ClientSession Sesion, Transporte Transporte) Mago(short hechizo)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Mapa, 50, 50), Heading = 2, Hp = 100, MaxHp = 100, Level = 30, Class = 2,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, CombatMode = true,
            FactionStatus = Player.StatusCriminal,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = hechizo;
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, transporte);
    }

    private Npc Criatura(byte x, byte y, bool hostil = true, short dueno = 0)
    {
        var ficha = new NpcInfo(Index: 905, Name: "Bicho de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 10, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: hostil, Attackable: true, Movement: 1);
        var npc = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 500, Pos = new WorldPos(Mapa, x, y),
            Hostile = hostil, OriginallyHostile = hostil, Attackable = true, OwnerCharIndex = dueno,
        };
        _world.AddNpc(npc);
        return npc;
    }

    /// <summary>Castea clickeando la CABEZA del blanco (la baldosa de arriba de sus pies).</summary>
    private async Task CastearSobre(ClientSession sesion, Npc blanco)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, blanco.Pos.X, (byte)(blanco.Pos.Y - 1), (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    private static bool AvisoInventado(Transporte t) =>
        t.Recibidos.Any(p => p.Length > 0 && p[0] == (byte)ServerPacketId.ConsoleMsg
                             && System.Text.Encoding.Latin1.GetString(p).Contains("qued"));

    [Fact]
    public async Task Inmovilizar_a_una_criatura_la_inmoviliza_y_cobra()
    {
        Assert.True(_datos.Spell(Inmovilizar)!.IsImmobilize);                    // premisa
        var (mago, sesion, transporte) = Mago(Inmovilizar);
        var bicho = Criatura(53, 50);
        await CastearSobre(sesion, bicho);
        Assert.True(bicho.EstaInmovilizada(_loop.Tick), "tenía que quedar inmovilizada");
        Assert.False(bicho.EstaParalizada(_loop.Tick), "inmovilizar no es paralizar");
        Assert.True(mago.Mana < 5000, "se cobra");
        Assert.False(AvisoInventado(transporte));
    }

    /// <summary>El control: paralizar la sigue paralizando, y tampoco avisa con texto.</summary>
    [Fact]
    public async Task Paralizar_a_una_criatura_la_paraliza_sin_avisar_con_texto()
    {
        var (mago, sesion, transporte) = Mago(Paralizar);
        var bicho = Criatura(53, 50);
        await CastearSobre(sesion, bicho);
        Assert.True(bicho.EstaParalizada(_loop.Tick));
        Assert.True(mago.Mana < 5000);
        Assert.False(AvisoInventado(transporte), "el original no le avisa al lanzador con texto");
    }

    /// <summary>Remover la parálisis de la mascota propia la libera también si estaba inmovilizada.</summary>
    [Fact]
    public async Task Remover_paralisis_libera_a_la_mascota_inmovilizada()
    {
        var (mago, sesion, _) = Mago(RemoverParalisis);
        var mascota = Criatura(53, 50, hostil: false, dueno: mago.CharIndex);
        mascota.InmovilizadaHastaTick = Siempre;
        await CastearSobre(sesion, mascota);
        Assert.False(mascota.EstaQuieta(_loop.Tick));
        Assert.True(mago.Mana < 5000);
    }

    // ---- el contador compartido y el estado -----------------------------------------------------------------

    private static Npc Suelta() => new()
    {
        CharIndex = 1, Hp = 10,
        Template = new NpcInfo(Index: 1, Name: "x", Body: 1, Head: 1, Heading: 3, MaxHp: 10, MinHit: 1, MaxHit: 1, Def: 0,
            GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
    };

    [Fact]
    public void Las_dos_marcas_comparten_el_contador()
    {
        var npc = Suelta();
        npc.Aquietar(100, paraliza: true, inmoviliza: false, tick: 10);
        npc.Aquietar(300, paraliza: false, inmoviliza: true, tick: 50);         // la paraliza el primero, la reinicia el segundo
        Assert.Equal(300, npc.ParalyzedUntilTick);
        Assert.Equal(300, npc.InmovilizadaHastaTick);
        Assert.True(npc.EstaParalizada(200));                                     // la parálisis manda
        Assert.False(npc.EstaInmovilizada(200));

        var otra = Suelta();
        otra.ParalyzedUntilTick = 40;                                             // ya vencida
        otra.Aquietar(300, paraliza: false, inmoviliza: true, tick: 50);
        Assert.Equal(40, otra.ParalyzedUntilTick);                                // una marca vencida no revive
        Assert.True(otra.EstaInmovilizada(200));
    }

    [Fact]
    public void Clickear_a_la_criatura_muestra_las_dos_marcas()
    {
        var npc = Suelta();
        var quien = new Player { CharIndex = 2, Name = "Rugura", Account = "cuenta_test" };
        const int St1 = 9;                                   // id, Numero(2), Status, PuedeVerVida, vida(4)
        npc.InmovilizadaHastaTick = 100;
        Assert.Equal(2, ServerMessages.CharMsgStatusNpc(npc, quien, 50)[St1]);
        npc.ParalyzedUntilTick = 100;
        Assert.Equal(3, ServerMessages.CharMsgStatusNpc(npc, quien, 50)[St1]);
        Assert.Equal(0, ServerMessages.CharMsgStatusNpc(npc, quien, 100)[St1]);
    }

    // ---- la IA -------------------------------------------------------------------------------------------------

    private static (World Mundo, Player Jugador) Escena(byte x = 10, byte y = 10)
    {
        var world = new World();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        jugador.Pos = new WorldPos(1, x, y);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        world.Add(jugador);
        return (world, jugador);
    }

    private static Npc Bicho(World world, byte x, byte y, bool hostil, int movimiento, byte heading = 1)
    {
        var ficha = new NpcInfo(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: heading, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(1, x, y),
            Movement = movimiento, OriginalMovement = movimiento, Hostile = hostil, OriginallyHostile = hostil,
            Heading = heading,
        };
        world.AddNpc(npc);
        return npc;
    }

    private static void Correr(NpcSystem sys)
    {
        for (int i = 1; i <= Vueltas; i++) sys.Update(i);
    }

    [Fact]
    public void La_inmovilizada_no_camina_y_la_libre_si()
    {
        var (world, _) = Escena();
        var libre = Bicho(world, 30, 30, hostil: false, movimiento: 3);          // MueveAlAzar
        var quieta = Bicho(world, 40, 30, hostil: false, movimiento: 3);
        quieta.InmovilizadaHastaTick = Siempre;
        var desde = (libre.Pos, quieta.Pos);
        // EL CONTROL MIRA SI DIO ALGÚN PASO, no dónde terminó: deambulando al azar vuelve a su baldosa bastante
        // seguido, y la posición final sola daba rojo una de cada seis corridas.
        var sistema = new NpcSystem(world, new GameData());
        bool seMovio = false;
        for (int i = 1; i <= Vueltas; i++) { sistema.Update(i); seMovio |= libre.Pos != desde.Item1; }
        Assert.True(seMovio, "el control: la libre no dio ni un paso");
        Assert.Equal(desde.Item2, quieta.Pos);
    }

    [Fact]
    public void La_hostil_inmovilizada_no_va_al_usuario()
    {
        var (world, jugador) = Escena(10, 10);
        var quieta = Bicho(world, 14, 10, hostil: true, movimiento: 0);          // NpcMaloAtacaUsersBuenos
        quieta.InmovilizadaHastaTick = Siempre;
        var desde = quieta.Pos;
        Correr(new NpcSystem(world, new GameData()));
        Assert.Equal(desde, quieta.Pos);
        Assert.Equal(jugador.MaxHp, jugador.Hp);
    }

    /// <summary>HostilMalvadoAI tiene comentado el chequeo de inmovilizado: pega a los cuatro lados.</summary>
    [Fact]
    public void La_hostil_inmovilizada_pega_a_los_cuatro_lados()
    {
        var (world, jugador) = Escena(10, 10);
        var quieta = Bicho(world, 11, 10, hostil: true, movimiento: 1, heading: 1);   // mira al norte; el jugador, al oeste
        quieta.InmovilizadaHastaTick = Siempre;
        Correr(new NpcSystem(world, new GameData()));
        Assert.True(jugador.Hp < jugador.MaxHp, "la hostil inmovilizada tenía que pegarle al de al lado");
    }

    /// <summary>HostilBuenoAI inmovilizada sólo pega hacia donde mira.</summary>
    [Fact]
    public void La_pacifica_inmovilizada_solo_pega_al_frente()
    {
        var (world, jugador) = Escena(10, 10);
        var costado = Bicho(world, 11, 10, hostil: false, movimiento: 1, heading: 1);   // mira al norte; el jugador, al oeste
        costado.AttackedBy = jugador.Name;
        costado.InmovilizadaHastaTick = Siempre;
        Correr(new NpcSystem(world, new GameData()));
        Assert.Equal(jugador.MaxHp, jugador.Hp);

        var (world2, jugador2) = Escena(10, 10);
        var frente = Bicho(world2, 10, 11, hostil: false, movimiento: 1, heading: 1);   // mira al norte; el jugador, adelante
        frente.AttackedBy = jugador2.Name;
        frente.InmovilizadaHastaTick = Siempre;
        Correr(new NpcSystem(world2, new GameData()));
        Assert.True(jugador2.Hp < jugador2.MaxHp, "al que tiene adelante le tenía que pegar");
    }

    [Fact]
    public void La_paralizada_no_pega()
    {
        var (world, jugador) = Escena(10, 10);
        var quieta = Bicho(world, 11, 10, hostil: true, movimiento: 1);
        quieta.ParalyzedUntilTick = Siempre;
        Correr(new NpcSystem(world, new GameData()));
        Assert.Equal(jugador.MaxHp, jugador.Hp);
    }

    /// <summary>La hostil le pega a la mascota que tiene al lado aunque no haya ningún jugador pegado (HostilMalvadoAI,
    /// `ElseIf NPCI > 0`, AI_NPC.bas:189).</summary>
    [Fact]
    public void La_hostil_ataca_a_la_mascota_sola()
    {
        var (world, amo) = Escena(30, 30);
        var mascota = Bicho(world, 10, 10, hostil: false, movimiento: 1);
        mascota.OwnerCharIndex = amo.CharIndex;
        Bicho(world, 11, 10, hostil: true, movimiento: 1);
        Correr(new NpcSystem(world, new GameData()));
        Assert.True(mascota.Hp < 1000, "la hostil le tenía que pegar a la mascota de al lado");
    }

    /// <summary>La pacífica no tiene esa rama (HostilBuenoAI): aunque haya alguien pegado a ella, no se le tira encima a
    /// la mascota de al lado.</summary>
    [Fact]
    public void La_pacifica_no_ataca_a_la_mascota_de_al_lado()
    {
        var (world, amo) = Escena(12, 10);                                         // pegado al este, y no es su agresor
        var mascota = Bicho(world, 10, 10, hostil: false, movimiento: 1);        // pegada al oeste
        mascota.OwnerCharIndex = amo.CharIndex;
        Bicho(world, 11, 10, hostil: false, movimiento: 1);
        Correr(new NpcSystem(world, new GameData()));
        Assert.Equal(1000, mascota.Hp);
        Assert.Equal(amo.MaxHp, amo.Hp);
    }

    /// <summary>La que va al usuario, inmovilizada, sólo castea en línea recta hacia donde mira.</summary>
    [Fact]
    public void La_maga_inmovilizada_castea_solo_en_linea_recta_al_frente()
    {
        const short Cuna = 37;
        var d = _datos;
        NpcInfo? maga = null;
        for (short ficha = 1; ficha < 1000 && maga is null; ficha++)
            if (d.Npc(ficha) is { Hostile: true, Spells: { Count: > 0 } hechizos } n
                && hechizos.All(id => d.Spell(id) is { IsDamage: true }))
                maga = n;
        Assert.NotNull(maga);                                                      // premisa
        Assert.False(d.MapNoMagic(Cuna));

        foreach (var (px, py, leTira) in new[] { ((byte)50, (byte)54, true), ((byte)52, (byte)54, false) })
        {
            var world = new World();
            var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
            jugador.Pos = new WorldPos(Cuna, px, py);
            jugador.MaxHp = jugador.Hp = 1_000_000;
            world.Add(jugador);
            var sys = new NpcSystem(world, d);
            sys.AddSpawn(maga! with { Movement = 0 }, new WorldPos(Cuna, 50, 50), hostile: true, posOrig: true);
            var npc = world.Npcs.Single();
            npc.Heading = 3;                                                       // mira al sur
            npc.InmovilizadaHastaTick = Siempre;
            for (int i = 1; i <= Vueltas; i++) sys.Update(i);
            Assert.Equal(new WorldPos(Cuna, 50, 50), npc.Pos);
            if (leTira) Assert.True(jugador.Hp < jugador.MaxHp, "al que tiene en línea recta adelante le tenía que tirar");
            else Assert.Equal(jugador.MaxHp, jugador.Hp);
        }
    }
}
