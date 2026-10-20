using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;
using Como = CoverAO.Server.Net.SentidosDelBot.ComoLoDijo;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT OYE EL CHAT (<see cref="SentidosDelBot.TomarLoOido"/>) con los mismos paquetes que arma el servidor: la charla por el globo
/// (<c>ChatOverHead</c> modo 1) y el grito, el global y el susurro por la línea "[Nombre] texto" de la consola.
/// </summary>
public sealed class ElBotOyeTests
{
    private const short Yo = 500, Juan = 501, Lobo = 600;

    private static SentidosDelBot Sentidos()
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        s.Recibir(ServerMessages.UserCharIndexInServer(Yo));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = Yo, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(1, 50, 50),
        }));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = Juan, Name = "Juan", Account = "juan_test", GuildName = "Los Pibes", Pos = new WorldPos(1, 52, 50),
        }));
        s.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = Lobo,
            Template = new NpcInfo(501, "Lobo", 0, 0, 0, MaxHp: 20, MinHit: 1, MaxHit: 2, Def: 0,
                                   GiveExp: 5, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
            Pos = new WorldPos(1, 54, 50),
        }));
        return s;
    }

    [Fact]
    public void Oye_la_charla_de_la_persona_a_la_vista_con_su_nombre_sin_el_clan()
    {
        var s = Sentidos();
        Assert.Equal("Juan", s.NombreDe(Juan));
        s.Recibir(ServerMessages.ChatOverHead("hola Bruma", Juan, 1));
        var oido = Assert.Single(s.TomarLoOido());
        Assert.Equal(("Juan", "hola Bruma", Como.Charla), (oido.Quien, oido.Texto, oido.Como));
        Assert.Empty(s.TomarLoOido());
        Assert.Equal(1, s.OidoEnTotal(Como.Charla));
    }

    [Fact]
    public void No_se_oye_a_sí_mismo_ni_a_las_criaturas_ni_a_quien_no_ve()
    {
        var s = Sentidos();
        s.Recibir(ServerMessages.ChatOverHead("yo hablo", Yo, 1));
        s.Recibir(ServerMessages.ChatOverHead("grr", Lobo, 1));
        s.Recibir(ServerMessages.ChatOverHead("de lejos", 999, 1));
        s.Recibir(ServerMessages.ConsoleMsg("[Bruma] lo que susurré", PacketDispatcher.FuenteSusurro));
        Assert.Null(s.NombreDe(Lobo));
        Assert.Empty(s.TomarLoOido());
    }

    [Fact]
    public void El_grito_el_global_y_el_susurro_se_oyen_una_vez_por_la_consola()
    {
        var s = Sentidos();
        // El servidor manda los dos: la línea de consola y el globo. El globo de esos modos no se cuenta.
        s.Recibir(ServerMessages.ChatOverHead("vendo espada", Juan, 2));
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan", "vendo espada"), PacketDispatcher.FuenteGrito));
        s.Recibir(ServerMessages.ChatOverHead("alguien para el dragon?", Juan, 3));
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan", "alguien para el dragon?"), PacketDispatcher.FuenteGlobal));
        s.Recibir(ServerMessages.ChatOverHead("che", Juan, 4));
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan", "che"), PacketDispatcher.FuenteSusurro));
        // De uno que no está a la vista también: la línea trae el nombre.
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Juan Perez", "hola"), PacketDispatcher.FuenteSusurro));
        var oido = s.TomarLoOido();
        Assert.Equal(new[] { Como.Grito, Como.Global, Como.Susurro, Como.Susurro }, oido.Select(o => o.Como));
        Assert.Equal(new[] { "Juan", "Juan", "Juan", "Juan Perez" }, oido.Select(o => o.Quien));
        Assert.Equal("alguien para el dragon?", oido[1].Texto);
    }

    [Fact]
    public void La_consola_que_no_es_chat_no_se_oye_y_la_puñalada_se_sigue_contando()
    {
        var s = Sentidos();
        s.Recibir(ServerMessages.ConsoleMsg("[Juan] hola", 0));                       // otra fuente
        s.Recibir(ServerMessages.ConsoleMsg("Has ganado 10 puntos", PacketDispatcher.FuenteSusurro));
        s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.ApunalasteLaCriatura(24), 1));
        Assert.Empty(s.TomarLoOido());
        Assert.Equal(24, s.TomarLoQuePego().Item3);
    }

    [Fact]
    public void Guarda_hasta_el_tope_y_pierde_lo_más_viejo()
    {
        var s = Sentidos();
        for (int i = 0; i < SentidosDelBot.OidoMax + 5; i++)
            s.Recibir(ServerMessages.ConsoleMsg($"[Juan] {i}", PacketDispatcher.FuenteGlobal));
        var oido = s.TomarLoOido();
        Assert.Equal(SentidosDelBot.OidoMax, oido.Count);
        Assert.Equal("5", oido[0].Texto);
        Assert.Equal(SentidosDelBot.OidoMax + 5, s.OidoEnTotal(Como.Global));
    }

    [Theory]
    [InlineData("[Juan] hola", "Juan", "hola")]
    [InlineData("[Juan] [x] y", "Juan", "[x] y")]
    [InlineData("[Juan] ", null, null)]
    [InlineData("[Juan Perez] hola", "Juan Perez", "hola")]
    [InlineData("[ Juan] hola", null, null)]
    [InlineData("Juan: hola", null, null)]
    [InlineData("[] hola", null, null)]
    public void La_linea_de_chat_es_nombre_entre_corchetes_y_texto(string linea, string? quien, string? texto)
    {
        var chat = SentidosDelBot.LineaDeChat(linea);
        Assert.Equal(quien, chat?.Quien);
        Assert.Equal(texto, chat?.Texto);
    }

    [Fact]
    public void En_el_log_el_texto_va_saneado_y_cortado()
    {
        var oido = new SentidosDelBot.LoOido("Juan", "hola\r\n[TRAMPA] falso\u001b[2J" + new string('x', 200), Como.Susurro, 0);
        string linea = Bot.LineaDelLog("Bruma", oido);
        Assert.DoesNotContain('\n', linea);
        Assert.DoesNotContain('\r', linea);
        Assert.DoesNotContain('\u001b', linea);
        Assert.StartsWith("[bot Bruma] Susurro de Juan: hola  [TRAMPA] falso", linea);
        Assert.True(linea.Length < 140, "cortado");
    }

    [Fact]
    public void El_nombre_se_actualiza_con_el_clan()
    {
        var s = Sentidos();
        s.Recibir(ServerMessages.UpdateTagAndStatus(new Player
        {
            CharIndex = Juan, Name = "Juan", Account = "juan_test", GuildName = "Otros", FactionStatus = 1,
        }));
        Assert.Equal("Juan", s.NombreDe(Juan));
    }

    [Theory]
    [InlineData(Como.Susurro, "lo que sea", true)]
    [InlineData(Como.Charla, "hola Bruma", true)]
    [InlineData(Como.Charla, "BRUMA, vení", true)]
    [InlineData(Como.Charla, "las brumas del norte", false)]
    [InlineData(Como.Charla, "hola", false)]
    [InlineData(Como.Grito, "Bruma!", false)]
    [InlineData(Como.Global, "Bruma vende?", false)]
    public void Es_para_él_el_susurro_y_la_charla_que_lo_nombra(Como como, string texto, bool paraEl)
    {
        Assert.Equal(paraEl, Bot.LeHablanAEl(new SentidosDelBot.LoOido("Juan", texto, como, 0), "Bruma"));
    }
}
