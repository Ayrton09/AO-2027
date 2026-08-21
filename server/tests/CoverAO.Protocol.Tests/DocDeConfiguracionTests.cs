using System.Text.RegularExpressions;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// TODA CLAVE QUE EL SERVIDOR LEE DEL <c>Server.ini</c> TIENE QUE ESTAR DOCUMENTADA.
///
/// <para>Existe por una deriva real: el 2026-08-21, agregando <c>PersistirSuelo</c>, quedó a la vista que la
/// tabla de <c>docs/HOSTING.md</c> y el <c>Server.ini.ejemplo</c> se mantienen A MANO. Una clave nueva entra en
/// tres archivos y nada avisa si entra sólo en uno — y la que falta es siempre la del documento, porque el
/// código anda igual sin él. Quien hostea se queda sin saber que el interruptor existe.</para>
///
/// <para>La lista NO está escrita a mano acá: sale de <c>ServerIni.cs</c> por regex sobre las llamadas
/// <c>ini.GetX("INIT", "Clave"</c>. Escribirla a mano sería el mismo problema una capa más arriba — habría que
/// acordarse de actualizar la lista del test, que es justo lo que el test dice que nadie hace.</para>
/// </summary>
public class DocDeConfiguracionTests
{
    /// <summary>Ubica un archivo del repo subiendo directorios desde donde corre el test (bin/…).</summary>
    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }

    private static List<string> ClavesQueElServidorLee()
    {
        string fuente = File.ReadAllText(BuscarEnElRepo(Path.Combine(
            "server", "src", "CoverAO.Server", "Data", "ServerIni.cs")));
        return Regex.Matches(fuente, @"ini\.Get\w+\(\s*""INIT""\s*,\s*""(?<clave>[A-Za-z]+)""")
                    .Select(m => m.Groups["clave"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
    }

    /// <summary>LA PREMISA, y va primero. Si el regex dejara de matchear —alguien reformatea las llamadas, o
    /// las mueve a otro archivo— la lista saldría vacía y los dos tests de abajo pasarían sin mirar nada. El
    /// piso es holgado a propósito: no fija el número exacto, sólo que la extracción SIGUE FUNCIONANDO.</summary>
    [Fact]
    public void El_extractor_de_claves_sigue_encontrando_algo()
    {
        var claves = ClavesQueElServidorLee();
        Assert.True(claves.Count >= 20,
            $"el regex encontró {claves.Count} claves en ServerIni.cs; ¿cambió la forma de leer el ini?");
        // Tres testigos de familias distintas: una de red, una de operación y una regla de la casa. Si el
        // extractor se rompiera a medias —matcheando unas sí y otras no— esto lo caza y la cuenta de arriba no.
        Assert.Contains("MaxUsers", claves);
        Assert.Contains("MinutosBackup", claves);
        Assert.Contains("PersistirSuelo", claves);
    }

    [Fact]
    public void Todas_las_claves_del_ini_estan_en_la_guia_de_hosting()
    {
        string doc = File.ReadAllText(BuscarEnElRepo(Path.Combine("docs", "HOSTING.md")));
        // Se busca la clave entre backticks: es como la escribe la tabla de la guía, y evita que un `MaxUsers`
        // mencionado de pasada en una frase cuente como documentación.
        var faltan = ClavesQueElServidorLee().Where(k => !doc.Contains($"`{k}`", StringComparison.Ordinal)).ToList();
        Assert.True(faltan.Count == 0,
            "estas claves del Server.ini no están en docs/HOSTING.md: " + string.Join(", ", faltan));
    }

    /// <summary>Las reglas de la casa, además, tienen que estar en la PLANTILLA que se distribuye — con su
    /// valor escrito, no sólo nombradas. Son las únicas que cambian cómo se JUEGA, así que un hosteador que no
    /// las ve en su archivo no se entera de que puede elegir.</summary>
    [Fact]
    public void Las_reglas_de_la_casa_estan_en_el_ini_de_ejemplo()
    {
        string ejemplo = File.ReadAllText(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        foreach (var regla in new[] { "OroAlSubirNivel", "CuraAlSubirNivel", "QuitarEquipoNovato", "PersistirSuelo" })
            Assert.True(Regex.IsMatch(ejemplo, $@"^{regla}=", RegexOptions.Multiline),
                $"la regla de la casa {regla} no tiene su línea en hosting/Server.ini.ejemplo");
    }
}
