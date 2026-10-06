using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA ESCALERA DE MEJORAS (investigación del 06/10 sobre mid105-116): el bot viajaba por cualquier pieza un punto mejor, y medía el
/// arma sólo por el golpe máximo.
/// </summary>
public class LaEscaleraDelEquipoTests
{
    /// <summary>EL ARMA SE MIDE CON EL MÍNIMO Y EL MÁXIMO: la Hacha Orca (18-23) no le gana al Martillo de Guerra (22-22). Con el
    /// máximo solo, Ivo la compraba en 11 de 12 corridas y pegaba menos.</summary>
    [Fact]
    public void La_Hacha_Orca_no_le_gana_al_Martillo_de_Guerra()
    {
        var datos = GameData.Load();
        var hacha = datos.Objects.Values.FirstOrDefault(o => o.Name == "Hacha Orca");
        var martillo = datos.Objects.Values.FirstOrDefault(o => o.Name == "Martillo de Guerra");
        if (hacha is null || martillo is null) return;
        Assert.True(hacha.MaxHit > martillo.MaxHit, "la premisa: por el máximo, el hacha parecía mejor");
        Assert.True(Bot.ValorDeCombate(hacha) < Bot.ValorDeCombate(martillo));
    }

    /// <summary>SE VIAJA POR UN SALTO, NO POR UN ESCALÓN: una pieza que no tiene sí; si tiene, desde un 20 % más.</summary>
    [Theory]
    [InlineData(30, 0, true)]     // no tiene nada de eso
    [InlineData(60, 56, false)]   // la Túnica Arcana contra la de Druida: +7 %
    [InlineData(56, 50, false)]   // +12 %
    [InlineData(60, 50, true)]    // +20 %
    [InlineData(24, 6, true)]
    public void Se_viaja_por_un_salto_no_por_un_escalon(int nuevo, int tengo, bool viaja)
    {
        Assert.Equal(viaja, Bot.ElSaltoValeElViaje(nuevo, tengo));
    }
}
