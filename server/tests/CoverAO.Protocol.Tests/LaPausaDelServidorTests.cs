using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PAUSA DEL SERVIDOR, COMO EL ORIGINAL (el botón de frmServidor, `EnPausa`). Frena SÓLO la IA de las criaturas
/// (TIMER_AI, mMainLoop.bas:77) —con su parálisis, que no corre— y los logins, que reciben el aviso de mantenimiento
/// sin que se les corte la conexión (Cuentas.bas:393). A todos les avisa con el PauseToggle y el 3, y al reanudar
/// con el PauseToggle y el 436. Acá congelaba el mundo entero y avisaba con un texto propio.
/// </summary>
public class LaPausaDelServidorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_pausa_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly AdminConfig _admin = new();
    private readonly PacketDispatcher _dispatcher;

    private const string Mantenimiento = "El servidor se encuentra restringido por mantenimiento. Por favor intente más tarde.";

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public volatile bool Cerrado;
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) => Cerrado = true;
    }

    private sealed class Conexion : IConnection
    {
        public readonly List<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Add(packet);
        public void Close(string reason) { }
    }

    public LaPausaDelServidorTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling,
            admin: _admin);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    /// <summary>Los textos de los ShowMessageBox que llegaron.</summary>
    private static List<string> Carteles(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.ShowMessageBox)
        .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

    private async Task<Transporte> Mandar(byte[] paquete)
    {
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        await Task.Delay(300);
        return t;
    }

    [Fact]
    public async Task En_pausa_entrar_con_un_personaje_avisa_el_mantenimiento_sin_cortar()
    {
        _admin.Paused = true;
        var t = await Mandar(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginExistingChar)
            .WriteAsciiString("cuenta").WriteAsciiString("clave").WriteByte(1).WriteByte(4).WriteByte(5)
            .WriteAsciiString("Aldo").WriteAsciiString("mac").WriteLong(1).ToArray());
        Assert.Equal(new[] { Mantenimiento }, Carteles(t));
        Assert.False(t.Cerrado, "el original deja la conexión abierta (CierraConexion = False)");
        Assert.Empty(_world.Players);
    }

    [Fact]
    public async Task En_pausa_crear_un_personaje_avisa_el_mantenimiento_sin_cortar()
    {
        _admin.Paused = true;
        var t = await Mandar(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginNewChar)
            .WriteAsciiString("cuenta").WriteAsciiString("clave").WriteByte(1).WriteByte(4).WriteByte(5)
            .WriteAsciiString("Aldo").WriteByte(1).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(1)
            .WriteAsciiString("mac").WriteLong(1).ToArray());
        Assert.Equal(new[] { Mantenimiento }, Carteles(t));
        Assert.False(t.Cerrado);
    }

    [Fact]
    public async Task Sin_pausa_el_login_no_avisa_el_mantenimiento()
    {
        var t = await Mandar(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginExistingChar)
            .WriteAsciiString("cuenta").WriteAsciiString("clave").WriteByte(1).WriteByte(4).WriteByte(5)
            .WriteAsciiString("Aldo").WriteAsciiString("mac").WriteLong(1).ToArray());
        Assert.DoesNotContain(Mantenimiento, Carteles(t));
    }

    [Fact]
    public void Pausar_avisa_a_todos_con_el_toggle_y_el_3_y_reanudar_con_el_toggle_y_el_436()
    {
        var world = new World();
        var conexion = new Conexion();
        world.Add(new Player { CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Connection = conexion });
        PacketDispatcher.AvisarPausa(world, pausado: true);
        PacketDispatcher.AvisarPausa(world, pausado: false);
        var ids = conexion.Recibidos.Select(p => p[0] == (byte)ServerPacketId.LocaleMsg
            ? $"LocaleMsg {BitConverter.ToInt16(p, 1)}" : ((ServerPacketId)p[0]).ToString()).ToList();
        Assert.Equal(new[] { "PauseToggle", "LocaleMsg 3", "PauseToggle", "LocaleMsg 436" }, ids);
    }

    /// <summary>Un jugador al lado de una criatura hostil que pega sí o sí, y un muerto esperando el revive.</summary>
    private (NpcSystem Sistema, Player Vivo, Player Muerto, Npc Criatura) Escena()
    {
        var world = new World();
        var vivo = new Player { CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Connection = new Conexion() };
        vivo.Pos = new WorldPos(1, 10, 10);
        vivo.MaxHp = vivo.Hp = 1_000_000;
        world.Add(vivo);
        var muerto = new Player { CharIndex = world.AllocateCharIndex(), Name = "Bruno", Account = "bruno", Connection = new Conexion() };
        muerto.Pos = new WorldPos(1, 30, 30);
        muerto.MaxHp = 100; muerto.Hp = 0;
        muerto.Resucitando = true;
        world.Add(muerto);
        var ficha = new NpcInfo(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 4, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(1, 11, 10),
            Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = true, Heading = 4,
        };
        world.AddNpc(npc);
        return (new NpcSystem(world, _datos), vivo, muerto, npc);
    }

    [Fact]
    public void En_pausa_la_criatura_no_pega_y_el_revive_sigue()
    {
        var (sistema, vivo, muerto, _) = Escena();
        for (int vuelta = 1; vuelta <= 750; vuelta++) sistema.Update(vuelta, pausado: true);
        Assert.Equal(vivo.MaxHp, vivo.Hp);
        Assert.True(muerto.IsAlive, "el revive es de otro timer y no se pausa");

        for (int vuelta = 751; vuelta <= 1500; vuelta++) sistema.Update(vuelta);
        Assert.True(vivo.Hp < vivo.MaxHp, "premisa: sin pausa, la criatura pega");
    }

    [Fact]
    public void La_paralisis_de_una_criatura_no_corre_durante_la_pausa()
    {
        var (sistema, _, _, criatura) = Escena();
        criatura.ParalyzedUntilTick = 100;
        for (int vuelta = 1; vuelta <= 50; vuelta++) sistema.Update(vuelta);
        for (int vuelta = 51; vuelta <= 250; vuelta++) sistema.Update(vuelta, pausado: true);
        sistema.Update(251);
        Assert.Equal(100 + (251 - 51), criatura.ParalyzedUntilTick);
    }
}
