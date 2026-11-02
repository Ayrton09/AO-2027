using System.Text.RegularExpressions;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL SALTO DE UN JUGADOR ES UNO SOLO. El original tiene un único <c>WarpUserChar</c>
/// (Modulo_UsUaRiOs.bas:1569) para la salida de mapa, el /TELEP, el /IRA, el /IRCERCA, el /SUM, la cárcel, el
/// hogar, la runa, el pasaje y el pergamino a Intermundia. Acá eran tres copias y a cada una le faltaba algo
/// distinto: nadie le avisaba a quien veía las dos puntas del salto, el sonido y el destello estaban sólo en el
/// /IRCERCA, y la gracia contra las criaturas sólo en el /TELEP y la cárcel.
///
/// <para>Se mide sobre lo que le LLEGA a cada conexión, porque es lo único que el cliente dibuja.</para>
/// </summary>
public class SaltoDeUnJugadorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_salto_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly PacketDispatcher _dispatcher;

    private const short Campo = 14;     // Campos Abiertos: inseguro y despejado alrededor de (40,40)
    private const short Otro = 17;      // cualquier otro mapa, para los saltos que cambian de mapa

    private sealed class Grabadora : IConnection
    {
        public readonly List<byte[]> Paquetes = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => 0;
        public void Send(byte[] packet) => Paquetes.Add(packet);
        public void Close(string reason) { }

        public int Donde(byte[] esperado) => Paquetes.FindIndex(p => p.AsSpan().SequenceEqual(esperado));
        public bool Recibio(byte[] esperado) => Donde(esperado) >= 0;
    }

    public SaltoDeUnJugadorTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, new GameLoop(), new Persistence(AlmacenSqlite.EnMemoria(), leveling),
            _datos, leveling);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player, Grabadora) Jugador(string nombre, short mapa, byte x, byte y)
    {
        var grabadora = new Grabadora();
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, y), Hp = 100, MaxHp = 100, Connection = grabadora,
        };
        _world.Add(p);
        return (p, grabadora);
    }

    /// <summary>QUIEN VE LAS DOS PUNTAS lo ve irse y llegar: el borrado y la creación en el lugar nuevo, en ese
    /// orden. Antes no le llegaba nada y seguía dibujándolo donde estaba.</summary>
    [Fact]
    public void Quien_ve_las_dos_puntas_del_salto_lo_ve_llegar()
    {
        var (_, olga) = Jugador("Olga", Campo, 40, 40);
        var (tito, _) = Jugador("Tito", Campo, 42, 40);
        var destino = new WorldPos(Campo, 44, 42);
        Assert.True(World.InView(new WorldPos(Campo, 40, 40), tito.Pos) && World.InView(new WorldPos(Campo, 40, 40), destino),
            "premisa: Olga ya no ve las dos puntas del salto");

        _dispatcher.WarpOtroJugador(tito, destino);

        int borrado = olga.Donde(ServerMessages.CharacterRemove(tito.CharIndex, faded: true));
        int creado = olga.Donde(ServerMessages.CharacterCreate(tito));
        Assert.True(borrado >= 0, "a Olga no le llegó el borrado del que saltó");
        Assert.True(creado > borrado, "a Olga no le llegó la creación en el lugar nuevo, después del borrado");
    }

    /// <summary>EL SALTO SUENA Y DESTELLA para todos los que ven la llegada, el que salta incluido.</summary>
    [Fact]
    public void El_salto_suena_y_destella_en_la_llegada()
    {
        var (_, olga) = Jugador("Olga", Campo, 40, 40);
        var (tito, suyo) = Jugador("Tito", Campo, 42, 40);
        var destino = new WorldPos(Campo, 44, 42);

        _dispatcher.WarpUserChar(tito, destino, fx: true);

        var sonido = ServerMessages.PlayWave(3, destino.X, destino.Y);     // SND_WARP
        var destello = ServerMessages.CreateFX(tito.CharIndex, 1, 0);      // FXWARP
        Assert.True(olga.Recibio(sonido) && olga.Recibio(destello), "Olga no oyó ni vio el salto");
        Assert.True(suyo.Recibio(sonido) && suyo.Recibio(destello), "el que saltó no oyó ni vio su propio salto");
    }

    /// <summary>Y el camino que el original llama sin <c>FX</c> —la salida común de un mapa— salta callado.</summary>
    [Fact]
    public void Sin_fx_el_salto_no_suena()
    {
        var (_, olga) = Jugador("Olga", Campo, 40, 40);
        var (tito, _) = Jugador("Tito", Campo, 42, 40);

        _dispatcher.WarpUserChar(tito, new WorldPos(Campo, 44, 42), fx: false);

        Assert.False(olga.Recibio(ServerMessages.PlayWave(3, 44, 42)), "sonó un salto que el original hace callado");
    }

    /// <summary>UN GM EN INVISIBILIDAD PERMANENTE salta sin anunciarse (<c>If FX And .flags.AdminInvisible = 0</c>).</summary>
    [Fact]
    public void Un_gm_invisible_salta_sin_anunciarse()
    {
        var (_, olga) = Jugador("Olga", Campo, 40, 40);
        var (gm, _) = Jugador("Gema", Campo, 42, 40);
        gm.Privileges = 3;
        gm.Invisible = gm.AdminInvisible = true;   // la del /INVISIBLE

        _dispatcher.WarpUserChar(gm, new WorldPos(Campo, 44, 42), fx: true);

        Assert.False(olga.Recibio(ServerMessages.PlayWave(3, 44, 42)), "el GM invisible anunció su salto");
    }

    /// <summary>TODO SALTO DA LA GRACIA contra las criaturas, no sólo el /TELEP y la cárcel.</summary>
    [Fact]
    public void Todo_salto_da_la_gracia_contra_las_criaturas()
    {
        var (tito, _) = Jugador("Tito", Campo, 42, 40);
        tito.NoAtacablePorNpcHastaMs = 0;

        _dispatcher.WarpUserChar(tito, new WorldPos(Otro, 50, 50), fx: false);

        Assert.True(tito.NoAtacablePorNpcHastaMs > Environment.TickCount64, "el salto no le dio la gracia");
    }

    /// <summary>LA CRIATURA QUE ERA TUYA deja de serlo al saltar (<c>PerdioNpc</c>, :1663).</summary>
    [Fact]
    public void Saltar_suelta_la_criatura_que_era_tuya()
    {
        var (tito, _) = Jugador("Tito", Campo, 42, 40);
        var ficha = new NpcInfo(Index: 900, Name: "Presa", Body: 1, Head: 1, Heading: 3, MaxHp: 50, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 10, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0);
        var presa = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 50,
            Pos = new WorldPos(Campo, 43, 40), ApropiadoPor = tito.CharIndex,
        };
        _world.AddNpc(presa);
        tito.NpcApropiado = presa.CharIndex;

        _dispatcher.WarpUserChar(tito, new WorldPos(Otro, 50, 50), fx: false);

        Assert.Equal(0, tito.NpcApropiado);
        Assert.Equal(0, presa.ApropiadoPor);
    }

    /// <summary>EL MAPA QUE NO ADMITE INVISIBILIDAD se la saca al usuario común que entra invisible, con el aviso
    /// 447. A un GM no.</summary>
    [Fact]
    public void Entrar_invisible_a_un_mapa_sin_invisibilidad_te_hace_visible()
    {
        _datos.SetMapNoInvi(Otro, true);
        var (tito, suyo) = Jugador("Tito", Campo, 42, 40);
        tito.Invisible = true;
        tito.InvisibleUntilTick = 5000;   // por hechizo
        var (gm, _) = Jugador("Gema", Campo, 30, 30);
        gm.Privileges = 3;
        gm.Invisible = true;

        _dispatcher.WarpUserChar(tito, new WorldPos(Otro, 50, 50), fx: false);
        _dispatcher.WarpUserChar(gm, new WorldPos(Otro, 60, 60), fx: false);

        Assert.False(tito.Invisible);
        Assert.True(suyo.Recibio(ServerMessages.LocaleMsg(447)), "no le avisó por qué dejó de ser invisible");
        Assert.True(gm.Invisible, "al GM también le sacó la invisibilidad");
    }

    /// <summary>
    /// Y QUE NO SE DUPLIQUE EL SALTO. Un salto que mueve la posición y refresca el área por su cuenta es
    /// exactamente como nacieron las tres de antes, y cada una se olvidó de algo distinto. El refresco de área
    /// tiene cuatro usos legítimos: el paso, el "casper" empujado por otro jugador, el "casper" empujado por un NPC
    /// y el salto. Los empujones son pasos forzados: ambos deben actualizar lo que el desplazado ve al cruzar un área
    /// (Modulo_UsUaRiOs.bas:765), incluido el sacerdote que necesita un cadáver.
    /// </summary>
    [Fact]
    public void Los_saltos_pasan_todos_por_el_mismo_lugar()
    {
        string net = null!;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "server", "src", "CoverAO.Server", "Net");
            if (Directory.Exists(cand)) { net = cand; break; }
        }
        Assert.NotNull(net);
        var usos = Directory.EnumerateFiles(net, "*.cs")
            .SelectMany(f => File.ReadAllLines(f).Select((linea, i) => (archivo: Path.GetFileName(f), i, linea)))
            .Where(t => Regex.IsMatch(t.linea, @"\bRefreshAreaVisibility\(") && !t.linea.Contains("static void RefreshAreaVisibility"))
            .ToList();
        Assert.True(usos.Count == 4,
            "el refresco de área se llama desde " + usos.Count + " lugares (se esperaban el paso, el casper de jugador, el casper de NPC y "
            + "WarpUserChar): " + string.Join("; ", usos.Select(u => $"{u.archivo}:{u.i + 1}")));
        Assert.Equal(3, usos.Count(u => u.archivo == "PacketDispatcher.cs"));
        Assert.Single(usos, u => u.archivo == "NpcSystem.cs");
    }
}
