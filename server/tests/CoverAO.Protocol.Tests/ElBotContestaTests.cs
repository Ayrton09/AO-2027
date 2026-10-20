using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;
using Como = CoverAO.Server.Net.SentidosDelBot.ComoLoDijo;
using Tema = CoverAO.Server.Net.Bot.DeQueHabla;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT CONTESTA CON FRASES ARMADAS (decisión del autor, 20/10): de qué le hablan (<see cref="Bot.DeQueLeHablan"/>), qué le
/// contesta (<see cref="Bot.QueLeContesta"/>), cuándo (<see cref="Bot.LeContesta"/>) y que la respuesta salga por donde le hablaron.
/// </summary>
public sealed class ElBotContestaTests
{
    [Theory]
    [InlineData("hola", Tema.Saludo)]
    [InlineData("Buenos días!", Tema.Saludo)]
    [InlineData("¿Qué hacés?", Tema.QueHace)]
    [InlineData("que nivel sos", Tema.Nivel)]
    [InlineData("hola, dame oro", Tema.Pide)]
    [InlineData("vendes pociones?", Tema.Comercio)]
    [InlineData("vendo botas", Tema.Comercio)]      // "botas" no es "bot"
    [InlineData("vamos a cazar?", Tema.Grupo)]
    [InlineData("sos bot?", Tema.EsBot)]
    [InlineData("sos un bot forro", Tema.Insulto)]   // el insulto gana: no se contesta
    [InlineData("gracias!!", Tema.Gracias)]
    [InlineData("chau", Tema.Despedida)]
    [InlineData("q nv sos?", Tema.Nivel)]           // en el chat de AO "nv" es el nivel
    [InlineData("forro", Tema.Insulto)]
    [InlineData("asdf qwe", Tema.Otro)]
    public void Sabe_de_qué_le_hablan(string texto, Tema tema)
    {
        Assert.Equal(tema, Bot.DeQueLeHablan(texto));
    }

    [Fact]
    public void Las_frases_entran_en_la_charla_y_no_nombran_a_nadie()
    {
        Assert.Null(Bot.QueLeContesta(Tema.Insulto, 20, 0));
        Assert.Contains("23", Bot.QueLeContesta(Tema.Nivel, 23, 0));
        foreach (var (tema, frases) in Bot.Frases)
            for (int i = 0; i < frases.Length; i++)
            {
                string f = Bot.QueLeContesta(tema, 40, i)!;
                Assert.True(f.Length is > 0 and < 80, $"{tema}: \"{f}\" no entra en la charla (HandleTalk corta en 80)");
                Assert.DoesNotContain("{", f);
                Assert.DoesNotContain("[", f);
            }
        // Y NINGUNA NOMBRA A UN BOT: es lo que evita que dos bots se contesten entre ellos.
        foreach (var frases in Bot.Frases.Values)
            foreach (string f in frases)
                foreach (string bot in PobladoDeBots.Nombres.Concat(PobladoDeBots.Suplentes))
                    Assert.False(Bot.LoNombra(f, bot), $"\"{f}\" nombra al bot {bot}");
        Assert.True(Bot.DemoraMs("hola", 0) >= 1_500 && Bot.DemoraMs(new string('x', 200), 999) == 8_000);
    }

    [Theory]
    [InlineData(Como.Susurro, Tema.Saludo, 84, 0L, null, 0, true)]
    [InlineData(Como.Susurro, Tema.Saludo, 85, 0L, null, 0, false)]    // no todo se contesta
    [InlineData(Como.Charla, Tema.Saludo, 69, 0L, null, 0, true)]
    [InlineData(Como.Charla, Tema.Saludo, 70, 0L, null, 0, false)]
    [InlineData(Como.Susurro, Tema.Otro, 39, 0L, null, 0, true)]
    [InlineData(Como.Susurro, Tema.Otro, 40, 0L, null, 0, false)]
    [InlineData(Como.Susurro, Tema.Insulto, 0, 0L, null, 0, false)]
    [InlineData(Como.Susurro, Tema.Saludo, 0, 19_999L, 0L, 0, false)]   // al mismo, cada tanto
    [InlineData(Como.Susurro, Tema.Saludo, 0, 20_000L, 0L, 0, true)]
    [InlineData(Como.Susurro, Tema.Saludo, 0, 0L, null, 4, false)]      // tope por minuto
    public void Contesta_como_una_persona(Como como, Tema tema, int sorteo, long ahora, long? ultima, int enElMinuto, bool contesta)
    {
        Assert.Equal(contesta, Bot.LeContesta(como, tema, sorteo, ahora, ultima, enElMinuto));
    }

    private static readonly MethodInfo CharlarM =
        typeof(Bot).GetMethod("Charlar", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Y LA RESPUESTA SALE POR DONDE LE HABLARON: a un susurro de Juan, un susurro a Juan con una frase de saludo; a una charla que
    /// lo nombra, una charla. Con los paquetes que arma el servidor y el bot de verdad (su semilla es fija).
    /// </summary>
    [Fact]
    public async Task Al_susurro_le_contesta_con_un_susurro_y_a_la_charla_con_la_charla()
    {
        var (bot, s, t) = Armar();
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan", "hola"), PacketDispatcher.FuenteSusurro));
        var susurro = await Respuesta(bot, s, t);
        Assert.NotNull(susurro);
        Assert.Equal((byte)ClientPacketId.Whisper, susurro![0]);
        var (a, texto) = (Cadena(susurro, 1), Cadena(susurro, 3 + Cadena(susurro, 1).Length));
        Assert.Equal("Juan", a);
        Assert.Contains(texto, Bot.Frases[Tema.Saludo]);

        // No todo se contesta (`ContestaCharla`): le hablan hasta tres personas, como en una plaza.
        byte[]? charla = null;
        foreach (var (ci, nombre) in new[] { ((short)501, "Ana"), ((short)502, "Beto"), ((short)503, "Caro") })
        {
            s.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = ci, Name = nombre, Account = nombre.ToLowerInvariant() + "_test", Pos = new WorldPos(1, 52, 50),
            }));
            s.Recibir(ServerMessages.ChatOverHead("bruma que haces?", ci, 1));
            if ((charla = await Respuesta(bot, s, t)) is not null) break;
        }
        Assert.NotNull(charla);
        Assert.Equal((byte)ClientPacketId.Talk, charla![0]);
        Assert.Contains(Cadena(charla, 1), Bot.Frases[Tema.QueHace]);
        Assert.Equal(1, charla[^1]);   // modo 1, la charla
    }

    [Fact]
    public async Task Al_grito_y_al_insulto_no_les_contesta()
    {
        var (bot, s, t) = Armar();
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan", "hola Bruma"), PacketDispatcher.FuenteGrito));
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan", "forro"), PacketDispatcher.FuenteSusurro));
        Assert.Null(await Respuesta(bot, s, t, ms: 9_000));
    }

    private static (Bot, SentidosDelBot, TransporteDeBot) Armar()
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        s.Recibir(ServerMessages.UserCharIndexInServer(500));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 500, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(1, 50, 50), Level = 20,
        }));
        var d = GameData.Load();
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, new Navegante(d), null!), 1);
        return (bot, s, new TransporteDeBot("Bruma"));
    }

    /// <summary>Llama a <c>Charlar</c> como lo hacen los bucles del bot hasta que manda algo, o null a los <paramref name="ms"/>.</summary>
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
