using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Hard-wired self-preservation that outbids the LLM (Numen's rule: the model
/// is the lowest bidder for the body). Runs at the end of every brain update,
/// so when an instinct fires its movement overrides whatever the current
/// order requested this frame: lava escape, drowning ascent, fire dousing.
/// The system prompt carries a one-line self-description of each, so the
/// model knows these are handled and never needs to micro-manage them.
/// </summary>
public sealed class GeniusInstincts
{
    private Point3? _lastSafeCell;
    private double _nextSafeCellTime;
    private double _nextWaterScanTime;
    private Point3? _waterCell;

    /// <summary>Human-readable label of the instinct currently overriding the body, if any.</summary>
    public string? ActiveInstinct { get; private set; }

    public void Tick(ComponentGeniusBrain brain, float dt)
    {
        var creature = brain.Creature;
        var body = creature.ComponentBody;
        var terrain = brain.SubsystemTerrain.Terrain;
        var position = body.Position;
        var feetCell = Terrain.ToCell(position);

        // 1. Standing in lava: jump and head for the last safe footing.
        if (body.ImmersionFluidBlock is MagmaBlock)
        {
            creature.ComponentLocomotion.JumpOrder = 1f;
            SteerTo(brain, _lastSafeCell);
            ActiveInstinct = "escaping lava";
            return;
        }

        // 2. Drowning: swim up and back toward remembered air. The navigator
        // has its own breath guard for planned swims; this one catches
        // everything else (following the player into a lake, knockback...).
        var headInWater = BlocksManager.Blocks[Terrain.ExtractContents(
            terrain.GetCellValue(feetCell.X, feetCell.Y + 1, feetCell.Z))] is WaterBlock;
        var health = creature.ComponentHealth;
        if (headInWater && health is not null && health.Air < 0.25f)
        {
            creature.ComponentLocomotion.JumpOrder = 1f;
            SteerTo(brain, _lastSafeCell);
            ActiveInstinct = "surfacing for air";
            return;
        }

        // 3. On fire: sprint to nearby water if there is any.
        var onFire = creature.Entity.FindComponent<ComponentOnFire>();
        if (onFire?.IsOnFire == true)
        {
            if (brain.m_subsystemTime.GameTime >= _nextWaterScanTime)
            {
                _nextWaterScanTime = brain.m_subsystemTime.GameTime + 0.5;
                _waterCell = FindNearbyWater(terrain, feetCell);
            }

            if (_waterCell is { } water)
            {
                SteerTo(brain, water);
                ActiveInstinct = "dousing fire in water";
                return;
            }
        }
        else
        {
            _waterCell = null;
        }

        ActiveInstinct = null;

        // Remember safe footing: solid ground, head in air, not burning.
        if (brain.m_subsystemTime.GameTime >= _nextSafeCellTime
            && !headInWater
            && body.StandingOnValue.HasValue
            && body.ImmersionFluidBlock is null
            && onFire?.IsOnFire != true)
        {
            _nextSafeCellTime = brain.m_subsystemTime.GameTime + 0.5;
            _lastSafeCell = feetCell;
        }
    }

    private static void SteerTo(ComponentGeniusBrain brain, Point3? cell)
    {
        if (cell is not { } target)
        {
            return;
        }

        brain.m_componentPathfinding.SetDestination(
            new Vector3(target.X + 0.5f, target.Y, target.Z + 0.5f),
            1f, 0.5f, 0,
            useRandomMovements: false, ignoreHeightDifference: true,
            raycastDestination: false, null!);
    }

    private static Point3? FindNearbyWater(Terrain terrain, Point3 center)
    {
        Point3? best = null;
        var bestDistance = int.MaxValue;
        for (var dx = -6; dx <= 6; dx++)
        {
            for (var dz = -6; dz <= 6; dz++)
            {
                for (var dy = -2; dy <= 2; dy++)
                {
                    var y = center.Y + dy;
                    if (y is < 1 or > 254)
                    {
                        continue;
                    }

                    var contents = Terrain.ExtractContents(
                        terrain.GetCellValue(center.X + dx, y, center.Z + dz));
                    if (BlocksManager.Blocks[contents] is not WaterBlock)
                    {
                        continue;
                    }

                    var distance = dx * dx + dy * dy + dz * dz;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = new Point3(center.X + dx, y, center.Z + dz);
                    }
                }
            }
        }

        return best;
    }
}
