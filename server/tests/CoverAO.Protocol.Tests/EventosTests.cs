using System.Linq;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// /EVENTOS CONTESTA — el handler que en el original está entero comentado.
///
/// <para><c>HandleHayEventos</c> (Servidor/Codigo/Protocol.bas:16448) tiene las tres líneas escritas y
/// TACHADAS: la de experiencia, la de oro y el "No hay eventos en curso.". El resultado es que el botón
/// "¿Hay eventos?" del cliente y el comando <c>/EVENTOS</c> mandan su paquete y no reciben nada, que para el
/// jugador no se distingue de un botón roto.</para>
///
/// <para>De este lado pesa más que allá porque la perilla SÍ existe: el panel de administración tiene los
/// tres multiplicadores del frmMain del VB6 (exp/oro/drop, ×1..50). Sin esto, un GM prende experiencia ×5 y
/// no hay una sola forma de que el jugador se entere.</para>
///
/// <para>Lo que se fija acá son los TEXTOS y la rama vacía, que es lo que un cambio distraído se lleva
/// puesto. El camino de que el mensaje salga por el socket es el mismo <c>ConsoleMsg</c> de las otras
/// consultas (/ONLINE, /UPTIME) y ya está cubierto; lo que no estaba cubierto por nada es QUÉ dice.</para>
/// </summary>
public class EventosTests
{
    /// <summary>Sin ningún multiplicador tocado contesta UNA línea, y es la del autor. Ésta es la respuesta
    /// que va a ver el jugador el 99 % de las veces: si esta rama se cae, el botón vuelve a ser mudo y no lo
    /// nota nadie hasta que alguien lo aprieta.</summary>
    [Fact]
    public void Sin_eventos_contesta_que_no_hay()
    {
        var lineas = PacketDispatcher.LineasDeEventos(1, 1, 1);
        Assert.Equal(new[] { "No hay eventos en curso." }, lineas);
    }

    /// <summary>Cada multiplicador aporta SU línea, con el texto del autor y el número adentro.</summary>
    [Theory]
    [InlineData(5, 1, 1, "¡El evento 'Experiencia x5' se encuentra en curso!")]
    [InlineData(1, 3, 1, "¡El evento 'Oro x3' se encuentra en curso!")]
    [InlineData(1, 1, 50, "¡El evento 'Drop x50' se encuentra en curso!")]
    public void Un_evento_prendido_se_anuncia_con_su_texto(int exp, int oro, int drop, string esperada)
    {
        Assert.Equal(new[] { esperada }, PacketDispatcher.LineasDeEventos(exp, oro, drop));
    }

    /// <summary>Los tres a la vez son TRES líneas, en el orden del original (experiencia, oro) con el drop
    /// —que es el agregado— tercero. Y no se cuela el "No hay eventos en curso.": la rama vacía es
    /// excluyente, no un pie de página.</summary>
    [Fact]
    public void Los_tres_prendidos_dan_tres_lineas_en_orden()
    {
        var lineas = PacketDispatcher.LineasDeEventos(2, 4, 8);
        Assert.Equal(new[]
        {
            "¡El evento 'Experiencia x2' se encuentra en curso!",
            "¡El evento 'Oro x4' se encuentra en curso!",
            "¡El evento 'Drop x8' se encuentra en curso!",
        }, lineas);
        Assert.DoesNotContain(lineas, l => l.Contains("No hay eventos"));
    }

    /// <summary>El umbral es ESTRICTO: 1 es "sin evento", no "evento ×1". Un anuncio de "Experiencia x1" no
    /// sólo sobra, miente sobre que está pasando algo.</summary>
    [Fact]
    public void El_multiplicador_en_uno_no_es_un_evento()
    {
        Assert.DoesNotContain(PacketDispatcher.LineasDeEventos(1, 1, 1), l => l.Contains("x1"));
        // Y con uno solo prendido, los otros dos no aportan nada: una sola línea.
        Assert.Single(PacketDispatcher.LineasDeEventos(1, 2, 1));
    }
}
