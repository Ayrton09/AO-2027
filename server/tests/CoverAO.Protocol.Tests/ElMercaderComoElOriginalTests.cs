using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL MERCADER COMO EL ORIGINAL. El mostrador son 25 ranuras cargadas de las líneas 1..NROITEMS del NPCs.dat
/// (<c>val(ReadField(...))</c>, MODULO_NPCs.bas:200-210), con un contador <c>NroItems</c> aparte. Al vaciarse una
/// ranura, lo Crucial se repone solo (<c>EncontrarCant</c>), y al llegar el contador a cero se recarga todo salvo con
/// <c>InvReSpawn=1</c> (QuitarNpcInvItem, Modulo_InventANDobj.bas:202-263). Al abrir la tienda van las 25 ranuras y
/// DESPUÉS el CommerceInit; comprar y vender avisan la ranura a todos los que tienen abierta esa tienda y no dicen nada
/// por la consola (Comercio.bas:43-194). La llave comprada queda vendida para siempre (Comercio.bas:106-109).
/// </summary>
public class ElMercaderComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_mercader_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;

    private const short PocionRoja = 38, Manzana = 1, EspadaLarga = 2;
    private const short PirataIlliandor = 141, SastreGeneral = 12, HerreroArmadurasRinkel = 19, GranjeroNorte = 9;
    private const short PropiedadesUllathorpe = 34, LlaveCasa1Ullathorpe = 54;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
        public List<ServerPacketId> Ids() => Recibidos.Where(x => x.Length > 0).Select(x => (ServerPacketId)x[0]).ToList();
    }

    public ElMercaderComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    // ── LA CARGA DEL MOSTRADOR ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "31-100'Vestimentas", "31")]
    [InlineData(2, "31-100'Vestimentas", "100'Vestimentas")]
    [InlineData(1, "30", "30")]
    [InlineData(2, "30", "30")]        // sin separador NO da vacío: vuelve a dar el texto entero
    [InlineData(2, "", "")]
    [InlineData(3, "a-b", "a")]        // y con menos separadores que el campo pedido, vuelve a empezar
    public void ReadField_como_el_original(int campo, string texto, string esperado) =>
        Assert.Equal(esperado, GameData.ReadField(campo, texto, '-'));

    [Fact]
    public void Del_dat_se_venden_solo_las_lineas_hasta_NROITEMS()
    {
        var pirata = _datos.Npc(PirataIlliandor)!;

        Assert.Equal(5, pirata.NroItems);
        Assert.Equal(new[] { (1026, 100), (1029, 100) }, pirata.StockItems!);
        Assert.Equal((1031, 100), pirata.LineasDelDat![5]);    // la 6: el EncontrarCant la ve, el mostrador no
    }

    [Fact]
    public void Una_linea_con_comentario_vale_por_el_numero_de_adelante()
    {
        var sastre = _datos.Npc(SastreGeneral)!;

        Assert.Equal(12, sastre.StockItems!.Count);
        Assert.Equal((31, 100), sastre.StockItems[0]);
    }

    [Fact]
    public void Una_linea_sin_cantidad_vende_tantas_como_su_numero()
    {
        var herrero = _datos.Npc(HerreroArmadurasRinkel)!;

        Assert.Equal((30, 30), herrero.LineasDelDat![8]);       // Obj9=30
        Assert.Equal((369, 100), herrero.LineasDelDat[21]);     // OBj22: la clave con otra grafía también
    }

    [Fact]
    public void Cada_linea_en_su_ranura_y_la_que_falta_cuenta_igual()
    {
        var npc = Mercader(_datos.Npc(GranjeroNorte)!);

        Assert.Equal(GameLimits.MaxInventorySlots, npc.Stock!.Count);
        Assert.Equal(Manzana, npc.Stock[0].ObjIndex);
        Assert.Equal(0, npc.Stock[1].ObjIndex);                  // Obj2 no existe
        Assert.Equal(EspadaLarga, npc.Stock[2].ObjIndex);
        Assert.Equal(4, npc.NroItems);
    }

    [Fact]
    public void Las_propiedades_no_se_recargan_y_el_resto_si()
    {
        Assert.True(_datos.Npc(PropiedadesUllathorpe)!.InvReSpawn);
        Assert.False(_datos.Npc(GranjeroNorte)!.InvReSpawn);
    }

    // ── EL MOSTRADOR AL COMPRAR Y AL VENDER ──────────────────────────────────────────────────────────────────

    private static NpcInfo Ficha(params (int Obj, int Cant)[] lineas)
    {
        var todas = new (int, int)[GameLimits.MaxInventorySlots];
        for (int i = 0; i < lineas.Length; i++) todas[i] = lineas[i];
        return new NpcInfo(900, "mercader de prueba", 1, 1, 3, 100, 0, 0, 0, 0, 0, 0, 0,
            StockItems: lineas.Where(l => l.Obj > 0 && l.Cant > 0).Select(l => (l.Obj, l.Cant)).ToList(),
            NroItems: lineas.Length, LineasDelDat: todas);
    }

    private Npc Mercader(NpcInfo ficha, WorldPos? pos = null)
    {
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Pos = pos ?? new WorldPos(1, 50, 50),
                            InvReSpawn = ficha.InvReSpawn };
        InventarioDelMercader.Cargar(npc, ficha.LineasDelDat!, ficha.NroItems);
        return npc;
    }

    private static void Quitar(Npc npc, byte ranura, int cantidad, bool crucial) =>
        InventarioDelMercader.Quitar(npc, ranura, cantidad, crucial, npc.Template.LineasDelDat!, npc.Template.NroItems);

    [Fact]
    public void Lo_crucial_tambien_se_descuenta()
    {
        var npc = Mercader(Ficha((PocionRoja, 100)));

        Quitar(npc, 1, 2, crucial: true);

        Assert.Equal(98, npc.Stock![0].Amount);
    }

    [Fact]
    public void Lo_crucial_agotado_se_repone_con_la_cantidad_de_su_linea()
    {
        var npc = Mercader(Ficha((Manzana, 5), (PocionRoja, 100)));

        Quitar(npc, 2, 100, crucial: true);

        Assert.Equal(PocionRoja, npc.Stock![1].ObjIndex);
        Assert.Equal(100, npc.Stock[1].Amount);
        Assert.Equal(2, npc.NroItems);
    }

    [Fact]
    public void Lo_crucial_no_se_repone_si_queda_otro_igual_en_el_mostrador()
    {
        var npc = Mercader(Ficha((PocionRoja, 3), (PocionRoja, 100)));

        Quitar(npc, 1, 3, crucial: true);

        Assert.Equal(0, npc.Stock![0].ObjIndex);
        Assert.Equal(1, npc.NroItems);
    }

    [Fact]
    public void Lo_crucial_que_no_esta_en_el_dat_no_se_repone()
    {
        var npc = Mercader(Ficha((Manzana, 5)));
        byte ranura = InventarioDelMercader.Meter(npc, PocionRoja, 10);   // se lo vendió un jugador

        Quitar(npc, ranura, 10, crucial: true);

        Assert.Equal(0, npc.Stock![ranura - 1].ObjIndex);
    }

    [Fact]
    public void Lo_no_crucial_agotado_queda_vacio_y_al_vaciarse_todo_se_recarga()
    {
        var npc = Mercader(Ficha((Manzana, 5), (EspadaLarga, 1)));

        Quitar(npc, 2, 1, crucial: false);
        Assert.Equal(0, npc.Stock![1].ObjIndex);
        Assert.Equal(1, npc.NroItems);

        Quitar(npc, 1, 5, crucial: false);
        Assert.Equal((Manzana, (short)5), (npc.Stock[0].ObjIndex, npc.Stock[0].Amount));
        Assert.Equal((EspadaLarga, (short)1), (npc.Stock[1].ObjIndex, npc.Stock[1].Amount));
        Assert.Equal(2, npc.NroItems);
    }

    [Fact]
    public void Con_InvReSpawn_no_se_recarga()
    {
        var npc = Mercader(Ficha((LlaveCasa1Ullathorpe, 1)) with { InvReSpawn = true });

        Quitar(npc, 1, 1, crucial: false);

        Assert.All(npc.Stock!, st => Assert.Equal(0, st.ObjIndex));
        Assert.Equal(0, npc.NroItems);
    }

    [Fact]
    public void Si_una_linea_falta_el_contador_no_llega_a_cero_y_no_se_recarga()
    {
        var npc = Mercader(_datos.Npc(GranjeroNorte)!);   // NROITEMS=4 con tres líneas

        for (byte r = 1; r <= 4; r++)
            if (npc.Stock![r - 1].ObjIndex != 0) Quitar(npc, r, npc.Stock[r - 1].Amount, crucial: false);

        Assert.All(npc.Stock!, st => Assert.Equal(0, st.ObjIndex));
        Assert.Equal(1, npc.NroItems);
    }

    [Fact]
    public void Lo_vendido_se_apila_si_entra_y_si_no_va_a_la_primera_vacia()
    {
        var npc = Mercader(Ficha((Manzana, 5), (0, 0), (EspadaLarga, 1)));

        Assert.Equal(1, InventarioDelMercader.Meter(npc, Manzana, 10));
        Assert.Equal(15, npc.Stock![0].Amount);
        Assert.Equal(3, npc.NroItems);

        Assert.Equal(2, InventarioDelMercader.Meter(npc, PocionRoja, 7));
        Assert.Equal(4, npc.NroItems);

        Assert.Equal(4, InventarioDelMercader.Meter(npc, Manzana, GameLimits.MaxInventoryObjs));   // no entra en la 1
    }

    [Fact]
    public void Resetinv_lo_vacia_y_apaga_InvReSpawn_asi_que_vender_y_recomprar_lo_recarga()
    {
        var npc = Mercader(Ficha((LlaveCasa1Ullathorpe, 1)) with { InvReSpawn = true });

        InventarioDelMercader.Vaciar(npc);
        Assert.Equal(0, npc.NroItems);
        Assert.False(npc.InvReSpawn);

        byte r = InventarioDelMercader.Meter(npc, Manzana, 1);
        Quitar(npc, r, 1, crucial: false);

        Assert.Equal(LlaveCasa1Ullathorpe, npc.Stock![0].ObjIndex);
    }

    // ── LO QUE VE EL QUE COMERCIA ────────────────────────────────────────────────────────────────────────────

    private short MapaQueAtiende() =>
        (short)Enumerable.Range(1, 400).First(m => _datos.MapExists((short)m) && _datos.MapBattleMode((short)m) == 3);

    private (Player, ClientSession, Transporte) Conectado(string nombre, WorldPos pos)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = pos,
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30, Privileges = 1, Gold = 10_000,
        };
        if (nombre == "Comprador") p.Skills[GameLimits.SkillComerciar] = 100;   // tope: no sube, no avisa nada
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

    private async Task AbrirTienda(Player p, ClientSession s, Npc npc)
    {
        p.TargetNpc = npc.CharIndex;
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.CommerceStart));
    }

    private Task Comprar(ClientSession s, byte ranura, short cantidad) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.CommerceBuy).WriteByte(ranura).WriteInteger(cantidad));

    private Task Vender(ClientSession s, byte ranura, short cantidad) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.CommerceSell).WriteByte(ranura).WriteInteger(cantidad));

    private (Npc, Player, ClientSession, Transporte) TiendaConCliente(NpcInfo ficha)
    {
        short mapa = MapaQueAtiende();
        var npc = Mercader(ficha, new WorldPos(mapa, 50, 50));
        _world.AddNpc(npc);
        var (p, s, t) = Conectado("Comprador", new WorldPos(mapa, 50, 51));
        return (npc, p, s, t);
    }

    [Fact]
    public async Task Al_abrir_van_las_25_ranuras_y_despues_el_CommerceInit()
    {
        var (npc, p, s, t) = TiendaConCliente(Ficha((PocionRoja, 100), (0, 0), (Manzana, 5)));

        await AbrirTienda(p, s, npc);

        var ids = t.Ids();
        Assert.Equal(GameLimits.MaxInventorySlots, ids.Count(i => i == ServerPacketId.ChangeNPCInventorySlot));
        Assert.Equal(ServerPacketId.CommerceInit, ids[^1]);
        Assert.True(ids.IndexOf(ServerPacketId.CommerceInit) > ids.LastIndexOf(ServerPacketId.ChangeNPCInventorySlot));
    }

    [Fact]
    public async Task Comprar_manda_el_inventario_el_oro_y_la_ranura_sin_texto()
    {
        var (npc, p, s, t) = TiendaConCliente(Ficha((PocionRoja, 100)));
        await AbrirTienda(p, s, npc);
        t.Recibidos.Clear();

        await Comprar(s, 1, 2);

        var ids = t.Ids();
        int inv = ids.IndexOf(ServerPacketId.ChangeInventorySlot), oro = ids.IndexOf(ServerPacketId.UpdateGold),
            ranura = ids.IndexOf(ServerPacketId.ChangeNPCInventorySlot);
        Assert.True(inv >= 0 && inv < oro && oro < ranura, string.Join(",", ids));
        Assert.Empty(t.De(ServerPacketId.ConsoleMsg));
        Assert.Equal(98, npc.Stock![0].Amount);
    }

    [Fact]
    public async Task Comprar_lo_ultimo_de_una_ranura_crucial_la_repone()
    {
        // Con un segundo producto: si no, agotar la única ranura deja el contador en cero y lo que la repone es la
        // recarga entera, que no distingue lo Crucial de lo que no.
        var (npc, p, s, t) = TiendaConCliente(Ficha((PocionRoja, 100), (Manzana, 5)));
        await AbrirTienda(p, s, npc);
        t.Recibidos.Clear();

        await Comprar(s, 1, 100);

        Assert.Equal((PocionRoja, (short)100), (npc.Stock![0].ObjIndex, npc.Stock[0].Amount));
        Assert.Equal(ServerMessages.ChangeNpcInventorySlot(1, 100, PrecioDeVitrinaDe(p, PocionRoja), PocionRoja),
                     Assert.Single(t.De(ServerPacketId.ChangeNPCInventorySlot)));
    }

    private float PrecioDeVitrinaDe(Player p, short obj) =>
        Comercio.PrecioDeVitrina(_datos.Obj(obj)!.Value, p.Skills[GameLimits.SkillComerciar]);

    [Fact]
    public async Task Comprar_o_vender_cero_no_hace_nada()
    {
        var (npc, p, s, t) = TiendaConCliente(Ficha((PocionRoja, 100)));
        p.Inventory[1] = new InventorySlot { ObjIndex = Manzana, Amount = 3 };
        await AbrirTienda(p, s, npc);
        t.Recibidos.Clear();

        await Comprar(s, 1, 0);
        await Vender(s, 1, 0);

        Assert.Empty(t.Recibidos);
        Assert.Equal(100, npc.Stock![0].Amount);
        Assert.Equal(3, p.Inventory[1].Amount);
    }

    [Fact]
    public async Task Vender_manda_el_inventario_el_oro_y_la_ranura_sin_texto()
    {
        var (npc, p, s, t) = TiendaConCliente(Ficha((PocionRoja, 100)) with { TipoItems = GameLimits.TipoItemsCualquiera });
        p.Inventory[1] = new InventorySlot { ObjIndex = Manzana, Amount = 3 };
        await AbrirTienda(p, s, npc);
        t.Recibidos.Clear();

        await Vender(s, 1, 2);

        var ids = t.Ids();
        int inv = ids.IndexOf(ServerPacketId.ChangeInventorySlot), oro = ids.IndexOf(ServerPacketId.UpdateGold),
            ranura = ids.IndexOf(ServerPacketId.ChangeNPCInventorySlot);
        Assert.True(inv >= 0 && inv < oro && oro < ranura, string.Join(",", ids));
        Assert.Empty(t.De(ServerPacketId.ConsoleMsg));
        Assert.Equal(Manzana, npc.Stock![1].ObjIndex);
    }

    [Fact]
    public async Task La_ranura_le_llega_al_que_tiene_abierta_esa_tienda_y_no_al_de_otra()
    {
        var (npc, p, s, t) = TiendaConCliente(Ficha((PocionRoja, 100)));
        var otroMercader = Mercader(Ficha((Manzana, 5)), new WorldPos(npc.Pos.Map, 60, 50));
        _world.AddNpc(otroMercader);
        var (mirón, sm, tm) = Conectado("Miron", new WorldPos(npc.Pos.Map, 51, 51));
        var (ajeno, sa, ta) = Conectado("Ajeno", new WorldPos(npc.Pos.Map, 60, 51));
        await AbrirTienda(p, s, npc);
        await AbrirTienda(mirón, sm, npc);
        await AbrirTienda(ajeno, sa, otroMercader);
        tm.Recibidos.Clear(); ta.Recibidos.Clear();

        await Comprar(s, 1, 5);

        var visto = Assert.Single(tm.De(ServerPacketId.ChangeNPCInventorySlot));
        // Con el precio que le toca a ÉL: `Descuento` se calcula con el Comerciar de cada uno (Comercio.bas:273).
        Assert.Equal(ServerMessages.ChangeNpcInventorySlot(1, 95, Comercio.PrecioDeVitrina(_datos.Obj(PocionRoja)!.Value, 0), PocionRoja),
                     visto);
        Assert.Empty(ta.De(ServerPacketId.ChangeNPCInventorySlot));
    }

    [Fact]
    public void El_bot_lee_las_ranuras_que_llegan_antes_del_CommerceInit()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(1, 100, 20f, PocionRoja));
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 0, 20f, 0));
        s.Recibir(ServerMessages.CommerceInit());

        Assert.True(s.Comerciando);
        Assert.Equal((byte)1, s.EnLaTienda(obj => obj == PocionRoja)!.Value.Ranura);
    }

    // ── LAS LLAVES DE LAS PROPIEDADES ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_llave_comprada_queda_vendida_aunque_el_server_arranque_de_nuevo()
    {
        var ficha = _datos.Npc(PropiedadesUllathorpe)!;
        var (npc, p, s, t) = TiendaConCliente(ficha);
        p.Gold = 20_000_000;
        await AbrirTienda(p, s, npc);
        _loop.Enqueue(() => _dispatcher.VolcarPersistenciaDiferida(forzar: true));   // lo que ya estaba, al disco
        await Task.Delay(250);

        await Comprar(s, 1, 1);
        Assert.Contains(p.Inventory, i => i.ObjIndex == LlaveCasa1Ullathorpe);
        Assert.Equal(0, npc.Stock![0].ObjIndex);                 // no es Crucial, y con InvReSpawn no se recarga

        _loop.Enqueue(() => _dispatcher.VolcarPersistenciaDiferida(forzar: true));   // lo que hace el apagado
        await Task.Delay(250);
        var otroMundo = new World();
        foreach (var (f, ranuras) in _db.LoadLlavesVendidas()) otroMundo.LlavesVendidas[f] = ranuras;
        new NpcSystem(otroMundo, _datos).AddSpawn(ficha, npc.Pos);
        var renacido = otroMundo.NpcAt(npc.Pos)!;

        Assert.Equal(LlaveCasa1Ullathorpe, renacido.Stock![0].ObjIndex);   // como el "54-0" del .dat reescrito
        Assert.Equal(0, renacido.Stock[0].Amount);
        Assert.Equal(65, renacido.Stock[1].ObjIndex);                      // las otras siguen a la venta
        Assert.Equal(1, renacido.Stock[1].Amount);
        Assert.True(renacido.InvReSpawn);
    }

    [Fact]
    public async Task El_resetinv_del_gm_vacia_el_mostrador_y_apaga_InvReSpawn()
    {
        var ficha = _datos.Npc(PropiedadesUllathorpe)!;
        var (npc, gm, s, _) = TiendaConCliente(ficha);
        gm.Privileges = 8;   // Dios
        gm.TargetNpc = npc.CharIndex;

        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(GmResetInv));

        Assert.All(npc.Stock!, st => Assert.Equal(0, st.ObjIndex));
        Assert.Equal(0, npc.NroItems);
        Assert.False(npc.InvReSpawn);
    }

    private const byte GmResetInv = 33;
}
