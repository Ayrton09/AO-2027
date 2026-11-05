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

    /// <summary>Están todas, una sola vez cada una: si falta alguna, esa clase no aparece
    /// nunca.</summary>
    [Fact]
    public void El_reparto_tiene_todas_las_clases_una_vez()
    {
        Assert.Equal(GameLimits.NombresDeClase.Length, Bot.Reparto.Count);
        Assert.Equal(Bot.Reparto.Count, Bot.Reparto.Distinct().Count());
        for (byte clase = 1; clase <= GameLimits.NombresDeClase.Length; clase++)
            Assert.Contains(clase, Bot.Reparto);
    }

    /// <summary>El poblado incluye los dos eslabones de la metalurgia.</summary>
    [Fact]
    public void El_poblado_incluye_mineros_y_herreros()
    {
        var clases = Enumerable.Range(0, PobladoDeBots.Nombres.Count).Select(Bot.ClaseQueLeToca).ToList();
        Assert.Contains(Minero, clases);
        Assert.Contains(Herrero, clases);
    }

    [Fact]
    public void Los_poblados_guardados_suman_los_oficios_con_suplentes_sin_cambiar_personajes()
    {
        var anteriores = PobladoDeBots.Nombres.ToDictionary(n => n,
            n => Bot.Reparto[PobladoDeBots.LugarEnElPoblado(n) % 16]);
        var elegidos = PobladoDeBots.NombresParaArrancar(96,_=>false,n=>anteriores.TryGetValue(n,out var c)?c:null);
        byte Clase(string n) => anteriores.TryGetValue(n,out var c)?c:Bot.ClaseQueLeToca(PobladoDeBots.LugarEnElPoblado(n));
        Assert.Equal(96,elegidos.Count);Assert.Equal(96,elegidos.Distinct().Count());
        Assert.Contains(elegidos,n=>Clase(n)==Minero);Assert.Contains(elegidos,n=>Clase(n)==Herrero);
        Assert.Equal(2,elegidos.Count(n=>!anteriores.ContainsKey(n)));
        Assert.DoesNotContain(Minero,anteriores.Values);Assert.DoesNotContain(Herrero,anteriores.Values);
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

    /// <summary>Con dieciocho hay uno de cada clase; con treinta y seis, dos.</summary>
    [Fact]
    public void Con_dieciocho_hay_uno_de_cada_clase()
    {
        var deCada = Enumerable.Range(0, 18).Select(Bot.ClaseQueLeToca).GroupBy(c => c).ToList();
        Assert.Equal(18, deCada.Count);
        Assert.All(deCada, g => Assert.Single(g));

        var elDoble = Enumerable.Range(0, 36).Select(Bot.ClaseQueLeToca).GroupBy(c => c).ToList();
        Assert.Equal(18, elDoble.Count);
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
