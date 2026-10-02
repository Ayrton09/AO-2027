using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// HUIR ES CORRER (02/10). Las criaturas dan un paso cada 400 ms y cada bot camina a su ritmo, de 0,8 a 1,3 veces el paso de
/// referencia: Aldo, el más lento, a 2,6 baldosas por segundo, y el Lobo a 2,5. En `early38`, huyendo de un Lobo en la cuna con la
/// vida casi llena, Aldo lo tuvo a dos o tres baldosas durante doce segundos una vez y veinticinco otra, hasta que lo alcanzó. Huyendo,
/// el bot da el paso del cliente con la tecla apretada (ver <see cref="Bot.CorriendoMinMs"/>). Y peleando o viajando no miraba lo que
/// se le arrimaba: ver <see cref="Bot.LoPeligrosoEstaATiro"/>.
/// </summary>
public class HuirEsCorrerTests
{
    private static readonly (byte X, byte Y)[] Lobo = { (60, 55) };

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

    /// <summary>Lo que se arrima a la distancia de la que el ciclo huye corta la pelea y el viaje.</summary>
    [Fact]
    public void Lo_peligroso_a_tres_pasos_corta_la_pelea()
    {
        Assert.True(Bot.LoPeligrosoEstaATiro((58, 56), Lobo), "a tres pasos");
        Assert.False(Bot.LoPeligrosoEstaATiro((58, 57), Lobo), "a cuatro, todavía no");
        Assert.False(Bot.LoPeligrosoEstaATiro((60, 56), Array.Empty<(byte, byte)>()));
    }

    /// <summary>LO QUE LO MATA DE DOS GOLPES CON LA VIDA QUE TIENE (ver <see cref="Bot.LoMataDeDosGolpesAhora"/>): el Lobo (golpe hasta
    /// 18) al de nivel 1, el Señor de las sombras (hasta 75) al de nivel 14; el Goblin (hasta 20) no a un nivel 9 con 60 de vida, salvo
    /// que ya le quede poca.</summary>
    [Fact]
    public void Lo_que_lo_mata_de_dos_golpes_es_con_la_vida_que_tiene()
    {
        var d = GameData.Load();
        if (d.Npc(501) is not { } lobo || d.Npc(534) is not { } senor || d.Npc(505) is not { } goblin) return;
        Assert.True(Bot.LoMataDeDosGolpesAhora(lobo, 17));
        Assert.False(Bot.LoMataDeDosGolpesAhora(lobo, 40));
        Assert.True(Bot.LoMataDeDosGolpesAhora(senor, 110));
        Assert.False(Bot.LoMataDeDosGolpesAhora(goblin, 60));
        Assert.True(Bot.LoMataDeDosGolpesAhora(goblin, 35), "con 35 de vida, el Goblin sí");
    }
}
