using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL 839 SIN SALIDA (el tester, 27/11, sobre la 1.1.1): Amparo, renegada de nivel 20, desnuda y con 600 mil en las Ruinas del Destino. Con
/// el Server.ini que se distribuye, desde ahí un ciudadano o un renegado pasado el novato no llega a pie a ningún pueblo que le venda; un
/// republicano sí, a la Avanzada republicana. El muerto sin runa vuelve a casa en vez de revivir ahí (<c>VuelveAlHogarDeMuerto</c>).
/// </summary>
public sealed class VolverAlHogarDeMuertoTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas, ServerIni Ini)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        return (datos, new Navegante(datos), ServerIni.Cargar(ruta));
    });

    private static bool HayPueblo(byte bando, int mapa, byte x = 50, byte y = 54) =>
        Bot.HayPuebloQueLeVendaDesde(Mundo.Value.Mapas, Mundo.Value.Datos, Mundo.Value.Ini, bando, 20, mapa, (x, y));

    [Fact]
    public void Desde_las_Ruinas_del_Destino_solo_el_republicano_tiene_pueblo()
    {
        Assert.False(HayPueblo(Player.StatusCriminal, 839));
        Assert.False(HayPueblo(Player.StatusCiudadano, 839));
        Assert.False(HayPueblo(Player.StatusCiudadano, 770));
        Assert.True(HayPueblo(Player.StatusRepublicano, 839));
        // Y desde el 27, al lado de Rinkel, el ciudadano sí.
        Assert.True(HayPueblo(Player.StatusCiudadano, 27, 51, 11));
    }
}
