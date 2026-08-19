using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Changing the world: digging, placing, mining, crafting, smelting.</summary>
public static class WorkTools
{
    public static Task<string> DigBlock(GeniusToolContext context, JObject arguments)
    {
        var order = new DigOrder(GeniusToolContext.ReadPoint(arguments));
        return context.Dispatch(order);
    }

    public static Task<string> PlaceBlock(GeniusToolContext context, JObject arguments)
    {
        var slotIndex = (int?)arguments["slot_index"] ?? -1;
        var order = new PlaceOrder(GeniusToolContext.ReadPoint(arguments), slotIndex);
        return context.Dispatch(order);
    }

    /// <summary>Runs the die-revive-recover-resume loop, so it lives on the component.</summary>
    public static Task<string> MineResource(GeniusToolContext context, JObject arguments) =>
        context.Player.DispatchResilientMining(
            (string?)arguments["resource_name"] ?? "",
            (int?)arguments["count"] ?? 1);

    public static Task<string> Craft(GeniusToolContext context, JObject arguments)
    {
        var order = new CraftOrder(
            (string?)arguments["item_name"] ?? "",
            (int?)arguments["count"] ?? 1);
        return context.Dispatch(order);
    }

    public static Task<string> Smelt(GeniusToolContext context, JObject arguments)
    {
        var order = new SmeltOrder(
            (string?)arguments["item_name"] ?? "",
            (int?)arguments["count"] ?? 1);
        return context.Dispatch(order);
    }
}
