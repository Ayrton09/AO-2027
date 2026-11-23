using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class MandadoParaVestirseTests : IAsyncDisposable
{
    private const short Avanzada = 815;
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private readonly World _mundo = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _fin = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public MandadoParaVestirseTests()
    {
        var datos = Datos.Value;
        var niveles = new Leveling(datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), niveles);
        // Sin el préstamo del banco, que también le daría oro al que no tiene nada: acá se mide sólo el mandado.
        _dispatcher = new PacketDispatcher(_mundo, _loop, _db, datos, niveles,
            serverIni: ServerIni.Desde(IniFile.Parse("[INIT]\nPrestamoDelBanco=0\n")));
        _corriendo = _loop.RunAsync(_fin.Token);
        _mundo.AddNpc(new Npc
        {
            CharIndex = _mundo.AllocateCharIndex(), Pos = new WorldPos(Avanzada, 43, 71),
            Template = datos.Npc(24)!, IsBanker = true, Hp = 100,
        });
        _mundo.AddNpc(new Npc
        {
            CharIndex = _mundo.AllocateCharIndex(), Pos = new WorldPos(Avanzada, 51, 30),
            Template = datos.Npc(153)!, Stock = [], Hp = 100,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _fin.Cancel();
        await _corriendo.WaitAsync(TimeSpan.FromSeconds(5));
        _db.Dispose();
        _fin.Dispose();
    }

    private ClientSession Conectar(Player p)
    {
        var sesion = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = sesion;
        return sesion;
    }

    private Player Aldo() => new()
    {
        CharIndex = _mundo.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Avanzada, 44, 71),
        Level = 24, Race = 6, Class = 3, Gender = 1, Hp = 100, MaxHp = 100, Stamina = 0,
        FactionStatus = Player.StatusRepublicano,
    };

    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        var procesado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => procesado.SetResult());
        await procesado.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Paga_en_el_sastre_tras_recibir_el_encargo_y_guarda_la_espera_con_el_oro()
    {
        var p = Aldo();
        var cuenta = _db.CreateAccount(p.Account, "ClaveLocalDePrueba");
        _db.CreateCharacter(cuenta, p.Name, p.CharIndex, p.Race, p.Gender, p.Class, homeland: 1, head: 1);
        _db.EsperarEscriturasPendientes();
        var sesion = Conectar(p);
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 43, 71);
        Assert.Equal(Avanzada, sesion.MandadoPendiente?.Mapa);

        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 30);
        Assert.Equal(0, p.Gold);

        p.Pos = new WorldPos(Avanzada, 50, 30);
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 30);
        Assert.Equal(MandadoParaVestirse.Paga, p.Gold);
        Assert.Null(sesion.MandadoPendiente);
        Assert.True(p.Comerciando);
        await Mandar(sesion, (byte)ClientPacketId.CommerceEnd);
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 30);
        Assert.Equal(MandadoParaVestirse.Paga, p.Gold);

        _db.EsperarEscriturasPendientes();
        var guardado = _db.LoadCharacter(p.Name, p.CharIndex, p.Account)!;
        Assert.Equal(p.UltimoMandado, guardado.UltimoMandado);
        Assert.Equal(p.Gold, guardado.Gold);
        Assert.True(guardado.UltimoMandado > 0);
        guardado.Pos = new WorldPos(Avanzada, 44, 71);
        guardado.Gold = 0;
        var reconectado = Conectar(guardado);
        await Mandar(reconectado, (byte)ClientPacketId.DoubleClick, 43, 71);
        Assert.Null(reconectado.MandadoPendiente);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(60, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public async Task El_sastre_exige_encargo_vigente_y_el_banquero_respeta_ropa_y_ahorros(int banco, bool ropa, bool vencido)
    {
        var p = Aldo();
        p.Bank = banco;
        if (ropa) p.BankInventory[1] = new InventorySlot { ObjIndex = 35, Amount = 1 };
        var sesion = Conectar(p);
        if (banco > 0 || ropa)
        {
            await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 43, 71);
            Assert.Null(sesion.MandadoPendiente);
        }
        if (vencido) sesion.MandadoPendiente = (Avanzada, Environment.TickCount64 - 1);
        p.Pos = new WorldPos(Avanzada, 50, 30);
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 51, 30);
        Assert.Equal(0, p.Gold);
        Assert.True(p.Comerciando);
    }
}
