using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// FUNDAR UN CLAN, COMO EL ORIGINAL (HandleCreateNewGuild, Protocol.bas:3094-3103, y CrearNuevoClan). Se avisa a TODO
/// el server "X fundó el clan Y de alineación Z." con el sonido 44, el clan arranca con su noticia, y las gemas se
/// van si están: una por ranura y otra más de cada una. Fundar estando en un clan sigue rechazado (decisión del
/// autor, 24/09: en el original la compuerta está anulada).
/// </summary>
public class FundarUnClanComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_fundar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short GemaLunar = 406, GemaNaranja = 408, GemaGris = 409, GemaDorada = 410;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    public FundarUnClanComoElOriginalTests()
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

    private (Player, ClientSession, Transporte) Conectado(string nombre, byte x)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(1, x, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = 1,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Fundar(ClientSession s, string nombre)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.CreateNewGuild)
            .WriteAsciiString("desc").WriteAsciiString(nombre).WriteAsciiString("").WriteAsciiString("").ToArray()));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Asentar(params Transporte[] ts)
    {
        int antes = -1;
        for (int i = 0; i < 50; i++)
        {
            int ahora = ts.Sum(t => t.Recibidos.Count);
            if (ahora == antes) break;
            antes = ahora;
            await Task.Delay(150);
        }
    }

    [Fact]
    public async Task Fundar_avisa_a_todo_el_server_con_el_sonido()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        var (_, _, otro) = Conectado("Bruma", 90);   // lejos: el aviso es para TODOS, no para el área
        p.FoundingAlignment = 2;                       // Imperial

        await Fundar(s, "Los Del Sur");
        await Asentar(t, otro);

        foreach (var quien in new[] { t, otro })
        {
            Assert.Contains("Aldo fundó el clan Los Del Sur de alineación Imperial.", quien.Textos());
            Assert.True(quien.Recibio(ServerMessages.PlayWave(44, 0, 0)));
        }
        Assert.DoesNotContain(t.Textos(), m => m.StartsWith("Fundaste"));
        Assert.Equal("Clan creado con alineación: Imperial", _world.GuildByName("Los Del Sur")!.News);
    }

    [Fact]
    public async Task Fundar_se_lleva_las_gemas_una_por_ranura_y_otra_mas()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        p.Inventory[1] = new InventorySlot { ObjIndex = GemaLunar, Amount = 3 };
        p.Inventory[2] = new InventorySlot { ObjIndex = GemaNaranja, Amount = 1 };
        p.Inventory[3] = new InventorySlot { ObjIndex = GemaDorada, Amount = 2 };

        await Fundar(s, "Los Del Sur");
        await Asentar(t);

        Assert.Equal(1, p.Inventory[1].Amount);     // 3 - 1 (su ranura) - 1 (QuitarObjetos)
        Assert.True(p.Inventory[2].IsEmpty);         // la única se fue en la primera pasada
        Assert.True(p.Inventory[3].IsEmpty);         // 2 - 1 - 1
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlot(2, 0, 0, false, 0f, 0)));
    }

    [Fact]
    public async Task Sin_gemas_tambien_se_funda()
    {
        var (p, s, _) = Conectado("Aldo", 50);

        await Fundar(s, "Los Del Sur");

        Assert.Equal("Los Del Sur", p.GuildName);
    }

    [Fact]
    public async Task Estando_en_un_clan_no_se_funda_otro()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        await Fundar(s, "Los Del Sur");
        await Fundar(s, "Los Del Norte");
        await Asentar(t);

        Assert.Equal("Los Del Sur", p.GuildName);
        Assert.Null(_world.GuildByName("Los Del Norte"));
        Assert.Contains("Ya perteneces a un clan, no puedes fundar otro", t.Textos());
    }
}
