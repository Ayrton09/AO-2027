using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// BOTS MÁS AGRESIVOS (pedido del tester, 25/11): cada bot tiene un ánimo fijo por su nombre, y se anima contra personas hasta 10, 7 o 4
/// niveles por encima (3 más con un aliado cerca), corregido por las clases (<see cref="Bot.SeAnimaContra"/>).
/// </summary>
public sealed class AnimoParaPelearTests
{
    [Fact]
    public void El_animo_es_fijo_por_el_nombre_y_hay_de_los_tres()
    {
        Assert.Equal(Bot.AnimoDe("Ciro"), Bot.AnimoDe("ciro"));
        var animos = PobladoDeBots.Nombres.Take(20).Select(Bot.AnimoDe).ToList();
        foreach (var a in Enum.GetValues<Animo>())
            Assert.InRange(animos.Count(x => x == a), 4, 10);
    }

    [Theory]
    [InlineData(Animo.Agresivo, false, 10)]
    [InlineData(Animo.Normal, false, 7)]
    [InlineData(Animo.Prudente, false, 4)]
    [InlineData(Animo.Prudente, true, 7)]
    public void Cuantos_niveles_se_anima(Animo animo, bool conAliado, int niveles) =>
        Assert.Equal(niveles, Bot.NivelesQueSeAnima(animo, conAliado));

    [Theory]
    [InlineData(GameLimits.ClaseMago, 22, (short)29, GameLimits.ClasePaladin, 100, 7, true)]       // el normal, hasta 7 por encima
    [InlineData(GameLimits.ClaseMago, 22, (short)30, GameLimits.ClasePaladin, 100, 7, false)]
    [InlineData(GameLimits.ClaseMago, 22, (short)26, GameLimits.ClaseGladiador, 100, 7, true)]     // contra un Gladiador, 3 menos
    [InlineData(GameLimits.ClaseMago, 22, (short)27, GameLimits.ClaseGladiador, 100, 7, false)]
    [InlineData(GameLimits.ClaseMago, 22, (short)26, GameLimits.ClaseNigromante, 100, 7, true)]    // el que pelea con magia, contra un Nigromante, 3 menos
    [InlineData(GameLimits.ClaseGuerrero, 22, (short)29, GameLimits.ClaseGladiador, 100, 7, true)] // el Guerrero, contra el Gladiador, como siempre
    [InlineData(GameLimits.ClaseAsesino, 22, (short)32, 14, 100, 7, true)]                         // a un Minero, 3 más
    [InlineData(GameLimits.ClaseMago, 22, (short)40, GameLimits.ClaseGladiador, 20, 4, true)]      // casi muerto, lo remata
    public void Contra_quien_se_anima(byte miClase, int miNivel, short nivel, byte clase, int vida, int niveles, bool pelea) =>
        Assert.Equal(pelea, Bot.SeAnimaContra(miClase, miNivel, nivel, (clase, vida), niveles));

    [Theory]
    [InlineData(10, true)]     // el agresivo, con el nivel escondido
    [InlineData(7, false)]     // el normal solo, no
    [InlineData(4, false)]
    public void Con_el_nivel_escondido(int niveles, bool pelea) =>
        Assert.Equal(pelea, Bot.SeAnimaContra(GameLimits.ClaseGuerrero, 22, SentidosDelBot.NivelEscondido, (GameLimits.ClaseMago, 100), niveles));

    [Fact]
    public void Sin_la_ficha_primero_la_mira() =>
        Assert.True(Bot.SeAnimaContra(GameLimits.ClaseMago, 22, null, null, 4));

    [Fact]
    public void Con_cuanta_vida_deja_la_pelea()
    {
        Assert.Equal(Bot.VidaParaSeguirPeleandoDe(GameLimits.ClaseMago), Bot.VidaParaSeguirConAnimo(GameLimits.ClaseMago, Animo.Prudente));
        Assert.Equal(0.35, Bot.VidaParaSeguirConAnimo(GameLimits.ClaseMago, Animo.Normal));
        Assert.Equal(0.25, Bot.VidaParaSeguirConAnimo(GameLimits.ClaseMago, Animo.Agresivo));
        Assert.Null(Bot.VidaParaSeguirConAnimo(GameLimits.ClaseGuerrero, Animo.Agresivo));
    }

    [Fact]
    public void Sale_a_buscar_al_de_nivel_escondido_solo_si_se_anima()
    {
        Assert.False(Bot.SeLoBusca(SentidosDelBot.NivelEscondido, 1, 1));
        Assert.True(Bot.SeLoBusca(SentidosDelBot.NivelEscondido, 1, 1, conEscondido: true));
    }

    [Theory]
    [InlineData(40, false, true)]
    [InlineData(10, false, false)]   // con poca habilidad casi nunca sale
    [InlineData(40, true, false)]    // montado el servidor no deja
    public void Cuando_prueba_ocultarse_de_una_criatura(int skill, bool montado, bool prueba) =>
        Assert.Equal(prueba, Bot.PruebaOcultarse(skill, montado));
}
