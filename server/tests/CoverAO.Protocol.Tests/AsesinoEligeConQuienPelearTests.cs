using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El Asesino elige con quién pelear (rondas 119 a 123: Juana, Asesina 21, murió 9 veces, contra un Guerrero 25, un Leñador 24, un
/// Nigromante 22 y un Paladín 23 entre otros) y pelea de cerca con el combo, como el Clérigo.
/// </summary>
public sealed class AsesinoEligeConQuienPelearTests
{
    [Theory]
    [InlineData(21, (short)25, GameLimits.ClaseGuerrero, 100, false)]    // Ciro
    [InlineData(21, (short)20, GameLimits.ClaseGuerrero, 100, true)]     // de menos nivel, sí
    [InlineData(21, (short)22, GameLimits.ClaseNigromante, 100, false)]  // Kael
    [InlineData(22, (short)23, GameLimits.ClasePaladin, 100, false)]     // Milo
    [InlineData(22, (short)22, GameLimits.ClasePaladin, 100, true)]      // de su nivel, sí
    [InlineData(21, (short)24, 13, 100, false)]                          // Quim, Leñador
    [InlineData(21, (short)23, 13, 100, true)]                           // a un oficio, hasta 2 por encima
    [InlineData(21, (short)25, GameLimits.ClaseGuerrero, 25, true)]      // casi muerto, lo remata
    [InlineData(21, SentidosDelBot.NivelEscondido, 13, 10, false)]
    public void Con_quien_pelea_el_asesino(int miNivel, short nivel, byte clase, int vida, bool pelea) =>
        Assert.Equal(pelea, Bot.AsesinoLeConvienePelear(miNivel, nivel, (clase, vida)));

    [Fact]
    public void Se_va_con_la_mitad_de_la_vida_como_el_mago() =>
        Assert.Equal(Bot.VidaParaSeguirPeleando, Bot.VidaParaSeguirPeleandoDe(GameLimits.ClaseAsesino));

    /// <summary>La premisa de no usar la daga: la mejor que se vende pega menos que la Spatha, y apuñalar con la habilidad baja
    /// casi no sale.</summary>
    [Fact]
    public void La_daga_pega_menos_que_la_spatha()
    {
        var datos = GameData.Load();
        Assert.True(datos.Obj(366) is { Apunala: 1, Value: > 0 } daga && datos.Obj(569) is { Apunala: 0 } spatha
                    && daga.MaxHit < spatha.MinHit + 5);
        Assert.Equal(4, Combat.SuerteApunalar(GameLimits.ClaseAsesino, 3));
        Assert.True(Combat.SuerteApunalar(GameLimits.ClaseAsesino, 100) <= 35);
    }
}
