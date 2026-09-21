using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE CANCELA LA CUENTA DEL /SALIR. La salida con cuenta existe para que no te vayas del mundo en medio de una
/// pelea: el original la cancela desde cada cosa que demuestra que seguís jugando (<c>CancelExit</c>). Faltaban
/// dos llamados, atacar y pedir un trabajo: arrancar la salida y seguir pegando dejaba irse a los cinco segundos
/// en plena pelea.
/// </summary>
public class SalidaSeCancelaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_salida_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short MapaPk = 15;    // Desierto Rinkel: en un mapa pk la salida tiene cuenta

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public SalidaSeCancelaTests()
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

    private (Player Jugador, ClientSession Sesion) SaliendoEnUnMapaPk()
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(MapaPk, 50, 50), Heading = 2, Hp = 100, MaxHp = 100, Level = 1,
            Stamina = 1000, MaxStamina = 1000, CombatMode = true,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        await Task.Delay(300);
    }

    [Fact]
    public async Task Atacar_cancela_la_salida()
    {
        var (p, sesion) = SaliendoEnUnMapaPk();
        await Mandar(sesion, (byte)ClientPacketId.Quit);
        Assert.True(p.Saliendo, "premisa: en un mapa pk la salida arranca la cuenta");

        await Mandar(sesion, (byte)ClientPacketId.attack);          // al aire: igual cancela (Protocol.bas:1709)
        Assert.False(p.Saliendo);
    }

    /// <summary>CON UN ARMA DE PROYECTIL PUESTA NO SE PEGA (Protocol.bas:1700-1706): el 127 sale antes del
    /// <c>CancelExit</c>, así que tampoco cancela la salida.</summary>
    [Fact]
    public async Task Con_un_arco_puesto_no_se_pega_ni_se_cancela_la_salida()
    {
        const short ArcoSimple = 1355;
        var (p, sesion) = SaliendoEnUnMapaPk();
        p.Inventory[1] = new InventorySlot { ObjIndex = ArcoSimple, Amount = 1, Equipped = true };
        await Mandar(sesion, (byte)ClientPacketId.Quit);
        Assert.True(p.Saliendo, "premisa: en un mapa pk la salida arranca la cuenta");
        int energiaAntes = p.Stamina;

        await Mandar(sesion, (byte)ClientPacketId.attack);
        Assert.True(p.Saliendo);
        Assert.Equal(energiaAntes, p.Stamina);                               // no se cobró el golpe
    }

    /// <summary>PARALIZADO, EL /SALIR NO CANCELA LA CUENTA (Protocol.bas:4975-4980): la parálisis se mira antes que
    /// la salida en curso, contesta el 119 y la cuenta sigue.</summary>
    [Fact]
    public async Task Paralizado_el_salir_no_cancela_la_cuenta()
    {
        var (p, sesion) = SaliendoEnUnMapaPk();
        await Mandar(sesion, (byte)ClientPacketId.Quit);
        Assert.True(p.Saliendo, "premisa: en un mapa pk la salida arranca la cuenta");

        p.ParalyzedUntilTick = long.MaxValue;
        await Mandar(sesion, (byte)ClientPacketId.Quit);
        Assert.True(p.Saliendo);
    }

    [Fact]
    public async Task Pedir_un_trabajo_cancela_la_salida()
    {
        var (p, sesion) = SaliendoEnUnMapaPk();
        await Mandar(sesion, (byte)ClientPacketId.Quit);
        Assert.True(p.Saliendo, "premisa: en un mapa pk la salida arranca la cuenta");

        await Mandar(sesion, (byte)ClientPacketId.Work, (byte)GameLimits.SkillTalar);   // :2314
        Assert.False(p.Saliendo);
    }
}
