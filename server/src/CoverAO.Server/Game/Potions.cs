namespace CoverAO.Server.Game;

/// <summary>
/// Potion effect formulas ported from InvUsuario.bas (the UseInvItem Select Case .flags.TipoPocion)
/// and Matematicas.bas Porcentaje. SubTipo routes the effect: 1=agility, 2=strength, 3=HP, 4=mana,
/// 5=cast spell. HP/agility/strength restore RandomNumber(MinModificador, MaxModificador); mana uses
/// the fixed formula below. Fase 2 methodology: port the exact arithmetic (VB6 half-to-even rounding).
/// </summary>
public static class Potions
{
    /// <summary>Porcentaje(Total, Porc) = (Total * Porc) / 100, returned as a VB6 Long (half-to-even).</summary>
    public static long Porcentaje(long total, long porc) =>
        (long)System.Math.Round(total * porc / 100.0, System.MidpointRounding.ToEven);

    /// <summary>
    /// Mana restored by a blue potion (SubTipo 4): MinMAN += Porcentaje(MaxMAN,4) + ELV\2 + 40/ELV.
    /// Two VB6 rounding points: Porcentaje (Long return) and the final Integer assignment to MinMAN.
    /// </summary>
    public static int ManaRestore(int maxMana, int level)
    {
        int lvl = System.Math.Max(1, level);
        long porcentaje = Porcentaje(maxMana, 4);
        double raw = porcentaje + (lvl / 2) + (40.0 / lvl); // ELV\2 is integer division; 40/ELV is float
        return (int)System.Math.Round(raw, System.MidpointRounding.ToEven);
    }
}
