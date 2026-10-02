using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL RESCATE DEL VARADO (DECIDIDO POR EL AUTOR, 02/10; ver <c>Bot.RescatarseAsync</c>): sin tienda ni mapa seguro al alcance por donde no
/// hay peligro, camina hasta el pueblo más cercano que le venda, o si no hay, hasta el mapa seguro más cercano; primero por el camino de
/// siempre y si no, por donde se pueda. Contra la data, desde donde quedaron Rufo y Aldo en `mid34`-`mid37`.
/// </summary>
public class RescateDelVaradoTests
{
    private sealed record Mundo(GameData Datos, ServerIni Ini, Navegante Mapas);

    private static Mundo? ElMundo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return null;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        return File.Exists(ruta) ? new Mundo(d, ServerIni.Cargar(ruta), new Navegante(d)) : null;
    }

    private static Func<int, bool> LeVenden(Mundo w, int nivel) => m =>
        Bot.ElMercaderLeVende(w.Ini, w.Datos.MapBattleMode(m), Player.StatusCiudadano)
        && !Bot.HayGuardiaQueVaPor(m, Player.StatusCiudadano, nivel, w.Datos, w.Ini);

    private static Func<int, bool> VendePociones(GameData d) => f =>
        d.Npc((short)f) is { StockItems: { Count: > 0 } stock } && stock.Any(s => d.Obj((short)s.ObjIndex) is { IsPotion: true, SubType: 3 });

    /// <summary>RUFO, ciudadano de nivel 18 (138 de vida) con el hogar en Nix, donde no le venden: por el camino de siempre llega a un
    /// pueblo que sí.</summary>
    [Fact]
    public void Rufo_desde_Nix_llega_a_un_pueblo_por_el_camino_de_siempre()
    {
        if (ElMundo() is not { } w) return;
        Func<int, bool> pisable = m => Bot.PuedeEntrar(18, w.Datos.MapZone(m));
        var porElDeSiempre = Bot.PorDondeSeRescata(w.Datos, pisable, new Dictionary<int, long>(), 0, 138);
        Assert.NotNull(w.Mapas.PuebloMasCercano(34, (40, 88), LeVenden(w, 18), VendePociones(w.Datos), porElDeSiempre, Bot.SaltosDelRescate));
    }

    /// <summary>
    /// ALDO, ciudadano de nivel 19 (199 de vida) en el 195, del lado republicano: los pueblos que le venderían pociones tienen guardias que
    /// van por él, así que pueblo no hay por ningún camino; el mapa seguro más cercano, las Costas de Suramei (544), sólo por donde se pueda.
    /// </summary>
    [Fact]
    public void Aldo_desde_el_195_sale_al_mapa_seguro_por_donde_se_pueda()
    {
        if (ElMundo() is not { } w) return;
        Func<int, bool> pisable = m => Bot.PuedeEntrar(19, w.Datos.MapZone(m));
        var sinHuidas = new Dictionary<int, long>();
        var porElDeSiempre = Bot.PorDondeSeRescata(w.Datos, pisable, sinHuidas, 0, 199);
        Func<int, bool> seguro = m => Bot.MapaSeguroPara(m, Player.StatusCiudadano, 19, w.Datos, w.Ini);
        Assert.Null(w.Mapas.PuebloMasCercano(195, (24, 88), LeVenden(w, 19), VendePociones(w.Datos), pisable, Bot.SaltosDelRescate));
        Assert.Null(w.Mapas.MapaMasCercano(195, (24, 88), seguro, porElDeSiempre, Bot.SaltosDelRescate));
        Assert.Equal(544, w.Mapas.MapaMasCercano(195, (24, 88), seguro, pisable, Bot.SaltosDelRescate)?.Mapa);
    }
}
