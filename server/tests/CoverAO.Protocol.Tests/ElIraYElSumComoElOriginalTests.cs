using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL /IRA Y EL /SUM COMO EL ORIGINAL (VB6 HandleGoToChar, Protocol.bas:7040, y HandleSummonChar, :8725). El que salta
/// cae en la baldosa de ABAJO del otro, o en la primera sin nadie que encuentra <c>FindLegalPos</c> (GameLogic.bas:546);
/// el /IRA le avisa al jugador que el GM llegó —salvo que el GM esté invisible— y el /SUM le avisa quién lo trajo; al
/// GM ninguno de los dos le contesta nada. Acá los dos quedaban parados en la misma baldosa y el GM recibía un texto
/// nuestro.
/// </summary>
public class ElIraYElSumComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_ira_" + Guid.NewGuid().ToString("N"));
    private readonly string _ini = Path.Combine(Path.GetTempPath(), "coverao_ira_" + Guid.NewGuid().ToString("N") + ".ini");
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private PacketDispatcher _dispatcher;

    private const byte GmIra = 11, GmSum = 32;
    private const byte Consejero = 2, SemiDios = 4, Dios = 8, RoleMaster = 32;
    private const byte FontTypeInfo = 3;

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

    public ElIraYElSumComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = Dispatcher(new ServerIni());
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        try { File.Delete(_ini); } catch { /* idem */ }
    }

    private PacketDispatcher Dispatcher(ServerIni ini)
    {
        var leveling = new Leveling(_datos.LevelExp);
        return new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling,
                                    serverIni: ini);
    }

    private (Player, ClientSession, Transporte) Conectado(string nombre, WorldPos pos, byte privilegios = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = pos,
            Heading = 3, Hp = 100, MaxHp = 100, Level = 30, Privileges = privilegios,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private void Criatura(WorldPos pos) => _world.AddNpc(new Npc
    {
        CharIndex = _world.AllocateCharIndex(), Hp = 100, Pos = pos,
        Template = new NpcInfo(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 0, MaxHit: 0,
                               Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
    });

    private async Task Comando(ClientSession gm, byte sub, string nombre)
    {
        _dispatcher.HandleOne(gm, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands)
            .WriteByte(sub).WriteAsciiString(nombre).ToArray()));
        await Task.Delay(300);
    }

    private static WorldPos En(int x, int y, short mapa = 1) => new(mapa, (byte)x, (byte)y);
    private static byte[] Aviso(string texto) => ServerMessages.ConsoleMsg(texto, FontTypeInfo);

    // ── EL /IRA ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_ira_cae_abajo_del_jugador_le_avisa_y_al_gm_no_le_dice_nada()
    {
        var (gm, sesionGm, delGm) = Conectado("Rugura", En(30, 30), Dios);
        var (_, _, delOtro) = Conectado("Merlin", En(50, 50, mapa: 34));

        await Comando(sesionGm, GmIra, "Merlin");

        Assert.Equal(En(50, 51, mapa: 34), gm.Pos);
        Assert.True(delOtro.Recibio(Aviso("Rugura se ha trasportado hacia donde te encuentras.")));
        Assert.Empty(delGm.De(ServerPacketId.ConsoleMsg));
    }

    [Fact]
    public async Task El_gm_invisible_llega_sin_avisar()
    {
        var (gm, sesionGm, _) = Conectado("Rugura", En(30, 30), Dios);
        gm.AdminInvisible = gm.Invisible = true;
        var (_, _, delOtro) = Conectado("Merlin", En(50, 50));

        await Comando(sesionGm, GmIra, "Merlin");

        Assert.Equal(En(50, 51), gm.Pos);
        Assert.Empty(delOtro.De(ServerPacketId.ConsoleMsg));
    }

    /// <summary>Con alguien abajo, <c>FindLegalPos</c> recorre el primer anillo fila por fila desde la esquina de arriba a
    /// la izquierda y se queda con la primera baldosa sin nadie.</summary>
    [Fact]
    public async Task Con_alguien_abajo_busca_la_primera_baldosa_sin_nadie()
    {
        var (gm, sesionGm, _) = Conectado("Rugura", En(30, 30), Dios);
        Conectado("Merlin", En(50, 50));
        Conectado("Marino", En(49, 50));
        Criatura(En(50, 51));

        await Comando(sesionGm, GmIra, "Merlin");

        Assert.Equal(En(51, 50), gm.Pos);
    }

    /// <summary>De cada fila mira sólo la primera baldosa sin nadie, y si cae fuera de los bordes jugables pasa a la fila
    /// siguiente. Pegado al borde oeste la primera de todas las filas está afuera, así que no encuentra lugar y el GM
    /// cae en la pedida, encima de la criatura: el original la pisa.</summary>
    [Fact]
    public async Task Pegado_al_borde_no_encuentra_lugar_y_pisa_a_la_criatura()
    {
        var (gm, sesionGm, _) = Conectado("Rugura", En(50, 50), Dios);
        Conectado("Merlin", En(GameLimits.MinXBorder, 50));
        Criatura(En(GameLimits.MinXBorder, 51));

        await Comando(sesionGm, GmIra, "Merlin");

        Assert.Equal(En(GameLimits.MinXBorder, 51), gm.Pos);
    }

    /// <summary>Mira sólo quién está parado: si la baldosa de abajo es una pared sin nadie, el GM cae ahí, como allá.</summary>
    [Fact]
    public async Task Una_pared_abajo_no_lo_desvia()
    {
        var (x, y) = UnaBaldosaConParedAbajo(mapa: 1);
        var (gm, sesionGm, _) = Conectado("Rugura", En(30, 30, mapa: 2), Dios);
        Conectado("Merlin", En(x, y));

        await Comando(sesionGm, GmIra, "Merlin");

        Assert.Equal(En(x, y + 1), gm.Pos);
    }

    private (int, int) UnaBaldosaConParedAbajo(short mapa)
    {
        for (int y = 20; y < 80; y++)
            for (int x = 20; x < 80; x++)
                if (!_datos.IsBlockedTile(mapa, x, y) && _datos.IsBlockedTile(mapa, x, y + 1)) return (x, y);
        throw new InvalidOperationException("el mapa no tiene una baldosa libre con pared abajo");
    }

    [Fact]
    public async Task Hasta_un_dios_solo_va_otro_dios()
    {
        var (semi, sesionSemi, delSemi) = Conectado("Semi", En(30, 30, mapa: 2), SemiDios);
        var (dios, sesionDios, _) = Conectado("Rugura", En(40, 40), Dios);
        Conectado("Zeus", En(60, 60), Dios);

        await Comando(sesionSemi, GmIra, "Zeus");
        await Comando(sesionDios, GmIra, "Zeus");

        Assert.Equal(En(30, 30, mapa: 2), semi.Pos);
        Assert.Empty(delSemi.Recibidos);
        Assert.Equal(En(60, 61), dios.Pos);
    }

    /// <summary>El original lo mira en las listas del Server.ini, así que calla aunque el Dios esté desconectado: al
    /// SemiDios no le dice "Usuario offline".</summary>
    [Fact]
    public async Task Al_dios_desconectado_tampoco_le_contesta()
    {
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\nDioses=1\r\n[Dioses]\r\nDios1=Zeus\r\n");
        _dispatcher = Dispatcher(ServerIni.Cargar(_ini));
        var (_, sesionSemi, delSemi) = Conectado("Semi", En(30, 30), SemiDios);
        var (_, sesionDios, delDios) = Conectado("Rugura", En(40, 40), Dios);

        await Comando(sesionSemi, GmIra, "Zeus");
        await Comando(sesionDios, GmIra, "Zeus");

        Assert.Empty(delSemi.Recibidos);
        Assert.True(delDios.Recibio(ServerMessages.LocaleMsg(75)));
    }

    [Fact]
    public async Task Al_desconectado_le_contesta_usuario_offline()
    {
        var (_, sesionGm, delGm) = Conectado("Rugura", En(30, 30), Consejero);

        await Comando(sesionGm, GmIra, "Nadie");

        Assert.True(delGm.Recibio(ServerMessages.LocaleMsg(75)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(RoleMaster)]
    public async Task El_usuario_y_el_rolemaster_no_pueden(byte privilegios)
    {
        var (quien, sesion, suyo) = Conectado("Rugura", En(30, 30), privilegios);
        Conectado("Merlin", En(50, 50));

        await Comando(sesion, GmIra, "Merlin");

        Assert.Equal(En(30, 30), quien.Pos);
        Assert.Empty(suyo.Recibidos);
    }

    // ── EL /SUM ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_sum_trae_abajo_del_gm_le_avisa_y_al_gm_no_le_dice_nada()
    {
        var (_, sesionGm, delGm) = Conectado("Rugura", En(50, 50, mapa: 34), Dios);
        var (otro, _, delOtro) = Conectado("Merlin", En(30, 30));

        await Comando(sesionGm, GmSum, "Merlin");

        Assert.Equal(En(50, 51, mapa: 34), otro.Pos);
        Assert.True(delOtro.Recibio(Aviso("Rugura te ha trasportado.")));
        Assert.Empty(delGm.De(ServerPacketId.ConsoleMsg));
    }

    [Fact]
    public async Task Con_alguien_abajo_del_gm_busca_lugar()
    {
        var (_, sesionGm, _) = Conectado("Rugura", En(50, 50), Dios);
        var (otro, _, _) = Conectado("Merlin", En(30, 30));
        Criatura(En(50, 51));

        await Comando(sesionGm, GmSum, "Merlin");

        Assert.Equal(En(49, 50), otro.Pos);
    }

    /// <summary>El SemiDios trae sólo a usuarios y Consejeros; a otro rango, "Objetivo inválido." y no lo mueve.</summary>
    [Fact]
    public async Task El_semidios_no_trae_a_otro_semidios()
    {
        var (_, sesionGm, delGm) = Conectado("Semi", En(50, 50), SemiDios);
        var (otroSemi, _, _) = Conectado("Otro", En(30, 30), SemiDios);
        var (consejero, _, _) = Conectado("Conse", En(20, 20), Consejero);

        await Comando(sesionGm, GmSum, "Otro");
        await Comando(sesionGm, GmSum, "Conse");

        Assert.Equal(En(30, 30), otroSemi.Pos);
        Assert.True(delGm.Recibio(ServerMessages.LocaleMsg(298)));
        Assert.Equal(En(50, 51), consejero.Pos);
    }

    [Fact]
    public async Task El_consejero_no_puede_sum()
    {
        var (_, sesionGm, delGm) = Conectado("Conse", En(50, 50), Consejero);
        var (otro, _, _) = Conectado("Merlin", En(30, 30));

        await Comando(sesionGm, GmSum, "Merlin");

        Assert.Equal(En(30, 30), otro.Pos);
        Assert.Empty(delGm.Recibidos);
    }
}
