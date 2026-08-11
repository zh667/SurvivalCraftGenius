using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Builds a complete, enclosed shelter in one order.
///
/// Playtest 10 produced something the player would not call a house
/// ("盖的还没第一次好，而且还是浮空的"). The companion explained why itself:
///
///   "2. 我接着旧施工坐标补了几块木板,却没先重新确认房子的完整轮廓、地面、门和
///    内部空间,所以只做成了零碎结构,确实不能算房。"
///
/// The log shows the mechanism: thirty-odd separate place_block calls, one LLM
/// round-trip each, resuming from coordinates it remembered from an earlier
/// site. A house is not thirty independent decisions — it is one plan. So the
/// plan is computed here, up front, from a surveyed footprint, and then laid
/// down without further model involvement:
///
///   floor (gaps filled, so it can never float) → four walls → doorway →
///   roof → a torch inside if one is carried.
/// </summary>
public sealed class BuildShelterOrder(
    Point3? requestedOrigin, int width, int length, int wallHeight, string? materialQuery)
    : GeniusOrder
{
    private const float ReachDistance = 4.5f;
    private const float PlaceSeconds = 0.25f;

    private readonly List<(Point3 Cell, CellRole Role)> _plan = [];
    private readonly List<string> _problems = [];
    private int _index;
    private int _placed;
    private int _cleared;
    private float _elapsed;
    private Point3 _origin;
    private int _groundY;
    private bool _planned;
    private int _stuckCount;
    private GeniusLeveling.Runner? _leveller;

    private enum CellRole
    {
        /// <summary>Must end up solid: floor, wall or roof.</summary>
        Solid,

        /// <summary>Must end up empty: interior air and the doorway.</summary>
        Air,
    }

    private int Width => Math.Clamp(width, 3, 9);

    private int Length => Math.Clamp(length, 3, 9);

    private int Height => Math.Clamp(wallHeight, 2, 4);

    protected override float TimeoutSeconds => 900f;

    protected override string TimeoutResult() =>
        Summary() + "; error[timeout]: ran out of time — call build_shelter again with the " +
        "same arguments to finish it";

    protected override void OnStart(ComponentGeniusBrain brain)
    {
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        if (!_planned)
        {
            if (Plan(brain) is { } failure)
            {
                return failure;
            }

            _planned = true;
            return null;
        }

        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: no inventory";
        }

        // Flatten first. A player faced with a lumpy patch digs the humps off
        // and fills the dips before laying a floor; the survey used to just
        // refuse the ground instead (playtest 12: "没有合适的地形你可以自改造嘛").
        if (_leveller is { Done: false } leveller)
        {
            leveller.Step(brain, dt);
            if (leveller.Failure is { } levelFailure)
            {
                return levelFailure;
            }

            return null;
        }

        if (_index >= _plan.Count)
        {
            return Summary() + Advice(brain);
        }

        var (cell, role) = _plan[_index];
        var center = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        switch (ApproachCell(brain, center, ReachDistance, ref _stuckCount))
        {
            case GeniusApproach.Result.Walking:
                return null;
            case GeniusApproach.Result.Unreachable:
                _problems.Add($"({cell.X},{cell.Y},{cell.Z}) 走不到");
                _index++;
                return null;
        }

        _elapsed += dt;
        if (_elapsed < PlaceSeconds)
        {
            return null;
        }

        _elapsed = 0f;
        var terrain = brain.SubsystemTerrain.Terrain;
        var existing = Terrain.ExtractContents(terrain.GetCellValue(cell.X, cell.Y, cell.Z));

        if (role == CellRole.Air)
        {
            if (existing != 0 && !GeniusProtectedBlocks.IsPlayerBuilt(existing))
            {
                brain.SubsystemTerrain.DestroyCell(
                    0, cell.X, cell.Y, cell.Z, 0, noDrop: false, noParticleSystem: true);
                _cleared++;
            }

            _index++;
            return null;
        }

        if (existing != 0 && BlocksManager.Blocks[existing].IsCollidable)
        {
            // Natural ground already does the job — leave it and save a block.
            _index++;
            return null;
        }

        if (GeniusBuildMaterials.FindSlot(brain, inventory, materialQuery) is not { } slot)
        {
            return Summary() +
                "; error[missing_material]: I ran out of building blocks. " +
                "Bring me cobblestone or planks (mine_resource 石头 then craft, or " +
                "take_from_chest) and call build_shelter again — what is already up stays up";
        }

        brain.SubsystemTerrain.DestroyCell(
            0, cell.X, cell.Y, cell.Z,
            inventory.GetSlotValue(slot),
            noDrop: false,
            noParticleSystem: true);
        inventory.RemoveSlotItems(slot, 1);
        _placed++;
        _index++;
        return null;
    }

    /// <summary>
    /// Works out the whole structure before laying a single block. Returns a
    /// failure string when there is nowhere sane to put it.
    /// </summary>
    private string? Plan(ComponentGeniusBrain brain)
    {
        GeniusSiteSurvey.Site? site;
        if (requestedOrigin is { } wanted)
        {
            site = GeniusSiteSurvey.Evaluate(brain, wanted.X, wanted.Z, Width, Length, forFarm: false);
            if (site is null)
            {
                // Do NOT quietly build it anyway. The last house went up on a
                // spot exactly like this and came out floating and broken.
                var nearby = GeniusSiteSurvey.FindBest(brain, Width, Length, 12, forFarm: false);
                return "error[invalid_target]: " +
                    $"({wanted.X},{wanted.Y},{wanted.Z}) will not hold a {Width}x{Length} " +
                    "building — the ground there is broken up, hollow, or under something. " +
                    (nearby is { } alternative
                        ? $"The nearest spot that will is ({alternative.Origin.X}," +
                          $"{alternative.Origin.Y},{alternative.Origin.Z}) ({alternative.Note}) — " +
                          "call build_shelter there, or use find_build_site to see the options"
                        : "Nothing within 12m works either; move somewhere flatter and retry");
            }
        }
        else
        {
            site = GeniusSiteSurvey.FindBest(brain, Width, Length, 16, forFarm: false);
            if (site is null)
            {
                return "error[not_found]: no flat, supported patch big enough for a " +
                    $"{Width}x{Length} building within 16m — move me somewhere flatter";
            }
        }

        _origin = site.Value.Origin;
        _groundY = site.Value.GroundY;
        if (GeniusLeveling.Columns(brain, _origin.X, _origin.Z, Width, Length) is { } columns)
        {
            var ops = GeniusGroundLevel.Plan(columns, _groundY).ToList();
            if (ops.Count > 0)
            {
                _leveller = new GeniusLeveling.Runner(ops, forFarm: false);
            }
        }

        foreach (var cell in GeniusShelterPlan.Cells(
            _origin.X, _groundY, _origin.Z, Width, Length, Height))
        {
            _plan.Add((
                new Point3(cell.X, cell.Y, cell.Z),
                cell.Solid ? CellRole.Solid : CellRole.Air));
        }

        return null;
    }

    /// <summary>
    /// What actually happened, never what was planned.
    ///
    /// This used to open with "built a WxL shelter: floor filled in, four walls,
    /// a doorway and a roof" unconditionally and only then append the counts —
    /// so a run that placed ZERO blocks still announced a finished house, and
    /// the model relayed that to the player in good faith. Playtest 12:
    /// "我让你用木板盖!你看看你怎么盖的!" The tool lied first.
    /// </summary>
    private string Summary()
    {
        var where = $"({_origin.X},{_groundY},{_origin.Z})";
        var levelled = _leveller?.Summary() is { Length: > 0 } l ? l + ";" : "";
        var needed = _plan.Count(entry => entry.Role == CellRole.Solid);
        var trouble = _problems.Count == 0
            ? ""
            : $";{_problems.Count} 格没做成: " + string.Join(", ", _problems.Take(5));

        if (_placed == 0)
        {
            return levelled + $"房子没有盖起来:{where} 一块都没放上去(计划需要 {needed} 块){trouble}";
        }

        if (_problems.Count > 0 || _placed < needed)
        {
            return levelled + $"房子只盖了一部分:{where} 放了 {_placed}/{needed} 块" +
                (_cleared > 0 ? $",掏空 {_cleared} 格" : "") +
                ",墙和屋顶都还不完整,不能算房子" + trouble;
        }

        return levelled + $"盖好了 {Width}x{Length} 的小屋,{Height} 格高墙,在 {where}:" +
            $"地基填满、四面墙、-Z 面一个两格门洞、屋顶齐全,共放了 {_placed} 块" +
            (_cleared > 0 ? $",掏空 {_cleared} 格" : "");
    }

    private string Advice(ComponentGeniusBrain brain)
    {
        var interior = new Point3(_origin.X + Width / 2, _groundY + 1, _origin.Z + Length / 2);
        var light = Terrain.ExtractLight(
            brain.SubsystemTerrain.Terrain.GetCellValue(interior.X, interior.Y, interior.Z));
        return light < 9
            ? $". Inside is dark (light {light}) — place a torch at " +
              $"({interior.X},{interior.Y},{interior.Z}) so mobs cannot spawn in it"
            : "";
    }
}

/// <summary>
/// Chooses what to build with. Restricted to plain bulk materials on purpose:
/// spending the player's ore, tools or furniture on a wall would be worse than
/// running out of blocks.
/// </summary>
public static class GeniusBuildMaterials
{
    /// <summary>
    /// Things worth more standing in the bag than sitting in a wall: ore, seeds,
    /// and the functional blocks a base needs. Everything else — planks,
    /// cobblestone, bricks, stairs, glass, fences — IS the building material.
    ///
    /// This deliberately does NOT reuse <see cref="GeniusProtectedBlocks"/>.
    /// That list answers "may I dig this cell out of the world", and planks are
    /// on it because a plank wall is usually the player's house. Reusing it here
    /// answered a different question — "may I spend this slot" — and the answer
    /// came back no for the most obvious building material in the game.
    /// Playtest 12: 82 planks in the bag, and every attempt reported
    /// "I ran out of building blocks. Bring me cobblestone or planks".
    /// </summary>
    public static bool IsTooValuableForAWall(Block block, string displayName) =>
        block is SeedsBlock
            or ChestBlock or FurnaceBlock or CraftingTableBlock
            or TorchBlock or DoorBlock or LadderBlock
        || displayName.Contains('矿');

    public static int? FindSlot(
        ComponentGeniusBrain brain, IInventory inventory, string? preferredName)
    {
        var best = -1;
        var bestCount = 0;
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var count = inventory.GetSlotCount(slot);
            if (count <= 0)
            {
                continue;
            }

            var value = inventory.GetSlotValue(slot);
            var contents = Terrain.ExtractContents(value);
            var block = BlocksManager.Blocks[contents];
            if (!block.IsPlaceable || !block.IsCollidable)
            {
                continue;
            }

            var name = block.GetDisplayName(brain.SubsystemTerrain, value);
            if (IsTooValuableForAWall(block, name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(preferredName)
                && name.Contains(preferredName, StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }

            if (count > bestCount)
            {
                best = slot;
                bestCount = count;
            }
        }

        return best >= 0 ? best : null;
    }
}
