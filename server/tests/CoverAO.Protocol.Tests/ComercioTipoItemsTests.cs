using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El gate de la VENTA a un mercader (VB6 <c>Comercio.bas:123</c>): <c>TipoItems</c> decide qué te compra.
///
/// Se testea la REGLA, no que el campo se cargue. El transcode ya extraía <c>TipoItems</c> —está en los 82
/// mercaderes del <c>NPCs.dat</c>— y el server nunca lo leía, así que cualquier NPC te compraba cualquier
/// cosa: al vendedor de manzanas se le vendía una armadura.
///
/// Los otros cinco gates de la misma cadena <c>ElseIf</c> del original NO se portan y acá queda el porqué:
/// <c>Newbie</c>, <c>Shop</c>, <c>Real</c>, <c>Caos</c> y <c>Milicia</c> aparecen CERO veces en el
/// <c>obj.dat</c> crudo de este install, y los mercaderes que los atienden ("SR", "SC", "SM") no existen
/// en el <c>NPCs.dat</c>. Son ramas inalcanzables, no deuda.
/// </summary>
public class ComercioTipoItemsTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    private static NpcInfo PorNombre(GameData d, string nombre)
    {
        foreach (var n in d.Npcs.Values) if (n.Name == nombre) return n;
        throw new Xunit.Sdk.XunitException($"no existe el NPC '{nombre}' en la data");
    }

    /// <summary>Un NPC sintético: sólo importan TipoItems y que el resto compile.</summary>
    private static NpcInfo Mercader(int tipoItems) =>
        new(1, "mercader de prueba", 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, TipoItems: tipoItems);

    [Fact]
    public void La_data_trae_TipoItems_en_82_mercaderes_y_5_compran_de_todo()
    {
        var d = LoadOrSkip();
        int con = 0, cualquiera = 0;
        foreach (var n in d.Npcs.Values)
        {
            if (n.TipoItems == 0) continue;
            con++;
            if (n.TipoItems == GameLimits.TipoItemsCualquiera) cualquiera++;
        }
        // Contado sobre el NPCs.dat CRUDO del install: 82 apariciones de la clave, 5 con el valor 1000.
        Assert.Equal(82, con);
        Assert.Equal(5, cualquiera);
    }

    [Fact]
    public void El_herrero_compra_armas_y_no_comida()
    {
        var d = LoadOrSkip();
        var herrero = PorNombre(d, "HerreriaGeneral");
        Assert.Equal(2, herrero.TipoItems);

        var espada = d.Obj(2);    // "Espada Larga"
        var manzana = d.Obj(1);   // "Manzana Roja"
        Assert.NotNull(espada);
        Assert.NotNull(manzana);
        Assert.Equal(2, espada!.ObjType);
        Assert.Equal(1, manzana!.ObjType);

        Assert.True(herrero.CompraObjeto(espada.ObjType, espada.Index));
        Assert.False(herrero.CompraObjeto(manzana.ObjType, manzana.Index));
    }

    [Fact]
    public void El_de_comidas_compra_comida_y_no_armas()
    {
        var d = LoadOrSkip();
        var comidas = PorNombre(d, "Comidas");
        Assert.Equal(1, comidas.TipoItems);

        Assert.True(comidas.CompraObjeto(d.Obj(1)!.ObjType, 1));
        Assert.False(comidas.CompraObjeto(d.Obj(2)!.ObjType, 2));
    }

    [Fact]
    public void OtCualquiera_compra_de_todo_menos_el_oro()
    {
        var universal = Mercader(GameLimits.TipoItemsCualquiera);
        for (int t = 1; t <= 60; t++) Assert.True(universal.CompraObjeto(t, 999));
        // El oro es el segundo término de la condición y NO depende del TipoItems.
        Assert.False(universal.CompraObjeto(1, GameLimits.ObjOro));
    }

    [Fact]
    public void Un_npc_sin_la_clave_no_compra_nada()
    {
        // TipoItems = 0 y ningún ObjType vale 0: al que no comercia no se le vende, igual que el original.
        var inerte = Mercader(0);
        for (int t = 1; t <= 60; t++) Assert.False(inerte.CompraObjeto(t, 999));
    }

    [Fact]
    public void El_oro_no_se_le_vende_ni_al_que_compra_justo_ese_tipo()
    {
        var d = LoadOrSkip();
        var oro = d.Obj(GameLimits.ObjOro);
        Assert.NotNull(oro);
        // El `Or ObjIndex = iORO` del VB6 está FUERA del paréntesis, así que gana sobre la coincidencia
        // de tipo: un mercader de ese mismo ObjType igual rechaza el oro.
        var quienSea = Mercader(oro!.ObjType);
        Assert.True(quienSea.CompraObjeto(oro.ObjType, 999));
        Assert.False(quienSea.CompraObjeto(oro.ObjType, GameLimits.ObjOro));
    }
}
