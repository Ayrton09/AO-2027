using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El Mago pelea de lejos (pedido del tester del 20/11): con el rival pegado, primero lo inmoviliza o se aleja, y lanza desde
/// lejos. Las otras clases siguen como antes.
/// </summary>
public sealed class MagoPeleaDeLejosTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private const short Saeta = 6, Inmovilizar = 24;

    [Fact]
    public void Los_hechizos_de_la_prueba_son_los_del_juego()
    {
        var datos = Mundo.Value.Datos;
        Assert.True(datos.Spell(Inmovilizar) is { IsImmobilize: true, ManaCost: 250 });
        Assert.True(datos.Spell(Saeta) is { SubeHp: 2 });
    }

    [Theory]
    [InlineData(false, 1, 0, false, true)]    // a una persona pegada, enseguida
    [InlineData(false, 2, 5, false, false)]   // a dos pasos todavía no: se aleja
    [InlineData(true, 1, 1, false, false)]    // a la criatura que lo alcanza la primera vez, no: se aleja
    [InlineData(true, 1, 2, false, true)]     // a la que lo vuelve a alcanzar aunque se alejó, sí
    [InlineData(true, 1, 9, true, false)]     // a una inmune, nunca
    public void Cuando_gasta_el_mana_en_inmovilizar(bool esNpc, int pasos, int alcances, bool inmune, bool inmoviliza) =>
        Assert.Equal(inmoviliza, Bot.MagoInmoviliza(esNpc, pasos, alcances, inmune));

    /// <summary>Ronda 115: alejarse de todo le costó a Bruma (Mago 20, 166 de vida) un 30 y un 48 % de la experiencia. Contra lo
    /// que cazaba —lobos, ratas, murciélagos— lanza donde está; se aleja de lo que lo mata en cinco golpes.</summary>
    [Theory]
    [InlineData(501, 166, false)]   // Lobo, pega 18
    [InlineData(559, 166, false)]   // Rata Gigante, pega 22
    [InlineData(500, 166, false)]   // Murciélago, pega 1
    [InlineData(592, 166, true)]    // Ent, pega 90
    [InlineData(559, 100, true)]    // la misma Rata con un Mago de 100 de vida: 5 x 22 la alcanza
    public void Contra_criaturas_se_aleja_solo_de_las_que_lo_lastiman(short ficha, int vida, bool seAleja) =>
        Assert.Equal(seAleja, Bot.SeAlejaDeLaCriatura(Mundo.Value.Datos.Npc(ficha), vida));

    /// <summary>Rondas 117 y 118: de 14 muertes de los Magos, 7 contra un Gladiador de 3 a 5 niveles más y 2 contra un Guerrero
    /// de 1 a 3 más; sus bajas, clases de oficio y pares.</summary>
    [Theory]
    [InlineData(21, (short)26, GameLimits.ClaseGladiador, 100, false)]   // Fabia contra Rufo
    [InlineData(22, (short)24, GameLimits.ClaseGuerrero, 100, false)]    // Ciro contra Bruma
    [InlineData(22, (short)22, GameLimits.ClaseGuerrero, 100, false)]    // ni de su nivel
    [InlineData(22, (short)21, GameLimits.ClaseGuerrero, 100, true)]     // de menos nivel, sí
    [InlineData(22, (short)26, GameLimits.ClaseGladiador, 25, true)]     // casi muerto, lo remata
    [InlineData(22, (short)24, GameLimits.ClasePaladin, 100, true)]      // otras clases, hasta 2 por encima
    [InlineData(22, (short)25, GameLimits.ClasePaladin, 100, false)]
    [InlineData(22, (short)27, 14, 100, true)]                           // un Minero, hasta 5 por encima
    [InlineData(22, SentidosDelBot.NivelEscondido, 14, 10, false)]        // escondido, nunca
    public void Con_quien_pelea_el_mago(int miNivel, short nivel, byte clase, int vida, bool pelea) =>
        Assert.Equal(pelea, Bot.MagoLeConvienePelear(miNivel, nivel, (clase, vida)));

    /// <summary>Ronda 120: Kael, Nigromante 20, mató a Rufo, Mago 21.</summary>
    [Theory]
    [InlineData(21, (short)20, true)]
    [InlineData(21, (short)21, false)]
    [InlineData(21, (short)22, false)]
    public void Al_nigromante_solo_si_es_de_menos_nivel(int miNivel, short nivel, bool pelea) =>
        Assert.Equal(pelea, Bot.MagoLeConvienePelear(miNivel, nivel, (GameLimits.ClaseNigromante, 100)));

    /// <summary>Ronda 120: alejarse unos pasos no alcanzó contra Fabia, que lo siguió; con poca vida, igual.</summary>
    [Theory]
    [InlineData(false, 1.0, true)]    // un rival que no le conviene: se va del mapa
    [InlineData(true, 0.45, true)]    // con menos de la mitad de la vida, también
    [InlineData(true, 0.8, false)]    // le conviene y está bien: pelea
    [InlineData(null, 0.8, false)]    // sin la ficha, primero lo mira
    [InlineData(null, 0.3, true)]
    public void Cuando_se_va_del_mapa(bool? leConviene, double salud, bool seVa) =>
        Assert.Equal(seVa, Bot.MagoSeVaDelMapa(leConviene, salud));

    [Theory]
    [InlineData(0.45, 80, true)]      // va perdiendo: la deja
    [InlineData(0.45, 25, false)]     // el otro está para rematar: sigue
    [InlineData(0.45, null, true)]
    [InlineData(0.7, 90, false)]
    public void Cuando_deja_la_pelea(double salud, int? vidaDelRival, bool laDeja) =>
        Assert.Equal(laDeja, Bot.MagoDejaLaPelea(salud, vidaDelRival));

    [Fact]
    public void Sin_la_ficha_hace_lo_de_siempre() =>
        Assert.True(Bot.MagoLeConvienePelear(22, null, null));

    [Fact]
    public void La_ficha_guarda_la_clase_y_la_vida()
    {
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player { CharIndex = 500, Name = "Rufo", Account = "rufo_test", Level = 22, Pos = new WorldPos(18, 50, 50) };
        var ella = new Player { CharIndex = 501, Name = "Fabia", Account = "fabia_test", Level = 26, Class = GameLimits.ClaseGladiador, MaxHp = 400, Hp = 100,
                                Pos = new WorldPos(18, 51, 50) };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.CharMsgStatus(ella, yo, 0));
        Assert.Equal((short)26, s.NivelDe(501, 60_000));
        Assert.Equal(((byte)GameLimits.ClaseGladiador, 25), s.FichaDe(501, 60_000));
    }

    [Fact]
    public async Task El_mago_deja_la_pelea_con_un_gladiador_mas_fuerte()
    {
        var f = Armar(GameLimits.ClaseMago, sabeInmovilizar: true);
        var gladiadora = new Player { CharIndex = 501, Name = "Kael", Account = "kael_test", Level = 33, Class = GameLimits.ClaseGladiador, MaxHp = 400, Hp = 400,
                                      Pos = new WorldPos(18, f.X, (byte)(f.Y - 1)) };
        f.Sentidos.Recibir(ServerMessages.CharMsgStatus(gladiadora, new Player { CharIndex = 502, Name = "Mira", Account = "mira_test", Level = 30 }, 0));
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(3)));   // la deja enseguida
    }

    [Fact]
    public void A_una_criatura_la_da_por_quieta_menos_que_a_una_persona()
    {
        Assert.Equal(1_000 + Bot.QuietaMs, Bot.QuietaHasta(1_000, cobro: true));
        Assert.Equal(1_000 + Bot.QuietaNpcMs, Bot.QuietaHasta(1_000, cobro: true, esNpc: true));
        Assert.Equal(1_000 + Bot.ReintentoDeQuietarMs, Bot.QuietaHasta(1_000, cobro: false, esNpc: true));
        Assert.True(Bot.QuietaNpcMs < 14_000);   // 37 vueltas de la IA de 380 ms (PacketDispatcher.CastStateOnNpc)
    }

    [Fact]
    public async Task El_mago_inmoviliza_a_la_persona_pegada_se_aleja_y_lanza_de_lejos()
    {
        var f = Armar(GameLimits.ClaseMago, sabeInmovilizar: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.LeftClick, (await Proximo(f))[0]);   // primero lo mira
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 2 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.UpdateMana(2_000 - 250));   // el servidor le cobró: quedó inmovilizada
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
            var siguiente = await HastaQueNoCamine(f);
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, siguiente);
            Assert.True(Distancia(f) >= Bot.DistanciaDelMago, $"Lanzó a {Distancia(f)} baldosas.");
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task El_mago_que_no_sabe_inmovilizar_se_aleja_antes_de_lanzar()
    {
        var f = Armar(GameLimits.ClaseMago, sabeInmovilizar: false);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.LeftClick, (await Proximo(f))[0]);   // primero lo mira
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await HastaQueNoCamine(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Las_otras_clases_lanzan_donde_estan()
    {
        var f = Armar(GameLimits.ClaseClerigo, sabeInmovilizar: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, TaskCompletionSource Cerrada, byte X, byte Y);

    /// <summary>El bot en un claro del mapa 18 con lugar para alejarse, y una persona de otro bando pegada al norte.</summary>
    private static Fixture Armar(byte clase, bool sabeInmovilizar)
    {
        var (datos, mapas) = Mundo.Value;
        const short mapa = 18;
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-4, 9)
                            from dx in Enumerable.Range(-4, 9)
                            select datos.IsBlockedTile(mapa, x + dx, y + dy) || datos.MapExits.ContainsKey((mapa, x + dx, y + dy)))
                           .All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 30,
            FactionStatus = Player.StatusCiudadano, MaxHp = 300, Hp = 300, MaxMana = 2_000, Mana = 2_000,
            MaxStamina = 400, Stamina = 400, Class = clase,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeSpellSlot(1, Saeta));
        if (sabeInmovilizar) s.Recibir(ServerMessages.ChangeSpellSlot(2, Inmovilizar));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
            Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - 1)),
        }));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, clase);
        return new Fixture(bot, s, new TransporteDeBot("Rufo"),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), sitio.X, sitio.Y);
    }

    private static int Distancia(Fixture f)
    {
        var (x, y) = f.Sentidos.Posicion;
        return Math.Abs(x - f.X) + Math.Abs(y - (f.Y - 1));
    }

    private static Task<bool> Empezar(Fixture f, CancellationToken ct) =>
        (Task<bool>)Cazar.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos, (short)501, f.Cerrada.Task, ct, true })!;

    /// <summary>El primer paquete que no es un paso: los pasos de alejarse pueden ser uno o dos.</summary>
    private static async Task<byte[]> HastaQueNoCamine(Fixture f)
    {
        for (int i = 0; i < 4; i++)
        {
            var p = await Proximo(f);
            if (p[0] != (byte)ClientPacketId.Walk) return p;
        }
        throw new InvalidOperationException("Siguió caminando.");
    }

    private static async Task<byte[]> Proximo(Fixture f)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[4_096];
        int n = await f.Transporte.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ f.Transporte.Clave)).ToArray();
    }

    private static async Task Cancelar(Task tarea, CancellationTokenSource corte)
    {
        corte.Cancel();
        try { await tarea.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) when (corte.IsCancellationRequested) { }
    }
}
