using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HECHIZO QUE MATA A UN JUGADOR, EN EL ORDEN DEL ORIGINAL (HechizoEstadoUsuario, modHechizos.bas:367-384). La
/// muerte va adentro del daño: el que lo lanzó lee que lo mató y el otro que murió ANTES de las palabras mágicas, los
/// avisos 497/498 y el cobro del maná, que salen después. Acá la muerte salía al final de todo.
/// </summary>
public class LaMuerteDelHechizoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_muerte_hechizo_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15, MisilMagico = 92;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public int Indice(Func<byte[], bool> cual) => Recibidos.ToList().FindIndex(x => cual(x));
        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    public LaMuerteDelHechizoTests()
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

    private (Player, ClientSession, Transporte) Conectado(string nombre, byte x, byte faccion)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(Desierto, x, 50), Heading = 2, Hp = 100, MaxHp = 100, Level = 30, FactionStatus = faccion,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    /// <summary>Aldo (mago, nivel 30) le tira un Misil Mágico a Bruno, que tiene 1 de vida.</summary>
    private async Task<(Player Mago, Player Victima, Transporte DelMago, Transporte DeLaVictima)> Matar(byte nivelDeLaVictima)
    {
        var (mago, sesion, delMago) = Conectado("Aldo", 50, Player.StatusCiudadano);
        mago.Class = 2; mago.CombatMode = true;
        mago.Mana = 5000; mago.MaxMana = 5000; mago.Stamina = 5000; mago.MaxStamina = 5000;
        mago.Skills[GameLimits.SkillMagia] = 100;
        mago.Spells[1] = MisilMagico;
        var (victima, _, deLaVictima) = Conectado("Bruno", 52, Player.StatusCriminal);
        victima.Hp = 1;
        victima.Level = nivelDeLaVictima;

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, victima.Pos.X, (byte)(victima.Pos.Y - 1), (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
        return (mago, victima, delMago, deLaVictima);
    }

    /// <summary>VB6 ActStats (Modulo_UsUaRiOs.bas:2353): "¡Has matado a X!", al muerto "¡Y te ha matado!", y "Has ganado
    /// N puntos de experiencia." con N = nivel del muerto × 2.</summary>
    [Fact]
    public async Task Matar_avisa_a_los_dos_y_da_la_experiencia_como_el_original()
    {
        var (mago, victima, delMago, deLaVictima) = await Matar(nivelDeLaVictima: 30);

        Assert.Equal(0, victima.Hp);
        Assert.Equal(["¡Has matado a Bruno!", "Has ganado 60 puntos de experiencia."],
            delMago.Textos().Where(m => m.StartsWith("¡Has matado") || m.StartsWith("Has ganado")).ToList());
        Assert.Contains("¡Aldo te ha matado!", deLaVictima.Textos());
        Assert.Equal(60, mago.Exp);
    }

    /// <summary>Si el que mata le lleva más de 10 niveles al muerto, ActStats sale en su primera línea: ni avisos ni
    /// experiencia. Sólo queda el LocaleMsg 330 de ContarMuerte.</summary>
    [Fact]
    public async Task Con_mas_de_diez_niveles_de_diferencia_no_hay_avisos_ni_experiencia()
    {
        var (mago, victima, delMago, deLaVictima) = await Matar(nivelDeLaVictima: 19);

        Assert.Equal(0, victima.Hp);
        Assert.True(delMago.Indice(x => EsLocale(x, 330)) >= 0);
        Assert.DoesNotContain(delMago.Textos(), m => m.Contains("Has matado") || m.Contains("Has ganado"));
        Assert.DoesNotContain(deLaVictima.Textos(), m => m.Contains("te ha matado"));
        Assert.Equal(0, mago.Exp);
    }

    private static bool EsLocale(byte[] x, short id) =>
        x.Length > 2 && x[0] == (byte)ServerPacketId.LocaleMsg && BitConverter.ToInt16(x, 1) == id;

    [Fact]
    public async Task Si_el_hechizo_mata_la_muerte_sale_antes_que_los_avisos_y_el_cobro()
    {
        var (mago, sesion, delMago) = Conectado("Aldo", 50, Player.StatusCiudadano);
        mago.Class = 2; mago.CombatMode = true;
        mago.Mana = 5000; mago.MaxMana = 5000; mago.Stamina = 5000; mago.MaxStamina = 5000;
        mago.Skills[GameLimits.SkillMagia] = 100;
        mago.Spells[1] = MisilMagico;
        var (victima, _, deLaVictima) = Conectado("Bruno", 52, Player.StatusCriminal);
        victima.Hp = 1;

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, victima.Pos.X, (byte)(victima.Pos.Y - 1), (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);

        Assert.Equal(0, victima.Hp);
        var loMato = ServerMessages.ConsoleMsg("¡Has matado a Bruno!", 3);   // VB6 ActStats, FONTTYPE_INFO
        int muerte = delMago.Indice(x => x.SequenceEqual(loMato));
        int aviso = delMago.Indice(x => EsLocale(x, 497));
        int cobro = delMago.Indice(x => x.Length > 0 && x[0] == (byte)ServerPacketId.UpdateMana);
        Assert.True(muerte >= 0 && aviso >= 0 && cobro >= 0, $"muerte={muerte} aviso={aviso} cobro={cobro}");
        Assert.True(muerte < aviso, "el que lanzó leyó el 497 antes de enterarse de que lo mató");
        Assert.True(muerte < cobro, "el maná se cobró antes de la muerte");

        var murio = ServerMessages.ConsoleMsg("¡Aldo te ha matado!", 3);
        int suMuerte = deLaVictima.Indice(x => x.SequenceEqual(murio));
        int suAviso = deLaVictima.Indice(x => EsLocale(x, 498));
        Assert.True(suMuerte >= 0 && suAviso >= 0 && suMuerte < suAviso, $"muerte={suMuerte} aviso={suAviso}");
    }
}
