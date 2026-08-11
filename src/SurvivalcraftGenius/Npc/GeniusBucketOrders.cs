using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Filling and emptying a bucket — the verb the companion did not have.
///
/// It worked this out for itself in playtest 11, and was exactly right:
///
///   "我确认了正确方法:空桶要握着「轻击水源块」,但我现有工具接口只有挖掘和放置,
///    无法执行轻击交互,所以没法替你装水。"
///
/// The engine puts both halves in SubsystemBucketBlockBehavior.OnUse:
///
///  - <b>Fill</b> (block 90, empty bucket): raycast must hit a WaterBlock whose
///    <c>FluidBlock.GetLevel(data) == 0</c> — a SOURCE, not flowing water — then
///    the slot becomes block 91 and the source cell is destroyed.
///  - <b>Pour</b> (block 91, water bucket): <c>Place(raycast, MakeBlockValue(18))</c>
///    and the slot goes back to 90.
///
/// Neither is reachable from an NPC: OnUse needs a Ray3 aimed by a real player
/// camera, and <c>ComponentMiner.Place</c> throws without a ComponentPlayer. So
/// this order performs the same state change directly, the way TillSoilOrder
/// does for the rake.
/// </summary>
public sealed class UseBucketOrder(Point3 target) : GeniusOrder
{
    private const float ReachDistance = 4.5f;

    public const int EmptyBucketContents = 90;
    public const int WaterBucketContents = 91;
    public const int MagmaBucketContents = 93;

    protected override float TimeoutSeconds => 120f;

    protected override string TimeoutResult() =>
        "error[timeout]: could not get to that cell in time";

    protected override void OnStart(ComponentGeniusBrain brain)
    {
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: no inventory";
        }

        var center = new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);
        if (Vector3.Distance(brain.Creature.ComponentBody.Position, center) > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                return $"error[no_path]: cannot get close enough to ({target.X},{target.Y},{target.Z})";
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, center, 2.5f);
            }

            return null;
        }

        var terrain = brain.SubsystemTerrain.Terrain;
        var value = terrain.GetCellValue(target.X, target.Y, target.Z);
        var contents = Terrain.ExtractContents(value);
        var block = BlocksManager.Blocks[contents];

        // Which way round? Decided by what is in the cell, not by a parameter —
        // there is only ever one sensible answer and the model should not have
        // to name it.
        if (block is WaterBlock)
        {
            return Fill(brain, inventory, value);
        }

        if (contents == 0)
        {
            return Pour(brain, inventory);
        }

        return $"error[invalid_target]: ({target.X},{target.Y},{target.Z}) is {block.GetDisplayName(brain.SubsystemTerrain, value)} — "
            + "point me at a water source to fill the bucket, or at an empty cell to pour it out";
    }

    private string Fill(ComponentGeniusBrain brain, IInventory inventory, int waterValue)
    {
        if (FindSlot(inventory, EmptyBucketContents) is not { } slot)
        {
            return CarryingWater(inventory)
                ? "error[wrong_method]: my bucket is already full — pour it somewhere first"
                : "error[missing_material]: I have no empty bucket (空桶) — craft one from 3 iron "
                    + "ingots or take one from a chest";
        }

        // A flowing edge cannot be scooped: the engine requires level 0.
        if (FluidBlock.GetLevel(Terrain.ExtractData(waterValue)) != 0)
        {
            return $"error[invalid_target]: ({target.X},{target.Y},{target.Z}) is flowing water, not a source — "
                + "scoop the middle of the pool, not its edge";
        }

        var full = Terrain.ReplaceContents(
            inventory.GetSlotValue(slot), WaterBucketContents);
        inventory.RemoveSlotItems(slot, 1);
        inventory.AddSlotItems(slot, full, 1);
        brain.SubsystemTerrain.ChangeCell(target.X, target.Y, target.Z, 0);
        return $"filled the bucket from the water source at ({target.X},{target.Y},{target.Z})";
    }

    private string Pour(ComponentGeniusBrain brain, IInventory inventory)
    {
        if (FindSlot(inventory, WaterBucketContents) is not { } slot)
        {
            return FindSlot(inventory, EmptyBucketContents) is not null
                ? "error[wrong_method]: my bucket is empty — fill it at a water source first "
                    + "(use_bucket on the water itself)"
                : "error[missing_material]: I am not carrying a water bucket (水桶)";
        }

        // Water spreads. Dropping it next to the field washes the crops out and
        // reverts the farmland — the channel has to stand off from the plot.
        if (GeniusFarming.FarmlandNear(brain, target) is { } farmland)
        {
            return $"error[wrong_method]: there is farmland/crops at ({farmland.X},{farmland.Y},{farmland.Z}), "
                + "right next to that cell — poured water spreads and would wash the crops out and "
                + "turn the soil back to dirt. Dig the channel at least 2 cells away from the plot; "
                + "farmland hydrates from water up to 3 cells off, so it does not need to touch";
        }

        var empty = Terrain.ReplaceContents(
            inventory.GetSlotValue(slot), EmptyBucketContents);
        inventory.RemoveSlotItems(slot, 1);
        inventory.AddSlotItems(slot, empty, 1);
        brain.SubsystemTerrain.ChangeCell(
            target.X, target.Y, target.Z, Terrain.MakeBlockValue(GeniusFarming.WaterContents));
        return $"poured the water out at ({target.X},{target.Y},{target.Z}); the bucket is empty again";
    }

    private static bool CarryingWater(IInventory inventory) =>
        FindSlot(inventory, WaterBucketContents) is not null
        || FindSlot(inventory, MagmaBucketContents) is not null;

    private static int? FindSlot(IInventory inventory, int contents)
    {
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            if (inventory.GetSlotCount(slot) > 0
                && Terrain.ExtractContents(inventory.GetSlotValue(slot)) == contents)
            {
                return slot;
            }
        }

        return null;
    }
}
