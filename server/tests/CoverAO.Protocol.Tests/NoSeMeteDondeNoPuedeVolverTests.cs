using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT NO VA A CAZAR DONDE NO TIENE CÓMO VOLVER A COMPRAR (decisión del autor, 30/09: "que no se meta ahí", para todos). Milo,
/// republicano de nivel 23 con 177 de vida, cazaba en el 19, del lado imperial: el mapa seguro para su runa quedaba a seis mapas y
/// murió sin energía en `tardeA` y en `tardeB`. Ver <see cref="Bot.TieneComoVolver"/>.
/// </summary>
public class NoSeMeteDondeNoPuedeVolverTests
{
    private static (Navegante Mapas, GameData Datos, ServerIni Ini)? Mundo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return null;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        return File.Exists(ruta) ? (new Navegante(d), d, ServerIni.Cargar(ruta)) : null;
    }

    private static bool Vuelve((Navegante Mapas, GameData Datos, ServerIni Ini) m, byte bando, int nivel, int vida, int mapa, bool conRuna)
    {
        var (pasa, seguro, tienda) = Bot.FiltrosDeLaVuelta(m.Datos, m.Ini, bando, nivel, vida);
        return Bot.TieneComoVolver(m.Mapas, mapa, Bot.DesdeDondeSeCaza(m.Datos, mapa), pasa, seguro, tienda, conRuna);
    }

    [Fact]
    public void Del_19_el_republicano_no_tiene_como_volver_y_el_ciudadano_si()
    {
        if (Mundo() is not { } m) return;
        Assert.True(m.Ini.GuardiasAtacan, "premisa: el ini que se distribuye trae los guardias que atacan");
        Assert.False(Vuelve(m, Player.StatusRepublicano, 23, 177, 19, conRuna: true));
        Assert.True(Vuelve(m, Player.StatusCiudadano, 23, 177, 19, conRuna: true), "al ciudadano, Ullathorpe le sirve a cuatro");
    }

    /// <summary>
    /// Y AL REVÉS EN EL 261: el republicano usa la runa en Suramei, a un mapa, y el ciudadano de nivel 16 o más no tiene cómo: los
    /// guardias de Suramei van por él, y el único mapa seguro cerca, el 544, queda del otro lado de Suramei.
    /// </summary>
    [Fact]
    public void Del_261_el_republicano_vuelve_por_Suramei_y_el_ciudadano_no()
    {
        if (Mundo() is not { } m) return;
        Assert.True(Vuelve(m, Player.StatusRepublicano, 20, 200, 261, conRuna: true));
        Assert.False(Vuelve(m, Player.StatusCiudadano, 20, 200, 261, conRuna: true));
        Assert.True(Vuelve(m, Player.StatusCiudadano, 14, 140, 261, conRuna: true), "de novato, los guardias no van por él");
    }

    /// <summary>Sin la runa, o con una que lo deja donde no le venden, lo que cuenta es la tienda: el mapa seguro solo no alcanza.</summary>
    [Fact]
    public void Sin_runa_el_mapa_seguro_no_alcanza()
    {
        if (Mundo() is not { } m) return;
        var (pasa, seguro, _) = Bot.FiltrosDeLaVuelta(m.Datos, m.Ini, Player.StatusCiudadano, 23, 177);
        var desde = Bot.DesdeDondeSeCaza(m.Datos, 19);
        Assert.True(Bot.TieneComoVolver(m.Mapas, 19, desde, pasa, seguro, _ => false, conRuna: true));
        Assert.False(Bot.TieneComoVolver(m.Mapas, 19, desde, pasa, seguro, _ => false, conRuna: false));
    }
}
