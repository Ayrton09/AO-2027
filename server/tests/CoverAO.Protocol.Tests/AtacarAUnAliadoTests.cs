using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// ATACAR A UN LEGAL TE VUELVE CRIMINAL — regla de la casa <c>AtacarAliadoVuelveCriminal</c>, prendida por decisión
/// del autor (2026-09-25). Con 0 es como el fuente del original: <c>UsuarioAtacadoPorUsuario</c> no toca el estado de
/// nadie. Se mide con un golpe cuerpo a cuerpo de un ciudadano a un republicano en un mapa con PvP: dos ciudadanos no
/// se pueden pelear (<c>PuedeAtacar</c>, LocaleMsg 97), así que el caso que llega a la regla es entre bandos. El golpe
/// puede errar, pero la marca se decide antes de la tirada.
/// </summary>
public class AtacarAUnAliadoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_aliado_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;

    private const short Desierto = 15;   // mapa con PvP
    private const string Aviso = "Has atacado a un aliado. Ahora eres CRIMINAL.";

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    public AtacarAUnAliadoTests()
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

    private Player Pj(string nombre, byte x, byte faccion)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(Desierto, x, 50), Heading = 2, Hp = 100, MaxHp = 100, Level = 30,
            FactionStatus = faccion, Stamina = 500, MaxStamina = 500,
        };
        _world.Add(p);
        return p;
    }

    /// <summary>El atacante, en (50,50) mirando al este, le pega a la víctima en (51,50).</summary>
    private async Task<(Player Atacante, Transporte Consola)> Golpear(string? ini, byte atacante, byte victima)
    {
        var d = Dispatcher(ini);
        var aldo = Pj("Aldo", 50, atacante);
        Pj("Bruno", 51, victima);
        aldo.CombatMode = true;
        var t = new Transporte();
        var sesion = new ClientSession(t, d) { Player = aldo };
        aldo.Connection = sesion;

        d.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.attack }));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 20 && !t.Recibidos.Any(); i++) await Task.Delay(50);   // la cola de salida es asincrónica
        await Task.Delay(200);
        return (aldo, t);
    }

    [Fact]
    public async Task Por_defecto_un_ciudadano_que_ataca_a_un_republicano_se_vuelve_criminal()
    {
        var (aldo, t) = await Golpear(ini: null, Player.StatusCiudadano, Player.StatusRepublicano);

        Assert.Equal(Player.StatusCriminal, aldo.FactionStatus);
        Assert.Contains(Aviso, t.Textos());
    }

    [Fact]
    public async Task Con_la_clave_en_cero_atacar_no_cambia_el_estado()
    {
        var (aldo, t) = await Golpear("AtacarAliadoVuelveCriminal=0\r\n", Player.StatusCiudadano, Player.StatusRepublicano);

        Assert.Equal(Player.StatusCiudadano, aldo.FactionStatus);
        Assert.DoesNotContain(Aviso, t.Textos());
        Assert.NotEmpty(t.Recibidos);     // el golpe se procesó: no es un verde por no haber hecho nada
    }

    [Fact]
    public async Task Un_renegado_que_ataca_a_un_ciudadano_no_recibe_el_aviso()
    {
        var (aldo, t) = await Golpear(ini: null, Player.StatusCriminal, Player.StatusCiudadano);

        Assert.Equal(Player.StatusCriminal, aldo.FactionStatus);
        Assert.DoesNotContain(Aviso, t.Textos());
        Assert.NotEmpty(t.Recibidos);
    }
}
