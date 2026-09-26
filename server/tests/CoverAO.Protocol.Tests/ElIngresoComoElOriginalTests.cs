using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL INGRESO, COMO EL ORIGINAL. Los carteles del login son los de la tabla de errores del cliente original
/// (<c>locale_error_es.ind</c>): el servidor manda el número (<c>WriteShowMessageBox(UserIndex, 47)</c>, que VB6 convierte
/// en el texto "47") y el cliente lo traduce; acá se manda el texto ya traducido.
/// </summary>
public class ElIngresoComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_ingreso_" + Guid.NewGuid().ToString("N"));
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;

    private const string Clave = "clave_larga_de_prueba";

    private sealed class Transporte(string origen) : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => origen;
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public ElIngresoComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        _db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, datos, leveling);
        // El login verifica la contraseña fuera del loop y vuelve por él: el loop tiene que girar de verdad.
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private static List<string> Carteles(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.ShowMessageBox)
        .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

    private static async Task<bool> Esperar(Func<bool> condicion, int topeMs = 15_000)
    {
        long fin = Environment.TickCount64 + topeMs;
        while (Environment.TickCount64 < fin)
        {
            if (condicion()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    [Fact]
    public async Task Con_otro_personaje_de_la_cuenta_adentro_sale_el_cartel_47()
    {
        var cuenta = _db.CreateAccount("cuenta", Clave);
        _db.CreateCharacter(cuenta, "Aldo", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        _db.CreateCharacter(cuenta, "Bruno", 2, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);

        // Aldo ya está jugando, desde otra dirección (así no lo frena el tope por IP).
        var aldo = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "cuenta",
            Pos = new WorldPos(1, 50, 50), Hp = 100, MaxHp = 100, Level = 1,
        };
        aldo.Connection = new ClientSession(new Transporte("tcp:10.0.0.1:1111"), _dispatcher) { Player = aldo };
        _world.Add(aldo);

        var t = new Transporte("tcp:10.0.0.2:2222");
        _dispatcher.HandleOne(new ClientSession(t, _dispatcher), new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.LoginExistingChar).WriteAsciiString("cuenta").WriteAsciiString(Clave)
            .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString("Bruno").WriteAsciiString("mac").WriteLong(1).ToArray()));

        Assert.True(await Esperar(() => Carteles(t).Count > 0), "no llegó ningún cartel");
        Assert.Equal(["Ya hay un usuario conectado con esta cuenta, solo puedes logear un personaje por cuenta."], Carteles(t));
        Assert.Null(_world.PlayerByName("Bruno"));
    }
}
