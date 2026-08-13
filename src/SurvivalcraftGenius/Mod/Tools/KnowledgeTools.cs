using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>
/// Recipe lookup, the game's own help text, and the player-curated knowledge
/// folder. All three answer without the companion being summoned.
/// </summary>
public static class KnowledgeTools
{
    public static Task<string> QueryRecipes(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(GeniusKnowledge.QueryRecipes(
            context.SubsystemTerrain, (string?)arguments["item_name"] ?? ""));

    public static Task<string> QueryHelp(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(GeniusKnowledge.QueryHelp((string?)arguments["keyword"] ?? ""));

    public static Task<string> ReadKnowledge(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(context.Knowledge.Read((string?)arguments["topic"]));
}
