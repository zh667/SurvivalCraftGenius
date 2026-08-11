using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Cuts every ripe crop in a radius and collects the drops.
///
/// Without this the farming loop had no end: the companion could till, sow and
/// fertilize, and then the field just stood there. Sowing without reaping is
/// not farming.
///
/// Cutting goes through the block's own GetDropValues rather than DestroyCell,
/// so a rye at size 7 yields grain and the bonus roll exactly as the engine
/// would. (SubsystemPickables.AddPickable in this engine version takes no owner
/// entity — that overload is newer — so the drops are ordinary loose items and
/// we sweep them up cell by cell as we go.)
/// </summary>
public sealed class HarvestCropsOrder(Point3? center, int radius) : GeniusOrder
{
    private const float ReachDistance = 4.5f;

    /// <summary>Seconds per cut — roughly a swing.</summary>
    private const float CutSeconds = 0.35f;

    private const int MaxRadius = 16;
    private const int VerticalRange = 3;

    private readonly List<Point3> _ripe = [];
    private readonly List<string> _notRipe = [];
    private readonly Dictionary<string, int> _taken = [];
    private int _index;
    private int _cut;
    private float _cutElapsed;

    protected override float TimeoutSeconds => 600f;

    protected override string TimeoutResult() =>
        Summary() + "; error[timeout]: ran out of time — call harvest_crops again for the rest";

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        var origin = center ?? Terrain.ToCell(brain.Creature.ComponentBody.Position);
        var reach = Math.Clamp(radius, 1, MaxRadius);
        var terrain = brain.SubsystemTerrain.Terrain;

        for (var dx = -reach; dx <= reach; dx++)
        {
            for (var dz = -reach; dz <= reach; dz++)
            {
                for (var dy = -VerticalRange; dy <= VerticalRange; dy++)
                {
                    var x = origin.X + dx;
                    var y = origin.Y + dy;
                    var z = origin.Z + dz;
                    if (!terrain.IsCellValid(x, y, z))
                    {
                        continue;
                    }

                    var value = terrain.GetCellValue(x, y, z);
                    var contents = Terrain.ExtractContents(value);
                    if (!GeniusHarvestRules.IsCrop(contents))
                    {
                        continue;
                    }

                    var (size, isWild) = Decode(contents, Terrain.ExtractData(value));
                    if (GeniusHarvestRules.IsRipe(contents, size, isWild))
                    {
                        _ripe.Add(new Point3(x, y, z));
                    }
                    else if (_notRipe.Count < 3)
                    {
                        _notRipe.Add(GeniusHarvestRules.NotRipeReason(contents, size, isWild));
                    }
                }
            }
        }

        // Nearest first, so a timeout still leaves a tidy worked area rather
        // than holes scattered across the whole field.
        var me = brain.Creature.ComponentBody.Position;
        _ripe.Sort((a, b) => Vector3.DistanceSquared(me, Center(a))
            .CompareTo(Vector3.DistanceSquared(me, Center(b))));
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        if (_index >= _ripe.Count)
        {
            brain.VacuumNearbyPickables(6f);
            return Summary();
        }

        var cell = _ripe[_index];
        var center2 = Center(cell);
        if (Vector3.Distance(brain.Creature.ComponentBody.Position, center2) > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                _index++;
                return null;
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, center2, 2.5f);
            }

            return null;
        }

        _cutElapsed += dt;
        if (_cutElapsed < CutSeconds)
        {
            return null;
        }

        _cutElapsed = 0f;
        Cut(brain, cell);
        _index++;
        // Sweep as we go: standing next to what we just cut is the cheapest
        // moment to pick it up.
        brain.VacuumNearbyPickables(3f);
        return null;
    }

    private void Cut(ComponentGeniusBrain brain, Point3 cell)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var value = terrain.GetCellValue(cell.X, cell.Y, cell.Z);
        var contents = Terrain.ExtractContents(value);
        if (!GeniusHarvestRules.IsCrop(contents))
        {
            // Grown, withered or eaten since the survey.
            return;
        }

        var block = BlocksManager.Blocks[contents];
        var drops = new List<BlockDropValue>();
        block.GetDropValues(brain.SubsystemTerrain, value, 0, 0, drops, out _);
        var position = Center(cell);
        foreach (var drop in drops.Where(drop => drop.Count > 0))
        {
            brain.SubsystemPickables.AddPickable(
                drop.Value, drop.Count, position, null, null);
            var name = BlocksManager.Blocks[Terrain.ExtractContents(drop.Value)]
                .GetDisplayName(null, drop.Value);
            _taken[name] = _taken.GetValueOrDefault(name) + drop.Count;
        }

        brain.SubsystemTerrain.ChangeCell(cell.X, cell.Y, cell.Z, 0);
        _cut++;
    }

    /// <summary>
    /// Size and wildness live in per-block data layouts, so each crop decodes
    /// its own. Pumpkins have no wild flag.
    /// </summary>
    private static (int Size, bool IsWild) Decode(int contents, int data) => contents switch
    {
        GeniusHarvestRules.RyeContents => (RyeBlock.GetSize(data), RyeBlock.GetIsWild(data)),
        GeniusHarvestRules.CottonContents => (CottonBlock.GetSize(data), CottonBlock.GetIsWild(data)),
        _ => (BasePumpkinBlock.GetSize(data), false),
    };

    private static Vector3 Center(Point3 cell) =>
        new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);

    private string Summary()
    {
        if (_cut == 0)
        {
            var why = _notRipe.Count > 0
                ? ";附近的作物:" + string.Join("、", _notRipe)
                : "";
            return $"没有可收的作物(搜了半径 {Math.Clamp(radius, 1, MaxRadius)}m){why}";
        }

        var haul = _taken.Count > 0
            ? ",收到 " + string.Join("、", _taken.Select(entry => $"{entry.Key}×{entry.Value}"))
            : "";
        var left = _notRipe.Count > 0 ? ";还没熟的先留着了" : "";
        return $"收了 {_cut} 株{haul}{left}";
    }
}
