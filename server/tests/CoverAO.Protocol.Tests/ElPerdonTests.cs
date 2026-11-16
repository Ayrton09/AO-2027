using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL /PERDÓN (pedido del autor, 2026-11-16): el renegado que clickea al sacerdote de una ciudad imperial y escribe /PERDÓN vuelve
/// a ser ciudadano imperial; con el de una republicana, republicano. El primer perdón es gratis y los siguientes cuestan lo mismo
/// que el Convertidor de Rinkel (100.001 de oro), la ciudad del sacerdote pasa a ser su hogar, y con <c>UnPerdonPorNivel</c> (prendida por defecto) no se pide otro en el mismo
/// nivel.
/// </summary>
public class ElPerdonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_perdon_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;

    private const short Nix = 34, Illiandor = 194, Rinkel = 20;
    private const int Oro = 250_000, Precio = 100_000;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool RecibioElLocale(short id) =>
            Recibidos.Any(x => x.Length > 2 && x[0] == (byte)ServerPacketId.LocaleMsg && (x[1] | (x[2] << 8)) == id);
    }

    public ElPerdonTests()
    {
        Directory.CreateDirectory(_root);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private PacketDispatcher Dispatcher(string? ini)
    {
        var leveling = new Leveling(_datos.LevelExp);
        ServerIni? cfg = null;
        if (ini is not null)
        {
            string ruta = Path.Combine(_root, "Server.ini");
            File.WriteAllText(ruta, "[INIT]\r\nStartPort=7666\r\n" + ini);
            cfg = ServerIni.Cargar(ruta);
        }
        return new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling, serverIni: cfg);
    }

    /// <summary>Un jugador al lado del sacerdote de ese mapa, con el sacerdote clickeado, que escribe /PERDÓN.</summary>
    private async Task<(Player Jugador, Transporte Consola)> PedirPerdon(short mapa, byte faccion = Player.StatusCriminal,
        int oro = Oro, int nivelDelUltimoPerdon = 0, string? ini = null, bool sacerdote = true, string clan = "")
    {
        var d = Dispatcher(ini);
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(mapa, 50, 50), Heading = 3,
            Hp = 100, MaxHp = 100, Level = 30, FactionStatus = faccion, Gold = oro, HomeCity = 6,
            NivelDelUltimoPerdon = nivelDelUltimoPerdon, GuildName = clan,
        };
        _world.Add(p);
        var npc = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 1000, Pos = new WorldPos(mapa, 51, 50), IsReviver = sacerdote,
            Template = new NpcInfo(Index: 5, Name: "Sacerdote", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 0,
                                   MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        };
        _world.AddNpc(npc);
        p.TargetNpc = npc.CharIndex;
        var t = new Transporte();
        var sesion = new ClientSession(t, d) { Player = p };
        p.Connection = sesion;

        d.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.Perdon }));
        var atendido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 20 && !t.Recibidos.Any(); i++) await Task.Delay(50);   // la cola de salida es asincrónica
        await Task.Delay(200);
        return (p, t);
    }

    [Fact]
    public async Task Con_el_sacerdote_de_una_ciudad_imperial_vuelve_a_ser_ciudadano_imperial()
    {
        var (p, _) = await PedirPerdon(Nix);

        Assert.Equal(Player.StatusCiudadano, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);   // el primero es gratis
        Assert.Equal(Cities.Nix, p.HomeCity);
        Assert.Equal(30, p.NivelDelUltimoPerdon);
    }

    [Fact]
    public async Task Con_el_sacerdote_de_una_ciudad_republicana_vuelve_a_ser_republicano_y_ese_es_su_hogar()
    {
        var (p, _) = await PedirPerdon(Illiandor);

        Assert.Equal(Player.StatusRepublicano, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);
        Assert.Equal(2, p.HomeCity);   // Illiandor
    }

    [Fact]
    public async Task El_sacerdote_de_una_ciudad_neutral_no_perdona()
    {
        var (p, t) = await PedirPerdon(Rinkel);

        Assert.Equal(Player.StatusCriminal, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);
        Assert.NotEmpty(t.Recibidos);   // contestó: no es un verde por no haber hecho nada
    }

    [Fact]
    public async Task Sin_el_sacerdote_clickeado_no_pasa_nada()
    {
        var (p, t) = await PedirPerdon(Nix, sacerdote: false);

        Assert.Equal(Player.StatusCriminal, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);
        Assert.NotEmpty(t.Recibidos);
    }

    [Fact]
    public async Task Dos_perdones_en_el_mismo_nivel_no_se_puede()
    {
        var (p, t) = await PedirPerdon(Nix, nivelDelUltimoPerdon: 30);

        Assert.Equal(Player.StatusCriminal, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);
        Assert.True(t.RecibioElLocale(325), "no avisó que hay un perdón por nivel");
    }

    [Fact]
    public async Task Despues_de_subir_de_nivel_se_puede_otra_vez()
    {
        var (p, _) = await PedirPerdon(Nix, nivelDelUltimoPerdon: 29);

        Assert.Equal(Player.StatusCiudadano, p.FactionStatus);
        Assert.Equal(Oro - Precio, p.Gold);   // el segundo ya se cobra
    }

    [Fact]
    public async Task El_primer_perdon_no_pide_oro()
    {
        var (p, _) = await PedirPerdon(Nix, oro: 0);

        Assert.Equal(Player.StatusCiudadano, p.FactionStatus);
        Assert.Equal(0, p.Gold);
        Assert.Equal(30, p.NivelDelUltimoPerdon);
    }

    [Fact]
    public async Task Con_la_clave_en_cero_no_hay_limite_por_nivel()
    {
        var (p, _) = await PedirPerdon(Nix, nivelDelUltimoPerdon: 30, ini: "UnPerdonPorNivel=0\r\n");

        Assert.Equal(Player.StatusCiudadano, p.FactionStatus);
    }

    [Fact]
    public async Task Sin_el_oro_no_hay_un_segundo_perdon()
    {
        var (p, t) = await PedirPerdon(Nix, oro: Precio, nivelDelUltimoPerdon: 10);   // con 100.000 justos no alcanza, como en el original

        Assert.Equal(Player.StatusCriminal, p.FactionStatus);
        Assert.Equal(Precio, p.Gold);
        Assert.True(t.RecibioElLocale(383), "no avisó el precio");
    }

    [Theory]
    [InlineData(Player.StatusCaos)]
    [InlineData(Player.StatusRepublicano)]
    public async Task Al_que_no_es_renegado_no_lo_perdona(byte faccion)
    {
        var (p, _) = await PedirPerdon(Nix, faccion);

        Assert.Equal(faccion, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);
    }

    [Fact]
    public async Task Con_clan_no_hay_perdon()
    {
        var (p, _) = await PedirPerdon(Nix, clan: "Los Lobos");

        Assert.Equal(Player.StatusCriminal, p.FactionStatus);
        Assert.Equal(Oro, p.Gold);
    }
}
