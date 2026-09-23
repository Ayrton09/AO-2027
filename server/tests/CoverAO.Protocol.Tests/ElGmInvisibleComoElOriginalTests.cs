using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL /INVISIBLE DEL GM COMO EL ORIGINAL (HandleInvisible → DoAdminInvisible, Trabajo.bas:1186). No es la invisibilidad
/// del hechizo, que deja al personaje latiendo: el GM desaparece. Cuerpo, cabeza y equipo viajan en 0, el nombre se
/// apaga y el área recibe un SetInvisible(false), sin ningún texto; a quien entra después en su área ni se lo crea; se
/// lo puede pisar y se lo corre (el "casper"), no se lo ataca, y lo que dice lo leen sólo los GMs. Acá le contestaba
/// "Ahora eres invisible." y a los demás les mandaba un SetInvisible(true), que el cliente dibuja como una silueta que
/// late: el GM invisible se veía.
/// </summary>
public class ElGmInvisibleComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_gm_invisible_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly Persistence _db;

    private const byte GmInvisible = 12;
    private const byte Este = 2, Oeste = 4;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
        public void Vaciar() { while (Recibidos.TryDequeue(out _)) { } }
    }

    public ElGmInvisibleComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player, ClientSession, Transporte) Conectado(string nombre, WorldPos pos, byte privilegios = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(), Pos = pos,
            Heading = Este, Hp = 1000, MaxHp = 1000, Level = 30, Stamina = 1000, MaxStamina = 1000, Privileges = privilegios,
            Body = 21, Head = 2, WeaponAnim = 5, ShieldAnim = 3, HelmetAnim = 4,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        await Task.Delay(300);
    }

    private Task Invisible(ClientSession gm) => Mandar(gm, (byte)ClientPacketId.GMCommands, GmInvisible);
    private Task Caminar(ClientSession s, byte hacia) => Mandar(s, (byte)ClientPacketId.Walk, hacia);

    /// <summary>Tres baldosas libres en fila de oeste a este (ni pared, ni agua, ni salida, ni arena) en un mapa del
    /// tipo pedido: la data decide dónde, para no atarse a un número.</summary>
    private WorldPos Fila(bool pk)
    {
        foreach (short mapa in new short[] { 1, 34, 37, 15, 2, 3, 11, 12 })
        {
            if (_datos.IsPkMap(mapa) != pk) continue;
            for (byte y = 20; y < 80; y++)
                for (byte x = 20; x < 78; x++)
                    if (Enumerable.Range(0, 3).All(d => Libre(mapa, x + d, y))) return new WorldPos(mapa, x, y);
        }
        throw new InvalidOperationException("no hay tres baldosas libres en fila en los mapas probados");
    }

    private bool Libre(short mapa, int x, int y) =>
        !_datos.IsBlockedTile(mapa, x, y) && !_datos.IsWater(mapa, x, y) && !_datos.IsArena(mapa, x, y)
        && !_datos.MapExits.ContainsKey((mapa, x, y)) && !_datos.IsNpcInvalidTile(mapa, x, y);

    private static WorldPos Al(WorldPos p, int dx) => new(p.Map, (byte)(p.X + dx), p.Y);

    // Los campos de un CharacterChange (id, CharIndex, Body, Head, Heading, Arma, Escudo, Casco, FX, FXLoops).
    private static (short ci, short body, short head, short arma, short escudo, short casco) Cambio(byte[] x) =>
        (BitConverter.ToInt16(x, 1), BitConverter.ToInt16(x, 3), BitConverter.ToInt16(x, 5),
         BitConverter.ToInt16(x, 8), BitConverter.ToInt16(x, 10), BitConverter.ToInt16(x, 12));

    private static string Tag(byte[] x) => Encoding.Latin1.GetString(x, 5, BitConverter.ToInt16(x, 3));

    // ── EL COMANDO ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hacerse_invisible_no_contesta_nada_y_al_area_le_llega_lo_del_original()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        var (_, _, delOtro) = Conectado("Merlin", Al(aca, 2));
        await Invisible(sesionGm);

        Assert.True(gm.AdminInvisible && gm.Invisible);
        Assert.Empty(suyo.De(ServerPacketId.ConsoleMsg));                          // ningún "Ahora eres invisible."
        foreach (var t in new[] { suyo, delOtro })                                 // ToPCArea: él incluido
        {
            var orden = t.Recibidos.Where(x => x.Length > 0 && x[0] is (byte)ServerPacketId.UpdateTagAndStatus
                or (byte)ServerPacketId.CharacterChange or (byte)ServerPacketId.SetInvisible).Select(x => x[0]).ToList();
            Assert.Equal(new[] { (byte)ServerPacketId.UpdateTagAndStatus, (byte)ServerPacketId.CharacterChange,
                                 (byte)ServerPacketId.SetInvisible }, orden);
            Assert.Equal("", Tag(t.De(ServerPacketId.UpdateTagAndStatus).Single()));
            Assert.Equal((gm.CharIndex, (short)0, (short)0, (short)0, (short)0, (short)0),
                         Cambio(t.De(ServerPacketId.CharacterChange).Single()));
            Assert.True(t.Recibio(ServerMessages.SetInvisible(gm.CharIndex, false)), "no le llegó el SetInvisible(false)");
            Assert.False(t.Recibio(ServerMessages.SetInvisible(gm.CharIndex, true)), "lo mandó latiendo");
        }
    }

    [Fact]
    public async Task Al_volver_recupera_su_apariencia_entera()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, _) = Conectado("Rugura", aca, privilegios: 8);
        var (_, _, delOtro) = Conectado("Merlin", Al(aca, 2));
        await Invisible(sesionGm);
        delOtro.Vaciar();
        await Invisible(sesionGm);

        Assert.False(gm.AdminInvisible || gm.Invisible);
        Assert.Equal("Rugura", Tag(delOtro.De(ServerPacketId.UpdateTagAndStatus).Single()));
        // Con el arma, el escudo y el casco: el original no los restaura, y su cliente no los pierde porque toma el
        // 0 como "sin cambio"; el web lo toma como "nada".
        Assert.Equal((gm.CharIndex, (short)21, (short)2, (short)5, (short)3, (short)4),
                     Cambio(delOtro.De(ServerPacketId.CharacterChange).Single()));
    }

    [Fact]
    public async Task Un_jugador_comun_no_puede()
    {
        var (p, sesion, suyo) = Conectado("Aldo", Fila(pk: false));
        await Invisible(sesion);
        Assert.False(p.AdminInvisible || p.Invisible);
        Assert.Empty(suyo.Recibidos);
    }

    /// <summary>Equiparse, cambiarse la cara o navegar no lo destapan: las piezas sueltas también viajan en 0. La
    /// dirección, que no es apariencia, va igual.</summary>
    [Fact]
    public void Las_piezas_sueltas_tambien_viajan_tapadas()
    {
        var p = new Player { CharIndex = 7, Name = "Rugura", Account = "rugura", Body = 21, AdminInvisible = true };
        Assert.Equal(ServerMessages.CharacterChangeSlot(7, 0, 1), ServerMessages.CharacterChangeSlot(p, p.Body, 1));
        Assert.Equal(ServerMessages.CharacterChangeSlot(7, 3, 3), ServerMessages.CharacterChangeSlot(p, 3, 3));
        p.AdminInvisible = false;
        Assert.Equal(ServerMessages.CharacterChangeSlot(7, 21, 1), ServerMessages.CharacterChangeSlot(p, p.Body, 1));
    }

    // ── LAS ÁREAS ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A quien lo empieza a ver no se le crea (ModAreas.bas:226/241), y él sí ve a los demás.</summary>
    [Fact]
    public async Task Al_que_lo_empieza_a_ver_no_se_le_crea()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        await Invisible(sesionGm);
        var lejos = new WorldPos(aca.Map, (byte)(aca.X + 40 > 90 ? aca.X - 40 : aca.X + 40), aca.Y);
        var (otro, _, delOtro) = Conectado("Merlin", lejos);
        suyo.Vaciar();

        otro.Pos = Al(aca, 2);
        PacketDispatcher.RefreshAreaVisibility(_world, _datos, otro, lejos, otro.Pos);
        await Task.Delay(300);

        Assert.DoesNotContain(delOtro.De(ServerPacketId.CharacterCreate), x => BitConverter.ToInt16(x, 1) == gm.CharIndex);
        Assert.Contains(suyo.De(ServerPacketId.CharacterCreate), x => BitConverter.ToInt16(x, 1) == otro.CharIndex);
    }

    /// <summary>Al que entra al mundo tampoco se le crea; y al invisible por hechizo que tiene a la vista se lo manda
    /// con su SetInvisible, como en el cambio de área (acá le llegaba entero).</summary>
    [Fact]
    public async Task Al_que_entra_al_mundo_no_se_le_crea()
    {
        var cuenta = _db.CreateAccount("cuenta", "clave");
        var nace = _db.CreateCharacter(cuenta, "Aldo", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1).Pos;
        var (gm, sesionGm, _) = Conectado("Rugura", Al(nace, 1), privilegios: 8);
        await Invisible(sesionGm);
        var (oculto, _, _) = Conectado("Bruno", Al(nace, 2));
        oculto.Invisible = true;
        oculto.InvisibleUntilTick = long.MaxValue;

        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginExistingChar)
            .WriteAsciiString("cuenta").WriteAsciiString("clave").WriteByte(1).WriteByte(4).WriteByte(5)
            .WriteAsciiString("Aldo").WriteAsciiString("mac").WriteLong(1).ToArray()));
        for (int i = 0; i < 40 && !t.Recibidos.Any(x => x[0] == (byte)ServerPacketId.Logged); i++)
            await Task.Delay(100);                                                 // el login verifica la clave afuera del loop
        await Task.Delay(300);

        Assert.Contains(_world.Players, p => p.Name == "Aldo");
        var creados = t.De(ServerPacketId.CharacterCreate).Select(x => BitConverter.ToInt16(x, 1)).ToList();
        Assert.DoesNotContain(gm.CharIndex, creados);
        Assert.Contains(oculto.CharIndex, creados);
        Assert.True(t.Recibio(ServerMessages.SetInvisible(oculto.CharIndex, true)), "al invisible por hechizo lo vio entero");
    }

    /// <summary>La criatura también lo pisa y lo corre (LegalPosNPC, GameLogic.bas:642; el swap en MODULO_NPCs.bas:707):
    /// una que deambula y cuya única salida es la baldosa del GM termina en ella, con el GM en la suya. El control: a un
    /// jugador visible no lo corre nunca.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void La_criatura_lo_pisa_y_lo_corre(bool invisible)
    {
        var world = new World();
        var data = new GameData();
        // La criatura en (10,10), encerrada salvo por el este, donde está el GM.
        foreach (var (x, y) in new[] { (10, 9), (10, 11), (9, 10) }) data.AddBlocked(1, x, y);
        var gm = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "rugura", Privileges = 8 };
        gm.Pos = new WorldPos(1, 11, 10);
        gm.MaxHp = gm.Hp = 1000;
        gm.Invisible = gm.AdminInvisible = invisible;
        world.Add(gm);
        var sistema = new NpcSystem(world, data);
        sistema.AddSpawn(new NpcInfo(Index: 1, Name: "Rata", Body: 1, Head: 1, Heading: 3, MaxHp: 10, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0), new WorldPos(1, 10, 10), movement: 3, posOrig: true);
        var rata = world.Npcs.Single();

        for (int vuelta = 1; vuelta <= 600 && rata.Pos.X == 10; vuelta++) sistema.Update(vuelta * NpcSystem.MoveEveryTicks);

        if (invisible)
        {
            Assert.Equal(new WorldPos(1, 11, 10), rata.Pos);
            Assert.Equal(new WorldPos(1, 10, 10), gm.Pos);
        }
        else
        {
            Assert.Equal(new WorldPos(1, 10, 10), rata.Pos);
            Assert.Equal(new WorldPos(1, 11, 10), gm.Pos);
        }
    }

    /// <summary>En el salto, al que ve las dos puntas le llega el borrado pero no se lo vuelve a crear, y el salto no
    /// suena (Modulo_UsUaRiOs.bas:1597, :1647 y :1652).</summary>
    [Fact]
    public async Task Al_saltar_no_se_lo_vuelve_a_crear_en_nadie()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, _) = Conectado("Rugura", aca, privilegios: 8);
        var (_, _, delOtro) = Conectado("Merlin", Al(aca, 2));
        await Invisible(sesionGm);
        delOtro.Vaciar();

        _dispatcher.WarpUserChar(gm, Al(aca, 1), fx: true);
        await Task.Delay(300);

        Assert.True(delOtro.Recibio(ServerMessages.CharacterRemove(gm.CharIndex, faded: true)));
        Assert.DoesNotContain(delOtro.De(ServerPacketId.CharacterCreate), x => BitConverter.ToInt16(x, 1) == gm.CharIndex);
        Assert.Empty(delOtro.De(ServerPacketId.PlayWave));
    }

    /// <summary>El clic de un jugador común lo selecciona igual; el de otro GM, no (GameLogic.bas:770, con la
    /// precedencia del `And` sobre el `Or`).</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(8, false)]
    public async Task El_clic_de_otro_gm_no_lo_selecciona(byte privilegios, bool loSelecciona)
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, _) = Conectado("Rugura", aca, privilegios: 8);
        var (quien, sesion, _) = Conectado("Merlin", Al(aca, 2), privilegios);
        await Invisible(sesionGm);

        await Mandar(sesion, (byte)ClientPacketId.LeftClick, gm.Pos.X, gm.Pos.Y);

        Assert.Equal(loSelecciona ? gm.CharIndex : (short)0, quien.TargetUser);
    }

    // ── CAMINAR: EL CASPER ────────────────────────────────────────────────────────────────────────────

    /// <summary>Al GM invisible se lo pisa, y se lo corre a la baldosa de la que viene el que camina: le llega su
    /// posición vieja y un ForceCharMove hacia el otro lado (Modulo_UsUaRiOs.bas:749-765).</summary>
    [Fact]
    public async Task Al_gm_invisible_se_lo_pisa_y_se_lo_corre()
    {
        var aca = Fila(pk: false);
        var (p, sesion, _) = Conectado("Aldo", aca);
        var (gm, sesionGm, suyo) = Conectado("Rugura", Al(aca, 1), privilegios: 8);
        await Invisible(sesionGm);
        suyo.Vaciar();

        await Caminar(sesion, Este);

        Assert.Equal(Al(aca, 1), p.Pos);
        Assert.Equal(aca, gm.Pos);
        var orden = suyo.Recibidos.Where(x => x.Length > 0 && x[0] is (byte)ServerPacketId.PosUpdate
            or (byte)ServerPacketId.ForceCharMove).ToList();
        Assert.Equal(ServerMessages.PosUpdate(Al(aca, 1).X, aca.Y), orden[0]);   // la vieja, antes de correrlo
        Assert.Equal(ServerMessages.ForceCharMove(Oeste), orden[1]);
    }

    [Fact]
    public async Task Al_muerto_se_lo_pisa_y_se_lo_corre()
    {
        var aca = Fila(pk: false);
        var (p, sesion, _) = Conectado("Aldo", aca);
        var (muerto, _, suyo) = Conectado("Bruno", Al(aca, 1));
        muerto.Hp = 0;

        await Caminar(sesion, Este);

        Assert.Equal(Al(aca, 1), p.Pos);
        Assert.Equal(aca, muerto.Pos);                                              // antes quedaban en la misma baldosa
        Assert.True(suyo.Recibio(ServerMessages.ForceCharMove(Oeste)));
    }

    /// <summary>El que camina con /INVISIBLE no corre a nadie: rebota.</summary>
    [Fact]
    public async Task El_gm_invisible_no_corre_a_nadie()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        var (muerto, _, _) = Conectado("Bruno", Al(aca, 1));
        muerto.Hp = 0;
        await Invisible(sesionGm);
        suyo.Vaciar();

        await Caminar(sesionGm, Este);

        Assert.Equal(aca, gm.Pos);
        Assert.Equal(Al(aca, 1), muerto.Pos);
        Assert.True(suyo.Recibio(ServerMessages.PosUpdate(aca.X, aca.Y)), "no rebotó");
    }

    /// <summary>Al invisible POR HECHIZO no se lo atraviesa: el original sólo deja pisar al muerto y al GM invisible
    /// (GameLogic.bas:527). Acá se lo dejaba pasar.</summary>
    [Fact]
    public async Task Al_invisible_por_hechizo_no_se_lo_atraviesa()
    {
        var aca = Fila(pk: false);
        var (p, sesion, suyo) = Conectado("Aldo", aca);
        var (otro, _, _) = Conectado("Bruno", Al(aca, 1));
        otro.Invisible = true;
        otro.InvisibleUntilTick = long.MaxValue;

        await Caminar(sesion, Este);

        Assert.Equal(aca, p.Pos);
        Assert.Equal(Al(aca, 1), otro.Pos);
        Assert.True(suyo.Recibio(ServerMessages.PosUpdate(aca.X, aca.Y)), "no rebotó");
    }

    // ── ATACARLO, HABLAR, OCULTARSE ───────────────────────────────────────────────────────────────────

    /// <summary>Al GM invisible no se lo ataca, y sin el 101 que lo delataría; al GM visible se lo rechaza con el 101
    /// (es el control: prueba que el golpe llegó hasta ahí).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Al_gm_invisible_no_se_lo_ataca_y_nadie_se_entera(bool invisible)
    {
        var aca = Fila(pk: true);
        var (p, sesion, suyo) = Conectado("Aldo", aca);
        p.CombatMode = true;
        var (gm, sesionGm, _) = Conectado("Rugura", Al(aca, 1), privilegios: 8);
        gm.FactionStatus = ServerIni.StatusDeStaff(8);   // el status de staff que le pone el ini: si no, corta antes el 97
        if (invisible) await Invisible(sesionGm);
        suyo.Vaciar();

        await Mandar(sesion, (byte)ClientPacketId.attack);

        Assert.Equal(gm.MaxHp, gm.Hp);
        Assert.Equal(!invisible, suyo.Recibio(ServerMessages.LocaleMsg(101)));
    }

    /// <summary>En la arena tampoco (`PuedeAtacar = (AdminInvisible = 0)`, SistemaCombate.bas:1645). La señal es que
    /// el golpe no llegue a tirarse: ni el silbido del fallo ni el impacto. Con el GM visible, en la arena sí se tira
    /// (el control).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task En_la_arena_tampoco_se_lo_ataca(bool invisible)
    {
        var (mapa, x, y) = Enumerable.Range(1, 900).Select(m => (short)m)
            .SelectMany(m => Enumerable.Range(10, 80).SelectMany(xx => Enumerable.Range(10, 80).Select(yy => (m, xx, yy))))
            .First(t => _datos.IsArena(t.m, t.xx, t.yy) && _datos.IsArena(t.m, t.xx + 1, t.yy)
                        && !_datos.IsBlockedTile(t.m, t.xx, t.yy) && !_datos.IsBlockedTile(t.m, t.xx + 1, t.yy)
                        && !_datos.IsWater(t.m, t.xx, t.yy) && !_datos.IsWater(t.m, t.xx + 1, t.yy));
        var (p, sesion, suyo) = Conectado("Aldo", new WorldPos(mapa, (byte)x, (byte)y));
        p.CombatMode = true;
        var (gm, sesionGm, _) = Conectado("Rugura", new WorldPos(mapa, (byte)(x + 1), (byte)y), privilegios: 8);
        gm.FactionStatus = ServerIni.StatusDeStaff(8);
        if (invisible) await Invisible(sesionGm);
        suyo.Vaciar();

        await Mandar(sesion, (byte)ClientPacketId.attack);

        Assert.Equal(!invisible, suyo.De(ServerPacketId.PlayWave).Count > 0);
        if (invisible) Assert.Equal(gm.MaxHp, gm.Hp);
    }

    /// <summary>Aunque haya perdido el Invisible por pegarle a una criatura, ninguna le pega mientras siga con el
    /// /INVISIBLE: es la primera guarda de NpcAtacaUser (SistemaCombate.bas:700). Con el fixture, que deja perseguir
    /// a los GMs, porque si no el GM ya estaría a salvo por ser GM.</summary>
    [Fact]
    public async Task Ninguna_criatura_le_pega_aunque_haya_perdido_el_invisible()
    {
        var aca = Fila(pk: true);
        var (gm, sesionGm, _) = Conectado("Rugura", aca, privilegios: 8);
        gm.CombatMode = true;
        _world.AddNpc(new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 1_000_000, Pos = Al(aca, 1), Hostile = true, OriginallyHostile = true,
            Movement = 1, OriginalMovement = 1,
            Template = new NpcInfo(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 4, MaxHp: 1_000_000, MinHit: 50, MaxHit: 50,
                                   Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000) with { Attackable = true },
        });
        await Invisible(sesionGm);
        await Mandar(sesionGm, (byte)ClientPacketId.attack);
        Assert.False(gm.Invisible);                                                 // premisa: el golpe se lo bajó
        gm.Hp = gm.MaxHp;

        var sistema = new NpcSystem(_world, _datos, gmsPerseguibles: true);
        for (int vuelta = 1; vuelta <= 200; vuelta++) sistema.Update(vuelta * NpcSystem.AttackEveryTicks);

        Assert.Equal(gm.MaxHp, gm.Hp);
    }

    /// <summary>Cuando le erra a otro jugador, el silbido lo escucha sólo él (SistemaCombate.bas:1230-1233). Pega
    /// flojo contra alguien que esquiva todo, así que en doce golpes erra seguro alguno.</summary>
    [Fact]
    public async Task Su_golpe_errado_lo_escucha_solo_el()
    {
        var aca = Fila(pk: true);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        gm.FactionStatus = ServerIni.StatusDeStaff(8);
        gm.CombatMode = true;
        var (victima, _, deLaVictima) = Conectado("Aldo", Al(aca, 1));
        victima.MaxHp = victima.Hp = 1_000_000;
        victima.Attributes[2] = 40;                                                 // agilidad
        for (int i = 1; i < victima.Skills.Length; i++) victima.Skills[i] = 100;
        var (_, _, delTestigo) = Conectado("Merlin", Al(aca, 2));
        await Invisible(sesionGm);

        for (int i = 0; i < 12; i++) await Mandar(sesionGm, (byte)ClientPacketId.attack);

        var silbido = ServerMessages.PlayWave(2, gm.Pos.X, gm.Pos.Y);                // SND_SWING, en su baldosa
        Assert.True(suyo.Recibio(silbido), "no erró ninguno de los doce, o no le sonó a él");
        Assert.False(deLaVictima.Recibio(silbido));
        Assert.False(delTestigo.Recibio(silbido));
    }

    /// <summary>Pegarle a una criatura le baja el Invisible y le avisa el 307, como a cualquiera: el original no mira
    /// AdminInvisible ahí (SistemaCombate.bas:898). Pero el /INVISIBLE sigue puesto, así que nadie lo ve igual.</summary>
    [Fact]
    public async Task Pegarle_a_una_criatura_le_avisa_el_307_pero_sigue_sin_verse()
    {
        var aca = Fila(pk: true);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        gm.CombatMode = true;
        _world.AddNpc(new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 1_000_000, Pos = Al(aca, 1), Hostile = true, OriginallyHostile = true,
            Template = new NpcInfo(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 4, MaxHp: 1_000_000, MinHit: 1, MaxHit: 1,
                                   Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0) with { Attackable = true },
        });
        await Invisible(sesionGm);
        suyo.Vaciar();

        await Mandar(sesionGm, (byte)ClientPacketId.attack);

        Assert.True(suyo.Recibio(ServerMessages.LocaleMsg(307)), "no le avisó que dejó de ser invisible");
        Assert.False(gm.Invisible);
        Assert.True(gm.AdminInvisible);
    }

    /// <summary>Lo que dice no sale sobre su cabeza: lo leen sólo los GMs, por consola (Protocol.bas:1417-1421).</summary>
    [Fact]
    public async Task Lo_que_dice_lo_leen_solo_los_gms()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        var (_, _, delOtroGm) = Conectado("Merlin", Al(aca, 1), privilegios: 8);
        var (_, _, delJugador) = Conectado("Aldo", Al(aca, 2));
        await Invisible(sesionGm);
        foreach (var t in new[] { suyo, delOtroGm, delJugador }) t.Vaciar();

        await Mandar(sesionGm, new ByteQueueWriter().WriteByte((byte)ClientPacketId.Talk).WriteAsciiString("hola")
                                   .WriteByte(1).ToArray());

        var linea = ServerMessages.ConsoleMsg("Game Master> [Rugura] hola", 14);
        Assert.True(suyo.Recibio(linea));
        Assert.True(delOtroGm.Recibio(linea));
        Assert.Empty(delJugador.Recibidos);
        Assert.Empty(delOtroGm.De(ServerPacketId.ChatOverHead));
    }

    [Fact]
    public async Task Ocultarse_no_hace_nada()
    {
        var aca = Fila(pk: false);
        var (gm, sesionGm, suyo) = Conectado("Rugura", aca, privilegios: 8);
        gm.Skills[GameLimits.SkillOcultar] = 100;
        await Invisible(sesionGm);
        suyo.Vaciar();

        for (int i = 0; i < 5; i++) await Mandar(sesionGm, (byte)ClientPacketId.Work, (byte)GameLimits.SkillOcultar);

        Assert.False(gm.Oculto);
        Assert.Empty(suyo.Recibidos);
    }
}
