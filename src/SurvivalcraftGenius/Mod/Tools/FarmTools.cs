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
        return context.Dispatch(order);
    }

    public static Task<string> PlantSeed(GeniusToolContext context, JObject arguments)
    {
        var order = new PlantSeedOrder(
            GeniusToolContext.ReadPoint(arguments),
            (string?)arguments["seed_name"] ?? "",
            (int?)arguments["count"] ?? 1);
        return context.Dispatch(order);
    }

    public static Task<string> Fertilize(GeniusToolContext context, JObject arguments)
    {
        var order = new FertilizeOrder(GeniusToolContext.ReadPoint(arguments));
        return context.Dispatch(order);
    }

    public static Task<string> UseBucket(GeniusToolContext context, JObject arguments)
    {
        var order = new UseBucketOrder(GeniusToolContext.ReadPoint(arguments));
        return context.Dispatch(order);
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
        return context.Dispatch(order);
    }
}
