using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Turns a patch of ground into farmland, one cell at a time.
///
/// This exists because the companion had no way to do it at all. Asked what was
/// blocking it, it answered for itself (playtest 9):
///
///   "难点不是材料,而是我的操作接口: 1. 木耙已装备,但我没有'挥动/使用工具'的动作;
///    dig_block只会把泥土挖掉,不能翻成耕土。2. 种子也需要对耕土执行'使用/播种',
///    place_block未必能代替。"
///
/// Exactly right. Digging and placing are the only two verbs it had, and
/// tilling is neither — it is SubsystemRakeBlockBehavior.OnUse, which no tool
/// exposed. This order performs that behaviour directly (the engine's own
/// version is a ChangeCell plus tool damage) rather than trying to synthesise
/// a raycast onto the right face from wherever the body happens to be standing.
/// </summary>
public sealed class TillSoilOrder(Point3 origin, int width, int length) : GeniusOrder
{
    private const float ReachDistance = 4.5f;

    /// <summary>Seconds per rake stroke — roughly the vanilla swing cadence.</summary>
    private const float StrokeSeconds = 0.45f;

    private readonly List<Point3> _plot = [];
    private readonly List<string> _skipped = [];
    private int _index;
    private int _tilled;
    private float _strokeElapsed;
    private int _strokesOnCell;

    /// <summary>Rake passes one cell may take before it is written off.</summary>
    private const int MaxStrokesPerCell = 4;

    protected override float TimeoutSeconds => 600f;

    protected override string TimeoutResult() =>
        Summary() + "; error[timeout]: ran out of time — call till_soil again for the rest";

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        // Snap each column to its actual ground rather than trusting the y we
        // were handed. Playtest 10 called till_soil three times and got
        // "下面是空的" nine cells at a time every time, because the model was
        // passing the y of the air it was standing in, or a y remembered from
        // a different site. The column is unambiguous; the y is not.
        for (var dx = 0; dx < Math.Clamp(width, 1, 16); dx++)
        {
            for (var dz = 0; dz < Math.Clamp(length, 1, 16); dz++)
            {
                var x = origin.X + dx;
                var z = origin.Z + dz;
                var ground = GeniusSiteSurvey.GroundHeight(brain, x, z);
                if (ground is { } groundY && Math.Abs(groundY - origin.Y) <= SnapRange)
                {
                    _plot.Add(new Point3(x, groundY, z));
                    if (groundY != origin.Y)
                    {
                        _snapped++;
                    }
                }
                else
                {
                    _plot.Add(new Point3(x, origin.Y, z));
                }
            }
        }
    }

    /// <summary>How far the given y may be off before we stop second-guessing it.</summary>
    private const int SnapRange = 4;

    private int _snapped;

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: no inventory";
        }

        if (GeniusFarming.FindRakeSlot(inventory) is not { } rakeSlot)
        {
            return "error[missing_material]: I need a rake (木耙/铜耙/铁耙/钻石耙) to till soil — " +
                "dig_block only removes dirt, it cannot turn it into farmland. " +
                "query_recipes 木耙 and craft one first";
        }

        if (_index >= _plot.Count)
        {
            return Summary() + AdviceForPlot(brain);
        }

        var cell = _plot[_index];
        var center = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        var distance = Vector3.Distance(brain.Creature.ComponentBody.Position, center);
        if (distance > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                _skipped.Add($"({cell.X},{cell.Y},{cell.Z}) 走不到");
                _index++;
                return null;
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, center, 2.5f);
            }

            return null;
        }

        _strokeElapsed += dt;
        if (_strokeElapsed < StrokeSeconds)
        {
            return null;
        }

        _strokeElapsed = 0f;
        // DamageActiveTool works on the active slot, so the rake has to be in
        // hand for the wear to land on it rather than on whatever was equipped.
        inventory.ActiveSlotIndex = rakeSlot;
        var terrain = brain.SubsystemTerrain.Terrain;
        var value = terrain.GetCellValue(cell.X, cell.Y, cell.Z);
        var contents = Terrain.ExtractContents(value);

        // Anything on top reverts farmland to dirt the moment it is placed, so
        // the cell has to be clear before there is any point tilling it.
        var above = Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y + 1, cell.Z));
        if (above != 0 && BlocksManager.Blocks[above].IsCollidable)
        {
            _skipped.Add($"({cell.X},{cell.Y},{cell.Z}) 上面压着方块");
            _index++;
            return null;
        }

        if (!GeniusFarming.HasSolidSupport(brain, cell))
        {
            _skipped.Add($"({cell.X},{cell.Y},{cell.Z}) 下面是空的");
            _index++;
            return null;
        }

        switch (contents)
        {
            case GeniusFarming.GrassContents:
                // Grass rakes down to dirt first — this cell needs a second
                // pass. Cap the passes: if the cell somehow refuses to change
                // (a mod, a protected region), looping here would burn the
                // whole 600s budget on one square and report nothing useful.
                if (++_strokesOnCell > MaxStrokesPerCell)
                {
                    _skipped.Add($"({cell.X},{cell.Y},{cell.Z}) 耙不动");
                    _index++;
                    _strokesOnCell = 0;
                    return null;
                }

                brain.SubsystemTerrain.ChangeCell(
                    cell.X, cell.Y, cell.Z,
                    Terrain.ReplaceContents(value, GeniusFarming.DirtContents));
                brain.Miner.DamageActiveTool(1);
                return null;

            case GeniusFarming.DirtContents:
                brain.SubsystemTerrain.ChangeCell(
                    cell.X, cell.Y, cell.Z,
                    Terrain.ReplaceContents(value, GeniusFarming.SoilContents));
                brain.Miner.DamageActiveTool(1);
                _tilled++;
                _index++;
                _strokesOnCell = 0;
                return null;

            case GeniusFarming.SoilContents:
                _index++;
                return null;

            default:
                _skipped.Add(
                    $"({cell.X},{cell.Y},{cell.Z}) 是" +
                    BlocksManager.Blocks[contents].GetDisplayName(brain.SubsystemTerrain, value) +
                    ",不是泥土/草地");
                _index++;
                return null;
        }
    }

    private string Summary()
    {
        var text = $"tilled {_tilled} cells into farmland around " +
            $"({origin.X},{origin.Y},{origin.Z})" +
            (_snapped > 0
                ? $" (snapped {_snapped} cells to the real ground height — the y you gave was off)"
                : "");
        if (_skipped.Count == 0)
        {
            return text;
        }

        text += $"; skipped {_skipped.Count}: " + string.Join(", ", _skipped.Take(6));
        return _tilled == 0
            ? text + ". Nothing here is farmable — use find_build_site with purpose=\"farm\" " +
                "to get a spot that is actually flat soil in daylight, then till_soil there"
            : text;
    }

    /// <summary>
    /// Farmland alone grows nothing if it is dark, and grows at half speed if
    /// it is dry — say so now rather than let the player wonder why the field
    /// sits there.
    /// </summary>
    private string AdviceForPlot(ComponentGeniusBrain brain)
    {
        if (_tilled == 0)
        {
            return "";
        }

        var sample = _plot.FirstOrDefault(cell =>
            Terrain.ExtractContents(brain.SubsystemTerrain.Terrain.GetCellValue(
                cell.X, cell.Y, cell.Z)) == GeniusFarming.SoilContents);
        return ". Soil state: " + GeniusFarming.DescribeSoil(brain, sample) +
            ". Next: plant_seed on these cells";
    }
}

/// <summary>
/// Sows seeds on prepared farmland. The other half of the interface gap: a seed
/// is "used" on the top face of the soil, which places a different block than
/// the seed itself (SeedsBlock.GetPlacementValue maps 棉花种子 → CottonBlock and
/// so on), so place_block genuinely could not stand in for it.
/// </summary>
public sealed class PlantSeedOrder(Point3 origin, string seedQuery, int count) : GeniusOrder
{
    private const float ReachDistance = 4.5f;
    private const float StrokeSeconds = 0.35f;

    private readonly List<Point3> _candidates = [];
    private readonly List<string> _skipped = [];
    private int _index;
    private int _planted;
    private float _strokeElapsed;
    private string _seedName = "";

    protected override float TimeoutSeconds => 600f;

    protected override string TimeoutResult() =>
        Summary() + "; error[timeout]: ran out of time — call plant_seed again for the rest";

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        // Spiral outward from the given cell so "plant 9 seeds here" fills the
        // patch the player just had tilled, without needing exact coordinates.
        var terrain = brain.SubsystemTerrain.Terrain;
        for (var ring = 0; ring <= 8 && _candidates.Count < 64; ring++)
        {
            foreach (var (x, z) in GeniusScanGeometry.RingColumns(origin.X, origin.Z, ring))
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    var cell = new Point3(x, origin.Y + dy, z);
                    if (Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y, cell.Z))
                            == GeniusFarming.SoilContents
                        && Terrain.ExtractContents(
                            terrain.GetCellValue(cell.X, cell.Y + 1, cell.Z)) == 0)
                    {
                        _candidates.Add(cell);
                    }
                }
            }
        }
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: no inventory";
        }

        if (GeniusFarming.FindSeedSlot(brain, inventory, seedQuery, out var matched)
            is not { } seedSlot)
        {
            var carried = GeniusFarming.CarriedSeedNames(brain, inventory).Distinct().ToList();
            return $"error[missing_material]: no seeds matching '{seedQuery}' in my inventory" +
                (carried.Count > 0
                    ? $" — I am carrying: {string.Join(", ", carried)}"
                    : " — I have no seeds at all; 高草/黑麦 drop seeds when harvested, " +
                      "or take some from a chest");
        }

        _seedName = matched ?? seedQuery;
        if (_candidates.Count == 0)
        {
            return "error[not_found]: no bare farmland near " +
                $"({origin.X},{origin.Y},{origin.Z}) — till_soil there first " +
                "(seeds only grow on tilled soil, block 168)";
        }

        if (_index >= _candidates.Count || _planted >= Math.Max(1, count))
        {
            return Summary() + Advice(brain);
        }

        var cell = _candidates[_index];
        var center = new Vector3(cell.X + 0.5f, cell.Y + 1.5f, cell.Z + 0.5f);
        var distance = Vector3.Distance(brain.Creature.ComponentBody.Position, center);
        if (distance > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                _skipped.Add($"({cell.X},{cell.Y},{cell.Z}) 走不到");
                _index++;
                return null;
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, center, 2.5f);
            }

            return null;
        }

        _strokeElapsed += dt;
        if (_strokeElapsed < StrokeSeconds)
        {
            return null;
        }

        _strokeElapsed = 0f;
        var seedValue = inventory.GetSlotValue(seedSlot);
        var plantValue = GeniusFarming.SeedPlacementValue(brain, seedValue, cell);
        if (plantValue == 0)
        {
            return $"error[invalid_target]: '{_seedName}' cannot be sown on farmland";
        }

        brain.SubsystemTerrain.ChangeCell(cell.X, cell.Y + 1, cell.Z, plantValue);
        inventory.RemoveSlotItems(seedSlot, 1);
        _planted++;
        _index++;
        return null;
    }

    private string Summary() =>
        $"planted {_planted} × {_seedName}" +
        (_skipped.Count > 0 ? $"; skipped {_skipped.Count}" : "");

    private string Advice(ComponentGeniusBrain brain)
    {
        if (_planted == 0)
        {
            return "";
        }

        return ". Soil: " + GeniusFarming.DescribeSoil(brain, _candidates[0]) +
            ". Crops grow on their own — no watering action exists; water within " +
            "3 blocks (a dug channel) halves the growing time, and saltpeter " +
            "(fertilize) speeds it up further";
    }
}

/// <summary>
/// Spreads saltpeter over farmland: SubsystemFertilizerBlockBehavior sets
/// nitrogen=3 across the 3x3 centred on the cell, and one harvest spends one
/// nitrogen. Nitrogen cuts a growth stage off the timer and reduces the chance
/// a crop reverts to its wild form.
/// </summary>
public sealed class FertilizeOrder(Point3 target) : GeniusOrder
{
    private const float ReachDistance = 4.5f;

    protected override float TimeoutSeconds => 120f;

    protected override void OnStart(ComponentGeniusBrain brain) =>
        WalkTowards(brain, new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f), 2.5f);

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: no inventory";
        }

        if (GeniusFarming.FindFertilizerSlot(inventory) is not { } slot)
        {
            return "error[missing_material]: I have no 硝石 (saltpeter) — that is the " +
                "fertilizer in this game. It generates at y50-90 in sandstone; " +
                "mine_resource 硝石 will fetch some";
        }

        var center = new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);
        if (Vector3.Distance(brain.Creature.ComponentBody.Position, center) > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                return "error[no_path]: cannot reach that spot";
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, center, 2.5f);
            }

            return null;
        }

        var terrain = brain.SubsystemTerrain.Terrain;
        var fertilized = 0;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                var value = terrain.GetCellValue(target.X + dx, target.Y, target.Z + dz);
                if (Terrain.ExtractContents(value) != GeniusFarming.SoilContents)
                {
                    continue;
                }

                brain.SubsystemTerrain.ChangeCell(
                    target.X + dx, target.Y, target.Z + dz,
                    Terrain.ReplaceData(value, SoilBlock.SetNitrogen(Terrain.ExtractData(value), 3)));
                fertilized++;
            }
        }

        if (fertilized == 0)
        {
            return $"error[invalid_target]: no farmland in the 3x3 around " +
                $"({target.X},{target.Y},{target.Z}) — till_soil first";
        }

        inventory.RemoveSlotItems(slot, 1);
        return $"fertilized {fertilized} farmland cells (nitrogen 3) around " +
            $"({target.X},{target.Y},{target.Z}); one harvest spends one nitrogen";
    }
}
