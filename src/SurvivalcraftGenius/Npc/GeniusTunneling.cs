using Engine;
using Game;

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
/// Travel with optional terrain modification: uses the vanilla pathfinder while
/// it works; when stuck (and digging is allowed) it carves a stair-stepped
/// tunnel toward the target — digging through rock, stepping down, and building
/// steps up from inventory blocks. This is M3's simplified take on
/// "A* with dig costs": greedy directional tunneling with hazard checks.
/// </summary>
public sealed class TunnelNavigator(Vector3 target, bool allowDigging, float arriveDistance = 2.2f)
{
    private readonly TimedDigger _digger = new();
    private readonly List<Point3> _pendingCells = [];
    private bool _tunneling;
    private double _nextPathUpdateTime;
    private double _nextRepathProbeTime;
    private Vector3? _stepDestination;
    private double _stepDeadline;
    private float _bestDistance = float.MaxValue;
    private double _lastProgressTime;

    public string FailureReason { get; private set; } = "";

    public NavStatus Tick(ComponentGeniusBrain brain, float dt)
    {
        var myPosition = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(myPosition, target);
        if (distance <= arriveDistance)
        {
            brain.m_componentPathfinding.Stop();
            return NavStatus.Arrived;
        }

        // Progress watchdog: the vanilla pathfinder can dither for a long time
        // in enclosed spaces before admitting IsStuck. Any tick that fails to
        // close in on the target for a while forces the tunneling fallback.
        var now = brain.m_subsystemTime.GameTime;
        if (_lastProgressTime == 0.0 || distance < _bestDistance - 0.3f)
        {
            _bestDistance = Math.Min(_bestDistance, distance);
            _lastProgressTime = now;
        }

        if (!_tunneling && allowDigging && now - _lastProgressTime > 5.0)
        {
            StartTunneling(brain);
            _lastProgressTime = now;
        }
        else if (_tunneling && now - _lastProgressTime > 35.0)
        {
            FailureReason = "I'm wedged and not making progress";
            return NavStatus.Failed;
        }

        return _tunneling ? TickTunnel(brain, dt, myPosition) : TickPathfinding(brain, myPosition);
    }

    private void StartTunneling(ComponentGeniusBrain brain)
    {
        brain.m_componentPathfinding.Stop();
        _tunneling = true;
        _pendingCells.Clear();
        _stepDestination = null;
    }

    private NavStatus TickPathfinding(ComponentGeniusBrain brain, Vector3 myPosition)
    {
        if (brain.m_componentPathfinding.IsStuck)
        {
            if (!allowDigging)
            {
                FailureReason = "path is blocked and digging is not allowed here";
                return NavStatus.Failed;
            }

            StartTunneling(brain);
            return NavStatus.Running;
        }

        if (!brain.m_componentPathfinding.Destination.HasValue
            || brain.m_subsystemTime.GameTime >= _nextPathUpdateTime)
        {
            _nextPathUpdateTime = brain.m_subsystemTime.GameTime + 2.0;
            brain.m_componentPathfinding.SetDestination(
                target, 1f, Math.Max(1f, arriveDistance - 0.7f), 2000,
                useRandomMovements: true, ignoreHeightDifference: false,
                raycastDestination: false, null!);
        }

        return NavStatus.Running;
    }

    private NavStatus TickTunnel(ComponentGeniusBrain brain, float dt, Vector3 myPosition)
    {
        // 1. Finish any block currently being dug.
        switch (_digger.Tick(brain, dt))
        {
            case TimedDigger.DigStatus.Digging:
                return NavStatus.Running;
            case TimedDigger.DigStatus.Undiggable:
                FailureReason = "hit a block I cannot dig";
                return NavStatus.Failed;
        }

        // 2. Dig queued cells one at a time.
        while (_pendingCells.Count > 0)
        {
            var cell = _pendingCells[0];
            _pendingCells.RemoveAt(0);
            var contents = Terrain.ExtractContents(
                brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z));
            if (contents == 0)
            {
                continue;
            }

            var block = BlocksManager.Blocks[contents];
            if (block is WaterBlock)
            {
                // Water is passable — swim through instead of digging.
                continue;
            }

            if (block is FluidBlock)
            {
                FailureReason = "there is lava in the way — stopping to stay safe";
                return NavStatus.Failed;
            }

            _digger.Start(brain, cell);
            return NavStatus.Running;
        }

        // 3. Walk into the cleared step.
        if (_stepDestination is { } stepDestination)
        {
            if (Vector3.Distance(myPosition, stepDestination) <= 0.9f)
            {
                _stepDestination = null;
            }
            else if (brain.m_subsystemTime.GameTime >= _stepDeadline)
            {
                _stepDestination = null;
            }
            else
            {
                if (!brain.m_componentPathfinding.Destination.HasValue)
                {
                    brain.m_componentPathfinding.SetDestination(
                        stepDestination, 1f, 0.6f, 0,
                        useRandomMovements: false, ignoreHeightDifference: false,
                        raycastDestination: false, null!);
                }

                return NavStatus.Running;
            }
        }

        // 4. Periodically give the vanilla pathfinder another chance.
        if (brain.m_subsystemTime.GameTime >= _nextRepathProbeTime)
        {
            _nextRepathProbeTime = brain.m_subsystemTime.GameTime + 8.0;
            _tunneling = false;
            brain.m_componentPathfinding.Stop();
            return NavStatus.Running;
        }

        return PlanNextStep(brain, myPosition);
    }

    private NavStatus PlanNextStep(ComponentGeniusBrain brain, Vector3 myPosition)
    {
        var feet = Terrain.ToCell(myPosition);
        var delta = target - myPosition;
        var (dx, dz) = DominantHorizontal(delta);
        var wantDown = delta.Y < -1.5f;
        var wantUp = delta.Y > 1.5f;
        if (dx == 0 && dz == 0)
        {
            // Directly above/below: pick any horizontal direction to spiral.
            dx = 1;
        }

        var terrain = brain.SubsystemTerrain.Terrain;
        if (wantUp)
        {
            // Step up onto (feet + dir, y+1): needs that support solid (place if
            // missing) and two air cells above it.
            var stepCell = new Point3(feet.X + dx, feet.Y, feet.Z + dz);
            var support = Terrain.ExtractContents(terrain.GetCellValue(stepCell.X, stepCell.Y, stepCell.Z));
            if (support == 0 && !TryPlaceBlock(brain, stepCell))
            {
                FailureReason = "I need spare blocks in my inventory to build steps upward";
                return NavStatus.Failed;
            }

            Enqueue(brain, new Point3(stepCell.X, stepCell.Y + 1, stepCell.Z));
            Enqueue(brain, new Point3(stepCell.X, stepCell.Y + 2, stepCell.Z));
            SetStep(brain, new Vector3(stepCell.X + 0.5f, stepCell.Y + 1, stepCell.Z + 0.5f));
            return NavStatus.Running;
        }

        var forwardFeet = new Point3(feet.X + dx, feet.Y, feet.Z + dz);
        if (wantDown)
        {
            // Descend one step: clear head/body/new-feet, ensure landing support.
            var landing = new Point3(forwardFeet.X, forwardFeet.Y - 1, forwardFeet.Z);
            var below = Terrain.ExtractContents(terrain.GetCellValue(landing.X, landing.Y - 1, landing.Z));
            if (below == 0 && !TryPlaceBlock(brain, new Point3(landing.X, landing.Y - 1, landing.Z)))
            {
                FailureReason = "the way down has no floor and I have no blocks to place";
                return NavStatus.Failed;
            }

            Enqueue(brain, new Point3(forwardFeet.X, forwardFeet.Y + 1, forwardFeet.Z));
            Enqueue(brain, forwardFeet);
            Enqueue(brain, landing);
            SetStep(brain, new Vector3(landing.X + 0.5f, landing.Y, landing.Z + 0.5f));
            return NavStatus.Running;
        }

        // Level tunnel: clear feet + head ahead; make sure there's a floor.
        var floor = Terrain.ExtractContents(
            terrain.GetCellValue(forwardFeet.X, forwardFeet.Y - 1, forwardFeet.Z));
        if (floor == 0 && !TryPlaceBlock(brain, new Point3(forwardFeet.X, forwardFeet.Y - 1, forwardFeet.Z)))
        {
            FailureReason = "there is a drop ahead and I have no blocks to bridge it";
            return NavStatus.Failed;
        }

        Enqueue(brain, forwardFeet);
        Enqueue(brain, new Point3(forwardFeet.X, forwardFeet.Y + 1, forwardFeet.Z));
        SetStep(brain, new Vector3(forwardFeet.X + 0.5f, forwardFeet.Y, forwardFeet.Z + 0.5f));
        return NavStatus.Running;
    }

    private void Enqueue(ComponentGeniusBrain brain, Point3 cell)
    {
        var contents = Terrain.ExtractContents(
            brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z));
        if (contents != 0 && BlocksManager.Blocks[contents] is not WaterBlock)
        {
            _pendingCells.Add(cell);
        }
    }

    private void SetStep(ComponentGeniusBrain brain, Vector3 destination)
    {
        _stepDestination = destination;
        _stepDeadline = brain.m_subsystemTime.GameTime + 4.0;
        brain.m_componentPathfinding.Stop();
    }

    private static (int Dx, int Dz) DominantHorizontal(Vector3 delta)
    {
        if (Math.Abs(delta.X) < 0.4f && Math.Abs(delta.Z) < 0.4f)
        {
            return (0, 0);
        }

        return Math.Abs(delta.X) >= Math.Abs(delta.Z)
            ? (Math.Sign(delta.X), 0)
            : (0, Math.Sign(delta.Z));
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
