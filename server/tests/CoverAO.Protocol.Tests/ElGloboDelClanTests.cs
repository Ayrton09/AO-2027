using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL GLOBO DEL CHAT DE CLAN (VB6 HandleGuildMessage, Protocol.bas:5575). Además del renglón "Nombre&gt; texto" a los del
/// clan, el que habla muestra "&lt; texto &gt;" sobre la cabeza, en modo 0, a los que lo ven y son de su clan o Dioses
/// que no son RoleMaster (<c>ToClanArea</c> → <c>SendToUserGuildArea</c>, modSendData.bas:353). El GM con /INVISIBLE
/// habla sólo por el chat. Acá el globo no salía nunca.
/// </summary>
public class ElGloboDelClanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_globo_clan_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const string Clan = "LosDeSiempre";
    private const byte Dios = 8, RoleMaster = 32;

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

    public ElGloboDelClanTests()
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

    private (Player, ClientSession, Transporte) Conectado(string nombre, WorldPos pos, string clan = Clan, byte privilegios = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = pos,
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30, GuildName = clan, Privileges = privilegios,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Hablar(ClientSession sesion, string texto)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.GuildMessage).WriteAsciiString(texto).ToArray()));
        await Task.Delay(300);
    }

    private static WorldPos En(int x, int y) => new(1, (byte)x, (byte)y);
    private static byte[] Globo(Player quien, string texto) => ServerMessages.ChatOverHead($"< {texto} >", quien.CharIndex, 0);

    [Fact]
    public async Task Lo_ven_el_que_habla_y_los_del_clan_que_estan_cerca()
    {
        var (yo, sesion, mio) = Conectado("Merlin", En(50, 50));
        var (_, _, delCompanero) = Conectado("Marino", En(52, 50));

        await Hablar(sesion, "vamos al dungeon");

        Assert.True(mio.Recibio(Globo(yo, "vamos al dungeon")));
        Assert.True(delCompanero.Recibio(Globo(yo, "vamos al dungeon")));
        Assert.True(delCompanero.Recibio(ServerMessages.GuildChat("Merlin> vamos al dungeon")));
    }

    [Fact]
    public async Task El_del_clan_que_esta_lejos_lee_el_chat_pero_no_ve_el_globo()
    {
        var (_, sesion, _) = Conectado("Merlin", En(50, 50));
        var (_, _, delLejano) = Conectado("Marino", En(50, 50 + 40));

        await Hablar(sesion, "hola");

        Assert.True(delLejano.Recibio(ServerMessages.GuildChat("Merlin> hola")));
        Assert.Empty(delLejano.De(ServerPacketId.ChatOverHead));
    }

    /// <summary>De los que no son del clan, lo ve sólo el Dios que no es RoleMaster.</summary>
    [Fact]
    public async Task De_los_ajenos_lo_ve_solo_el_dios()
    {
        var (yo, sesion, _) = Conectado("Merlin", En(50, 50));
        var (_, _, delAjeno) = Conectado("Ajeno", En(51, 50), clan: "");
        var (_, _, delDios) = Conectado("Rugura", En(49, 50), clan: "", privilegios: Dios);
        var (_, _, delRoleMaster) = Conectado("Rolero", En(50, 51), clan: "", privilegios: Dios | RoleMaster);

        await Hablar(sesion, "hola");

        Assert.Empty(delAjeno.De(ServerPacketId.ChatOverHead));
        Assert.True(delDios.Recibio(Globo(yo, "hola")));
        Assert.Empty(delRoleMaster.De(ServerPacketId.ChatOverHead));
    }

    [Fact]
    public async Task El_gm_invisible_habla_solo_por_el_chat()
    {
        var (yo, sesion, mio) = Conectado("Merlin", En(50, 50), privilegios: Dios);
        yo.AdminInvisible = yo.Invisible = true;
        var (_, _, delCompanero) = Conectado("Marino", En(52, 50));

        await Hablar(sesion, "hola");

        Assert.True(delCompanero.Recibio(ServerMessages.GuildChat("Merlin> hola")));
        Assert.Empty(delCompanero.De(ServerPacketId.ChatOverHead));
        Assert.Empty(mio.De(ServerPacketId.ChatOverHead));
    }
}
