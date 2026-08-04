using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Guardia de completitud del dispatch. El protocolo AO NO lleva length-prefix: un ClientPacketId que caiga
/// al <c>default</c> del switch principal (PacketDispatcher.Dispatch) no se puede saltear sin desincronizar
/// el stream, así que el servidor CIERRA la conexión. Por eso agregar un id nuevo al enum sin su
/// <c>case</c> es una regresión silenciosa: recién se nota cuando un cliente manda ese paquete y lo echan.
///
/// Este test la vuelve ruidosa: reflexiona sobre <c>Enum.GetValues&lt;ClientPacketId&gt;()</c> y comprueba
/// que cada valor tenga un <c>case ClientPacketId.X</c> en el fuente del dispatcher. Si falta alguno, el
/// test rompe en CI listando cuál, ANTES de que llegue a producción.
/// </summary>
public class DispatchCompletenessTests
{
    /// <summary>Ubica un archivo del repo subiendo directorios desde donde corre el test (bin/…).</summary>
    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(System.AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"no encontré {relative} subiendo desde {System.AppContext.BaseDirectory}");
    }

    [Fact]
    public void Every_client_packet_id_has_a_dispatch_case()
    {
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("server", "src", "CoverAO.Server", "Net", "PacketDispatcher.cs")));

        // Todos los `case ClientPacketId.X` del fuente (varias etiquetas por línea también cuentan).
        var handled = Regex.Matches(source, @"case\s+ClientPacketId\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var missing = System.Enum.GetNames<ClientPacketId>()
            .Where(name => !handled.Contains(name))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"Estos ClientPacketId no tienen case en el dispatcher y caerían al default (cierran la conexión): "
            + string.Join(", ", missing));
    }

    /// <summary>Sanidad del propio test: si el regex dejara de matchear, el conjunto vacío haría pasar todo
    /// falsamente. Anclamos que se reconoce un puñado de ids que sabemos dispatchados.</summary>
    [Fact]
    public void The_parser_actually_finds_known_cases()
    {
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("server", "src", "CoverAO.Server", "Net", "PacketDispatcher.cs")));
        var handled = Regex.Matches(source, @"case\s+ClientPacketId\.(\w+)")
            .Select(m => m.Groups[1].Value).ToHashSet();

        Assert.True(handled.Count > 100, $"el parser debería ver >100 cases, vio {handled.Count}");
        foreach (var known in new[] { "Walk", "CastSpell", "GMCommands", "LoginExistingChar" })
            Assert.Contains(known, handled);
    }
}
