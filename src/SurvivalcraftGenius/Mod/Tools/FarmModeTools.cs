using Engine;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>
/// The standing farm order. One call sets it up; after that the body keeps the
/// plot with no further LLM involvement, which is where the real token saving
/// lives — a maintenance round costs four-plus steps otherwise, every time.
/// </summary>
public static class FarmModeTools
{
    public static Task<string> TendFarm(GeniusToolContext context, JObject arguments)
    {
        var brain = context.Brain;
        var keeper = brain.Farm;

        if ((bool?)arguments["stop"] == true)
        {
            if (!keeper.Enabled)
            {
                return Task.FromResult("看田模式本来就没开");
            }

            var summary = keeper.Describe();
            keeper.Stop();
            return Task.FromResult("已停止看田。" + summary);
        }

        Point3 centre;
        if (GeniusToolContext.HasPoint(arguments))
        {
            centre = GeniusToolContext.ReadPoint(arguments);
        }
        else if (keeper.Enabled)
        {
            centre = keeper.Centre;
        }
        else
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                "给我田的中心坐标 x/y/z——不知道盯哪块地就没法看田"));
        }

        keeper.Start(
            centre,
            (int?)arguments["radius"] ?? GeniusFarmMode.DefaultRadius,
            (string?)arguments["seed_name"] ?? "");
        return Task.FromResult(
            $"开始看田:{keeper.Describe()}。从现在起我会自己捡掉落、收熟的、补种空地," +
            "不用你再一步步指挥;你随时可以给我别的活,那会优先,干完自动回来看田");
    }
}
