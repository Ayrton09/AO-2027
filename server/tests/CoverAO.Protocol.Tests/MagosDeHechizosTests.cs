using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS MAGOS QUE VENDEN HECHIZOS DE CLASE TAMBIÉN EN NUEVA ESPERANZA Y TIAMA (pedido del autor, 28/11). Los mapas del IAO-Clon 1.4.5 ponen
/// al Druida (ficha 104) en (51,74) de Nueva Esperanza y al Clérigo (ficha 154) en (36,43) de Tiama. Las fichas son las mismas que las
/// nuestras, que hasta ahora sólo estaban en Rinkel, Banderbill, Suramei y el 475. Nueva Esperanza y Tiama son otras tierras, y les
/// venden a todos.
/// </summary>
public sealed class MagosDeHechizosTests
{
    private const short Druida = 104, Clerigo = 154, Sacerdote = 14;

    [Theory]
    [InlineData(112, 51, 74, Druida)]
    [InlineData(218, 36, 43, Clerigo)]
    public void Estan_en_una_baldosa_libre_a_la_que_se_llega_desde_el_sacerdote(int mapa, int x, int y, short ficha)
    {
        var d = GameData.Load();
        var nacen = d.MapSpawns[mapa];
        Assert.Single(nacen, s => s.NpcIndex == ficha);
        Assert.Single(nacen, s => s.X == x && s.Y == y);
        Assert.False(d.IsBlockedTile(mapa, x, y));
        Assert.False(d.IsWater(mapa, x, y));
        Assert.True(d.Npc(ficha) is { StockItems.Count: > 0 });
        var cura = nacen.First(s => s.NpcIndex == Sacerdote);
        Assert.NotNull(new Navegante(d).HayComoLlegar(mapa, ((byte)cura.X, (byte)cura.Y), ((byte)x, (byte)y), 1));
    }
}
