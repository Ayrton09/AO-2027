using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// QUÉ SE LLEVA <c>QuitarEquipoNovato</c>, Y —lo que importa— QUÉ NO.
///
/// <para>La regla la pidió el tester (2.2b) y el autor la aprobó el 2026-08-17 con una condición explícita:
/// <i>"que le desaparezcan sólo los items de newbie, lo demás no"</i>. El handler recorre el inventario y
/// saltea todo lo que no tenga <c>IsNewbie</c>, así que la garantía entera descansa en ese predicado — y
/// hasta hoy <b>nada lo verificaba</b>: los tests de <c>ReglasDeLaCasaTests</c> prueban que el interruptor se
/// LEE del ini, no qué borra cuando está prendido.</para>
///
/// <para><c>IsNewbie</c> sale de la clave <c>Newbie</c> del <c>obj.dat</c> o, si no está —y en este install no
/// está en ninguna grafía—, de que el nombre termine en "(Newbies)". Derivar de un NOMBRE es exactamente la
/// clase de regla que se desborda sin avisar: alcanza con que alguien cambie el sufijo o agregue un objeto
/// mal nombrado para que la regla empiece a comerse cosas que el jugador se ganó.</para>
///
/// <para>El control más filoso está en la propia data: <b>"Poción Roja" (38) y "Poción Roja (Newbies)" (461)</b>
/// difieren sólo en el sufijo. La primera se compra y se fabrica; la segunda es del kit. Si el predicado se
/// afloja a un <c>Contains</c>, o el sufijo se normaliza de más, el jugador pierde sus pociones compradas al
/// pasar el nivel 15 y nadie se entera hasta que lo reporta.</para>
/// </summary>
public class EquipoDeNovatoTests
{
    private static GameData CargarOSaltar()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    /// <summary>Los 25 del kit, contados sobre la data real (obj.json, 1520 objetos).</summary>
    private const int ObjetosDeNovato = 25;

    [Fact]
    public void Todo_lo_marcado_como_novato_es_del_kit_y_son_veinticinco()
    {
        var data = CargarOSaltar();
        var marcados = data.Objects.Where(kv => kv.Value.IsNewbie).ToList();

        // Ni uno de más: cada marcado tiene que llevar el sufijo. Si mañana aparece la clave `Newbie` en el
        // .dat esto sigue valiendo mientras la data sea coherente; si no lo es, el test lo canta con nombre.
        var intrusos = marcados
            .Where(kv => !kv.Value.Name.TrimEnd().EndsWith("(Newbies)", System.StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{kv.Key} '{kv.Value.Name}'")
            .ToList();
        Assert.True(intrusos.Count == 0,
            "hay objetos marcados como de novato que no son del kit, y `QuitarEquipoNovato` se los llevaría: "
            + string.Join(", ", intrusos));

        Assert.Equal(ObjetosDeNovato, marcados.Count);
    }

    [Theory]
    // Lo que un jugador junta, compra o fabrica. Ninguno puede desaparecer al pasar el nivel 15.
    [InlineData(38,   "Poción Roja")]        // la MISMA poción del kit, sin el sufijo — el control filoso
    [InlineData(2,    "Espada Larga")]
    [InlineData(386,  "Lingote de Hierro")]
    [InlineData(388,  "Lingote de Oro")]
    [InlineData(1333, "Nudillos de Oro")]
    public void Lo_que_no_es_del_kit_no_se_toca(int objIndex, string queEs)
    {
        var data = CargarOSaltar();
        var o = data.Obj(objIndex);
        Assert.True(o is not null, $"{queEs} ({objIndex}) tiene que existir en la data");
        Assert.False(o!.IsNewbie, $"{queEs} ({objIndex}) NO es equipo de novato: se lo llevaría una regla de más");
    }

    [Fact]
    public void La_pocion_del_kit_y_la_comprada_son_objetos_distintos()
    {
        var data = CargarOSaltar();
        var kit = data.Obj(461);
        var comprada = data.Obj(38);
        Assert.True(kit is not null && comprada is not null);
        Assert.True(kit!.IsNewbie, "la 461 es la del kit y tiene que estar marcada");
        Assert.False(comprada!.IsNewbie, "la 38 es la que se compra y NO puede estar marcada");
    }
}
