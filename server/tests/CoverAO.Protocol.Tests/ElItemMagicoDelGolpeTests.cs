using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ÍTEM MÁGICO QUE AUMENTA EL GOLPE es el efecto <b>6</b> (<c>eMagicType.AumentaGolpe</c>, Declares.bas:863), y suma su
/// <c>CuantoAumento</c> al golpe contra una criatura, con el arma y con el arco (SistemaCombate.bas:429-433, al final de la
/// misma CalcularDaño). Acá se usaba el 18, que es <c>Experto</c> y el original no lee: el Pendiente del Experto pegaba +20
/// y el Brazalete del Ogro nada. Y el tiro no sumaba ni el ítem ni la montura.
/// </summary>
public class ElItemMagicoDelGolpeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_item_golpe_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly List<(CancellationTokenSource Cts, Task Corriendo)> _loops = new();

    private const short Mapa = 15;
    private const byte X = 50, Y = 50;
    private const short BrazaleteDelOgro = 707, PendienteDelExperto = 1149;
    private const short ArcoSimple = 989, Flecha = 480;   // 5-8 y 4-6

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

    [Fact]
    public void El_Brazalete_del_Ogro_suma_30_contra_una_criatura_y_nada_contra_una_persona()
    {
        var ogro = _datos.Obj(BrazaleteDelOgro)!;
        Assert.Equal(Combat.AumentaGolpe, ogro.EfectoMagico);
        Assert.Equal(30, Combat.BonoDelItemMagico(ogro, contraNpc: true));
        Assert.Equal(0, Combat.BonoDelItemMagico(ogro, contraNpc: false));
    }

    [Fact]
    public void El_Pendiente_del_Experto_no_hace_nada()
    {
        var experto = _datos.Obj(PendienteDelExperto)!;
        Assert.Equal(18, experto.EfectoMagico);   // eMagicType.Experto
        Assert.Equal(0, Combat.BonoDelItemMagico(experto, contraNpc: true));
    }

    /// <summary>Un mundo con un jugador en (50,50) del 15 mirando al este, con todas las skills en 100 y el golpe propio fijo en
    /// 1, y una criatura quieta en <paramref name="xCriatura"/> con la defensa que se pida (sale del jugador: depende de su
    /// fuerza).</summary>
    private (Player Jugador, ClientSession Sesion, PacketDispatcher Dispatcher, Npc Criatura) Armar(
        Func<Player, int> defensa, byte xCriatura, bool brazalete, string claves = "")
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
            Hp = 1000, MaxHp = 1000, Level = 30, Class = 2, MinHit = 1, MaxHit = 1, Stamina = 30_000, MaxStamina = 30_000,
            CombatMode = true,
        };
        p.FactionStatus = Player.StatusCriminal;
        for (int i = 1; i < p.Skills.Length; i++) p.Skills[i] = 100;
        if (brazalete) p.Inventory[5] = new InventorySlot { ObjIndex = BrazaleteDelOgro, Amount = 1, Equipped = true };
        var sesion = new ClientSession(new Transporte(), dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        world.Add(p);
        var ficha = new NpcInfo(Index: 900, Name: "Muñeco", Body: 1, Head: 1, Heading: 3, MaxHp: 10_000, MinHit: 1, MaxHit: 1,
            Def: defensa(p), GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
        var criatura = new Npc { CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 10_000, Pos = new WorldPos(Mapa, xCriatura, Y) };
        world.AddNpc(criatura);
        return (p, sesion, dispatcher, criatura);
    }

    /// <summary>El golpe más fuerte que da a puño limpio sin el brazalete: la tirada de 3 (1 a 3) contra un máximo de 3.</summary>
    private int MaximoAPunio(Player p) =>
        Math.Max(1, (int)Combat.CalcularDano(3, 3, 1, p.Strength, _datos.WrestlingMod.TryGetValue(p.Class, out var m) ? m : 0.4));

    /// <summary>El tiro más fuerte sin el brazalete: arco 8 más flecha 6, contra el máximo del arco.</summary>
    private int MaximoConElArco(Player p) =>
        Math.Max(1, (int)Combat.CalcularDano(8 + 6, 8, 1, p.Strength, _datos.ProjectileDamageMod.TryGetValue(p.Class, out var m) ? m : 1.0));

    /// <summary>Diez golpes a la criatura de al lado: con un acierto del 90% alguno entra seguro.</summary>
    private static async Task PegarDiezVeces(PacketDispatcher dispatcher, ClientSession sesion)
    {
        for (int i = 0; i < 10; i++) dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.attack }));
        await Task.Delay(400);
    }

    private static async Task TirarDiezFlechas(PacketDispatcher dispatcher, ClientSession sesion, byte x)
    {
        for (int i = 0; i < 10; i++)
            dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, Y, (byte)GameLimits.SkillProyectiles }));
        await Task.Delay(400);
    }

    /// <summary>La defensa de la criatura es el golpe más fuerte que se da sin el brazalete: sin él no le hace nada, y con él
    /// le llegan los 30 de más.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_golpe_suma_el_brazalete_contra_una_criatura(bool brazalete)
    {
        var (_, sesion, dispatcher, criatura) = Armar(MaximoAPunio, xCriatura: X + 1, brazalete);

        await PegarDiezVeces(dispatcher, sesion);

        Assert.Equal(brazalete, criatura.Hp < 10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_flecha_tambien_suma_el_brazalete(bool brazalete)
    {
        var (p, sesion, dispatcher, criatura) = Armar(MaximoConElArco, xCriatura: X + 2, brazalete,
            claves: "[INTERVALOS]\r\nIntervaloFlechasCazadores=0\r\n");
        p.Inventory[1] = new InventorySlot { ObjIndex = ArcoSimple, Amount = 1, Equipped = true };
        p.Inventory[2] = new InventorySlot { ObjIndex = Flecha, Amount = 50, Equipped = true };

        await TirarDiezFlechas(dispatcher, sesion, X + 2);

        Assert.True(p.Inventory[2].Amount < 50, "no salió ninguna flecha");
        Assert.Equal(brazalete, criatura.Hp < 10_000);
    }
}
