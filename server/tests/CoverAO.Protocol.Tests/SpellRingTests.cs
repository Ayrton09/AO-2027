using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Requisito de ANILLO para lanzar (VB6 PuedeLanzar, modHechizos.bas:344-370). Era el último gate que le
/// faltaba a PuedeLanzar: con MinSkill, stamina y maná ya portados, un personaje que juntara el maná podía
/// tirar Apocalipsis sin tener el Anillo Espectral.
/// </summary>
public class SpellRingTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    /// <summary>Los 7 hechizos que exigen anillo en la data real, con el valor que traen.</summary>
    [Fact]
    public void The_seven_ring_spells_are_parsed()
    {
        var data = LoadOrSkip();
        var conAnillo = data.Spells.Values.Where(s => s.Anillo > 0).OrderBy(s => s.Index).ToArray();

        Assert.Equal(7, conAnillo.Length);
        // Apocalipsis(25), Implosión(34), Juicio final(52), Descarga Flamígera(69), Semillas de fuego(76),
        // Lamento de las almas(84) piden el Espectral; Lamento de la Banshee(63) pide el de las Penumbras.
        Assert.Equal(1, data.Spell(25)!.Anillo);
        Assert.Equal(1, data.Spell(34)!.Anillo);
        Assert.Equal(1, data.Spell(52)!.Anillo);
        Assert.Equal(2, data.Spell(63)!.Anillo);
        Assert.Equal(1, data.Spell(84)!.Anillo);
    }

    /// <summary>El resto de los hechizos NO pide anillo — si el parser leyera mal la clave, todo el juego de
    /// magia quedaría bloqueado.</summary>
    [Fact]
    public void Ordinary_spells_need_no_ring()
    {
        var data = LoadOrSkip();
        Assert.Equal(0, data.Spell(6)!.Anillo);    // Saeta ígnea
        Assert.Equal(0, data.Spell(9)!.Anillo);    // Paralizar
        Assert.Equal(0, data.Spell(11)!.Anillo);   // Resucitar

        int sinAnillo = data.Spells.Values.Count(s => s.Anillo == 0);
        Assert.Equal(data.Spells.Count - 7, sinAnillo);
    }

    /// <summary>
    /// Los hechizos que piden anillo son, además, los caros: es el escalón de poder que el gate protege.
    /// Sirve de sanity check de que la clave no se cruzó con otra columna del .dat.
    /// </summary>
    [Fact]
    public void Ring_spells_are_the_expensive_ones()
    {
        var data = LoadOrSkip();
        foreach (var s in data.Spells.Values.Where(s => s.Anillo > 0))
            Assert.True(s.ManaCost >= 500,
                $"'{s.Name}' pide anillo pero cuesta {s.ManaCost} de maná — ¿se leyó la columna equivocada?");
    }
}
