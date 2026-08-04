using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Perder el inventario al morir (VB6 UserDie -> TirarTodo, InvUsuario.bas:2147). Antes morir era gratis:
/// el cadáver conservaba TODO, así que un mapa PK no se distinguía de una ciudad.
///
/// El camino feliz (matar a alguien y ver los ítems en el suelo) lo cubre `drop_on_death_flow.py` contra
/// el servidor real. Acá se fijan las dos COMPUERTAS y la regla de qué ítem cae, que son las que deciden
/// si el jugador pierde sus cosas o no — y se verifican contra la data transcodeada de verdad.
/// </summary>
public class DropOnDeathTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    /// <summary>
    /// La regla efectiva del original es <c>Pk = (battle_mode == 0) &amp;&amp; map != 457</c>. El chequeo de
    /// zona "CIUDAD" que trae FileIO.bas:1420 es código muerto: la línea siguiente lo pisa. Lo que protege a
    /// las ciudades son sus tiles ZONASEGURA, no este flag.
    /// </summary>
    [Fact]
    public void Cities_are_safe_and_open_field_is_not()
    {
        var data = LoadOrSkip();
        Assert.False(data.IsPkMap(1), "Ullathorpe (battle_mode 1) no puede ser Pk");
        Assert.False(data.IsPkMap(34), "Nix no puede ser Pk");
        Assert.False(data.IsPkMap(62), "Lindos no puede ser Pk");
        Assert.False(data.IsPkMap(49), "Intermundia no puede ser Pk");
        Assert.False(data.IsPkMap(457), "el mapa 457 está forzado a seguro en FileIO.bas:1428");
        Assert.True(data.IsPkMap(15), "el Desierto Rinkel (battle_mode 0) sí es Pk");
    }

    /// <summary>Los mapas seguros que sale de battle_mode tienen que INCLUIR la lista que el propio VB6 dejó
    /// hardcodeada (y comentada) dentro de TirarTodo. Es la validación cruzada de que la regla es la correcta:
    /// dos fuentes independientes del original coinciden.</summary>
    [Fact]
    public void Safe_maps_cover_the_hardcoded_list_in_the_original()
    {
        var data = LoadOrSkip();
        int[] hardcoded = { 1, 34, 49, 62, 63, 64, 58, 59, 60, 61, 111, 112, 179, 180, 181, 20, 37 };
        var pkAnyway = hardcoded.Where(data.IsPkMap).ToArray();
        Assert.True(pkAnyway.Length == 0,
            $"estos mapas están en la lista de TirarTodo pero salen Pk: {string.Join(", ", pkAnyway)}");
    }

    [Fact]
    public void Map_safety_can_be_flipped_at_runtime()
    {
        var data = LoadOrSkip();
        Assert.True(data.IsPkMap(15));
        data.SetMapSafe(15, true);
        Assert.False(data.IsPkMap(15));
        data.SetMapSafe(15, false);
        Assert.True(data.IsPkMap(15));
    }

    /// <summary>
    /// ItemSeCae (InvUsuario.bas:2175): cae si NoSeCae == 0 y no es llave (otLlaves 9), barco (otBarcos 31)
    /// ni montura (otMonturas 44). Contra la data real: 1520 objetos, sólo 5 con NoSeCae.
    /// </summary>
    [Fact]
    public void Keys_boats_and_mounts_never_drop()
    {
        var data = LoadOrSkip();
        foreach (var o in data.Objects.Values.Where(o => o.IsKey || o.IsBoat || o.IsMount))
            Assert.False(o.DropsOnDeath, $"'{o.Name}' (tipo {o.ObjType}) no se puede caer al morir");
    }

    [Fact]
    public void Items_flagged_nosecae_never_drop()
    {
        var data = LoadOrSkip();
        var flagged = data.Objects.Values.Where(o => o.NoSeCae != 0).ToArray();
        Assert.True(flagged.Length > 0, "la data real trae objetos con NoSeCae");
        foreach (var o in flagged)
            Assert.False(o.DropsOnDeath, $"'{o.Name}' tiene NoSeCae y no puede caerse");
    }

    /// <summary>Y el complemento: el equipo corriente SÍ se cae, que es lo que le pone riesgo a morir.
    /// Si esto se rompiera, `DropsOnDeath` estaría devolviendo false para todo y nadie perdería nada.</summary>
    [Fact]
    public void Ordinary_gear_does_drop()
    {
        var data = LoadOrSkip();
        var espada = data.Obj(2);
        Assert.NotNull(espada);
        Assert.True(espada!.DropsOnDeath, $"'{espada.Name}' tiene que caerse al morir");
    }

    /// <summary>
    /// El conjunto que NO se cae tiene que ser EXACTAMENTE el que describe ItemSeCae: llaves, barcos,
    /// monturas y lo marcado con NoSeCae. Nada más y nada menos — así el test no depende de un umbral
    /// arbitrario y detecta tanto que se pierda una exención como que aparezca una de más.
    /// </summary>
    [Fact]
    public void The_only_exempt_items_are_keys_boats_mounts_and_nosecae()
    {
        var data = LoadOrSkip();
        var noCaen = data.Objects.Values.Where(o => !o.DropsOnDeath).ToArray();
        var esperados = data.Objects.Values
            .Where(o => o.IsKey || o.IsBoat || o.IsMount || o.NoSeCae != 0).ToArray();

        Assert.Equal(esperados.Length, noCaen.Length);
        var sobran = noCaen.Except(esperados).ToArray();
        Assert.True(sobran.Length == 0,
            $"no se caen sin motivo: {string.Join(", ", sobran.Select(o => $"{o.Index} '{o.Name}' tipo {o.ObjType}"))}");
    }
}
