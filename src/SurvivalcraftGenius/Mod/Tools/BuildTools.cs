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
        // v0.11.7 refused every repeat of an identical build here. That was the
        // wrong shape: it also refused the player five minutes later changing
        // their mind about the house. Dispatch now refuses only a second
        // dispatch inside the SAME reply, which is the case that actually
        // restarted three houses from zero.
        return context.Dispatch(order);
    }
}
