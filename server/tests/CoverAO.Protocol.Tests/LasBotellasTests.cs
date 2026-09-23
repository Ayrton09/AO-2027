using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS BOTELLAS (VB6 InvUsuario.bas:1770-1817). La vacía (534, la vende el Vendedor General) se llena en el agua de la
/// última baldosa clickeada y pasa a ser la llena (533); la llena se toma, sube la sed y vuelve a ser la vacía, con el
/// trago. Acá no se podían usar: caían en el "No puedes usar ese objeto".
/// </summary>
public class LasBotellasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_botellas_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Llena = 533, Vacia = 534, SndBeber = 135;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public LasBotellasTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    /// <summary>Una baldosa de tierra con agua al este.</summary>
    private (short Mapa, byte X, byte Y) Orilla()
    {
        foreach (short mapa in new short[] { 17, 1, 15, 23, 34, 47 })
            for (byte y = 10; y < 90; y++)
                for (byte x = 10; x < 90; x++)
                    if (!_datos.IsWater(mapa, x, y) && !_datos.IsBlockedTile(mapa, x, y) && _datos.IsWater(mapa, x + 1, y))
                        return (mapa, x, y);
        throw new InvalidOperationException("no hay orilla en los mapas mirados");
    }

    private (Player, ClientSession, Transporte) Conectado(string nombre, short mapa, byte x, byte y)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, y), Heading = 2, Hp = 100, MaxHp = 100, Level = 1, Thirst = 0,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Usar(ClientSession sesion, byte slot)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.UseItem, slot }));
        await Task.Delay(300);
    }

    private static bool EsTrago(byte[] x) =>
        x.Length >= 3 && x[0] == (byte)ServerPacketId.PlayWave && BitConverter.ToInt16(x, 1) == SndBeber;

    [Fact]
    public void La_data_las_encadena()
    {
        Assert.Equal(33, _datos.Obj(Vacia)!.ObjType);
        Assert.Equal(34, _datos.Obj(Llena)!.ObjType);
        Assert.Equal(Llena, _datos.Obj(Vacia)!.IndexAbierta);
        Assert.Equal(Vacia, _datos.Obj(Llena)!.IndexCerrada);
        Assert.True(_datos.Obj(Llena)!.MinSed > 0);
    }

    [Fact]
    public async Task La_vacia_se_llena_con_el_agua_clickeada()
    {
        var (mapa, x, y) = Orilla();
        var (p, sesion, t) = Conectado("Aldo", mapa, x, y);
        p.Inventory[1] = new InventorySlot { ObjIndex = Vacia, Amount = 1 };
        p.TargetX = (byte)(x + 1); p.TargetY = y;                                  // el clic en el agua
        await Usar(sesion, 1);
        Assert.Equal(Llena, p.Inventory[1].ObjIndex);
        Assert.Equal(1, p.Inventory[1].Amount);
        Assert.DoesNotContain(t.Recibidos, EsTrago);
    }

    [Fact]
    public async Task Sin_agua_en_el_clic_avisa_el_392_y_no_cambia_nada()
    {
        var (mapa, x, y) = Orilla();
        var (p, sesion, t) = Conectado("Aldo", mapa, x, y);
        p.Inventory[1] = new InventorySlot { ObjIndex = Vacia, Amount = 1 };
        p.TargetX = x; p.TargetY = y;                                              // la tierra donde está parado
        await Usar(sesion, 1);
        Assert.Equal(Vacia, p.Inventory[1].ObjIndex);
        Assert.Contains(t.Recibidos, r => r.Length >= 3 && r[0] == (byte)ServerPacketId.LocaleMsg && BitConverter.ToInt16(r, 1) == 392);
    }

    [Fact]
    public async Task La_llena_se_toma_y_vuelve_vacia_con_el_trago()
    {
        var (p, sesion, t) = Conectado("Aldo", 15, 50, 50);
        var (_, _, delOtro) = Conectado("Bruno", 15, 52, 50);
        p.Inventory[1] = new InventorySlot { ObjIndex = Llena, Amount = 2 };
        await Usar(sesion, 1);
        Assert.Equal(_datos.Obj(Llena)!.MinSed, p.Thirst);
        Assert.Equal(1, p.Inventory[1].Amount);                                    // queda una llena
        Assert.Contains(Enumerable.Range(1, GameLimits.MaxInventorySlots),
            i => p.Inventory[i].ObjIndex == Vacia && p.Inventory[i].Amount == 1);  // y aparece la vacía
        Assert.Contains(t.Recibidos, EsTrago);
        Assert.Contains(delOtro.Recibidos, EsTrago);
    }

    [Fact]
    public async Task Con_la_mochila_llena_la_vacia_cae_al_piso()
    {
        var (p, sesion, _) = Conectado("Aldo", 15, 50, 50);
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            p.Inventory[i] = new InventorySlot { ObjIndex = 38, Amount = 1 };        // pociones rojas por todos lados
        p.Inventory[1] = new InventorySlot { ObjIndex = Llena, Amount = 2 };
        await Usar(sesion, 1);
        Assert.Equal(1, p.Inventory[1].Amount);
        bool enElPiso = false;
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
                if (_world.TryGetGround(new WorldPos(15, (byte)(50 + dx), (byte)(50 + dy)), out var g) && g.ObjIndex == Vacia) enElPiso = true;
        Assert.True(enElPiso, "la vacía no entraba en la mochila: tenía que caer al piso");
    }
}
