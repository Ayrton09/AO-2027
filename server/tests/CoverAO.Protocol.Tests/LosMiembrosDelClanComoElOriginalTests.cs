using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// ACEPTAR, RECHAZAR, EXPULSAR Y SALIR DE UN CLAN, COMO EL ORIGINAL (Protocol.bas:4478/4547/4617/4995/11145 y
/// modGuilds.bas). Los avisos van a los del clan (ToGuildMembers) con su sonido; aceptar mira la alineación; rechaza
/// cualquier miembro; lo que no se puede echar calla; el motivo de un rechazo a un desconectado queda guardado.
/// </summary>
public class LosMiembrosDelClanComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_miembros_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private byte _x = 40;

    private const string Clan = "Los Del Sur";
    private const byte Imperial = 2;

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

    public LosMiembrosDelClanComoElOriginalTests()
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

    private (Player P, ClientSession S, Transporte T) Conectado(string nombre, string clan = "", byte status = Player.StatusCiudadano, byte priv = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(1, _x++, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = 20,
            GuildName = clan, FactionStatus = status, Privileges = priv,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private Guild ClanDe(string lider, params string[] miembros)
    {
        var g = _world.CreateGuild(Clan, lider, "desc")!;
        g.Alignment = Imperial;
        foreach (var m in miembros) g.Members.Add(m);
        return g;
    }

    private async Task Mandar(ClientSession s, ByteQueueWriter w)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(w.ToArray()));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ByteQueueWriter Paquete(ClientPacketId id) => new ByteQueueWriter().WriteByte((byte)id);

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
    public async Task Aceptar_avisa_al_clan_con_el_sonido_43()
    {
        var g = ClanDe("Aldo", "Bruma");
        var (_, sa, ta) = Conectado("Aldo", Clan);
        var (_, _, tb) = Conectado("Bruma", Clan);
        var (c, _, tc) = Conectado("Ciro");
        g.JoinRequests["Ciro"] = "quiero entrar";

        await Mandar(sa, Paquete(ClientPacketId.GuildAcceptNewMember).WriteAsciiString("Ciro"));
        await Asentar(ta, tb, tc);

        Assert.Equal(Clan, c.GuildName);
        foreach (var t in new[] { ta, tb, tc })
        {
            Assert.Contains("Ciro ha sido aceptado como miembro del clan.", t.Textos());
            Assert.True(t.Recibio(ServerMessages.PlayWave(43, 0, 0)));
        }
        Assert.DoesNotContain(ta.Textos(), m => m.StartsWith("Aceptaste"));
    }

    [Fact]
    public async Task Aceptar_mira_la_alineacion_y_saca_de_la_cola()
    {
        var g = ClanDe("Aldo");
        var (_, sa, ta) = Conectado("Aldo", Clan);
        var (c, _, _) = Conectado("Ciro", status: Player.StatusCaos);   // un caótico a un clan imperial
        g.JoinRequests["Ciro"] = "";

        await Mandar(sa, Paquete(ClientPacketId.GuildAcceptNewMember).WriteAsciiString("Ciro"));
        await Asentar(ta);

        Assert.Contains("Ciro no puede entrar a un clan de alineación Imperial", ta.Textos());
        Assert.Equal("", c.GuildName);
        Assert.Empty(g.JoinRequests);
    }

    [Fact]
    public async Task Aceptar_sin_ser_lider_y_sin_aspirante_con_los_textos_del_original()
    {
        var g = ClanDe("Aldo", "Bruma");
        var (_, sa, ta) = Conectado("Aldo", Clan);
        var (_, sb, tb) = Conectado("Bruma", Clan);

        await Mandar(sb, Paquete(ClientPacketId.GuildAcceptNewMember).WriteAsciiString("Ciro"));
        await Mandar(sa, Paquete(ClientPacketId.GuildAcceptNewMember).WriteAsciiString("Ciro"));
        await Asentar(ta, tb);

        Assert.Contains("No eres el líder de tu clan", tb.Textos());
        Assert.Contains("El Pj no es aspirante al clan.", ta.Textos());
    }

    [Fact]
    public async Task Rechaza_cualquier_miembro_y_el_rechazado_lee_el_motivo()
    {
        var g = ClanDe("Aldo", "Bruma");
        Conectado("Aldo", Clan);
        var (_, sb, tb) = Conectado("Bruma", Clan);   // no es el líder
        var (_, _, tc) = Conectado("Ciro");
        g.JoinRequests["Ciro"] = "";

        await Mandar(sb, Paquete(ClientPacketId.GuildRejectNewMember).WriteAsciiString("Ciro").WriteAsciiString("muy nuevo"));
        await Asentar(tb, tc);

        Assert.Empty(g.JoinRequests);
        Assert.Contains($"Fue rechazada tu solicitud de ingreso a {Clan} : muy nuevo", tc.Textos());
        Assert.Empty(tb.Textos());
    }

    [Fact]
    public async Task El_motivo_de_un_rechazo_a_un_desconectado_queda_guardado()
    {
        var g = ClanDe("Aldo");
        var (_, sa, _) = Conectado("Aldo", Clan);
        g.JoinRequests["Ciro"] = "";

        await Mandar(sa, Paquete(ClientPacketId.GuildRejectNewMember).WriteAsciiString("Ciro").WriteAsciiString("muy nuevo"));

        Assert.Equal("muy nuevo", _world.MotivosDeRechazo["Ciro"]);
    }

    [Fact]
    public async Task Expulsar_avisa_al_clan_con_el_sonido_45_y_el_expulsado_no_lo_lee()
    {
        ClanDe("Aldo", "Bruma");
        var (_, sa, ta) = Conectado("Aldo", Clan);
        var (b, _, tb) = Conectado("Bruma", Clan);

        await Mandar(sa, Paquete(ClientPacketId.GuildKickMember).WriteAsciiString("Bruma"));
        await Asentar(ta, tb);

        Assert.Equal("", b.GuildName);
        Assert.Contains("Bruma fue expulsado del clan.", ta.Textos());
        Assert.True(ta.Recibio(ServerMessages.PlayWave(45, 0, 0)));
        Assert.DoesNotContain(tb.Textos(), m => m.Contains("expulsado"));
    }

    [Fact]
    public async Task Lo_que_no_se_puede_echar_calla()
    {
        ClanDe("Aldo", "Bruma", "Ciro");
        var (_, sa, ta) = Conectado("Aldo", Clan);
        var (_, sb, tb) = Conectado("Bruma", Clan);
        var (c, _, _) = Conectado("Ciro", Clan);

        await Mandar(sb, Paquete(ClientPacketId.GuildKickMember).WriteAsciiString("Ciro"));   // no es el líder
        await Mandar(sa, Paquete(ClientPacketId.GuildKickMember).WriteAsciiString("Aldo"));   // el líder no sale
        await Asentar(ta, tb);

        Assert.Equal(Clan, c.GuildName);
        Assert.Empty(ta.Textos());
        Assert.Empty(tb.Textos());
    }

    [Fact]
    public async Task Salir_avisa_a_los_que_quedan_y_no_al_que_se_va()
    {
        ClanDe("Aldo", "Bruma");
        var (_, _, ta) = Conectado("Aldo", Clan);
        var (b, sb, tb) = Conectado("Bruma", Clan);

        await Mandar(sb, Paquete(ClientPacketId.GuildLeave));
        await Asentar(ta, tb);

        Assert.Equal("", b.GuildName);
        Assert.Contains("Bruma deja el clan.", ta.Textos());
        Assert.Empty(tb.Textos());
    }

    [Fact]
    public async Task El_rajarclan_del_gm_avisa_al_clan_y_al_gm_nada()
    {
        ClanDe("Aldo", "Bruma");
        var (_, _, ta) = Conectado("Aldo", Clan);
        var (b, _, _) = Conectado("Bruma", Clan);
        var (_, sg, tg) = Conectado("ElDios", priv: 8);

        await Mandar(sg, Paquete(ClientPacketId.GMCommands).WriteByte(65).WriteAsciiString("Bruma"));
        await Asentar(ta, tg);

        Assert.Equal("", b.GuildName);
        Assert.Contains("Bruma ha sido expulsado del clan por los administradores del servidor.", ta.Textos());
        Assert.Empty(tg.Textos());
    }
}
