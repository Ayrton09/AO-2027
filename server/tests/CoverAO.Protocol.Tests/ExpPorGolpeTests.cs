using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// La experiencia se GOTEA por golpe, no se paga entera al que remata (VB6 <c>CalcularDarExp</c>,
/// <c>SistemaCombate.bas:1992</c>). El port la daba toda al matador.
///
/// El total se conserva —quien hace todo el daño termina igual que antes— así que lo que cambia no es cuánta
/// experiencia hay, sino <b>quién se la lleva</b>: el que pelea y no remata deja de irse con las manos vacías.
/// </summary>
public class ExpPorGolpeTests
{
    [Fact]
    public void La_porcion_es_proporcional_al_dano()
    {
        // Media vida = media experiencia.
        Assert.Equal(50, Combat.ExpPorGolpe(damage: 50, npcHpActual: 100, npcMaxHp: 100, npcGiveExp: 100));
        Assert.Equal(25, Combat.ExpPorGolpe(25, 100, 100, 100));
        // Y escala con GiveEXP, no con el HP.
        Assert.Equal(500, Combat.ExpPorGolpe(50, 100, 100, 1000));
    }

    [Fact]
    public void El_golpe_que_mata_no_cobra_por_el_exceso()
    {
        // Le quedan 10 de vida y le pego por 999: sólo se paga por los 10 que sacó. Sin este recorte, rematar
        // con un golpe grande pagaría de más y el total dejaría de conservarse.
        Assert.Equal(10, Combat.ExpPorGolpe(999, 10, 100, 100));
    }

    [Fact]
    public void Un_dano_negativo_no_paga()
    {
        Assert.Equal(0, Combat.ExpPorGolpe(-50, 100, 100, 100));
        Assert.Equal(0, Combat.ExpPorGolpe(0, 100, 100, 100));
    }

    [Fact]
    public void Un_rasguno_a_un_bicho_enorme_no_paga_nada()
    {
        // 1 de daño sobre 10.000 de vida con 100 de exp: la proporción es 0,01 y redondea a 0. El original
        // sale con Exit Sub, así que ese golpe no otorga nada — y el pool NO se toca.
        Assert.Equal(0, Combat.ExpPorGolpe(1, 10_000, 10_000, 100));
    }

    [Fact]
    public void Sin_experiencia_o_sin_vida_maxima_no_hay_nada_que_repartir()
    {
        Assert.Equal(0, Combat.ExpPorGolpe(50, 100, 100, 0));    // criatura que no da exp
        Assert.Equal(0, Combat.ExpPorGolpe(50, 100, 0, 100));    // MaxHP 0: el original hace Exit Sub
    }

    [Fact]
    public void Redondea_half_to_even_como_el_resto_del_combate()
    {
        // 5 de daño sobre 20 de vida con 10 de exp = 2,5 exacto. Half-to-even baja al par: 2.
        Assert.Equal(2, Combat.ExpPorGolpe(5, 20, 20, 10));
        // 15 sobre 20 con 10 = 7,5 → sube al par: 8.
        Assert.Equal(8, Combat.ExpPorGolpe(15, 20, 20, 10));
    }

    [Fact]
    public void Matarlo_de_un_golpe_paga_toda_su_experiencia()
    {
        // El caso que hace que el total se conserve: un solo golpe que saca toda la vida paga el 100%.
        Assert.Equal(500, Combat.ExpPorGolpe(100, 100, 100, 500));
    }

    [Fact]
    public void La_suma_de_los_golpes_no_supera_el_total()
    {
        // Propiedad que sostiene todo el diseño: pegando de a poco no se saca más experiencia que de un saque.
        // (El pool del NPC es el que lo garantiza en runtime; acá se comprueba que la fórmula no lo viole.)
        const int maxHp = 100, giveExp = 100;
        long acumulado = 0;
        int hp = maxHp;
        while (hp > 0)
        {
            long e = Combat.ExpPorGolpe(7, hp, maxHp, giveExp);
            acumulado += e;
            hp -= 7;
            if (hp < 0) hp = 0;
        }
        Assert.True(acumulado <= giveExp, $"la suma de los goteos dio {acumulado}, más que los {giveExp} del bicho");
    }
}
