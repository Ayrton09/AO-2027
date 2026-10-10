using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO DEL HUECO (`ropa1`, 28/09): tres de las cinco muertes fueron de Zombies del 13 a bots con pociones para los que el Zombie no
/// es presa ni peligro, así que con el Zombie pegándoles ni se iban ni le devolvían el golpe. Decidido por el autor (29/09): en un
/// mapa inseguro, lo del hueco que le pega lo hace huir y, si no se lo saca de encima, irse del mapa, al vecino donde nace menos de
/// lo que lo puede matar; en uno seguro, nada cambia. Con las fichas y el mundo de verdad.
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
    /// EN LA CUNA LO DEL HUECO NO ES AMENAZA (`hueco3`): un Paladín de 79 de vida pasó cuarenta minutos entre el 9 y el 10 huyendo
    /// del Lobo y devolviéndole el golpe. En el 13, que es inseguro, el Zombie pegado sí; a dos pasos todavía no, y si algo peligroso
    /// está más cerca, la amenaza es eso.
    /// </summary>
    [Fact]
    public void En_la_cuna_el_Lobo_del_hueco_no_es_amenaza_y_en_el_13_el_Zombie_si()
    {
        if (Datos() is not { } d) return;
        var lobo = d.Npc(501)!;
        var zombie = d.Npc(502)!;
        Assert.True(Bot.EsDelHuecoPara(lobo, 79, 9, conPociones: true, curaVeneno: false), "premisa: con 79 de vida el Lobo es del hueco");
        Assert.False(Bot.LoDelHuecoEsLaAmenaza(d.IsPkMap(37), lobo, pasos: 1, pasosDelPeligro: null));
        Assert.True(Bot.LoDelHuecoEsLaAmenaza(d.IsPkMap(13), zombie, pasos: 1, pasosDelPeligro: null));
        Assert.False(Bot.LoDelHuecoEsLaAmenaza(d.IsPkMap(13), zombie, pasos: 2, pasosDelPeligro: null));
        Assert.False(Bot.LoDelHuecoEsLaAmenaza(d.IsPkMap(13), zombie, pasos: 1, pasosDelPeligro: 0));
        Assert.True(Bot.LoDelHuecoEsLaAmenaza(d.IsPkMap(13), zombie, pasos: 1, pasosDelPeligro: 3));
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

    /// <summary>Sin fuelle para pelear, la pelea que se ganaría no se gana, y en un mapa inseguro se va del mapa (Fabia en early181).</summary>
    [Fact]
    public void Sin_energia_la_pelea_no_se_gana_y_se_va_del_mapa_inseguro()
    {
        Assert.False(Bot.SeGanaConEnergia(tieneFuelle: false, laPeleaSeGana: true));
        Assert.True(Bot.SeGanaConEnergia(tieneFuelle: true, laPeleaSeGana: true));
        Assert.False(Bot.SeGanaConEnergia(tieneFuelle: true, laPeleaSeGana: false));
        Assert.True(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: false, mapaInseguro: true, huidasDeLaMismaCriatura: 3,
                                       laPeleaSeGana: Bot.SeGanaConEnergia(tieneFuelle: false, laPeleaSeGana: true)));
        Assert.False(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: false, mapaInseguro: false, huidasDeLaMismaCriatura: 3,
                                        laPeleaSeGana: Bot.SeGanaConEnergia(tieneFuelle: false, laPeleaSeGana: true)));
    }

    /// <summary>Sin fuelle en un mapa inseguro va al mapa seguro, no al vecino (autor, 09/10); con fuelle, o en uno seguro, no.</summary>
    [Fact]
    public void Sin_fuelle_en_un_mapa_inseguro_va_al_seguro()
    {
        Assert.True(Bot.SinFuelleVaAlSeguro(tieneFuelle: false, mapaInseguro: true));
        Assert.False(Bot.SinFuelleVaAlSeguro(tieneFuelle: true, mapaInseguro: true));
        Assert.False(Bot.SinFuelleVaAlSeguro(tieneFuelle: false, mapaInseguro: false));
    }

    /// <summary>LO PELIGROSO QUE NO LE GANA, FUERA DEL MAPA, PERO SÓLO EN UNO INSEGURO (autor, 03/10): como lo del hueco.</summary>
    [Fact]
    public void Lo_peligroso_que_no_le_gana_lo_saca_de_un_mapa_inseguro()
    {
        Assert.True(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: false, mapaInseguro: true, huidasDeLaMismaCriatura: 3, laPeleaSeGana: false));
        Assert.False(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: false, mapaInseguro: false, huidasDeLaMismaCriatura: 3, laPeleaSeGana: false));
        Assert.False(Bot.SeVaDelMapaPor(noLaLastima: false, esDelHueco: false, mapaInseguro: true, huidasDeLaMismaCriatura: 2, laPeleaSeGana: false));
    }

    private static NpcInfo Criatura(int minHit, int maxHit, int vida) =>
        new(Index: 900, Name: "de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: vida, MinHit: minHit, MaxHit: maxHit, Def: 0,
            GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true);

    /// <summary>
    /// LA PELEA SE GANA SI LA MATA ANTES DE QUE ELLA LE SAQUE LA VIDA QUE TIENE: Aldo, Leñador de 219 de vida, contra un Ent
    /// (golpe 40-90, 650 de vida), si lo mata en 26 golpes tarda unos 36 s, en los que el Ent le pega 13 veces, 845: no gana (murió
    /// así en `tardeN9` y `tardeN10`). Contra el Lobo de la cuna (golpe 12-18, 75 de vida), si lo mata en 6 golpes, un bot de 80 de
    /// vida recibe tres, 45: gana. Si no le saca nada, no se gana; sin medida del golpe, se gana si no lo mata de dos golpes con la
    /// vida de ahora (lo que eligió el autor): al Lobo sí (36 contra 80), al Ent no (180 contra 150).
    /// </summary>
    [Fact]
    public void La_pelea_se_gana_si_la_mata_antes()
    {
        Assert.False(Bot.LaPeleaSeGana(26, 219, Criatura(40, 90, 650)));
        Assert.True(Bot.LaPeleaSeGana(6, 80, Criatura(12, 18, 75)));
        Assert.True(Bot.LaPeleaSeGana(null, 80, Criatura(12, 18, 75)));
        Assert.False(Bot.LaPeleaSeGana(null, 150, Criatura(40, 90, 650)));
        Assert.False(Bot.LaPeleaSeGana(int.MaxValue, 80, Criatura(12, 18, 75)));
    }

    /// <summary>
    /// LA SALIDA QUE NO LO SACÓ SE SALTEA UN RATO (`hueco2`, `hueco3`): escapando, los bots elegían siempre la esquina (9,7) del 13,
    /// tapada por un Zombie, y morían ahí intentándolo. La vez siguiente prueba por otro vecino; pasado el rato, otra vez por esa.
    /// </summary>
    [Fact]
    public void La_salida_que_no_lo_saco_se_saltea_un_rato()
    {
        var queNoAndan = new Dictionary<(int Aca, int Vecino), long> { [(13, 12)] = 300_000 };
        Assert.False(Bot.SePruebaEsaSalida(queNoAndan, 13, 12, ahora: 100_000));
        Assert.True(Bot.SePruebaEsaSalida(queNoAndan, 13, 19, ahora: 100_000), "por otro vecino, sí");
        Assert.True(Bot.SePruebaEsaSalida(queNoAndan, 12, 12, ahora: 100_000), "desde otro mapa, sí");
        Assert.True(Bot.SePruebaEsaSalida(queNoAndan, 13, 12, ahora: 300_000), "pasado el rato, se vuelve a probar");
    }

    /// <summary>
    /// DEL MAPA DEL QUE RECIÉN HUYÓ, NI DE PASO (`hueco2`): una Ladrona se fue del 19 por un Lobo que no lastimaba, salió al 98, y el
    /// viaje al coto del 27 la volvía a meter al 19 por la misma baldosa: 520 viajes en 35 minutos. Por un rato no se cruza —salvo
    /// que sea el destino—, y después sí.
    /// </summary>
    [Fact]
    public void Del_mapa_del_que_recien_huyo_no_se_vuelve_a_cruzar_por_un_rato()
    {
        var huyoDe = new Dictionary<int, long> { [19] = 300_000 };
        Assert.True(Bot.NoVuelveACruzar(huyoDe, 19, mapaDestino: 27, ahora: 100_000), "de paso al 27, no");
        Assert.False(Bot.NoVuelveACruzar(huyoDe, 19, mapaDestino: 19, ahora: 100_000), "si el destino es ese mismo, sí");
        Assert.False(Bot.NoVuelveACruzar(huyoDe, 97, mapaDestino: 27, ahora: 100_000), "por otro mapa, sí");
        Assert.False(Bot.NoVuelveACruzar(huyoDe, 19, mapaDestino: 27, ahora: 300_000), "pasado el rato, sí");
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

    /// <summary>El viaje agotado esquivando peligros (la opción C de Fabia, 10/10): la mitad o más de los tramos cortados por un
    /// peligro. Fabia en `early181`: 10 de 12.</summary>
    [Fact]
    public void El_viaje_que_se_agota_esquivando_peligros_pide_el_rescate()
    {
        Assert.True(Bot.AgotadoPorPeligro(10, 12));
        Assert.True(Bot.AgotadoPorPeligro(6, 12));
        Assert.False(Bot.AgotadoPorPeligro(5, 12));
        Assert.False(Bot.AgotadoPorPeligro(0, 0));
    }
}
