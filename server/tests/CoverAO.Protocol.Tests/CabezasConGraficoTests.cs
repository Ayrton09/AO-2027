using System.Text.Json;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// TODA CABEZA QUE SE PUEDE CREAR TIENE GRÁFICO. El creador del original acepta el 121 (elfo) y el 314-315 (enano), que no están en
/// las cabezas del install: el personaje salía sin cabeza (Ciro, en el servidor público, 13/11).
/// </summary>
public class CabezasConGraficoTests
{
    private static HashSet<int> ConGrafico()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gamedata", "heads.json")))
            dir = dir.Parent;
        Assert.True(dir is not null, "no encontré gamedata/heads.json subiendo desde " + AppContext.BaseDirectory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "gamedata", "heads.json")));
        return doc.RootElement.GetProperty("heads").EnumerateObject().Select(p => int.Parse(p.Name)).ToHashSet();
    }

    [Fact]
    public void Cada_cabeza_de_crear_personaje_y_de_la_pocion_tiene_grafico()
    {
        var hay = ConGrafico();
        var faltan = new List<string>();
        for (byte raza = 1; raza <= 6; raza++)
            for (byte genero = 1; genero <= 2; genero++)
            {
                var (min, max) = Equipment.HeadRange(raza, genero)!.Value;
                for (int h = min; h <= max; h++) if (!hay.Contains(h)) faltan.Add($"crear {raza}/{genero}: {h}");
                var (pmin, pmax) = Equipment.HeadRangeDePocion(raza, genero);
                for (int h = pmin; h <= pmax; h++) if (!hay.Contains(h)) faltan.Add($"poción {raza}/{genero}: {h}");
            }
        Assert.True(faltan.Count == 0, string.Join(", ", faltan));
    }

    [Fact]
    public void Las_que_se_cambian_al_entrar_son_justo_las_del_original_sin_grafico()
    {
        var hay = ConGrafico();
        Assert.All(new[] { 121, 314, 315 }, h => { Assert.DoesNotContain(h, hay); Assert.True(Equipment.CabezaSinGrafico(h)); });
        Assert.False(Equipment.CabezaSinGrafico(120));
        Assert.False(Equipment.CabezaSinGrafico(313));
    }
}
