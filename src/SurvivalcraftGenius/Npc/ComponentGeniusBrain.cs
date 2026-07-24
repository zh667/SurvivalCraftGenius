using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// The NPC-side executor. Owns at most one active order (goto/dig/place) plus an
/// optional continuous follow mode, and drives the vanilla pathfinding stack.
/// All calls happen on the game main thread; async callers get their result via
/// the TaskCompletionSource attached to the order.
/// </summary>
public sealed class ComponentGeniusBrain : ComponentBehavior, IUpdateable
{
    public SubsystemTerrain m_subsystemTerrain = null!;
    public SubsystemTime m_subsystemTime = null!;
    public SubsystemBodies m_subsystemBodies = null!;
    public SubsystemPickables m_subsystemPickables = null!;
    public SubsystemBlockEntities m_subsystemBlockEntities = null!;
    public ComponentCreature m_componentCreature = null!;
    public ComponentPathfinding m_componentPathfinding = null!;
    public ComponentMiner m_componentMiner = null!;

    private GeniusOrder? _order;
    private ComponentBody? _followTarget;
    private double _nextFollowUpdateTime;
    private float _suppressedTime;

    public UpdateOrder UpdateOrder => UpdateOrder.Default;

    public override float ImportanceLevel
    {
        get
        {
            // 310 outranks even low-health fleeing (300): orders run to the
            // end — or to death, which the revive-and-resume loop handles.
            if (_order is not null)
            {
                return 310f;
            }

            return _followTarget is not null ? 190f : 0f;
        }
    }

    /// <summary>Where the NPC last died (for gear recovery); set on fatal removal.</summary>
    public static Vector3? LastDeathPosition { get; private set; }

    public ComponentCreature Creature => m_componentCreature;

    public ComponentMiner Miner => m_componentMiner;

    public SubsystemTerrain SubsystemTerrain => m_subsystemTerrain;

    public SubsystemBodies SubsystemBodies => m_subsystemBodies;

    public SubsystemPickables SubsystemPickables => m_subsystemPickables;

    public SubsystemBlockEntities SubsystemBlockEntities => m_subsystemBlockEntities;

    public bool HasActiveOrder => _order is not null;

    /// <summary>Starts an order; a running order is cancelled with a failure result.</summary>
    public void StartOrder(GeniusOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _order?.Finish("error: superseded by a newer order");
        _order = order;
        order.Start(this);
    }

    public void StartFollowing(ComponentBody target)
    {
        _followTarget = target;
        _nextFollowUpdateTime = 0.0;
    }

    public void StopMoving()
    {
        _followTarget = null;
        _order?.Finish("error: stopped");
        _order = null;
        m_componentPathfinding.Stop();
    }

    public void Update(float dt)
    {
        if (_order is not null)
        {
            if (!IsActive)
            {
                // A higher-priority behavior (fleeing at low health, importance
                // 300) is overriding us. Fail fast instead of hanging the tool
                // call until its timeout.
                _suppressedTime += dt;
                if (_suppressedTime > 8f)
                {
                    _suppressedTime = 0f;
                    _order.Finish("error: I'm in danger (fleeing) and cannot work right now");
                    _order = null;
                }

                return;
            }

            _suppressedTime = 0f;
            var finished = _order.Tick(this, dt);
            if (finished)
            {
                _order = null;
                m_componentPathfinding.Stop();
            }

            return;
        }

        if (_followTarget is not null && IsActive)
        {
            if (_followTarget.Entity.Project is null)
            {
                _followTarget = null;
                m_componentPathfinding.Stop();
                return;
            }

            if (m_subsystemTime.GameTime >= _nextFollowUpdateTime)
            {
                _nextFollowUpdateTime = m_subsystemTime.GameTime + 0.5;
                var distance = Vector3.Distance(
                    m_componentCreature.ComponentBody.Position,
                    _followTarget.Position);
                if (distance > 3.5f)
                {
                    m_componentPathfinding.SetDestination(
                        _followTarget.Position,
                        1f,
                        3f,
                        2000,
                        useRandomMovements: true,
                        ignoreHeightDifference: false,
                        raycastDestination: false,
                        _followTarget);
                }
                else
                {
                    m_componentPathfinding.Stop();
                }
            }
        }
    }

    public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
        m_subsystemTime = Project.FindSubsystem<SubsystemTime>(throwOnError: true);
        m_subsystemBodies = Project.FindSubsystem<SubsystemBodies>(throwOnError: true);
        m_subsystemPickables = Project.FindSubsystem<SubsystemPickables>(throwOnError: true);
        m_subsystemBlockEntities = Project.FindSubsystem<SubsystemBlockEntities>(throwOnError: true);
        m_componentCreature = Entity.FindComponent<ComponentCreature>(throwOnError: true)!;
        m_componentPathfinding = Entity.FindComponent<ComponentPathfinding>(throwOnError: true)!;
        m_componentMiner = Entity.FindComponent<ComponentMiner>(throwOnError: true)!;
    }

    public override void OnEntityAdded()
    {
        Engine.Log.Information(
            $"[Genius] NPC entity added to project at {m_componentCreature.ComponentBody.Position}.");
        base.OnEntityAdded();
    }

    public override void OnEntityRemoved()
    {
        var died = m_componentCreature.ComponentHealth.Health <= 0f;
        Engine.Log.Information(
            $"[Genius] NPC entity removed from project (pos={m_componentCreature.ComponentBody.Position}, " +
            $"health={m_componentCreature.ComponentHealth.Health}, died={died}).");
        SpillInventoryIfDead();
        _order?.Finish(died
            ? "error: I died on the job"
            : "error: the companion was removed from the world");
        _order = null;
        base.OnEntityRemoved();
    }

    /// <summary>On death, drop everything carried so the player can recover it.</summary>
    private void SpillInventoryIfDead()
    {
        if (m_componentCreature.ComponentHealth.Health > 0f
            || m_componentMiner.Inventory is not { } inventory)
        {
            return;
        }

        LastDeathPosition = m_componentCreature.ComponentBody.Position;
        var position = m_componentCreature.ComponentBody.Position + new Vector3(0f, 0.5f, 0f);
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            var count = inventory.GetSlotCount(slot);
            if (value != 0 && count > 0)
            {
                m_subsystemPickables.AddPickable(value, count, position, null, null);
                inventory.RemoveSlotItems(slot, count);
            }
        }
    }
}

/// <summary>A multi-frame task executed by the brain on the game thread.</summary>
public abstract class GeniusOrder
{
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double _deadline;

    public Task<string> Completion => _completion.Task;

    protected abstract float TimeoutSeconds { get; }

    public void Start(ComponentGeniusBrain brain)
    {
        _deadline = brain.m_subsystemTime.GameTime + TimeoutSeconds;
        try
        {
            OnStart(brain);
        }
        catch (Exception exception)
        {
            Finish($"error: {exception.Message}");
        }
    }

    /// <summary>Returns true when the order is finished (result already set).</summary>
    public bool Tick(ComponentGeniusBrain brain, float dt)
    {
        if (_completion.Task.IsCompleted)
        {
            return true;
        }

        if (brain.m_subsystemTime.GameTime >= _deadline)
        {
            Finish("error: timed out");
            return true;
        }

        try
        {
            var result = OnTick(brain, dt);
            if (result is not null)
            {
                Finish(result);
                return true;
            }
        }
        catch (Exception exception)
        {
            Finish($"error: {exception.Message}");
            return true;
        }

        return false;
    }

    public void Finish(string result)
    {
        _completion.TrySetResult(result);
    }

    protected abstract void OnStart(ComponentGeniusBrain brain);

    /// <summary>Returns null while running, or the final result string.</summary>
    protected abstract string? OnTick(ComponentGeniusBrain brain, float dt);

    protected static void WalkTowards(
        ComponentGeniusBrain brain,
        Vector3 destination,
        float range)
    {
        brain.m_componentPathfinding.SetDestination(
            destination,
            1f,
            range,
            2000,
            useRandomMovements: true,
            ignoreHeightDifference: false,
            raycastDestination: false,
            null!);
    }
}

public sealed class GotoOrder(Point3 target, bool digThrough = false) : GeniusOrder
{
    private TunnelNavigator? _navigator;

    private Vector3 Destination => new(target.X + 0.5f, target.Y, target.Z + 0.5f);

    protected override float TimeoutSeconds => digThrough ? 240f : 90f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        _navigator = new TunnelNavigator(Destination, digThrough, arriveDistance: 2.0f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        switch (_navigator!.Tick(brain, dt))
        {
            case NavStatus.Arrived:
                return $"arrived at ({target.X}, {target.Y}, {target.Z})";
            case NavStatus.Failed:
                var position = brain.Creature.ComponentBody.Position;
                return $"error: {_navigator.FailureReason} — I'm at " +
                    $"({(int)position.X}, {(int)position.Y}, {(int)position.Z})" +
                    (digThrough ? "" : "; retry with dig_through=true to tunnel there");
            default:
                return null;
        }
    }
}

public sealed class DigOrder(Point3 target) : GeniusOrder
{
    private const float ReachDistance = 4.5f;
    private bool _digging;
    private float _digTimeNeeded;
    private float _digTimeSpent;

    private Vector3 BlockCenter => new(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);

    protected override float TimeoutSeconds => 120f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(target.X, target.Y, target.Z);
        if (Terrain.ExtractContents(cellValue) == 0)
        {
            Finish("error: that position is air, nothing to dig");
            return;
        }

        WalkTowards(brain, BlockCenter, 2.5f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(target.X, target.Y, target.Z);
        var contents = Terrain.ExtractContents(cellValue);
        if (contents == 0)
        {
            return "the block is already gone";
        }

        var position = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(position, BlockCenter);
        if (!_digging)
        {
            if (distance <= ReachDistance)
            {
                EquipBestToolFor(brain, cellValue);
                var activeValue = brain.Miner.ActiveBlockValue;
                _digTimeNeeded = brain.Miner.CalculateDigTime(cellValue, Terrain.ExtractContents(activeValue));
                if (float.IsPositiveInfinity(_digTimeNeeded))
                {
                    return "error: this block cannot be dug with my current tool";
                }

                brain.m_componentPathfinding.Stop();
                _digging = true;
            }
            else if (brain.m_componentPathfinding.IsStuck)
            {
                return "error: cannot get close enough to the block — path is blocked";
            }
            else if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, BlockCenter, 2.5f);
            }

            return null;
        }

        if (distance > ReachDistance + 2f)
        {
            _digging = false;
            WalkTowards(brain, BlockCenter, 2.5f);
            return null;
        }

        _digTimeSpent += dt;
        if (_digTimeSpent < _digTimeNeeded)
        {
            return null;
        }

        var block = BlocksManager.Blocks[contents];
        var blockName = block.GetDisplayName(brain.SubsystemTerrain, cellValue);
        var toolValue = brain.Miner.ActiveBlockValue;
        var toolLevel = BlocksManager.Blocks[Terrain.ExtractContents(toolValue)].ToolLevel;
        brain.SubsystemTerrain.DestroyCell(
            toolLevel,
            target.X,
            target.Y,
            target.Z,
            0,
            noDrop: false,
            noParticleSystem: false);
        brain.Miner.DamageActiveTool(1);
        return $"dug '{blockName}' at ({target.X}, {target.Y}, {target.Z}); drops fell on the ground";
    }

    /// <summary>Switches the active slot to whichever tool digs this block fastest.</summary>
    private static void EquipBestToolFor(ComponentGeniusBrain brain, int cellValue)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return;
        }

        var bestSlot = inventory.ActiveSlotIndex;
        var bestTime = brain.Miner.CalculateDigTime(
            cellValue,
            Terrain.ExtractContents(inventory.GetSlotValue(bestSlot)));
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var candidate = brain.Miner.CalculateDigTime(
                cellValue,
                Terrain.ExtractContents(inventory.GetSlotValue(slot)));
            if (candidate < bestTime)
            {
                bestTime = candidate;
                bestSlot = slot;
            }
        }

        inventory.ActiveSlotIndex = bestSlot;
    }
}

public sealed class PlaceOrder(Point3 target, int slotIndex) : GeniusOrder
{
    private const float ReachDistance = 4.5f;

    private Vector3 BlockCenter => new(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);

    protected override float TimeoutSeconds => 90f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        WalkTowards(brain, BlockCenter, 2.5f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null || slotIndex < 0 || slotIndex >= inventory.SlotsCount)
        {
            return "error: invalid inventory slot";
        }

        var position = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(position, BlockCenter);
        if (distance > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                return "error: cannot get close enough to the target spot";
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, BlockCenter, 2.5f);
            }

            return null;
        }

        var existing = brain.SubsystemTerrain.Terrain.GetCellValue(target.X, target.Y, target.Z);
        if (Terrain.ExtractContents(existing) != 0)
        {
            return "error: target position is not empty";
        }

        var slotValue = inventory.GetSlotValue(slotIndex);
        var slotCount = inventory.GetSlotCount(slotIndex);
        if (slotValue == 0 || slotCount <= 0)
        {
            return "error: that inventory slot is empty";
        }

        var block = BlocksManager.Blocks[Terrain.ExtractContents(slotValue)];
        if (!block.IsPlaceable)
        {
            return "error: that item is not a placeable block";
        }

        if (target.Y is <= 0 or >= 255)
        {
            return "error: cannot place at that height";
        }

        var blockName = block.GetDisplayName(brain.SubsystemTerrain, slotValue);
        brain.SubsystemTerrain.DestroyCell(
            0,
            target.X,
            target.Y,
            target.Z,
            slotValue,
            noDrop: false,
            noParticleSystem: true);
        inventory.RemoveSlotItems(slotIndex, 1);
        return $"placed '{blockName}' at ({target.X}, {target.Y}, {target.Z})";
    }
}
