using Game;
using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// Authoritative game knowledge for the LLM: real recipes from
/// CraftingRecipesManager (mods included), the in-game help topics, and a
/// player-extensible markdown knowledge folder (Numen-style "调教").
/// The model is told to query instead of guessing from Minecraft habits.
/// </summary>
public static class GeniusKnowledge
{
    private static Dictionary<string, string>? _craftingIdNames;

    /// <summary>Real recipes whose result or ingredients match the query.</summary>
    public static string QueryRecipes(SubsystemTerrain subsystemTerrain, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "error: give an item name to look up";
        }

        var names = GetCraftingIdNames(subsystemTerrain);
        var matches = new List<(CraftingRecipe Recipe, string ResultName, bool Exact)>();
        foreach (var recipe in CraftingRecipesManager.Recipes)
        {
            if (recipe.ResultValue == 0)
            {
                continue;
            }

            var resultBlock = BlocksManager.Blocks[Terrain.ExtractContents(recipe.ResultValue)];
            var resultName = resultBlock.GetDisplayName(subsystemTerrain, recipe.ResultValue);
            var resultId = resultBlock.GetCraftingId(recipe.ResultValue) ?? "";
            var exact = string.Equals(resultName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resultId, query, StringComparison.OrdinalIgnoreCase);
            var resultMatch = exact
                || resultName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || resultId.Contains(query, StringComparison.OrdinalIgnoreCase);
            var ingredientMatch = !resultMatch && recipe.Ingredients.Any(ingredient =>
            {
                if (string.IsNullOrEmpty(ingredient))
                {
                    return false;
                }

                CraftingRecipesManager.DecodeIngredient(ingredient, out var craftingId, out _);
                return craftingId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (names.TryGetValue(craftingId, out var display)
                        && display.Contains(query, StringComparison.OrdinalIgnoreCase));
            });
            if (resultMatch || ingredientMatch)
            {
                matches.Add((recipe, resultName, exact));
            }
        }

        if (matches.Count == 0)
        {
            return $"no recipes involve '{query}' — the name may be wrong for this game; " +
                "try another keyword (e.g. 锤 instead of 镐)";
        }

        var ordered = matches
            .OrderByDescending(match => match.Exact)
            .ThenBy(match => match.ResultName.Length)
            .Take(10)
            .Select(match =>
            {
                var needs = new JArray();
                foreach (var pair in Npc.GeniusCrafting.CountNeeds(match.Recipe))
                {
                    needs.Add(new JObject
                    {
                        ["item"] = names.TryGetValue(pair.Key.Id, out var display) ? display : pair.Key.Id,
                        ["count"] = pair.Value,
                    });
                }

                var entry = new JObject
                {
                    ["result"] = match.ResultName,
                    ["result_count"] = match.Recipe.ResultCount,
                    ["ingredients"] = needs,
                };
                if (Npc.GeniusCrafting.NeedsCraftingTable(match.Recipe))
                {
                    entry["needs_crafting_table"] = true;
                }

                if (match.Recipe.RequiredHeatLevel > 0f)
                {
                    entry["furnace_smelting"] = true;
                }

                return entry;
            });

        return new JObject
        {
            ["total_matches"] = matches.Count,
            ["recipes"] = new JArray(ordered),
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Searches the in-game help topics (localized).</summary>
    public static string QueryHelp(string query)
    {
        if (LanguageControl.KeyWords["Help"] is not SimpleJson.JsonObject helpObject)
        {
            return "error: no help content loaded";
        }

        var topics = new List<(string Title, string Text)>();
        foreach (var item in helpObject)
        {
            if (item.Value is not SimpleJson.JsonObject topic)
            {
                continue;
            }

            topic.TryGetValue("Title", out var titleValue);
            topic.TryGetValue("value", out var textValue);
            var title = titleValue as string ?? "";
            var text = (textValue as string ?? "").Replace("\r", " ").Replace("\n", " ");
            if (title.Length > 0 && text.Length > 0)
            {
                topics.Add((title, text));
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "help topics: " + string.Join("; ", topics.Select(topic => topic.Title));
        }

        var hits = topics
            .Where(topic => topic.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || topic.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(topic => $"【{topic.Title}】{Truncate(topic.Text, 700)}")
            .ToList();
        return hits.Count > 0
            ? string.Join("\n", hits)
            : $"no help topic mentions '{query}'; call query_help without a keyword to list all topics";
    }

    private static Dictionary<string, string> GetCraftingIdNames(SubsystemTerrain subsystemTerrain)
    {
        if (_craftingIdNames is not null)
        {
            return _craftingIdNames;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in BlocksManager.Blocks)
        {
            if (block is null || string.IsNullOrEmpty(block.CraftingId))
            {
                continue;
            }

            try
            {
                names.TryAdd(
                    block.CraftingId,
                    block.GetDisplayName(subsystemTerrain, Terrain.MakeBlockValue(block.BlockIndex)));
            }
            catch
            {
            }
        }

        _craftingIdNames = names;
        return names;
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}

/// <summary>
/// Player-extensible knowledge folder: drop .md/.txt guides (online tips,
/// house rules) into data:/SurvivalcraftGenius/knowledge and the companion can
/// read them on demand.
/// </summary>
public sealed class GeniusKnowledgeStore(string directory)
{
    private const string StarterFileName = "游戏技巧-内置.md";

    private const string StarterContent =
        """
        # 生存战争实用技巧(内置,可自行修改/添加更多 .md 文件)

        - 本游戏不是 Minecraft,配方不同:没有木镐/石镐,石制挖掘工具是"石锤";一切配方以 query_recipes 为准。
        - 猎杀动物:直接跑过去会把动物吓跑;鸟类受惊会飞走。接近猎物要绕后慢慢靠近,首选远程或伏击;打鸟基本只能靠潜行接近或远程。
        - 工具科技链:挖花岗岩掉"石块"(stonechunk) → 石块+木棒(需工作台)合成石锤/石斧/石矛 → 石锤挖铁矿 → 熔炉炼铁锭 → 铁质工具。
        - 木头链:砍树得原木 → 原木合成木板 → 木板合成木棒。
        - 铁矿常在较深的地下,煤矿较浅;深处小心岩浆,听到咕噜声先停手。
        - 夜晚地表有狼、狼人等敌对生物;身上带武器,打不过就撤。
        - 食物:动物掉生肉,篝火/熔炉烤熟更顶饿;南瓜地和捕鱼也是稳定食物来源。
        """;

    public string Directory { get; } = directory;

    public void EnsureStarter()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var starter = Path.Combine(Directory, StarterFileName);
            if (!File.Exists(starter))
            {
                File.WriteAllText(starter, StarterContent);
            }
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] knowledge folder init failed: {exception.Message}");
        }
    }

    public string Read(string? topic)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return "error: knowledge folder missing";
            }

            var files = System.IO.Directory.GetFiles(Directory)
                .Where(file => file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName)
                .ToList();
            if (files.Count == 0)
            {
                return "the knowledge folder is empty";
            }

            if (string.IsNullOrWhiteSpace(topic))
            {
                var listing = files.Select(file =>
                {
                    var firstLine = File.ReadLines(file).FirstOrDefault(line => line.Trim().Length > 0) ?? "";
                    return $"{Path.GetFileNameWithoutExtension(file)}: {firstLine.TrimStart('#', ' ')}";
                });
                return "knowledge files: " + string.Join("; ", listing);
            }

            var match = files.FirstOrDefault(file =>
                    Path.GetFileNameWithoutExtension(file)
                        .Contains(topic, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(file =>
                {
                    try
                    {
                        return File.ReadAllText(file).Contains(topic, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
            if (match is null)
            {
                return $"no knowledge file mentions '{topic}'";
            }

            var content = File.ReadAllText(match);
            return content.Length <= 4000 ? content : content[..4000] + "…(truncated)";
        }
        catch (Exception exception)
        {
            return $"error: {exception.Message}";
        }
    }
}
