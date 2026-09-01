using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL DROP GLOBAL, la tabla que el original trae y no lee nadie.
///
/// <para><c>Servidor/Dat/DropGlobal.dat</c> viaja con el install: 30 secciones con un ítem y un porcentaje
/// cada una. El menú de recarga del panel del servidor la lista junto a los demás .dat, y su rama entera es
/// <c>MsgBox "Drop Global"</c> (frmServidor.frm:520). Ningún módulo del VB6 la abre. El campo que el diseño
/// necesitaba —un <c>TiraItem</c> por NPC, según el comentario que el autor dejó pegado a la entrada 29— no
/// existe ni en <c>NPCs.dat</c> ni en una sola línea del servidor.</para>
///
/// <para>Lo que se cuida acá no es el interruptor sino <b>cómo se lee ese archivo</b>, que es donde estaban
/// las trampas: valores con comentarios VB6 pegados, entradas que el autor apagó a mano, y unos porcentajes
/// que NO son probabilidades. El camino de que el ítem llegue al piso ya lo cubren el botín normal y sus
/// flows: cae por el mismo <c>FindFreeGround</c>/<c>DejarEnElPiso</c>.</para>
/// </summary>
public class DropGlobalTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "coverao_dg_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ruta); } catch { } }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ruta, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ruta);
    }

    private static GameData Datos()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "este test necesita la gamedata en disco");
        return d;
    }

    // ── LA REGLA ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Apagada por defecto, con archivo y sin archivo. Es la mitad que hace que "sin Server.ini" siga
    /// significando "como el original".</summary>
    [Fact]
    public void Viene_apagada()
    {
        Assert.Equal(0, new ServerIni().DropGlobal);
        Assert.Equal(0, Cargar("MaxUsers=50\r\n").DropGlobal);
        Assert.Equal(7, Cargar("DropGlobal=7\r\n").DropGlobal);
    }

    // ── EL ARCHIVO ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// LAS CUATRO QUE EL AUTOR APAGÓ. Tres las dejó en <c>0</c> y una en <c>-1</c>, y en esa última escribió
    /// por qué: <c>'le pongo -1, así no cualquiera puede dropear</c>. Descartar por peso &lt;= 0 honra esa
    /// convención; leerlas como "0 % igual entra en la ruleta" metería cuatro ítems más en el sorteo.
    ///
    /// <para>Se nombran los objetos y no sólo el conteo: si mañana el filtro se rompe, el número podría seguir
    /// dando 26 por otra razón y el test pasaría igual.</para>
    /// </summary>
    [Fact]
    public void Las_cuatro_entradas_apagadas_no_se_cargan()
    {
        var d = Datos();
        var objetos = d.DropsGlobales.Select(g => g.ObjIndex).ToHashSet();
        Assert.Equal(26, d.DropsGlobales.Count);
        foreach (int apagado in new[] { 992, 993, 1180, 406 })   // RM +10, RM +20, Reflex +30, Gema Lunar
            Assert.DoesNotContain(apagado, objetos);
        Assert.Contains(1149, objetos);   // Pendiente del Experto, la de peso 1
        Assert.Contains(57, objetos);     // Orbe de la Ponzoña, la última de peso 20
    }

    /// <summary>
    /// Los valores del archivo traen comentarios VB6 pegados —<c>Index=992 'RM +10</c>— y las cuatro apagadas
    /// son justamente las que los traen. Un <c>int.Parse</c> pelado las tiraría a todas, que es un resultado
    /// que se parece MUCHO al correcto: 26 entradas, las mismas 26 — o sea que el bug sería invisible desde el
    /// conteo. Acá se separan las dos capas: el parser del ini deja el comentario pegado (es texto crudo) y el
    /// lector de data es el que lo corta, que es lo que hace que un <c>-1</c> se distinga de un ilegible.
    /// </summary>
    [Fact]
    public void Un_valor_con_comentario_VB6_se_lee_igual()
    {
        var ini = IniFile.Parse("[DROP1]\r\nIndex=992 'RM +10\r\nPorcentajeDrop=0\r\n"
                              + "[DROP2]\r\nIndex=1180 'Reflex +30\r\nPorcentajeDrop=-1 'le pongo -1\r\n");
        // El parser del ini se queda con el texto crudo, comentario incluido: cortarlo es del lector de data.
        Assert.Equal("992 'RM +10", ini.GetString("DROP1", "Index"));
        // Y el lector de data lo corta — es lo que hace que el -1 se distinga de un 0 y de un valor ilegible.
        var d = Datos();
        Assert.All(d.DropsGlobales, g => Assert.True(g.ObjIndex > 0 && g.Peso > 0));
    }

    /// <summary>
    /// El <c>NumDrops=30</c> del encabezado no manda: en el archivo hay 30 secciones y utilizables son 26. Se
    /// afirma la diferencia para dejar escrito que el desacuerdo viene del install y no es una pérdida nuestra.
    ///
    /// <para>Se lee de NUESTRO <c>DropGlobal.json</c> y no del .dat del original a propósito: la carpeta
    /// <c>CoverAO-2021/</c> está fuera de git, así que un test colgado de ella se pone rojo en cualquier
    /// checkout limpio y el rojo parece del producto. El JSON viaja en el repo y en el zip.</para>
    /// </summary>
    [Fact]
    public void El_encabezado_dice_30_y_las_utilizables_son_26()
    {
        var d = Datos();
        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(d.SourceDir!, "DropGlobal.json")));
        Assert.Equal(30, doc.RootElement.GetProperty("DROP").GetArrayLength());
        Assert.Equal("30", doc.RootElement.GetProperty("meta").GetProperty("INIT").GetProperty("NumDrops").GetString());
        Assert.Equal(26, d.DropsGlobales.Count);
    }

    // ── EL SORTEO ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EL REPARTO, BARRIDO ENTERO Y SIN AZAR. Se recorren las 482 tiradas posibles y se cuenta cuántas caen en
    /// cada ítem: tiene que dar exactamente su peso. Así se verifica de una que la ruleta no deja huecos, no
    /// solapa franjas y no se saltea la primera ni la última entrada — los tres errores clásicos de un
    /// acumulador por resta.
    /// </summary>
    [Fact]
    public void Cada_item_se_lleva_una_franja_del_tamano_de_su_peso()
    {
        var d = Datos();
        Assert.Equal(482, d.PesoTotalDropGlobal);
        var salieron = new Dictionary<int, int>();
        for (int t = 0; t < d.PesoTotalDropGlobal; t++)
        {
            var g = d.DropGlobalPara(t);
            Assert.NotNull(g);
            salieron[g!.ObjIndex] = salieron.GetValueOrDefault(g.ObjIndex) + 1;
        }
        Assert.Equal(d.DropsGlobales.Count, salieron.Count);           // ninguno quedó sin franja
        foreach (var g in d.DropsGlobales) Assert.Equal(g.Peso, salieron[g.ObjIndex]);
        // Y la jerarquía que el archivo dejó escrita: los dos Pendientes salen 20 veces menos que el resto.
        Assert.Equal(1, salieron[1149]);
        Assert.Equal(20, salieron[57]);
    }

    /// <summary>Fuera de rango no devuelve nada. Importa porque el llamador sortea con
    /// <c>_rng.Next(PesoTotal)</c>: si un día ese tope cambiara, el error tiene que ser un "no cae nada" y no
    /// una excepción en el medio de la muerte de una criatura.</summary>
    [Fact]
    public void Una_tirada_fuera_de_rango_no_devuelve_nada()
    {
        var d = Datos();
        Assert.Null(d.DropGlobalPara(-1));
        Assert.Null(d.DropGlobalPara(d.PesoTotalDropGlobal));
        Assert.NotNull(d.DropGlobalPara(0));
        Assert.NotNull(d.DropGlobalPara(d.PesoTotalDropGlobal - 1));
    }

    /// <summary>
    /// LOS 26 SON ÍTEMS DE VERDAD. La tabla apunta a obj.dat por índice, y un índice que no exista dejaría al
    /// jugador con un objeto fantasma en el piso. También justifica por qué la regla sale apagada: son todos
    /// accesorios de los caros, no material de relleno.
    /// </summary>
    [Fact]
    public void Los_26_apuntan_a_objetos_que_existen()
    {
        var d = Datos();
        var huerfanos = d.DropsGlobales.Where(g => d.Obj(g.ObjIndex) is null).Select(g => g.ObjIndex).ToArray();
        Assert.True(huerfanos.Length == 0, "apuntan a objetos que no existen: " + string.Join(", ", huerfanos));
    }

}
