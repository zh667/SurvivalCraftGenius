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

    private const string TutorialFileName = "进阶攻略-贴吧教程整理.md";

    private const string TutorialContent =
        """
        # 生存战争进阶攻略(整理自贴吧教程《生存战争2原版新手快速入门方法》)

        说明:这是**按主题查询的参考**,不是执行流程——按当前任务查对应章节即可,不要机械地从"开局"一步步做。

        ## 矿物分布(找矿必读)
        - 铁矿、锗矿、硫磺、钻石都在海平面以下的玄武岩层;钻石只在基岩上 10 格以内。
        - 附近有山的地形,地底玄武岩层更厚(平坦大陆可能只有约 5 层),矿物明显更多。
        - 挖矿策略:先确定地下洞穴方向,在基岩上约 5 格的高度朝洞穴方向水平直线挖——路上有几率碰到玄武岩层矿物,挖到头也能进洞穴,不会白挖。
        - 煤炭:有花岗岩就可能有。最快是去裸露花岗岩的石山表面找,不需要照明,视野好;挖煤掉落多、经验多,前期升级首选。
        - 铜矿(孔雀石):海平面以下的花岗岩层。可从地下水池边缘挖下去找,或去洞穴浅层;注意水里可能有食人鱼,低温下水会感冒。
        - 硝石:只在砂岩层(沙子底下),即沙漠和沙滩;沙漠峡谷的裸露砂岩最好找。地形不合适时比钻石还难找。

        ## 科技等级路线
        - 1 级木石工具 → 2 级铜 → 3 级铁 → 4 级钻石。
        - 强力武器弩和火枪都要铁;弩只需 2 块铁(含弩箭),做出弩/枪后野外基本无敌。
        - 有一身铜护具+铜刀铜镐就可以直接下洞穴最底层。

        ## 工具与合成要点
        - 2 块原木 → 工作台;原木 → 4 木板;木板上下竖放 → 木棍;木棍撸方块比空手快。
        - 石斧是主力工具(砍挖凿通用,耐久约 40 次);石矛用于投掷打鸟。推荐"一矛三斧"。
        - 8 块花岗岩石块围一圈(中间留空)→ 熔炉;原木入熔炉烧成木炭;木炭+木棍 → 火把。
        - 木棍+石块 → 石棒(狼牙棒),前期近战主武器。
        - 下矿备货参考:20 块木头 → 80 木板 → 80 个梯子 + 20 根木棍。

        ## 陷阱(安全过夜/刷经验/攒毛皮腐肉)
        - 基础陷阱:选凹形平坦地形(动物爱聚集),挖 2x3 坑,坑口围一圈树叶(防动物踩着同伴跳出,也不挡掉落)。
        - 坑深 3 格,底部铺木活板门(触屏版改用木栏杆,更稳但慢),活板门下再挖 2 格活动空间,侧壁开带木门的出入口。
        - 夜里躲在活板门下方攻击掉进来的动物,安全刷经验/毛皮/腐肉;没怪就原地睡觉并立即起床,下一波刷出来继续打;睡一下精神恢复大半。
        - 钉刺陷阱:2x3 深 4 格,靠门 1 格放活板门、其余放钉刺,动物摔上去自己死(注意:系统判定非玩家击杀则没有经验,可掐时机补最后一击)。
        - 流水钉刺陷阱(进阶,月圆夜主力):3x3 深 4 格,一边一排箱子,箱子上一格放钉刺,另一边放水让水流向箱子——掉落物自动冲进箱子保存,人在旁边睡觉当诱饵即可。
        - 陷阱附近不要有树:豹子会刷在树上跳过防线。

        ## 食物与农耕
        - 腐肉放置一段时间会变成自带一次肥料的耕地块,建议只种小麦。
        - 海上耕地:用鹅卵石在海面围条状水槽,水槽间填耕地,动物踩不到,产量稳。
        - 捕鱼场:向深海铺一条与水面同高的路,尽头水下铺 5x5 两层平台后挖掉上层,鱼(含鲨鱼鲸鱼)会掉进凹槽。
        - 仙人掌可以当燃料(但不能炼铁)。

        ## 狩猎
        - 动物会被惊走:潜行接近,或用矛投掷;打鸟基本只能矛投/潜行接近。
        - 食草动物可用草引诱进陷阱;头两天尽量多囤肉,之后附近食草动物会被夜里刷的食肉动物清光。

        ## 实用机关(需电路材料)
        - 刷石机:核心是粘性活塞(活塞+铁块),锗矿要 48 个起步;材料=一桶岩浆+一桶水+2 电线+穿线方块+n板+活塞。
          活塞设置:推动距离拉最低(1 格)、可推方块数拉最高(8)。面向东,左槽放岩浆(建议放在前方第 3 格,防止石头堵源头),右槽放水,水流把刷出的鹅卵石/玄武岩冲进箱子。环境太热(沙漠)箱子可能着火,可挖深一格用流水远端接收。
        - 辨方向:放一个方块把可推挤的植物挤出去,植物掉出的方向就是东。
        - 食肉动物吸引器:垫高 2 格 + 穿线方块 + 朝天的发射器(调成"可接受物品"),放肉块或海胆当诱饵,大范围吸引食肉动物进陷阱;缺点是吵,用腐肉要定期检查防止变耕地。

        ## 环境与安全
        - 月圆之夜是第 4 天晚上:狼人血厚难打,但有几率掉钻石——配合流水钉刺陷阱效率最高。
        - 低温会感冒(下水更冷),毛皮做衣服保暖;第一天必定下雨。
        - 往深处挖之前尽量先探明下方有没有岩浆池,避免直接挖穿掉进去。

        ## 给玩家的开局建议(守护灵仅供转述,无法替玩家执行)
        - 模式选残酷/挑战;出生地难度选简单;世界类型选小岛(出生点附近有山的概率高,矿多)。
        - 角色:女角色移速快抗饿,DORIS 穿得多抗寒,适合长期生存;男角色近战伤害高但易饿,适合前期速推。
        - 开局第一眼看:离岸远不远、附近有没有沙砾滩和树、有没有沙漠(仙人掌燃料+硝石)、有没有山。
        """;

    public string Directory { get; } = directory;

    public void EnsureStarter()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            WriteIfMissing(StarterFileName, StarterContent);
            WriteIfMissing(TutorialFileName, TutorialContent);
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] knowledge folder init failed: {exception.Message}");
        }
    }

    private void WriteIfMissing(string fileName, string content)
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
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
