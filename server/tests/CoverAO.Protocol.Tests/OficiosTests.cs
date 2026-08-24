using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Cuánta Minería hace falta para fundir cada mineral (VB6 <c>FundirMineral</c> + <c>ModFundicion</c>,
/// Trabajo.bas:225/944).
///
/// <para>El tester reportó que su MINERO necesitaba 90 puntos para hacer lingotes de hierro, cuando la wiki
/// del juego dice 30. Tenía razón, y la causa no es del port: el install le da el factor bueno al
/// <b>Guerrero</b>. La divergencia la decidió el autor — acá los factores cuelgan de la clase del oficio.</para>
///
/// <para>Lo que fija este archivo no es la tabla que yo escribí sino que la tabla COINCIDA CON LA DATA: los
/// <c>MinSkill</c> de <c>obj.dat</c> por los factores tienen que dar exactamente los números de la wiki, con
/// sus tres "no puede hacer" cayendo por pasarse de 100. Si mañana alguien mueve un factor, o el obj.dat
/// cambia un MinSkill, el cruce deja de cerrar y esto se pone rojo.</para>
/// </summary>
public class OficiosTests
{
    private const byte Minero = 14, Herrero = 12, Guerrero = 3, Mago = 2;
    // obj.dat: los tres minerales crudos.
    private const int Hierro = 192, Plata = 193, Oro = 194;

    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    /// <summary>Los MinSkill del obj.dat son la columna del MINERO de la wiki, tal cual. Es la premisa de
    /// todo lo demás: si esto no fuera cierto, la tabla de factores no querría decir nada.</summary>
    [Fact]
    public void The_ore_thresholds_in_the_data_are_the_miners_column()
    {
        var data = LoadOrSkip();
        Assert.Equal(30, data.Obj(Hierro)!.MinSkill);
        Assert.Equal(60, data.Obj(Plata)!.MinSkill);
        Assert.Equal(80, data.Obj(Oro)!.MinSkill);
    }

    /// <summary>La tabla entera de la wiki, mineral por mineral y clase por clase, contra la data real.</summary>
    [Theory]
    // Minero: 30 / 60 / 80.
    [InlineData(Minero, Hierro, 30)]
    [InlineData(Minero, Plata, 60)]
    [InlineData(Minero, Oro, 80)]
    // Herrero: 45 / 90 / imposible.
    [InlineData(Herrero, Hierro, 45)]
    [InlineData(Herrero, Plata, 90)]
    [InlineData(Herrero, Oro, 0)]
    // Cualquier otra clase: 90 / imposible / imposible.
    [InlineData(Guerrero, Hierro, 90)]
    [InlineData(Guerrero, Plata, 0)]
    [InlineData(Mago, Oro, 0)]
    public void Each_class_needs_exactly_what_the_wiki_says(byte clase, int mineral, int esperado)
    {
        var data = LoadOrSkip();
        int minSkill = data.Obj(mineral)!.MinSkill;
        // El primer valor de 0..100 con el que la clase puede fundir ese mineral; 0 si ninguno alcanza.
        int umbral = Enumerable.Range(1, SkillProgression.MaxSkillPoints)
            .FirstOrDefault(sk => Oficios.PuedeFundir(minSkill, sk, clase));
        Assert.Equal(esperado, umbral);
    }

    /// <summary>El 1,5 del herrero no se puede portar con división entera: la comparación va en flotante como
    /// en el original. Con `mineria / 1` entero, 44 puntos ya alcanzarían para el hierro.</summary>
    [Fact]
    public void The_blacksmith_factor_is_not_rounded_away()
    {
        Assert.False(Oficios.PuedeFundir(30, 44, Herrero));
        Assert.True(Oficios.PuedeFundir(30, 45, Herrero));
    }

    /// <summary>Y el que NO es del oficio sigue teniendo un techo real: el minero de 100 funde oro, el resto
    /// no llega ni con la skill al máximo. Sin esto, "arreglar" el factor podría haber abierto los tres
    /// minerales para todo el mundo sin que nadie lo notara.</summary>
    [Fact]
    public void Maxed_skill_is_not_a_free_pass_for_everyone()
    {
        var data = LoadOrSkip();
        int oro = data.Obj(Oro)!.MinSkill;
        Assert.True(Oficios.PuedeFundir(oro, 100, Minero));
        Assert.False(Oficios.PuedeFundir(oro, 100, Herrero));
        Assert.False(Oficios.PuedeFundir(oro, 100, Guerrero));
    }
}
