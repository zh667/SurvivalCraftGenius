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

        if (_index >= _plan.Count)
        {
            return Summary() + Advice(brain);
        }

        var (cell, role) = _plan[_index];
        var center = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        if (Vector3.Distance(brain.Creature.ComponentBody.Position, center) > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                _problems.Add($"({cell.X},{cell.Y},{cell.Z}) 走不到");
                _index++;
                return null;
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, center, 2.5f);
            }

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
        foreach (var cell in GeniusShelterPlan.Cells(
            _origin.X, _groundY, _origin.Z, Width, Length, Height))
        {
            _plan.Add((
                new Point3(cell.X, cell.Y, cell.Z),
                cell.Solid ? CellRole.Solid : CellRole.Air));
        }

        return null;
    }

    private string Summary()
    {
        var text = $"built a {Width}x{Length} shelter with {Height}-high walls at " +
            $"({_origin.X},{_groundY},{_origin.Z}): floor filled in, four walls, " +
            $"a doorway on the -Z side, and a roof. Placed {_placed} blocks" +
            (_cleared > 0 ? $", cleared {_cleared} to hollow out the inside" : "");
        return _problems.Count == 0
            ? text
            : text + $"; {_problems.Count} spots gave trouble: " +
                string.Join(", ", _problems.Take(5));
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
            if (!block.IsPlaceable || !block.IsCollidable || GeniusProtectedBlocks.IsPlayerBuilt(contents))
            {
                continue;
            }

            // Ore and anything with a crafting use is worth more than a wall.
            var name = block.GetDisplayName(brain.SubsystemTerrain, value);
            if (name.Contains('矿') || block is SeedsBlock)
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
