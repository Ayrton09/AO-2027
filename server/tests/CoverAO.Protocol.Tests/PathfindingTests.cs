using System.Collections.Generic;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>Tests the A* NPC pathfinder: straight-line on open ground, routing around a wall (where a
/// greedy step would butt into the obstacle), and reporting no path when the goal is enclosed.</summary>
public class PathfindingTests
{
    private static Func<int, int, bool> Blocked(params (int x, int y)[] walls)
    {
        var set = new HashSet<(int, int)>(walls);
        return (x, y) => set.Contains((x, y));
    }

    [Fact]
    public void StepsStraightOnOpenGround()
    {
        var step = Pathfinding.NextStep(0, 0, 3, 0, (_, _) => false);
        Assert.Equal((1, 0), step);
    }

    [Fact]
    public void ReturnsNullWhenAlreadyAtGoal()
        => Assert.Null(Pathfinding.NextStep(5, 5, 5, 5, (_, _) => false));

    [Fact]
    public void RoutesAroundAWall()
    {
        // Vertical wall at x=1 for y=0,1,2 with a gap at y=3. From (0,0) to (2,0) the direct east tile
        // (1,0) is blocked, so the shortest path goes down to the gap first — the first step is south.
        var blocked = Blocked((1, 0), (1, 1), (1, 2));
        var step = Pathfinding.NextStep(0, 0, 2, 0, blocked);
        Assert.Equal((0, 1), step);
    }

    [Fact]
    public void ReturnsNullWhenGoalIsEnclosed()
    {
        // Goal (2,2) walled off on all four sides → unreachable.
        var blocked = Blocked((1, 2), (3, 2), (2, 1), (2, 3));
        Assert.Null(Pathfinding.NextStep(0, 0, 2, 2, blocked));
    }
}
