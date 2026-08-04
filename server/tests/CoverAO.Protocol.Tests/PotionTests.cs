using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Golden test locking the ported blue-potion mana formula from InvUsuario.bas (Case 4):
///   MinMAN += Porcentaje(MaxMAN, 4) + ELV\2 + 40/ELV
/// with VB6 half-to-even rounding at Porcentaje (Long return) and the final Integer assignment.
/// Values computed by hand from the formula.
/// </summary>
public class PotionTests
{
    [Theory]
    // level, maxMana, expected restore
    [InlineData(1, 200, 48)]  // Porcentaje(200,4)=8 ; 8 + 0 + 40/1=40 => 48
    [InlineData(2, 200, 29)]  // 8 + 1 + 20 => 29
    [InlineData(10, 500, 29)] // Porcentaje(500,4)=20 ; 20 + 5 + 4 => 29
    [InlineData(3, 150, 20)]  // Porcentaje(150,4)=6 ; 6 + 1 + 40/3=13.33 => 20.33 => round=20
    [InlineData(4, 210, 20)]  // Porcentaje(210,4)=round(8.4)=8 ; 8 + 2 + 10 => 20
    public void ManaRestoreMatchesFormula(int level, int maxMana, int expected)
        => Assert.Equal(expected, Potions.ManaRestore(maxMana, level));

    [Fact]
    public void PorcentajeRoundsHalfToEven()
    {
        // 50*4/100 = 2.0 (exact) ; 25*4/100 = 1.0 ; 37*4/100 = 1.48 -> 1
        Assert.Equal(2, Potions.Porcentaje(50, 4));
        Assert.Equal(1, Potions.Porcentaje(37, 4));
        // exact-half: 10*5/100 = 0.5 -> even -> 0 ; 30*5/100 = 1.5 -> even -> 2
        Assert.Equal(0, Potions.Porcentaje(10, 5));
        Assert.Equal(2, Potions.Porcentaje(30, 5));
    }

    [Fact]
    public void ManaRestoreClampsLevelToAtLeastOne()
        => Assert.Equal(Potions.ManaRestore(200, 1), Potions.ManaRestore(200, 0)); // level 0 treated as 1
}
