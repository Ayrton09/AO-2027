using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS BOLSONES DEL EARLY (investigación del 06/10, `early122` a `early138`): desde el 382 todo camino a un pueblo que le venda a
/// un ciudadano pasa por el 65 (Terrenos de Banderbill, Lobos), y desde el 275 por el 195. El caster que huye de esos Lobos no
/// vuelve a cruzar el mapa por un rato (<see cref="Bot.NoVuelveACruzar"/>), así que la búsqueda del pueblo tiene que mirar lo mismo:
/// si no, elige Rinkel por ahí y el viaje se corta en el primer tramo, cada minuto y medio. (Desde el 382 el camino más corto a un
/// pueblo es por el 65; los otros son más largos y, para un nivel 11, con peligro.)
/// </summary>
public class ElBolsonDelEarlyTests
{
    private static (int Mapa, byte X, byte Y, int Saltos)? Pueblo(GameData datos, Navegante mapas, int mapa, (byte, byte) desde,
                                                                  Func<int, bool> pisable) =>
        Bot.PuebloConMercader(mapas, mapa, desde, m => datos.MapBattleMode(m) is 2 or 3,
                              f => datos.Npc((short)f) is { StockItems.Count: > 0 }, pisable, pisable);

    /// <summary>Con el mapa de la huida afuera la búsqueda cambia: otro pueblo por un camino más largo (desde el 382, la cuna a 7
    /// saltos; desde el 275, Illiandor a 6), o ninguno si los otros caminos tienen peligro. Nunca el mismo viaje que se corta.</summary>
    [Theory]
    [InlineData(382, 50, 30, 65)]
    [InlineData(275, 50, 50, 195)]
    public void Sin_el_mapa_de_la_huida_la_busqueda_da_otro_camino(int mapa, int x, int y, int laSalidaDeSiempre)
    {
        var datos = GameData.Load();
        if (datos.MapExits.Count == 0 || !datos.MapSpawns.ContainsKey(mapa)) return;
        var mapas = new Navegante(datos);
        var conLaSalida = Pueblo(datos, mapas, mapa, ((byte)x, (byte)y), _ => true);
        var sinLaSalida = Pueblo(datos, mapas, mapa, ((byte)x, (byte)y), m => m != laSalidaDeSiempre);
        Assert.NotNull(conLaSalida);
        Assert.True(sinLaSalida is null || sinLaSalida.Value.Saltos > conLaSalida.Value.Saltos,
                    $"con la salida {conLaSalida}, sin ella {sinLaSalida}");
    }
}
