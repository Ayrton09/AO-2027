using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL /TELEP COMO EL ORIGINAL (VB6 HandleWarpChar, Protocol.bas:6850). Lleva a un jugador —o al GM, con <c>YO</c>— a una
/// baldosa: cae ahí o en la más cercana sin nadie (<c>FindLegalPos</c>), al GM le contesta el 487 con SU nombre y al que
/// movió no le dice nada. El Consejero sólo se mueve a sí mismo; sin nombre, con un mapa que no existe o fuera de los
/// bordes no hace nada. Dos cosas del original no se portan, por decisión del port: el reemplazo del nombre por el
/// jugador clickeado y el aviso 77 ("¡Estás muerto!") al desconectado, que va como 75.
/// </summary>
public class ElTelepComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_telep_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const byte GmTelep = 8;
    private const byte Consejero = 2, Dios = 8;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
    }

    public ElTelepComoElOriginalTests()
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

    private (Player, ClientSession, Transporte) Conectado(string nombre, WorldPos pos, byte privilegios = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = pos,
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30, Privileges = privilegios,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Telep(ClientSession gm, string nombre, short mapa, int x, int y)
    {
        _dispatcher.HandleOne(gm, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands)
            .WriteByte(GmTelep).WriteAsciiString(nombre).WriteInteger(mapa).WriteByte((byte)x).WriteByte((byte)y).ToArray()));
        await Task.Delay(300);
    }

    private static WorldPos En(int x, int y, short mapa = 1) => new(mapa, (byte)x, (byte)y);

    [Fact]
    public async Task Mueve_al_nombrado_le_contesta_al_gm_con_su_nombre_y_al_otro_nada()
    {
        var (_, sesionGm, delGm) = Conectado("Rugura", En(30, 30), Dios);
        var (otro, _, delOtro) = Conectado("Merlin", En(50, 50));

        await Telep(sesionGm, "Merlin", 34, 40, 40);

        Assert.Equal(En(40, 40, mapa: 34), otro.Pos);
        Assert.True(delGm.Recibio(ServerMessages.LocaleMsg(487, "Rugura")), "el 487 va con el nombre del GM");
        Assert.Empty(delOtro.De(ServerPacketId.ConsoleMsg));
        Assert.Empty(delOtro.De(ServerPacketId.LocaleMsg));
    }

    [Fact]
    public async Task Con_yo_se_mueve_el_gm()
    {
        var (gm, sesionGm, _) = Conectado("Rugura", En(30, 30), Dios);

        await Telep(sesionGm, "yo", 1, 60, 60);

        Assert.Equal(En(60, 60), gm.Pos);
    }

    [Fact]
    public async Task Con_alguien_en_la_baldosa_cae_en_la_primera_sin_nadie()
    {
        var (_, sesionGm, _) = Conectado("Rugura", En(30, 30), Dios);
        var (otro, _, _) = Conectado("Merlin", En(20, 20));
        Conectado("Marino", En(60, 60));

        await Telep(sesionGm, "Merlin", 1, 60, 60);

        Assert.Equal(En(59, 59), otro.Pos);
    }

    /// <summary>El original no mueve a otro con un nombre escrito aunque el GM tenga a alguien clickeado: ese reemplazo
    /// no se porta.</summary>
    [Fact]
    public async Task El_jugador_clickeado_no_reemplaza_al_nombrado()
    {
        var (gm, sesionGm, _) = Conectado("Rugura", En(30, 30), Dios);
        var (clickeado, _, _) = Conectado("Merlin", En(31, 30));
        var (nombrado, _, _) = Conectado("Marino", En(20, 20));
        gm.TargetUser = clickeado.CharIndex;

        await Telep(sesionGm, "Marino", 1, 60, 60);

        Assert.Equal(En(60, 60), nombrado.Pos);
        Assert.Equal(En(31, 30), clickeado.Pos);
    }

    [Fact]
    public async Task El_consejero_solo_se_mueve_a_si_mismo()
    {
        var (conse, sesion, suyo) = Conectado("Conse", En(30, 30), Consejero);
        var (otro, _, _) = Conectado("Merlin", En(50, 50));

        await Telep(sesion, "Merlin", 1, 60, 60);
        Assert.Equal(En(50, 50), otro.Pos);
        Assert.True(suyo.Recibio(ServerMessages.LocaleMsg(75)));

        await Telep(sesion, "YO", 1, 60, 60);
        Assert.Equal(En(60, 60), conse.Pos);
    }

    [Fact]
    public async Task Al_desconectado_le_contesta_usuario_offline()
    {
        var (_, sesionGm, delGm) = Conectado("Rugura", En(30, 30), Dios);

        await Telep(sesionGm, "Nadie", 1, 60, 60);

        Assert.True(delGm.Recibio(ServerMessages.LocaleMsg(75)));
        Assert.False(delGm.Recibio(ServerMessages.LocaleMsg(77)));
    }

    /// <summary>Sin nombre, con un mapa que no existe o con la baldosa fuera de los bordes jugables no hace nada. El mapa
    /// 0 lo usaba acá como "el mismo mapa", y el original no.</summary>
    [Theory]
    [InlineData("Merlin", 0, 60, 60)]
    [InlineData("Merlin", 9999, 60, 60)]
    [InlineData("Merlin", 1, GameLimits.MinXBorder - 1, 60)]
    [InlineData("Merlin", 1, 60, GameLimits.MaxYBorder + 1)]
    [InlineData("", 1, 60, 60)]
    public async Task Lo_invalido_no_hace_nada(string nombre, short mapa, int x, int y)
    {
        var (gm, sesionGm, delGm) = Conectado("Rugura", En(30, 30), Dios);
        var (otro, _, _) = Conectado("Merlin", En(50, 50));

        await Telep(sesionGm, nombre, mapa, x, y);

        Assert.Equal(En(50, 50), otro.Pos);
        Assert.Equal(En(30, 30), gm.Pos);
        Assert.Empty(delGm.Recibidos);
    }

    [Fact]
    public async Task El_usuario_comun_no_puede()
    {
        var (quien, sesion, suyo) = Conectado("Merlin", En(50, 50));

        await Telep(sesion, "YO", 1, 60, 60);

        Assert.Equal(En(50, 50), quien.Pos);
        Assert.Empty(suyo.Recibidos);
    }
}
