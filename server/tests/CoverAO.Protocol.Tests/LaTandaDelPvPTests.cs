using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL GOLPE Y EL TIRO A OTRO JUGADOR SON LA MISMA RUTINA, como en el original (<c>UsuarioAtacaUsuario</c>,
/// SistemaCombate.bas:1172), y el daño es el de <c>UserDañoUser</c> entero (:1263). Salió de la auditoría de las armas
/// especiales (2026-09-27): el tiro a un jugador tenía un camino propio que se salteaba el escudo de la víctima —la evasión
/// y el rechazo— y el desarme; el aviso del desarme sólo salía si había arma; la cifra del golpe le llegaba al que pega
/// sobre la víctima y no sobre él; y apuñalar iba con el daño de antes de la armadura y sin mirar el hambre.
/// </summary>
public class LaTandaDelPvPTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_tanda_pvp_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly List<(CancellationTokenSource Cts, Task Corriendo)> _loops = new();

    private const short Mapa = 15;                     // mapa inseguro: se pelea
    private const byte X = 50, Y = 50;
    private const short ArcoSimple = 989, Flecha = 480, Daga = 15, EscudoDeTortuga = 404;
    private const byte Renegado = 1;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Enviados = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Enviados.Enqueue(data);
        public void Close(string reason) { }
    }

    private sealed class Conexion : IConnection
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Enqueue(packet);
        public void Close(string reason) { }
    }

    public void Dispose()
    {
        foreach (var (cts, corriendo) in _loops)
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    /// <summary>Dos renegados pegados en el 15: el que pega en (50,50) mirando al este y la víctima en (51,50), con una
    /// conexión que anota lo que le llega. Sin intervalos, para pegar y tirar seguido.</summary>
    private (PacketDispatcher D, ClientSession S, Player Yo, Player El, Conexion AlOtro, Transporte AMi) Pelea(byte clase)
    {
        string carpeta = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var world = new World();
        var loop = new GameLoop();
        var leveling = new Leveling(_datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(new FileStore(carpeta), leveling), _datos, leveling,
            serverIni: ServerIni.Desde(IniFile.Parse(
                "[INIT]\r\n[INTERVALOS]\r\nIntervaloFlechasCazadores=0\r\nIntervaloUserPuedeAtacar=0\r\n")));
        var cts = new CancellationTokenSource();
        _loops.Add((cts, loop.RunAsync(cts.Token)));
        var yo = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Mapa, X, Y), Heading = 2,
            Hp = 1000, MaxHp = 1000, Level = 50, Stamina = 50_000, MaxStamina = 50_000, Class = clase,
            FactionStatus = Renegado, CombatMode = true, Hunger = 100, Thirst = 100,
        };
        var el = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Bruno", Account = "bruno", Pos = new WorldPos(Mapa, X + 1, Y), Heading = 4,
            Hp = 30_000, MaxHp = 30_000, Level = 10, Stamina = 500, MaxStamina = 500, Class = 1,
            FactionStatus = Renegado, Hunger = 100, Thirst = 100,
        };
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, dispatcher);
        sesion.Player = yo;
        yo.Connection = sesion;
        var conexion = new Conexion();
        el.Connection = conexion;
        world.Add(yo);
        world.Add(el);
        return (dispatcher, sesion, yo, el, conexion, transporte);
    }

    private static async Task Pegar(PacketDispatcher d, ClientSession s, int veces)
    {
        for (int i = 0; i < veces; i++)
        {
            d.HandleOne(s, new ByteQueueReader(new[] { (byte)ClientPacketId.attack }));
            await Task.Delay(15);
        }
        await Task.Delay(300);
    }

    private static async Task Tirar(PacketDispatcher d, ClientSession s, int veces)
    {
        for (int i = 0; i < veces; i++)
        {
            d.HandleOne(s, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)(X + 1), Y, (byte)GameLimits.SkillProyectiles }));
            await Task.Delay(15);
        }
        await Task.Delay(300);
    }

    /// <summary>Los textos de consola de los paquetes, como el cliente los leería (el ASCII del texto alcanza para buscarlos).</summary>
    private static string Textos(IEnumerable<byte[]> paquetes) =>
        string.Join("|", paquetes.Where((p) => p.Length > 3 && p[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select((p) => Encoding.Latin1.GetString(p, 3, BitConverter.ToInt16(p, 1))));

    private void Armar(Player p, short arco = 0, short flechas = 0, short arma = 0, short escudo = 0)
    {
        byte ranura = 1;
        if (arco > 0) p.Inventory[ranura++] = new InventorySlot { ObjIndex = arco, Amount = 1, Equipped = true };
        if (flechas > 0) p.Inventory[ranura++] = new InventorySlot { ObjIndex = Flecha, Amount = flechas, Equipped = true };
        if (arma > 0) p.Inventory[ranura++] = new InventorySlot { ObjIndex = arma, Amount = 1, Equipped = true };
        if (escudo > 0) p.Inventory[ranura++] = new InventorySlot { ObjIndex = escudo, Amount = 1, Equipped = true };
    }

    [Fact]
    public async Task El_tiro_a_un_jugador_lo_puede_rechazar_su_escudo()
    {
        var (d, s, yo, el, alOtro, _) = Pelea(clase: 1);
        Armar(yo, arco: ArcoSimple, flechas: 500);
        yo.Skills[GameLimits.SkillProyectiles] = 0;          // un tirador malo: erra mucho
        Armar(el, escudo: EscudoDeTortuga);
        el.Level = 50; el.Attributes[2] = 40;                  // agilidad
        el.Skills[GameLimits.SkillTacticas] = 1;
        el.Skills[GameLimits.SkillDefensa] = 100;             // rechaza el 90% de lo que falla
        await Tirar(d, s, 80);
        Assert.Contains("rechazado el ataque con el escudo", Textos(alOtro.Recibidos));
    }

    [Fact]
    public async Task El_tiro_de_un_ladron_desarma()
    {
        var (d, s, yo, el, _, _) = Pelea(clase: 5);           // ladrón
        Armar(yo, arco: ArcoSimple, flechas: 500);
        yo.Skills[GameLimits.SkillProyectiles] = 100;
        yo.Skills[GameLimits.SkillWrestling] = 100;           // 100 * 0,2 + 50 * 0,66 = 53%
        Armar(el, arma: Daga);
        for (int tanda = 0; tanda < 6 && el.Inventory[1].Equipped; tanda++) await Tirar(d, s, 20);
        Assert.False(el.Inventory[1].Equipped, "tirándole un ladrón tendría que haberle sacado la daga");
        Assert.Equal(Daga, el.Inventory[1].ObjIndex);          // se la saca, no se la lleva
    }

    [Fact]
    public async Task El_aviso_del_desarme_llega_aunque_no_tenga_arma()
    {
        var (d, s, yo, el, alOtro, _) = Pelea(clase: 5);
        yo.Skills[GameLimits.SkillWrestling] = 100;
        await Pegar(d, s, 60);                                 // a mano: el golpe entra casi siempre contra nivel 10
        Assert.Contains("te ha desarmado", Textos(alOtro.Recibidos));
    }

    [Fact]
    public async Task La_cifra_del_golpe_le_llega_al_que_pega_sobre_su_propio_personaje()
    {
        var (d, s, yo, el, _, aMi) = Pelea(clase: 1);
        yo.Skills[GameLimits.SkillWrestling] = 100;
        await Pegar(d, s, 20);
        var cifras = aMi.Enviados.Where((p) => p.Length == 8 && p[0] == (byte)ServerPacketId.ChatOverHeadLocale && p[7] == 2).ToList();
        Assert.NotEmpty(cifras);
        Assert.All(cifras, (p) => Assert.Equal(yo.CharIndex, BitConverter.ToInt16(p, 1)));
        Assert.True(el.Hp < el.MaxHp, "premisa: algún golpe tuvo que entrar");
    }

    [Fact]
    public async Task Con_hambre_no_se_apunala()
    {
        var (d, s, yo, el, alOtro, _) = Pelea(clase: 4);      // asesino: apuñala siempre que puede
        Armar(yo, arma: Daga);
        yo.Skills[GameLimits.SkillArmas] = 100;
        yo.Skills[GameLimits.SkillApunalar] = 100;
        yo.Hunger = 0;
        await Pegar(d, s, 60);
        Assert.True(el.Hp < el.MaxHp, "premisa: los golpes entran");
        Assert.DoesNotContain("apuñalado", Textos(alOtro.Recibidos));

        yo.Hunger = 100;                                       // y comido, sí
        await Pegar(d, s, 60);
        Assert.Contains("apuñalado", Textos(alOtro.Recibidos));
    }

    /// <summary>USAR UNA ARROJADIZA pide objetivo, como el arco (InvUsuario.bas:1488-1496): con la skill Armas Arrojadizas,
    /// y los mismos gates, equipada (391) y modo combate (102). Acá caía en la rama del cuerpo a cuerpo y contestaba el 422
    /// de la leña.</summary>
    [Fact]
    public async Task Usar_una_arrojadiza_pide_objetivo_como_el_arco()
    {
        const short Lanza = 656;
        var (d, s, yo, _, _, aMi) = Pelea(clase: 1);
        byte[] Usar() => new[] { (byte)ClientPacketId.UseItem, (byte)1 };
        bool Pidio() => aMi.Enviados.Any((p) => p.Length == 2 && p[0] == (byte)ServerPacketId.WorkRequestTarget
                                                 && p[1] == GameLimits.SkillArmasArrojadizas);
        bool Dijo(short id) => aMi.Enviados.Any((p) => p.Length > 2 && p[0] == (byte)ServerPacketId.LocaleMsg && BitConverter.ToInt16(p, 1) == id);

        yo.Inventory[1] = new InventorySlot { ObjIndex = Lanza, Amount = 1, Equipped = false };
        d.HandleOne(s, new ByteQueueReader(Usar()));
        await Task.Delay(300);
        Assert.True(Dijo(391), "sin equipar: el 391");
        Assert.False(Pidio());

        yo.Inventory[1] = new InventorySlot { ObjIndex = Lanza, Amount = 1, Equipped = true };
        yo.CombatMode = false;
        d.HandleOne(s, new ByteQueueReader(Usar()));
        await Task.Delay(300);
        Assert.True(Dijo(102), "sin modo combate: el 102");
        Assert.False(Pidio());

        yo.CombatMode = true;
        d.HandleOne(s, new ByteQueueReader(Usar()));
        await Task.Delay(300);
        Assert.True(Pidio(), "equipada y en combate: pide objetivo con Armas Arrojadizas");
        Assert.False(Dijo(422), "nada de la leña");
    }

    [Fact]
    public void La_defensa_del_barco_y_la_montura_cubre_solo_donde_hay_pieza()
    {
        static int Justo(int lo, int hi) => lo;
        // En el cuerpo, sin armadura: el barco no cubre nada.
        Assert.Equal(100, Combat.AplicarDefensa(100, zona: 3, casco: null, armadura: null, escudo: null, Justo, defensaConPieza: 40));
        // En el cuerpo, con armadura: se suma a lo que absorbe la armadura.
        Assert.Equal(50, Combat.AplicarDefensa(100, zona: 3, casco: null, armadura: (10, 10), escudo: null, Justo, defensaConPieza: 40));
        // En la cabeza con casco, igual; sin casco, nada.
        Assert.Equal(50, Combat.AplicarDefensa(100, zona: 1, casco: (10, 10), armadura: (99, 99), escudo: null, Justo, defensaConPieza: 40));
        Assert.Equal(100, Combat.AplicarDefensa(100, zona: 1, casco: null, armadura: (99, 99), escudo: null, Justo, defensaConPieza: 40));
        // Y lo que se pasa de largo queda en 1, como siempre.
        Assert.Equal(1, Combat.AplicarDefensa(30, zona: 3, casco: null, armadura: (10, 10), escudo: null, Justo, defensaConPieza: 40));
    }
}
