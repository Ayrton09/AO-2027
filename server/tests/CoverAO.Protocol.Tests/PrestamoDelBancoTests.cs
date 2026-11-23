using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El préstamo del banco (decisión del autor del 23/11): al que no tiene nada el banquero le presta lo justo para comer, uno a la vez,
/// y lo próximo que deposite paga primero la deuda. Lo encontró el tester: Elmo y Tulio, con 5 monedas y la bóveda vacía, iban y
/// venían de la taberna del Puerto de Rinkel.
/// </summary>
public sealed class PrestamoDelBancoTests : IAsyncDisposable
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

    public PrestamoDelBancoTests()
    {
        var datos = Datos.Value;
        var niveles = new Leveling(datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), niveles);
        _dispatcher = new PacketDispatcher(_mundo, _loop, _db, datos, niveles);
        _corriendo = _loop.RunAsync(_fin.Token);
        _mundo.AddNpc(new Npc
        {
            CharIndex = _mundo.AllocateCharIndex(), Pos = new WorldPos(Avanzada, 43, 71),
            Template = datos.Npc(24)!, IsBanker = true, Hp = 100,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _fin.Cancel();
        try { await _corriendo.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        _db.Dispose();
        _fin.Dispose();
    }

    [Theory]
    [InlineData(300, 23, 5, 0, 0, true)]
    [InlineData(300, 23, 60, 39, 0, true)]     // 99 entre las dos: no tiene nada
    [InlineData(300, 23, 60, 40, 0, false)]    // 100: ya tiene algo
    [InlineData(300, 23, 5, 0, 300, false)]    // debe el anterior
    [InlineData(300, 15, 5, 0, 0, false)]      // novato: con un personaje nuevo se sacaban 300 para el principal
    [InlineData(0, 23, 5, 0, 0, false)]        // apagado en el Server.ini
    public void A_quien_le_presta(int monto, int nivel, long oro, long banco, long deuda, bool presta) =>
        Assert.Equal(presta, PrestamoDelBanco.LePresta(monto, nivel, oro, banco, deuda));

    [Theory]
    [InlineData(500, 300, 300, 200)]
    [InlineData(100, 300, 100, 0)]
    [InlineData(500, 0, 0, 500)]
    public void El_deposito_paga_primero_la_deuda(long deposito, long deuda, long paga, long guarda) =>
        Assert.Equal((paga, guarda), PrestamoDelBanco.Repartir(deposito, deuda));

    [Fact]
    public void Viene_prendido_y_el_ini_lo_apaga()
    {
        Assert.Equal(PrestamoDelBanco.PorDefecto, new ServerIni().PrestamoDelBanco);
        Assert.Equal(0, ServerIni.Desde(IniFile.Parse("[INIT]\nPrestamoDelBanco=0\n")).PrestamoDelBanco);
    }

    [Fact]
    public async Task Presta_una_vez_y_el_deposito_lo_devuelve_y_se_guarda()
    {
        // Tulio: Mago de nivel 22, con 5 monedas, la bóveda vacía y Vestimentas puestas (así no le toca el mandado del sastre).
        var p = new Player
        {
            CharIndex = _mundo.AllocateCharIndex(), Name = "Tulio", Account = "tulio", Pos = new WorldPos(Avanzada, 44, 71),
            Level = 22, Race = 1, Class = 2, Gender = 1, Hp = 155, MaxHp = 155, Stamina = 300, Gold = 5,
            FactionStatus = Player.StatusCriminal,
        };
        var cuenta = _db.CreateAccount(p.Account, "ClaveLocalDePrueba");
        _db.CreateCharacter(cuenta, p.Name, p.CharIndex, p.Race, p.Gender, p.Class, homeland: 1, head: 1);
        _db.EsperarEscriturasPendientes();
        var sesion = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = sesion;

        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 43, 71);
        Assert.Equal(5 + PrestamoDelBanco.PorDefecto, p.Gold);
        Assert.Equal(PrestamoDelBanco.PorDefecto, p.DeudaConElBanco);

        // Debiendo, no le presta otro aunque vuelva a quedar sin nada.
        p.Gold = 5;
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 43, 71);
        Assert.Equal(5, p.Gold);

        // Y lo que deposita paga primero la deuda.
        p.Gold = 500;
        await Mandar(sesion, Deposito(400));
        Assert.Equal(0, p.DeudaConElBanco);
        Assert.Equal(100, p.Bank);
        Assert.Equal(100, p.Gold);

        _db.EsperarEscriturasPendientes();
        Assert.Equal(0, _db.LoadCharacter(p.Name, p.CharIndex, p.Account)!.DeudaConElBanco);
    }

    [Fact]
    public async Task La_deuda_se_guarda_en_la_base()
    {
        var p = new Player
        {
            CharIndex = _mundo.AllocateCharIndex(), Name = "Elmo", Account = "elmo", Pos = new WorldPos(Avanzada, 44, 71),
            Level = 23, Race = 4, Class = 1, Gender = 2, Hp = 157, MaxHp = 157, Stamina = 370, Gold = 5,
            FactionStatus = Player.StatusCriminal,
        };
        var cuenta = _db.CreateAccount(p.Account, "ClaveLocalDePrueba");
        _db.CreateCharacter(cuenta, p.Name, p.CharIndex, p.Race, p.Gender, p.Class, homeland: 1, head: 1);
        _db.EsperarEscriturasPendientes();
        var sesion = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = sesion;
        await Mandar(sesion, (byte)ClientPacketId.DoubleClick, 43, 71);
        _db.EsperarEscriturasPendientes();
        var guardado = _db.LoadCharacter(p.Name, p.CharIndex, p.Account)!;
        Assert.Equal(PrestamoDelBanco.PorDefecto, guardado.DeudaConElBanco);
        Assert.Equal(p.Gold, guardado.Gold);
    }

    private static byte[] Deposito(int monto) =>
        new[] { (byte)ClientPacketId.BankDepositGold }.Concat(BitConverter.GetBytes(monto)).ToArray();

    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        var procesado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => procesado.SetResult());
        await procesado.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
