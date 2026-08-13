using Engine;
using Game;
using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Builds a prefab from the library: walk to the site, check the whole bill of
/// materials, then lay the cells bottom-up.
///
/// <para>The all-or-nothing material check is the part worth keeping from
/// 铁器风云's approach and from Numen's build tool alike: a build that stops
/// half-way leaves the player a ruin, and our own playtests produced a yard
/// full of those. Either the whole house goes up or nothing is placed.</para>
/// </summary>
public sealed class BuildPrefabOrder(GeniusPrefab prefab, Point3 origin) : GeniusOrder
{
    private const float PlaceSeconds = 0.2f;
    private const float ReachDistance = 5f;

    private int _index;
    private int _placed;
    private int _alreadySolid;
    private int _skipped;
    private float _elapsed;
    private int _stuckCount;
    private bool _checked;

    protected override float TimeoutSeconds => 900f;

    public override string? Signature => $"prefab:{prefab.Name}:{origin.X},{origin.Y},{origin.Z}";

    private string Summary() =>
        $"{prefab.Name} @ ({origin.X},{origin.Y},{origin.Z}): 放置 {_placed} 格" +
        (_alreadySolid > 0 ? $"、{_alreadySolid} 格原本就有东西" : "") +
        (_skipped > 0 ? $"、{_skipped} 格是玩家的建筑没动" : "");

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        if (prefab.Cells.Count == 0)
        {
            Finish(GeniusFailure.Format(FailureType.NotFound,
                $"图纸 '{prefab.Name}' 是空的——文件里没有一行有效的 x,y,z,方块值"));
        }
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        if (!_checked)
        {
            if (CheckMaterials(brain) is { } shortfall)
            {
                return shortfall;
            }

            _checked = true;
        }

        if (_index >= prefab.Cells.Count)
        {
            return Summary() + " —— 盖好了";
        }

        var cell = prefab.Cells[_index];
        var target = new Point3(origin.X + cell.X, origin.Y + cell.Y, origin.Z + cell.Z);
        var centre = new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);
        switch (GeniusApproach.Step(brain, centre, ReachDistance, ref _stuckCount))
        {
            case GeniusApproach.Result.Walking:
                return null;
            case GeniusApproach.Result.Unreachable:
                _skipped++;
                _index++;
                return null;
        }

        _elapsed += dt;
        if (_elapsed < PlaceSeconds)
        {
            return null;
        }

        _elapsed = 0f;
        return PlaceOne(brain, target, cell.Value);
    }

    private string? PlaceOne(ComponentGeniusBrain brain, Point3 target, int value)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        if (!GeniusTerrainReady.HasCells(terrain, target.X, target.Z))
        {
            return GeniusFailure.Format(FailureType.AreaNotLoaded,
                $"({target.X},{target.Z}) 那一块还没加载完,等几秒再来");
        }

        var existing = Terrain.ExtractContents(terrain.GetCellValue(target.X, target.Y, target.Z));

        // Never build over the player's own work, even when the blueprint says so.
        if (GeniusProtectedBlocks.IsOffLimitsForDigging(existing))
        {
            _skipped++;
            _index++;
            return null;
        }

        if (value == 0)
        {
            // A blueprint hole: doorway, window opening, interior air.
            if (existing != 0)
            {
                brain.SubsystemTerrain.DestroyCell(
                    0, target.X, target.Y, target.Z, 0, noDrop: false, noParticleSystem: true);
                _placed++;
            }

            _index++;
            return null;
        }

        if (existing == Terrain.ExtractContents(value))
        {
            _alreadySolid++;
            _index++;
            return null;
        }

        var inventory = brain.Miner.Inventory;
        if (inventory is null || FindSlot(inventory, value) is not { } slot)
        {
            // The up-front check passed, so running dry here means something
            // else emptied the bag mid-build. Report progress honestly.
            return Summary() + "; " + GeniusFailure.Format(FailureType.MissingMaterial,
                $"盖到一半 {DisplayName(brain, value)} 用完了——补给我再调用一次,已经盖好的部分会保留");
        }

        brain.SubsystemTerrain.DestroyCell(
            0, target.X, target.Y, target.Z,
            inventory.GetSlotValue(slot), noDrop: false, noParticleSystem: true);
        inventory.RemoveSlotItems(slot, 1);
        _placed++;
        _index++;
        return null;
    }

    /// <summary>
    /// The whole bill of materials, before laying anything. Returns a failure
    /// naming every shortfall at once — one round trip instead of one per
    /// missing material.
    /// </summary>
    private string? CheckMaterials(ComponentGeniusBrain brain)
    {
        var inventory = brain.Miner.Inventory;
        var missing = new List<string>();
        foreach (var (value, needed) in prefab.MaterialCost())
        {
            var have = inventory is null ? 0 : CountOf(inventory, value);
            if (have < needed)
            {
                missing.Add($"{DisplayName(brain, value)} 还差 {needed - have} 个(要 {needed},有 {have})");
            }
        }

        return missing.Count == 0
            ? null
            : GeniusFailure.Format(FailureType.MissingMaterial,
                $"盖 {prefab.Name}({prefab.Describe()})材料不够,一格都还没放:" +
                string.Join("、", missing) +
                "。凑齐再调用一次——半截房子比没有房子更糟,所以我不会先动工");
    }

    private static int? FindSlot(IInventory inventory, int value)
    {
        var wanted = Terrain.ExtractContents(value);
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            if (inventory.GetSlotCount(slot) > 0
                && Terrain.ExtractContents(inventory.GetSlotValue(slot)) == wanted)
            {
                return slot;
            }
        }

        return null;
    }

    private static int CountOf(IInventory inventory, int value)
    {
        var wanted = Terrain.ExtractContents(value);
        var total = 0;
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            if (Terrain.ExtractContents(inventory.GetSlotValue(slot)) == wanted)
            {
                total += inventory.GetSlotCount(slot);
            }
        }

        return total;
    }

    private static string DisplayName(ComponentGeniusBrain brain, int value) =>
        BlocksManager.Blocks[Terrain.ExtractContents(value)]
            .GetDisplayName(brain.SubsystemTerrain, value);
}
