using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO DEL HUECO (`ropa1`, 28/09): tres de las cinco muertes fueron de Zombies del 13 a bots con pociones para los que el Zombie no
/// es presa ni peligro, así que con el Zombie pegándoles ni se iban ni le devolvían el golpe. Decidido por el autor (29/09): lo del
/// hueco que le pega lo hace huir y, en un mapa inseguro, si no se lo saca de encima, irse del mapa, al vecino donde nace menos de
/// lo que lo puede matar. Con las fichas y el mundo de verdad.
/// </summary>
public class LoDelHuecoTests
{
    private static GameData? Datos()
    {
        var datos = GameData.Load();
        return datos.Npc(502) is null || datos.MapExits.Count == 0 ? null : datos;
    }

    /// <summary>Los números de `ropa1`: Osmar, Carpintero de nivel 15 con 122 de vida; Rufo, Mago de nivel 13 con 107; Aldo,
    /// Leñador de nivel 16 con 163. Sin pociones no hay hueco: lo que no es presa ya es peligroso.</summary>
    [Fact]
    public void El_Zombie_es_del_hueco_para_Osmar_y_Rufo_y_presa_para_Aldo()
    {
        if (Datos() is not { } d) return;
        var zombie = d.Npc(502)!;
        Assert.True(Bot.EsDelHuecoPara(zombie, 122, 15, conPociones: true, curaVeneno: false));
        Assert.True(Bot.EsDelHuecoPara(zombie, 107, 13, conPociones: true, curaVeneno: false));

        Assert.True(Bot.EsPresaPara(zombie, 163, 16, conPociones: true, curaVeneno: false));
        Assert.False(Bot.EsDelHuecoPara(zombie, 163, 16, conPociones: true, curaVeneno: false));

        Assert.True(Bot.EsPeligrosoPara(zombie, 122, 15, conPociones: false, curaVeneno: false));
        Assert.False(Bot.EsDelHuecoPara(zombie, 122, 15, conPociones: false, curaVeneno: false));
    }

    /// <summary>Para un bot de 55 a 89 de vida el Lobo (golpe 18) también es del hueco; con 94 ya es presa. Lo que no es hostil
    /// no es del hueco aunque pegue fuerte: el Guerrero de Ullathorpe, que pega hasta 280.</summary>
    [Fact]
    public void El_Lobo_es_del_hueco_con_poca_vida_y_lo_que_no_es_hostil_nunca()
    {
        if (Datos() is not { } d) return;
        var lobo = d.Npc(501)!;
        Assert.True(Bot.EsDelHuecoPara(lobo, 75, 8, conPociones: true, curaVeneno: false));
        Assert.False(Bot.EsDelHuecoPara(lobo, 94, 8, conPociones: true, curaVeneno: false));
        var guerrero = d.Npc(6)!;
        Assert.True(guerrero is { Hostile: false, MaxHit: > 200 }, "premisa: el Guerrero de Ullathorpe no es hostil y pega fuerte");
        Assert.False(Bot.EsDelHuecoPara(guerrero, 300, 20, conPociones: true, curaVeneno: false));
    }

    /// <summary>
    /// DE UN MAPA SEGURO LO DEL HUECO NO LO SACA (`hueco1`): de la cuna salían por el Lobo y casi la mitad volvía antes de cinco
    /// minutos. Lo que no puede lastimar sí lo saca de cualquier mapa, y lo del hueco del 13, que es inseguro, también.
    /// </summary>
    [Fact]
    public void De_la_cuna_lo_del_hueco_no_lo_saca_del_mapa_y_del_13_si()
    {
        if (Datos() is not { } d) return;
        Assert.False(d.IsPkMap(37), "premisa: la cuna es segura");
        Assert.True(d.IsPkMap(13), "premisa: el 13 es inseguro");
        Assert.False(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: true, mapaInseguro: d.IsPkMap(37), huidasDeLaMismaCriatura: 3));
        Assert.True(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: true, mapaInseguro: d.IsPkMap(13), huidasDeLaMismaCriatura: 3));
        Assert.True(Bot.SeVaDelMapaPor(noLaLastima: true, esDelHueco: false, mapaInseguro: false, huidasDeLaMismaCriatura: 3));
        Assert.False(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: true, mapaInseguro: true, huidasDeLaMismaCriatura: 2));
        Assert.False(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: false, mapaInseguro: true, huidasDeLaMismaCriatura: 3));
    }

    /// <summary>
    /// ESCAPANDO DE UN ZOMBIE DEL 13, NO A OTRO MAPA DE ZOMBIES. Para Osmar, en el 12 y en el 19 no nace nada que lo mate, en el 553
    /// (la salida del norte) nacen tres Zombies y en el 15 cinco Arañas gigantes. Contando sólo lo peligroso, el 553 empataba en
    /// cero con el 12 y el 19, y quedaba antes que el 19.
    /// </summary>
    [Fact]
    public void Del_13_Osmar_no_sale_al_553_de_los_Zombies_si_tiene_otro_vecino()
    {
        if (Datos() is not { } d) return;
        const int nivel = 15, vida = 122;
        const byte bando = Player.StatusCiudadano;
        var ini = new ServerIni();
        Func<int, bool> sirve = mapa => Bot.PuedeEntrar(nivel, d.MapZone(mapa)) && !Bot.HayGuardiaQueVaPor(mapa, bando, nivel, d, ini);
        Func<int, int> soloPeligroso = mapa => d.MapSpawns.TryGetValue(mapa, out var nacen)
            ? nacen.Count(sp => Bot.EsPeligrosoParaElBot(d.Npc((short)sp.NpcIndex), vida, nivel, true, false, bando, d, ini)) : 0;
        Func<int, int> loPuedenMatar = mapa => Bot.CuantasLoPuedenMatar(d, ini, bando, mapa, vida, nivel, conPociones: true, curaVeneno: false);

        Assert.Equal(0, soloPeligroso(553));   // premisa: para él los Zombies del 553 no son peligrosos
        var antes = Bot.VecinosDelMenosPeligroso(d, 13, sirve, soloPeligroso).ToList();
        Assert.True(antes.IndexOf(553) < antes.IndexOf(19), "premisa: contando sólo lo peligroso, el 553 salía antes que el 19");

        Assert.Equal(3, loPuedenMatar(553));
        Assert.Equal(0, loPuedenMatar(12));
        Assert.Equal(0, loPuedenMatar(19));
        var orden = Bot.VecinosDelMenosPeligroso(d, 13, sirve, loPuedenMatar).ToList();
        Assert.True(orden.IndexOf(553) > orden.IndexOf(12) && orden.IndexOf(553) > orden.IndexOf(19));
        Assert.Equal(15, orden[^1]);
    }
}
