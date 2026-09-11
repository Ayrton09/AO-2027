using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA VALENTÍA SEGÚN LAS POCIONES, contra las fichas de verdad. En `sano1` los tres bots con pociones no murieron nunca y los
/// cinco sin pociones murieron 21 veces en media hora, casi todas contra el Escorpión y el Lobo: sin nada que tomar el margen
/// tiene que ser mayor, lo que envenena sin cura queda afuera, y lo hostil que no es presa se esquiva.
/// </summary>
public class ValentiaDelBotTests
{
    private static GameData? Datos()
    {
        var datos = GameData.Load();
        return datos.Npc(501) is null ? null : datos;
    }

    /// <summary>EL HUECO, CERRADO PARA EL QUE NO TIENE POCIONES: el Lobo (golpe 18) contra una Guerrera de 58 de vida no es presa,
    /// y sin pociones se esquiva. Con pociones queda como estaba.</summary>
    [Fact]
    public void Sin_pociones_el_lobo_que_no_es_presa_se_esquiva()
    {
        if (Datos() is not { } d) return;
        var lobo = d.Npc(501)!;
        Assert.False(Bot.EsPresaPara(lobo, 58, 6, conPociones: false, curaVeneno: false));
        Assert.True(Bot.EsPeligrosoPara(lobo, 58, 6, conPociones: false, curaVeneno: false));
        Assert.False(Bot.EsPeligrosoPara(lobo, 58, 6, conPociones: true, curaVeneno: false));
    }

    /// <summary>SIN POCIONES LA VALENTÍA BAJA A LA MITAD: el Lobo es presa para un Gladiador de 94 con pociones y deja de serlo
    /// sin ellas. Lo chico sigue siendo presa y no asusta.</summary>
    [Fact]
    public void Sin_pociones_la_valentia_baja_a_la_mitad()
    {
        if (Datos() is not { } d) return;
        var lobo = d.Npc(501)!;
        Assert.True(Bot.EsPresaPara(lobo, 94, 8, conPociones: true, curaVeneno: false));
        Assert.False(Bot.EsPresaPara(lobo, 94, 8, conPociones: false, curaVeneno: false));
        var gallina = d.Npc(594)!;
        Assert.True(Bot.EsPresaPara(gallina, 50, 5, conPociones: false, curaVeneno: false));
        Assert.False(Bot.EsPeligrosoPara(gallina, 50, 5, conPociones: false, curaVeneno: false));
    }

    /// <summary>CON LA VIDA EN ROJO Y SIN POCIONES, EL ÁGUILA ASUSTA aunque sea presa. Con pociones o con la vida alta, no.</summary>
    [Fact]
    public void Con_la_vida_en_rojo_y_sin_pociones_hasta_la_presa_asusta()
    {
        if (Datos() is not { } d) return;
        var aguila = d.Npc(532)!;
        Assert.True(Bot.EsPresaPara(aguila, 50, 5, conPociones: false, curaVeneno: false));
        Assert.True(Bot.AsustaConLaVidaEnRojo(aguila, conPociones: false, salud: 0.30));
        Assert.False(Bot.AsustaConLaVidaEnRojo(aguila, conPociones: true, salud: 0.30));
        Assert.False(Bot.AsustaConLaVidaEnRojo(aguila, conPociones: false, salud: 0.90));
    }

    /// <summary>CON LA VIDA EN ROJO Y SIN POCIONES, HUIR ES EL SACERDOTE A MANO O LA RUNA fuera de un mapa pk. Con pociones, con la
    /// vida alta, en un mapa pk o con la runa recién usada, se aleja caminando.</summary>
    [Fact]
    public void Con_la_vida_en_rojo_y_sin_pociones_huir_es_el_sacerdote_o_la_runa()
    {
        Assert.Equal(Huida.Sacerdote, Bot.ComoHuye(conPociones: false, salud: 0.30, sacerdoteAMano: true, mapaSinPk: true, runaLista: true));
        Assert.Equal(Huida.Sacerdote, Bot.ComoHuye(conPociones: false, salud: 0.30, sacerdoteAMano: true, mapaSinPk: false, runaLista: false));
        Assert.Equal(Huida.Runa, Bot.ComoHuye(conPociones: false, salud: 0.30, sacerdoteAMano: false, mapaSinPk: true, runaLista: true));
        Assert.Equal(Huida.Alejarse, Bot.ComoHuye(conPociones: false, salud: 0.30, sacerdoteAMano: false, mapaSinPk: false, runaLista: true));
        Assert.Equal(Huida.Alejarse, Bot.ComoHuye(conPociones: false, salud: 0.30, sacerdoteAMano: false, mapaSinPk: true, runaLista: false));
        Assert.Equal(Huida.Alejarse, Bot.ComoHuye(conPociones: true, salud: 0.30, sacerdoteAMano: true, mapaSinPk: true, runaLista: true));
        Assert.Equal(Huida.Alejarse, Bot.ComoHuye(conPociones: false, salud: 0.90, sacerdoteAMano: true, mapaSinPk: true, runaLista: true));
    }

    /// <summary>NO SE EMPIEZA UNA PELEA A LA VISTA DE ALGO PELIGROSO: las criaturas que persiguen ven 8 baldosas a los costados y 6
    /// arriba y abajo, contadas por separado, como el original.</summary>
    [Fact]
    public void Una_pelea_no_se_empieza_a_la_vista_de_algo_peligroso()
    {
        Assert.True(Bot.AlAlcanceDeSuVista((50, 50), (58, 56)));
        Assert.True(Bot.AlAlcanceDeSuVista((50, 50), (42, 44)));
        Assert.False(Bot.AlAlcanceDeSuVista((50, 50), (59, 50)));
        Assert.False(Bot.AlAlcanceDeSuVista((50, 50), (50, 57)));
    }

    /// <summary>SIN POCIONES NI CURA, LO QUE ENVENENA NO ES PRESA: el Escorpión chico (golpe 3, veneno) sí lo es con pociones o con
    /// la poción que cura el veneno.</summary>
    [Fact]
    public void Sin_pociones_ni_cura_el_veneno_no_es_presa()
    {
        if (Datos() is not { } d) return;
        var escorpion = d.Npc(564)!;
        Assert.True(Bot.EsPresaPara(escorpion, 50, 5, conPociones: true, curaVeneno: false));
        Assert.False(Bot.EsPresaPara(escorpion, 50, 5, conPociones: false, curaVeneno: false));
        Assert.True(Bot.EsPresaPara(escorpion, 50, 5, conPociones: false, curaVeneno: true));
    }
}
