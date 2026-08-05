using Engine;
using Game;
using SurvivalcraftGenius.Npc.Nav;

namespace SurvivalcraftGenius.Npc;

/// <summary>Digs one block over real dig time with the best tool equipped.</summary>
public sealed class TimedDigger
{
    private Point3? _cell;
    private float _timeNeeded;
    private float _timeSpent;

    public enum DigStatus
    {
        Idle,
        Digging,
        Done,
        Undiggable,
    }

    public Point3? Cell => _cell;

    public void Start(ComponentGeniusBrain brain, Point3 cell)
    {
        _cell = cell;
        _timeSpent = 0f;
        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
        EquipBestToolFor(brain, cellValue);
        _timeNeeded = brain.Miner.CalculateDigTime(
            cellValue,
            Terrain.ExtractContents(brain.Miner.ActiveBlockValue));
    }

    public DigStatus Tick(ComponentGeniusBrain brain, float dt)
    {
        if (_cell is not { } cell)
        {
            return DigStatus.Idle;
        }

        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
        if (Terrain.ExtractContents(cellValue) == 0)
        {
            _cell = null;
            return DigStatus.Done;
        }

        if (float.IsPositiveInfinity(_timeNeeded))
        {
            _cell = null;
            return DigStatus.Undiggable;
        }

        _timeSpent += dt;
        if (_timeSpent < _timeNeeded)
        {
            return DigStatus.Digging;
        }

        var toolLevel = BlocksManager.Blocks[Terrain.ExtractContents(brain.Miner.ActiveBlockValue)].ToolLevel;
        brain.SubsystemTerrain.DestroyCell(
            toolLevel, cell.X, cell.Y, cell.Z, 0, noDrop: false, noParticleSystem: false);
        brain.Miner.DamageActiveTool(1);
        _cell = null;
        return DigStatus.Done;
    }

    public static void EquipBestToolFor(ComponentGeniusBrain brain, int cellValue)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return;
        }

        var bestSlot = inventory.ActiveSlotIndex;
        var bestTime = brain.Miner.CalculateDigTime(
            cellValue, Terrain.ExtractContents(inventory.GetSlotValue(bestSlot)));
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var candidate = brain.Miner.CalculateDigTime(
                cellValue, Terrain.ExtractContents(inventory.GetSlotValue(slot)));
            if (candidate < bestTime)
            {
                bestTime = candidate;
                bestSlot = slot;
            }
        }

        inventory.ActiveSlotIndex = bestSlot;
    }
}

public enum NavStatus
{
    Running,
    Arrived,
    Failed,
}

/// <summary>
/// Travel with optional terrain modification, powered by a 3D A* planner
/// (Numen/Baritone-style cost search adapted to Survivalcraft): plans on a
/// background thread over dig/place/swim/fall move primitives, then executes
/// the plan step by step — digging queued cells with real tool times, opening
/// doors instead of eating them, bridging gaps, and retreating to air when a
/// swim runs the NPC low on breath.
/// </summary>
public sealed class TunnelNavigator(Vector3 target, bool allowDigging, float arriveDistance = 2.2f)
{
    private const int MaxStalledReplans = 6;
    private const int MaxTotalReplans = 40;

    private readonly TimedDigger _digger = new();
    private readonly List<Point3> _airTrail = [];
    private Task<NavPlan?>? _planning;
    private CancellationTokenSource? _planCancel;
    private NavPlan? _plan;
    private int _stepIndex;
    private double _stepDeadline;
    private double _planFreshDeadline;
    private int _stalledReplans;
    private int _totalReplans;
    private float _bestGoalDistance = float.MaxValue;
    private bool _sawToolLimit;
    private bool _sawMissingBlocks;
    private bool _surfacing;
    private HashSet<Point3>? _penalized;
    private Vector3? _currentDestination;
    private double _waitingForChunksSince;

    public string FailureReason { get; private set; } = "";

    /// <summary>
    /// True while the async planner is thinking and the body is idle. Orders
    /// freeze their deadline on this (planning wall-clock is not body work).
    /// </summary>
    public bool PlanningInFlight => _planning is { IsCompleted: false };

    public NavStatus Tick(ComponentGeniusBrain brain, float dt)
    {
        var myPosition = brain.Creature.ComponentBody.Position;
        if (Vector3.Distance(myPosition, target) <= arriveDistance)
        {
            StopMoving(brain);
            return NavStatus.Arrived;
        }

        if (BreathGuard(brain, myPosition) is { } breathStatus)
        {
            return breathStatus;
        }

        if (_planning is { IsCompleted: true } finished)
        {
            _planning = null;
            var plan = finished.Status == TaskStatus.RanToCompletion ? finished.Result : null;
            if (plan?.ToolLimited == true)
            {
                // A shorter route existed but the dig-time tool gate cut it off
                // — remember it so the failure autopsy can say "get a pick".
                _sawToolLimit = true;
            }

            if (plan is null || plan.Steps.Count == 0)
            {
                return AccountSetback(brain, myPosition);
            }

            _plan = plan;
            _stepIndex = 0;
            _stepDeadline = 0.0;
            _planFreshDeadline = brain.m_subsystemTime.GameTime + 30.0;
        }

        if (PlanningInFlight)
        {
            return NavStatus.Running;
        }

        if (_plan is null)
        {
            // Unloaded terrain reads as all-air, which would make the planner
            // burn its replan budget in milliseconds and blame the terrain.
            // Chunks load around players and (via the expedition keeper)
            // around the NPC itself — so an unloaded start just means "wait a
            // few seconds", not "give up". A goal in unloaded terrain is fine:
            // segmented planning walks to the loaded edge, the keeper loads
            // the next ring, and the next segment continues.
            var terrain = brain.SubsystemTerrain.Terrain;
            var start = Terrain.ToCell(myPosition);
            if (terrain.GetChunkAtCell(start.X, start.Z) is null)
            {
                var now = brain.m_subsystemTime.GameTime;
                if (_waitingForChunksSince == 0.0)
                {
                    _waitingForChunksSince = now;
                }

                if (now - _waitingForChunksSince > 15.0)
                {
                    FailureReason = "the terrain here never loaded (I waited 15s) — " +
                        "the world loads around players and around me on expeditions; " +
                        "teleport me near the player and retry";
                    return NavStatus.Failed;
                }

                return NavStatus.Running;
            }

            _waitingForChunksSince = 0.0;
            StartPlanning(brain, myPosition);
            return NavStatus.Running;
        }

        if (brain.m_subsystemTime.GameTime >= _planFreshDeadline)
        {
            // The world may have changed under a long-running plan.
            _plan = null;
            return NavStatus.Running;
        }

        return ExecuteStep(brain, dt, myPosition);
    }

    private NavStatus ExecuteStep(ComponentGeniusBrain brain, float dt, Vector3 myPosition)
    {
        var steps = _plan!.Steps;
        if (_stepIndex >= steps.Count)
        {
            // Segment exhausted (partial path, or arrival radius not yet met):
            // plan the next leg from here — Numen's segmented planning.
            _plan = null;
            return NavStatus.Running;
        }

        var now = brain.m_subsystemTime.GameTime;
        var step = steps[_stepIndex];
        if (_stepDeadline == 0.0)
        {
            _stepDeadline = now + 6.0;
        }

        var stepCenter = CellCenter(step.Feet);
        if (Vector3.Distance(myPosition, stepCenter) > 6f)
        {
            // Knocked away / fell off the route.
            return AccountSetback(brain, myPosition);
        }

        // 1. Place the support block first (bridging / pillaring).
        if (step.Place is { } placeCell && !IsSolidAt(brain, placeCell))
        {
            if (step.Kind == StepKind.Pillar)
            {
                return TickPillar(brain, step, placeCell, myPosition, now);
            }

            if (!TryPlaceBlock(brain, placeCell))
            {
                _sawMissingBlocks = true;
                return AccountSetback(brain, myPosition);
            }

            return NavStatus.Running;
        }

        // 2. Clear queued cells: open doors, dig the rest over real tool time.
        switch (_digger.Tick(brain, dt))
        {
            case TimedDigger.DigStatus.Digging:
                _stepDeadline = now + 6.0;
                return NavStatus.Running;
            case TimedDigger.DigStatus.Undiggable:
                FailureReason = "hit a block I cannot dig";
                return NavStatus.Failed;
        }

        foreach (var cell in step.Dig)
        {
            var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
            var contents = Terrain.ExtractContents(cellValue);
            if (contents == 0)
            {
                continue;
            }

            var block = BlocksManager.Blocks[contents];
            if (block is WaterBlock)
            {
                continue;
            }

            if (IsDoorLike(block) && TryOpenDoor(brain, cell, cellValue))
            {
                _stepDeadline = now + 6.0;
                return NavStatus.Running;
            }

            if (!allowDigging && !IsDoorLike(block))
            {
                return AccountSetback(brain, myPosition);
            }

            // Tool gate: the plan assumed our snapshot tools; a broken pick
            // must not turn into minutes of bare-handed scraping.
            TimedDigger.EquipBestToolFor(brain, cellValue);
            var digTime = brain.Miner.CalculateDigTime(
                cellValue, Terrain.ExtractContents(brain.Miner.ActiveBlockValue));
            if (digTime > 8.5f)
            {
                var blockName = block.GetDisplayName(brain.SubsystemTerrain, cellValue);
                FailureReason = $"my tools are too weak/broken to tunnel through {blockName} " +
                    "(craft or get a pick first)";
                return NavStatus.Failed;
            }

            _digger.Start(brain, cell);
            return NavStatus.Running;
        }

        // 3. Move. Coalesce a straight run of clean walk steps into one hop.
        var moveIndex = CoalesceWalkRun(steps, _stepIndex, myPosition);
        var moveCenter = CellCenter(steps[moveIndex].Feet);
        SetDirectDestination(brain, moveCenter, ignoreHeight: step.Kind == StepKind.Fall);

        if (step.Kind is StepKind.Jump
            && HorizontalDistance(myPosition, stepCenter) < 1.4f
            && myPosition.Y < step.Feet.Y + 0.2f)
        {
            brain.Creature.ComponentLocomotion.JumpOrder = 1f;
        }

        if (HorizontalDistance(myPosition, moveCenter) < 0.6f
            && MathF.Abs(myPosition.Y - steps[moveIndex].Feet.Y) < 1.2f)
        {
            _stepIndex = moveIndex + 1;
            _stepDeadline = 0.0;
            _currentDestination = null;
            RecordAirTrail(brain, steps[moveIndex].Feet);
            return NavStatus.Running;
        }

        return now > _stepDeadline ? AccountSetback(brain, myPosition) : NavStatus.Running;
    }

    private NavStatus TickPillar(
        ComponentGeniusBrain brain, NavStep step, Point3 placeCell, Vector3 myPosition, double now)
    {
        // Jump, then slot the block under our own feet at the apex.
        brain.Creature.ComponentLocomotion.JumpOrder = 1f;
        if (myPosition.Y > placeCell.Y + 0.95f && !TryPlaceBlock(brain, placeCell))
        {
            _sawMissingBlocks = true;
            return AccountSetback(brain, myPosition);
        }

        return now > _stepDeadline ? AccountSetback(brain, myPosition) : NavStatus.Running;
    }

    private void StartPlanning(ComponentGeniusBrain brain, Vector3 myPosition)
    {
        var (world, caps) = ScNavWorld.Capture(brain, allowDigging);
        caps.PenalizedCells = _penalized;
        var start = Terrain.ToCell(myPosition);
        var goal = target;
        var arrive = arriveDistance;
        _planCancel?.Cancel();
        _planCancel = new CancellationTokenSource();
        var token = _planCancel.Token;
        _planning = Task.Run(() =>
        {
            try
            {
                return NavAStar.Plan(world, start, goal, arrive, caps, token);
            }
            catch
            {
                return null;
            }
        }, token);
        StopMoving(brain);
    }

    /// <summary>
    /// Shared bookkeeping for every "that didn't work" path: plan failure, step
    /// timeout, knock-off, missing blocks. Progress is measured by closing
    /// distance to the goal; too many replans without progress means boxed in.
    /// </summary>
    private NavStatus AccountSetback(ComponentGeniusBrain brain, Vector3 myPosition)
    {
        _plan = null;
        _currentDestination = null;
        var distance = Vector3.Distance(myPosition, target);
        if (_bestGoalDistance - distance >= 1.5f)
        {
            _bestGoalDistance = distance;
            _stalledReplans = 0;
        }
        else
        {
            _stalledReplans++;
        }

        if (++_totalReplans >= MaxTotalReplans || _stalledReplans >= MaxStalledReplans)
        {
            FailureReason = BuildAutopsy(distance);
            StopMoving(brain);
            return NavStatus.Failed;
        }

        return NavStatus.Running;
    }

    /// <summary>Teach the LLM why the route failed, not just that it failed.</summary>
    private string BuildAutopsy(float distance)
    {
        var reason = $"no viable route — still {distance:0}m from the target after " +
            $"{_totalReplans} attempts";
        if (_sawToolLimit)
        {
            reason += "; a shorter way exists but my tools are too weak to dig it";
        }

        if (_sawMissingBlocks)
        {
            reason += "; I ran out of spare blocks for bridging/steps";
        }

        if (!allowDigging)
        {
            reason += "; digging is not allowed (dig_through=true may help)";
        }

        return reason;
    }

    private NavStatus? BreathGuard(ComponentGeniusBrain brain, Vector3 myPosition)
    {
        var health = brain.Creature.ComponentHealth;
        if (health is null)
        {
            return null;
        }

        var headCell = Terrain.ToCell(myPosition);
        headCell.Y += 1;
        var headInWater = BlocksManager.Blocks[Terrain.ExtractContents(
            brain.SubsystemTerrain.Terrain.GetCellValue(headCell.X, headCell.Y, headCell.Z))] is WaterBlock;

        if (_surfacing)
        {
            if (!headInWater && health.Air > 0.85f)
            {
                _surfacing = false;
                _plan = null;
                return NavStatus.Running;
            }

            return RetreatTowardsAir(brain, myPosition);
        }

        if (headInWater && health.Air < 0.45f)
        {
            // About to drown: abandon the route, poison its upcoming water
            // cells so the replan steers around, and walk back the way we came.
            _surfacing = true;
            _penalized ??= [];
            if (_plan is not null)
            {
                for (var i = _stepIndex; i < Math.Min(_plan.Steps.Count, _stepIndex + 12); i++)
                {
                    _penalized.Add(_plan.Steps[i].Feet);
                }
            }

            _penalized.Add(Terrain.ToCell(myPosition));
            _plan = null;
            _planCancel?.Cancel();
            return RetreatTowardsAir(brain, myPosition);
        }

        if (!headInWater)
        {
            RecordAirTrail(brain, Terrain.ToCell(myPosition));
        }

        return null;
    }

    private NavStatus RetreatTowardsAir(ComponentGeniusBrain brain, Vector3 myPosition)
    {
        while (_airTrail.Count > 0)
        {
            var waypoint = _airTrail[^1];
            var center = CellCenter(waypoint);
            if (Vector3.Distance(myPosition, center) < 1.2f)
            {
                _airTrail.RemoveAt(_airTrail.Count - 1);
                continue;
            }

            SetDirectDestination(brain, center);
            return NavStatus.Running;
        }

        FailureReason = "I nearly drowned underwater and had no safe way back to air";
        return NavStatus.Failed;
    }

    private void RecordAirTrail(ComponentGeniusBrain brain, Point3 cell)
    {
        var headValue = brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y + 1, cell.Z);
        if (BlocksManager.Blocks[Terrain.ExtractContents(headValue)] is WaterBlock)
        {
            return;
        }

        if (_airTrail.Count > 0 && _airTrail[^1] == cell)
        {
            return;
        }

        _airTrail.Add(cell);
        if (_airTrail.Count > 48)
        {
            _airTrail.RemoveAt(0);
        }
    }

    private static int CoalesceWalkRun(List<NavStep> steps, int index, Vector3 myPosition)
    {
        var end = index;
        var first = steps[index];
        if (first.Kind != StepKind.Walk || first.Dig.Length > 0 || first.Place.HasValue)
        {
            return end;
        }

        var previous = first.Feet;
        Point3? direction = null;
        while (end + 1 < steps.Count && end - index < 5)
        {
            var next = steps[end + 1];
            if (next.Kind != StepKind.Walk || next.Dig.Length > 0 || next.Place.HasValue
                || next.Feet.Y != first.Feet.Y)
            {
                break;
            }

            var delta = new Point3(
                next.Feet.X - previous.X, 0, next.Feet.Z - previous.Z);
            direction ??= delta;
            if (delta != direction.Value)
            {
                break;
            }

            previous = next.Feet;
            end++;
        }

        return end;
    }

    private void SetDirectDestination(
        ComponentGeniusBrain brain, Vector3 destination, bool ignoreHeight = false)
    {
        if (_currentDestination is { } current && Vector3.Distance(current, destination) < 0.1f
            && brain.m_componentPathfinding.Destination.HasValue)
        {
            return;
        }

        _currentDestination = destination;
        brain.m_componentPathfinding.SetDestination(
            destination, 1f, 0.5f, 0,
            useRandomMovements: false, ignoreHeightDifference: ignoreHeight,
            raycastDestination: false, null!);
    }

    private void StopMoving(ComponentGeniusBrain brain)
    {
        _currentDestination = null;
        brain.m_componentPathfinding.Stop();
    }

    private static Vector3 CellCenter(Point3 cell) =>
        new(cell.X + 0.5f, cell.Y, cell.Z + 0.5f);

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static bool IsSolidAt(ComponentGeniusBrain brain, Point3 cell)
    {
        var contents = Terrain.ExtractContents(
            brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z));
        return contents != 0 && BlocksManager.Blocks[contents].IsCollidable;
    }

    private static bool IsDoorLike(Block block) =>
        block is DoorBlock or FenceGateBlock or TrapdoorBlock;

    private static bool TryOpenDoor(ComponentGeniusBrain brain, Point3 cell, int cellValue)
    {
        var data = Terrain.ExtractData(cellValue);
        var block = BlocksManager.Blocks[Terrain.ExtractContents(cellValue)];
        int newData;
        switch (block)
        {
            case DoorBlock when !DoorBlock.GetOpen(data):
                newData = DoorBlock.SetOpen(data, open: true);
                break;
            case FenceGateBlock when !FenceGateBlock.GetOpen(data):
                newData = FenceGateBlock.SetOpen(data, open: true);
                break;
            case TrapdoorBlock when !TrapdoorBlock.GetOpen(data):
                newData = TrapdoorBlock.SetOpen(data, open: true);
                break;
            default:
                return false;
        }

        brain.SubsystemTerrain.ChangeCell(
            cell.X, cell.Y, cell.Z, Terrain.ReplaceData(cellValue, newData));
        return true;
    }

    private static bool TryPlaceBlock(ComponentGeniusBrain brain, Point3 cell)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return false;
        }

        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            if (value == 0 || inventory.GetSlotCount(slot) <= 0)
            {
                continue;
            }

            var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            if (!block.IsPlaceable || !block.IsCollidable || block is FluidBlock)
            {
                continue;
            }

            brain.SubsystemTerrain.DestroyCell(
                0, cell.X, cell.Y, cell.Z, value, noDrop: true, noParticleSystem: true);
            inventory.RemoveSlotItems(slot, 1);
            return true;
        }

        return false;
    }
}
