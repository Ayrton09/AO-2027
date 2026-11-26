using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>El Paladín elige con quién pelear (rondas 121 a 129: Milo, Paladina 23, murió 34 veces, 18 contra Ciro, Guerrero 25).</summary>
public sealed class PaladinEligeConQuienPelearTests
{
    [Fact]
    public void Se_va_con_la_mitad_de_la_vida() =>
        Assert.Equal(Bot.VidaParaSeguirPeleando, Bot.VidaParaSeguirPeleandoDe(GameLimits.ClasePaladin));
}
