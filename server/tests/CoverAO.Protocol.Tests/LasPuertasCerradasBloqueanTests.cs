using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PUERTA CERRADA BLOQUEA DESDE EL ARRANQUE (decisión del autor, 2026-09-27). Trece puertas cerradas de cinco ciudades
/// vienen sin sus baldosas en la lista de bloqueados del <c>.csm</c> y se atravesaban hasta que alguien las abría y las
/// cerraba —también en el original, que sólo bloquea lo que trae el mapa—. Lo reportó el tester. Ver
/// <see cref="WorldPopulator.PlaceObjects"/>.
/// </summary>
public class LasPuertasCerradasBloqueanTests
{
    private static GameData Datos()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita la gamedata en disco");
        return data;
    }

    [Fact]
    public void La_puerta_de_Tiama_que_el_mapa_trae_libre_bloquea_al_poblar()
    {
        var data = Datos();
        // Tiama (218), la puerta cerrada de (17,46): el .csm no trae ni su baldosa ni la de su oeste.
        Assert.Equal(1, data.Obj(8)!.Closed);
        Assert.Contains(data.MapObjects[218], o => o.X == 17 && o.Y == 46 && o.ObjIndex == 8);
        Assert.False(data.IsBlockedTile(218, 17, 46));
        Assert.False(data.IsBlockedTile(218, 16, 46));

        WorldPopulator.PlaceObjects(new World(), data);

        Assert.True(data.IsBlockedTile(218, 17, 46));
        Assert.True(data.IsBlockedTile(218, 16, 46));
    }

    [Fact]
    public void Ninguna_puerta_cerrada_del_mundo_se_atraviesa()
    {
        var data = Datos();
        WorldPopulator.PlaceObjects(new World(), data);

        var cerradas = data.MapObjects
            .SelectMany(m => m.Value.Select(o => (Mapa: m.Key, o.X, o.Y, Info: data.Obj(o.ObjIndex))))
            .Where(p => p.Info is { IsDoor: true, Closed: 1 })
            .ToList();
        Assert.True(cerradas.Count >= 490, $"se esperaban las 492 puertas cerradas del mundo y hay {cerradas.Count}");
        var pasables = cerradas.Where(p => !data.IsBlockedTile(p.Mapa, p.X, p.Y) || !data.IsBlockedTile(p.Mapa, p.X - 1, p.Y))
            .Select(p => $"mapa {p.Mapa} ({p.X},{p.Y})").ToList();
        Assert.Empty(pasables);
    }

    [Fact]
    public void La_puerta_abierta_sigue_libre()
    {
        var data = Datos();
        // Las dos únicas puertas que el mundo trae abiertas, en el 750: (50,33) y (52,33).
        Assert.Contains(data.MapObjects[750], o => o.X == 50 && o.Y == 33 && data.Obj(o.ObjIndex) is { IsDoor: true, Closed: 0 });

        WorldPopulator.PlaceObjects(new World(), data);

        Assert.False(data.IsBlockedTile(750, 50, 33));
        Assert.False(data.IsBlockedTile(750, 49, 33));
    }

    [Fact]
    public void Abrirla_la_libera_y_cerrarla_la_vuelve_a_bloquear()
    {
        var data = Datos();
        WorldPopulator.PlaceObjects(new World(), data);

        // Lo que hace AccionParaPuerta con las dos baldosas al abrir y al cerrar.
        data.SetTileBlocked(218, 17, 46, false);
        data.SetTileBlocked(218, 16, 46, false);
        Assert.False(data.IsBlockedTile(218, 17, 46));
        Assert.False(data.IsBlockedTile(218, 16, 46));

        data.SetTileBlocked(218, 16, 46, true);
        data.SetTileBlocked(218, 17, 46, true);
        Assert.True(data.IsBlockedTile(218, 17, 46));
        Assert.True(data.IsBlockedTile(218, 16, 46));
    }
}
