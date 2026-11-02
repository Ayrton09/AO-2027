using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class CastearEnElBordeTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private const byte PrimeraFila = (byte)GameLimits.MinYBorder;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_blanco_de_la_primera_fila_se_elige_en_los_pies_si_la_baldosa_de_abajo_esta_vacia(bool esNpc)
    {
        var f = Armar(esNpc: esNpc);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, esNpc ? (short)2 : (short)6));
        Assert.True(Lanzar(f));
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
        Assert.Equal(new byte[] { (byte)ClientPacketId.WorkLeftClick, 48, PrimeraFila, (byte)GameLimits.SkillMagia }, await Proximo(f));
    }

    [Theory]
    [InlineData("persona")]
    [InlineData("criatura")]
    [InlineData("cadaver")]
    [InlineData("propio bot")]
    public void No_clickea_los_pies_si_la_baldosa_de_abajo_elegiria_a_otro(string ocupante)
    {
        var f = Armar(botX: ocupante == "propio bot" ? (byte)48 : (byte)49);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 2));
        if (ocupante != "propio bot") AgregarOcupante(f, ocupante);
        Assert.False(Lanzar(f));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_cabeza_habitual_sigue_siendo_el_clic_cuando_cae_en_el_area_jugable(bool esNpc)
    {
        var f = Armar(botY: (byte)(PrimeraFila + 2), blancoY: (byte)(PrimeraFila + 1), esNpc: esNpc);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, esNpc ? (short)2 : (short)6));
        AgregarOcupante(f, "persona", (byte)(PrimeraFila + 2));
        Assert.True(Lanzar(f));
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal(PrimeraFila, (await Proximo(f))[2]);
    }

    [Theory]
    [InlineData(49, 14, 48, 7)]
    [InlineData(49, 15, 48, 8)]
    [InlineData(9, 8, 8, 7)]
    public void Un_clic_fuera_del_rango_o_del_area_jugable_no_se_manda(byte botX, byte botY, byte blancoX, byte blancoY)
    {
        var f = Armar(botX: botX, botY: botY, blancoX: blancoX, blancoY: blancoY);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 2));
        Assert.False(Lanzar(f));
    }

    [Fact]
    public async Task La_autocura_en_la_primera_fila_usa_sus_pies_si_no_hay_nadie_abajo()
    {
        var f = Armar(botX: 48, botY: PrimeraFila, ponerBlanco: false);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 5));
        Assert.True(Invocar<bool>(f, "CurarseConMagia", f.Transporte, f.Sentidos));
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal(new byte[] { (byte)ClientPacketId.WorkLeftClick, 48, PrimeraFila, (byte)GameLimits.SkillMagia }, await Proximo(f));
    }

    [Fact]
    public void La_autocura_no_se_desvia_a_la_persona_parada_abajo()
    {
        var f = Armar(botX: 48, botY: PrimeraFila, ponerBlanco: false);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 5));
        AgregarOcupante(f, "persona");
        Assert.False(Invocar<bool>(f, "CurarseConMagia", f.Transporte, f.Sentidos));
    }

    [Fact]
    public async Task Para_quietar_tambien_se_elige_el_mismo_blanco_en_el_borde()
    {
        var f = Armar(esNpc: false);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 24));
        Assert.True(Invocar<bool>(f, "QuietarA", f.Transporte, f.Sentidos, f.Blanco));
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal(PrimeraFila, (await Proximo(f))[2]);
    }

    [Fact]
    public async Task Para_remover_su_paralisis_tambien_puede_elegirse_en_el_borde()
    {
        var f = Armar(botX: 48, botY: PrimeraFila, ponerBlanco: false);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 10));
        f.Sentidos.Recibir(ServerMessages.ParalizeOK());
        Assert.True(Invocar<bool>(f, "RemoverseLaParalisis", f.Transporte, f.Sentidos));
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal(PrimeraFila, (await Proximo(f))[2]);
    }

    [Fact]
    public async Task Un_blanco_inseguro_no_borra_la_referencia_de_una_cura_cuyo_rechazo_llega_tarde()
    {
        var f = Armar();
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 5));
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(2, 2));
        Assert.True(Invocar<bool>(f, "CurarseConMagia", f.Transporte, f.Sentidos));
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
        f.Sentidos.Recibir(ServerMessages.PosUpdate(48, (byte)(PrimeraFila + 1)));
        Assert.False(Lanzar(f)); // Ahora el propio bot ocupa la baldosa que desviaría el clic.
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(221));
        Assert.False(Invocar<bool>(f, "CurarseConMagia", f.Transporte, f.Sentidos));
    }

    [Fact]
    public async Task El_dispatcher_real_permite_el_clic_en_los_pies_desde_la_diagonal()
    {
        await using var juego = new PruebaDelDispatcher(botDebajo: false);
        await juego.CastearEnLosPies();
        Assert.True(juego.Npc.Hp < 100);
        Assert.True(juego.Mago.Mana < 1_000);
    }

    [Fact]
    public async Task El_dispatcher_real_elige_al_propio_mago_si_esta_justo_debajo_del_npc()
    {
        await using var juego = new PruebaDelDispatcher(botDebajo: true);
        await juego.CastearEnLosPies();
        Assert.Equal(100, juego.Npc.Hp);
        Assert.Equal(1_000, juego.Mago.Mana);
        Assert.Equal(juego.Mago.CharIndex, juego.Mago.TargetUser);
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, SentidosDelBot.Visible Blanco);

    private static Fixture Armar(byte botX = 49, byte botY = PrimeraFila + 1, byte blancoX = 48, byte blancoY = PrimeraFila,
        bool esNpc = true, bool ponerBlanco = true)
    {
        var (datos, mapas) = Mundo.Value;
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(18, botX, botY), Level = 24,
            FactionStatus = Player.StatusCiudadano, MaxHp = 200, Hp = 60, MaxMana = 2_000, Mana = 2_000,
            MaxStamina = 300, Stamina = 300,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(18));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        var blanco = new SentidosDelBot.Visible(501, blancoX, blancoY, esNpc ? (short)502 : (short)0, Player.StatusCriminal, esNpc ? "" : "Kael");
        if (ponerBlanco)
        {
            if (esNpc) s.Recibir(ServerMessages.CharacterCreate(new Npc
            {
                CharIndex = 501, Template = datos.Npc(502)!, Hp = 100, Pos = new WorldPos(18, blancoX, blancoY),
            }));
            else s.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
                Pos = new WorldPos(18, blancoX, blancoY),
            }));
        }
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        return new Fixture(bot, s, new TransporteDeBot("Rufo"), blanco);
    }

    private static void AgregarOcupante(Fixture f, string cual, byte y = PrimeraFila + 1)
    {
        if (cual == "criatura") f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = 503, Template = Mundo.Value.Datos.Npc(500)!, Hp = 15, Pos = new WorldPos(18, 48, y),
        }));
        else f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 503, Name = "Bruno", Account = "bruno_test", Pos = new WorldPos(18, 48, y),
            Body = cual == "cadaver" ? PacketDispatcher.CorpseBody : (short)1,
        }));
    }

    private static bool Lanzar(Fixture f) => Invocar<bool>(f, "LanzarAlgo", f.Transporte, f.Sentidos, f.Blanco, null!);

    private static T Invocar<T>(Fixture f, string metodo, params object[] args) =>
        (T)typeof(Bot).GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Bot, args)!;

    private static async Task<byte[]> Proximo(Fixture f)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[4_096];
        int n = await f.Transporte.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ f.Transporte.Clave)).ToArray();
    }

    private sealed class PruebaDelDispatcher : IAsyncDisposable
    {
        private readonly GameLoop _loop = new();
        private readonly CancellationTokenSource _corte = new();
        private readonly Task _corriendo;
        private readonly Persistence _db;
        private readonly PacketDispatcher _dispatcher;
        private readonly ClientSession _sesion;
        public Player Mago { get; }
        public Npc Npc { get; }

        public PruebaDelDispatcher(bool botDebajo)
        {
            var datos = Mundo.Value.Datos;
            var world = new World();
            var niveles = new Leveling(datos.LevelExp);
            _db = new Persistence(AlmacenSqlite.EnMemoria(), niveles);
            _dispatcher = new PacketDispatcher(world, _loop, _db, datos, niveles);
            Mago = new Player
            {
                CharIndex = world.AllocateCharIndex(), Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(18, botDebajo ? (byte)48 : (byte)49, (byte)(PrimeraFila + 1)),
                Level = 24, MaxHp = 200, Hp = 200, MaxMana = 1_000, Mana = 1_000, MaxStamina = 300, Stamina = 300,
                CombatMode = true, FactionStatus = Player.StatusCiudadano,
            };
            Mago.Skills[GameLimits.SkillMagia] = 100;
            Mago.Spells[1] = 2;
            world.Add(Mago);
            _sesion = new ClientSession(new TransporteMudo(), _dispatcher) { Player = Mago };
            Mago.Connection = _sesion;
            var ficha = new NpcInfo(904, "NPC de prueba", 1, 1, 3, 100, 1, 1, 0, 0, 0, 0, 0, Hostile: true, Attackable: true);
            Npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 100, Hostile = true, Pos = new WorldPos(18, 48, PrimeraFila) };
            world.AddNpc(Npc);
            _corriendo = _loop.RunAsync(_corte.Token);
        }

        public async Task CastearEnLosPies()
        {
            _dispatcher.HandleOne(_sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
            await _loop.SincronizarContinuacionesAsync().WaitAsync(TimeSpan.FromSeconds(3));
            _dispatcher.HandleOne(_sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)48, PrimeraFila, (byte)GameLimits.SkillMagia }));
            await _loop.SincronizarContinuacionesAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }

        public async ValueTask DisposeAsync()
        {
            _corte.Cancel();
            try { await _corriendo.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            _db.Dispose();
            _corte.Dispose();
        }
    }

    private sealed class TransporteMudo : ITransport
    {
        public string RemoteEndpoint => "test-borde";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }
}
