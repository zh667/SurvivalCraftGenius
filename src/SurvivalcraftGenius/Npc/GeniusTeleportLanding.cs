using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Decides where a teleport actually puts the companion.
///
/// The old rule was "keep the requested spot only if it is already an air
/// pocket with ground within 3 blocks, otherwise snap to the surface". That
/// made every underground destination useless: the ore bands sit at y2-40 in
/// solid rock, so <c>teleport(x, 38, z)</c> always came back as y=76. The
/// companion worked this out for itself in playtest 8 — "传送地下坐标会落到地表,
/// 不能直达矿层" — and it was the single biggest reason mining kept failing.
///
/// So: honour the requested Y. Land in an existing pocket when there is one,
/// otherwise carve a body-sized one and stand in it. The guard that remains is
/// about survival, not about second-guessing the destination:
///  - never materialise inside or beside lava,
///  - never carve into a player's build (the companion may not wreck houses),
///  - never leave the body hanging over a long drop.
/// </summary>
public static class GeniusTeleportLanding
{
    /// <summary>How far up/down to look for a pocket before carving one.</summary>
    private const int SearchRange = 6;

    /// <summary>A fall from higher than this hurts, so find footing instead.</summary>
    private const int SafeDropHeight = 4;

    public readonly record struct Result(Vector3 Position, string Note, string? Error);

    public static Result Resolve(ComponentGeniusBrain brain, Point3 target)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var surface = terrain.GetTopHeight(target.X, target.Z);
        var requested = Math.Clamp(target.Y, 2, 250);

        if (LavaNear(terrain, target.X, requested, target.Z))
        {
            return new Result(
                default,
                "",
                $"error[blocked]: there is lava at ({target.X},{requested},{target.Z}) — " +
                "I will not materialise in it. Pick a spot a few blocks away, or " +
                "teleport to the surface and descend_to from there");
        }

        // 1. An existing pocket at (or very near) the requested height.
        for (var offset = 0; offset <= SearchRange; offset++)
        {
            foreach (var y in offset == 0 ? [requested] : new[] { requested - offset, requested + offset })
            {
                if (y is < 2 or > 250 || !IsStandable(terrain, target.X, y, target.Z))
                {
                    continue;
                }

                var note = y == requested
                    ? ""
                    : $" (asked for y={requested}; the nearest open footing was y={y})";
                return new Result(new Vector3(target.X + 0.5f, y, target.Z + 0.5f), note, null);
            }
        }

        // 2. Solid rock: carve a pocket. This is the underground case that used
        //    to bounce to the surface.
        if (IsBuriedInStone(terrain, target.X, requested, target.Z))
        {
            if (TouchesPlayerBuild(brain, target.X, requested, target.Z))
            {
                return new Result(
                    default,
                    "",
                    $"error[blocked]: ({target.X},{requested},{target.Z}) is inside something built " +
                    "— I will not cut a hole in the player's structures. Pick a spot further away");
            }

            for (var height = 0; height < 2; height++)
            {
                brain.SubsystemTerrain.ChangeCell(target.X, requested + height, target.Z, 0);
            }

            return new Result(
                new Vector3(target.X + 0.5f, requested, target.Z + 0.5f),
                $" — that was solid rock, so I opened a small pocket to stand in " +
                $"(surface here is y={surface}; I am {surface - requested} blocks underground)",
                null);
        }

        // 3. Open air with nothing to land on: drop to the first real floor
        //    BELOW the request rather than teleporting to the roof of the world.
        for (var y = requested; y >= 2; y--)
        {
            if (IsStandable(terrain, target.X, y, target.Z))
            {
                var fall = requested - y;
                return new Result(
                    new Vector3(target.X + 0.5f, y, target.Z + 0.5f),
                    fall > SafeDropHeight
                        ? $" (asked for y={requested}, which is open air; landed on the floor {fall} blocks below)"
                        : "",
                    null);
            }
        }

        return new Result(new Vector3(target.X + 0.5f, surface + 1, target.Z + 0.5f),
            $" (nothing to stand on anywhere below y={requested}, so I landed on the surface)",
            null);
    }

    /// <summary>Feet and head clear, and something solid directly underfoot.</summary>
    private static bool IsStandable(Terrain terrain, int x, int y, int z) =>
        !IsSolid(terrain, x, y, z)
        && !IsSolid(terrain, x, y + 1, z)
        && IsSolid(terrain, x, y - 1, z)
        && !LavaNear(terrain, x, y, z);

    /// <summary>Fully enclosed in diggable ground — the carve case.</summary>
    private static bool IsBuriedInStone(Terrain terrain, int x, int y, int z) =>
        IsSolid(terrain, x, y, z) && IsSolid(terrain, x, y - 1, z);

    private static bool IsSolid(Terrain terrain, int x, int y, int z)
    {
        if (y is < 0 or > 255)
        {
            return true;
        }

        var contents = Terrain.ExtractContents(terrain.GetCellValue(x, y, z));
        return BlocksManager.Blocks[contents].IsCollidable;
    }

    private static bool LavaNear(Terrain terrain, int x, int y, int z)
    {
        for (var dy = -1; dy <= 2; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var cellY = y + dy;
                    if (cellY is < 0 or > 255)
                    {
                        continue;
                    }

                    var contents = Terrain.ExtractContents(
                        terrain.GetCellValue(x + dx, cellY, z + dz));
                    if (BlocksManager.Blocks[contents] is MagmaBlock)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Crafted blocks in the two cells we would open. Natural stone teleports
    /// are fine; cutting into someone's wall is not, and the "绝不拆玩家的建筑"
    /// rule has to hold for teleport exactly as it does for digging.
    /// </summary>
    private static bool TouchesPlayerBuild(ComponentGeniusBrain brain, int x, int y, int z)
    {
        for (var height = 0; height < 2; height++)
        {
            var contents = Terrain.ExtractContents(
                brain.SubsystemTerrain.Terrain.GetCellValue(x, y + height, z));
            if (GeniusProtectedBlocks.IsPlayerBuilt(contents))
            {
                return true;
            }
        }

        return false;
    }
}
