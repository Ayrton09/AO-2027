using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// HUIR ES CORRER (02/10). Las criaturas dan un paso cada 400 ms y cada bot camina a su ritmo, de 0,8 a 1,3 veces el paso de
/// referencia: Aldo, el más lento, a 2,6 baldosas por segundo, y el Lobo a 2,5. En `early38`, huyendo de un Lobo en la cuna con la
/// vida casi llena, Aldo lo tuvo a dos o tres baldosas durante doce segundos una vez y veinticinco otra, hasta que lo alcanzó. Huyendo,
/// el bot da el paso del cliente con la tecla apretada (ver <see cref="Bot.CorriendoMinMs"/>).
/// </summary>
public class HuirEsCorrerTests
{
    /// <summary>LA PREMISA: corriendo, hasta el paso más largo le saca a la criatura que lo persigue más de una baldosa y media por
    /// segundo.</summary>
    [Fact]
    public void Corriendo_se_le_saca_distancia_a_lo_que_persigue()
    {
        int pasoDeLaCriatura = NpcSystem.MoveEveryTicks * GameLoop.TickMs;
        Assert.Equal(400, pasoDeLaCriatura);
        double leSaca = 1000.0 / Bot.CorriendoMaxMs - 1000.0 / pasoDeLaCriatura;
        Assert.True(leSaca >= 1.5, $"le saca {leSaca:0.0} baldosas por segundo");
    }
}
