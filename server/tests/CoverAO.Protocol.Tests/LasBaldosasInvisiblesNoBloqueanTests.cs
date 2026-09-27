using System.Text.Json;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS BALDOSAS QUE BLOQUEABAN SIN NADA DIBUJADO YA NO BLOQUEAN (decisión del autor, 2026-09-27). Reporte del tester: en el
/// parque de Nix, cerca de (78,82), "hay cosas invisibles que te tosquean". Eran baldosas bloqueadas sueltas sin gráfico, luz,
/// partícula ni objeto encima, y así están también en el original: el .csm del server y el Mapas.cao del cliente coinciden
/// ahí. Se revisaron una por una y se desbloquearon 34 en 16 mapas, en los DOS lados: el terreno del server
/// (<c>gamedata/MapTerrain.json</c>) y el mapa del cliente (<c>client-web/assets/map&lt;N&gt;.json</c>). Las que tienen otra
/// bloqueada en diagonal —la punta de una hilera de piedras, de una cresta o del muelle— y las de agua quedaron como estaban.
/// Si alguien regenera los mapas desde el .csm vuelven a bloquear, y este test lo avisa.
/// </summary>
public class LasBaldosasInvisiblesNoBloqueanTests
{
    private static readonly (int Mapa, int X, int Y)[] Desbloqueadas =
    {
        (16, 27, 51), (34, 69, 74), (34, 70, 82), (34, 74, 74), (34, 74, 82), (34, 78, 82), (34, 79, 74), (35, 13, 60),
        (150, 12, 13), (194, 65, 28), (194, 65, 34), (194, 68, 28), (194, 68, 34), (213, 47, 47), (213, 64, 62), (326, 43, 72),
        (462, 51, 67), (499, 77, 42), (543, 29, 78), (555, 44, 61), (761, 21, 18), (761, 30, 51), (761, 46, 20), (761, 83, 34),
        (762, 25, 80), (762, 34, 43), (762, 43, 75), (762, 67, 50), (762, 84, 32), (763, 34, 11), (763, 59, 13), (763, 73, 53),
        (764, 58, 77), (806, 18, 12),
    };

    /// <summary>Las que se decidió DEJAR: la punta de las piedras del 13, la pata del muelle del 99, la cresta del 219, y las
    /// paredes del parque de Nix alrededor de las que se abrieron.</summary>
    private static readonly (int Mapa, int X, int Y)[] Siguen =
    {
        (13, 76, 65), (99, 50, 38), (219, 38, 19), (34, 68, 72), (34, 82, 78), (34, 75, 85),
    };

    private static GameData Datos()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita la gamedata en disco");
        return data;
    }

    private static HashSet<(int, int)> BloqueadasEnElCliente(int mapa)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "client-web", "assets"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "client-web", "assets", $"map{mapa}.json")));
        return json.RootElement.GetProperty("blocked").EnumerateArray()
            .Select(b => (b[0].GetInt32(), b[1].GetInt32())).ToHashSet();
    }

    [Fact]
    public void En_el_server_no_bloquean()
    {
        var data = Datos();
        var bloquean = Desbloqueadas.Where(t => data.IsBlockedTile(t.Mapa, t.X, t.Y)).ToList();
        Assert.Empty(bloquean);
    }

    [Fact]
    public void En_el_cliente_tampoco()
    {
        var bloquean = Desbloqueadas.GroupBy(t => t.Mapa)
            .SelectMany(g => { var b = BloqueadasEnElCliente(g.Key); return g.Where(t => b.Contains((t.X, t.Y))); })
            .ToList();
        Assert.Empty(bloquean);
    }

    [Fact]
    public void Lo_que_se_decidio_dejar_sigue_bloqueando_en_los_dos_lados()
    {
        var data = Datos();
        foreach (var (m, x, y) in Siguen)
        {
            Assert.True(data.IsBlockedTile(m, x, y), $"server: mapa {m} ({x},{y})");
            Assert.Contains((x, y), BloqueadasEnElCliente(m));
        }
    }
}
