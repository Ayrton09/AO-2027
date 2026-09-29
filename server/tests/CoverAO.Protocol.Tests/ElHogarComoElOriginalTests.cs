using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HOGAR COMO EL ORIGINAL, SIN SUS BUGS (VB6 HandleSeleccionarHogar, Protocol.bas:16266; decisión del autor del
/// 2026-09-23). El hogar es la ciudad del MAPA DONDE ESTÁS: el /HOGAR (modo 0) le pregunta al sacerdote clickeado y
/// aceptar la pregunta (modo 1) fija esa ciudad, si tu bando puede vivir ahí. Acá se elegía de una lista cualquier
/// ciudad desde cualquier templo, y el tester lo reportó. Los bugs que no se portan: el ciudadano común y los
/// republicanos no podían elegir las ciudades de su bando, y en Lindos, Suramei y Nueva Esperanza el "ya es tu hogar"
/// miraba otro mapa.
/// </summary>
public class ElHogarComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_hogar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const byte PreguntarAlSacerdote = 0, Aceptar = 1;
    private const byte FontTypeInfo = 3;            // VB6 FontTypeNames.FONTTYPE_INFO: el oráculo compara la fuente
    private const byte SinHogarElegible = 6;        // el Dungeon Newbie: ningún mapa lo da, así que todo es un cambio

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

    public ElHogarComoElOriginalTests()
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

    private (Player, ClientSession, Transporte) Conectado(WorldPos pos, byte faccion = Player.StatusCiudadano,
                                                         byte hogar = SinHogarElegible)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Merlin", Account = "merlin", Pos = pos, Heading = 3,
            Hp = 100, MaxHp = 100, Level = 20, FactionStatus = faccion, HomeCity = hogar,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private Npc Criatura(WorldPos pos, bool sacerdote = true)
    {
        var npc = new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Hp = 1000, Pos = pos, IsReviver = sacerdote,
            Template = new NpcInfo(Index: 5, Name: "Sacerdote", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 0,
                                   MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        };
        _world.AddNpc(npc);
        return npc;
    }

    private async Task Hogar(ClientSession sesion, byte modo)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.SeleccionarHogar, modo }));
        // SE ESPERA LA VUELTA DEL LOOP, que es quien lo atiende, y no un tiempo fijo: con la máquina cargada, los 300 ms solos no
        // alcanzaron (29/09, `Fuera_de_los_mapas_del_hogar_es_invalida(mapa: 37)`). Después, el rato de la cola de salida de la
        // sesión, que también es asincrónica.
        var atendido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
    }

    private static byte[] Aviso(string texto) => ServerMessages.ConsoleMsg(texto, FontTypeInfo);
    private static readonly byte[] LaPregunta = ServerMessages.ShowMessageBox("0", esPregunta: true, accion: 5);
    private static WorldPos En(short mapa, int x = 50, int y = 50) => new(mapa, (byte)x, (byte)y);

    // ── EL /HOGAR: LA PREGUNTA DEL SACERDOTE ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Al_alcance_del_sacerdote_pregunta_y_no_cambia_nada()
    {
        var (p, sesion, t) = Conectado(En(1));
        p.TargetNpc = Criatura(En(1, 51)).CharIndex;

        await Hogar(sesion, PreguntarAlSacerdote);

        Assert.Equal(new[] { LaPregunta }, t.Recibidos.ToArray());   // WriteShowMessageBox(UserIndex, 0, True, 5), nada más
        Assert.Equal(SinHogarElegible, p.HomeCity);
    }

    [Fact]
    public async Task Sin_nadie_clickeado_pide_que_se_elija_un_personaje()
    {
        var (_, sesion, t) = Conectado(En(1));
        Criatura(En(1, 51));

        await Hogar(sesion, PreguntarAlSacerdote);

        Assert.True(t.Recibio(Aviso("Primero tienes que seleccionar un personaje, haz click izquierdo sobre él.")));
        Assert.Empty(t.De(ServerPacketId.ShowMessageBox));
    }

    [Fact]
    public async Task Con_otra_criatura_clickeada_no_contesta_nada()
    {
        var (p, sesion, t) = Conectado(En(1));
        p.TargetNpc = Criatura(En(1, 51), sacerdote: false).CharIndex;

        await Hogar(sesion, PreguntarAlSacerdote);

        Assert.Empty(t.Recibidos);
    }

    /// <summary>La distancia es la <c>Distancia</c> del original: la suma de los dos lados, y cien por mapa. A tres y
    /// dos baldosas pregunta; a tres y tres ya está lejos, que con la mayor de las dos (o la euclídea) seguiría a tiro.</summary>
    [Fact]
    public async Task La_distancia_al_sacerdote_es_la_del_original()
    {
        var (p, sesion, t) = Conectado(En(1));

        p.TargetNpc = Criatura(En(1, 53, 52)).CharIndex;
        await Hogar(sesion, PreguntarAlSacerdote);
        Assert.True(t.Recibio(LaPregunta), "a cinco baldosas no preguntó");

        foreach (var lejos in new[] { En(1, 53, 53), En(2, 50, 50) })
        {
            t.Vaciar();
            p.TargetNpc = Criatura(lejos).CharIndex;
            await Hogar(sesion, PreguntarAlSacerdote);
            Assert.True(t.Recibio(Aviso("Estás muy lejos del sacerdote.")), $"con el sacerdote en {lejos} no dijo que estaba lejos");
            Assert.Empty(t.De(ServerPacketId.ShowMessageBox));
        }
    }

    // ── ACEPTAR: LA CIUDAD DEL MAPA DONDE ESTÁS ──────────────────────────────────────────────────────────

    /// <summary>Las once ciudades donde se elige, cada una con un bando que puede vivir ahí; el ciudadano común en las del
    /// Imperio y el republicano en las de la República son los que el bug del original dejaba afuera. Sin sacerdote
    /// clickeado: aceptar no lo vuelve a mirar, como allá (la pregunta ya lo pidió).</summary>
    [Theory]
    [InlineData(20, 5, Player.StatusCaos, "Rinkel")]
    [InlineData(151, 8, Player.StatusCriminal, "Arghâl Central")]
    [InlineData(218, 9, Player.StatusRepublicano, "Tiama")]
    [InlineData(180, 10, Player.StatusArmada, "Orac")]
    [InlineData(112, 12, Player.StatusMilicia, "Nueva Esperanza")]
    [InlineData(1, 3, Player.StatusCiudadano, "Ullathorpe")]
    [InlineData(34, 1, Player.StatusArmada, "Nix")]
    [InlineData(59, 4, Player.StatusCiudadano, "Banderbill Central")]
    [InlineData(194, 2, Player.StatusRepublicano, "Illiandor")]
    [InlineData(63, 7, Player.StatusMilicia, "Abadía de Lindos")]
    [InlineData(184, 11, Player.StatusRepublicano, "Suramei")]
    public async Task Aceptar_fija_la_ciudad_del_mapa_donde_esta(short mapa, byte ciudad, byte faccion, string nombre)
    {
        var (p, sesion, t) = Conectado(En(mapa), faccion);

        await Hogar(sesion, Aceptar);

        Assert.Equal(ciudad, p.HomeCity);
        Assert.True(t.Recibio(Aviso($"Tu nuevo hogar ahora es {nombre}.")), $"no confirmó {nombre}");
    }

    [Theory]
    [InlineData(1, Player.StatusRepublicano)]
    [InlineData(34, Player.StatusMilicia)]
    [InlineData(59, Player.StatusCaos)]
    [InlineData(1, Player.StatusCriminal)]
    [InlineData(194, Player.StatusCiudadano)]
    [InlineData(63, Player.StatusArmada)]
    [InlineData(184, Player.StatusCaos)]
    [InlineData(194, Player.StatusCriminal)]
    public async Task La_ciudad_de_otro_bando_es_invalida(short mapa, byte faccion)
    {
        var (p, sesion, t) = Conectado(En(mapa), faccion);

        await Hogar(sesion, Aceptar);

        Assert.Equal(SinHogarElegible, p.HomeCity);
        Assert.Equal(new[] { Aviso("Ciudad inválida.") }, t.Recibidos.ToArray());
    }

    /// <summary>Fuera de los once mapas no hay hogar para ningún bando, aunque tengan sacerdote: el Dungeon Newbie, el
    /// Lindos donde se resucita y el desierto del fixture.</summary>
    [Theory]
    [InlineData(37)]
    [InlineData(64)]
    [InlineData(62)]
    [InlineData(15)]
    public async Task Fuera_de_los_mapas_del_hogar_es_invalida(short mapa)
    {
        for (byte faccion = Player.StatusCriminal; faccion <= Player.StatusMilicia; faccion++)
        {
            var (p, sesion, t) = Conectado(En(mapa), faccion);

            await Hogar(sesion, Aceptar);

            Assert.Equal(SinHogarElegible, p.HomeCity);
            Assert.Equal(new[] { Aviso("Ciudad inválida.") }, t.Recibidos.ToArray());
            _world.Remove(p.CharIndex);
        }
    }

    /// <summary>Si ya es su hogar se lo dice, también en Lindos, Suramei y Nueva Esperanza, donde el original miraba
    /// otro mapa que el de la elección.</summary>
    [Theory]
    [InlineData(1, 3, Player.StatusCiudadano, "Ullathorpe")]
    [InlineData(63, 7, Player.StatusRepublicano, "Abadía de Lindos")]
    [InlineData(184, 11, Player.StatusMilicia, "Suramei")]
    [InlineData(112, 12, Player.StatusCaos, "Nueva Esperanza")]
    public async Task Si_ya_es_su_hogar_se_lo_dice(short mapa, byte ciudad, byte faccion, string nombre)
    {
        var (p, sesion, t) = Conectado(En(mapa), faccion, hogar: ciudad);

        await Hogar(sesion, Aceptar);

        Assert.Equal(ciudad, p.HomeCity);
        Assert.Equal(new[] { Aviso($"{nombre} es tu hogar.") }, t.Recibidos.ToArray());
    }

    /// <summary>El "ya es tu hogar" va antes que el bando, como allá: el renegado que nació en Nix no puede elegirla,
    /// pero parado en Nix se entera de que es la suya.</summary>
    [Fact]
    public async Task El_ya_es_tu_hogar_va_antes_que_el_bando()
    {
        var (p, sesion, t) = Conectado(En(34), Player.StatusCriminal, hogar: 1);

        await Hogar(sesion, Aceptar);

        Assert.Equal(new[] { Aviso("Nix es tu hogar.") }, t.Recibidos.ToArray());
    }

    /// <summary>El byte es un modo y no una ciudad: el cliente viejo mandaba el número de la ciudad elegida en una lista,
    /// y con eso el hogar se cambiaba desde cualquier templo.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(255)]
    public async Task Otro_modo_no_hace_nada(byte modo)
    {
        var (p, sesion, t) = Conectado(En(1));
        p.TargetNpc = Criatura(En(1, 51)).CharIndex;

        await Hogar(sesion, modo);

        Assert.Empty(t.Recibidos);
        Assert.Equal(SinHogarElegible, p.HomeCity);
    }
}
