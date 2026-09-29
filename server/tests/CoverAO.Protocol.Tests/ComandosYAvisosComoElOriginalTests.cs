using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE QUEDABA DEL BARRIDO DE TEXTOS INVENTADOS. /HORA se anuncia a todos ("Hora: …"), /LISTUSU contesta sólo
/// con la lista y no al RoleMaster, /CI de un objeto que no existe calla, /CARCEL no le contesta al GM, /RMATA mata
/// a la criatura clickeada ("RMatas (con posible respawn) a: X"), /MIEMBROSCLAN da una línea "Nombre&lt;clan&gt;" por
/// miembro, y las noticias del clan traen los avisos de la elección en vez de un resumen por consola.
/// </summary>
public class ComandosYAvisosComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_comandos_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
        public bool RecibioId(ServerPacketId id) => Recibidos.Any(x => x.Length > 0 && x[0] == (byte)id);
        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    public ComandosYAvisosComoElOriginalTests()
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

    private (Player, ClientSession, Transporte) Conectado(string nombre, byte x, byte privilegios = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(1, x, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = 30, Privileges = privilegios,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Mandar(ClientSession s, ByteQueueWriter w)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(w.ToArray()));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ByteQueueWriter Gm(byte comando) => new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(comando);

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
    public async Task La_hora_se_le_anuncia_a_todo_el_servidor()
    {
        var (_, s, t) = Conectado("Rugura", 50, privilegios: 8);
        var (_, _, otro) = Conectado("Aldo", 90);

        await Mandar(s, Gm(5));
        await Asentar(t, otro);

        Assert.Contains(otro.Textos(), m => m.StartsWith("Hora: "));
        Assert.DoesNotContain(t.Textos(), m => m.StartsWith("Hora del servidor"));
    }

    [Fact]
    public async Task La_lista_de_usuarios_va_sin_texto_y_no_al_rolemaster()
    {
        var (_, s, t) = Conectado("Rugura", 50, privilegios: 8);
        var (_, sr, rm) = Conectado("Rol", 60, privilegios: 32);

        await Mandar(s, Gm(14));
        await Mandar(sr, Gm(14));
        await Asentar(t, rm);

        Assert.True(t.RecibioId(ServerPacketId.UserNameList));
        Assert.Empty(t.Textos());
        Assert.Empty(rm.Recibidos);
    }

    [Fact]
    public async Task Crear_un_objeto_que_no_existe_calla()
    {
        var (_, s, t) = Conectado("Rugura", 50, privilegios: 8);

        await Mandar(s, Gm(53).WriteInteger(-5).WriteInteger(1));
        await Asentar(t);

        Assert.Empty(t.Recibidos);
    }

    [Fact]
    public async Task Encarcelar_no_le_contesta_al_gm()
    {
        var (_, s, t) = Conectado("Rugura", 50, privilegios: 8);
        var (preso, _, _) = Conectado("Aldo", 60);

        await Mandar(s, Gm(17).WriteAsciiString("Aldo").WriteAsciiString("spam").WriteByte(5));
        await Asentar(t);

        Assert.Equal(5, preso.JailMinutesLeft);
        Assert.Empty(t.Textos());
    }

    [Fact]
    public async Task Rmata_mata_a_la_criatura_clickeada_y_avisa_como_el_original()
    {
        var (gm, s, t) = Conectado("Rugura", 50, privilegios: 8);
        var npc = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 100, Pos = new WorldPos(1, 70, 70), Movement = 1,
            Template = new NpcInfo(Index: 904, Name: "Lobo", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 0, MaxHit: 0,
                                   Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        };
        _world.AddNpc(npc);

        await Mandar(s, Gm(18));                   // sin criatura marcada
        gm.TargetNpc = npc.CharIndex;
        await Mandar(s, Gm(18));
        await Asentar(t);

        Assert.True(t.Recibio(ServerMessages.LocaleMsg(22)));
        Assert.Contains("RMatas (con posible respawn) a: Lobo", t.Textos());
        Assert.False(_world.HasNpc(npc.CharIndex));
        Assert.Equal(0, gm.TargetNpc);
    }

    [Fact]
    public async Task Los_miembros_de_un_clan_van_de_a_uno_por_linea()
    {
        var (_, s, t) = Conectado("Rugura", 50, privilegios: 8);
        var g = _world.CreateGuild("Los Del Sur", "Aldo", "desc")!;
        g.Members.Add("Bruma");

        await Mandar(s, Gm(49).WriteAsciiString("Los Del Sur"));
        await Mandar(s, Gm(49).WriteAsciiString("Nadie"));
        await Asentar(t);

        Assert.Contains("Aldo<Los Del Sur>", t.Textos());
        Assert.Contains("Bruma<Los Del Sur>", t.Textos());
        Assert.Contains("No existe el clan: Nadie", t.Textos());
    }

    [Fact]
    public async Task La_mesa_de_trabajo_abre_su_lista_sin_texto()
    {
        var (_, s, t) = Conectado("Aldo", 50);
        _world.AddNpc(new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 100, Pos = new WorldPos(1, 51, 50), Movement = 1,
            WorkbenchTrade = "carpenter",
            Template = new NpcInfo(Index: 904, Name: "Carpintero", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 0,
                                   MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        });

        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.DoubleClick).WriteByte(51).WriteByte(50));
        await Asentar(t);

        Assert.True(t.RecibioId(ServerPacketId.CarpenterObjects));
        Assert.Empty(t.Textos());
    }

    [Fact]
    public async Task Las_noticias_del_clan_traen_los_avisos_de_la_eleccion_y_ningun_resumen()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        var g = _world.CreateGuild("Los Del Sur", "Aldo", "desc")!;
        p.GuildName = g.Name;

        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.ShowGuildNews));
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.GuildAlliancePropList));
        await Asentar(t);
        Assert.True(t.RecibioId(ServerPacketId.guildNews));
        Assert.Empty(t.Textos());

        g.ElectionsOpen = true;
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.ShowGuildNews));
        await Asentar(t);
        Assert.Equal(
        [
            "Hoy es la votación para elegir un nuevo líder para el clan.",
            "La elección durará 24 horas, se puede votar a cualquier miembro del clan.",
            "Para votar escribe /VOTO NICKNAME.",
            "Sólo se computará un voto por miembro. Tu voto no puede ser cambiado.",
        ], t.Textos());
    }
}
