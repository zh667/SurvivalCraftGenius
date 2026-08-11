namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Turning uneven ground into a building site — the step that turned every
/// survey tool from an inspector into a builder.
///
/// Playtest 12, after the companion reported it could not find anywhere flat
/// enough for even a 1x1 plot:
///
///   玩家:"没有合适的地形你可以自改造嘛"
///
/// Exactly right, and it is what any player does without thinking: a slope is
/// not a reason to give up, it is two minutes of digging and filling. The tools
/// only ever measured the ground and refused it.
///
/// Engine-free so the arithmetic is testable: callers hand in each column's
/// ground height, get back the cut/fill operations that flatten it.
/// </summary>
public static class GeniusGroundLevel
{
    /// <summary>One column of the footprint and where its ground currently is.</summary>
    public readonly record struct Column(int X, int Z, int GroundY);

    /// <summary>
    /// A single cell to change. <see cref="Fill"/> false means dig it out,
    /// true means put a block there.
    /// </summary>
    public readonly record struct LevelOp(int X, int Y, int Z, bool Fill);

    /// <summary>
    /// The height to flatten to: the lower median of the column heights.
    ///
    /// Median, not mean, and not min or max — it minimises the number of cells
    /// that have to move, and unlike the average it is always an existing ground
    /// height, so at least half the plot is already correct.
    /// </summary>
    public static int ChooseTargetY(IReadOnlyList<int> groundHeights)
    {
        ArgumentNullException.ThrowIfNull(groundHeights);
        if (groundHeights.Count == 0)
        {
            throw new ArgumentException("no columns", nameof(groundHeights));
        }

        var sorted = groundHeights.Order().ToList();
        return sorted[(sorted.Count - 1) / 2];
    }

    /// <summary>
    /// Cells to change so every column's ground sits at <paramref name="targetY"/>.
    ///
    /// Ordered cuts-first and top-down, then fills bottom-up: sand and gravel
    /// fall, so digging a column from the bottom would just pull the material
    /// above back into the hole (the same rule that shapes the descent
    /// staircase, see docs/MECHANICS-MINING.md §9).
    /// </summary>
    public static IEnumerable<LevelOp> Plan(IEnumerable<Column> columns, int targetY)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var all = columns.ToList();

        var cuts = new List<LevelOp>();
        foreach (var column in all.Where(c => c.GroundY > targetY))
        {
            for (var y = column.GroundY; y > targetY; y--)
            {
                cuts.Add(new LevelOp(column.X, y, column.Z, Fill: false));
            }
        }

        var fills = new List<LevelOp>();
        foreach (var column in all.Where(c => c.GroundY < targetY))
        {
            for (var y = column.GroundY + 1; y <= targetY; y++)
            {
                fills.Add(new LevelOp(column.X, y, column.Z, Fill: true));
            }
        }

        // Highest cut first; lowest fill first.
        return cuts.OrderByDescending(op => op.Y)
            .Concat(fills.OrderBy(op => op.Y));
    }

    /// <summary>How much work a plot needs, for reporting before committing.</summary>
    public static (int Cut, int Fill) Cost(IEnumerable<Column> columns, int targetY)
    {
        var ops = Plan(columns, targetY).ToList();
        return (ops.Count(op => !op.Fill), ops.Count(op => op.Fill));
    }

    /// <summary>Plain-language summary of the work, or null when the plot is already flat.</summary>
    public static string? Describe(IEnumerable<Column> columns, int targetY)
    {
        var (cut, fill) = Cost(columns, targetY);
        if (cut == 0 && fill == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (cut > 0)
        {
            parts.Add($"削平 {cut} 格");
        }

        if (fill > 0)
        {
            parts.Add($"垫高 {fill} 格");
        }

        return string.Join("、", parts);
    }
}
