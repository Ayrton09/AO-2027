using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// APRENDER DE LA HUIDA (decisión del autor, 01/10): el mapa del que ya se fue huyendo dos veces no se elige de coto hasta subir de
/// nivel (<see cref="Bot.HuidasDelNivel"/>), sin coto en un mapa inseguro se camina al seguro más cercano, y lo del hueco que ya le
/// pega corta el viaje (<see cref="Bot.LoDelHuecoCortaElViaje"/>). Medido en `early11`: Petra veinte minutos entre el 275 y el 392,
/// y Aldo muerto cruzando el 19 con los Lobos encima.
/// </summary>
public class AprenderDeLaHuidaTests
{
    [Fact]
    public void A_la_segunda_huida_del_mismo_mapa_no_vuelve_a_cazar_ahi()
    {
        var huidas = new Bot.HuidasDelNivel();
        huidas.Anotar(275, 8);
        Assert.False(huidas.NoVuelve(275, 8), "una sola huida no alcanza: la marca de cinco minutos ya lo cubre");
        huidas.Anotar(275, 8);
        Assert.True(huidas.NoVuelve(275, 8));
        Assert.False(huidas.NoVuelve(392, 8), "el otro mapa no tiene nada que ver");
    }

    [Fact]
    public void Al_subir_de_nivel_se_olvidan_las_huidas()
    {
        var huidas = new Bot.HuidasDelNivel();
        huidas.Anotar(275, 8);
        huidas.Anotar(275, 8);
        Assert.False(huidas.NoVuelve(275, 9));
        huidas.Anotar(275, 9);
        Assert.False(huidas.NoVuelve(275, 9), "la cuenta vuelve a empezar en el nivel nuevo");
    }

    /// <summary>
    /// CONTRA LA DATA: para Aldo en `early11` (nivel 6, 61 de vida, con pociones) el Lobo es del hueco, y adyacente en un mapa
    /// inseguro corta el viaje. En un mapa seguro, o a dos baldosas, no; y sin nada del hueco a la vista, tampoco.
    /// </summary>
    [Fact]
    public void Lo_del_hueco_que_le_pega_en_un_mapa_inseguro_corta_el_viaje()
    {
        var d = GameData.Load();
        if (d.Npc(501) is not { } lobo) return;
        Assert.True(Bot.EsDelHuecoPara(lobo, 61, 6, conPociones: true, curaVeneno: false),
            "premisa: para un nivel 6 de 61 de vida el Lobo es del hueco");
        Assert.True(d.IsPkMap(19) && !d.IsPkMap(37), "premisa: el 19 es inseguro y la cuna no");
        Assert.True(Bot.LoDelHuecoCortaElViaje(d.IsPkMap(19), lobo, 1));
        Assert.False(Bot.LoDelHuecoCortaElViaje(d.IsPkMap(37), lobo, 1), "en un mapa seguro lo del hueco no es la amenaza");
        Assert.True(Bot.LoDelHuecoCortaElViaje(d.IsPkMap(37), lobo, 1, salud: 0.3), "salvo con la vida en rojo (decisión del 02/10)");
        Assert.False(Bot.LoDelHuecoCortaElViaje(d.IsPkMap(37), lobo, 1, salud: 0.5), "con la vida a la mitad, en la cuna sigue");
        Assert.False(Bot.LoDelHuecoCortaElViaje(d.IsPkMap(19), lobo, 2), "todavía no le pega");
        Assert.False(Bot.LoDelHuecoCortaElViaje(d.IsPkMap(19), null, null));
    }

    /// <summary>
    /// Y DESDE EL 392, DONDE PETRA SE QUEDABA SIN COTO, HAY UN MAPA SEGURO AL QUE SE LLEGA A PIE: el 392 es pk (la runa no anda) y
    /// no tiene criaturas.
    /// </summary>
    [Fact]
    public void Desde_el_392_hay_un_mapa_seguro_al_que_se_llega_a_pie()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        Assert.True(d.IsPkMap(392) && (!d.MapSpawns.TryGetValue(392, out var nacen) || nacen.Count == 0),
            "premisa: el 392 es pk y sin criaturas");
        var mapas = new Navegante(d);
        foreach (byte bando in new[] { Player.StatusCiudadano, Player.StatusRepublicano })
        {
            var seguro = mapas.MapaMasCercano(392, (88, 16), m => Bot.MapaSeguroPara(m, bando, 8, d, ini),
                                              m => Bot.PuedeEntrar(8, d.MapZone(m)));
            Assert.True(seguro is not null, $"bando {bando}: desde el 392 no hay mapa seguro a pie");
            Assert.False(d.IsPkMap(seguro!.Value.Mapa));
        }
    }

    /// <summary>
    /// ESCAPANDO, NO A UN BOLSILLO (ver <see cref="Bot.LaPuertaDeLaHuidaDaAUnBolsillo"/>): desde donde Rufo (`early99`) y Lira
    /// (`early70`) se fueron del 19 huyendo de un Lobo, la puerta más cercana al Río Otoren (98) da a su parte norte, que sólo sale
    /// al 19. Por otro vecino, no.
    /// </summary>
    [Fact]
    public void Del_19_la_puerta_mas_cercana_al_98_da_a_un_bolsillo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var mapas = new Navegante(d);
        Func<int, bool> pisable = m => Bot.PuedeEntrar(10, d.MapZone(m));
        Assert.True(d.IsPkMap(98), "premisa: el 98 es inseguro, ahí la runa no anda");
        Assert.True(Bot.LaPuertaDeLaHuidaDaAUnBolsillo(mapas, d, 19, (22, 43), 98, pisable), "desde donde huyó Rufo");
        Assert.True(Bot.LaPuertaDeLaHuidaDaAUnBolsillo(mapas, d, 19, (10, 9), 98, pisable), "desde donde huyó Lira");
        var otros = Bot.VecinosDelMenosPeligroso(d, 19, m => m != 98 && pisable(m), _ => 0).ToList();
        Assert.Contains(otros, v => !Bot.LaPuertaDeLaHuidaDaAUnBolsillo(mapas, d, 19, (22, 43), v, pisable));
    }
}
