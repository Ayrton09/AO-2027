using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS VIAJES DE MÁS DEL EARLY (DECIDIDO POR EL AUTOR, 07/10): el envenenado con pociones para pelear no camina a un templo de otro mapa
/// (<c>Bot.VaAlTemploPorElVeneno</c>), y nadie sale de viaje por una pieza de equipo de menos de 1000 (<c>Bot.CuestaComoParaElViaje</c>).
/// </summary>
public class ElEarlySinViajesDeMasTests
{
    [Fact]
    public void Envenenado_con_pociones_no_va_a_un_templo_de_otro_mapa()
    {
        Assert.False(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 200, temploEnEsteMapa: false));
        Assert.False(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 10, temploEnEsteMapa: false));
    }

    [Fact]
    public void Envenenado_sin_pociones_o_con_el_templo_aca_va()
    {
        Assert.True(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 9, temploEnEsteMapa: false));
        Assert.True(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 0, temploEnEsteMapa: false));
        Assert.True(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 200, temploEnEsteMapa: true));
    }

    [Fact]
    public void Los_escudos_baratos_no_valen_el_viaje_y_la_primera_armadura_si()
    {
        Assert.False(Bot.CuestaComoParaElViaje(300));    // Escudo de Tortuga
        Assert.False(Bot.CuestaComoParaElViaje(500));    // Escudo de Madera
        Assert.False(Bot.CuestaComoParaElViaje(999));
        Assert.True(Bot.CuestaComoParaElViaje(1_000));
        Assert.True(Bot.CuestaComoParaElViaje(3_000));   // Armadura de Cuero
    }
}
