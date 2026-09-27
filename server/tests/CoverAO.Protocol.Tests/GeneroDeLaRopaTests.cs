using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA ROPA DE MUJER, SÓLO PARA MUJERES. Reporte del tester del 2026-09-12 ("probé personajes femeninos… poner que
/// las mujeres sólo puedan usar armaduras y ropa de mujeres") y decisión del autor.
///
/// <para><b>Esto NO es paridad con el original, y el test lo fija a propósito.</b> El VB6 tiene la regla
/// (<c>SexoPuedeUsarItem</c>, InvUsuario.bas:839) pero la alimenta con las claves <c>Mujer</c>/<c>Hombre</c> del
/// obj.dat, que no existen en esta data: allá no rechaza nada. Acá se respeta la clave <c>Genero</c>, que el VB6 no
/// lee. Lo que sí se conserva del original: sólo la armadura de CUERPO y el mensaje 267.</para>
///
/// <para>Y DESDE EL 27/09, LA ROPA DE HOMBRE SÓLO PARA HOMBRES (otro reporte del tester, otra decisión del autor): lo que
/// no lleva marca es de hombre, salvo las túnicas y las Tribales de novato de la Orca (<c>Equipment.GeneroDeLaPrenda</c>).</para>
/// </summary>
public class GeneroDeLaRopaTests
{
    private const int Hombre = 1, Mujer = 2;   // eGenero (Declares.bas:173)

    private static GameData Datos()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita la gamedata en disco");
        return data;
    }

    /// <summary>La regla sola: con género 0 la usa cualquiera —las túnicas, y lo que no es de cuerpo—, con género sólo ese.</summary>
    [Theory]
    // marca de la prenda | género del jugador | ¿puede?
    [InlineData(0, Hombre, true)]     // sin marca: el `Else … = True` del original
    [InlineData(0, Mujer, true)]
    [InlineData(Mujer, Mujer, true)]
    [InlineData(Mujer, Hombre, false)]   // el caso del tester
    [InlineData(Hombre, Hombre, true)]   // no hay ninguna en la data, pero la regla lo contempla
    [InlineData(Hombre, Mujer, false)]
    public void Sin_marca_la_usa_cualquiera_y_con_marca_solo_ese_genero(int prenda, int jugador, bool puede)
    {
        Assert.Equal(puede, Equipment.PuedeUsarloSuGenero(prenda, jugador));
    }

    /// <summary>
    /// Y CONTRA LA DATA DE VERDAD: las 43 prendas marcadas son todas de mujer y todas armadura de CUERPO, así que la
    /// regla tiene dónde morder y no toca ni un casco ni un escudo.
    /// </summary>
    [Fact]
    public void Las_prendas_marcadas_son_de_mujer_y_todas_de_cuerpo()
    {
        var datos = Datos();
        var marcadas = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i))
                                 .Where(o => o is { Gender: > 0 }).ToList();

        Assert.True(marcadas.Count >= 40, $"la data trae {marcadas.Count} prendas con género: el test no prueba nada");
        Assert.All(marcadas, o => Assert.Equal(Mujer, o!.Gender));      // no hay una sola de hombre en esta data
        Assert.All(marcadas, o => Assert.True(o!.IsArmor,
            $"'{o.Name}' tiene género pero no es armadura de cuerpo: el chequeo del original no la miraría"));
        Assert.All(marcadas, o => Assert.False(Equipment.PuedeUsarloSuGenero(o!.Gender, Hombre)));
        Assert.All(marcadas, o => Assert.True(Equipment.PuedeUsarloSuGenero(o!.Gender, Mujer)));
    }

    /// <summary>
    /// EL GÉNERO SE MIRA SÓLO EN LA ARMADURA DE CUERPO, como el original: un casco o un escudo con marca —que en esta
    /// data no existen, pero un obj.dat editado sí podría traer— no se rechazan. Es la rama de `SexoPuedeUsarItem`.
    /// </summary>
    [Fact]
    public void El_genero_solo_se_mira_en_la_armadura_de_cuerpo()
    {
        var datos = Datos();
        var todos = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i)).Where(o => o is not null).ToList();
        var armadura = todos.First(o => o!.IsArmor);
        var casco = todos.First(o => o!.IsHelmet);
        var escudo = todos.First(o => o!.IsShield);
        var arma = todos.First(o => o!.IsWeapon);

        Assert.True(Equipment.MiraElGenero(armadura!), $"'{armadura!.Name}' es armadura de cuerpo y tiene que mirarlo");
        Assert.False(Equipment.MiraElGenero(casco!), $"'{casco!.Name}' es un casco: el original no le mira el género");
        Assert.False(Equipment.MiraElGenero(escudo!), $"'{escudo!.Name}' es un escudo: el original no le mira el género");
        Assert.False(Equipment.MiraElGenero(arma!), $"'{arma!.Name}' es un arma: el original no le mira el género");
    }

    /// <summary>
    /// LAS TÚNICAS LAS USA CUALQUIERA, que es lo que dijo el tester: no existe la túnica de mujer ni la de hombre.
    /// De las 57 del catálogo, 56 no llevan marca; la única que la lleva es la "Túnica Reforzada (Bajas)".
    /// </summary>
    [Fact]
    public void Las_tunicas_no_tienen_genero()
    {
        var datos = Datos();
        var tunicas = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i))
                                .Where(o => o is not null && o.Name.StartsWith("T", System.StringComparison.OrdinalIgnoreCase)
                                            && o.Name.Contains("nica", System.StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(tunicas.Count >= 50, $"aparecieron {tunicas.Count} túnicas: el test no prueba nada");
        int conMarca = tunicas.Count(o => o!.Gender > 0);
        Assert.True(conMarca <= 1, $"{conMarca} túnicas traen género; se esperaba a lo sumo una");
        Assert.True(tunicas.Count(o => Equipment.PuedeUsarloSuGenero(o!.Gender, Hombre)) >= tunicas.Count - 1,
            "un hombre dejó de poder ponerse las túnicas");
    }

    /// <summary>
    /// LA ROPA DE HOMBRE, SÓLO PARA HOMBRES (decisión del autor del 2026-09-27, sobre otro reporte del tester: "en todas las
    /// ropas, las de hombre se las pueden poner las mujeres"). La data no marca las de hombre: son las que no son de mujer.
    /// </summary>
    [Fact]
    public void La_armadura_sin_marca_es_de_hombre()
    {
        var datos = Datos();
        var cuero = datos.Obj(30);
        Assert.True(cuero is { IsArmor: true, Gender: 0 }, "premisa: la Armadura de Cuero es de cuerpo y no trae marca");
        Assert.Equal(Hombre, Equipment.GeneroDeLaPrenda(cuero!));
        Assert.False(Equipment.PuedeUsarloSuGenero(Equipment.GeneroDeLaPrenda(cuero!), Mujer), "una mujer se pone la de hombre");
        Assert.True(Equipment.PuedeUsarloSuGenero(Equipment.GeneroDeLaPrenda(cuero!), Hombre));
    }

    /// <summary>
    /// Y CONTRA TODA LA DATA: una mujer se pone las de mujer, las túnicas y las Tribales de novato de la Orca, y nada más; un
    /// hombre, todo menos las de mujer. Cascos, escudos y armas no tienen género.
    /// </summary>
    [Fact]
    public void La_mujer_se_pone_las_de_mujer_las_tunicas_y_las_tribales()
    {
        var datos = Datos();
        var todos = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i)).Where(o => o is not null).Select(o => o!).ToList();
        var deCuerpo = todos.Where(o => o.IsArmor).ToList();
        Assert.True(deCuerpo.Count >= 200, $"la data trae {deCuerpo.Count} armaduras de cuerpo: el test no prueba nada");
        Assert.True(deCuerpo.Count(Equipment.EsTunica) >= 50, "no aparecieron las túnicas: el test no prueba nada");

        foreach (var o in deCuerpo)
        {
            bool deMujer = o.Gender == Mujer, deLasDos = Equipment.EsTunica(o) || o.Index == Equipment.TribalesDeNovato;
            int genero = Equipment.GeneroDeLaPrenda(o);
            Assert.True((deMujer || deLasDos) == Equipment.PuedeUsarloSuGenero(genero, Mujer), $"'{o.Name}' ({o.Index}) para una mujer");
            Assert.True(!deMujer == Equipment.PuedeUsarloSuGenero(genero, Hombre), $"'{o.Name}' ({o.Index}) para un hombre");
        }
        Assert.All(todos.Where(o => o.IsHelmet || o.IsShield || o.IsWeapon), o => Assert.Equal(0, Equipment.GeneroDeLaPrenda(o)));
    }
}
