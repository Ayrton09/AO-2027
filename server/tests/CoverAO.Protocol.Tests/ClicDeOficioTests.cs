using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL CLIC DE OFICIO NO COBRA POR SU CUENTA. En el original el <c>WorkLeftClick</c> con pesca, tala, minería o
/// botánica no hace nada —el <c>Select</c> de Protocol.bas:2653 no los tiene—: se junta con el doble clic, que
/// arranca una faena que el loop hace de a una vuelta por intervalo. El port pagaba una unidad por clic sin mirar
/// la baldosa ni la distancia, así que un cliente modificado juntaba desde cualquier lado y tan rápido como
/// mandara paquetes.
///
/// <para>Eran tres caminos por los que un clic se salteaba el intervalo de trabajo, y cada uno tiene su test:
/// el clic de oficio que pagaba solo, la faena que al reiniciarse ponía el reloj en cero, y la fragua que
/// fundía una tanda por clic.</para>
/// </summary>
public class ClicDeOficioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_clic_oficio_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;              // Desierto Rinkel: Pk y campo, se puede talar
    private const short Hacha = 127;            // Hacha de Leñador
    private const byte X = 50, Y = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ClicDeOficioTests()
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

    /// <summary>Un leñador con la skill al tope (la tirada no falla nunca: si hay una sola unidad de más, se ve)
    /// y energía de sobra, con el hacha PUESTA.</summary>
    private (Player Jugador, ClientSession Sesion) Lenador()
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Mapa, X, Y), Heading = 2, Hp = 100, MaxHp = 100, Level = 1,
            Stamina = 100_000, MaxStamina = 100_000,
        };
        p.Skills[GameLimits.SkillTalar] = 100;
        p.Skills[GameLimits.SkillMineria] = 100;
        p.Inventory[1] = new InventorySlot { ObjIndex = Hacha, Amount = 1, Equipped = true };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private WorldPos PlantarUnArbol()
    {
        var arbol = _datos.Objects.Values.First(o => o.IsTree);
        var pos = new WorldPos(Mapa, X + 1, Y);
        _world.SetGround(pos, (short)arbol.Index, 1, esFijo: true);
        return pos;
    }

    private async Task Clickear(ClientSession sesion, byte x, byte y, int skill, int veces)
    {
        for (int i = 0; i < veces; i++)
            _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, y, (byte)skill }));
        await Task.Delay(300);   // el loop los atiende en la vuelta siguiente
    }

    private static int Cuantos(Player p, int obj) =>
        Enumerable.Range(1, GameLimits.MaxInventorySlots).Where(i => p.Inventory[i].ObjIndex == obj).Sum(i => p.Inventory[i].Amount);

    [Fact]
    public async Task Lejos_de_un_arbol_el_clic_no_junta_nada()
    {
        var (p, sesion) = Lenador();
        await Clickear(sesion, X + 1, Y, GameLimits.SkillTalar, veces: 10);   // la baldosa está vacía

        Assert.Equal(0, Cuantos(p, GameLimits.WoodObj));
        Assert.False(p.Laborando);
    }

    [Fact]
    public async Task El_clic_sobre_el_arbol_arranca_la_faena_y_no_paga_por_su_cuenta()
    {
        var (p, sesion) = Lenador();
        PlantarUnArbol();
        await Clickear(sesion, X + 1, Y, GameLimits.SkillTalar, veces: 10);

        Assert.True(p.Laborando, "el clic sobre el árbol no arrancó la faena");
        Assert.Equal(0, Cuantos(p, GameLimits.WoodObj));        // los diez clics no pagaron nada
        _dispatcher.TickTrabajo(sesion, p, 1_000_000);
        Assert.True(Cuantos(p, GameLimits.WoodObj) > 0, "la faena arrancada no junta");
    }

    /// <summary>La primera vuelta sale en el acto; reiniciar la faena al toque NO cobra otra: el reloj es del
    /// trabajo, no de la faena (en el original `TimerPuedeTrabajar` sólo se mueve cuando una vuelta pasa).</summary>
    [Fact]
    public async Task Reiniciar_la_faena_no_saltea_el_intervalo()
    {
        var (p, sesion) = Lenador();
        PlantarUnArbol();
        const long T0 = 1_000_000;
        await Clickear(sesion, X + 1, Y, GameLimits.SkillTalar, veces: 1);
        _dispatcher.TickTrabajo(sesion, p, T0);
        int primera = Cuantos(p, GameLimits.WoodObj);
        Assert.True(primera > 0, "premisa: la primera vuelta tiene que juntar");

        await Clickear(sesion, X + 1, Y, GameLimits.SkillTalar, veces: 1);    // reinicia
        _dispatcher.TickTrabajo(sesion, p, T0 + 100);
        Assert.Equal(primera, Cuantos(p, GameLimits.WoodObj));

        _dispatcher.TickTrabajo(sesion, p, T0 + _dispatcher.IntervaloTrabajoMs);
        Assert.True(Cuantos(p, GameLimits.WoodObj) > primera, "cumplido el intervalo, la faena sigue");
    }

    [Fact]
    public async Task Clickear_la_fragua_en_rafaga_no_funde_mas_rapido()
    {
        var (p, sesion) = Lenador();
        var fragua = _datos.Objects.Values.First(o => o.IsForge);
        _world.SetGround(new WorldPos(Mapa, X + 1, Y), (short)fragua.Index, 1, esFijo: true);
        p.Inventory[2] = new InventorySlot { ObjIndex = (short)GameLimits.OreIronObj, Amount = 1000 };

        await Clickear(sesion, X + 1, Y, GameLimits.SkillFundir, veces: 1);
        int tanda = Cuantos(p, GameLimits.IngotIronObj);
        Assert.True(tanda > 0, "premisa: el primer clic en la fragua tiene que fundir");

        await Clickear(sesion, X + 1, Y, GameLimits.SkillFundir, veces: 5);
        Assert.Equal(tanda, Cuantos(p, GameLimits.IngotIronObj));
        Assert.True(p.Laborando, "la faena de fundir sigue abierta: la próxima tanda la hace el loop");
    }
}
