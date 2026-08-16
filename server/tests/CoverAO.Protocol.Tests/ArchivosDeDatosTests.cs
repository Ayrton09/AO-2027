using System.Text.RegularExpressions;
using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// UN ARCHIVO DE DATOS QUE FALTA NO PUEDE SER INVISIBLE.
///
/// <para>Los quince cargadores de <see cref="GameData"/> devuelven vacío cuando no encuentran su <c>.json</c>
/// y siguen adelante. Sin <c>Balance.json</c> los modificadores de combate quedan en cero, sin
/// <c>NACIMIENTO.json</c> los personajes nuevos nacen desnudos, sin <c>MapTriggers.json</c> no hay un solo
/// interior — y la línea de resumen del arranque sólo cuenta cinco de las veinte cosas, así que el resto
/// desaparece sin una palabra. El juego arranca roto de un modo que no se parece a un error.</para>
///
/// <para>El manifiesto <c>ArchivosDeDatos</c> convierte eso en un aviso que nombra el archivo Y lo que se
/// pierde. Estos tests existen para que el manifiesto no envejezca: uno nuevo que se cargue sin su renglón
/// deja el agujero exactamente donde estaba.</para>
/// </summary>
public class ArchivosDeDatosTests
{
    /// <summary>El manifiesto tiene que estar bien formado: sin repetidos y con la consecuencia escrita.</summary>
    [Fact]
    public void ElManifiestoEstaBienFormado()
    {
        var m = GameData.ArchivosDeDatos;
        Assert.NotEmpty(m);
        var repetidos = m.GroupBy(a => a.Archivo, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(repetidos.Length == 0, "archivos repetidos en el manifiesto: " + string.Join(", ", repetidos));
        foreach (var (archivo, pierde) in m)
        {
            Assert.EndsWith(".json", archivo, StringComparison.OrdinalIgnoreCase);
            // Sin el "qué se pierde" el aviso es un nombre de archivo suelto, que no le dice nada al que hostea.
            Assert.False(string.IsNullOrWhiteSpace(pierde), $"'{archivo}' no dice qué se pierde si falta");
        }
    }

    /// <summary>Y tiene que nombrar TODOS los que el cargador abre de verdad. Es el único check con dientes:
    /// lo demás es una lista a mano, y una lista a mano es de donde salió este defecto.</summary>
    [Fact]
    public void ElManifiestoNombraTodosLosArchivosQueElCargadorAbre()
    {
        string fuente = LeerFuenteDeGameData();
        var abiertos = Regex.Matches(fuente, @"Path\.Combine\(dir,\s*""([^""]+\.json)""\)")
                            .Select(x => x.Groups[1].Value)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
        // Si el regex dejó de enganchar, el test pasaría con la lista vacía sin haber mirado nada.
        Assert.True(abiertos.Length >= 15, $"sólo encontré {abiertos.Length} archivos en el cargador; el parseo se rompió");

        var enManifiesto = GameData.ArchivosDeDatos.Select(a => a.Archivo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sinRenglon = abiertos.Where(f => !enManifiesto.Contains(f)).ToArray();
        Assert.True(sinRenglon.Length == 0,
            "el cargador abre archivos que el manifiesto no nombra, así que faltando NO avisan: "
            + string.Join(", ", sinRenglon));
    }

    /// <summary>Sube desde el binario del test hasta encontrar el fuente. Si no lo encuentra FALLA: saltear el
    /// test en silencio sería el mismo defecto que este archivo entero existe para cazar.</summary>
    private static string LeerFuenteDeGameData()
    {
        const string relativo = "server/src/CoverAO.Server/Data/GameData.cs";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidato = Path.Combine(dir.FullName, relativo.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidato)) return File.ReadAllText(candidato);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}. Este test cruza el manifiesto " +
            "contra el fuente, así que sin el fuente no prueba nada y no puede dar verde.");
    }
}
