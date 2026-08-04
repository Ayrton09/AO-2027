using System.Linq;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Ganancias al subir de nivel (VB6 CheckUserLevel, Modulo_UsUaRiOs.bas:397-560). Antes subir de nivel era
/// puramente cosmético: sólo avanzaba ELV/ELU y daba skill points, así que un personaje de nivel 50 seguía
/// con los 100 HP del nivel 1 — MODVIDA estaba cargada pero nadie la usaba. Estos tests fijan que la vida,
/// el maná, la stamina y el golpe suban de verdad, con los topes y las diferencias por clase del original.
/// El RNG se inyecta con semilla para que el resultado sea determinista.
/// </summary>
public class LevelGainsTests
{
    // Ids de clase = eClass (Declares.bas:117), el mismo espacio que indexa MODVIDA y NACIMIENTO.dat.
    // CUIDADO: NO es el orden del AO clásico — Clérigo es 1 y Mago es 2, no al revés.
    private const byte Clerigo = 1, Mago = 2, Guerrero = 3, Ladron = 5, Gladiador = 8,
        Pescador = 11, Mercenario = 17, Nigromante = 18;

    // MODVIDA real de Balance.dat: Guerrero 10, Mago 7.
    private static readonly Dictionary<int, double> LifeMod = new() { [Mago] = 7, [Guerrero] = 10 };
    private static readonly Dictionary<int, long> Curve =
        Enumerable.Range(1, 60).ToDictionary(i => i, _ => 100L);

    private static Player NewChar(byte klass)
    {
        var p = new Player
        {
            CharIndex = 1, Name = "Test", Account = "cuenta",
            Class = klass, Level = 1, MaxHp = 100, Hp = 100, MaxMana = 0, Mana = 0,
            MaxStamina = 100, Stamina = 100, MinHit = 2, MaxHit = 5,
        };
        for (int i = 1; i <= 5; i++) p.Attributes[i] = 18;
        p.Elu = 100;   // sin ELU inicial el bucle de Grant no sube ningún nivel
        return p;
    }

    private static Leveling Sut(int seed = 12345) => new(Curve, LifeMod, new Random(seed));

    [Fact]
    public void Leveling_up_raises_max_hp()
    {
        var p = NewChar(Guerrero);   // Guerrero
        Sut().Grant(p, 100);  // exactamente un nivel
        Assert.Equal(2, p.Level);
        Assert.True(p.MaxHp > 100, $"un Guerrero debe ganar vida al subir (MaxHp = {p.MaxHp})");
    }

    [Fact]
    public void A_level_50_warrior_is_far_tougher_than_a_rookie()
    {
        var p = NewChar(Guerrero);
        Sut().Grant(p, 100 * 60);   // sube hasta el tope
        Assert.Equal(Leveling.MaxLevel, p.Level);
        // ~10 HP por nivel * 49 niveles: muy por encima de los 100 iniciales (antes se quedaba en 100).
        Assert.True(p.MaxHp > 400, $"un nivel 50 no puede seguir con vida de novato (MaxHp = {p.MaxHp})");
        Assert.True(p.MaxHp <= 999, "no puede pasar el tope STAT_MAXHP");
    }

    [Fact]
    public void Mage_gains_mana_warrior_does_not()
    {
        var mago = NewChar(Mago);
        var guerrero = NewChar(Guerrero);
        Sut().Grant(mago, 100 * 10);
        Sut().Grant(guerrero, 100 * 10);
        Assert.True(mago.MaxMana > 0, "el Mago gana maná por nivel (2.8 x Inteligencia)");
        Assert.Equal(0, guerrero.MaxMana);   // el Guerrero no tiene maná
    }

    [Fact]
    public void Warrior_gains_more_hp_than_mage()
    {
        var mago = NewChar(Mago);          // MODVIDA 7
        var guerrero = NewChar(Guerrero);  // MODVIDA 10
        Sut().Grant(mago, 100 * 20);
        Sut().Grant(guerrero, 100 * 20);
        Assert.True(guerrero.MaxHp > mago.MaxHp,
            $"el Guerrero (MODVIDA 10) debe superar al Mago (7): {guerrero.MaxHp} vs {mago.MaxHp}");
    }

    /// <summary>
    /// REGRESIÓN: las tablas por clase se habían escrito con el orden del AO clásico (Mago=1) mientras que
    /// MODVIDA y NACIMIENTO.dat usan eClass (Clérigo=1, Mago=2), así que el Mago cobraba el maná del Clérigo
    /// y viceversa. Este test fija el espacio de ids correcto por el lado observable.
    /// </summary>
    [Fact]
    public void Mage_and_cleric_are_not_swapped()
    {
        var clerigo = NewChar(Clerigo);
        var mago = NewChar(Mago);
        Sut().Grant(clerigo, 100 * 10);
        Sut().Grant(mago, 100 * 10);

        // Ambos lanzan, pero el Mago gana 2.8 x Int por nivel contra los 2.0 del Clérigo (CheckUserLevel).
        Assert.True(mago.MaxMana > clerigo.MaxMana,
            $"el Mago (2.8 x Int) debe ganar más maná que el Clérigo (2.0): {mago.MaxMana} vs {clerigo.MaxMana}");
        // Y el Mago es el único que gana MENOS stamina que el resto (AumentoSTMago = Def - 1).
        Assert.True(mago.MaxStamina < clerigo.MaxStamina,
            $"el Mago gana menos stamina que el Clérigo: {mago.MaxStamina} vs {clerigo.MaxStamina}");
    }

    /// <summary>
    /// REGRESIÓN: le había dado al Gladiador (clase 8) <c>AumentoSTDef + 23</c> deduciéndolo del nombre de la
    /// constante <c>AumentoStBandido</c>. Esa constante está declarada en Declares.bas:510 pero NO la usa
    /// ninguna rama del Select Case — el Gladiador cae en AumentoSTDef, igual que el Guerrero.
    /// </summary>
    [Fact]
    public void Gladiator_gets_default_stamina_not_the_dead_bandit_constant()
    {
        var gladiador = NewChar(Gladiador);
        var guerrero = NewChar(Guerrero);
        Sut().Grant(gladiador, 100 * 10);
        Sut().Grant(guerrero, 100 * 10);
        Assert.Equal(guerrero.MaxStamina, gladiador.MaxStamina);
    }

    [Theory]
    [InlineData(Ladron)]      // AumentoSTLadron  = Def + 3
    [InlineData(Pescador)]    // AumentoSTPescador = Def + 20
    public void Rogue_and_fisherman_out_stamina_the_warrior(byte klass)
    {
        var p = NewChar(klass);
        var guerrero = NewChar(Guerrero);
        Sut().Grant(p, 100 * 10);
        Sut().Grant(guerrero, 100 * 10);
        Assert.True(p.MaxStamina > guerrero.MaxStamina);
    }

    /// <summary>Mercenario (17) y Nigromante (18) no existían en la tabla: caían al Case Else.</summary>
    [Fact]
    public void Mercenary_and_necromancer_have_their_own_gains()
    {
        var merc = NewChar(Mercenario);
        var nigro = NewChar(Nigromante);
        var clerigo = NewChar(Clerigo);
        Sut().Grant(merc, 100 * 10);      // ELV<=30 → +3 de golpe por nivel, contra los +2 del Case Else
        Sut().Grant(nigro, 100 * 10);
        Sut().Grant(clerigo, 100 * 10);
        Assert.True(merc.MaxHit > clerigo.MaxHit,
            $"el Mercenario gana 3 de golpe bajo nivel 30, no los 2 del default: {merc.MaxHit} vs {clerigo.MaxHit}");
        Assert.True(nigro.MaxMana > 0, "el Nigromante gana maná (2.5 x Inteligencia)");
    }

    [Fact]
    public void Stamina_and_hit_grow_with_level()
    {
        var p = NewChar(Guerrero);
        int sta0 = p.MaxStamina, hit0 = p.MaxHit;
        Sut().Grant(p, 100 * 5);
        Assert.True(p.MaxStamina > sta0, "la stamina máxima sube por nivel");
        Assert.True(p.MaxHit > hit0, "el golpe máximo sube por nivel");
        Assert.True(p.MaxHit <= 99, "bajo nivel 36 el golpe no pasa STAT_MAXHIT_UNDER36");
    }

    [Fact]
    public void Level_up_leaves_the_character_at_full()
    {
        var p = NewChar(Guerrero);
        p.Hp = 1; p.Stamina = 1;
        Sut().Grant(p, 100);
        Assert.Equal(p.MaxHp, p.Hp);
        Assert.Equal(p.MaxStamina, p.Stamina);
    }

    [Fact]
    public void Without_lifemod_data_progression_still_works()
    {
        // Sin Balance.dat (entorno sin data) no debe romper: sube de nivel sin ganar vida.
        var p = NewChar(Guerrero);
        new Leveling(Curve).Grant(p, 100);
        Assert.Equal(2, p.Level);
        Assert.Equal(100, p.MaxHp);
    }
}
