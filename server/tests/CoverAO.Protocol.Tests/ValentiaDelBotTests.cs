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
