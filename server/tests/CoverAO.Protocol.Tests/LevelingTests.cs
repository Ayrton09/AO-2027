using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Golden test locking the ported level-progression from CheckUserLevel (Modulo_UsUaRiOs.bas:357).
/// Uses the real Niveles.dat curve values (Nivel1=300, Nivel2=450, Nivel3=675, Nivel4=1012).
/// These assertions encode the source's exact ELU-update ordering (ELU set from levelELU(ELV) BEFORE
/// ELV is incremented), so any change to that logic is caught immediately.
/// </summary>
public class LevelingTests
{
    private static Leveling Curve() => new(new Dictionary<int, long>
    {
        [1] = 300, [2] = 450, [3] = 675, [4] = 1012, [5] = 1518,
    });

    private static Player FreshLevel1(Leveling lv)
    {
        var p = new Player { CharIndex = 1, Name = "T", Account = "a" };
        lv.InitNewCharacter(p);
        return p;
    }

    [Fact]
    public void NewCharacterStartsAtLevel1WithFirstCurveElu()
    {
        var p = FreshLevel1(Curve());
        Assert.Equal(1, p.Level);
        Assert.Equal(0, p.Exp);
        Assert.Equal(300, p.Elu);
    }

    [Fact]
    public void SingleLevelUpConsumesEluAndAdvances()
    {
        var lv = Curve();
        var p = FreshLevel1(lv);

        Assert.Equal(1, lv.Grant(p, 300));   // exactly enough for one level
        Assert.Equal(2, p.Level);
        Assert.Equal(0, p.Exp);
        Assert.Equal(300, p.Elu);            // source quirk: ELU = levelELU(1) after L1->L2
        Assert.Equal(5, p.FreeSkillPoints);
    }

    [Fact]
    public void SequentialGrantsFollowTheSourceOrdering()
    {
        var lv = Curve();
        var p = FreshLevel1(lv);
        lv.Grant(p, 300);   // -> L2, ELU=300
        lv.Grant(p, 300);   // -> L3, ELU=levelELU(2)=450
        Assert.Equal(3, p.Level);
        Assert.Equal(450, p.Elu);
        lv.Grant(p, 450);   // -> L4, ELU=levelELU(3)=675
        Assert.Equal(4, p.Level);
        Assert.Equal(675, p.Elu);
    }

    [Fact]
    public void MultiLevelInASingleGrant()
    {
        var lv = Curve();
        var p = FreshLevel1(lv);
        // 300 (L1->L2) + 300 (L2->L3, ELU was 300) + 450 (L3->L4, ELU was 450) = 1050
        int gained = lv.Grant(p, 1050);
        Assert.Equal(3, gained);
        Assert.Equal(4, p.Level);
        Assert.Equal(0, p.Exp);
        Assert.Equal(675, p.Elu);            // levelELU(3)
        Assert.Equal(15, p.FreeSkillPoints); // 3 levels * 5
    }

    [Fact]
    public void LeftoverExperienceIsKept()
    {
        var lv = Curve();
        var p = FreshLevel1(lv);
        lv.Grant(p, 500);                    // 300 to level, 200 left over
        Assert.Equal(2, p.Level);
        Assert.Equal(200, p.Exp);
    }

    [Fact]
    public void UninitializedEluDoesNotInfiniteLoop()
    {
        var lv = Curve();
        var p = new Player { CharIndex = 1, Name = "T", Account = "a" }; // Elu = 0 (not initialized)
        Assert.Equal(0, lv.Grant(p, 1_000_000)); // guard: Elu>0 required to level
        Assert.Equal(1, p.Level);
    }
}
