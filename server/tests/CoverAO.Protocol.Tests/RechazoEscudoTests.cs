using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El RECHAZO con escudo: la segunda tirada que el original hace sobre un golpe ya fallado
/// (<c>SistemaCombate.bas:1138</c> en PvP, <c>:261</c> contra NPC).
///
/// Importa por dos motivos que no se ven en la fórmula:
///   · un escudo equipado en PvP no hacía absolutamente nada — <c>PoderEvasionEscudo</c> tenía un único call
///     site en todo el server, la evasión contra NPC;
///   · bloquear es la ÚNICA vía por la que la skill <b>Defensa</b> sube por uso. Sin esto no tenía un solo
///     <c>RaiseSkill</c> en ninguna parte, así que los puntos puestos ahí quedaban congelados para siempre.
/// </summary>
public class RechazoEscudoTests
{
    [Fact]
    public void Sin_skills_no_hay_rechazo_en_vez_de_dividir_por_cero()
    {
        // El original se olvidó este guard en la rama PvP y ahí divide por cero; el ErrHandler se traga la
        // excepción y no hay rechazo. Devolver 0 produce la MISMA consecuencia observable, sin la excepción.
        Assert.Equal(0, Combat.ProbabilidadRechazoEscudo(0, 0));
    }

    [Fact]
    public void El_piso_es_10_y_el_techo_90()
    {
        // Nada de Defensa contra mucha Táctica: el original igual deja un 10% de rechazo.
        Assert.Equal(10, Combat.ProbabilidadRechazoEscudo(1, 1000));
        // Y al revés, nunca pasa de 90 aunque la proporción dé 100.
        Assert.Equal(90, Combat.ProbabilidadRechazoEscudo(100, 0));
        Assert.Equal(90, Combat.ProbabilidadRechazoEscudo(1000, 1));
    }

    [Fact]
    public void Redondea_como_el_VB6_y_no_trunca()
    {
        // "Chances are rounded" dice la fuente: el `/` del VB6 es de punto flotante y la conversión a entero
        // usa half-to-even. Con división entera de C# los dos casos darían UNO MENOS.
        Assert.Equal(67, Combat.ProbabilidadRechazoEscudo(2, 1));   // 66,67 → 67 (truncar daría 66)
        Assert.Equal(38, Combat.ProbabilidadRechazoEscudo(3, 5));   // 37,50 → 38, half-to-even (truncar daría 37)
    }

    [Fact]
    public void Un_medio_exacto_par_baja_por_half_to_even()
    {
        // 12,5 con 12 par: el redondeo bancario NO sube. Es el caso donde half-to-even y "round half up"
        // difieren, y el que justifica usar el mismo Clng que el resto del combate.
        Assert.Equal(12, Combat.ProbabilidadRechazoEscudo(1, 7));
    }

    [Fact]
    public void Mitad_y_mitad_es_cincuenta()
    {
        Assert.Equal(50, Combat.ProbabilidadRechazoEscudo(50, 50));
        Assert.Equal(50, Combat.ProbabilidadRechazoEscudo(1, 1));
    }

    [Fact]
    public void Mas_Defensa_nunca_baja_la_chance()
    {
        // Monotonía: subir Defensa con Tácticas fija no puede empeorar el rechazo. Es la propiedad que hace
        // que gastar puntos en Defensa tenga sentido, y la que se rompería si alguien invierte la división.
        int previo = 0;
        for (int d = 0; d <= 100; d++)
        {
            int actual = Combat.ProbabilidadRechazoEscudo(d, 50);
            Assert.True(actual >= previo, $"Defensa {d} con Tácticas 50 dio {actual}, menos que el {previo} anterior");
            previo = actual;
        }
    }
}
