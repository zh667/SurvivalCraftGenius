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

    private enum Phase
    {
        Search,
        Travel,
        Dig,
        Return,
    }

    private readonly Dictionary<string, int> _collected = [];
    private readonly HashSet<string> _seenBlockNames = [];
    private readonly TimedDigger _digger = new();
    private readonly List<Candidate> _candidates = [];
    private Point3 _scanCenter;
    private Phase _phase = Phase.Search;
    private Vector3? _startPosition;
    private Point3 _oreCell;
    private TunnelNavigator? _navigator;
    private int _dugCount;

    protected override float TimeoutSeconds => 1500f;

    protected override string TimeoutResult() =>
        Summary() + "; error: ran out of time — call mine_resource again with the remaining count to continue";

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        _startPosition = brain.Creature.ComponentBody.Position;
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
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

                    var suggestions = Agent.NameSuggest.Clause(query, _seenBlockNames);
                    return $"error: no blocks matching '{query}' within ~{SearchRadius}m " +
                        $"(searched down to {SearchDepth} blocks below me){suggestions}" +
                        "; names must match this game's own terms (this is not Minecraft) — " +
                        "verify with query_recipes or query_help, or move me elsewhere and retry";
                }

                var toolCheck = CheckToolEfficiency(brain, found.Value);
                if (toolCheck is not null)
                {
                    return toolCheck;
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

    private readonly record struct Candidate(Point3 Cell, int Contents, bool IsOre);

    /// <summary>
    /// Nearest matching block, ore-preferred. The full box scan costs ~88k
    /// cell reads on the main thread, so it runs once per area: results are
    /// cached as a candidate list that later iterations pop from (revalidating
    /// each pick against live terrain); a rescan happens only when the list
    /// runs dry or the NPC has wandered far from the scanned center.
    /// </summary>
    private Point3? FindNearestMatch(ComponentGeniusBrain brain)
    {
        var center = Terrain.ToCell(brain.Creature.ComponentBody.Position);
        if (_candidates.Count == 0 || MovedFarFromScan(center))
        {
            ScanForCandidates(brain, center);
        }

        return TakeNearestCandidate(brain, center);
    }

    private bool MovedFarFromScan(Point3 center)
    {
        var dx = center.X - _scanCenter.X;
        var dy = center.Y - _scanCenter.Y;
        var dz = center.Z - _scanCenter.Z;
        return dx * dx + dy * dy + dz * dz > 16 * 16;
    }

    /// <summary>
    /// If both natural ore blocks (name contains 矿/ore) and crafted blocks
    /// match the query, only the ore ones count — "煤" must never target the
    /// player's decorative coal blocks at home.
    /// </summary>
    private void ScanForCandidates(ComponentGeniusBrain brain, Point3 center)
    {
        _candidates.Clear();
        _scanCenter = center;
        var terrain = brain.SubsystemTerrain.Terrain;
        var matchKinds = new Dictionary<int, int>();
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

                    if (!matchKinds.TryGetValue(contents, out var kind))
                    {
                        var block = BlocksManager.Blocks[contents];
                        var displayName = block.GetDisplayName(brain.SubsystemTerrain, value);
                        var craftingId = block.GetCraftingId(value) ?? "";
                        _seenBlockNames.Add(displayName);
                        var matches = displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craftingId.Contains(query, StringComparison.OrdinalIgnoreCase);
                        var isOre = displayName.Contains('矿')
                            || craftingId.Contains("ore", StringComparison.OrdinalIgnoreCase);
                        kind = matches ? (isOre ? 2 : 1) : 0;
                        matchKinds[contents] = kind;
                    }

                    if (kind != 0)
                    {
                        _candidates.Add(new Candidate(
                            new Point3(center.X + dx, y, center.Z + dz), contents, kind == 2));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pops the best cached candidate: still-valid (terrain may have changed
    /// since the scan), ore before non-ore, then nearest to where I stand now.
    /// </summary>
    private Point3? TakeNearestCandidate(ComponentGeniusBrain brain, Point3 center)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        _candidates.RemoveAll(candidate =>
            Terrain.ExtractContents(terrain.GetCellValue(
                candidate.Cell.X, candidate.Cell.Y, candidate.Cell.Z)) != candidate.Contents);

        var bestIndex = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < _candidates.Count; i++)
        {
            var candidate = _candidates[i];
            if (bestIndex >= 0 && _candidates[bestIndex].IsOre && !candidate.IsOre)
            {
                continue;
            }

            var dx = candidate.Cell.X - center.X;
            var dy = candidate.Cell.Y - center.Y;
            var dz = candidate.Cell.Z - center.Z;
            var distanceSquared = dx * dx + dy * dy * 4f + dz * dz;
            var upgradeToOre = bestIndex >= 0 && candidate.IsOre && !_candidates[bestIndex].IsOre;
            if (bestIndex < 0 || upgradeToOre || distanceSquared < bestDistance)
            {
                bestIndex = i;
                bestDistance = distanceSquared;
            }
        }

        if (bestIndex < 0)
        {
            return null;
        }

        var cell = _candidates[bestIndex].Cell;
        _candidates.RemoveAt(bestIndex);
        return cell;
    }

    /// <summary>
    /// Refuses to grind for minutes with bare hands: if the best tool still
    /// needs more than ~6s per block, ask for/craft a proper pick first.
    /// </summary>
    private static string? CheckToolEfficiency(ComponentGeniusBrain brain, Point3 cell)
    {
        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
        TimedDigger.EquipBestToolFor(brain, cellValue);
        var digTime = brain.Miner.CalculateDigTime(
            cellValue, Terrain.ExtractContents(brain.Miner.ActiveBlockValue));
        if (digTime <= 6f)
        {
            return null;
        }

        var name = GeniusInventoryOps.ItemName(brain, cellValue);
        return $"error: digging '{name}' with what I have would take ~{digTime:F0}s per block — " +
            "give me a proper pick or let me craft one first (craft 'pick'), then retry";
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
