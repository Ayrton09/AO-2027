using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL /ACC COMO EL ORIGINAL (HandleCreateNPC, Protocol.bas:10964 → SpawnNpc(npcindex, .Pos, True, False)): la criatura se
/// pide en la baldosa DEL GM, así que cae en la legal más cercana —ClosestLegalPos arranca por la fila de arriba: la
/// diagonal noroeste si está libre— y al GM no le contesta nada. Acá salía enfrente y con textos propios.
/// </summary>
public class ElAccComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_acc_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly NpcSystem _npcs;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15;
    private const byte GmAcc = 60;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public ElAccComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _npcs = new NpcSystem(_world, _datos);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling,
            npcs: _npcs);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player, ClientSession, Transporte) Gm()
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Rugura", Account = "rugura", Pos = new WorldPos(Desierto, 50, 50),
            Heading = 2, Hp = 100, MaxHp = 100, Privileges = 8,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Acc(ClientSession sesion, short criatura)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands)
            .WriteByte(GmAcc).WriteInteger(criatura).ToArray()));
        await Task.Delay(300);
    }

    private short UnaCriatura() => _datos.Npcs.Values.First(n => n.Name.Contains("Lobo") && !n.AguaValida).Index;

    [Fact]
    public async Task La_criatura_cae_al_lado_del_GM_y_no_le_contesta_nada()
    {
        var (gm, sesion, t) = Gm();
        Assert.False(_datos.IsBlockedTile(Desierto, 49, 49));                      // premisa: la diagonal está libre
        short lobo = UnaCriatura();
        await Acc(sesion, lobo);
        var creada = Assert.Single(_world.Npcs, n => n.Template.Index == lobo);
        Assert.Equal(new WorldPos(Desierto, 49, 49), creada.Pos);                  // la primera legal desde arriba
        Assert.DoesNotContain(t.Recibidos, x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg);
    }

    [Fact]
    public async Task Con_un_numero_que_no_existe_calla()
    {
        var (_, sesion, t) = Gm();
        await Acc(sesion, 30000);
        Assert.Empty(_world.Npcs);
        Assert.Empty(t.Recibidos);
    }
}
