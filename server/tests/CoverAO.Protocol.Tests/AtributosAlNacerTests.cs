using System.Text.Json;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS ATRIBUTOS CON LOS QUE SE NACE: base de la clase + modificador de la raza (VB6 <c>AsignarAtributos</c>). La pantalla de
/// creación los muestra desde <c>client-web/assets/creacion.json</c>; si ese archivo dice otra base que el server, el jugador ve un
/// número y nace con otro. Antes la pantalla mostraba sólo el modificador, y un Gnomo Mago con "Fue -5" nacía con 1 (el tester, 06/10).
/// </summary>
public class AtributosAlNacerTests
{
    [Fact]
    public void La_pantalla_de_creacion_muestra_la_misma_base_que_da_el_server()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "client-web", "assets"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "client-web", "assets", "creacion.json")));
        int clases = 0;
        foreach (var c in json.RootElement.GetProperty("clases").EnumerateArray())
        {
            var a = c.GetProperty("atributos");
            var b = Persistence.AtributosBaseDeClase((byte)c.GetProperty("id").GetInt32());
            Assert.Equal((b.f, b.a, b.i, b.c, b.co),
                (a.GetProperty("fuerza").GetInt32(), a.GetProperty("agilidad").GetInt32(), a.GetProperty("inteligencia").GetInt32(),
                 a.GetProperty("carisma").GetInt32(), a.GetProperty("constitucion").GetInt32()));
            clases++;
        }
        Assert.Equal(18, clases);
    }

    /// <summary>El caso del tester: el Mago tiene fuerza base 6, así que un Gnomo (−5) nace con 1.</summary>
    [Fact]
    public void Un_Gnomo_Mago_nace_con_1_de_fuerza()
    {
        Assert.Equal(6, Persistence.AtributosBaseDeClase(2).f);
    }
}
