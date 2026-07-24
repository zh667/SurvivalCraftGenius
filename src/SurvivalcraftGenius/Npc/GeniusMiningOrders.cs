using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// M3's autonomous expedition: find matching ore/blocks, tunnel to them, dig,
/// grab the drops, repeat until the quota is met, then walk back to where it
/// started. One tool call, one long deterministic loop — the LLM only sees the
/// final summary (token economy).
/// </summary>
public sealed class MineResourceOrder(string query, int targetCount) : GeniusOrder
{
    private const int SearchRadius = 24;
    private const int SearchDepth = 28;
    private const int SearchHeight = 8;
    private const float LowHealthAbort = 0.4f;

    private enum Phase
    {
        Search,
        Travel,
        Dig,
        Return,
    }

    private readonly Dictionary<string, int> _collected = [];
    private readonly TimedDigger _digger = new();
    private Phase _phase = Phase.Search;
    private Vector3? _startPosition;
    private Point3 _oreCell;
    private TunnelNavigator? _navigator;
    private int _dugCount;

    protected override float TimeoutSeconds => 600f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        _startPosition = brain.Creature.ComponentBody.Position;
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        if (brain.Creature.ComponentHealth.Health < LowHealthAbort)
        {
            return Summary() + "; error: I'm badly hurt and had to stop";
        }

        switch (_phase)
        {
            case Phase.Search:
            {
                var found = FindNearestMatch(brain);
                if (found is null)
                {
                    if (_dugCount > 0)
                    {
                        BeginReturn(brain);
                        return null;
                    }

                    return $"error: no blocks matching '{query}' within ~{SearchRadius}m " +
                        $"(searched down to {SearchDepth} blocks below me)";
                }

                _oreCell = found.Value;
                _navigator = new TunnelNavigator(
                    new Vector3(_oreCell.X + 0.5f, _oreCell.Y + 0.5f, _oreCell.Z + 0.5f),
                    allowDigging: true,
                    arriveDistance: 4.0f);
                _phase = Phase.Travel;
                return null;
            }

            case Phase.Travel:
                switch (_navigator!.Tick(brain, dt))
                {
                    case NavStatus.Arrived:
                        _digger.Start(brain, _oreCell);
                        _phase = Phase.Dig;
                        break;
                    case NavStatus.Failed:
                        return Summary() + $"; error: {_navigator.FailureReason}";
                }

                return null;

            case Phase.Dig:
                switch (_digger.Tick(brain, dt))
                {
                    case TimedDigger.DigStatus.Undiggable:
                        return Summary() + "; error: I cannot dig that block with my tools";
                    case TimedDigger.DigStatus.Done:
                    case TimedDigger.DigStatus.Idle:
                        _dugCount++;
                        GrabNearbyDrops(brain);
                        if (_dugCount >= Math.Max(1, targetCount))
                        {
                            BeginReturn(brain);
                        }
                        else
                        {
                            _phase = Phase.Search;
                        }

                        break;
                }

                return null;

            case Phase.Return:
                switch (_navigator!.Tick(brain, dt))
                {
                    case NavStatus.Arrived:
                        GrabNearbyDrops(brain);
                        return Summary() + "; I'm back";
                    case NavStatus.Failed:
                        return Summary() + $"; error on the way back: {_navigator.FailureReason} — " +
                            $"I'm at ({(int)brain.Creature.ComponentBody.Position.X}, " +
                            $"{(int)brain.Creature.ComponentBody.Position.Y}, " +
                            $"{(int)brain.Creature.ComponentBody.Position.Z})";
                }

                return null;

            default:
                return "error: internal phase error";
        }
    }

    private void BeginReturn(ComponentGeniusBrain brain)
    {
        _navigator = new TunnelNavigator(_startPosition!.Value, allowDigging: true, arriveDistance: 2.5f);
        _phase = Phase.Return;
    }

    /// <summary>Vacuums drops within 3.5m straight into the inventory.</summary>
    private void GrabNearbyDrops(ComponentGeniusBrain brain)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return;
        }

        var myPosition = brain.Creature.ComponentBody.Position;
        foreach (var pickable in brain.SubsystemPickables.Pickables)
        {
            if (pickable.ToRemove
                || Vector3.Distance(pickable.Position, myPosition) > 3.5f)
            {
                continue;
            }

            var leftover = ComponentInventoryBase.AcquireItems(inventory, pickable.Value, pickable.Count);
            var taken = pickable.Count - leftover;
            if (taken > 0)
            {
                var name = GeniusInventoryOps.ItemName(brain, pickable.Value);
                _collected[name] = _collected.TryGetValue(name, out var count) ? count + taken : taken;
            }

            if (leftover == 0)
            {
                pickable.ToRemove = true;
            }
            else
            {
                pickable.Count = leftover;
            }
        }
    }

    private Point3? FindNearestMatch(ComponentGeniusBrain brain)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var center = Terrain.ToCell(brain.Creature.ComponentBody.Position);
        Point3? best = null;
        var bestDistanceSquared = float.MaxValue;
        var matchingContents = new Dictionary<int, bool>();
        for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
        {
            for (var dz = -SearchRadius; dz <= SearchRadius; dz++)
            {
                if (terrain.GetChunkAtCell(center.X + dx, center.Z + dz) is null)
                {
                    continue;
                }

                for (var dy = -SearchDepth; dy <= SearchHeight; dy++)
                {
                    var y = center.Y + dy;
                    if (y is < 1 or > 255)
                    {
                        continue;
                    }

                    var value = terrain.GetCellValue(center.X + dx, y, center.Z + dz);
                    var contents = Terrain.ExtractContents(value);
                    if (contents == 0)
                    {
                        continue;
                    }

                    if (!matchingContents.TryGetValue(contents, out var matches))
                    {
                        var block = BlocksManager.Blocks[contents];
                        var displayName = block.GetDisplayName(brain.SubsystemTerrain, value);
                        var craftingId = block.GetCraftingId(value) ?? "";
                        matches = displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craftingId.Contains(query, StringComparison.OrdinalIgnoreCase);
                        matchingContents[contents] = matches;
                    }

                    if (!matches)
                    {
                        continue;
                    }

                    var distanceSquared = dx * dx + dy * dy * 4f + dz * dz;
                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = distanceSquared;
                        best = new Point3(center.X + dx, y, center.Z + dz);
                    }
                }
            }
        }

        return best;
    }

    private string Summary()
    {
        if (_collected.Count == 0)
        {
            return _dugCount == 0 ? "mined nothing" : $"dug {_dugCount} blocks but collected nothing";
        }

        var parts = _collected.Select(pair => $"{pair.Key} x{pair.Value}");
        return $"mined and collected: {string.Join(", ", parts)}";
    }
}
