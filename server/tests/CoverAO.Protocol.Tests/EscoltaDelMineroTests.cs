using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>El Minero va a la mina acompañado (pedido del tester del 20/11, decisión del autor del 22/11).</summary>
public sealed class EscoltaDelMineroTests
{
    private const byte Minero = 14;

    [Theory]
    [InlineData(Minero, true, 1_000, 0, true)]
    [InlineData(Minero, false, 1_000, 0, false)]          // una mina en un mapa seguro, no
    [InlineData(Minero, true, 1_000, 5_000, false)]       // la pidió hace poco
    [InlineData(GameLimits.ClaseGuerrero, true, 1_000, 0, false)]
    public void Cuando_pide_escolta(byte clase, bool insegura, long ahora, long proximo, bool pide) =>
        Assert.Equal(pide, Bot.PideEscolta(clase, insegura, ahora, proximo));

    [Theory]
    [InlineData(GameLimits.ClaseGuerrero, (short)25, true)]
    [InlineData(GameLimits.ClaseMago, (short)20, true)]
    [InlineData(13, (short)24, false)]                       // un Leñador no
    [InlineData(GameLimits.ClaseGuerrero, SentidosDelBot.NivelEscondido, false)]
    public void Quien_sirve_de_escolta(byte clase, short nivel, bool sirve) =>
        Assert.Equal(sirve, Bot.SirveDeEscolta((clase, 100), nivel));

    [Fact]
    public void Sin_ficha_no_sirve() => Assert.False(Bot.SirveDeEscolta(null, 25));

    /// <summary>El que lo escucha la toma por una invitación, como la de una persona.</summary>
    [Fact]
    public void La_frase_es_una_invitacion() => Assert.True(Bot.EsInvitacion(Bot.FraseDeLaEscolta, "Ciro"));
}
