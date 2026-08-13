using Engine;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Tilling, planting, fertilizing, irrigating, harvesting.</summary>
public static class FarmTools
{
    public static Task<string> TillSoil(GeniusToolContext context, JObject arguments)
    {
        var order = new TillSoilOrder(
            GeniusToolContext.ReadPoint(arguments),
            (int?)arguments["width"] ?? 1,
            (int?)arguments["length"] ?? 1);
        context.Brain.StartOrder(order);
        return order.Completion;
    }

    public static Task<string> PlantSeed(GeniusToolContext context, JObject arguments)
    {
        var order = new PlantSeedOrder(
            GeniusToolContext.ReadPoint(arguments),
            (string?)arguments["seed_name"] ?? "",
            (int?)arguments["count"] ?? 1);
        context.Brain.StartOrder(order);
        return order.Completion;
    }

    public static Task<string> Fertilize(GeniusToolContext context, JObject arguments)
    {
        var order = new FertilizeOrder(GeniusToolContext.ReadPoint(arguments));
        context.Brain.StartOrder(order);
        return order.Completion;
    }

    public static Task<string> UseBucket(GeniusToolContext context, JObject arguments)
    {
        var order = new UseBucketOrder(GeniusToolContext.ReadPoint(arguments));
        context.Brain.StartOrder(order);
        return order.Completion;
    }

    public static Task<string> HarvestCrops(GeniusToolContext context, JObject arguments)
    {
        Point3? center = GeniusToolContext.HasPoint(arguments)
            ? GeniusToolContext.ReadPoint(arguments)
            : null;
        var order = new HarvestCropsOrder(
            center,
            (int?)arguments["radius"] ?? 8,
            (bool?)arguments["include_wild"] ?? false);
        context.Brain.StartOrder(order);
        return order.Completion;
    }
}
