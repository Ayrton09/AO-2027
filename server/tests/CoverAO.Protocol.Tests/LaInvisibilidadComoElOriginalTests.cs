using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA INVISIBILIDAD, COMO EL ORIGINAL. El hechizo manda el SetInvisible a `ToPCArea` (modHechizos.bas:1089), que
/// incluye al que lo tira, y al vencer (EfectoInvisibilidad, General.bas:914-917) avisa con el 307 y manda el
/// SetInvisible false al área entera. Acá el propio no recibía ninguno de los dos y el aviso era un texto nuestro.
/// </summary>
public class LaInvisibilidadComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_invisibilidad_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15;
    private const short Invisibilidad = 14;

    /// <summary>Anota cada paquete que el server le manda (llegan de a uno y sin cifrar) y no recibe nada.</summary>
    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public LaInvisibilidadComoElOriginalTests()
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

    private (Player Jugador, ClientSession Sesion, Transporte Transporte) Conectado(string nombre, byte x)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(Desierto, x, 50), Heading = 2, Hp = 100, MaxHp = 100, Level = 30,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, Class = 2, CombatMode = true,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = Invisibilidad;
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, transporte);
    }

    /// <summary>Los SetInvisible de ese personaje que llegaron, con su valor, en orden.</summary>
    private static List<bool> SetInvisibleDe(Transporte t, Player p) => t.Recibidos
        .Where(x => x.Length >= 4 && x[0] == (byte)ServerPacketId.SetInvisible && BitConverter.ToInt16(x, 1) == p.CharIndex)
        .Select(x => x[3] != 0).ToList();

    private static int IndiceDe(Transporte t, Func<byte[], bool> cual) => t.Recibidos.ToList().FindIndex(x => cual(x));

    private static bool EsElLocale(byte[] x, short id) =>
        x.Length >= 3 && x[0] == (byte)ServerPacketId.LocaleMsg && BitConverter.ToInt16(x, 1) == id;

    [Fact]
    public async Task El_que_lanza_la_invisibilidad_recibe_su_propio_SetInvisible()
    {
        Assert.True(_datos.Spell(Invisibilidad)!.EsAutoLanzado);                  // premisa: se lanza sin clic
        var (mago, sesion, suyo) = Conectado("Aldo", 50);
        var (_, _, delOtro) = Conectado("Bruno", 52);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(300);
        Assert.True(mago.Invisible, "premisa: quedó invisible");
        Assert.Equal(new[] { true }, SetInvisibleDe(suyo, mago));
        Assert.Equal(new[] { true }, SetInvisibleDe(delOtro, mago));
    }

    [Fact]
    public void Al_vencer_avisa_con_el_307_y_despues_lo_ven_todos_incluido_el_propio()
    {
        var (jugador, _, suyo) = Conectado("Aldo", 50);
        var (_, _, delOtro) = Conectado("Bruno", 52);
        jugador.Invisible = true;
        jugador.InvisibleUntilTick = 1000;
        _dispatcher.VencerInvisibilidad(jugador, 999);
        Assert.True(jugador.Invisible, "premisa: todavía no venció");

        _dispatcher.VencerInvisibilidad(jugador, 1000);
        Assert.False(jugador.Invisible);
        Assert.Equal(new[] { false }, SetInvisibleDe(suyo, jugador));
        Assert.Equal(new[] { false }, SetInvisibleDe(delOtro, jugador));
        int aviso = IndiceDe(suyo, x => EsElLocale(x, 307));
        int visible = IndiceDe(suyo, x => x[0] == (byte)ServerPacketId.SetInvisible);
        Assert.True(aviso >= 0 && aviso < visible, "el 307 va antes del SetInvisible");
        Assert.DoesNotContain(suyo.Recibidos, x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg);
    }

    [Fact]
    public void Al_vencer_el_que_sigue_oculto_no_se_destapa()
    {
        var (jugador, _, suyo) = Conectado("Aldo", 50);
        var (_, _, delOtro) = Conectado("Bruno", 52);
        jugador.Invisible = true;
        jugador.InvisibleUntilTick = 1000;
        jugador.Oculto = true;
        jugador.OcultoUntilTick = long.MaxValue;
        _dispatcher.VencerInvisibilidad(jugador, 1000);
        Assert.False(jugador.Invisible);
        Assert.Empty(SetInvisibleDe(suyo, jugador));
        Assert.Empty(SetInvisibleDe(delOtro, jugador));
        Assert.DoesNotContain(suyo.Recibidos, x => EsElLocale(x, 307));
    }

    [Fact]
    public void La_invisibilidad_del_GM_no_vence()
    {
        var (gm, _, suyo) = Conectado("Aldo", 50);
        gm.Invisible = true;
        gm.InvisibleUntilTick = 0;                                                // el /INVISIBLE no tiene reloj
        _dispatcher.VencerInvisibilidad(gm, long.MaxValue);
        Assert.True(gm.Invisible);
        Assert.Empty(suyo.Recibidos);
    }
}
