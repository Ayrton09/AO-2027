using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// HUIR DE ALGO QUE TE SIGUE AL MISMO PASO NO SIRVE. En `verdad3`, de las 120 huidas de la corrida 91 fueron de un mismo bot con un
/// Lobo encima —84 de ellas con la criatura a UNA baldosa— y la racha más larga fue de treinta y seis huidas seguidas, con la
/// mochila llena de pociones. El contador de "acorralado" no lo veía: sólo contaba las huidas en las que el bot NO se movía, y ahí
/// el bot se movía siempre; el que no se movía era el problema, que lo seguía.
/// </summary>
public class HuidaQueNoSirveTests
{
    [Theory]
    // huidas sin moverse | huidas que no lo sacaron de encima | está acorralado
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]    // ninguna de las dos llega: todavía puede seguir intentando
    [InlineData(3, 0, true)]     // el rincón de siempre
    [InlineData(0, 3, true)]     // corrió las tres veces y lo siguió igual: el caso del Lobo
    [InlineData(0, 36, true)]    // la racha medida en `verdad3`
    public void Acorralado_es_tambien_no_podersela_sacar_de_encima(int sinMoverse, int queNoLoSacaron, bool acorralado)
    {
        Assert.Equal(acorralado, Bot.EstaAcorralado(sinMoverse, queNoLoSacaron));
    }

    [Theory]
    // a cuántas baldosas quedó la amenaza DESPUÉS de huir | la huida no sirvió
    [InlineData(-1, false)]   // ya no se la ve: la única que sirvió
    [InlineData(1, true)]     // pegada
    [InlineData(3, true)]     // en el borde del radio de susto: en la vuelta siguiente vuelve a huir
    [InlineData(4, false)]    // afuera: dejó de ser un problema
    [InlineData(9, false)]
    public void Huir_no_sirve_si_la_amenaza_queda_adentro_del_radio_de_susto(int pasosDespues, bool noSirvio)
    {
        Assert.Equal(noSirvio, Bot.LaHuidaNoSirvio(pasosDespues));
    }

    [Theory]
    // huidas seguidas de LA MISMA criatura en la ventana | hay que dejar de correr
    [InlineData(0, false)]
    [InlineData(2, false)]    // todavía puede probar
    [InlineData(3, true)]     // a la tercera, correr dejó de ser una estrategia
    [InlineData(37, true)]    // la racha que se midió en `verdad5`
    public void A_la_tercera_huida_de_la_misma_criatura_se_pelea(int huidas, bool dejaDeCorrer)
    {
        Assert.Equal(dejaDeCorrer, Bot.NoSeLoSacaDeEncima(huidas));
    }

    /// <summary>
    /// EL RESPIRO NO BORRA LA SERIE. Es lo que midió `verdad6` con la regla ya puesta: 53 huidas, CERO peleas. Huir despega a la
    /// criatura —queda a cuatro o cinco baldosas—, así que entre alcance y alcance el bot sale del peligro; si ese respiro
    /// reinicia el contador, la serie no pasa nunca de uno y la regla no entra jamás.
    /// </summary>
    [Fact]
    public void El_respiro_entre_un_alcance_y_el_siguiente_no_corta_la_serie()
    {
        var contadores = new Bot.ContadoresDeHuida();
        contadores.Huyo(501, 1_000, 60_000);
        contadores.SalioDelPeligro();                 // el Lobo quedó a cuatro baldosas: el peligro se terminó
        contadores.Huyo(501, 20_000, 60_000);
        contadores.SalioDelPeligro();
        contadores.Huyo(501, 40_000, 60_000);
        Assert.Equal(3, contadores.DeLaMismaCriatura);
        Assert.True(Bot.NoSeLoSacaDeEncima(contadores.DeLaMismaCriatura));
    }

    /// <summary>Y lo que el respiro SÍ tiene que borrar: los dos contadores que son de UNA huida.</summary>
    [Fact]
    public void El_respiro_borra_los_contadores_del_rincon()
    {
        var contadores = new Bot.ContadoresDeHuida { SinMoverse = 2, QueNoLoSacaron = 2 };
        contadores.SalioDelPeligro();
        Assert.Equal(0, contadores.SinMoverse);
        Assert.Equal(0, contadores.QueNoLoSacaron);
    }

    /// <summary>La serie la cortan las dos cosas que tienen que cortarla: otra criatura, o que haya pasado la ventana.</summary>
    [Fact]
    public void La_serie_se_corta_con_otra_criatura_y_con_el_reloj()
    {
        var contadores = new Bot.ContadoresDeHuida();
        contadores.Huyo(501, 1_000, 60_000);
        contadores.Huyo(501, 2_000, 60_000);
        contadores.Huyo(502, 3_000, 60_000);          // otro Lobo: es otro susto, empieza de nuevo
        Assert.Equal(1, contadores.DeLaMismaCriatura);
        contadores.Huyo(502, 4_000, 60_000);
        contadores.Huyo(502, 200_000, 60_000);        // dos minutos después: ya no es la misma persecución
        Assert.Equal(1, contadores.DeLaMismaCriatura);
    }

    /// <summary>Y LA REGLA VIEJA SIGUE VALIENDO tal cual para el que sólo mira el rincón.</summary>
    [Fact]
    public void La_regla_del_rincon_no_cambia()
    {
        Assert.False(Bot.EstaAcorralado(2));
        Assert.True(Bot.EstaAcorralado(3));
    }
}
