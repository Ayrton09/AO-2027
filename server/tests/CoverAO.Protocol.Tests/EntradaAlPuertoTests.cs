using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA ENTRADA AL PUERTO DE RINKEL (100): el Pescador, que vende la caña, está detrás de la puerta (14,21), y desde el 101 sólo la
/// entrada (76,8) llega hasta ahí. La (25,8) es una franja del borde a tres baldosas de (76,8) pero del otro lado de la muralla, y el
/// viaje entraba por ahí. Medido en las rondas 173 a 196 (27/11): Dalia, Pescadora sin caña, llegaba y se volvía.
/// </summary>
public sealed class EntradaAlPuertoTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    [Fact]
    public void Desde_el_101_entra_por_donde_se_llega_al_Pescador()
    {
        var (d, nav) = Mundo.Value;
        const short Pescador = 58;
        var pueblo = nav.PuebloMasCercano(101, (10, 26), m => m == 100, f => f == Pescador, null, 3);
        Assert.Equal((100, (byte)76, (byte)8), (pueblo!.Value.Mapa, pueblo.Value.X, pueblo.Value.Y));
        var puerta = nav.PuertaHacia(101, 100, (10, 26), (pueblo.Value.X, pueblo.Value.Y));
        Assert.True(d.MapExits.TryGetValue((101, puerta!.Value.Puerta.X, puerta.Value.Puerta.Y), out var llega));
        Assert.Equal((100, 76, 8), (llega.DestMap, llega.DestX, llega.DestY));
        // La franja de (25,8) no llega a (76,8), aunque quede a tres baldosas.
        Assert.Null(nav.Camino(100, (25, 8), (76, 8)));
    }
}
