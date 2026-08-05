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
        if (hits.Count > 0)
        {
            return string.Join("\n", hits);
        }

        var suggestions = SurvivalcraftGenius.Agent.NameSuggest.Clause(
            query, topics.Select(topic => topic.Title));
        return $"no help topic mentions '{query}'{suggestions}; " +
            "call query_help without a keyword to list all topics";
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
    /// <summary>
    /// Built-in topical guides, Numen-skill style: each file opens with a
    /// "when to read me" line, sections are `##` so read_knowledge can return
    /// just the matching section. Inclusion discipline: only what the engine
    /// state can't tell us AND the in-game help doesn't cover — perception
    /// carries live state, query_help carries the official manual.
    /// </summary>
    private static readonly (string FileName, string Content)[] BuiltinGuides =
    [
        ("战斗与狩猎.md",
        """
        # 战斗与狩猎
        > 何时读:要打猎/打鸟/夜间遇敌/选武器时。

        ## 打鸟与潜行狩猎(引擎实测)
        - 鸟(乌鸦/海鸥/鸭等)受惊起飞有三个触发条件:
          1) 视觉:只看得见它"正面半球"16 格内的捕食者(我算捕食者),从背后接近不会被看见;
          2) 听觉:任何 ≥0.25 强度的噪音会惊飞它——正常脚步恰好是 0.25 强度、传 8 格,而潜行时脚步完全无声;跳跃/重落地传 10 格;
          3) 碰撞:碰到身体立刻受惊。
        - 结论:打鸟用 attack 且 sneak=true(会自动潜行并绕到背后接近),接近中绝不跳跃;或远处用矛投掷。
        - 其他容易受惊的动物同理:拿不准就 sneak=true,只是慢一点。

        ## 谁会怕我、谁不怕(引擎实测)
        - 食草动物白天"躲人"的行为只对玩家生效——它们不躲我,我可以直接走到牛/马/鹿跟前;
          但被攻击后会逃跑并记仇 10~20 秒。
        - 掠食动物(狼/鬣狗/熊等)会主动袭击;打不过就撤,别硬拼。

        ## 生物在哪里刷新(引擎规则)
        - 野生动物按周期在**玩家**周围刷新;我远征时也会自己维持身边的世界——区块跟着我加载,
          生物按同一套规则和配额在我周围刷新,所以我可以独立远征打猎。
        - 刚到一片新区域:第一次到会立刻初刷一批,之后按周期补充(约一分钟一波)——
          scan 没看到猎物就等一会儿或边走边找,别原地反复 scan。
        - 有些地方本来就贫瘠(没有适合该生物的地形);换个环境找(鸟喜欢开阔地和树)。

        ## 武器与战法
        - 石矛可投掷,是打鸟和远程消耗的主力;"一矛三斧"是前期标配。
        - 弩和火枪都需要铁;弩只要 2 块铁(含弩箭),做出后野外基本无敌。
        - 食草动物可用草引诱进陷阱;头两天尽量多囤肉,之后附近食草动物会被夜里刷的食肉动物清光。

        ## 月圆夜与变身怪
        - 月相 0=满月、4=新月的黑夜会出变身怪(狼人等);scan 的 world.shapeshifter_night=true 就是这种夜。
        - 月圆之夜(第 4 天晚上)狼人血厚难打,但有几率掉钻石——配合流水钉刺陷阱效率最高。
        """),

        ("环境与生存.md",
        """
        # 环境与生存
        > 何时读:安排过夜/远行/下水/生病/开新档时。

        ## 体温与疾病
        - 低温会感冒(下水更冷);毛皮衣物保暖。第一天必定下雨。
        - scan 的 world.temperature_0_to_15 是当前位置体感温度,≤0 属于严寒环境(降雪、掉体温)。

        ## 睡觉与夜晚
        - 睡觉恢复精神;安全处"睡觉+立即起床"可跳过等待。
        - 夜晚地表刷敌对生物;月相与变身怪细节见《战斗与狩猎》。

        ## 食物
        - 动物掉生肉,篝火/熔炉烤熟更顶饿;食物会腐烂,别久囤生食。
        - 腐肉放置一段时间会变成自带一次肥料的耕地块。

        ## 地下安全
        - 深处小心岩浆:听到咕噜声先停手;往深挖之前先探明脚下有没有岩浆池。
        - 我的导航会自动避开岩浆、憋气不足会自动回到空气,这两件事不用你操心。

        ## 世界加载规则(引擎)
        - 世界围绕玩家**和我**保持加载:我远征离开玩家后,区块会跟着我自动加载,不用回头。
        - 刚传送/刚走进新区域的头几秒,区块可能还在加载(scan 报 area_not_loaded)——
          等几秒再 scan,不要在没加载完时下结论"这里什么都没有"。

        ## 给玩家的开局建议(仅供转述,我无法替玩家执行)
        - 模式选残酷/挑战;出生地难度选简单;世界类型选小岛(出生点附近有山概率高、矿多)。
        - 女角色移速快抗饿,DORIS 穿得多抗寒;男角色近战伤害高但易饿。
        - 开局第一眼看:离岸远不远、附近有没有沙砾滩和树、有没有沙漠(仙人掌燃料+硝石)、有没有山。
        """),

        ("矿物与科技.md",
        """
        # 矿物与科技
        > 何时读:找矿/合成工具/规划科技升级/下矿备货时。

        ## 矿物分布
        - 铁矿、锗矿、硫磺、钻石都在海平面以下的玄武岩层;钻石只在基岩上 10 格以内。
        - 附近有山的地形玄武岩层更厚、矿更多(平坦大陆可能只有约 5 层)。
        - 挖矿策略:在基岩上约 5 格高度,朝洞穴方向水平直线挖——路上有几率碰矿,挖到头也能进洞穴,不白挖。
        - 煤:有花岗岩就可能有;裸露花岗岩石山表面最快,不需要照明;挖煤掉落多经验多,前期升级首选。
        - 铜(孔雀石):海平面以下的花岗岩层;从地下水池边缘挖下去或洞穴浅层找;水里可能有食人鱼。
        - 硝石:只在砂岩层(沙漠/沙滩);沙漠峡谷的裸露砂岩最好找,地形不合适时比钻石还难找。

        ## 科技路线
        - 1 级木石 → 2 级铜 → 3 级铁 → 4 级钻石;一身铜护具+铜刀铜镐就可以直接下洞穴最底层。
        - 没有木镐/石镐:石制挖掘工具是"石锤"。一切配方以 query_recipes 为准,不要凭 Minecraft 印象。

        ## 合成要点
        - 2 原木 → 工作台;原木 → 4 木板;木板上下竖放 → 木棍;木棍撸方块比空手快。
        - 石斧是主力工具(砍挖凿通用,耐久约 40 次);石块+木棍 → 石棒(狼牙棒),前期近战主武器。
        - 8 块花岗岩石块围一圈(中间留空)→ 熔炉;原木入熔炉烧成木炭;木炭+木棍 → 火把。
        - 仙人掌可以当燃料(但热度不够炼铁)。
        - 下矿备货参考:20 块木头 → 80 木板 → 80 个梯子 + 20 根木棍。
        """),

        ("农牧渔与陷阱.md",
        """
        # 农牧渔与陷阱
        > 何时读:建陷阱/种田/搞渔场/安全过夜刷资源时。

        ## 基础陷阱(安全过夜/刷经验/攒毛皮腐肉)
        - 选凹形平坦地形(动物爱聚集),挖 2x3 坑,坑口围一圈树叶(防动物踩同伴跳出,也不挡掉落)。
        - 坑深 3 格,底部铺木活板门(触屏版改用木栏杆更稳),活板门下再挖 2 格活动空间,侧壁开带木门的出入口。
        - 夜里躲在活板门下方攻击掉进来的动物;没怪就原地睡觉并立即起床,下一波刷出来继续打。
        - 陷阱附近不要有树:豹子会刷在树上跳过防线。

        ## 钉刺陷阱
        - 2x3 深 4 格,靠门 1 格放活板门、其余放钉刺,动物摔上去自己死。
        - 注意:系统判定非玩家击杀则没有经验,可掐时机补最后一击。
        - 流水钉刺陷阱(进阶,月圆夜主力):3x3 深 4 格,一边一排箱子,箱子上一格放钉刺,
          另一边放水让水流向箱子——掉落物自动冲进箱子保存,人在旁边睡觉当诱饵即可。

        ## 农耕
        - 腐肉变成的耕地自带一次肥料,建议只种小麦。
        - 海上耕地:用鹅卵石在海面围条状水槽,水槽间填耕地,动物踩不到,产量稳。

        ## 捕鱼场
        - 向深海铺一条与水面同高的路,尽头水下铺 5x5 两层平台后挖掉上层,鱼(含鲨鱼鲸鱼)会掉进凹槽。
        """),

        ("机关电路.md",
        """
        # 机关电路
        > 何时读:要做刷石机/自动装置/电路相关的东西时。

        ## 刷石机
        - 核心是粘性活塞(活塞+铁块),锗矿要 48 个起步;材料=一桶岩浆+一桶水+2 电线+穿线方块+n板+活塞。
        - 活塞设置:推动距离拉最低(1 格)、可推方块数拉最高(8)。
        - 面向东,左槽放岩浆(建议放在前方第 3 格,防止石头堵源头),右槽放水,水流把刷出的鹅卵石/玄武岩冲进箱子。
        - 环境太热(沙漠)箱子可能着火,可挖深一格用流水远端接收。

        ## 实用技巧
        - 辨方向:放一个方块把可推挤的植物挤出去,植物掉出的方向就是东。
        - 食肉动物吸引器:垫高 2 格 + 穿线方块 + 朝天的发射器(调成"可接受物品"),放肉块或海胆当诱饵,
          大范围吸引食肉动物进陷阱;缺点是吵,用腐肉要定期检查防止变耕地。
        """),
    ];

    /// <summary>战斗与狩猎.md exactly as shipped in v0.6.2 (before the spawn-rules section).</summary>
    private const string CombatGuideV062 =
        """
        # 战斗与狩猎
        > 何时读:要打猎/打鸟/夜间遇敌/选武器时。

        ## 打鸟与潜行狩猎(引擎实测)
        - 鸟(乌鸦/海鸥/鸭等)受惊起飞有三个触发条件:
          1) 视觉:只看得见它"正面半球"16 格内的捕食者(我算捕食者),从背后接近不会被看见;
          2) 听觉:任何 ≥0.25 强度的噪音会惊飞它——正常脚步恰好是 0.25 强度、传 8 格,而潜行时脚步完全无声;跳跃/重落地传 10 格;
          3) 碰撞:碰到身体立刻受惊。
        - 结论:打鸟用 attack 且 sneak=true(会自动潜行并绕到背后接近),接近中绝不跳跃;或远处用矛投掷。
        - 其他容易受惊的动物同理:拿不准就 sneak=true,只是慢一点。

        ## 谁会怕我、谁不怕(引擎实测)
        - 食草动物白天"躲人"的行为只对玩家生效——它们不躲我,我可以直接走到牛/马/鹿跟前;
          但被攻击后会逃跑并记仇 10~20 秒。
        - 掠食动物(狼/鬣狗/熊等)会主动袭击;打不过就撤,别硬拼。

        ## 武器与战法
        - 石矛可投掷,是打鸟和远程消耗的主力;"一矛三斧"是前期标配。
        - 弩和火枪都需要铁;弩只要 2 块铁(含弩箭),做出后野外基本无敌。
        - 食草动物可用草引诱进陷阱;头两天尽量多囤肉,之后附近食草动物会被夜里刷的食肉动物清光。

        ## 月圆夜与变身怪
        - 月相 0=满月、4=新月的黑夜会出变身怪(狼人等);scan 的 world.shapeshifter_night=true 就是这种夜。
        - 月圆之夜(第 4 天晚上)狼人血厚难打,但有几率掉钻石——配合流水钉刺陷阱效率最高。
        """;

    /// <summary>环境与生存.md exactly as shipped in v0.6.2 (before the world-loading section).</summary>
    private const string EnvironmentGuideV062 =
        """
        # 环境与生存
        > 何时读:安排过夜/远行/下水/生病/开新档时。

        ## 体温与疾病
        - 低温会感冒(下水更冷);毛皮衣物保暖。第一天必定下雨。
        - scan 的 world.temperature_0_to_15 是当前位置体感温度,≤0 属于严寒环境(降雪、掉体温)。

        ## 睡觉与夜晚
        - 睡觉恢复精神;安全处"睡觉+立即起床"可跳过等待。
        - 夜晚地表刷敌对生物;月相与变身怪细节见《战斗与狩猎》。

        ## 食物
        - 动物掉生肉,篝火/熔炉烤熟更顶饿;食物会腐烂,别久囤生食。
        - 腐肉放置一段时间会变成自带一次肥料的耕地块。

        ## 地下安全
        - 深处小心岩浆:听到咕噜声先停手;往深挖之前先探明脚下有没有岩浆池。
        - 我的导航会自动避开岩浆、憋气不足会自动回到空气,这两件事不用你操心。

        ## 给玩家的开局建议(仅供转述,我无法替玩家执行)
        - 模式选残酷/挑战;出生地难度选简单;世界类型选小岛(出生点附近有山概率高、矿多)。
        - 女角色移速快抗饿,DORIS 穿得多抗寒;男角色近战伤害高但易饿。
        - 开局第一眼看:离岸远不远、附近有没有沙砾滩和树、有没有沙漠(仙人掌燃料+硝石)、有没有山。
        """;

    /// <summary>Legacy single-file guides (pre-v0.6.2); deleted on upgrade only if the player never edited them.</summary>
    private const string LegacyStarterFileName = "游戏技巧-内置.md";

    private const string LegacyStarterContent =
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

    private const string LegacyTutorialFileName = "进阶攻略-贴吧教程整理.md";

    private const string LegacyTutorialContent =
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
            RemoveLegacyIfUnmodified(LegacyStarterFileName, LegacyStarterContent);
            RemoveLegacyIfUnmodified(LegacyTutorialFileName, LegacyTutorialContent);
            foreach (var (fileName, content) in BuiltinGuides)
            {
                WriteOrUpgrade(fileName, content, PreviousShippedVersions(fileName));
            }
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] knowledge folder init failed: {exception.Message}");
        }
    }

    /// <summary>战斗与狩猎.md as shipped in v0.6.3 (spawn rules said "players only" — superseded by the expedition keeper).</summary>
    private static readonly string CombatGuideV063 = CombatGuideV062.Replace(
        "\n## 武器与战法",
        "\n## 生物在哪里刷新(引擎规则)\n" +
        "- 动物和怪物只在**玩家**周围的区块生成和活动;我不算玩家——我独自跑远后身边永远\n" +
        "  不会刷出任何生物,而且区块会卸载(scan 一片空白、无法寻路)。\n" +
        "- 所以打猎/找动物必须在玩家附近进行,或让玩家跟我同行;scan 空白就说明我离玩家太远,\n" +
        "  先回到玩家身边。\n" +
        "\n## 武器与战法");

    /// <summary>环境与生存.md as shipped in v0.6.3 (same superseded world-loading wording).</summary>
    private static readonly string EnvironmentGuideV063 = EnvironmentGuideV062.Replace(
        "\n## 给玩家的开局建议",
        "\n## 世界加载规则(引擎)\n" +
        "- 世界只在**玩家**周围保持加载;我靠走路或传送都无法让远处区块加载。\n" +
        "- 在未加载区域:看不见方块(scan 为空)、不能寻路、不会刷生物。\n" +
        "  远征要么带上玩家同行,要么放弃;发现自己\"失明\"就先回玩家身边。\n" +
        "\n## 给玩家的开局建议");

    /// <summary>Earlier shipped contents of a built-in guide, for upgrade detection.</summary>
    private static string[] PreviousShippedVersions(string fileName) => fileName switch
    {
        "战斗与狩猎.md" => [CombatGuideV062, CombatGuideV063],
        "环境与生存.md" => [EnvironmentGuideV062, EnvironmentGuideV063],
        _ => [],
    };

    /// <summary>Deletes a superseded built-in file, but only if the player never edited it.</summary>
    private void RemoveLegacyIfUnmodified(string fileName, string originalContent)
    {
        var path = Path.Combine(Directory, fileName);
        if (File.Exists(path) && File.ReadAllText(path).Trim() == originalContent.Trim())
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Ships the current built-in guide: creates it when missing, upgrades it
    /// in place when the on-disk copy is an unmodified older shipped version,
    /// and never touches a file the player edited.
    /// </summary>
    private void WriteOrUpgrade(string fileName, string content, string[] previousVersions)
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
            return;
        }

        var existing = File.ReadAllText(path).Trim();
        if (existing == content.Trim())
        {
            return;
        }

        if (previousVersions.Any(version => existing == version.Trim()))
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
                    var lines = File.ReadLines(file).Where(line => line.Trim().Length > 0).Take(2).ToList();
                    var hint = lines.LastOrDefault() ?? "";
                    return $"{Path.GetFileNameWithoutExtension(file)}: {hint.TrimStart('#', '>', ' ')}";
                });
                return "knowledge files (query a topic to get the matching section): "
                    + string.Join("; ", listing);
            }

            // Section-level retrieval: return only the ## sections that match,
            // never whole files — keeps each lookup to a few hundred tokens
            // no matter how large the library grows.
            var allSections = new List<(string File, string Heading, string Body)>();
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                foreach (var (heading, body) in SplitSections(File.ReadAllText(file)))
                {
                    allSections.Add((name, heading, body));
                }
            }

            var matches = allSections
                .Where(section => section.File.Contains(topic, StringComparison.OrdinalIgnoreCase)
                    || section.Heading.Contains(topic, StringComparison.OrdinalIgnoreCase)
                    || section.Body.Contains(topic, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(section =>
                    section.Heading.Contains(topic, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToList();
            if (matches.Count == 0)
            {
                var candidates = allSections.Select(section => section.Heading)
                    .Concat(files.Select(Path.GetFileNameWithoutExtension))
                    .Where(name => name is not null)
                    .Select(name => name!);
                var suggestions = SurvivalcraftGenius.Agent.NameSuggest.Clause(topic, candidates);
                return $"no knowledge section mentions '{topic}'{suggestions}; " +
                    "call read_knowledge without a topic to list all files";
            }

            var parts = matches.Select(section =>
                $"【{section.File} › {section.Heading}】\n{Truncate(section.Body.Trim(), 1500)}");
            var siblings = allSections
                .Where(section => section.File == matches[0].File
                    && !matches.Any(m => m.Heading == section.Heading))
                .Select(section => section.Heading)
                .ToList();
            var footer = siblings.Count > 0
                ? $"\n(《{matches[0].File}》其他章节: {string.Join("、", siblings)})"
                : "";
            return string.Join("\n\n", parts) + footer;
        }
        catch (Exception exception)
        {
            return $"error: {exception.Message}";
        }
    }

    /// <summary>Splits markdown into (heading, body) by `##` headings; the preamble uses the `#` title.</summary>
    private static List<(string Heading, string Body)> SplitSections(string content)
    {
        var sections = new List<(string Heading, string Body)>();
        var heading = "";
        var body = new System.Text.StringBuilder();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal) && heading.Length == 0)
            {
                heading = line[2..].Trim();
            }
            else
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return sections;

        void Flush()
        {
            if (heading.Length > 0 && body.ToString().Trim().Length > 0)
            {
                sections.Add((heading, body.ToString()));
            }

            body.Clear();
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…(truncated)";
}
