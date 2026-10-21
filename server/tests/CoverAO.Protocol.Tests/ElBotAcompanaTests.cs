using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;
using Inv = CoverAO.Server.Net.Bot.Invitacion;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT CAZA CON LA PERSONA QUE LO INVITA (decisión del autor, 21/10): acepta si la ve (<see cref="Bot.QueHaceConLaInvitacion"/>),
/// la acompaña un rato y la deja al cumplirse o al perderla (<see cref="Bot.DejaDeAcompanar"/>), y mientras tanto va hacia ella.
/// </summary>
public sealed class ElBotAcompanaTests
{
    [Theory]
    [InlineData(true, true, null, "Ana", Inv.Acepta)]
    [InlineData(true, true, "ana", "Ana", Inv.Acepta)]       // la misma vuelve a invitar: alarga el rato
    [InlineData(true, false, null, "Ana", Inv.Lejos)]        // le susurró desde otro lado
    [InlineData(true, true, "Beto", "Ana", Inv.NoPuede)]     // ya acompaña a otra
    [InlineData(false, true, null, "Ana", Inv.NoPuede)]      // muerto
    public void Acepta_si_la_ve_y_no_acompaña_a_otra(bool vivo, bool laVe, string? acompana, string quien, Inv que)
    {
        Assert.Equal(que, Bot.QueHaceConLaInvitacion(vivo, laVe, acompana, quien));
    }

    [Theory]
    [InlineData(1_000, 2_000, 1_000, false)]
    [InlineData(2_000, 2_000, 1_900, true)]                          // se cumplió el rato
    [InlineData(100_000, 999_999, 100_000 - Bot.PerdidaMs, false)]
    [InlineData(100_000, 999_999, 100_000 - Bot.PerdidaMs - 1, true)] // la perdió
    public void La_deja_al_cumplirse_el_rato_o_al_perderla(long ahora, long hasta, long vistaEn, bool deja)
    {
        Assert.Equal(deja, Bot.DejaDeAcompanar(ahora, hasta, vistaEn));
    }

    [Fact]
    public void Las_frases_de_acompañar_entran_en_la_charla_y_no_nombran_a_un_bot()
    {
        foreach (var frases in new[] { Bot.FrasesAcepta, Bot.FrasesLejos, Bot.FrasesSeVa, Bot.FrasesLaPerdio })
            foreach (string f in frases)
            {
                Assert.True(f.Length is > 0 and < 80, f);
                foreach (string bot in PobladoDeBots.Nombres.Concat(PobladoDeBots.Suplentes))
                    Assert.False(Bot.LoNombra(f, bot), $"\"{f}\" nombra al bot {bot}");
            }
        Assert.Equal(Bot.DeQueHabla.Grupo, Bot.DeQueLeHablan("Bruma vamos a cazar?"));
        Assert.Equal(Bot.DeQueHabla.Grupo, Bot.DeQueLeHablan("cazamos?"));
    }

    private static readonly MethodInfo CharlarM = typeof(Bot).GetMethod("Charlar", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo UltimaRespuestaA =
        typeof(Bot).GetField("_ultimaRespuestaA", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo AcompanarM = typeof(Bot).GetMethod("AcompanarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Y LO HACE: Ana, a seis baldosas en Rinkel, le susurra "vamos?"; el bot le contesta que sí por susurro, queda acompañándola,
    /// y en la vuelta que sigue camina hacia ella.
    /// </summary>
    [Fact]
    public async Task A_la_que_lo_invita_y_ve_le_dice_que_si_y_camina_hacia_ella()
    {
        var (bot, s, t) = Armar();
        // No todo se contesta: le insiste hasta tres veces, como cualquiera, separado por más que el rato entre respuestas.
        byte[]? si = null;
        for (int i = 0; i < 3 && si is null; i++)
        {
            s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Ana", "vamos a cazar?"), PacketDispatcher.FuenteSusurro));
            si = await Respuesta(bot, s, t);
            if (si is null) ((System.Collections.IDictionary)UltimaRespuestaA.GetValue(bot)!).Clear();
        }
        Assert.NotNull(si);
        Assert.Equal((byte)ClientPacketId.Whisper, si![0]);
        Assert.Contains(Cadena(si, 3 + Cadena(si, 1).Length), Bot.FrasesAcepta);
        Assert.Equal("Ana", bot.AcompanaA);

        var fin = new TaskCompletionSource();
        var vuelta = (Task<bool?>)AcompanarM.Invoke(bot, new object[] { t, s, (Func<short, bool>)(_ => false), true, fin.Task,
                                                                        CancellationToken.None })!;
        byte[]? paso = null;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (paso is null && DateTime.UtcNow < limite)
            if (await Proximo(t, 200) is { } p && p[0] == (byte)ClientPacketId.Walk) paso = p;
        fin.TrySetResult();
        try { await vuelta.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        Assert.NotNull(paso);
        Assert.Equal((byte)3, paso![1]);   // al sur, donde está ella (rumbo 3 del original)
    }

    [Theory]
    [InlineData(Bot.AburreMs, 0, false)]
    [InlineData(Bot.AburreMs + 1, 0, true)]                       // cinco minutos sin pelear nada
    [InlineData(0, Bot.IntentosSinLlegar - 1, false)]
    [InlineData(0, Bot.IntentosSinLlegar, true)]                  // no llega hasta ella
    public void Se_aburre_sin_pelear_o_sin_poder_llegar(long ahora, int sinLlegar, bool seAburre)
    {
        Assert.Equal(seAburre, Bot.SeAburre(ahora, 0, sinLlegar));
    }

    /// <summary>La criatura cerca de ella se elige sin la que ya no alcanzó (<c>NoSeLlegaA</c>): sin eso daba vueltas sin esperar.</summary>
    [Fact]
    public void Cerca_de_ella_se_elige_sin_la_que_no_se_alcanzo()
    {
        var (_, s, _) = Armar();
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, 75, 62)));   // al lado de Ana
        s.Recibir(ServerMessages.CharacterCreate(Criatura(601, 75, 66)));   // a cinco de Ana
        s.Recibir(ServerMessages.CharacterCreate(Criatura(602, 75, 70)));   // a nueve: fuera del radio
        Assert.Equal((short)600, s.MasCercanoA((75, 61), _ => true, Bot.RadioDeAyuda)!.Value.Quien.CharIndex);
        Assert.Equal((short)601, s.MasCercanoA((75, 61), _ => true, Bot.RadioDeAyuda, new HashSet<short> { 600 })!.Value.Quien.CharIndex);
        Assert.Null(s.MasCercanoA((75, 61), _ => true, Bot.RadioDeAyuda, new HashSet<short> { 600, 601 }));
        Assert.Null(s.MasCercanoA((75, 61), _ => false, Bot.RadioDeAyuda));
    }

    private static readonly FieldInfo AcompanaF = typeof(Bot).GetField("_acompana", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo InvitacionM =
        typeof(Bot).GetMethod("ContestarLaInvitacion", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void La_misma_que_vuelve_a_invitar_no_alarga_el_rato()
    {
        var (bot, s, _) = Armar();
        InvitacionM.Invoke(bot, new object[] { s, "Ana", 1_000L });
        var antes = AcompanaF.GetValue(bot);
        InvitacionM.Invoke(bot, new object[] { s, "ana", 900_000L });
        Assert.Equal(antes, AcompanaF.GetValue(bot));
        Assert.Equal("Ana", bot.AcompanaA);
    }

    [Fact]
    public async Task Al_cumplirse_el_rato_se_despide_en_la_charla_y_al_perderla_por_susurro()
    {
        var (bot, s, t) = Armar();
        long ahora = Environment.TickCount64;
        // El rato ya se cumplió, y Ana está a la vista: se despide en la charla.
        AcompanaF.SetValue(bot, ("Ana", ahora - 1, ahora, ahora, 0));
        Assert.Equal(false, await Vuelta(bot, s, t));
        var adios = await Proximo(t, 500);
        Assert.Equal((byte)ClientPacketId.Talk, adios![0]);
        Assert.Contains(Cadena(adios, 1), Bot.FrasesSeVa);
        Assert.Null(bot.AcompanaA);

        // Beto se fue de la vista hace más de la cuenta: se lo dice por susurro.
        AcompanaF.SetValue(bot, ("Beto", ahora + 600_000, ahora - Bot.PerdidaMs - 1, ahora, 0));
        Assert.Equal(false, await Vuelta(bot, s, t));
        var perdida = await Proximo(t, 500);
        Assert.Equal((byte)ClientPacketId.Whisper, perdida![0]);
        Assert.Equal("Beto", Cadena(perdida, 1));
        Assert.Contains(Cadena(perdida, 3 + "Beto".Length), Bot.FrasesLaPerdio);
        Assert.Null(bot.AcompanaA);
    }

    [Fact]
    public void La_que_acompaña_y_se_despide_la_suelta()
    {
        var (bot, s, t) = Armar();
        long ahora = Environment.TickCount64;
        AcompanaF.SetValue(bot, ("Ana", ahora + 600_000, ahora, ahora, 0));
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Beto", "chau"), PacketDispatcher.FuenteSusurro));
        CharlarM.Invoke(bot, new object[] { t, s });
        Assert.Equal("Ana", bot.AcompanaA);   // se despidió otro
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Ana", "chau, gracias"), PacketDispatcher.FuenteSusurro));
        CharlarM.Invoke(bot, new object[] { t, s });
        Assert.Null(bot.AcompanaA);
    }

    private static Task<bool?> Vuelta(Bot bot, SentidosDelBot s, TransporteDeBot t) =>
        (Task<bool?>)AcompanarM.Invoke(bot, new object[] { t, s, (Func<short, bool>)(_ => false), true, new TaskCompletionSource().Task,
                                                           CancellationToken.None })!;

    private static Npc Criatura(short charIndex, byte x, byte y) => new()
    {
        CharIndex = charIndex,
        Template = new NpcInfo(501, "Lobo", 0, 0, 0, MaxHp: 20, MinHit: 1, MaxHit: 2, Def: 0,
                               GiveExp: 5, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        Pos = new WorldPos(Rinkel, x, y),
    };

    private const short Yo = 500, Ana = 501;
    private const int Rinkel = 20;

    private static (Bot, SentidosDelBot, TransporteDeBot) Armar()
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        var yo = new Player
        {
            CharIndex = Yo, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(Rinkel, 75, 55), Level = 20,
            MaxHp = 200, Hp = 200, MaxMana = 0, Mana = 0, MaxStamina = 300, Stamina = 300, FactionStatus = Player.StatusCiudadano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(Yo));
        s.Recibir(ServerMessages.ChangeMap(Rinkel));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = Ana, Name = "Ana", Account = "ana_test", Pos = new WorldPos(Rinkel, 75, 61),
        }));
        var d = GameData.Load();
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, new Navegante(d), null!), 1);
        return (bot, s, new TransporteDeBot("Bruma"));
    }

    private static async Task<byte[]?> Respuesta(Bot bot, SentidosDelBot s, TransporteDeBot t, int ms = 10_000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < limite)
        {
            CharlarM.Invoke(bot, new object[] { t, s });
            if (await Proximo(t, 100) is { } p) return p;
        }
        return null;
    }

    private static string Cadena(byte[] p, int desde)
    {
        int largo = (short)(p[desde] | (p[desde + 1] << 8));
        return Codec.Ansi.GetString(p, desde + 2, largo);
    }

    private static async Task<byte[]?> Proximo(TransporteDeBot t, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        var buffer = new byte[4096];
        try
        {
            int n = await t.ReceiveAsync(buffer, cts.Token);
            if (n == 0) return null;
            var paquete = new byte[n];
            for (int i = 0; i < n; i++) paquete[i] = (byte)(buffer[i] ^ t.Clave);
            return paquete;
        }
        catch (OperationCanceledException) { return null; }
    }
}
