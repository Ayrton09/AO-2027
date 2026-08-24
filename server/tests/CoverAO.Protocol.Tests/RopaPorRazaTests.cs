using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Qué ropa puede usar cada raza (VB6 <c>CheckRazaUsaRopa</c>, InvUsuario.bas:1298).
///
/// El tester reportó que a un ELFO sólo podía ponerle ropa de razas bajas. La causa: los números de
/// <c>RazasProhibidas</c> del obj.dat NO son los del <c>eRaza</c> de este server —el .dat viene de Imperium
/// con otro orden— y el original los traduce al cargar con <c>QueRazaEs</c> (FileIO.bas:1028). Acá se
/// comparaba el número crudo, así que las restricciones quedaban rotadas y cada raza recibía la lista de
/// otra.
///
/// El veredicto no sale de leer el mapeo sino de la DATA: los 95 ítems marcados <c>1,3,4,6</c> son
/// exactamente los que se llaman "(Bajos)" / "de Bajo" en el obj.dat. Si la traducción está bien, esos 95
/// tienen que estar prohibidos para el humano y permitidos para el enano; si está mal, pasa al revés. Eso
/// es lo que fija este archivo, y es lo que un cambio en <c>RazaDelDat</c> no puede romper en silencio.
/// </summary>
public class RopaPorRazaTests
{
    // eRaza del server (Declares.bas:162).
    private const byte Humano = 1, Elfo = 2, Drow = 3, Gnomo = 4, Enano = 5, Orco = 6;

    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    /// <summary>La "Armadura de Cuero" (30) lleva <c>RazasProhibidas=2,5</c>, que traducido es "prohibida a
    /// enano y gnomo": ropa de razas ALTAS. Las cuatro altas se la tienen que poder poner.</summary>
    [Fact]
    public void Tall_races_can_wear_tall_armor_and_short_races_cannot()
    {
        var data = LoadOrSkip();
        var cuero = data.Obj(30);
        Assert.NotNull(cuero);
        Assert.Equal("2,5", cuero!.ForbiddenRaces.Replace(" ", ""));

        foreach (var alta in new[] { Humano, Elfo, Drow, Orco })
            Assert.False(cuero.RaceForbidden(alta), $"la raza alta {alta} tiene que poder usar la Armadura de Cuero");
        foreach (var baja in new[] { Enano, Gnomo })
            Assert.True(cuero.RaceForbidden(baja), $"la raza baja {baja} NO puede usar la Armadura de Cuero");
    }

    /// <summary>El espejo: la "Armadura de Placas Completa (Bajos)" (243) lleva <c>1,3,4,6</c>, o sea
    /// prohibida a humano, elfo, drow y orco.</summary>
    [Fact]
    public void Short_armor_is_the_mirror_image()
    {
        var data = LoadOrSkip();
        var placas = data.Obj(243);
        Assert.NotNull(placas);
        Assert.Equal("1,3,4,6", placas!.ForbiddenRaces.Replace(" ", ""));

        foreach (var alta in new[] { Humano, Elfo, Drow, Orco })
            Assert.True(placas.RaceForbidden(alta), $"la raza alta {alta} NO puede usar ropa de bajos");
        foreach (var baja in new[] { Enano, Gnomo })
            Assert.False(placas.RaceForbidden(baja), $"la raza baja {baja} tiene que poder usar ropa de bajos");
    }

    /// <summary>El caso exacto que reportó el tester, dicho al derecho: el elfo se viste como el humano y el
    /// orco, no como el enano.</summary>
    [Fact]
    public void An_elf_dresses_like_the_tall_races()
    {
        var data = LoadOrSkip();
        var armaduras = data.Objects.Values.Where(o => o.IsArmor && o.ForbiddenRaces.Length > 0).ToArray();
        Assert.True(armaduras.Length > 100, "esta data tiene que traer cientos de armaduras con restricción de raza");

        var elfo = armaduras.Where(o => !o.RaceForbidden(Elfo)).Select(o => o.Index).ToHashSet();
        var humano = armaduras.Where(o => !o.RaceForbidden(Humano)).Select(o => o.Index).ToHashSet();
        var enano = armaduras.Where(o => !o.RaceForbidden(Enano)).Select(o => o.Index).ToHashSet();

        Assert.True(elfo.SetEquals(humano), "el elfo y el humano son los dos razas altas: usan la misma ropa");
        Assert.False(elfo.Overlaps(enano) && elfo.SetEquals(enano), "el elfo NO se viste como el enano");
    }

    /// <summary>La comprobación que decide, y que no depende de haber leído bien la tabla de traducción: el
    /// obj.dat NOMBRA a la ropa de razas bajas ("(Bajos)", "de Bajo"). Cada una de esas prendas tiene que
    /// estar prohibida para el ELFO y permitida para el GNOMO.
    ///
    /// El eje se eligió midiendo, no leyendo. Saboteando la traducción a la identidad —que es exactamente el
    /// bug que había— las prendas seguían saliendo bien para el humano y para el enano: la rotación deja a
    /// esas dos razas en su lugar. Todo el defecto era que <b>el Elfo y el Gnomo tenían la ropa del otro</b>,
    /// y por eso el único par que sirve de guardia es ese.</summary>
    [Fact]
    public void Items_named_for_short_races_belong_to_the_gnome_not_the_elf()
    {
        var data = LoadOrSkip();
        var deBajos = data.Objects.Values
            .Where(o => o.ForbiddenRaces.Length > 0
                     && (o.Name.Contains("(Bajos)", System.StringComparison.OrdinalIgnoreCase)
                      || o.Name.Contains("de Bajo", System.StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(deBajos.Length >= 20, $"se esperaban decenas de prendas 'de bajos' y hay {deBajos.Length}");

        var malElfo = deBajos.Where(o => !o.RaceForbidden(Elfo)).Select(o => o.Name).ToArray();
        Assert.True(malElfo.Length == 0,
            $"ropa de bajos que el elfo puede usar: {string.Join(", ", malElfo.Take(5))}");

        var malGnomo = deBajos.Where(o => o.RaceForbidden(Gnomo)).Select(o => o.Name).ToArray();
        Assert.True(malGnomo.Length == 0,
            $"ropa de bajos que el gnomo NO puede usar: {string.Join(", ", malGnomo.Take(5))}");

        // Y el humano y el enano quedan igual con la traducción y sin ella: se dejan escritos para que el
        // próximo que mire no crea que el bug los tocaba.
        Assert.All(deBajos, o => Assert.True(o.RaceForbidden(Humano)));
        Assert.All(deBajos, o => Assert.False(o.RaceForbidden(Enano)));
    }
}
