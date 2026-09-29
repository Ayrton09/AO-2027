using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// AL GUARDIA SE LE PEGA SI ÉL TE PUEDE PEGAR A VOS (decisión del autor, 2026-09-27). Los 38 guardias traen
/// <c>Attackable=0</c> —en el original no se los ataca, y tampoco peleaban—, y desde que pelean por la regla de la casa el
/// que recibía los golpes no podía devolverlos. Reporte del tester: Caos, al lado de los imperiales, sin poder pegarles.
/// Ver <see cref="NpcSystem.GuardiaQueVaPor"/>.
/// </summary>
public class AlGuardiaSeLePegaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_al_guardia_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly List<(CancellationTokenSource Cts, Task Corriendo)> _loops = new();

    private const short Mapa = 15;                        // Desierto Rinkel: pk
    private const byte X = 50, Y = 50;
    private const short ProyectilMagico = 2;
    private const short ArcoSimple = 989, Flecha = 480;   // Arco Simple: Proyectil 1, Municiones 1

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public void Dispose()
    {
        foreach (var (cts, corriendo) in _loops)
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    /// <summary>Un mundo con un jugador de esta facción en (50,50) del 15, mirando al este, y el ini que se pida.</summary>
    private (World Mundo, Player Jugador, ClientSession Sesion, PacketDispatcher Dispatcher) Armar(
        byte faccion, byte nivel = 30, string claves = "")
    {
        string carpeta = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var world = new World();
        var loop = new GameLoop();
        var leveling = new Leveling(_datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling,
            serverIni: ServerIni.Desde(IniFile.Parse("[INIT]\r\n" + claves)));
        var cts = new CancellationTokenSource();
        _loops.Add((cts, loop.RunAsync(cts.Token)));
        var p = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Mapa, X, Y), Heading = 2,
            Hp = 1000, MaxHp = 1000, Level = nivel, Class = 2, Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000,
            CombatMode = true,
        };
        p.FactionStatus = faccion;
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Skills[GameLimits.SkillProyectiles] = 100;
        var sesion = new ClientSession(new Transporte(), dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        world.Add(p);
        return (world, p, sesion, dispatcher);
    }

    /// <summary>Un guardia con los números del Guerrero del install, pegado al este del jugador, y con el
    /// <c>Attackable=0</c> de su ficha como se lo pone el spawn: la criatura suelta arranca atacable.</summary>
    private static Npc Guardia(World world, int bando, byte x = X + 1)
    {
        var ficha = new NpcInfo(Index: 6, Name: "Guerrero", Body: 1, Head: 1, Heading: 3, MaxHp: 40_000, MinHit: 250,
                MaxHit: 280, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000)
            with { NpcType = 2, Status = bando, Nivel = 50 };
        Assert.False(ficha.Attackable);
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 40_000, Pos = new WorldPos(Mapa, x, Y) };
        npc.Attackable = ficha.Attackable;
        world.AddNpc(npc);
        return npc;
    }

    private static async Task Pegar(PacketDispatcher dispatcher, ClientSession sesion)
    {
        dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.attack }));
        await Task.Delay(250);
    }

    /// <summary>¿El golpe pasó las compuertas? El agresor queda anotado ANTES de la tirada de acierto (NPCAtacado).</summary>
    private static bool LeLlegoElGolpe(Npc guardia, Player p) => guardia.AttackedBy == p.Name;

    [Fact]
    public async Task El_Caos_le_pega_al_guardia_imperial()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusCaos);
        var guardia = Guardia(world, ServerIni.GuardiaImperial);

        await Pegar(dispatcher, sesion);

        Assert.True(LeLlegoElGolpe(guardia, p), "el Caos no le pudo pegar al guardia imperial que lo ataca");
    }

    /// <summary>Al guardia que no va por vos no se le pega: los de tu bando.</summary>
    [Theory]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusCiudadano)]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusArmada)]
    [InlineData(ServerIni.GuardiaDelCaos, Player.StatusCaos)]
    [InlineData(ServerIni.GuardiaRepublicano, Player.StatusRepublicano)]
    public async Task Al_guardia_de_su_bando_no(int bando, byte faccion)
    {
        var (world, p, sesion, dispatcher) = Armar(faccion);
        var guardia = Guardia(world, bando);

        await Pegar(dispatcher, sesion);

        Assert.False(LeLlegoElGolpe(guardia, p));
        Assert.Equal(40_000, guardia.Hp);
    }

    /// <summary>El control negativo de la regla entera: con los guardias del original, inmunes como allá.</summary>
    [Fact]
    public async Task Con_la_regla_apagada_son_inmunes_como_en_el_original()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusCaos, claves: "GuardiasAtacan=0\r\n");
        var guardia = Guardia(world, ServerIni.GuardiaImperial);

        await Pegar(dispatcher, sesion);

        Assert.False(LeLlegoElGolpe(guardia, p));
    }

    [Fact]
    public async Task El_newbie_no_le_pega_salvo_que_el_ini_lo_pida()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusCaos, nivel: (byte)GameLimits.NewbieMaxLevel);
        var guardia = Guardia(world, ServerIni.GuardiaImperial);
        await Pegar(dispatcher, sesion);
        Assert.False(LeLlegoElGolpe(guardia, p));

        var (world2, p2, sesion2, dispatcher2) = Armar(Player.StatusCaos, nivel: (byte)GameLimits.NewbieMaxLevel,
            claves: "GuardiasAtacanNewbies=1\r\n");
        var guardia2 = Guardia(world2, ServerIni.GuardiaImperial);
        await Pegar(dispatcher2, sesion2);
        Assert.True(LeLlegoElGolpe(guardia2, p2), "con GuardiasAtacanNewbies=1 el newbie tenía que poder pegarle");
    }

    /// <summary>El del Caos ataca a los Armadas, así que un Armada le puede devolver el golpe aunque la regla del original
    /// no le deje pegarle a ninguna criatura sin dueño (el 272).</summary>
    [Fact]
    public async Task El_Armada_le_pega_al_guardia_del_Caos()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusArmada);
        var guardia = Guardia(world, ServerIni.GuardiaDelCaos);

        await Pegar(dispatcher, sesion);

        Assert.True(LeLlegoElGolpe(guardia, p), "el Armada no le pudo pegar al guardia del Caos que lo ataca");
    }

    /// <summary>Y la excepción es sólo para el guardia: a una criatura común sin dueño el Armada sigue sin poder pegarle.</summary>
    [Fact]
    public async Task El_Armada_sigue_sin_pegarle_a_una_criatura_sin_duenio()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusArmada);
        var ficha = new NpcInfo(Index: 900, Name: "Lobo de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 10, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
        var lobo = new Npc { CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(Mapa, X + 1, Y) };
        world.AddNpc(lobo);

        await Pegar(dispatcher, sesion);

        Assert.Null(lobo.AttackedBy);
    }

    [Fact]
    public async Task Con_un_hechizo_tambien()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusCaos);
        p.Spells[1] = ProyectilMagico;
        var guardia = Guardia(world, ServerIni.GuardiaImperial, x: X + 2);

        dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)(X + 2), Y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);

        Assert.True(LeLlegoElGolpe(guardia, p), "el hechizo del Caos no le llegó al guardia imperial");
    }

    [Fact]
    public async Task Con_una_flecha_tambien()
    {
        var (world, p, sesion, dispatcher) = Armar(Player.StatusCaos);
        p.Inventory[1] = new InventorySlot { ObjIndex = ArcoSimple, Amount = 1, Equipped = true };
        p.Inventory[2] = new InventorySlot { ObjIndex = Flecha, Amount = 50, Equipped = true };
        var guardia = Guardia(world, ServerIni.GuardiaImperial, x: X + 2);

        dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)(X + 2), Y, (byte)GameLimits.SkillProyectiles }));
        await Task.Delay(300);

        Assert.True(LeLlegoElGolpe(guardia, p), "la flecha del Caos no le llegó al guardia imperial");
    }
}
