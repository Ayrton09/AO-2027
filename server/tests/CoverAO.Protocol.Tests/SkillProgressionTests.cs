using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Aprender haciendo (VB6 SubirSkill, Modulo_UsUaRiOs.bas:1185). Antes ninguna acción mejoraba una skill:
/// el único aumento era gastar puntos a mano, así que pegar/castear/fabricar durante horas dejaba las
/// habilidades intactas. Estos tests fijan las cuatro reglas del original: hambre/sed bloquean, tope 100,
/// tope por nivel (LevelSkill) y la tirada de probabilidad.
/// </summary>
public class SkillProgressionTests
{
    private static Player NewChar(byte level = 20, byte skill = 0)
    {
        var p = new Player { CharIndex = 1, Name = "Test", Account = "cuenta", Level = level, Hunger = 100, Thirst = 100 };
        p.Skills[GameLimits.SkillWrestling] = skill;
        return p;
    }

    [Fact]
    public void Using_a_skill_raises_it()
    {
        var p = NewChar(skill: 10);
        Assert.True(SkillProgression.TryRaise(p, GameLimits.SkillWrestling, new Random(1)));
        Assert.Equal(11, p.Skills[GameLimits.SkillWrestling]);
    }

    [Fact]
    public void Hunger_or_thirst_blocks_learning()
    {
        var hambriento = NewChar(skill: 10); hambriento.Hunger = 0;
        var sediento = NewChar(skill: 10); sediento.Thirst = 0;
        Assert.False(SkillProgression.TryRaise(hambriento, GameLimits.SkillWrestling, new Random(1)));
        Assert.False(SkillProgression.TryRaise(sediento, GameLimits.SkillWrestling, new Random(1)));
        Assert.Equal(10, hambriento.Skills[GameLimits.SkillWrestling]);
    }

    [Fact]
    public void Level_caps_how_far_a_skill_can_go()
    {
        // Tabla LevelSkill: nivel 1 -> 3, nivel 20 -> 50, nivel 40+ -> 100.
        Assert.Equal(3, SkillProgression.CapForLevel(1));
        Assert.Equal(50, SkillProgression.CapForLevel(20));
        Assert.Equal(100, SkillProgression.CapForLevel(40));
        Assert.Equal(100, SkillProgression.CapForLevel(50));

        var novato = NewChar(level: 1, skill: 3);   // ya está en el tope de su nivel
        Assert.False(SkillProgression.TryRaise(novato, GameLimits.SkillWrestling, new Random(1)));
        Assert.Equal(3, novato.Skills[GameLimits.SkillWrestling]);
    }

    [Fact]
    public void A_maxed_skill_does_not_grow()
    {
        var p = NewChar(level: 50, skill: 100);
        Assert.False(SkillProgression.TryRaise(p, GameLimits.SkillWrestling, new Random(1)));
        Assert.Equal(100, p.Skills[GameLimits.SkillWrestling]);
    }

    [Fact]
    public void Out_of_range_skills_are_ignored()
    {
        var p = NewChar();
        Assert.False(SkillProgression.TryRaise(p, 0, new Random(1)));
        Assert.False(SkillProgression.TryRaise(p, GameLimits.NumSkills + 1, new Random(1)));
    }

    [Fact]
    public void Repeated_use_climbs_to_the_level_cap_and_stops()
    {
        var p = NewChar(level: 5, skill: 0);   // tope del nivel 5 = 13
        var rng = new Random(7);
        for (int i = 0; i < 100; i++) SkillProgression.TryRaise(p, GameLimits.SkillWrestling, rng);
        Assert.Equal(13, p.Skills[GameLimits.SkillWrestling]);
    }
}
