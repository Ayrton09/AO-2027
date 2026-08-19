using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El .dat del original escribe la MISMA clave con grafías distintas, y el VB6 no se entera porque su lector
/// normaliza: <c>clsIniManager</c> guarda con <c>UCase$</c> (clsIniManager.cls:203) y busca con <c>UCase$</c>
/// (:370). Nuestro loader comparaba string exacto contra una lista de alias escrita a mano, que se fue
/// parcheando de a un campo por vez cada vez que un bug delataba el que faltaba.
///
/// El barrido de <c>obj.dat</c> mostró el tamaño real: <b>22 de sus 98 claves tienen más de una grafía</b>
/// (<c>ObjType</c> 1119 / <c>Objtype</c> 444 / <c>objtype</c> 3, <c>Valor</c> 715 / <c>valor</c> 101…), y lo
/// que se perdía no era cosmético. Estos tests fijan los casos concretos que estaban rotos: si alguien vuelve
/// a poner una comparación sensible a mayúsculas, se caen.
/// </summary>
public class ClavesConMayusculasTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    /// <summary>92 objetos escriben <c>valor</c> en minúscula y se quedaban en precio 0. La Piel de Lobo es
    /// lo que un cazador vende: sin precio, cazar no daba una moneda.</summary>
    [Theory]
    [InlineData(414, 10)]          // Piel de Lobo — el ingreso del cazador
    [InlineData(58, 10)]           // Leña
    [InlineData(208, 20000000)]    // Manual de Liderazgo
    [InlineData(155, 1000)]        // Cama Simple
    public void El_precio_se_lee_aunque_la_clave_venga_en_minuscula(int objIndex, int valorEsperado)
    {
        var obj = LoadOrSkip().Obj(objIndex);
        Assert.NotNull(obj);
        Assert.Equal(valorEsperado, obj!.Value);
    }

    /// <summary>Los 4 Nudillos traen <c>MINHIT</c>/<c>MAXHIT</c> en mayúsculas y pegaban 0: un arma entera
    /// —la del monje— sin daño.</summary>
    [Theory]
    [InlineData(1331)]   // Nudillos de Hierro
    [InlineData(1332)]   // Nudillos de Plata
    [InlineData(1333)]   // Nudillos de Oro
    [InlineData(1354)]   // Nudillos de Hierro (Newbie)
    public void Los_nudillos_pegan(int objIndex)
    {
        var obj = LoadOrSkip().Obj(objIndex);
        Assert.NotNull(obj);
        Assert.True(obj!.MinHit > 0, $"obj {objIndex}: MinHit quedó en {obj.MinHit}");
        Assert.True(obj.MaxHit >= obj.MinHit, $"obj {objIndex}: MaxHit {obj.MaxHit} < MinHit {obj.MinHit}");
    }

    /// <summary>Los OTROS DOS que escriben <c>objtype</c> en minúscula. Son los tres únicos de todo el
    /// <c>obj.dat</c> con esa grafía, y hasta ahora sólo el Hacha tenía guardia: quedaban dos sin cubrir, que
    /// es justo el hueco por el que esta familia de bugs se vuelve a colar.
    ///
    /// <para>El 48 es <c>otCorreo</c>, así que hacerles doble clic abre el correo. Suena raro y es de la DATA,
    /// no del código: los seis objetos con ese tipo en este install son puentes y estandartes (ver la nota del
    /// buzón en <c>TestFixture</c>).</para></summary>
    [Theory]
    [InlineData(1336)]
    [InlineData(1337)]
    public void Los_puentes_tienen_su_tipo(int objIndex)
    {
        var obj = LoadOrSkip().Obj(objIndex);
        Assert.NotNull(obj);
        Assert.Equal(48, obj!.ObjType);
    }

    /// <summary>La Hacha Arrojadiza escribe <c>objtype</c> todo en minúscula. Sin tipo no es un arma para
    /// nada del server — y el propio cliente VB6 la lista entre las arrojadizas (Mod_AutoUsar.esArrojadiza).</summary>
    [Fact]
    public void La_hacha_arrojadiza_tiene_tipo_de_arma()
    {
        var obj = LoadOrSkip().Obj(980);
        Assert.NotNull(obj);
        Assert.Equal(2, obj!.ObjType);   // otWeapon
    }

    /// <summary>La Hacha (OBJ3) NO tiene la clave en la fuente, ni en una grafía ni en otra. El VB6 también la
    /// lee como 0, así que acá ser fiel es dejarla en 0: si algún día alguien "arregla" esto inventando el
    /// tipo, el port deja de coincidir con el oráculo.</summary>
    [Fact]
    public void El_hacha_sin_clave_en_la_fuente_queda_en_cero_como_en_el_VB6()
    {
        var obj = LoadOrSkip().Obj(3);
        Assert.NotNull(obj);
        Assert.Equal(0, obj!.ObjType);
    }
}
