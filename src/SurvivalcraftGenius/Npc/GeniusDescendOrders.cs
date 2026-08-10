using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Digs a spiral staircase straight down to a target depth.
///
/// Why this exists: the A* tunnel navigator plans a route through terrain, and
/// a 70-block vertical descent blows past its node budget — every replan makes
/// no measurable progress toward the goal, so it reports no_path. Playtest 6
/// showed the fallout: the model asked for goto(y=40, dig_through=true), got
/// no_path, and degenerated into hand-rolling the descent one dig_block+goto
/// pair per block ("他挖矿的方式我没看懂"). Descending is a deterministic loop,
/// not a search problem, so it gets a deterministic implementation.
///
/// The staircase rotates through the four compass directions so the shaft
/// stays inside a 2x2 footprint AND is walkable back up — a plain 1x1 drop
/// shaft would strand the companion at the bottom.
///
/// Playtest 7 (v0.9.6) footnote: every single descend_to call failed with
/// "could not step down into the shaft" after 0-3 levels, because the carve
/// below only opened two cells. See <see cref="CarveHeights"/>.
/// </summary>
public sealed class DescendOrder(int targetY, string? lookingFor = null) : GeniusOrder
{
    /// <summary>Steps between hazard rescans; also the stuck-check window.</summary>
    private const float StepTimeoutSeconds = 20f;

    /// <summary>
    /// Heights to clear in the next column, relative to the landing cell. See
    /// <see cref="GeniusDescentGeometry.CarveHeights"/> — it lives there so the
    /// solid-rock simulation in the tests exercises the same numbers this order
    /// digs with. Opening only two of the three was the v0.9.5 bug.
    /// </summary>
    private static readonly int[] CarveHeights = GeniusDescentGeometry.CarveHeights;

    private static readonly Point3[] Directions =
        [.. GeniusDescentGeometry.Directions.Select(d => new Point3(d.X, 0, d.Z))];

    private readonly TimedDigger _digger = new();
    private int _directionIndex;
    private int _levelsDug;
    private int _startY;
    private Point3? _stepTarget;
    private float _stepElapsed;
    private bool _walking;

    protected override float TimeoutSeconds => 900f;

    protected override string TimeoutResult() =>
        $"error[timeout]: dug down {_levelsDug} levels and ran out of time — I'm at y={_lastY}; " +
        "call descend_to again with the same target to continue";

    private int _lastY;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        _startY = Terrain.ToCell(brain.Creature.ComponentBody.Position).Y;
        _lastY = _startY;
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var feet = Terrain.ToCell(brain.Creature.ComponentBody.Position);
        _lastY = feet.Y;

        if (feet.Y <= Math.Max(2, targetY))
        {
            return Arrived(brain, feet);
        }

        if (feet.Y <= 2)
        {
            return $"error[blocked]: I hit the bottom of the world at y={feet.Y} " +
                $"after digging down {_levelsDug} levels";
        }

        // Standing in water: every cell around me is water, so the hazard check
        // below can only ever refuse. Playtest 7: the model teleported into an
        // ocean, got "水 right below me", and retried the identical call twice
        // because nothing told it what would actually help.
        if (brain.Creature.ComponentBody.ImmersionFluidBlock is WaterBlock)
        {
            return $"error[blocked]: I'm in water at y={feet.Y} — a shaft dug from here just " +
                "floods and I drown. Get me onto dry land first: scan_surroundings or " +
                "look_around to find a shore, goto it, then descend_to again";
        }

        // Walking into the step we just carved out.
        if (_walking)
        {
            _stepElapsed += dt;
            if (_stepTarget is { } target && feet.Y <= target.Y)
            {
                _walking = false;
                _stepTarget = null;
                _stepElapsed = 0f;
                _levelsDug++;
                GrabDrops(brain);
                return null;
            }

            if (_stepElapsed > StepTimeoutSeconds)
            {
                return $"error[blocked]: I could not step down into the shaft at " +
                    $"({feet.X},{feet.Y},{feet.Z}) after digging {_levelsDug} levels" +
                    Obstruction(brain, _stepTarget) +
                    "; teleport or goto me somewhere else and call descend_to again";
            }

            if (_stepTarget is { } destination)
            {
                WalkTowards(
                    brain,
                    new Vector3(destination.X + 0.5f, destination.Y, destination.Z + 0.5f),
                    0.6f);
            }

            return null;
        }

        // Mid-dig: keep swinging.
        if (_digger.Cell is not null)
        {
            switch (_digger.Tick(brain, dt))
            {
                case TimedDigger.DigStatus.Undiggable:
                    return $"error[tool_too_weak]: I cannot dig through the block at " +
                        $"({_digger.Cell?.X},{_digger.Cell?.Y},{_digger.Cell?.Z}) — " +
                        $"stopped at y={feet.Y} after {_levelsDug} levels";
                case TimedDigger.DigStatus.Digging:
                    return null;
            }
        }

        var direction = Directions[_directionIndex % Directions.Length];
        var next = new Point3(feet.X + direction.X, feet.Y - 1, feet.Z + direction.Z);

        if (HazardNear(terrain, next) is { } hazard)
        {
            // Try the other three directions before giving up: lava pockets sit
            // right above the diamond band (y15-20), so meeting one is normal.
            for (var attempt = 1; attempt < Directions.Length; attempt++)
            {
                var alternative = Directions[(_directionIndex + attempt) % Directions.Length];
                var candidate = new Point3(feet.X + alternative.X, feet.Y - 1, feet.Z + alternative.Z);
                if (HazardNear(terrain, candidate) is null)
                {
                    _directionIndex = (_directionIndex + attempt) % Directions.Length;
                    return null;
                }
            }

            return $"error[blocked]: {hazard} right below me at y={feet.Y} " +
                $"(dug down {_levelsDug} levels) — I stopped rather than dig into it";
        }

        // Carve a body-sized opening in the next column, then step in.
        foreach (var height in CarveHeights)
        {
            var cell = new Point3(next.X, next.Y + height, next.Z);
            if (cell.Y > 255)
            {
                continue;
            }

            if (Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y, cell.Z)) != 0)
            {
                _digger.Start(brain, cell);
                return null;
            }
        }

        _stepTarget = next;
        _walking = true;
        _stepElapsed = 0f;
        _directionIndex++;
        return null;
    }

    private string Arrived(ComponentGeniusBrain brain, Point3 feet)
    {
        GrabDrops(brain);
        var summary = $"dug a staircase down from y={_startY} to y={feet.Y} " +
            $"({_levelsDug} levels); I'm at ({feet.X},{feet.Y},{feet.Z})";
        if (string.IsNullOrWhiteSpace(lookingFor))
        {
            return summary;
        }

        return summary + ". " + GeniusPerception.FindBlocks(brain, lookingFor!, 32);
    }

    /// <summary>
    /// Names whatever is still solid in the column we tried to step into, so a
    /// stuck descent says "石灰岩 blocks (3,97,5)" instead of "something is in
    /// the way" — the model can only act on the former.
    /// </summary>
    private static string Obstruction(ComponentGeniusBrain brain, Point3? stepTarget)
    {
        if (stepTarget is not { } target)
        {
            return "";
        }

        var terrain = brain.SubsystemTerrain.Terrain;

        foreach (var height in CarveHeights)
        {
            var y = target.Y + height;
            if (y is < 0 or > 255)
            {
                continue;
            }

            var value = terrain.GetCellValue(target.X, y, target.Z);
            var contents = Terrain.ExtractContents(value);
            if (contents != 0)
            {
                var name = BlocksManager.Blocks[contents].GetDisplayName(brain.SubsystemTerrain, value);
                return $" — {name} is still blocking ({target.X},{y},{target.Z})";
            }
        }

        return " — the shaft is open, so something is pushing me back (a mob, or I'm stuck on geometry)";
    }

    /// <summary>
    /// Refuses to open a cell that is (or touches) lava or water. Checked on the
    /// target and its six neighbours: breaking the last block between a shaft
    /// and a lava pocket is the classic way to lose a companion plus its whole
    /// backpack.
    /// </summary>
    private static string? HazardNear(Terrain terrain, Point3 cell)
    {
        foreach (var offset in new[]
        {
            new Point3(0, 0, 0), new Point3(0, -1, 0),
            new Point3(1, 0, 0), new Point3(-1, 0, 0),
            new Point3(0, 0, 1), new Point3(0, 0, -1),
        })
        {
            var x = cell.X + offset.X;
            var y = cell.Y + offset.Y;
            var z = cell.Z + offset.Z;
            if (y is < 0 or > 255)
            {
                continue;
            }

            var block = BlocksManager.Blocks[
                Terrain.ExtractContents(terrain.GetCellValue(x, y, z))];
            if (block is MagmaBlock)
            {
                return "岩浆";
            }

            if (block is WaterBlock)
            {
                return "水";
            }
        }

        return null;
    }

    private static void GrabDrops(ComponentGeniusBrain brain) => brain.VacuumNearbyPickables(4f);
}
