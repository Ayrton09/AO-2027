using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS TASAS DEL SERVER, MIRADAS EN EL PAGO y no en la lectura del ini. <c>Exp</c> y <c>Oro</c> son un
/// multiplicador directo, como en el original (<c>.GiveEXP = GiveEXP * Expc</c> al cargar la criatura,
/// MODULO_NPCs.bas:996 y :1004), y el EVENTO del GM es otra perilla que multiplica encima.
///
/// <para>Se prueban los tres pagos por separado porque son tres caminos: el GOTEO de cada golpe (donde el
/// original aplica el evento y el medio extra del donador, SistemaCombate.bas:2025-2030), el RESTO que cobra el
/// que remata (pelado, MODULO_NPCs.bas:128-142) y el ORO. Los números esperados salen de la presa y de la tasa,
/// no de copiar lo que el producto calcula.</para>
/// </summary>
public class TasasDelServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_tasas_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly AdminConfig _admin = new();
    private NpcSystem _npcs = null!;
    private PacketDispatcher _dispatcher = null!;

    private const short Campo = 14;             // Campos Abiertos: inseguro y despejado alrededor de (40,40)
    private const long ExpDeLaPresa = 500;
    private const int OroDeLaPresa = 40;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private void Armar(int exp, int oro, GameLoop? loop = null, string otraClave = "")
    {
        Directory.CreateDirectory(_root);
        string ruta = Path.Combine(_root, "Server.ini");
        File.WriteAllText(ruta, $"[INIT]\r\nStartPort=7666\r\nExp={exp}\r\nOro={oro}\r\n{otraClave}\r\n");
        var leveling = new Leveling(_datos.LevelExp);
        _npcs = new NpcSystem(_world, _datos);
        _dispatcher = new PacketDispatcher(_world, loop ?? new GameLoop(), new Persistence(new FileStore(_root), leveling),
            _datos, leveling, admin: _admin, npcs: _npcs, serverIni: ServerIni.Cargar(ruta));
    }

    /// <summary>Un jugador conectado que no sube de nivel con lo que va a cobrar: así el delta de su
    /// experiencia es exactamente lo que se le pagó.</summary>
    private (Player Jugador, ClientSession Sesion) Conectado(byte x, byte heading)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Campo, x, 40), Heading = heading, Hp = 100, MaxHp = 100, Level = 1,
            Elu = long.MaxValue, CombatMode = true, Stamina = 100_000, MaxStamina = 100_000,
        };
        // Con las skills al tope el golpe no entrena nada: subir una skill da su propia experiencia
        // (`SkillProgression.ExpPerSkillUp`, sin tasa, como en el original) y se mezclaría con el pago.
        for (int i = 0; i < p.Skills.Length; i++) p.Skills[i] = 100;
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    /// <summary>Una criatura quieta de UN punto de vida: cualquier golpe la mata.</summary>
    private Npc Presa(byte x, IReadOnlyList<NpcDrop>? botin = null)
    {
        var ficha = new NpcInfo(Index: 900, Name: "Presa", Body: 1, Head: 1, Heading: 3, MaxHp: 1, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: ExpDeLaPresa, GiveGld: OroDeLaPresa, PoderEvasion: 0, PoderAtaque: 0,
            Drops: botin, Hostile: true, Attackable: true, Movement: 1);
        _npcs.AddSpawn(ficha, new WorldPos(Campo, x, 40), hostile: true, movement: 1);
        return _world.Npcs.Single(n => n.Template.Name == "Presa");
    }

    /// <summary>La mascota del amo, pegada a la presa y con ella de blanco. Lo que mata una mascota lo cobra
    /// su amo sin goteo previo, así que el pozo ENTERO llega por el camino del resto.</summary>
    private void MascotaContra(Player amo, Npc presa)
    {
        var ficha = new NpcInfo(Index: 901, Name: "Mascota", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 5,
            MaxHit: 5, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 1000);
        _world.AddNpc(new Npc
        {
            CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 100,
            Pos = new WorldPos(Campo, (byte)(presa.Pos.X - 1), 40), OwnerCharIndex = amo.CharIndex,
            Movement = GameLimits.TipoAiNpcAtacaNpc, TargetNpcCharIndex = presa.CharIndex,
        });
    }

    private void GirarHasta(Func<bool> listo, long vueltas)
    {
        for (long t = 1; t <= vueltas && !listo(); t++) _npcs.Update(t);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    [InlineData(200, 200)]   // la del install
    public void El_resto_y_el_oro_salen_por_la_tasa(int exp, int oro)
    {
        Armar(exp, oro);
        var (amo, _) = Conectado(38, heading: 2);
        var presa = Presa(42);
        MascotaContra(amo, presa);
        double expAntes = amo.Exp;
        int oroAntes = amo.Gold;

        GirarHasta(() => _world.NpcByIndex(presa.CharIndex) is null, 3000);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        Assert.Equal(ExpDeLaPresa * exp, (long)(amo.Exp - expAntes));
        Assert.Equal((long)OroDeLaPresa * oro, amo.Gold - oroAntes);
    }

    /// <summary>El resto va PELADO: ni el evento ni el donador, que en el original son del goteo por golpe. El
    /// oro sí lleva su evento — ahí el port se aparta a propósito, porque el <c>multiplicadorOro</c> del VB6 se
    /// escribe y nadie lo lee (ver <c>GmEvento</c>).</summary>
    [Fact]
    public void El_resto_no_lleva_el_evento_ni_el_bonus_del_donador()
    {
        Armar(exp: 5, oro: 3);
        _admin.ExpMult = 2;
        _admin.GoldMult = 2;
        var (amo, _) = Conectado(38, heading: 2);
        amo.Donador = true;
        var presa = Presa(42);
        MascotaContra(amo, presa);
        double expAntes = amo.Exp;
        int oroAntes = amo.Gold;

        GirarHasta(() => _world.NpcByIndex(presa.CharIndex) is null, 3000);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        Assert.Equal(ExpDeLaPresa * 5, (long)(amo.Exp - expAntes));
        Assert.Equal((long)OroDeLaPresa * 3 * 2, amo.Gold - oroAntes);
    }

    /// <summary>CON <c>EventoAlRematar=1</c> (regla de la casa) el resto cobra como el goteo: el evento y el medio extra del
    /// donador encima de la tasa. Es lo que le devuelve el evento al que mata de un hechizo, que no gotea.</summary>
    [Fact]
    public void Con_EventoAlRematar_el_resto_lleva_el_evento_y_el_bonus_del_donador()
    {
        Armar(exp: 5, oro: 3, otraClave: "EventoAlRematar=1");
        _admin.ExpMult = 2;
        var (amo, _) = Conectado(38, heading: 2);
        amo.Donador = true;
        var presa = Presa(42);
        MascotaContra(amo, presa);
        double expAntes = amo.Exp;

        GirarHasta(() => _world.NpcByIndex(presa.CharIndex) is null, 3000);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        long conEvento = ExpDeLaPresa * 5 * 2;
        Assert.Equal(conEvento + conEvento / 2, (long)(amo.Exp - expAntes));
    }

    /// <summary>
    /// CON EL ORO LLENO NO SE COBRA, PERO EL BOTÍN SE TIRA IGUAL. En el original el tope sólo corta
    /// <c>NPCTirarOro</c> (MODULO_NPCs.bas:903) y <c>NPC_TIRAR_ITEMS</c> corre después sin mirar nada (:166). Con
    /// una tasa alta llegar al tope deja de ser raro, y ahí el port perdía también los ítems.
    /// </summary>
    [Fact]
    public void Con_el_oro_lleno_el_botin_se_tira_igual()
    {
        const int Lenia = GameLimits.WoodObj;
        Armar(exp: 1, oro: 200);
        var (amo, _) = Conectado(38, heading: 2);
        amo.Gold = int.MaxValue - 10;                        // lo que da la presa no entra
        var presa = Presa(42, botin: new[] { new NpcDrop(Lenia, 3, ProbTirar: 100) });   // 100 = cae siempre
        var dondeMuere = presa.Pos;
        MascotaContra(amo, presa);

        GirarHasta(() => _world.NpcByIndex(presa.CharIndex) is null, 3000);

        Assert.Null(_world.NpcByIndex(presa.CharIndex));
        Assert.Equal(int.MaxValue - 10, amo.Gold);           // no cobró: se pasaba del tope
        bool cayo = false;
        for (int dx = -2; dx <= 2 && !cayo; dx++)
            for (int dy = -2; dy <= 2 && !cayo; dy++)
                cayo = _world.TryGetGround(new WorldPos(Campo, (byte)(dondeMuere.X + dx), (byte)(dondeMuere.Y + dy)), out var g)
                       && g.ObjIndex == Lenia;
        Assert.True(cayo, "con el oro lleno la presa no soltó su botín");
    }

    /// <summary>
    /// EL GOTEO DEL GOLPE lleva la tasa ADENTRO y el evento encima. Con la presa de un punto de vida el golpe
    /// que la mata cobra el pozo entero en el goteo —que corre antes de restar el daño— y al resto no le
    /// queda nada, así que lo que se mide acá es sólo el camino del golpe.
    /// </summary>
    [Fact]
    public async Task El_golpe_que_mata_cobra_el_pozo_por_la_tasa_y_por_el_evento()
    {
        var loop = new GameLoop();
        using var cts = new CancellationTokenSource();
        Armar(exp: 5, oro: 1, loop);
        var corriendo = loop.RunAsync(cts.Token);
        try
        {
            _admin.ExpMult = 2;
            var (jugador, sesion) = Conectado(40, heading: 2);   // mira al este, donde está la presa
            var presa = Presa(41);
            double expAntes = jugador.Exp;

            // La tirada de impacto puede fallar: se pega hasta que muere. Fallar no cobra nada.
            for (int i = 0; i < 300 && _world.NpcByIndex(presa.CharIndex) is not null; i++)
            {
                _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.attack }));
                await Task.Delay(20);
            }
            await Task.Delay(100);   // la muerte y el pago van en la misma acción del loop

            Assert.Null(_world.NpcByIndex(presa.CharIndex));
            Assert.Equal(ExpDeLaPresa * 5 * 2, (long)(jugador.Exp - expAntes));
        }
        finally
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
    }
}
