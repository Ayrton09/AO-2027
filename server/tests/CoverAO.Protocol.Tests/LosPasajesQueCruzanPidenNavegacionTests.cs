using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// REGLA DE LA CASA <c>NavegacionParaCruzar</c> (decisión del autor del 2026-09-23): los pasajes que CRUZAN EL MAR —los
/// que el <c>obj.dat</c> marca con <c>NecesitaNave=1</c>, a Tiama y a Nueva Esperanza— piden Navegación, 80 por
/// defecto. El original no la pide: la clave está en la data y ningún código la lee. La reportó el tester.
/// </summary>
public class LosPasajesQueCruzanPidenNavegacionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_pasajes_" + Guid.NewGuid().ToString("N"));
    private readonly string _ini = Path.Combine(Path.GetTempPath(), "coverao_pasajes_" + Guid.NewGuid().ToString("N") + ".ini");
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private PacketDispatcher? _dispatcher;

    private const short OracATiama = 1010, OracARinkel = 34;    // los dos salen del puerto de Orac
    private const short PuertoDeOrac = 181, Tiama = 217, Rinkel = 20;
    private const byte Slot = 1;
    private const int LocaleExhausto = 31;                       // "viajaste varios días": el viaje se hizo

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
    }

    public LosPasajesQueCruzanPidenNavegacionTests()
    {
        Directory.CreateDirectory(_root);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        try { File.Delete(_ini); } catch { /* idem */ }
    }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ini);
    }

    /// <summary>El jugador en el puerto de Orac, con el pirata clickeado a dos baldosas y el pasaje en el primer slot.</summary>
    private (Player, ClientSession, Transporte) EnElPuerto(ServerIni ini, short pasaje, int navegacion, byte privilegios = 1)
    {
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(new FileStore(_root), leveling), _datos,
                                           leveling, serverIni: ini);
        var aca = new WorldPos(PuertoDeOrac, 60, 20);
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Merlin", Account = "merlin", Pos = aca, Heading = 3,
            Hp = 100, MaxHp = 100, Level = 30, Privileges = privilegios,
        };
        p.Skills[GameLimits.SkillNavegacion] = (byte)navegacion;
        p.Inventory[Slot] = new InventorySlot { ObjIndex = pasaje, Amount = 1 };
        var pirata = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 1000, Pos = new WorldPos(PuertoDeOrac, 62, 20), IsTransportador = true,
            Template = new NpcInfo(Index: 106, Name: "Pirata", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 0,
                                   MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        };
        _world.AddNpc(pirata);
        p.TargetNpc = pirata.CharIndex;
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Usar(ClientSession sesion)
    {
        _dispatcher!.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.UseItem, Slot }));
        await Task.Delay(300);
    }

    private static void Viajo(Player p, Transporte t, short destino)
    {
        Assert.Equal(destino, p.Pos.Map);
        Assert.True(t.Recibio(ServerMessages.LocaleMsg(LocaleExhausto)), "no llegó el aviso del viaje");
        Assert.True(p.Inventory[Slot].IsEmpty, "el pasaje no se gastó");
    }

    private static void NoViajo(Player p, Transporte t, int pide)
    {
        Assert.Equal(PuertoDeOrac, p.Pos.Map);
        Assert.True(t.Recibio(ServerMessages.LocaleMsg(477, $"{pide}%*{GameLimits.SkillNavegacion}")),
                    $"no avisó que pide {pide} de Navegación");
        Assert.False(t.Recibio(ServerMessages.LocaleMsg(LocaleExhausto)));
        Assert.Equal(1, p.Inventory[Slot].Amount);
    }

    // ── LA DATA ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>La marca llega del <c>obj.dat</c>: quince pasajes, y todos van a Tiama o a Nueva Esperanza.</summary>
    [Fact]
    public void La_data_marca_los_quince_pasajes_que_cruzan()
    {
        var cruzan = _datos.Objects.Values.Where(o => o.NecesitaNave).ToList();

        Assert.Equal(15, cruzan.Count);
        Assert.All(cruzan, o => Assert.Equal(36, o.ObjType));
        Assert.All(cruzan, o => Assert.Contains(_datos.MapName(o.PasajeHastaMap), new[] { "Costas de Tiama", "Puerto Nueva Esperanza" }));
        Assert.True(_datos.Obj(OracATiama)!.NecesitaNave);
        Assert.False(_datos.Obj(OracARinkel)!.NecesitaNave);
        Assert.Equal(PuertoDeOrac, _datos.Obj(OracATiama)!.PasajeDesdeMap);
        Assert.Equal(PuertoDeOrac, _datos.Obj(OracARinkel)!.PasajeDesdeMap);
    }

    // ── EL PASAJE ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sin_la_navegacion_no_cruza_y_se_lo_dice_como_al_barco()
    {
        var (p, sesion, t) = EnElPuerto(new ServerIni(), OracATiama, navegacion: 79);

        await Usar(sesion);

        NoViajo(p, t, pide: 80);
    }

    [Fact]
    public async Task Con_la_navegacion_cruza()
    {
        var (p, sesion, t) = EnElPuerto(new ServerIni(), OracATiama, navegacion: 80);

        await Usar(sesion);

        Viajo(p, t, Tiama);
    }

    [Fact]
    public async Task El_pasaje_que_no_cruza_no_pide_nada()
    {
        var (p, sesion, t) = EnElPuerto(new ServerIni(), OracARinkel, navegacion: 0);

        await Usar(sesion);

        Viajo(p, t, Rinkel);
    }

    /// <summary>Al staff no lo gatea, como al barco: el original compara sólo con el bit de User prendido.</summary>
    [Fact]
    public async Task Al_staff_no_lo_gatea()
    {
        var (p, sesion, t) = EnElPuerto(new ServerIni(), OracATiama, navegacion: 0, privilegios: 2);

        await Usar(sesion);

        Viajo(p, t, Tiama);
    }

    [Fact]
    public async Task En_cero_es_como_el_original()
    {
        var (p, sesion, t) = EnElPuerto(Cargar("NavegacionParaCruzar=0\r\n"), OracATiama, navegacion: 0);

        await Usar(sesion);

        Viajo(p, t, Tiama);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public async Task El_numero_sale_del_ini(int navegacion, bool cruza)
    {
        var (p, sesion, t) = EnElPuerto(Cargar("NavegacionParaCruzar=50\r\n"), OracATiama, navegacion);

        await Usar(sesion);

        if (cruza) Viajo(p, t, Tiama);
        else NoViajo(p, t, pide: 50);
    }

    // ── EL SERVER.INI ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", 80)]
    [InlineData("NavegacionParaCruzar=65\r\n", 65)]
    [InlineData("NavegacionParaCruzar=150\r\n", 100)]
    [InlineData("NavegacionParaCruzar=-5\r\n", 0)]
    public void El_ini_la_lee_entre_cero_y_cien(string cuerpo, int esperado) =>
        Assert.Equal(esperado, Cargar(cuerpo).NavegacionParaCruzar);
}
