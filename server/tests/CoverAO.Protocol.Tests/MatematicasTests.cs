using System.Text.RegularExpressions;
using CoverAO.Server.Game;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA BARRA DE VB6 NUNCA TRUNCA, Y ESO SE REIMPLEMENTÓ MAL CINCO VECES.
///
/// <para><c>Porcentaje(Total, Porc) = (Total * Porc) / 100</c> divide en punto flotante y devuelve un
/// <c>Long</c>: redondea al par. La división entera de C# se parece lo bastante como para que nadie mire dos
/// veces, y da un punto menos cada vez que la cuenta no cierra exacta. Un punto suena a nada hasta que se mira
/// dónde cae: el mordisco del frío sobre un recién nacido es 1 contra 0 —o sea que el aviso salía para siempre
/// sin bajarle vida— y el maná de meditar con un pozo chico es 2 contra 1, la mitad.</para>
///
/// <para>El primer caso ya se había encontrado y arreglado UNA VEZ, con el helper metido adentro de
/// <c>Potions</c>. Los otros cuatro siguieron truncando durante todo ese tiempo. Por eso, además de fijar los
/// valores, acá hay un barrido del fuente: es lo único que impide la sexta.</para>
/// </summary>
public class MatematicasTests
{
    [Theory]
    [InlineData(10, 26, 3)]     // 2,6 → 3      (truncando: 2)
    [InlineData(7, 50, 4)]      // 3,5 → 4 al par (truncando: 3)
    [InlineData(5, 50, 2)]      // 2,5 → 2 al par (truncando: 2, coincide)
    [InlineData(17, 5, 1)]      // 0,85 → 1     (truncando: 0 — el frío del recién nacido)
    [InlineData(100, 5, 5)]     // exacto: los dos dan lo mismo, que es por lo que Merlin no lo delataba
    public void El_porcentaje_redondea_al_par_y_no_trunca(int total, int porc, int esperado)
        => Assert.Equal(esperado, Matematicas.Porcentaje(total, porc));

    /// <summary>Los cuatro lugares donde el punto perdido se nota, con el valor del original.</summary>
    [Fact]
    public void Las_formulas_que_truncaban_ahora_dan_lo_que_da_el_original()
    {
        Assert.Equal(1, Survival.ColdBite(17));                    // recién nacido: muerde 1, no 0
        Assert.Equal(2, Survival.ManaAlMeditar(250, 6));           // pozo chico: 2 por acierto, no 1
        Assert.Equal(13, Combat.DanoHechizoOfensivo(10, 13, 0, 0));// nivel 13: 13, no 12
        Assert.Equal(31, Combat.EfectoHechizoArea3Pct(30, 1));     // nivel 1: la cura rinde 1, no 0
        // El tope del dado de la curación natural: 17 de energía máxima da 1, no 0 — y el piso lo sube a 2.
        Assert.Equal(2, Survival.HealAmount(17, (a, b) => b));
    }

    /// <summary>
    /// BARRIDO DEL FUENTE: nadie puede volver a escribir un porcentaje con división entera.
    ///
    /// <para>Busca la forma <c>algo * algo / 100</c> en código (no en comentarios) y falla si aparece fuera de
    /// <c>Matematicas.cs</c>. Es el test que hubiera cazado las cinco de una: los valores de arriba fijan lo que
    /// YA se arregló, esto cubre lo que todavía no se escribió.</para>
    /// </summary>
    [Fact]
    public void Ningun_archivo_del_server_reimplementa_el_porcentaje_con_division_entera()
    {
        // Con su motivo, como corresponde. No son porcentajes portados del original.
        var permitidos = new Dictionary<string, string>
        {
            ["Matematicas.cs"] = "es el helper: acá vive la cuenta buena",
            ["Weather.cs"] = "no es un Porcentaje del VB6 sino el umbral de nuestro ciclo de lluvia",
        };

        var raiz = SubirHasta("server/src/CoverAO.Server");
        var culpables = new List<string>();
        foreach (var archivo in Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories))
        {
            string nombre = Path.GetFileName(archivo);
            if (archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || permitidos.ContainsKey(nombre)) continue;

            var lineas = File.ReadAllLines(archivo);
            for (int i = 0; i < lineas.Length; i++)
            {
                string s = lineas[i].Trim();
                if (s.StartsWith("//") || s.StartsWith("*") || s.StartsWith("///")) continue;
                // `x * y / 100` sin punto decimal detrás del 100: eso es división ENTERA.
                if (Regex.IsMatch(s, @"\*[^;/]*/\s*100(?![.\d])"))
                    culpables.Add($"{nombre}:{i + 1}  {s}");
            }
        }

        Assert.True(culpables.Count == 0,
            "hay porcentajes con división entera (usá Matematicas.Porcentaje, que redondea como VB6):\n  "
            + string.Join("\n  ", culpables));
    }

    /// <summary>Sube desde el binario del test hasta encontrar la carpeta. Si no está FALLA: saltear en
    /// silencio sería el mismo defecto que este archivo existe para cazar.</summary>
    private static string SubirHasta(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string rel = relativo.Replace('/', Path.DirectorySeparatorChar);
        while (dir is not null)
        {
            string candidato = Path.Combine(dir.FullName, rel);
            if (Directory.Exists(candidato)) return candidato;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}: sin el fuente este barrido no prueba nada.");
    }
}
