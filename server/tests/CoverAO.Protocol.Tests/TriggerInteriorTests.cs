using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los triggers de INTERIOR y las reglas que cuelgan de ellos.
///
/// El enum documentado (<c>eTrigger</c>, Declares.bas:244) tiene seis valores, y el transcode extraía sólo
/// esos seis: todo tile con otro valor llegaba al server como 0 ("Nada"). Pero este fork usa valores propios
/// <b>de 20 en adelante</b> para marcar los interiores, y son la mayoría — de los 90.014 tiles bajo techo de
/// los 863 mapas, 80.083 tienen un valor ≥ 20. Con el filtro viejo el server veía el 11%, y de ese dato
/// cuelgan las dos reglas de montura del original.
///
/// Estos tests miran la DATA REAL, no un fixture: Ullathorpe (mapa 1) es el caso que destapó el gap, porque
/// no tiene un solo tile con trigger 1, 2 ni 4 y tiene 1.222 con valores 20..38 — o sea que con el filtro
/// viejo el pueblo entero le parecía al server estar al aire libre, y ahí se podía andar a caballo por
/// adentro de las casas.
/// </summary>
public class TriggerInteriorTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    /// <summary>Los 1.222 tiles de interior de Ullathorpe llegan con su valor CRUDO (20..38), no aplanados.</summary>
    [Fact]
    public void Ullathorpe_trae_sus_1222_tiles_de_interior_con_el_valor_crudo()
    {
        var data = LoadOrSkip();
        int interiores = 0, antipiquete = 0, mayorValor = 0;
        for (int x = 1; x <= 100; x++)
            for (int y = 1; y <= 100; y++)
            {
                byte t = data.TileTrigger(1, x, y);
                if (t >= 20) { interiores++; if (t > mayorValor) mayorValor = t; }
                else if (t == 5) antipiquete++;
            }
        Assert.Equal(1222, interiores);
        Assert.Equal(52, antipiquete);
        Assert.Equal(38, mayorValor);        // el valor más alto del mapa 1: con el filtro viejo esto era 0
    }

    /// <summary>En un interior de Ullathorpe no se monta; en un tile de antipiquete del mismo mapa, sí.</summary>
    [Fact]
    public void En_un_interior_de_Ullathorpe_no_se_monta()
    {
        var data = LoadOrSkip();
        (int X, int Y)? interior = null, piquete = null;
        for (int x = 1; x <= 100 && (interior is null || piquete is null); x++)
            for (int y = 1; y <= 100; y++)
            {
                byte t = data.TileTrigger(1, x, y);
                if (t >= 20) interior ??= (x, y);
                else if (t == 5) piquete ??= (x, y);
            }
        Assert.NotNull(interior);
        Assert.NotNull(piquete);
        Assert.True(data.NoSeMontaAca(1, interior!.Value.X, interior.Value.Y));
        Assert.False(data.NoSeMontaAca(1, piquete!.Value.X, piquete.Value.Y));
    }

    /// <summary>El umbral de la montura es <b>&gt;= 20 pelado</b>, y no la regla de techo del cliente (que
    /// incluye 1, 2 y 4). Confundirlas cambia el gameplay: en un tile con trigger 1 o 4 el cliente desvanece
    /// el techo y aun así se puede montar.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]    // BAJOTECHO: el cliente le saca el techo, pero se monta igual
    [InlineData(2, false)]
    [InlineData(3, false)]    // POSINVALIDA
    [InlineData(4, false)]    // ZONASEGURA
    [InlineData(5, false)]    // ANTIPIQUETE
    [InlineData(6, false)]    // ZONAPELEA
    [InlineData(7, false)]    // existe en la data (57 tiles) y no traba la montura
    [InlineData(19, false)]   // el borde de abajo del umbral
    [InlineData(20, true)]    // desde acá son interiores de este fork
    [InlineData(38, true)]
    public void El_umbral_de_la_montura_es_veinte_pelado(byte trigger, bool sinMontura)
    {
        var data = new GameData();
        data.SetMapZone(77, "CAMPO");
        data.SetTileTrigger(77, 50, 50, trigger);
        Assert.Equal(sinMontura, data.NoSeMontaAca(77, 50, 50));
    }

    /// <summary>La otra mitad de la condición del original: en un DUNGEON no se monta ni con trigger 0.</summary>
    [Fact]
    public void En_un_dungeon_no_se_monta_aunque_el_tile_no_tenga_trigger()
    {
        var data = new GameData();
        data.SetMapZone(88, "DUNGEON");
        Assert.Equal(0, data.TileTrigger(88, 50, 50));
        Assert.True(data.NoSeMontaAca(88, 50, 50));
    }

    /// <summary>Poner 0 BORRA el trigger (es el default del original, no un valor guardado).</summary>
    [Fact]
    public void Poner_cero_borra_el_trigger()
    {
        var data = new GameData();
        data.SetTileTrigger(99, 10, 10, 27);
        Assert.Equal(27, data.TileTrigger(99, 10, 10));
        data.SetTileTrigger(99, 10, 10, 0);
        Assert.Equal(0, data.TileTrigger(99, 10, 10));
        Assert.False(data.NoSeMontaAca(99, 10, 10));
    }
}
