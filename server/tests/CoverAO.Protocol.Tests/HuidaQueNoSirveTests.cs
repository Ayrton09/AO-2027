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

    /// <summary>Y LA REGLA VIEJA SIGUE VALIENDO tal cual para el que sólo mira el rincón.</summary>
    [Fact]
    public void La_regla_del_rincon_no_cambia()
    {
        Assert.False(Bot.EstaAcorralado(2));
        Assert.True(Bot.EstaAcorralado(3));
    }
}
