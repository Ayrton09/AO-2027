using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL VENENO DEL ÁREA Y EL VALOR DEL VENENO. La Tormenta ácida (#57) trae `Envenena = 3` y el original la aplica en el
/// área después del daño (HechizoTerrenoHP, modHechizos.bas:572-575), con ese 3 en `flags.Envenenado`, que es un Byte.
/// El "ya está envenenado" del hechizo Envenenar pregunta por el 1 (:1103), así que al envenenado por el área se lo
/// vuelve a envenenar. El valor se guarda tal cual en el .chr, al GM el veneno no le pega (mMainLoop.bas:168) y morir
/// reinicia el contador (Modulo_UsUaRiOs.bas:1253). Acá el área no envenenaba y el veneno era un sí o no.
/// </summary>
public class ElVenenoDelAreaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_veneno_area_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15, Ullathorpe = 1;
    private const short Envenenar = 4, TormentaAcida = 57;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool RecibioElLocale(short id) =>
            Recibidos.Any(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.LocaleMsg && BitConverter.ToInt16(x, 1) == id);
    }

    public ElVenenoDelAreaTests()
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

    private (Player Jugador, ClientSession Sesion, Transporte Transporte) Conectado(string nombre, short mapa, byte x,
        short hechizo = 0)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, 50), Heading = 2, Hp = 5000, MaxHp = 5000, Level = 30, Class = 2,
            FactionStatus = Player.StatusCriminal, CombatMode = true,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = hechizo;
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, transporte);
    }

    /// <summary>Castea la ranura 1 clickeando la baldosa (x, y).</summary>
    private async Task Castear(ClientSession sesion, byte x, byte y)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    [Fact]
    public async Task La_tormenta_acida_envenena_en_el_area_con_su_valor()
    {
        var tormenta = _datos.Spell(TormentaAcida)!;
        Assert.True(tormenta.IsArea && tormenta.Envenena == 3);                  // premisa: la data
        var (_, sesion, _) = Conectado("Aldo", Desierto, 50, TormentaAcida);
        var (victima, _, _) = Conectado("Bruno", Desierto, 53);
        await Castear(sesion, 53, 51);                                            // el centro, abajo de la víctima
        Assert.True(victima.Hp < victima.MaxHp, "premisa: el área le pegó");
        Assert.True(victima.IsPoisoned(_loop.Tick));
        Assert.Equal(3, victima.ValorDelVeneno);
    }

    [Fact]
    public async Task Donde_no_se_puede_atacar_la_tormenta_tampoco_envenena()
    {
        var (mago, sesion, _) = Conectado("Aldo", Ullathorpe, 50, TormentaAcida);
        var (victima, _, _) = Conectado("Bruno", Ullathorpe, 53);
        await Castear(sesion, 53, 51);
        Assert.True(mago.Mana < 5000, "premisa: el área se lanzó (sin criaturas se cobra siempre)");
        Assert.Equal(victima.MaxHp, victima.Hp);
        Assert.False(victima.IsPoisoned(_loop.Tick));
    }

    [Fact]
    public async Task Envenenar_al_envenenado_por_el_area_lo_vuelve_a_envenenar_con_el_1()
    {
        var (mago, sesion, suyo) = Conectado("Aldo", Desierto, 50, Envenenar);
        var (victima, _, _) = Conectado("Bruno", Desierto, 52);
        victima.Envenenar(3);
        await Castear(sesion, 52, 49);                                            // la cabeza de la víctima
        Assert.False(suyo.RecibioElLocale(326));
        Assert.Equal(1, victima.ValorDelVeneno);
        Assert.True(mago.Mana < 5000, "quedó lanzado y se cobra");
    }

    [Fact]
    public async Task Envenenar_al_envenenado_comun_avisa_el_326_y_no_cobra()
    {
        var (mago, sesion, suyo) = Conectado("Aldo", Desierto, 50, Envenenar);
        var (victima, _, _) = Conectado("Bruno", Desierto, 52);
        victima.Envenenar();
        await Castear(sesion, 52, 49);
        Assert.True(suyo.RecibioElLocale(326));
        Assert.Equal(5000, mago.Mana);
    }

    [Fact]
    public void El_valor_del_veneno_se_guarda_tal_cual()
    {
        var p = new Player { CharIndex = 1, Name = "Bruno", Account = "bruno" };
        p.Envenenar(3);
        var vuelto = CharacterFile.FromIni(CharacterFile.ToIni(p), 1, "Bruno", "bruno");
        Assert.True(vuelto.IsPoisoned(0));
        Assert.Equal(3, vuelto.ValorDelVeneno);
    }

    [Fact]
    public void Morir_reinicia_el_contador_del_veneno()
    {
        var (p, _, _) = Conectado("Bruno", Desierto, 52);
        p.Envenenar();
        p.PoisonCounter = Survival.PoisonIntervalTicks - 1;
        p.Hp = 0;
        PacketDispatcher.KillPlayer(_world, _datos, p, "test");
        Assert.Equal(0, p.PoisonCounter);
    }

    [Fact]
    public void Al_GM_el_veneno_no_le_pega()
    {
        var comun = new Player { CharIndex = 1, Name = "Bruno", Account = "bruno", Hp = 100, MaxHp = 100 };
        var gm = new Player { CharIndex = 2, Name = "Rugura", Account = "rugura", Hp = 100, MaxHp = 100, Privileges = 8 };
        comun.Envenenar();
        gm.Envenenar();
        var rng = new Random(1);
        for (int vuelta = 1; vuelta <= Survival.PoisonIntervalTicks + 1; vuelta++)
        {
            PacketDispatcher.EfectoVeneno(_world, _datos, comun, vuelta, rng);
            PacketDispatcher.EfectoVeneno(_world, _datos, gm, vuelta, rng);
        }
        Assert.True(comun.Hp < 100, "premisa: al común le pegó");
        Assert.Equal(100, gm.Hp);
    }
}
