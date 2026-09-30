using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS AVISOS DE LAS FAENAS, COMO EL ORIGINAL. Botánica tiene sus textos propios ("¡Has obtenido raíces!(N)",
/// "¡No has obtenido raices!"), el aviso de fallo de cada oficio sale UNA vez por racha (<c>flags.UltimoMensaje</c>,
/// Trabajo.bas:1369/1894/1948/2020), el piquete mira la distancia antes que el yacimiento (Acciones.bas:49-67), cada
/// oficio avisa "Comienzas a trabajar..." con su color, y el clic de oficio sin la herramienta no dice nada.
/// </summary>
public class LasFaenasComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_faenas_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;              // Desierto Rinkel: Pk y campo, se puede talar
    private const short Hacha = 127, Tijeras = 885, Piquete = 187;
    private const byte X = 50, Y = 50;

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
        public int Locales(short id) => Recibidos.Count(x => x.SequenceEqual(ServerMessages.LocaleMsg(id)));
    }

    public LasFaenasComoElOriginalTests()
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

    private (Player, ClientSession, Transporte) Trabajador(short herramienta, int skill, int valor)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Mapa, X, Y), Heading = 2, Hp = 100, MaxHp = 100, Level = 1,
            Stamina = 100_000, MaxStamina = 100_000,
        };
        p.Skills[skill] = (byte)valor;
        p.Inventory[1] = new InventorySlot { ObjIndex = herramienta, Amount = 1, Equipped = true };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private void Plantar(byte x, byte y, Func<ObjInfo, bool> cual)
    {
        var obj = _datos.Objects.Values.First(cual);
        _world.SetGround(new WorldPos(Mapa, x, y), (short)obj.Index, 1, esFijo: true);
    }

    /// <summary>Manda el clic y espera a que el loop lo atienda: una marca encolada DETRÁS del trabajo del clic sale
    /// recién cuando ése terminó. Un tiempo fijo no alcanza con toda la suite corriendo en paralelo.</summary>
    private async Task Clickear(ClientSession s, byte x, byte y, int skill)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, y, (byte)skill }));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>La sesión despacha los paquetes por su propia cola: se lee recién cuando dejan de llegar.</summary>
    private static async Task Asentar(Transporte t)
    {
        int antes = -1;
        for (int i = 0; i < 50 && t.Recibidos.Count != antes; i++) { antes = t.Recibidos.Count; await Task.Delay(150); }
    }

    /// <summary>Vueltas de trabajo con relojes muy por ENCIMA del real: si el loop también atiende la faena, deja
    /// la última vuelta en su reloj, y una vuelta con un reloj más chico se descartaría por el intervalo.</summary>
    private void Vueltas(ClientSession s, Player p, int cuantas)
    {
        long t0 = Environment.TickCount64 + 100_000_000;
        for (int i = 0; i < cuantas; i++) _dispatcher.TickTrabajo(s, p, t0 + i * 10_000L);
    }

    [Fact]
    public async Task Botanica_acierta_con_su_texto()
    {
        var (p, s, t) = Trabajador(Tijeras, GameLimits.SkillBotanica, 100);   // con 100 la tirada no falla
        Plantar(X + 1, Y, o => o.IsTree);
        await Clickear(s, X + 1, Y, GameLimits.SkillBotanica);
        Vueltas(s, p, 1);
        await HastaQueLlegue(t, m => m.StartsWith("¡Has obtenido raíces!("));

        Assert.Contains(t.Textos(), m => m.StartsWith("¡Has obtenido raíces!("));
    }

    /// <summary>
    /// Espera a que llegue ESE texto, o cinco segundos. <see cref="Asentar"/> para cuando pasan 150 ms sin paquetes nuevos, y con
    /// la máquina cargada el escritor de la sesión puede no haber mandado nada todavía: medido el 30/09, este test falló una vez
    /// con la suite y cuatro servidores de bots andando, y pasó cinco de cinco solo.
    /// </summary>
    private static async Task HastaQueLlegue(Transporte t, Func<string, bool> cual)
    {
        for (int i = 0; i < 100 && !t.Textos().Any(cual); i++) await Task.Delay(50);
    }

    [Fact]
    public async Task Botanica_falla_con_su_texto_una_sola_vez_y_sube_la_skill_igual()
    {
        var (p, s, t) = Trabajador(Tijeras, GameLimits.SkillBotanica, 0);     // con 0 falla casi siempre
        Plantar(X + 1, Y, o => o.IsTree);
        await Clickear(s, X + 1, Y, GameLimits.SkillBotanica);
        Vueltas(s, p, 40);
        await Asentar(t);

        var textos = t.Textos();
        Assert.Equal(1, textos.Count(m => m == "¡No has obtenido raices!"));
        Assert.DoesNotContain(textos, m => m.Contains("No has conseguido nada"));
        Assert.True(p.Trabajando >= 40, $"botánica suma el crédito también al fallar ({p.Trabajando})");
    }

    [Fact]
    public async Task Talar_avisa_el_fallo_una_sola_vez_por_racha()
    {
        var (p, s, t) = Trabajador(Hacha, GameLimits.SkillTalar, 0);
        Plantar(X + 1, Y, o => o.IsTree);
        await Clickear(s, X + 1, Y, GameLimits.SkillTalar);
        Vueltas(s, p, 40);
        await Asentar(t);

        Assert.Equal(1, t.Locales(61));   // ¡No has obtenido leña!
    }

    [Fact]
    public async Task El_piquete_mira_la_distancia_antes_que_el_yacimiento()
    {
        var (_, s, t) = Trabajador(Piquete, GameLimits.SkillMineria, 100);
        Plantar(X + 2, Y + 1, o => o.IsTree);   // algo que no es yacimiento, a 3
        await Clickear(s, X + 2, Y + 1, GameLimits.SkillMineria);
        await Asentar(t);

        var textos = t.Textos();
        Assert.Contains("Estás demasiado lejos.", textos);
        Assert.DoesNotContain(textos, m => m.StartsWith("Ahí no hay"));
    }

    [Fact]
    public async Task El_piquete_sobre_una_baldosa_vacia_avisa_sin_tilde()
    {
        var (_, s, t) = Trabajador(Piquete, GameLimits.SkillMineria, 100);
        await Clickear(s, X + 1, Y, GameLimits.SkillMineria);
        await Asentar(t);

        Assert.Contains("Ahí no hay ningun yacimiento.", t.Textos());
    }

    [Fact]
    public async Task Las_tijeras_avisan_con_su_color()
    {
        var (_, s, t) = Trabajador(Tijeras, GameLimits.SkillBotanica, 100);
        Plantar(X + 1, Y, o => o.IsTree);
        await Clickear(s, X + 1, Y, GameLimits.SkillBotanica);
        await Asentar(t);

        Assert.True(t.Recibio(ServerMessages.ConsoleMsg("Comienzas a trabajar...", 4)));
    }

    [Fact]
    public async Task El_clic_de_oficio_sin_su_herramienta_no_dice_nada()
    {
        var (p, s, t) = Trabajador(Hacha, GameLimits.SkillPesca, 100);
        await Clickear(s, X + 1, Y, GameLimits.SkillPesca);
        await Asentar(t);

        Assert.Empty(t.Textos());
        Assert.False(p.Laborando);
    }
}
