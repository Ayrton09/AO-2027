using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS CONTADORES DE LAS CRIATURAS CORREN EN OTRO RELOJ. El original tiene DOS: el <c>GameTimer</c> de 40 ms, que baja los
/// contadores de los jugadores con <c>DeltaTick</c>, y el <c>TIMER_AI</c> de 380 ms (frmMain.frm:170-171), que baja los de las
/// criaturas de a UNO FIJO (General.bas:941, llamado desde mMainLoop.bas:90 y :95). Leer las vueltas de una criatura como si
/// fueran del game-loop las hace durar 9,5 veces menos.
/// </summary>
public class ContadoresDeCriaturaTests
{
    /// <summary>LAS 30 VUELTAS DEL GOLPE SON 11,4 SEGUNDOS, no 1,2.</summary>
    [Fact]
    public void Las_vueltas_de_una_criatura_valen_trescientos_ochenta_milisegundos()
    {
        Assert.Equal(380, GameLimits.MsPorVueltaDeIa);
        long ticks = GameLimits.TicksDeUnContadorDeCriatura(30);
        Assert.Equal(295, ticks);                                  // (30 + 1) x 380 / 40, redondeado para arriba
        Assert.InRange(ticks * GameLoop.TickMs / 1000.0, 11.0, 12.0);
    }

    /// <summary>LA VUELTA DE MAS ES DEL ORIGINAL: limpia la bandera en el disparo SIGUIENTE al que llega a cero.</summary>
    [Fact]
    public void Va_una_vuelta_de_mas_que_la_que_dice_el_numero()
    {
        // Una sola vuelta del TIMER_AI son 380 ms = 9,5 ticks de 40, y se redondea para arriba: con CERO vueltas escritas,
        // la criatura igual queda quieta una vuelta. (Escribir 380 / TickMs aca da 9: es division entera.)
        Assert.Equal(10, GameLimits.TicksDeUnContadorDeCriatura(0));
        Assert.True(GameLimits.TicksDeUnContadorDeCriatura(30) > 30 * GameLimits.MsPorVueltaDeIa / GameLoop.TickMs);
    }

    /// <summary>Y LAS OTRAS DOS DURACIONES DE LA FAMILIA, con la misma regla.</summary>
    [Fact]
    public void El_hechizo_sobre_criatura_y_el_del_familiar_usan_la_misma_escala()
    {
        Assert.Equal(361, GameLimits.TicksDeUnContadorDeCriatura(37));     // IntervaloParalizadoReducido, Admin.bas:58
        Assert.Equal(4760, GameLimits.TicksDeUnContadorDeCriatura(500));   // IntervaloParalizado sobre otra criatura
    }
}
