using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Walks to nearby dropped items and puts them into the NPC's inventory.
/// Finishes when nothing collectible remains in range or on timeout.
/// </summary>
public sealed class CollectItemsOrder : GeniusOrder
{
    private const float SearchRange = 14f;
    private const float PickupRange = 2.5f;

    private readonly Dictionary<string, int> _collected = [];
    private double _nextPathUpdateTime;

    protected override float TimeoutSeconds => 90f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: I have no inventory";
        }

        var myPosition = brain.Creature.ComponentBody.Position;
        Pickable? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var pickable in brain.SubsystemPickables.Pickables)
        {
            if (pickable.ToRemove)
            {
                continue;
            }

            var distance = Vector3.Distance(pickable.Position, myPosition);
            if (distance < nearestDistance && distance <= SearchRange)
            {
                nearest = pickable;
                nearestDistance = distance;
            }
        }

        if (nearest is null)
        {
            brain.m_componentPathfinding.Stop();
            var ambient = brain.DrainRecentPickups();
            return ambient.Length > 0
                ? Summarize() + $"; note: these were auto-picked into my inventory just before: {ambient}"
                : Summarize();
        }

        if (nearestDistance <= PickupRange)
        {
            var leftover = ComponentInventoryBase.AcquireItems(inventory, nearest.Value, nearest.Count);
            var taken = nearest.Count - leftover;
            if (taken > 0)
            {
                var name = BlocksManager.Blocks[Terrain.ExtractContents(nearest.Value)]
                    .GetDisplayName(brain.SubsystemTerrain, nearest.Value);
                _collected[name] = _collected.TryGetValue(name, out var count) ? count + taken : taken;
            }

            if (leftover == 0)
            {
                nearest.ToRemove = true;
            }
            else
            {
                nearest.Count = leftover;
                brain.m_componentPathfinding.Stop();
                return Summarize() + "; my inventory is full";
            }

            return null;
        }

        if (brain.m_componentPathfinding.IsStuck)
        {
            return Summarize() + $"; error[no_path]: cannot reach the remaining items at " +
                $"({(int)nearest.Position.X}, {(int)nearest.Position.Y}, {(int)nearest.Position.Z})";
        }

        if (brain.m_subsystemTime.GameTime >= _nextPathUpdateTime)
        {
            _nextPathUpdateTime = brain.m_subsystemTime.GameTime + 0.5;
            WalkTowards(brain, nearest.Position, 1f);
        }

        return null;
    }

    private string Summarize()
    {
        if (_collected.Count == 0)
        {
            return "no dropped items nearby to collect";
        }

        var parts = _collected.Select(pair => $"{pair.Key} x{pair.Value}");
        return "collected: " + string.Join(", ", parts);
    }
}

/// <summary>Shared logic for chest interactions: approach, resolve, transfer.</summary>
public abstract class ChestOrderBase(Point3 chest) : GeniusOrder
{
    private const float ReachDistance = 4.5f;


    private Vector3 ChestCenter => new(chest.X + 0.5f, chest.Y + 0.5f, chest.Z + 0.5f);

    protected override float TimeoutSeconds => 90f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        WalkTowards(brain, ChestCenter, 2.5f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var distance = Vector3.Distance(brain.Creature.ComponentBody.Position, ChestCenter);
        if (distance > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                return "error[no_path]: cannot get close enough to the chest — path is blocked";
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, ChestCenter, 2.5f);
            }

            return null;
        }

        brain.m_componentPathfinding.Stop();
        var blockEntity = brain.SubsystemBlockEntities.GetBlockEntity(chest.X, chest.Y, chest.Z);
        var chestInventory = blockEntity?.Entity.FindComponent<ComponentChest>();
        if (chestInventory is null)
        {
            return $"error[not_found]: no chest at ({chest.X}, {chest.Y}, {chest.Z})";
        }

        return Transfer(brain, chestInventory);
    }

    protected abstract string Transfer(ComponentGeniusBrain brain, ComponentChest chestInventory);

}

/// <summary>Inventory transfer helpers shared by chest, delivery and craft orders.</summary>
public static class GeniusInventoryOps
{
    public static string DescribeMoved(
        Dictionary<string, int> moved,
        string verb,
        string emptyMessage)
    {
        if (moved.Count == 0)
        {
            return emptyMessage;
        }

        var parts = moved.Select(pair => $"{pair.Key} x{pair.Value}");
        return $"{verb}: {string.Join(", ", parts)}";
    }

    public static string ItemName(ComponentGeniusBrain brain, int value)
    {
        return BlocksManager.Blocks[Terrain.ExtractContents(value)]
            .GetDisplayName(brain.SubsystemTerrain, value);
    }

    /// <summary>Moves up to maxCount matching items between two inventories.</summary>
    public static Dictionary<string, int> MoveItems(
        ComponentGeniusBrain brain,
        IInventory source,
        IInventory target,
        string? nameFilter,
        int maxCount)
    {
        var moved = new Dictionary<string, int>();
        var remainingBudget = maxCount;
        for (var slot = 0; slot < source.SlotsCount && remainingBudget > 0; slot++)
        {
            var value = source.GetSlotValue(slot);
            var count = source.GetSlotCount(slot);
            if (value == 0 || count <= 0)
            {
                continue;
            }

            var name = ItemName(brain, value);
            if (!string.IsNullOrEmpty(nameFilter)
                && !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var wanted = Math.Min(count, remainingBudget);
            var leftover = ComponentInventoryBase.AcquireItems(target, value, wanted);
            var taken = wanted - leftover;
            if (taken <= 0)
            {
                break;
            }

            source.RemoveSlotItems(slot, taken);
            moved[name] = moved.TryGetValue(name, out var existing) ? existing + taken : taken;
            remainingBudget -= taken;
        }

        return moved;
    }
}

public sealed class TakeFromChestOrder(Point3 chest, string? nameFilter, int maxCount)
    : ChestOrderBase(chest)
{
    protected override string Transfer(ComponentGeniusBrain brain, ComponentChest chestInventory)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: I have no inventory";
        }

        var moved = GeniusInventoryOps.MoveItems(brain, chestInventory, inventory, nameFilter, maxCount);
        if (moved.Count == 0 && !string.IsNullOrEmpty(nameFilter))
        {
            // Teach instead of dead-ending: show what IS in the chest, and
            // catch near-miss names.
            var contents = new Dictionary<string, int>();
            for (var slot = 0; slot < chestInventory.SlotsCount; slot++)
            {
                var value = chestInventory.GetSlotValue(slot);
                var count = chestInventory.GetSlotCount(slot);
                if (value != 0 && count > 0)
                {
                    var name = GeniusInventoryOps.ItemName(brain, value);
                    contents[name] = contents.TryGetValue(name, out var existing) ? existing + count : count;
                }
            }

            if (contents.Count == 0)
            {
                return "the chest is empty";
            }

            var listing = string.Join(", ", contents.Select(pair => $"{pair.Key} x{pair.Value}"));
            var suggestions = Agent.NameSuggest.Clause(nameFilter, contents.Keys);
            return $"nothing matching '{nameFilter}' in the chest{suggestions}; it holds: {listing}";
        }

        return GeniusInventoryOps.DescribeMoved(
            moved,
            "took from chest",
            "the chest is empty (or my inventory is full)");
    }
}

public sealed class PutIntoChestOrder(Point3 chest, string? nameFilter)
    : ChestOrderBase(chest)
{
    protected override string Transfer(ComponentGeniusBrain brain, ComponentChest chestInventory)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error[internal]: I have no inventory";
        }

        var moved = GeniusInventoryOps.MoveItems(brain, inventory, chestInventory, nameFilter, int.MaxValue);
        return GeniusInventoryOps.DescribeMoved(
            moved,
            "stored into chest",
            "I had nothing (matching) to store, or the chest is full");
    }
}
