using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// ¿PEGA CON EL ARMA O LANZA? (<c>Bot.PegaConElArma</c>) El que sabía magia peleaba siempre con magia, y los que pegan fuerte se
/// vaciaban de maná y se sentaban a meditar: Milo, Paladín de nivel 24 con el Martillo de Guerra, saca unos 97 por golpe contra un
/// Zombi y lanzaba el Misil Mágico, que le saca 40 (rondas 68 y 69). Los números de los casos son los de esas rondas.
/// </summary>
public class ElArmaOElHechizoTests
{
    [Theory]
    [InlineData(97.0, 40.0, true)]    // Milo
    [InlineData(49.0, 45.0, true)]    // Elmo, Clérigo
    [InlineData(47.0, 43.0, true)]    // Kael, Nigromante
    [InlineData(67.0, 37.0, true)]    // Juana, Asesina
    [InlineData(27.0, 47.0, false)]   // Nadia, Bardo
    [InlineData(15.0, 45.0, false)]   // Hilda, Druida
    [InlineData(19.0, 50.0, false)]   // Bruma, Maga
    [InlineData(40.0, 50.0, true)]    // justo en el 80 %
    [InlineData(39.0, 50.0, false)]
    public void Pega_con_el_arma_si_saca_al_menos_el_80_por_ciento_del_hechizo(double golpe, double hechizo, bool arma) =>
        Assert.Equal(arma, Bot.PegaConElArma(golpe, hechizo, 0));

    [Fact]
    public void Sin_saber_su_golpe_prueba_el_arma() => Assert.True(Bot.PegaConElArma(null, 50, 50));

    [Fact]
    public void Sin_saber_el_hechizo_va_por_su_ficha()
    {
        Assert.False(Bot.PegaConElArma(30, null, 50));
        Assert.True(Bot.PegaConElArma(40, null, 50));
    }

    [Fact]
    public void El_golpe_se_juzga_con_diez_golpes_de_la_misma_arma_y_contra_la_defensa_de_la_otra()
    {
        var golpe = new Bot.GolpeMedido();
        golpe.Anotar(arma: 5, golpes: 9, entraron: 9, dano: 900, defensa: 10);
        Assert.Null(golpe.DanoPorGolpe(5, 10));
        golpe.Anotar(arma: 5, golpes: 1, entraron: 0, dano: 0, defensa: 10);
        // Diez golpes, nueve entraron con 110 antes de la defensa: contra defensa 12, 9 × 98 / 10.
        Assert.Equal(88.2, golpe.DanoPorGolpe(5, 12)!.Value, 6);
        Assert.Null(golpe.DanoPorGolpe(6, 12));
    }

    [Fact]
    public void El_hechizo_se_juzga_con_tres_lanzamientos_y_recuerda_los_ultimos_veinte()
    {
        var hechizos = new Bot.HechizoMedido();
        hechizos.Anotar(10, 30);
        hechizos.Anotar(10, 50);
        Assert.Null(hechizos.DanoPorLanzamiento(10));
        hechizos.Anotar(10, 40);
        Assert.Equal(40.0, hechizos.DanoPorLanzamiento(10));
        for (int i = 0; i < 20; i++) hechizos.Anotar(10, 100);
        Assert.Equal(100.0, hechizos.DanoPorLanzamiento(10));
        Assert.Null(hechizos.DanoPorLanzamiento(11));
    }
}
