using System.Text.RegularExpressions;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// NINGÚN GATE DE ALCANCE PUEDE MEDIR EN CHEBYSHEV.
///
/// <para>El original mide de tres formas y ninguna es Chebyshev: <c>Distancia</c> es
/// <b>Manhattan</b> —<c>Abs(dx) + Abs(dy) + Abs(dmapa)*100</c>, Matematicas.bas:43—, <c>Distance</c> es
/// <b>euclídea</b> (<c>Sqr</c>, Matematicas.bas:55) y varios call sites suman los dos <c>Abs</c> a mano.
/// <c>Math.Max(Abs(dx), Abs(dy))</c> —el Chebyshev— no aparece en ningún lado.</para>
///
/// <para>La diferencia no es teórica: con Chebyshev un NPC en (+3,+3) está a 3 y con Manhattan está a 6. Los
/// gates del mercader/banco/entrenador (<c>&gt; 3</c>) y del sacerdote Revividor (<c>&gt; 10</c>) medían en
/// Chebyshev, así que se podía comerciar y resucitar en diagonal desde el doble de lejos que en el original.
/// Ni los flows ni el oráculo lo veían: las tres fórmulas coinciden cuando el clic es de frente, que es como
/// clickea todo el arnés.</para>
///
/// <para>El comentario de <c>AccionParaPuerta</c> ya decía "ojo, NO es el Chebyshev que usan los gates de NPC
/// de acá al lado": la observación estaba escrita y la conclusión no se había sacado. Este guardia es para que
/// no haga falta volver a sacarla.</para>
/// </summary>
public class DistanciaManhattanTests
{
    [Fact]
    public void Ningun_gate_de_alcance_mide_en_Chebyshev()
    {
        string fuente = LeerFuente("server/src/CoverAO.Server/Net/PacketDispatcher.cs");
        var culpables = new List<string>();
        var lineas = fuente.Split('\n');
        for (int i = 0; i < lineas.Length; i++)
        {
            string s = lineas[i].Trim();
            if (s.StartsWith("//") || s.StartsWith("///") || s.StartsWith("*")) continue;
            // El patrón es Math.Max(Math.Abs(...), Math.Abs(...)) COMPARADO contra un número: eso es un gate.
            // Un Math.Max(Abs, Abs) suelto puede ser otra cosa —el anillo de spawn usa `!= radio`—, así que
            // sólo cuentan los que deciden alcance con < > <= >=.
            if (Regex.IsMatch(s, @"Math\.Max\s*\(\s*Math\.Abs.*Math\.Abs[^)]*\)\s*\)\s*[<>]=?\s*\d"))
                culpables.Add($"PacketDispatcher.cs:{i + 1}  {s}");
        }
        Assert.True(culpables.Count == 0,
            "hay gates de alcance medidos en Chebyshev; el original usa Manhattan (DistanciaVB6) o euclídea:\n  "
            + string.Join("\n  ", culpables));
    }

    /// <summary>Y la fórmula del helper es la del original, coeficiente del mapa incluido: sin el ×100 dos
    /// tiles del mismo (x,y) en mapas distintos darían distancia 0 y todo gate de alcance pasaría.</summary>
    [Fact]
    public void El_helper_suma_el_mapa_por_cien()
    {
        string fuente = LeerFuente("server/src/CoverAO.Server/Net/PacketDispatcher.cs");
        Assert.Matches(
            @"Math\.Abs\(a\.X - b\.X\) \+ Math\.Abs\(a\.Y - b\.Y\) \+ Math\.Abs\(a\.Map - b\.Map\) \* 100",
            fuente);
    }

    private static string LeerFuente(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string rel = relativo.Replace('/', Path.DirectorySeparatorChar);
        while (dir is not null)
        {
            string candidato = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}: sin el fuente el barrido no prueba nada.");
    }
}
