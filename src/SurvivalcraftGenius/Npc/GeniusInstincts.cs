using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Hard-wired self-preservation that outbids the LLM (Numen's rule: the model
/// is the lowest bidder for the body). Runs at the end of every brain update,
/// so when an instinct fires its movement overrides whatever the current
/// order requested this frame: lava escape, drowning ascent, fire dousing,
/// and fighting back when attacked (Numen's MobDefenseChain). The system
/// prompt carries a one-line self-description of each, so the model knows
/// these are handled and never needs to micro-manage them.
/// </summary>
public sealed class GeniusInstincts
{
    private const float EngageSeconds = 12f;
    private const float DisengageRange = 20f;
    private const float StrikeRange = 2.2f;
    private const float MinHealthToFight = 0.3f;

    private Point3? _lastSafeCell;
    private double _nextSafeCellTime;
    private double _nextWaterScanTime;
    private Point3? _waterCell;
    private ComponentCreature? _attacker;
    private float _engageRemaining;
    private double _nextStrikePathTime;

    /// <summary>Human-readable label of the instinct currently overriding the body, if any.</summary>
    public string? ActiveInstinct { get; private set; }

    /// <summary>
    /// Wired to ComponentHealth.Attacked (same hook vanilla creatures use).
    /// Players are never counterattacked; a fresh hit re-arms the window and
    /// retargets to the latest aggressor.
    /// </summary>
    public void NotifyAttacked(ComponentCreature? attacker)
    {
        if (attacker is null or ComponentPlayer)
        {
            return;
        }

        _attacker = attacker;
        _engageRemaining = EngageSeconds;
    }

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

        // 4. Fight back: something hit me. Survival instincts above always win
        // (no brawling while drowning); nearly dead → disengage and leave the
        // body to the engine's flee behavior.
        if (_attacker is { } attacker)
        {
            _engageRemaining -= dt;
            var gone = attacker.ComponentHealth.Health <= 0f
                || attacker.ComponentBody.Entity.Project is null;
            if (gone
                || _engageRemaining <= 0f
                || health is null
                || health.Health < MinHealthToFight
                || Vector3.Distance(position, attacker.ComponentBody.Position) > DisengageRange)
            {
                _attacker = null;
                creature.ComponentCreatureModel.AttackOrder = false;
            }
            else
            {
                FightBack(brain, attacker, position);
                ActiveInstinct = $"fighting back at {attacker.DisplayName}";
                return;
            }
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

    /// <summary>Same melee core as AttackOrder: close in, swing on the hit moment.</summary>
    private void FightBack(ComponentGeniusBrain brain, ComponentCreature attacker, Vector3 myPosition)
    {
        var model = brain.Creature.ComponentCreatureModel;
        var targetPosition = attacker.ComponentBody.Position;
        model.LookAtOrder = attacker.ComponentCreatureModel.EyePosition;
        if (Vector3.Distance(myPosition, targetPosition) <= StrikeRange)
        {
            brain.m_componentPathfinding.Stop();
            model.AttackOrder = true;
            if (model.IsAttackHitMoment)
            {
                var direction = Vector3.Normalize(targetPosition - myPosition);
                brain.Miner.Hit(
                    attacker.ComponentBody,
                    targetPosition + new Vector3(0f, 1f, 0f),
                    direction);
            }

            return;
        }

        model.AttackOrder = false;
        if (brain.m_subsystemTime.GameTime >= _nextStrikePathTime)
        {
            _nextStrikePathTime = brain.m_subsystemTime.GameTime + 0.4;
            brain.m_componentPathfinding.SetDestination(
                targetPosition, 1f, 1.5f, 2000,
                useRandomMovements: true, ignoreHeightDifference: false,
                raycastDestination: false, attacker.ComponentBody);
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
