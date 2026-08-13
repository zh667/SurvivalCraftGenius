using Engine;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Putting up a shelter.</summary>
public static class BuildTools
{
    public static Task<string> BuildShelter(GeniusToolContext context, JObject arguments)
    {
        Point3? origin = arguments["x"] is not null && arguments["z"] is not null
            ? GeniusToolContext.ReadPoint(arguments)
            : null;
        var order = new BuildShelterOrder(
            origin,
            (int?)arguments["width"] ?? 5,
            (int?)arguments["length"] ?? 5,
            (int?)arguments["wall_height"] ?? 3,
            (string?)arguments["material"]);
        if (context.Brain.IsRunning(order.Signature!))
        {
            return Task.FromResult(
                "已经在盖同一栋了,还在施工中——**别再下同样的指令**,那会把正在跑的任务顶掉、" +
                "从头再来(实测因此连废三栋)。等它自己返回结果就行;要换地方或换尺寸再重新调用");
        }

        context.Brain.StartOrder(order);
        return order.Completion;
    }
}
