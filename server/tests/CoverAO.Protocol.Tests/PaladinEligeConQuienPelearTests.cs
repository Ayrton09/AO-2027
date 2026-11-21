using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>El Paladín elige con quién pelear (rondas 121 a 129: Milo, Paladina 23, murió 34 veces, 18 contra Ciro, Guerrero 25).</summary>
public sealed class PaladinEligeConQuienPelearTests
{
    [Theory]
    [InlineData(23, (short)25, GameLimits.ClaseGuerrero, 100, false)]    // Ciro
    [InlineData(23, (short)22, GameLimits.ClaseGuerrero, 100, true)]
    [InlineData(23, (short)24, 13, 100, false)]                          // Quim, Leñador
    [InlineData(23, (short)23, 13, 100, true)]
    [InlineData(23, (short)25, GameLimits.ClaseMago, 100, true)]         // el resto, hasta 2 por encima
    [InlineData(23, (short)26, GameLimits.ClaseMago, 100, false)]
    [InlineData(23, (short)27, GameLimits.ClaseGladiador, 20, true)]     // casi muerta, la remata
    public void Con_quien_pelea_el_paladin(int miNivel, short nivel, byte clase, int vida, bool pelea) =>
        Assert.Equal(pelea, Bot.PaladinLeConvienePelear(miNivel, nivel, (clase, vida)));

    [Fact]
    public void Se_va_con_la_mitad_de_la_vida() =>
        Assert.Equal(Bot.VidaParaSeguirPeleando, Bot.VidaParaSeguirPeleandoDe(GameLimits.ClasePaladin));
}
