using System;
using System.IO;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// <c>EfectoFrio</c> (VB6 General.bas:854). Andar sin armadura cuesta: cada <c>IntervaloFrio</c>, en un mapa
/// de terreno NIEVE se pierde el 5% de la VIDA máxima —y puede matar—, y en cualquier otro terreno el 5% de la
/// ENERGÍA máxima.
///
/// El subsistema no existía porque el dato no llegaba: el terreno del mapa vive en el header <c>tMapDat</c> de
/// cada <c>.csm</c>, que el transcode salteaba con un <c>pos += 182</c>. Los 95 mapas nevados del install eran
/// indistinguibles de un bosque.
/// </summary>
public class ColdTests
{
    [Theory]
    // Porcentaje(máximo, 5) — no es aleatorio, a diferencia de casi todos los demás efectos del loop.
    [InlineData(100, 5)]
    [InlineData(500, 25)]
    // `Porcentaje` devuelve As Long y `/` en VB6 es de punto flotante: la conversión REDONDEA (half-to-even),
    // no trunca. Estas tres filas son justo las que distinguen una cosa de la otra — y la de 19 es el rango de
    // un personaje recién nacido, donde truncar dejaba el frío en cero daño para siempre.
    [InlineData(19, 1)]    // 0.95 -> 1
    [InlineData(30, 2)]    // 1.5  -> 2 (half-to-even: el par de arriba)
    [InlineData(10, 0)]    // 0.5  -> 0 (half-to-even: el par de abajo)
    public void El_mordisco_de_frio_es_el_5_por_ciento_del_maximo(int maximo, int esperado) =>
        Assert.Equal(esperado, Survival.ColdBite(maximo));

    [Fact]
    public void El_intervalo_esta_en_VUELTAS_del_loop_no_en_milisegundos()
    {
        // IntervaloFrio = 50 en el Server.ini del install. Son VUELTAS de 40 ms (2 s), no milisegundos: leerlo
        // como ms lo haría correr 25 veces más seguido. Es la misma trampa que se comió el resto del loop y
        // que documenta docs/unidades-del-loop.md.
        Assert.Equal(50, Survival.ColdIntervalTicks);
        Assert.Equal(2_000, Survival.ColdIntervalTicks * GameLoop.TickMs);
    }

    [Fact]
    public void Los_mapas_de_nieve_salen_del_header_del_csm()
    {
        // El transcode extrae `terrain` del tMapDat y el server sólo guarda los NIEVE: los otros tres valores
        // (BOSQUE, DESIERTO, INFRAMUNDO) no los lee ninguna rama del original.
        var data = new CoverAO.Server.Data.GameData();
        Assert.False(data.IsSnowMap(1));       // sin cargar nada, ningún mapa es de nieve
        data.SetMapSnow(217, true);
        Assert.True(data.IsSnowMap(217));
        Assert.False(data.IsSnowMap(1));
        data.SetMapSnow(217, false);
        Assert.False(data.IsSnowMap(217));
    }

    [Fact]
    public void Ningun_mapa_CARGADO_de_un_csm_es_de_nieve_porque_el_original_compara_con_el_relleno()
    {
        // El hallazgo que dio vuelta la feature. `MapInfo(Map).Terreno = MapDat.terrain` (FileIO.bas:1413) va
        // SIN Trim$ —la línea de abajo sí se lo pone a Zona— y `terrain` es un String*16 que en los .csm viene
        // relleno con ESPACIOS (los bytes del Mapa217 son b"NIEVE           "). El gate compara binario contra
        // "NIEVE" (General.bas:869), así que da False: en el original el frío SIEMPRE saca energía y no mata a
        // nadie. Sólo `/MODMAPINFO TERRENO` lo enciende, porque asigna el string ya trimmeado.
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.Objects.Count == 0) return;
        Assert.False(data.IsSnowMap(217), "el 217 viene del .csm con relleno: no matchea, como en el original");
        Assert.Equal("NIEVE", data.MapTerrain(217).TrimEnd());   // el dato SÍ está — lo que no matchea es la comparación
        Assert.NotEqual("NIEVE", data.MapTerrain(217));          // …porque conserva el relleno del String*16

        // Y el comando GM sí lo enciende, que es el único camino vivo del original.
        data.SetMapTerrain(217, "NIEVE");
        Assert.True(data.IsSnowMap(217));
    }
}

/// <summary>
/// Campos del <c>obj.dat</c> que el transcode traía en el JSON y no leía nadie. Salieron del barrido del
/// "quinto eje": de 116 claves distintas, 18 no llegaban al server; al triarlas contra la fuente VB6, sólo
/// dos tenían un call site vivo. El resto —<c>Permanente</c>, <c>ExtraTimer</c>, <c>ValorBattle</c>,
/// <c>VGrande</c>, <c>subastable</c>, <c>Map</c>/<c>X</c>/<c>Y</c>…— no lo lee ni el propio original.
/// </summary>
public class ObjDataFieldsTests
{
    private static CoverAO.Server.Data.GameData Cargar() =>
        CoverAO.Server.Data.GameData.Load();

    [Fact]
    public void Los_items_crucial_llegan_del_dat()
    {
        // El casing del .dat está mezclado ("Crucial" en 298 objetos, "crucial" en 46): leer sólo uno deja
        // fuera a un sexto de los ítems.
        var data = Cargar();
        if (data.Objects.Count == 0) return;   // sin gamedata local no hay nada que verificar
        int crucial = data.Objects.Values.Count(o => o.Crucial != 0);
        Assert.True(crucial > 200, $"esperaba ~286 objetos crucial, encontré {crucial}");
        Assert.NotEqual(0, data.Obj(2)?.Crucial ?? 0);    // Espada Larga
        Assert.NotEqual(0, data.Obj(38)?.Crucial ?? 0);   // Poción Roja
    }

    [Fact]
    public void Los_instrumentos_traen_su_sonido()
    {
        // La clave del .dat va en MAYÚSCULAS ("SND1"). Buscarla como "Snd1" da cero y el campo parece muerto:
        // fue justo lo que casi hace que se descartara.
        var data = Cargar();
        if (data.Objects.Count == 0) return;
        foreach (var (index, sonido) in new[] { (362, 48), (469, 49), (540, 51) })   // Tambor, Laúd, Flauta
        {
            var o = data.Obj((short)index);
            Assert.NotNull(o);
            Assert.True(o!.IsInstrument, $"el objeto {index} ({o.Name}) debería ser otInstrumentos=26");
            Assert.Equal(sonido, o.Snd1);
        }
    }
}

/// <summary>
/// Campos de Hechizos.dat que el transcode traía y no leía nadie. Del barrido salieron 27; al triarlos contra
/// el VB6 sólo dos tienen call site (<c>Sanacion</c> y <c>RemoverEstupidez</c>) y de esos, uno solo es
/// alcanzable con esta data.
/// </summary>
public class SpellDataFieldsTests
{
    [Fact]
    public void Sanar_apaga_la_incineracion_ademas_de_la_paralisis()
    {
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.SpellCount == 0) return;
        var sanar = data.Spell(32);
        Assert.NotNull(sanar);
        // "Sanar" trae DOS banderas. El port las evaluaba en una cadena else-if, así que entraba por la
        // parálisis y no sanaba nunca: son efectos independientes, como los `If` sueltos del original.
        Assert.True(sanar!.IsRemoveParalysis, "Sanar debería remover parálisis");
        Assert.True(sanar.IsSanacion, "Sanar debería tener el flag Sanacion");
    }

    [Fact]
    public void La_incineracion_tiene_antidoto()
    {
        // El port sabía incinerar (56 "Incinerar", 69 "Descarga Flamígera") y no tenía con qué apagarlo,
        // porque el único antídoto del install lo dice por el campo `Sanacion`.
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.SpellCount == 0) return;
        Assert.True(data.Spell(56)?.IsIncinerate ?? false);
        Assert.Contains(data.Spells.Values, sp => sp.IsSanacion);
    }

    [Fact]
    public void RemoverEstupidez_es_inalcanzable_con_esta_data()
    {
        // Tiene call site vivo (modHechizos.bas:1236) pero NINGÚN hechizo del install aplica `Estupidez`:
        // no hay forma de quedar estúpido, así que no hay nada que remover. Se documenta y no se porta —
        // el mismo veredicto que `Newbie` en obj.dat. Si algún día la data lo activa, este test se cae y avisa.
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.SpellCount == 0) return;
        var json = System.IO.File.ReadAllText(
            System.IO.Path.Combine(data.SourceDir ?? "", "Hechizos.json"));
        Assert.DoesNotContain("\"Estupidez\"", json);
    }
}


/// <summary>
/// Modificadores de atributo por RAZA (Balance.dat §MODRAZA, VB6 TCP.bas:343-347). Estaban en el JSON del
/// transcode y no los leía nadie: elegir raza era puramente cosmético.
/// </summary>
public class ModRazaTests
{
    // Los seis del install, tal cual (Balance.dat §MODRAZA): F, A, I, C, Co.
    [Theory]
    [InlineData(1, +1, +1, +1,  0, +2)]   // Humano
    [InlineData(2,  0, +2, +3, +2,  0)]   // Elfo
    [InlineData(3, +2,  0, +2, -1, +1)]   // Drow
    [InlineData(4, -5, +3, +4,  0, -1)]   // Gnomo
    [InlineData(5, +3, -1, -5, -1, +4)]   // Enano
    [InlineData(6, +5, -2, -5, -4, +4)]   // Orco
    public void Cada_raza_trae_sus_modificadores(int raza, int f, int a, int i, int c, int co)
    {
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.Objects.Count == 0) return;   // sin gamedata local no hay nada que verificar
        Assert.Equal(f,  data.ModRaza(raza, 1));
        Assert.Equal(a,  data.ModRaza(raza, 2));
        Assert.Equal(i,  data.ModRaza(raza, 3));
        Assert.Equal(c,  data.ModRaza(raza, 4));
        Assert.Equal(co, data.ModRaza(raza, 5));
    }

    [Fact]
    public void Los_atributos_que_TERMINAN_en_el_personaje_salen_de_clase_mas_raza()
    {
        // Este test antes hacía `Assert.Equal(23, 18 + data.ModRaza(6,1))`, que es una tautología sobre el .dat:
        // no tocaba `CreateCharacter` y por eso no vio que la base no era 18 sino POR CLASE (AsignarAtributos,
        // TCP.bas:309-341). Ahora se crea el personaje de verdad y se miran sus cinco atributos.
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.Objects.Count == 0) return;   // sin gamedata local no hay nada que verificar
        var store = new FileStore(Path.Combine(Path.GetTempPath(), "coverao_attr_" + Guid.NewGuid().ToString("N")));
        var db = new Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("attr_test", "pw");

        // Humano GUERRERO (clase 3 -> Case Else: 18/18/6/10/18) + Humano (+1F +1A +1I 0C +2Co).
        var guerrero = db.CreateCharacter(acc, "AttrGuerrero", 1, race: 1, gender: 1, @class: 3, homeland: 1, head: 1,
                                          newbieKit: data.NewbieKit(3), modRaza: data.ModRaza);
        Assert.Equal([19, 19, 7, 10, 20], guerrero.Attributes[1..6]);

        // ORCO GUERRERO: la misma base, con (+5,-2,-5,-4,+4). La Inteligencia queda en 1, no en 13 — es el
        // margen real contra el gate de "atributo en 0" del original.
        var orco = db.CreateCharacter(acc, "AttrOrco", 2, race: 6, gender: 1, @class: 3, homeland: 1, head: 501,
                                      newbieKit: data.NewbieKit(3), modRaza: data.ModRaza);
        Assert.Equal([23, 16, 1, 6, 22], orco.Attributes[1..6]);

        // MAGO GNOMO (clase 2 -> 6/18/18/10/18) + Gnomo (-5,+3,+4,0,-1): la Fuerza toca 1.
        var mago = db.CreateCharacter(acc, "AttrMago", 3, race: 4, gender: 1, @class: 2, homeland: 1, head: 401,
                                      newbieKit: data.NewbieKit(2), modRaza: data.ModRaza);
        Assert.Equal([1, 21, 22, 10, 17], mago.Attributes[1..6]);

        // Y el derivado: el maná del Mago sale de la Inteligencia YA con el bono racial (22*3 = 66), no de 18.
        Assert.Equal(66, mago.MaxMana);
    }

    [Fact]
    public void Una_raza_desconocida_no_da_bono_ni_revienta()
    {
        var data = CoverAO.Server.Data.GameData.Load();
        Assert.Equal(0, data.ModRaza(99, 1));
        Assert.Equal(0, data.ModRaza(1, 99));
    }
}


/// <summary>
/// Las coordenadas de <see cref="CoverAO.Server.Game.Cities"/> están HARDCODEADAS, no se cargan de
/// Ciudades.dat. Hoy coinciden con la data —se verificó las 12 ciudades más prisión y libertad— así que no
/// hay bug de comportamiento, pero nada impedía que divergieran en silencio: cambiar el .dat de un install
/// no movería un solo tile y los jugadores nacerían y revivirían en el lugar equivocado.
///
/// Este test es esa red: compara el hardcode contra el JSON del transcode. Si alguien toca uno de los dos,
/// se cae acá y no en producción. Es la alternativa barata a escribir un loader para data que en la práctica
/// no cambia (última entrada del barrido del "quinto eje").
/// </summary>
public class CiudadesTests
{
    private static readonly (byte Id, string Seccion)[] Ciudades =
    {
        (1, "NIX"), (2, "ILLIANDOR"), (3, "ULLATHORPE"), (4, "BANDERBILL"), (5, "RINKEL"),
        (6, "DUNGEONNEWBIE"), (7, "LINDOS"), (8, "ARGHAL"), (9, "TIAMA"), (10, "ORAC"),
        (11, "SURAMEI"), (12, "NUEVA"),
    };

    private static System.Text.Json.JsonElement? Data()
    {
        string ruta = System.IO.Path.Combine(CoverAO.Server.Data.GameData.Load().SourceDir ?? "", "Ciudades.json");
        if (!System.IO.File.Exists(ruta)) return null;
        var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(ruta));
        return doc.RootElement.TryGetProperty("meta", out var m) ? m : doc.RootElement;
    }

    private static int Campo(System.Text.Json.JsonElement sec, string clave) =>
        int.Parse(sec.GetProperty(clave).GetString()!);

    [Fact]
    public void El_hardcode_de_ciudades_coincide_con_el_dat()
    {
        if (Data() is not { } raiz) return;
        foreach (var (id, seccion) in Ciudades)
        {
            Assert.True(raiz.TryGetProperty(seccion, out var sec), $"falta la sección {seccion} en Ciudades.json");
            var spawn = CoverAO.Server.Game.Cities.SpawnTile(id);
            var dead = CoverAO.Server.Game.Cities.DeadTile(id);
            Assert.Equal((Campo(sec, "MAPA"), Campo(sec, "X"), Campo(sec, "Y")),
                         ((int)spawn.Map, (int)spawn.X, (int)spawn.Y));
            Assert.Equal((Campo(sec, "MAPA_DEAD"), Campo(sec, "X_DEAD"), Campo(sec, "Y_DEAD")),
                         ((int)dead.Map, (int)dead.X, (int)dead.Y));
        }
    }

    [Fact]
    public void La_prision_y_la_libertad_tambien()
    {
        if (Data() is not { } raiz) return;
        foreach (var (seccion, pos) in new[]
                 { ("PRISION", CoverAO.Server.Game.Cities.Prision), ("LIBERTAD", CoverAO.Server.Game.Cities.Libertad) })
        {
            Assert.True(raiz.TryGetProperty(seccion, out var sec));
            Assert.Equal((Campo(sec, "MAPA"), Campo(sec, "X"), Campo(sec, "Y")),
                         ((int)pos.Map, (int)pos.X, (int)pos.Y));
        }
    }
}
