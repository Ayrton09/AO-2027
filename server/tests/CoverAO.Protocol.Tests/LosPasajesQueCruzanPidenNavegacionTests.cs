using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// REGLA DE LA CASA <c>NavegacionParaCruzar</c> (decisión del autor del 2026-09-23): los pasajes que CRUZAN EL AGUA a otra
/// tierra piden Navegación, 80 por defecto; entre dos puertos de la misma tierra, nada. El original no la pide. La
/// reportó el tester.
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

    private const short OracATiama = 1010, OracARinkel = 34;    // los dos salen del puerto de Orac, que es isla
    private const short IlliandorASuramei = 1034;                // de un puerto a otro del mismo continente
    private const short PuertoDeOrac = 181, Tiama = 217, Rinkel = 20, PuertoDeSuramei = 183;
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
        var puerto = (short)_datos.Obj(pasaje)!.PasajeDesdeMap;
        var aca = new WorldPos(puerto, 60, 20);
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Merlin", Account = "merlin", Pos = aca, Heading = 3,
            Hp = 100, MaxHp = 100, Level = 30, Privileges = privilegios,
        };
        p.Skills[GameLimits.SkillNavegacion] = (byte)navegacion;
        p.Inventory[Slot] = new InventorySlot { ObjIndex = pasaje, Amount = 1 };
        var pirata = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 1000, Pos = new WorldPos(puerto, 62, 20), IsTransportador = true,
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
        Assert.Equal(PuertoDeOrac, p.Pos.Map);   // los que no viajan salen todos de Orac
        Assert.True(t.Recibio(ServerMessages.LocaleMsg(477, $"{pide}%*{GameLimits.SkillNavegacion}")),
                    $"no avisó que pide {pide} de Navegación");
        Assert.False(t.Recibio(ServerMessages.LocaleMsg(LocaleExhausto)));
        Assert.Equal(1, p.Inventory[Slot].Amount);
    }

    // ── LA DATA ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Los_pasajes_de_los_casos_salen_de_donde_dice_el_test()
    {
        Assert.Equal(PuertoDeOrac, _datos.Obj(OracATiama)!.PasajeDesdeMap);
        Assert.Equal(PuertoDeOrac, _datos.Obj(OracARinkel)!.PasajeDesdeMap);
        Assert.Equal(PuertoDeSuramei, _datos.Obj(IlliandorASuramei)!.PasajeHastaMap);
    }

    /// <summary>Las tierras salen de la data: se llega caminando dentro de cada una y a las otras sólo por agua.</summary>
    [Fact]
    public void Las_tierras_del_mundo()
    {
        short Ullathorpe = 1, Nix = 34, Banderbill = 59, RinkelCiudad = 20, Arghal = 151;
        short Illiandor = 194, Suramei = 184, TiamaCiudad = 218, Orac = 180, Lindos = 64, NuevaEsperanza = 112;
        foreach (var m in new[] { Nix, Banderbill, RinkelCiudad, Arghal })
            Assert.True(_datos.MismaTierra(Ullathorpe, m), $"el mapa {m} no quedó en el continente de Ullathorpe");
        Assert.True(_datos.MismaTierra(Illiandor, Suramei));
        foreach (var m in new[] { Illiandor, TiamaCiudad, Orac, Lindos, NuevaEsperanza })
            Assert.False(_datos.MismaTierra(Ullathorpe, m), $"el mapa {m} quedó unido al continente de Ullathorpe");
        Assert.False(_datos.MismaTierra(Illiandor, Lindos));
    }

    /// <summary>De los treinta y siete pasajes, no cruzan el agua los ocho que el tester describió: los de Nix, Banderbill,
    /// Arghâl y Rinkel entre sí, y los dos entre Illiandor y Suramei.</summary>
    [Fact]
    public void Los_pasajes_que_no_cruzan_son_los_del_mismo_continente()
    {
        var pasajes = _datos.Objects.Values.Where(o => o.ObjType == 36).ToList();
        var sinCruzar = pasajes.Where(o => _datos.MismaTierra(o.PasajeDesdeMap, o.PasajeHastaMap)).Select(o => o.Index).OrderBy(i => i);

        Assert.Equal(37, pasajes.Count);
        Assert.All(pasajes, o => Assert.True(o.PasajeDesdeMap > 0, $"'{o.Name}' no trae su puerto de salida"));
        Assert.Equal(new short[] { 597, 598, 722, 723, 730, 1009, 1034, 1051 }, sinCruzar);
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
        var (p, sesion, t) = EnElPuerto(new ServerIni(), IlliandorASuramei, navegacion: 0);

        await Usar(sesion);

        Viajo(p, t, PuertoDeSuramei);
    }

    /// <summary>Orac es isla: su pasaje a Rinkel cruza el agua aunque la data no lo marque.</summary>
    [Theory]
    [InlineData(79, false)]
    [InlineData(80, true)]
    public async Task El_que_cruza_el_agua_pide_aunque_no_traiga_la_marca(int navegacion, bool cruza)
    {
        var (p, sesion, t) = EnElPuerto(new ServerIni(), OracARinkel, navegacion);

        await Usar(sesion);

        if (cruza) Viajo(p, t, Rinkel);
        else NoViajo(p, t, pide: 80);
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
