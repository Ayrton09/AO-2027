namespace CoverAO.Server.Game;

/// <summary>
/// Subida de habilidades POR USO (VB6 <c>SubirSkill</c>, Modulo_UsUaRiOs.bas:1185). Antes ninguna acción
/// mejoraba una skill: el único aumento posible era gastar puntos a mano con /SKILLS, así que pegar,
/// castear, fabricar o talar durante horas dejaba las habilidades intactas.
///
/// Reglas del original, en orden: no se sube con hambre o sed; tope absoluto de 100 puntos; tope por
/// nivel (<c>LevelSkill</c>, hardcodeada en General.bas:1669 — un nivel 1 no pasa de 3, un nivel 20 de 50,
/// y recién a nivel 40 se destraba el 100); y una tirada contra <c>PorcentajeSkills</c> del Server.ini.
/// Al subir, el VB6 además regala 10 de experiencia (que puede disparar un nivel) y avisa con LocaleMsg 454.
/// </summary>
public static class SkillProgression
{
    public const int MaxSkillPoints = 100;   // MAXSKILLPOINTS (Declares.bas:423)
    public const int ExpPerSkillUp = 10;     // VB6: .Exp = .Exp + 10 al subir una skill
    /// <summary>PorcentajeSkills del Server.ini de esta instalación (100 = cada uso cuenta).</summary>
    public const int RaiseChancePercent = 100;

    /// <summary>Tope de puntos de skill según el nivel del personaje (VB6 LevelSkill, General.bas:1669).</summary>
    private static readonly int[] LevelCaps =
    {
        0,  3,  5,  7, 10, 13, 15, 17, 20, 23, 25,   // niveles 0..10
        27, 30, 33, 35, 37, 40, 43, 45, 47, 50,      // 11..20
        53, 55, 57, 60, 63, 65, 67, 70, 73, 75,      // 21..30
        77, 80, 83, 85, 87, 90, 93, 95, 97, 100,     // 31..40
    };

    /// <summary>Tope de skill para ese nivel (a partir de 40 el tope es 100).</summary>
    public static int CapForLevel(int level) =>
        level <= 0 ? 0 : level < LevelCaps.Length ? LevelCaps[level] : MaxSkillPoints;

    /// <summary>
    /// Intenta subir un punto de <paramref name="skill"/> por haberla usado. Devuelve true sólo si subió
    /// (el llamador avisa al jugador y le da la experiencia). No sube con hambre/sed, ni pasada del tope
    /// absoluto o del tope del nivel, y respeta la tirada de probabilidad del original.
    /// </summary>
    public static bool TryRaise(Player p, int skill, Random rng)
    {
        if (skill < 1 || skill > GameLimits.NumSkills) return false;
        if (p.Hunger <= 0 || p.Thirst <= 0) return false;              // VB6: flags.Hambre = 0 And flags.Sed = 0
        if (p.Skills[skill] >= MaxSkillPoints) return false;
        if (p.Skills[skill] >= CapForLevel(p.Level)) return false;     // tope por nivel
        if (rng.Next(1, 101) > RaiseChancePercent) return false;
        p.Skills[skill]++;
        return true;
    }
}
