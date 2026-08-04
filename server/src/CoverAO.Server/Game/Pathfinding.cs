namespace CoverAO.Server.Game;

/// <summary>
/// A* pathfinding on the 4-connected tile grid (AO movement is cardinal). Used by hostile NPCs to chase
/// players around obstacles instead of walking straight into them (the earlier greedy step). Bounded by
/// a node budget so a blocked-off target can't stall the game tick.
/// </summary>
public static class Pathfinding
{
    private static readonly (int dx, int dy)[] Dirs = { (0, -1), (1, 0), (0, 1), (-1, 0) }; // N, E, S, W

    /// <summary>
    /// First cardinal step from (sx,sy) toward (gx,gy) along the shortest obstacle-free path, or null if
    /// already there or unreachable within <paramref name="maxNodes"/>. <paramref name="isBlocked"/>
    /// reports impassable tiles; the goal tile is always treated as reachable.
    /// </summary>
    public static (int x, int y)? NextStep(int sx, int sy, int gx, int gy, Func<int, int, bool> isBlocked, int maxNodes = 4000)
    {
        if (sx == gx && sy == gy) return null;

        var open = new PriorityQueue<(int x, int y), int>();
        var came = new Dictionary<(int, int), (int, int)>();
        var cost = new Dictionary<(int, int), int> { [(sx, sy)] = 0 };
        open.Enqueue((sx, sy), Heuristic(sx, sy, gx, gy));

        int expanded = 0;
        while (open.Count > 0 && expanded++ < maxNodes)
        {
            var cur = open.Dequeue();
            if (cur.x == gx && cur.y == gy) return FirstStep(came, (sx, sy), (gx, gy));

            foreach (var (dx, dy) in Dirs)
            {
                int nx = cur.x + dx, ny = cur.y + dy;
                if (nx is < 0 or > 255 || ny is < 0 or > 255) continue;
                bool isGoal = nx == gx && ny == gy;
                if (!isGoal && isBlocked(nx, ny)) continue;

                int next = cost[cur] + 1;
                if (!cost.TryGetValue((nx, ny), out var old) || next < old)
                {
                    cost[(nx, ny)] = next;
                    came[(nx, ny)] = cur;
                    open.Enqueue((nx, ny), next + Heuristic(nx, ny, gx, gy));
                }
            }
        }
        return null;
    }

    private static int Heuristic(int x, int y, int gx, int gy) => Math.Abs(x - gx) + Math.Abs(y - gy);

    /// <summary>Walk the came-from chain back from the goal to the tile right after the start.</summary>
    private static (int, int)? FirstStep(Dictionary<(int, int), (int, int)> came, (int, int) start, (int, int) goal)
    {
        var cur = goal;
        while (came.TryGetValue(cur, out var prev))
        {
            if (prev == start) return cur;
            cur = prev;
        }
        return null;
    }
}
