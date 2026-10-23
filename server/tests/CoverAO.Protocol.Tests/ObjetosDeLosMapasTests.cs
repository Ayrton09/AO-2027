using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// CADA OBJETO QUE UN MAPA TRAE COLOCADO TIENE QUE TENER FICHA EN EL obj.dat.
///
/// <para><c>WorldPopulator.PlaceObjects</c> saltea en silencio un objeto sin ficha, así que una referencia rota en
/// <c>MapObjects.json</c> (un transcode que corre los índices, un obj.dat recortado) deja el mundo sin ese objeto
/// sin un solo aviso. Este test la hace visible.</para>
///
/// <para>LA ÚNICA EXCEPCIÓN VIENE DEL ORIGINAL (auditoría del 23/10): el mapa 791 trae ocho veces el objeto 492, y
/// el 492 no existe en ningún obj.dat de los que hay (el de <c>Servidor/Dat</c>, el de
/// <c>Herramientas/Conversores/Dat</c> ni el de IAO-Clon-1.4.5, cuyo mapa 791 trae la misma figura de ocho 492 corrida
/// catorce baldosas). Volviendo a decodificar <c>mapa791.csm</c> del original sale exactamente el MapObjects del
/// repo, así que no es un error del transcode. El VB6 lo carga en una ranura vacía de <c>ObjData</c>
/// (FileIO.bas:1326): un objeto fijo, sin nombre ni gráfico. La lista está escrita entera para que una referencia
/// rota nueva no se esconda detrás de ésta, y para que si alguien completa el 492 el test pida sacarla.</para>
/// </summary>
public class ObjetosDeLosMapasTests
{
    private static readonly (int Mapa, int X, int Y, int Obj)[] SinFichaEnElOriginal =
    {
        (791, 74, 45, 492), (791, 79, 46, 492), (791, 73, 48, 492), (791, 77, 48, 492),
        (791, 75, 51, 492), (791, 72, 53, 492), (791, 79, 53, 492), (791, 77, 54, 492),
    };

    [Fact]
    public void CadaObjetoDeLosMapasTieneFichaEnObjDat()
    {
        var datos = GameData.Load();
        // Sin data el test pasaría sin mirar nada: que falle.
        Assert.True(datos.MapObjects.Count > 400, $"sólo hay objetos en {datos.MapObjects.Count} mapas: no se cargó MapObjects.json");
        Assert.True(datos.ItemCount > 1000, $"sólo hay {datos.ItemCount} objetos: no se cargó obj.json");

        var sinFicha = datos.MapObjects
            .SelectMany(m => m.Value.Select(o => (Mapa: m.Key, o.X, o.Y, Obj: o.ObjIndex)))
            .Where(r => datos.Obj(r.Obj) is null)
            .OrderBy(r => r).ToArray();

        Assert.True(sinFicha.SequenceEqual(SinFichaEnElOriginal.OrderBy(r => r)),
            "objetos de mapa sin ficha en obj.json (WorldPopulator los saltea en silencio): "
            + string.Join(", ", sinFicha.Select(r => $"mapa {r.Mapa} ({r.X},{r.Y}) obj {r.Obj}"))
            + ". Los únicos esperados son los ocho 492 del mapa 791, que vienen así del original.");
    }
}
