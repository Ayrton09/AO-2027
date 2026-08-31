using System;
using System.IO;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HIPOCAMPO EN MAR ABIERTO, la regla de la casa que pidió el tester.
///
/// <para><b>En el original el Hipocampo (obj 900) no se puede conseguir por ningún camino.</b> Está definido
/// en el <c>obj.dat</c> —comida, restaura 100 de hambre— pero nada lo produce: la caña da siempre Pargo
/// (<c>DoPescar</c>, Trabajo.bas:1315), la red sortea entre Pargo, Lisa, Pez Espada y Merluza
/// (<c>DoPescarRed</c>, :1429-1432), ninguna criatura lo tira, ningún mercader lo vende, no está colocado en
/// ninguno de los 863 mapas y el código del servidor VB6 no lo nombra ni una vez. Es data muerta.</para>
///
/// <para>Lo que este archivo cuida es lo que hace que la regla FUNCIONE, que no es el interruptor sino
/// <b>quién cuenta como mar abierto</b>. La respuesta sale del nombre del mapa, y ahí está la trampa: el
/// header del .csm escribe el mismo nombre de TRES formas —"Oceano abierto", "Océano abierto" y "Océano
/// Abierto"—. Comparando literal se perderían dos de las tres y la regla andaría en un tercio de los mapas,
/// que es la clase de bug que nadie reporta porque parece mala suerte.</para>
/// </summary>
public class HipocamposTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "coverao_hipo_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ruta); } catch { } }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ruta, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ruta);
    }

    /// <summary>Sale PRENDIDA: es la única regla de la casa que no viene apagada. Ver ReglasDeLaCasaTests.</summary>
    [Fact]
    public void Viene_prendida_y_se_puede_apagar()
    {
        Assert.True(new ServerIni().HipocamposEnMarAbierto);          // sin archivo
        Assert.True(Cargar("MaxUsers=50\r\n").HipocamposEnMarAbierto); // con un ini que no la menciona
        Assert.False(Cargar("HipocamposEnMarAbierto=0\r\n").HipocamposEnMarAbierto);
        Assert.True(Cargar("HipocamposEnMarAbierto=1\r\n").HipocamposEnMarAbierto);
    }

    private static GameData Cargar()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "este test necesita la gamedata en disco");
        return d;
    }

    /// <summary>
    /// El reconocedor, contra la data REAL. Los números son del mundo que se distribuye: 403 mapas de mar
    /// abierto sobre 863. Se fija el número y no un "&gt; 0" porque lo que se quiere cazar es que una grafía
    /// deje de matchear — con "&gt; 0" bastaría con que anduviera una de las tres.
    /// </summary>
    [Fact]
    public void Reconoce_los_403_mapas_de_mar_abierto()
    {
        var d = Cargar();
        int mares = 0;
        for (int m = 1; m <= 1000; m++) if (d.EsOceanoAbierto(m)) mares++;
        Assert.Equal(403, mares);
    }

    /// <summary>Uno de cada grafía, y un control de tierra firme. Sin quitar acentos, los dos últimos fallan.</summary>
    [Theory]
    [InlineData(837, true)]    // "Oceano abierto"  — sin acento
    [InlineData(743, true)]    // "Océano abierto"  — con acento, minúscula
    [InlineData(738, true)]    // "Océano Abierto"  — con acento, mayúscula
    [InlineData(34, false)]    // Nix, una ciudad con costa: tiene agua, pero no es mar abierto
    [InlineData(15, false)]    // Desierto Rinkel, el mapa del fixture
    public void Distingue_el_mar_abierto_de_la_costa(int mapa, bool esMar)
    {
        Assert.Equal(esMar, Cargar().EsOceanoAbierto(mapa));
    }

    /// <summary>
    /// EL MAR ABIERTO NO SE PUEDE DEDUCIR DE LA ZONA NI DEL TERRENO, y por eso el reconocedor mira el nombre.
    /// Si algún día alguien "simplifica" esto usando uno de esos campos, acá está la medición de por qué no
    /// alcanza:
    ///
    /// <list type="bullet">
    ///   <item>La ZONA de un mapa de mar es <c>CAMPO</c>, la misma que la del desierto del fixture.</item>
    ///   <item>El TERRENO no es uno solo: los 403 mapas de mar reparten <b>cuatro</b> terrenos distintos
    ///     —BOSQUE 336, NIEVE 61, INFRAMUNDO 4 y DESIERTO 2—, así que no hay ningún valor que los agrupe.
    ///     Hay mapas que son de mar y de nieve a la vez.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Ni_la_zona_ni_el_terreno_alcanzan_para_distinguirlo()
    {
        var d = Cargar();
        Assert.Equal("CAMPO", d.MapZone(837));
        Assert.Equal(d.MapZone(15), d.MapZone(837));   // el desierto del fixture comparte zona con el mar

        var terrenos = new HashSet<string>();
        for (int m = 1; m <= 1000; m++) if (d.EsOceanoAbierto(m)) terrenos.Add(d.MapTerrain(m));
        Assert.True(terrenos.Count >= 4,
            "los mapas de mar abierto reparten varios terrenos; con uno solo, el terreno sí serviría: "
            + string.Join(", ", terrenos));
    }
}
