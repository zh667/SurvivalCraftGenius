using Game;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Moving items between the ground, chests, the player and my hands.</summary>
public static class ItemTools
{
    public static Task<string> CollectItems(GeniusToolContext context, JObject arguments)
    {
        var order = new CollectItemsOrder();
        return context.Dispatch(order);
    }

    public static Task<string> TakeFromChest(GeniusToolContext context, JObject arguments)
    {
        var order = new TakeFromChestOrder(
            GeniusToolContext.ReadPoint(arguments),
            (string?)arguments["item_name"],
            (int?)arguments["max_count"] ?? int.MaxValue);
        return context.Dispatch(order);
    }

    public static Task<string> PutIntoChest(GeniusToolContext context, JObject arguments)
    {
        var order = new PutIntoChestOrder(
            GeniusToolContext.ReadPoint(arguments), (string?)arguments["item_name"]);
        return context.Dispatch(order);
    }

    public static Task<string> GiveToPlayer(GeniusToolContext context, JObject arguments)
    {
        var order = new GiveToPlayerOrder(
            context.ComponentPlayer.ComponentBody,
            (string?)arguments["item_name"],
            (int?)arguments["max_count"] ?? int.MaxValue);
        return context.Dispatch(order);
    }

    public static Task<string> EquipTool(GeniusToolContext context, JObject arguments)
    {
        var brain = context.Brain;
        var inventory = brain.Miner.Inventory;
        var slotIndex = (int?)arguments["slot_index"] ?? -1;
        if (inventory is null || slotIndex < 0 || slotIndex >= inventory.SlotsCount)
        {
            return Task.FromResult("error[invalid_argument]: invalid slot index");
        }

        inventory.ActiveSlotIndex = slotIndex;
        var value = inventory.GetSlotValue(slotIndex);
        var equippedName = value == 0
            ? "empty hand"
            : BlocksManager.Blocks[Terrain.ExtractContents(value)]
                .GetDisplayName(brain.SubsystemTerrain, value);
        return Task.FromResult($"equipped: {equippedName}");
    }
}
