using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Picks a place to build or farm, instead of leaving the model to guess a
/// coordinate and discover the ground is missing one place_block at a time.
///
/// Playtest 10, the companion's own post-mortem after producing a house the
/// player would not call a house:
///
///   "1. 选址地形很差: 小屋周围高低断层、悬空和危险地块多,很多位置没有支撑…
///    3. 农田沿用了主人屋边的旧坐标,没有围绕我这间新屋重新选址,这是我的规划错误…
///    核心问题是我只补局部、没先整体勘察和重规划。"
///
/// It is right that this is a planning failure, but planning was impossible:
/// nothing could answer "is this patch flat, supported and lit?" short of
/// place_block failing. That is what this answers, for a whole footprint at
/// once, before a single block is spent.
/// </summary>
public static class GeniusSiteSurvey
{
    /// <summary>Biggest step between columns still considered one flat plot.</summary>
    /// <summary>Spread that needs no work at all.</summary>
    private const int MaxLevelSpread = 1;

    /// <summary>
    /// Roughest ground we will still take on. Anything up to this gets levelled
    /// before building instead of rejected — a slope is not a reason to give up,
    /// it is some digging and filling. Past 4 it stops being a site and starts
    /// being a cliff, and the earthworks would cost more than moving.
    /// </summary>
    public const int MaxLevellableSpread = 4;

    public readonly record struct Site(
        Point3 Origin, int Score, int GroundY, int Spread, int Light, string Note);

    /// <summary>
    /// Scores one candidate footprint. Null when it is unusable at all: a hole,
    /// a cliff edge, lava, or (for farmland) the wrong ground entirely.
    /// </summary>
    public static Site? Evaluate(
        ComponentGeniusBrain brain, int x, int z, int width, int length, bool forFarm)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var heights = new List<int>(width * length);
        var soilCapable = 0;
        var lightSum = 0;

        for (var dx = 0; dx < width; dx++)
        {
            for (var dz = 0; dz < length; dz++)
            {
                var column = GroundHeight(brain, x + dx, z + dz);
                if (column is not { } groundY)
                {
                    return null;
                }

                var groundValue = terrain.GetCellValue(x + dx, groundY, z + dz);
                var contents = Terrain.ExtractContents(groundValue);
                if (BlocksManager.Blocks[contents] is MagmaBlock or WaterBlock)
                {
                    return null;
                }

                // A player's build is not a construction site.
                if (GeniusProtectedBlocks.IsPlayerBuilt(contents))
                {
                    return null;
                }

                if (contents == GeniusFarming.GrassContents
                    || contents == GeniusFarming.DirtContents
                    || contents == GeniusFarming.SoilContents)
                {
                    soilCapable++;
                }

                heights.Add(groundY);
                lightSum += Terrain.ExtractLight(
                    terrain.GetCellValue(x + dx, groundY + 1, z + dz));
            }
        }

        var cells = heights.Count;
        var spread = heights.Max() - heights.Min();
        var light = lightSum / cells;
        if (spread > MaxLevellableSpread)
        {
            return null;
        }

        // Farmland needs diggable soil AND light 9, or nothing will ever grow
        // there — the exact trap the last plot fell into ("那片田附近多是鹅卵石
        // 和空洞,光照还低于生长要求").
        if (forFarm && (soilCapable < cells || light < 9))
        {
            return null;
        }

        var score = 100 - (spread * 25) + (light >= 9 ? 10 : -30)
            + (forFarm ? soilCapable * 100 / cells : 0)
            + (WaterWithin(brain, x, GroundOf(heights), z, width, length) ? (forFarm ? 30 : 0) : 0);

        // Say what the earthworks cost, not just that the ground is uneven —
        // "高差2格" reads like a defect, "削平3格、垫高2格" reads like a job.
        var columns = heights
            .Select((h, i) => new GeniusGroundLevel.Column(x + (i / length), z + (i % length), h))
            .ToList();
        var targetY = GeniusGroundLevel.ChooseTargetY(heights);
        var work = GeniusGroundLevel.Describe(columns, targetY);
        var note = work is null ? "完全平坦" : $"高差{spread}格,我先整地({work})";
        if (forFarm)
        {
            note += WaterWithin(brain, x, GroundOf(heights), z, width, length)
                ? "、3格内有水(自动湿润)"
                : "、附近没水(要挖水渠,离田至少2格)";
        }

        return new Site(new Point3(x, targetY, z), score, targetY, spread, light, note);
    }

    /// <summary>
    /// Best footprint within <paramref name="radius"/> of the companion, nearest
    /// first among equals so it does not wander off to a marginally flatter
    /// patch 40 blocks away.
    /// </summary>
    public static Site? FindBest(
        ComponentGeniusBrain brain, int width, int length, int radius, bool forFarm)
    {
        var center = Terrain.ToCell(brain.Creature.ComponentBody.Position);
        Site? best = null;
        var bestKey = (Score: int.MinValue, Distance: int.MaxValue);

        for (var ring = 0; ring <= radius; ring++)
        {
            foreach (var (x, z) in GeniusScanGeometry.RingColumns(center.X, center.Z, ring))
            {
                if (Evaluate(brain, x, z, width, length, forFarm) is not { } site)
                {
                    continue;
                }

                var key = (site.Score, ring);
                if (key.Score > bestKey.Score
                    || (key.Score == bestKey.Score && ring < bestKey.Distance))
                {
                    best = site;
                    bestKey = (key.Score, ring);
                }
            }

            // Good enough and close by beats a long walk to something marginal.
            if (best is { Score: >= 130 } && ring >= 4)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Y of the topmost solid, standable ground in this column, or null when
    /// there is none within reach of the surface (a hole, or open air).
    /// </summary>
    public static int? GroundHeight(ComponentGeniusBrain brain, int x, int z)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var top = terrain.GetTopHeight(x, z);
        for (var y = Math.Min(top + 1, 253); y > 1; y--)
        {
            var contents = Terrain.ExtractContents(terrain.GetCellValue(x, y, z));
            if (contents == 0 || !BlocksManager.Blocks[contents].IsCollidable)
            {
                continue;
            }

            // Needs two cells of headroom to be worth standing or building on.
            var head1 = Terrain.ExtractContents(terrain.GetCellValue(x, y + 1, z));
            var head2 = Terrain.ExtractContents(terrain.GetCellValue(x, y + 2, z));
            return BlocksManager.Blocks[head1].IsCollidable
                || BlocksManager.Blocks[head2].IsCollidable
                ? null
                : y;
        }

        return null;
    }

    private static bool WaterWithin(
        ComponentGeniusBrain brain, int x, int y, int z, int width, int length)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        for (var dx = -3; dx < width + 3; dx++)
        {
            for (var dz = -3; dz < length + 3; dz++)
            {
                for (var dy = -2; dy <= 1; dy++)
                {
                    if (Terrain.ExtractContents(terrain.GetCellValue(x + dx, y + dy, z + dz))
                        == GeniusFarming.WaterContents)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static int GroundOf(List<int> heights) => heights.Max();
}
