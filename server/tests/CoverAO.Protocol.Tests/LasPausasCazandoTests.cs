using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS PAUSAS CAZANDO (DECIDIDO POR EL AUTOR, 07/10; ver <c>Bot.LaPausaDespuesDelPaseo</c>): buscando presa en el coto, el bot se queda
/// parado poco entre paseo y paseo y el rato largo le toca muy de vez en cuando; con una presa al alcance que no le dio ganas, o en un
/// pueblo, el ritmo de siempre.
/// </summary>
public class LasPausasCazandoTests
{
    [Fact]
    public void Buscando_la_pausa_es_corta()
    {
        var (min, max) = Bot.LaPausaDespuesDelPaseo(1, buscando: true);
        Assert.Equal(Bot.PausaCazandoMinMs, min);
        Assert.Equal(Bot.PausaCazandoMaxMs, max);
        Assert.True(max <= 2_000);
    }

    [Fact]
    public void Sin_buscar_la_pausa_es_la_de_siempre()
    {
        var (min, max) = Bot.LaPausaDespuesDelPaseo(1, buscando: false);
        Assert.Equal(1_500, min);
        Assert.Equal(9_000, max);
    }

    [Fact]
    public void Buscando_el_rato_largo_toca_una_de_cada_treinta_y_no_una_de_cada_ocho()
    {
        int largasBuscando = Enumerable.Range(1, 240).Count(i => Bot.LaPausaDespuesDelPaseo(i, true).Min >= 15_000);
        int largasDeSiempre = Enumerable.Range(1, 240).Count(i => Bot.LaPausaDespuesDelPaseo(i, false).Min >= 15_000);
        Assert.Equal(240 / Bot.UnaDeCadaTantasCazando, largasBuscando);
        Assert.Equal(240 / 8, largasDeSiempre);
    }
}
