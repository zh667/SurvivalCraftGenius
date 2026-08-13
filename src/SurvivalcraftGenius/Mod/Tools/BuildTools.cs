using Engine;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
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

    /// <summary>
    /// Builds a named design from the prefab folder. Where build_shelter
    /// generates geometry — and therefore re-decides how the house looks every
    /// time — this places a design somebody already made good once.
    /// </summary>
    public static Task<string> BuildPrefab(GeniusToolContext context, JObject arguments)
    {
        var library = context.Player.Prefabs;
        var name = (string?)arguments["name"] ?? "";
        var available = library.Names();
        if (available.Count == 0)
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.Unavailable,
                "图纸文件夹是空的——用 build_shelter 现搭,或者往图纸文件夹里放一份 x,y,z,方块值 的设计"));
        }

        if (library.Load(name) is not { } prefab)
        {
            var suggestions = NameSuggest.Clause(name, available);
            return Task.FromResult(GeniusFailure.Format(FailureType.NotFound,
                $"没有叫 '{name}' 的图纸{suggestions};现有的图纸:{string.Join("、", available)}"));
        }

        if (!GeniusToolContext.HasPoint(arguments))
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                $"'{prefab.Name}' 占地 {prefab.Describe()},给我一个左下角坐标 x/y/z——" +
                "先 find_build_site 拿一个平整、有支撑的位置"));
        }

        return context.Dispatch(new BuildPrefabOrder(prefab, GeniusToolContext.ReadPoint(arguments)));
    }

    /// <summary>The designs on hand, with their footprints and material bills.</summary>
    public static Task<string> ListPrefabs(GeniusToolContext context, JObject arguments)
    {
        var library = context.Player.Prefabs;
        var names = library.Names();
        if (names.Count == 0)
        {
            return Task.FromResult("图纸文件夹是空的——盖房子用 build_shelter");
        }

        var lines = names.Select(name =>
        {
            var prefab = library.Load(name);
            return prefab is null ? $"{name}(读不出来)" : $"{name}({prefab.Describe()})";
        });
        return Task.FromResult("现有图纸:" + string.Join("、", lines) +
            "。用 build_prefab 指定名字和左下角坐标来盖");
    }
}
