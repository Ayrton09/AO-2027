using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL SUBASTADOR, de punta a punta contra el dispatcher (regla de la casa, 06/10; ver <c>PacketDispatcher.Subastas</c>):
/// publicar cobra el depósito y saca de la mochila, ofertar retiene el oro, la compra directa reparte a las bóvedas, lo
/// vencido vuelve, y lo del desconectado le llega al entrar.
/// </summary>
public class ElSubastadorTests : IDisposable
{
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly Persistence _db;
    private readonly Npc _subastador;
    private long _ahora = 1_800_000_000;

    private const short Pocion = 38;
    private const short Mapa = 1;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
    }

    public ElSubastadorTests()
    {
        var leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, _datos, leveling) { RelojDeSubastas = () => _ahora };
        _subastador = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = _datos.Npc(164)!, Pos = new WorldPos(Mapa, 45, 76), IsSubastador = true,
        };
        _world.AddNpc(_subastador);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
    }

    private (Player, ClientSession, Transporte) Conectado(string nombre, byte x = 45, byte y = 77)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = new WorldPos(Mapa, x, y),
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30, Privileges = 1, Gold = 10_000, TargetNpc = _subastador.CharIndex,
        };
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

    private Task Publicar(ClientSession s, byte ranura, short cantidad, int inicial, int final, byte horas) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.SubastaPublicar).WriteByte(ranura).WriteInteger(cantidad)
            .WriteLong(inicial).WriteLong(final).WriteByte(horas));

    private Task Ofertar(ClientSession s, int id, int monto) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.SubastaOfertar).WriteLong(id).WriteLong(monto));

    private Task Comprar(ClientSession s, int id) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.SubastaComprar).WriteLong(id));

    private async Task EnElLoop(Action a)
    {
        var listo = new TaskCompletionSource();
        _loop.Enqueue(() => { a(); listo.SetResult(); });
        await listo.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static short EnLaBoveda(Player p, short objeto) =>
        (short)p.BankInventory.Where(b => !b.IsEmpty && b.ObjIndex == objeto).Sum(b => b.Amount);

    [Fact]
    public async Task Publicar_cobra_el_deposito_saca_de_la_mochila_y_manda_la_lista()
    {
        var (aldo, s, t) = Conectado("Aldo");
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 8 };

        await Publicar(s, 1, 5, 100, 1000, 3);

        var sub = Assert.Single(_world.Subastas.Abiertas);
        Assert.Equal(("Aldo", Pocion, (short)5, 100L, 1000L), (sub.Vendedor, sub.Objeto, sub.Cantidad, sub.PrecioInicial, sub.PrecioFinal));
        Assert.Equal(_ahora + 3 * 3600, sub.Termina);
        Assert.Equal(10_000 - 40, aldo.Gold);                     // 10 x 3 hs + el 1 % de 1000
        Assert.Equal(3, aldo.Inventory[1].Amount);
        Assert.NotEmpty(t.De(ServerPacketId.SubastasLista));
    }

    [Fact]
    public async Task Lejos_del_subastador_no_se_publica()
    {
        var (aldo, s, _) = Conectado("Aldo", 60, 60);
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 8 };

        await Publicar(s, 1, 5, 100, 0, 3);

        Assert.Empty(_world.Subastas.Abiertas);
        Assert.Equal(8, aldo.Inventory[1].Amount);
    }

    [Fact]
    public async Task Lo_de_novato_no_se_subasta()
    {
        var (aldo, s, _) = Conectado("Aldo");
        short novato = Enumerable.Range(1, 3000).Select(i => _datos.Obj((short)i)).First(o => o is { IsNewbie: true })!.Index;
        aldo.Inventory[1] = new InventorySlot { ObjIndex = novato, Amount = 1 };

        await Publicar(s, 1, 1, 100, 0, 3);

        Assert.Empty(_world.Subastas.Abiertas);
        Assert.Equal(1, aldo.Inventory[1].Amount);
    }

    [Fact]
    public async Task La_compra_directa_reparte_a_las_bovedas_y_le_devuelve_al_superado()
    {
        var (aldo, sa, _) = Conectado("Aldo");
        var (bruma, sb, _) = Conectado("Bruma", 46, 77);
        var (ciro, sc, _) = Conectado("Ciro", 44, 77);
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Publicar(sa, 1, 5, 100, 1000, 3);
        int id = _world.Subastas.Abiertas[0].Id;

        await Ofertar(sb, id, 200);
        Assert.Equal(10_000 - 200, bruma.Gold);

        await Comprar(sc, id);

        Assert.Empty(_world.Subastas.Abiertas);
        Assert.Empty(_world.Subastas.Pendientes);
        Assert.Equal(10_000 - 1000, ciro.Gold);
        Assert.Equal(5, EnLaBoveda(ciro, Pocion));
        Assert.Equal(1000, aldo.Bank);
        Assert.Equal(200, bruma.Bank);
    }

    /// <summary>
    /// OFERTAR Y COMPRAR GUARDAN YA al que paga y a los que reciben (auditoría del 22/10): el Centro se volcaba a los 500 ms y el
    /// personaje recién en el autoguardado, y una caída en el medio le devolvía el oro al que ya había pagado.
    /// </summary>
    [Fact]
    public async Task Ofertar_y_comprar_guardan_ya_a_los_personajes()
    {
        foreach (string n in new[] { "Aldo", "Bruma", "Ciro" })
            _db.CreateCharacter(_db.CreateAccount(n.ToLowerInvariant(), "clave_test")!, n, 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        var (aldo, sa, _) = Conectado("Aldo");
        var (bruma, sb, _) = Conectado("Bruma", 46, 77);
        var (ciro, sc, _) = Conectado("Ciro", 44, 77);
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Publicar(sa, 1, 5, 100, 1000, 3);
        int id = _world.Subastas.Abiertas[0].Id;

        await Ofertar(sb, id, 200);
        Assert.Equal(10_000 - 200, _db.LoadCharacter("Bruma", 0, "bruma")!.Gold);

        await Comprar(sc, id);
        var ciroGuardado = _db.LoadCharacter("Ciro", 0, "ciro")!;
        Assert.Equal(10_000 - 1000, ciroGuardado.Gold);
        Assert.Equal(5, EnLaBoveda(ciroGuardado, Pocion));
        Assert.Equal(200, _db.LoadCharacter("Bruma", 0, "bruma")!.Bank);   // al superado, lo devuelto también
        Assert.Equal(1000, _db.LoadCharacter("Aldo", 0, "aldo")!.Bank);
    }

    [Fact]
    public async Task No_se_oferta_en_la_propia()
    {
        var (aldo, s, t) = Conectado("Aldo");
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Publicar(s, 1, 5, 100, 0, 3);

        await Ofertar(s, _world.Subastas.Abiertas[0].Id, 500);

        Assert.Null(_world.Subastas.Abiertas[0].Comprador);
        Assert.Equal(10_000 - 31, aldo.Gold);                     // sólo el depósito: 30 + el 1 % de 100
    }

    [Fact]
    public async Task Vencida_sin_ofertas_vuelve_a_la_boveda_y_al_desconectado_le_llega_al_entrar()
    {
        var (aldo, s, _) = Conectado("Aldo");
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Publicar(s, 1, 5, 100, 0, 3);
        _world.Remove(aldo.CharIndex);   // se desconecta

        _ahora += 3 * 3600;
        await EnElLoop(_dispatcher.SubastasAlDia);

        Assert.Empty(_world.Subastas.Abiertas);
        Assert.Equal(new[] { new EntregaDeSubasta("Aldo", Pocion, 5, 0) }, _world.Subastas.Pendientes);

        await EnElLoop(() => _dispatcher.EntregarLoDeLasSubastas(aldo));   // lo que hace el ingreso
        Assert.Empty(_world.Subastas.Pendientes);
        Assert.Equal(5, EnLaBoveda(aldo, Pocion));
    }

    [Fact]
    public async Task Con_la_boveda_llena_el_objeto_espera()
    {
        var (aldo, s, _) = Conectado("Aldo");
        aldo.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Publicar(s, 1, 5, 100, 0, 3);
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++) aldo.BankInventory[i] = new InventorySlot { ObjIndex = 1, Amount = 1 };

        _ahora += 3 * 3600;
        await EnElLoop(_dispatcher.SubastasAlDia);

        Assert.Single(_world.Subastas.Pendientes);
        Assert.Equal(0, EnLaBoveda(aldo, Pocion));
    }
}
