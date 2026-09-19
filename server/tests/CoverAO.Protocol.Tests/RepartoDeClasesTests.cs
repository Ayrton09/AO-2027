using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL REPARTO DE CLASES DE LOS BOTS. Sorteando una por bot con el hash del nombre, la lista de cuarenta y ocho
/// nombres dejaba TRES CLASES EN CERO —Leñador, Carpintero y Sastre—, y el oficio del Leñador está entero en el
/// bot con sus tests en verde: escrito, probado y sin correr nunca en el juego.
/// </summary>
public class RepartoDeClasesTests
{
    private const byte Herrero = 12, Minero = 14;

    /// <summary>Están todas menos el Minero y el Herrero, una sola vez cada una: si falta alguna, esa clase no aparece
    /// nunca.</summary>
    [Fact]
    public void El_reparto_tiene_todas_las_clases_menos_el_minero_y_el_herrero_una_vez()
    {
        Assert.Equal(GameLimits.NombresDeClase.Length - 2, Bot.Reparto.Count);
        Assert.Equal(Bot.Reparto.Count, Bot.Reparto.Distinct().Count());
        for (byte clase = 1; clase <= GameLimits.NombresDeClase.Length; clase++)
            if (clase is Herrero or Minero) Assert.DoesNotContain(clase, Bot.Reparto);
            else Assert.Contains(clase, Bot.Reparto);
    }

    /// <summary>Ningún bot del poblado es Minero ni Herrero.</summary>
    [Fact]
    public void Ningun_bot_del_poblado_es_minero_ni_herrero()
    {
        var clases = Enumerable.Range(0, PobladoDeBots.Nombres.Count).Select(Bot.ClaseQueLeToca).ToList();
        Assert.DoesNotContain(Minero, clases);
        Assert.DoesNotContain(Herrero, clases);
    }

    /// <summary>
    /// Con los ocho primeros —que es con cuantos se arranca— se ejercen los oficios de juntar y uno de fabricar: el
    /// Pescador, el Leñador y el Druida. Con el sorteo viejo los ocho primeros daban Mago, Pescadora, Gladiador, Guerrera,
    /// Bardo, Clériga, Minero y Pescadora: ningún Leñador, y dos veces la misma clase.
    /// </summary>
    [Fact]
    public void Con_los_primeros_ocho_ya_se_ejercen_los_oficios()
    {
        var losOcho = Enumerable.Range(0, 8).Select(Bot.ClaseQueLeToca).ToList();
        Assert.Contains((byte)11, losOcho);   // Pescador
        Assert.Contains((byte)13, losOcho);   // Leñador
        Assert.Contains((byte)7, losOcho);    // Druida
    }

    /// <summary>Y con dieciséis hay uno de cada clase; con treinta y dos, dos. El reparto da la vuelta.</summary>
    [Fact]
    public void Con_dieciseis_hay_uno_de_cada_clase()
    {
        var deCada = Enumerable.Range(0, 16).Select(Bot.ClaseQueLeToca).GroupBy(c => c).ToList();
        Assert.Equal(16, deCada.Count);
        Assert.All(deCada, g => Assert.Single(g));

        var elDoble = Enumerable.Range(0, 32).Select(Bot.ClaseQueLeToca).GroupBy(c => c).ToList();
        Assert.Equal(16, elDoble.Count);
        Assert.All(elDoble, g => Assert.Equal(2, g.Count()));
    }

    /// <summary>Y sigue dando la vuelta más allá de los nombres que hay, que son cuarenta y ocho.</summary>
    [Fact]
    public void El_reparto_da_la_vuelta_mas_alla_del_poblado()
    {
        int vuelta = Bot.Reparto.Count;
        Assert.Equal(Bot.ClaseQueLeToca(0), Bot.ClaseQueLeToca(vuelta));
        Assert.Equal(Bot.ClaseQueLeToca(5), Bot.ClaseQueLeToca(5 + vuelta * 3));
        foreach (int i in new[] { 47, 48, 100 })
            Assert.InRange(Bot.ClaseQueLeToca(i), (byte)1, (byte)GameLimits.NombresDeClase.Length);
    }

    /// <summary>El lugar en el poblado, que es de donde sale el índice del reparto.</summary>
    [Fact]
    public void El_lugar_en_el_poblado_ubica_a_cada_nombre()
    {
        Assert.Equal(0, PobladoDeBots.LugarEnElPoblado("Aldo"));
        Assert.Equal(0, PobladoDeBots.LugarEnElPoblado("ALDO"));
        Assert.Equal(-1, PobladoDeBots.LugarEnElPoblado("NoEsUnBot"));
        for (int i = 0; i < PobladoDeBots.Nombres.Count; i++)
            Assert.Equal(i, PobladoDeBots.LugarEnElPoblado(PobladoDeBots.Nombres[i]));
    }
}
