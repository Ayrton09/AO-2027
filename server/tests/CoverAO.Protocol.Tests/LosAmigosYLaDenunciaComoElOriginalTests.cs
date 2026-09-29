using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS AMIGOS Y LA DENUNCIA, COMO EL ORIGINAL. El mensaje a los amigos es "[Nombre] mensaje" con eco al que lo
/// manda (HandleMsgAmigo); quitar a un amigo lo confirma y rompe la amistad de los dos lados, también con el otro
/// desconectado (HandleDelAmigo + delAmigoOfli); y la denuncia les llega a los administradores como
/// "nombre DENUNCIA: texto" (HandleDenounce).
/// </summary>
public class LosAmigosYLaDenunciaComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_amigos_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly Persistence _db;

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

    public LosAmigosYLaDenunciaComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private Player Pj(string nombre, byte x, params string[] amigos)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(1, x, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = 1,
        };
        p.Friends.AddRange(amigos);
        return p;
    }

    /// <summary>El alta del personaje en la base, con su cuenta: la base no crea personajes al guardarlos.</summary>
    private void Alta(Player p) => _db.AltaDe(p, new Account { Name = p.Account, PasswordHash = "pbkdf2$1$a$b", Salt = "" });

    private (Player, ClientSession, Transporte) Conectado(string nombre, byte x, params string[] amigos)
    {
        var p = Pj(nombre, x, amigos);
        Alta(p);
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Mandar(ClientSession s, ClientPacketId id, string texto)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)id).WriteAsciiString(texto).ToArray()));
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
    public async Task El_mensaje_a_los_amigos_llega_con_el_nombre_entre_corchetes_y_con_eco()
    {
        var (_, s, t) = Conectado("Aldo", 50, "Bruma");
        var (_, _, amiga) = Conectado("Bruma", 60, "Aldo");

        await Mandar(s, ClientPacketId.MsgAmigos, "¿jugamos?");
        await Asentar(t, amiga);

        Assert.Equal(["[Aldo] ¿jugamos?"], amiga.Textos());
        Assert.Equal(["[Aldo] ¿jugamos?"], t.Textos());
    }

    [Fact]
    public async Task Sin_amigos_conectados_solo_llega_el_eco()
    {
        var (_, s, t) = Conectado("Aldo", 50, "Bruma");

        await Mandar(s, ClientPacketId.MsgAmigos, "hola");
        await Asentar(t);

        Assert.Equal(["[Aldo] hola"], t.Textos());
    }

    [Fact]
    public async Task Quitar_a_un_amigo_conectado_rompe_la_amistad_de_los_dos_lados()
    {
        var (p, s, t) = Conectado("Aldo", 50, "Bruma");
        var (b, _, amiga) = Conectado("Bruma", 60, "Aldo");

        await Mandar(s, ClientPacketId.DelAmigos, "bruma");
        await Asentar(t, amiga);

        Assert.Contains("Bruma fue quitado de tu lista de amigos.", t.Textos());
        Assert.Contains("Aldo te ha quitado de su lista de amigos.", amiga.Textos());
        Assert.Empty(p.Friends);
        Assert.Empty(b.Friends);
    }

    [Fact]
    public async Task Quitar_a_un_amigo_desconectado_le_corrige_el_personaje_guardado()
    {
        var (p, s, t) = Conectado("Aldo", 50, "Bruma", "Ciro");
        Alta(Pj("Bruma", 60, "Ciro", "Aldo"));

        await Mandar(s, ClientPacketId.DelAmigos, "Bruma");
        await Asentar(t);

        Assert.Equal(["Ciro"], p.Friends);
        Assert.Equal(["Ciro"], _db.LoadCharacter("Bruma", 0, "")!.Friends);
    }

    [Fact]
    public async Task Quitar_a_alguien_que_no_esta_en_la_lista_calla()
    {
        var (_, s, t) = Conectado("Aldo", 50);
        Alta(Pj("Bruma", 60, "Aldo"));

        await Mandar(s, ClientPacketId.DelAmigos, "Bruma");
        await Asentar(t);

        Assert.Empty(t.Textos());
        Assert.Equal(["Aldo"], _db.LoadCharacter("Bruma", 0, "")!.Friends);
    }

    [Fact]
    public async Task La_denuncia_les_llega_a_los_administradores_como_en_el_original()
    {
        var (_, s, t) = Conectado("Aldo", 50);
        var (gm, _, staff) = Conectado("Rugura", 60);
        gm.Privileges = 8;

        await Mandar(s, ClientPacketId.Denounce, "hay un tramposo");
        await Asentar(t, staff);

        Assert.Contains("aldo DENUNCIA: hay un tramposo", staff.Textos());
        Assert.DoesNotContain(t.Textos(), m => m.Contains("DENUNCIA"));
        Assert.True(t.Recibio(ServerMessages.LocaleMsg(85)));
    }
}
