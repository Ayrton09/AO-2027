using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS AVISOS DEL LOGIN, COMO EL ORIGINAL (ChequeosServerIni, Cuentas.bas:336-398, en los dos login): con el server
/// sólo para GMs, con la IP ya adentro y con la creación cerrada, el cartel del cliente original (70, 25 y 3) y la
/// conexión cerrada. Acá eran ConsoleMsg con textos propios, y crear personaje no miraba ni el "sólo GMs" ni la IP.
/// La creación cerrada frena sólo al que crea: en el original frena también al que entra, y el autor decidió que no.
/// </summary>
public class LosAvisosDelLoginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_avisos_login_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Leveling _leveling;
    private readonly Persistence _db;

    private const string SoloGms = "El servidor se encuentra restringido para Game Master. Por favor intente más tarde.";
    private const string IpRepetida = "Su cuenta ha alcanzado el límite de conexiones simultáneas permitidas para este servidor.";
    private const string CreacionCerrada = "La creación de personajes está prohibida temporalmente. Intente más tarde.";

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public volatile bool Cerrado;
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) => Cerrado = true;
    }

    public LosAvisosDelLoginTests()
    {
        Directory.CreateDirectory(_root);
        _leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(new FileStore(_root), _leveling);
        var cuenta = _db.CreateAccount("cuenta", "clave");
        _db.CreateCharacter(cuenta, "Aldo", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private PacketDispatcher Dispatcher(string ini = "", bool soloGms = false) =>
        new(_world, _loop, _db, _datos, _leveling, admin: new AdminConfig { OnlyGms = soloGms },
            serverIni: ServerIni.Desde(IniFile.Parse(ini), "test.ini"));

    private static List<string> Carteles(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.ShowMessageBox)
        .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

    private static byte[] Entrar(string personaje) => new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginExistingChar)
        .WriteAsciiString("cuenta").WriteAsciiString("clave").WriteByte(1).WriteByte(4).WriteByte(5)
        .WriteAsciiString(personaje).WriteAsciiString("mac").WriteLong(1).ToArray();

    private static byte[] Crear(string personaje) => new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginNewChar)
        .WriteAsciiString("cuenta").WriteAsciiString("clave").WriteByte(1).WriteByte(4).WriteByte(5)
        .WriteAsciiString(personaje).WriteByte(1).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(1)
        .WriteAsciiString("mac").WriteLong(1).ToArray();

    private static async Task<Transporte> Mandar(PacketDispatcher d, byte[] paquete)
    {
        var t = new Transporte();
        d.HandleOne(new ClientSession(t, d), new ByteQueueReader(paquete));
        for (int i = 0; i < 40 && !t.Cerrado && !t.Recibidos.Any(x => x[0] == (byte)ServerPacketId.Logged); i++)
            await Task.Delay(100);                                                 // el login verifica la clave afuera del loop
        await Task.Delay(200);
        return t;
    }

    [Fact]
    public async Task Con_el_server_solo_para_GMs_entrar_muestra_el_cartel_y_corta()
    {
        var t = await Mandar(Dispatcher(soloGms: true), Entrar("Aldo"));
        Assert.Equal(new[] { SoloGms }, Carteles(t));
        Assert.True(t.Cerrado);
        Assert.Empty(_world.Players);
    }

    [Fact]
    public async Task Con_el_server_solo_para_GMs_no_se_crea_personaje()
    {
        var t = await Mandar(Dispatcher(soloGms: true), Crear("Bruno"));
        Assert.Equal(new[] { SoloGms }, Carteles(t));
        Assert.True(t.Cerrado);
        Assert.False(_db.CharacterExists("Bruno"));
    }

    [Fact]
    public async Task Con_la_creacion_cerrada_crear_muestra_el_cartel_y_corta()
    {
        var t = await Mandar(Dispatcher("[INIT]\nPuedeCrearPersonajes=0"), Crear("Bruno"));
        Assert.Equal(new[] { CreacionCerrada }, Carteles(t));
        Assert.True(t.Cerrado);
        Assert.False(_db.CharacterExists("Bruno"));
    }

    [Fact]
    public async Task Con_la_creacion_cerrada_se_entra_igual_con_uno_que_ya_existe()
    {
        var t = await Mandar(Dispatcher("[INIT]\nPuedeCrearPersonajes=0"), Entrar("Aldo"));
        Assert.Empty(Carteles(t));
        Assert.Contains(_world.Players, p => p.Name == "Aldo");
    }

    [Fact]
    public async Task Con_una_sola_conexion_por_IP_la_segunda_muestra_el_cartel_y_corta()
    {
        var d = Dispatcher("[INIT]\nAllowMultiLogins=0");
        var otro = new Player { CharIndex = _world.AllocateCharIndex(), Name = "Carla", Account = "otra", Pos = new WorldPos(1, 50, 50), Hp = 1, MaxHp = 1 };
        var sesionDelOtro = new ClientSession(new Transporte(), d);                // la misma "IP"
        sesionDelOtro.Player = otro;
        otro.Connection = sesionDelOtro;
        _world.Add(otro);
        var t = await Mandar(d, Entrar("Aldo"));
        Assert.Equal(new[] { IpRepetida }, Carteles(t));
        Assert.True(t.Cerrado);
        Assert.DoesNotContain(_world.Players, p => p.Name == "Aldo");
    }
}
