using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BANCO COMO EL ORIGINAL (decisión del autor del 2026-09-23). Son dos ventanas: el doble clic al banquero abre el
/// PANEL DEL ORO (<c>IniciarDeposito(True)</c>: sólo el BankInit con el oro de la bóveda y sus ítems, sin quedar
/// comerciando) y el BankStart abre la BÓVEDA DE ÍTEMS (<c>IniciarDeposito(False)</c>: las 40 ranuras, el oro que
/// lleva y un BankInit en cero). El oro se mueve con el banquero clickeado, sin la bóveda abierta, y el
/// <c>flags.Comerciando</c> es uno solo para la tienda y el banco.
/// </summary>
public class ElBancoComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_banco_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 1;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
        public List<ServerPacketId> Ids() => Recibidos.Where(x => x.Length > 0).Select(x => (ServerPacketId)x[0]).ToList();
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
    }

    public ElBancoComoElOriginalTests()
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

    private static NpcInfo Ficha(string nombre) => new(Index: 904, Name: nombre, Body: 1, Head: 1, Heading: 3, MaxHp: 100,
        MinHit: 0, MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0);

    private Npc Banquero(int x, int y)
    {
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = Ficha("Banquero"), Hp = 100,
                            Pos = new WorldPos(Mapa, (byte)x, (byte)y), IsBanker = true, Movement = 1 };
        _world.AddNpc(npc);
        return npc;
    }

    private Npc Aldeano(int x, int y)
    {
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = Ficha("Aldeano"), Hp = 100,
                            Pos = new WorldPos(Mapa, (byte)x, (byte)y), Movement = 1 };
        _world.AddNpc(npc);
        return npc;
    }

    private (Player, ClientSession, Transporte) Conectado(int x, int y)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Cliente", Account = "cliente", Pos = new WorldPos(Mapa, (byte)x, (byte)y),
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30, Privileges = 1, Gold = 1000, Bank = 500,
        };
        p.BankInventory[3] = new InventorySlot { ObjIndex = 38, Amount = 7 };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Mandar(ClientSession s, ByteQueueWriter w)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(w.ToArray()));
        await Task.Delay(250);
    }

    private Task Clic(ClientSession s, Npc npc) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.LeftClick).WriteByte(npc.Pos.X).WriteByte(npc.Pos.Y));

    private Task DobleClic(ClientSession s, Npc npc) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.DoubleClick).WriteByte(npc.Pos.X).WriteByte(npc.Pos.Y));

    private Task Boveda(ClientSession s) => Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankStart));

    private Task Depositar(ClientSession s, int oro) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankDepositGold).WriteLong(oro));

    [Fact]
    public async Task El_doble_clic_abre_el_panel_del_oro_y_nada_mas()
    {
        var banquero = Banquero(50, 50);
        var (p, s, t) = Conectado(51, 50);
        await Clic(s, banquero);
        t.Recibidos.Clear();

        await DobleClic(s, banquero);

        Assert.Equal(new[] { ServerPacketId.BankInit }, t.Ids());
        Assert.Equal(ServerMessages.BankInit(goliath: true, 500, 1), t.De(ServerPacketId.BankInit)[0]);
        Assert.False(p.Comerciando);
    }

    [Fact]
    public async Task La_boveda_manda_las_40_ranuras_el_oro_y_despues_el_BankInit_en_cero()
    {
        var banquero = Banquero(50, 50);
        var (p, s, t) = Conectado(51, 50);
        await Clic(s, banquero);
        t.Recibidos.Clear();

        await Boveda(s);

        var ids = t.Ids();
        Assert.Equal(GameLimits.MaxBankSlots, ids.Count(i => i == ServerPacketId.ChangeBankSlot));
        Assert.Equal(new[] { ServerPacketId.UpdateGold, ServerPacketId.BankInit }, ids.Skip(GameLimits.MaxBankSlots));
        Assert.Equal(ServerMessages.BankInit(goliath: false), t.De(ServerPacketId.BankInit)[0]);
        Assert.True(p.Comerciando);
    }

    [Fact]
    public async Task Desde_el_panel_del_oro_se_pide_la_boveda()
    {
        var banquero = Banquero(50, 50);
        var (p, s, t) = Conectado(51, 50);
        await Clic(s, banquero);
        await DobleClic(s, banquero);
        t.Recibidos.Clear();

        await Boveda(s);

        Assert.Empty(t.De(ServerPacketId.LocaleMsg));                 // no el 387 de "ya estás comerciando"
        Assert.Equal(GameLimits.MaxBankSlots, t.De(ServerPacketId.ChangeBankSlot).Count);
    }

    [Fact]
    public async Task El_oro_se_mueve_con_el_banquero_clickeado_sin_la_boveda_abierta()
    {
        var banquero = Banquero(50, 50);
        var (p, s, t) = Conectado(51, 50);
        await Clic(s, banquero);

        await Depositar(s, 200);

        Assert.Equal(800, p.Gold);
        Assert.Equal(700, p.Bank);
        Assert.True(t.Recibio(ServerMessages.ChatOverHead("Tenés 700 monedas de oro en tu cuenta.", banquero.CharIndex, 0)));
    }

    [Fact]
    public async Task Sin_nada_clickeado_el_22_lejos_el_8_aunque_no_sea_banquero_y_otro_npc_calla()
    {
        var (p, s, t) = Conectado(51, 50);
        await Depositar(s, 100);
        Assert.True(t.Recibio(ServerMessages.LocaleMsg(22)));

        var lejano = Aldeano(70, 50);
        p.TargetNpc = lejano.CharIndex;
        t.Recibidos.Clear();
        await Depositar(s, 100);
        Assert.True(t.Recibio(ServerMessages.LocaleMsg(8)));   // la distancia va ANTES que el tipo de NPC

        var cercano = Aldeano(52, 50);
        p.TargetNpc = cercano.CharIndex;
        t.Recibidos.Clear();
        await Depositar(s, 100);
        Assert.Empty(t.Recibidos);
        Assert.Equal(1000, p.Gold);
    }

    [Fact]
    public async Task Con_una_ventana_abierta_y_lejos_el_doble_clic_calla()
    {
        var banquero = Banquero(50, 50);
        var (p, s, t) = Conectado(51, 50);
        await Clic(s, banquero);
        await Boveda(s);
        p.Pos = new WorldPos(Mapa, 58, 50);
        t.Recibidos.Clear();

        await DobleClic(s, banquero);

        Assert.Empty(t.Recibidos);   // el original mira Comerciando ANTES que la distancia: no el 8
    }

    [Fact]
    public async Task Un_solo_Comerciando_cerrar_el_banco_cierra_la_tienda_y_al_reves()
    {
        var (p, s, t) = Conectado(51, 50);
        p.TradingWith = 999;
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankEnd));
        Assert.False(p.Comerciando);

        p.BankOpen = true;
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.CommerceEnd));
        Assert.False(p.Comerciando);
    }
}
