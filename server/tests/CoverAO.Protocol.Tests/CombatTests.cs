using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Golden test locking the ported melee damage formula from CalcularDano (SistemaCombate.bas:420):
///   (3*weaponDamage + (weaponMax/5)*max(0, strength-15) + userHit) * classModifier
/// assigned to a VB6 Long (round half-to-even). Values computed by hand from the formula.
/// </summary>
public class CombatTests
{
    [Fact]
    public void WrestlingExample()
    {
        // (3*2 + (3/5)*max(0,18-15) + 3) * 0.4 = (6 + 1.8 + 3) * 0.4 = 10.8*0.4 = 4.32 -> 4
        Assert.Equal(4, Combat.CalcularDano(weaponDamage: 2, weaponMaxDamage: 3, userHit: 3, strength: 18, classModifier: 0.4));
    }

    [Fact]
    public void WeaponExampleWithStrengthBonus()
    {
        // (3*10 + (20/5)*15 + 5) * 1.1 = 95*1.1. Note 1.1 is not exactly representable, so the product
        // is 104.5000...1 (just above the half) and rounds to 105 — the same result VB6's float yields.
        Assert.Equal(105, Combat.CalcularDano(weaponDamage: 10, weaponMaxDamage: 20, userHit: 5, strength: 30, classModifier: 1.1));
    }

    [Fact]
    public void StrengthAtOrBelow15GivesNoBonus()
    {
        // (3*5 + (10/5)*max(0,0) + 2) * 1.0 = 15 + 0 + 2 = 17
        Assert.Equal(17, Combat.CalcularDano(weaponDamage: 5, weaponMaxDamage: 10, userHit: 2, strength: 15, classModifier: 1.0));
        // strength below 15 also clamps to 0
        Assert.Equal(17, Combat.CalcularDano(weaponDamage: 5, weaponMaxDamage: 10, userHit: 2, strength: 10, classModifier: 1.0));
    }

    [Fact]
    public void BankersRoundingHalfToEven()
    {
        // 5*0.5 = 2.5 -> 2 (nearest even);  7*0.5 = 3.5 -> 4 (nearest even)
        Assert.Equal(2, Combat.CalcularDano(weaponDamage: 0, weaponMaxDamage: 0, userHit: 5, strength: 0, classModifier: 0.5));
        Assert.Equal(4, Combat.CalcularDano(weaponDamage: 0, weaponMaxDamage: 0, userHit: 7, strength: 0, classModifier: 0.5));
    }

    [Fact]
    public void EvasionPowerFromSkillsAgilityLevel()
    {
        // level 1, no skills -> 0
        Assert.Equal(0, Combat.PoderEvasion(tacticsSkill: 0, agility: 18, level: 1, evasionMod: 1.0));
        // (100 + 100/33*20)*1.0 = 160.606 -> 161 ; + 2.5*max(20-12,0)=20 -> 181
        Assert.Equal(181, Combat.PoderEvasion(tacticsSkill: 100, agility: 20, level: 20, evasionMod: 1.0));
    }

    /// <summary>EL ATAQUE USA LA MISMA CURVA QUE LA EVASIÓN. Es una <b>desviación deliberada</b> del
    /// original, que para el ataque usa cuatro escalones (skill&lt;31, &lt;61, &lt;91, resto) y para la evasión
    /// la curva continua <c>skill + skill/33 * agilidad</c>.
    ///
    /// <para>Que sean la misma cuenta no es una opinión: los escalones suman 0, 1, 2 o 3 agilidades y el
    /// factor de la curva vale exactamente 1 en 33, 2 en 66 y 3 en 99. Este test lo fija en los tres
    /// cruces —donde las dos ramas dan el MISMO número— y en el hueco más grande, que es el primer
    /// escalón: hasta skill 30 el original ignora la agilidad por completo.</para></summary>
    [Fact]
    public void ElAtaqueUsaLaMismaCurvaQueLaEvasion()
    {
        // EL ESPEJO: con los mismos argumentos, las dos fórmulas son la misma función.
        foreach (int skill in new[] { 0, 1, 5, 30, 31, 33, 50, 66, 90, 99, 100 })
            Assert.Equal(Combat.PoderEvasion(skill, agility: 18, level: 15, evasionMod: 0.9),
                         Combat.PoderDeAtaque(skill, agility: 18, level: 15, attackMod: 0.9));

        // Los tres cruces, donde el original coincide con la curva: la desviación no toca el late game.
        Assert.Equal(51, Combat.PoderDeAtaque(skill: 33, agility: 18, level: 1, attackMod: 1.0));   // 33 + 1*18
        Assert.Equal(102, Combat.PoderDeAtaque(skill: 66, agility: 18, level: 1, attackMod: 1.0));  // 66 + 2*18
        Assert.Equal(153, Combat.PoderDeAtaque(skill: 99, agility: 18, level: 1, attackMod: 1.0));  // 99 + 3*18

        // EL HUECO, que es de lo que se trata el cambio. Con la habilidad todavía verde el original daba
        // el skill pelado; la curva le reconoce la agilidad desde el primer punto.
        Assert.Equal(0, Combat.PoderDeAtaque(skill: 0, agility: 18, level: 1, attackMod: 1.0));     // el original: 0
        Assert.Equal(8, Combat.PoderDeAtaque(skill: 5, agility: 18, level: 1, attackMod: 1.0));     // el original: 5
        Assert.Equal(46, Combat.PoderDeAtaque(skill: 30, agility: 18, level: 1, attackMod: 1.0));   // el original: 30

        // Y NO ES UN AUMENTO PAREJO: justo pasado un escalón el original iba ADELANTE, y ahí la curva baja.
        Assert.Equal(48, Combat.PoderDeAtaque(skill: 31, agility: 18, level: 1, attackMod: 1.0));   // el original: 49

        // El resto del original queda intacto: dos redondeos al par y el +2.5*max(nivel-12,0).
        // (50 + 50/33*18)*1.0 = 77,27 -> 77 ; + 2.5*3 = 84,5 -> 84 (al par, no 85)
        Assert.Equal(84, Combat.PoderDeAtaque(skill: 50, agility: 18, level: 15, attackMod: 1.0));
    }

    [Fact]
    public void HitChanceClampsBetween10And90()
    {
        Assert.Equal(50, Combat.HitChance(0, 0));       // equal power -> 50%
        Assert.Equal(90, Combat.HitChance(100, 0));     // 50 + 40 = 90 (clamped ceiling)
        Assert.Equal(10, Combat.HitChance(0, 200));     // 50 - 80 -> clamped floor
        Assert.Equal(70, Combat.HitChance(60, 10));     // 50 + 50*0.4 = 70
    }

    // rollDef siempre devuelve el mínimo del rango (las armaduras de prueba tienen Min==Max).
    private static readonly System.Func<int, int, int> RollMin = (lo, _) => lo;

    [Fact]
    public void DefensaSinEquipoNoAbsorbe()
    {
        // Sin casco/armadura/escudo, el daño pasa intacto (cualquier zona).
        Assert.Equal(20, Combat.AplicarDefensa(20, zona: 1, casco: null, armadura: null, escudo: null, RollMin));
        Assert.Equal(20, Combat.AplicarDefensa(20, zona: 3, casco: null, armadura: null, escudo: null, RollMin));
    }

    [Fact]
    public void CuerpoAbsorbeConArmadura()
    {
        // zona 3 (torso) con armadura 12: 20 - 12 = 8.
        Assert.Equal(8, Combat.AplicarDefensa(20, zona: 3, casco: null, armadura: (12, 12), escudo: null, RollMin));
    }

    [Fact]
    public void CuerpoSumaEscudoAlaArmadura()
    {
        // zona 6 con armadura 12 + escudo 6: absorbe RandomNumber(12+6,12+6)=18 -> 20-18 = 2.
        Assert.Equal(2, Combat.AplicarDefensa(20, zona: 6, casco: null, armadura: (12, 12), escudo: (6, 6), RollMin));
    }

    [Fact]
    public void CabezaAbsorbeSoloConCasco_NoConArmadura()
    {
        // zona 1 (cabeza) con casco 20: 20-20 = 0, y "0 exacto se queda en 0" (VB6 usa "< 0").
        Assert.Equal(0, Combat.AplicarDefensa(20, zona: 1, casco: (20, 20), armadura: null, escudo: null, RollMin));
        // zona 1 pero SOLO armadura equipada: la cabeza no se protege con armadura -> daño intacto.
        Assert.Equal(20, Combat.AplicarDefensa(20, zona: 1, casco: null, armadura: (50, 50), escudo: null, RollMin));
    }

    [Fact]
    public void SobreabsorcionSeClampeaAUno()
    {
        // 5 - 10 = -5 < 0 -> se clampea a 1 (nunca cura).
        Assert.Equal(1, Combat.AplicarDefensa(5, zona: 4, casco: null, armadura: (10, 10), escudo: null, RollMin));
    }
}
