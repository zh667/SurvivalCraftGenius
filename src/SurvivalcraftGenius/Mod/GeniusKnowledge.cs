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
            return "error[invalid_argument]: give an item name to look up";
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
            return "error[unavailable]: no help content loaded";
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
          1) 视觉:只看得见它"正面半球"14 格内的**捕食者**——但**守护灵不算捕食者了**
             (Category 已改成 Bird),所以鸟不会因为看见我而起飞,不必再绕背后;
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

        ## 农耕(引擎实测,含我自己的操作步骤)
        标准流程,六步,别自己发明:
        1. **till_soil(x,y,z,width,length)** —— 把地翻成耕地。y 给的是地面那格本身,不是上面的空气。
           草地要耙两遍(草→泥土→耕地),这个工具已经替我做了。dig_block **不能**翻地,它只会把土挖掉。
        2. **plant_seed(x,y,z,seed_name,count)** —— 播种。种子放下去会变成另一个方块
           (棉花种子→棉花株),所以 place_block 顶不了这一步。
        3. 水:**没有"浇水"这个动作**。耕地会自己找 3 格内的水(横向 1 步、纵向 2 步),
           所以在田边挖一条水渠就够了。**水渠离田至少 2 格**——水会流,浇到田上会冲毁作物、
           耕地也会退回泥土。湿润只是让生长快一倍,不是必需。
        4. **fertilize(x,y,z)** —— 硝石是这游戏的肥料(不是堆肥),3×3 把氮设成 3,收一次耗 1 氮。
           硝石在 y50-90 的砂岩层,mine_resource 硝石 就能挖到。
        5. **use_bucket(x,y,z)** —— 取水/倒水,这是移动水的唯一办法(dig_block/place_block 都不行)。
           空桶点**水源块**(池子中间那种,不是流动的边缘)= 装水;水桶点**空格** = 倒出去。
           在田边 2 格外挖一条沟再倒进去,田会自己吸水;直接倒田上会冲毁作物、耕地也退回泥土。
        6. **harvest_crops(x,y,z,radius)** —— 收割。只割熟的、**默认只割种的不割野生的**,
           没熟的留着并告诉我还差多少。

        **成熟标准(照各方块自己的掉落表,提前割纯亏,因为株一样没了)**:
        - 黑麦:种的要 **7 级**才出麦(5-6 级只掉「野生小麦种子」这种次品种子,
          7 级才掉「小麦种子」并有一半概率多掉一样东西);
          **野生黑麦永远不出麦**,任何大小都只有 33% 掉一颗种子——所以别为了凑数去割野草。
        - 补一条容易搞反的:「野生小麦种子」和「小麦种子」**种下去长出来的都是普通(非野生)黑麦**
          (引擎 SeedsBlock.GetPlacementValue 里 data 4 和 5 都是 SetIsWild(false))。
          这两个名字的区别只体现在掉落,不影响你种。
        - 棉花:**2 级**(也是上限),不到 2 级什么都不掉。种的还会还 1-2 颗种子,野的不还。
        - 南瓜:任何大小都掉,但**不到 7 级营养为 0**,等于白割。

        硬门槛和坑:
        - **作物头顶光照必须 ≥9,否则一点都不长**——室内农田要点火把或开天窗,湿润和肥料都救不了。
        - 耕地上面压任何实心方块会立刻退回泥土;**重物踩上去也会**——不过我走到田上会自动蹲下,
          蹲着就不会踩坏(引擎的豁免),所以现在可以放心在田里干活(作物本身不会压坏耕地)。
        - 只有耕地能长黑麦和棉花;南瓜在草地/泥土上也能长。
        - 腐肉/发霉食物被打碎时会变成带 1 点氮的耕地,是个彩蛋,不是常规施肥手段。
        - 海上耕地:用鹅卵石在海面围条状水槽,水槽间填耕地,动物踩不到,产量稳。

        ## 捕鱼场
        - 向深海铺一条与水面同高的路,尽头水下铺 5x5 两层平台后挖掉上层,鱼(含鲨鱼鲸鱼)会掉进凹槽。
        """),

        ("食物与温度(引擎实测).md",
        """
        # 食物与温度(引擎实测)
        > 何时读:被要求搞食物/打猎/种田/过冬前必读。全部数据来自游戏源码掉落表与生长代码,不是经验之谈。

        ## 打猎目标怎么选(掉落表+刷新权重实测)
        - 只为食物打这些——食草兽:牛/公牛(2-4生肉,白公牛6-8最肥)、野猪(1-2)、野牛和驼鹿(2-4肉+毛皮)、
          驯鹿(2)、角马(2-3)、长颈鹿(5-6肉+8皮革)、犀牛(5-6肉,但150血5攻,别轻易惹)。
        - 肉鸟按"你在哪"选(各掉1生鸟肉):**乌鸦=冷或干燥地带最常见的鸟;海鸥=任何海岸线40格内(连冰面都刷);
          鸭=温暖湿润地带**——潜行从背后接近可猎(见"潜行猎鸟"节)。
        - 鸵鸟/食火鸡只刷在温度>8的干燥热带,**刷新权重是普通鸟的1/50,极其罕见**——它们是横财不是日常猎物:
          2生鸟肉+全游戏最肥的蛋、不会飞、听不见脚步,**遇到必打,但永远不要专门去找**。
        - **绝不为食物打食肉动物**:狼/熊/鬣狗/狮/虎/豹/美洲豹只有50%概率掉1块腐肉(吃了75%生病),
          打它们只为自卫或攒皮革毛皮。
        - **鸽子和麻雀掉的是"腐鸟肉×0-1",可能什么都不掉——永远不值得追。**
        - 鱼:鲈鱼/梭鱼/鳐鱼各1条生鱼,鲨鱼1-10条,白鲸和虎鲸15-20条(但240-300血,虎鲸10攻,慎重)。
          **这游戏没有钓鱼机制**——鱼只能下水(或岸边)近战打。
        - **火会销毁掉落**:马/斑马/驴/骆驼被火烧死一块肉都不掉;鸵鸟被烧死2块生肉缩成1块熟肉。别用火猎。

        ## 潜行猎鸟(引擎视听规则)
        - 会飞的鸟只看得见自己**前半球14格内**的目标(背后完全是盲区),只听得见响度≥0.25的声音——
          **潜行的脚步完全静音**,挖掘也无声,但跳跃/高处落地/呕吐会出声惊鸟。
        - 正确姿势:attack 带 sneak=true,我会自动执行猎人走位:只从鸟背后推进,被它面对时原地冻结等它转身。
        - 被惊飞≠猎物没了:受惊的鸟只会飞到约20格内的地面重新落下——attack sneak=true 时我会自动退到
          它的视野外(约18格)潜行等它落地,再从背后摸回去;千万别站在它近处盯着,那会让它永远落不下来。
        - 鸵鸟/食火鸡是聋子(噪音阈值1.0,脚步声远不够),接近它们不需要潜行,直接 attack。

        ## 吃什么与怎么吃
        - 营养排行:熟肉0.8 > 熟鱼0.7 > 面包0.5 = 熟鸟肉0.5 > 生肉0.3 > 生鱼0.25 > 生鸟肉0.2 > 南瓜/奶0.15。
        - 生肉/生鱼/生鸟肉有25%致病率;腐肉类虽然能吃(营养0.2)但**75%生病**——只配饿死边缘救急。
          生病时引擎直接拒绝再吃任何有风险的食物,只能吃零风险熟食/面包/奶/南瓜且只吸收75%营养;睡觉加速痊愈。
        - **偏食必生病**:同一种食物短时间内连吃(约4-5块熟肉的量)触发强制生病——多备几样轮换着吃。
        - **熟食铁律**:烤熟=营养×2~2.7、保质期×5、致病归零。拿到生肉的第一件事是找熔炉烤熟,没有例外。
        - 保质期(游戏分钟,先"变质中"再彻底腐坏):生鱼40最快烂 < 生肉/生鸟60 < 熟食300 < 面包400最耐放。
          没有任何防腐手段——要么尽快吃/烤,要么做成面包存。腐坏食物别扔:它变成带氮肥的堆肥土,是肥料。

        ## 蛋和奶(可再生,不用杀生)
        - 只有6种蛋能吃:**鸵鸟蛋=食火鸡蛋0.4(烤熟0.6,零致病,全游戏性价比之王)** > 鸭/乌鸦/海鸥蛋0.2 >
          鸽子蛋0.1 > 麻雀蛋0.05。生蛋10%致病,烤熟归零且营养×1.5。鸟站定不动约3秒就是在下蛋,平均每250-400秒一次。
        - 挤奶:只有奶牛可挤(空桶右键),普通奶牛300秒回奶。**警告:挤奶会激怒20格内整个牛群持续追打你**,
          先支开或圈养再挤。奶营养0.15、80分钟变质。

        ## 种田(成败全在整地)
        - 黑麦/棉花必须种在锄头翻出的耕土上,种别处立刻退化成野生;南瓜可种草地/泥土/耕土。
        - **整地铁律:浇水+施氮(用腐坏食物堆肥)的耕土上,作物永不枯死;裸耕土上80%概率半路变野生白种。**
        - 光照≥9才生长(露天白天自然满足;地下农场要打足光照)。
        - 低温(季节温度+海拔修正≤4)不禁止生长但大幅减速(黑麦一阶4分钟→8分钟)。
        - 只有长满第7阶的黑麦掉真种子(1-3颗)、长满的南瓜才能吃。
          面包链:9颗真种子→1面粉,+1桶水→面团,熔炉烤→面包(约4-9株麦一条面包,最耐放的主食)。

        ## 温度系统(三层,决策前分清)
        - **群系温度0-15**(每格固定):决定动物刷新(食草兽和多数鸟要>4)和野生植物;它是约千格波长的平滑场——
          找暖区要朝一个方向走几百格或传送跳点采样比较,原地打转没用;**下地洞不会让动物出现(刷新只看群系温度)**。
        - **季节修正(全局)**:夏0,深冬-24——深冬全图冰冻,一季=6个游戏日,熬得过去。
        - **海拔修正**:海平面y=64为0,越高越冷(y=128约-3),越低越暖(y=20约+4)——种田保暖选低处。
        - 识别标志:有冰=冻区;沙漠不下雨=热区;看到野南瓜=温暖湿润;骆驼/狮子/长颈鹿/斑马出没=热区;北极熊=冷区。

        ## 过冬战略
        - 深冬全球-24:种植近乎停摆、动物刷新本就稀少——正确做法是入冬前囤面包和熟食;
          冬天的食物来源:打鸵鸟/食火鸡、捡蛋烤蛋、挤奶、下水捞鱼;别指望冬天临时种田救急。
        """),

        ("挖矿与工具(引擎实测).md",
        """
        # 挖矿与工具(引擎实测)
        > 何时读:接到挖矿/找矿/做工具任务时必读。全部数据来自引擎代码与数据表,含一处纠正游戏内置帮助的重要事实。

        ## 最重要的事实:工具不影响掉落
        - **所有矿物的掉落没有任何工具门槛——徒手挖钻石矿照样掉2颗钻石**(全部7种矿 RequiredToolLevel=0)。
          游戏内置帮助说"钻石或孔雀石需要更好的工具否则什么都得不到"——**那是旧版遗留的错误文案,别信**。
        - 工具只决定速度和耐久:徒手挖铁矿22秒/块,石锤4.4秒,铜镐2秒,铁镐1.7秒。
        - 挖掘时我会自动换背包里最快的工具;用错类型惩罚巨大(铁斧挖铁矿要14.7秒——斧的挖掘力只有1.5)。

        ## 工具链自举(从零开始)
        1. 徒手就能:砍树(5秒/块)、挖泥土(1.5秒)、挖砾石(3秒,**67%出石块——石器原料的捷径**)、
           硬挖花岗岩(14秒/块,出鹅卵石)。**石层通常就在泥土下2-4格:徒手挖穿脚下的泥土马上见花岗岩,
           忍着挖1块(14秒)就够合石锤,之后全程提速——绝不要因为"徒手慢"就伸手向玩家要石头。**
        2. **石锤(石制工具,2木棒+1鹅卵石或石块,只要工作台不用熔炉)= 万能挖掘工具**:
           挖煤/铜2.8秒、铁/钻石4.4秒,耐久40。接到挖矿任务先确保有它,没有就地取材做一把。
        3. 孔雀石→熔炉(木头当燃料就够)→铜锭→铜镐(挖掘力11,耐久75);铁矿石→熔炉(**必须用煤当燃料**)→铁锭→铁镐(13,150)。
        4. 钻石工具=铁工具+3钻石升级(挖掘力15,耐久450)。
        - 武器同理分级:木/石矛枪棒(近战2-4)→铜砍刀(5)→铁砍刀(6)→钻石砍刀(7);打大型猎物(抗性≥50)至少要近战≥3。

        ## 矿层深度表(找不到矿=没挖到对应深度)
        | 矿 | 深度 | 宿主岩 |
        |---|---|---|
        | 煤 | y5-200 | 花岗岩(到处都是,最好找) |
        | 铜(孔雀石) | y20-65 | 花岗岩 |
        | 硝石 | y50-90 | 只在砂岩层(沙漠地下) |
        | 铁 / 硫 / 锗 | **y2-40** | **玄武岩(深层黑岩)** |
        | 钻石 | **y2-15** | 玄武岩最深层 |
        - **搜不到矿先看自己的y坐标**:站在地表(y≈65)搜铁矿基本搜不到——先 goto(dig_through=true)挖到目标深度再 mine_resource。
        - **岩浆窝集中在y15-20,恰好在挖钻石的路上**——导航会自动绕岩浆,但挖深层时留意脚下。
        - 水窝在y40-60;砾石/沙子会塌落,从下方挖它们时小心被埋。
        - 基岩(最底层)不可挖;水和岩浆不可挖(用桶或填方块处理)。

        ## 其他
        - 掉落物会自动吸进背包(1.75米);钻石矿掉3颗经验珠,别的矿掉1颗。
        - 耐久=可挖次数(石锤40次,铁镐150次),挖光工具直接消失——长途挖矿多带一把备用。
        - TNT炸矿零损耗(所有矿物有防爆掉落保护),但炸普通石头会丢料——火药充足时炸矿是效率手段。
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
          1) 视觉:只看得见它"正面半球"14 格内的**捕食者**——但**守护灵不算捕食者了**
             (Category 已改成 Bird),所以鸟不会因为看见我而起飞,不必再绕背后;
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

    /// <summary>食物与温度(引擎实测).md exactly as shipped in v0.8.2 (before availability-first bird guidance).</summary>
    private const string FoodGuideV082 =
        """
        # 食物与温度(引擎实测)
        > 何时读:被要求搞食物/打猎/种田/过冬前必读。全部数据来自游戏源码掉落表与生长代码,不是经验之谈。

        ## 打猎目标怎么选(掉落表实测)
        - 只为食物打这些——食草兽:牛/公牛(2-4生肉,白公牛6-8最肥)、野猪(1-2)、野牛和驼鹿(2-4肉+毛皮)、
          驯鹿(2)、角马(2-3)、长颈鹿(5-6肉+8皮革)、犀牛(5-6肉,但150血5攻,别轻易惹);
          好鸟:**鸵鸟和食火鸡(各掉2生鸟肉+4-6羽毛,不会飞、对脚步声耳背根本听不见你走近,最佳猎物!)**、
          鸭/乌鸦/海鸥(各1生鸟肉)。
        - **绝不为食物打食肉动物**:狼/熊/鬣狗/狮/虎/豹/美洲豹只有50%概率掉1块腐肉(吃了75%生病),
          打它们只为自卫或攒皮革毛皮。
        - **鸽子和麻雀掉的是"腐鸟肉×0-1",可能什么都不掉——永远不值得追。**
        - 鱼:鲈鱼/梭鱼/鳐鱼各1条生鱼,鲨鱼1-10条,白鲸和虎鲸15-20条(但240-300血,虎鲸10攻,慎重)。
          **这游戏没有钓鱼机制**——鱼只能下水(或岸边)近战打。
        - **火会销毁掉落**:马/斑马/驴/骆驼被火烧死一块肉都不掉;鸵鸟被烧死2块生肉缩成1块熟肉。别用火猎。

        ## 吃什么与怎么吃
        - 营养排行:熟肉0.8 > 熟鱼0.7 > 面包0.5 = 熟鸟肉0.5 > 生肉0.3 > 生鱼0.25 > 生鸟肉0.2 > 南瓜/奶0.15。
        - 生肉/生鱼/生鸟肉有25%致病率;腐肉类虽然能吃(营养0.2)但**75%生病**——只配饿死边缘救急。
          生病时引擎直接拒绝再吃任何有风险的食物,只能吃零风险熟食/面包/奶/南瓜且只吸收75%营养;睡觉加速痊愈。
        - **偏食必生病**:同一种食物短时间内连吃(约4-5块熟肉的量)触发强制生病——多备几样轮换着吃。
        - **熟食铁律**:烤熟=营养×2~2.7、保质期×5、致病归零。拿到生肉的第一件事是找熔炉烤熟,没有例外。
        - 保质期(游戏分钟,先"变质中"再彻底腐坏):生鱼40最快烂 < 生肉/生鸟60 < 熟食300 < 面包400最耐放。
          没有任何防腐手段——要么尽快吃/烤,要么做成面包存。腐坏食物别扔:它变成带氮肥的堆肥土,是肥料。

        ## 蛋和奶(可再生,不用杀生)
        - 只有6种蛋能吃:**鸵鸟蛋=食火鸡蛋0.4(烤熟0.6,零致病,全游戏性价比之王)** > 鸭/乌鸦/海鸥蛋0.2 >
          鸽子蛋0.1 > 麻雀蛋0.05。生蛋10%致病,烤熟归零且营养×1.5。鸟站定不动约3秒就是在下蛋,平均每250-400秒一次。
        - 挤奶:只有奶牛可挤(空桶右键),普通奶牛300秒回奶。**警告:挤奶会激怒20格内整个牛群持续追打你**,
          先支开或圈养再挤。奶营养0.15、80分钟变质。

        ## 种田(成败全在整地)
        - 黑麦/棉花必须种在锄头翻出的耕土上,种别处立刻退化成野生;南瓜可种草地/泥土/耕土。
        - **整地铁律:浇水+施氮(用腐坏食物堆肥)的耕土上,作物永不枯死;裸耕土上80%概率半路变野生白种。**
        - 光照≥9才生长(露天白天自然满足;地下农场要打足光照)。
        - 低温(季节温度+海拔修正≤4)不禁止生长但大幅减速(黑麦一阶4分钟→8分钟)。
        - 只有长满第7阶的黑麦掉真种子(1-3颗)、长满的南瓜才能吃。
          面包链:9颗真种子→1面粉,+1桶水→面团,熔炉烤→面包(约4-9株麦一条面包,最耐放的主食)。

        ## 温度系统(三层,决策前分清)
        - **群系温度0-15**(每格固定):决定动物刷新(食草兽和多数鸟要>4)和野生植物;它是约千格波长的平滑场——
          找暖区要朝一个方向走几百格或传送跳点采样比较,原地打转没用;**下地洞不会让动物出现(刷新只看群系温度)**。
        - **季节修正(全局)**:夏0,深冬-24——深冬全图冰冻,一季=6个游戏日,熬得过去。
        - **海拔修正**:海平面y=64为0,越高越冷(y=128约-3),越低越暖(y=20约+4)——种田保暖选低处。
        - 识别标志:有冰=冻区;沙漠不下雨=热区;看到野南瓜=温暖湿润;骆驼/狮子/长颈鹿/斑马出没=热区;北极熊=冷区。

        ## 过冬战略
        - 深冬全球-24:种植近乎停摆、动物刷新本就稀少——正确做法是入冬前囤面包和熟食;
          冬天的食物来源:打鸵鸟/食火鸡、捡蛋烤蛋、挤奶、下水捞鱼;别指望冬天临时种田救急。
        """;

    /// <summary>食物与温度(引擎实测).md exactly as shipped in v0.8.3 (before the hunter-wait automation note).</summary>
    private const string FoodGuideV083 =
        """
        # 食物与温度(引擎实测)
        > 何时读:被要求搞食物/打猎/种田/过冬前必读。全部数据来自游戏源码掉落表与生长代码,不是经验之谈。

        ## 打猎目标怎么选(掉落表+刷新权重实测)
        - 只为食物打这些——食草兽:牛/公牛(2-4生肉,白公牛6-8最肥)、野猪(1-2)、野牛和驼鹿(2-4肉+毛皮)、
          驯鹿(2)、角马(2-3)、长颈鹿(5-6肉+8皮革)、犀牛(5-6肉,但150血5攻,别轻易惹)。
        - 肉鸟按"你在哪"选(各掉1生鸟肉):**乌鸦=冷或干燥地带最常见的鸟;海鸥=任何海岸线40格内(连冰面都刷);
          鸭=温暖湿润地带**——潜行从背后接近可猎(见"潜行猎鸟"节)。
        - 鸵鸟/食火鸡只刷在温度>8的干燥热带,**刷新权重是普通鸟的1/50,极其罕见**——它们是横财不是日常猎物:
          2生鸟肉+全游戏最肥的蛋、不会飞、听不见脚步,**遇到必打,但永远不要专门去找**。
        - **绝不为食物打食肉动物**:狼/熊/鬣狗/狮/虎/豹/美洲豹只有50%概率掉1块腐肉(吃了75%生病),
          打它们只为自卫或攒皮革毛皮。
        - **鸽子和麻雀掉的是"腐鸟肉×0-1",可能什么都不掉——永远不值得追。**
        - 鱼:鲈鱼/梭鱼/鳐鱼各1条生鱼,鲨鱼1-10条,白鲸和虎鲸15-20条(但240-300血,虎鲸10攻,慎重)。
          **这游戏没有钓鱼机制**——鱼只能下水(或岸边)近战打。
        - **火会销毁掉落**:马/斑马/驴/骆驼被火烧死一块肉都不掉;鸵鸟被烧死2块生肉缩成1块熟肉。别用火猎。

        ## 潜行猎鸟(引擎视听规则)
        - 会飞的鸟只看得见自己**前半球14格内**的目标(背后完全是盲区),只听得见响度≥0.25的声音——
          **潜行的脚步完全静音**,挖掘也无声,但跳跃/高处落地/呕吐会出声惊鸟。
        - 正确姿势:attack 带 sneak=true,我会自动执行猎人走位:只从鸟背后推进,被它面对时原地冻结等它转身。
        - 被惊飞≠猎物没了:受惊的鸟只会飞到约20格内的地面重新落下——原地潜行别动,等它落地再接近。
        - 鸵鸟/食火鸡是聋子(噪音阈值1.0,脚步声远不够),接近它们不需要潜行,直接 attack。

        ## 吃什么与怎么吃
        - 营养排行:熟肉0.8 > 熟鱼0.7 > 面包0.5 = 熟鸟肉0.5 > 生肉0.3 > 生鱼0.25 > 生鸟肉0.2 > 南瓜/奶0.15。
        - 生肉/生鱼/生鸟肉有25%致病率;腐肉类虽然能吃(营养0.2)但**75%生病**——只配饿死边缘救急。
          生病时引擎直接拒绝再吃任何有风险的食物,只能吃零风险熟食/面包/奶/南瓜且只吸收75%营养;睡觉加速痊愈。
        - **偏食必生病**:同一种食物短时间内连吃(约4-5块熟肉的量)触发强制生病——多备几样轮换着吃。
        - **熟食铁律**:烤熟=营养×2~2.7、保质期×5、致病归零。拿到生肉的第一件事是找熔炉烤熟,没有例外。
        - 保质期(游戏分钟,先"变质中"再彻底腐坏):生鱼40最快烂 < 生肉/生鸟60 < 熟食300 < 面包400最耐放。
          没有任何防腐手段——要么尽快吃/烤,要么做成面包存。腐坏食物别扔:它变成带氮肥的堆肥土,是肥料。

        ## 蛋和奶(可再生,不用杀生)
        - 只有6种蛋能吃:**鸵鸟蛋=食火鸡蛋0.4(烤熟0.6,零致病,全游戏性价比之王)** > 鸭/乌鸦/海鸥蛋0.2 >
          鸽子蛋0.1 > 麻雀蛋0.05。生蛋10%致病,烤熟归零且营养×1.5。鸟站定不动约3秒就是在下蛋,平均每250-400秒一次。
        - 挤奶:只有奶牛可挤(空桶右键),普通奶牛300秒回奶。**警告:挤奶会激怒20格内整个牛群持续追打你**,
          先支开或圈养再挤。奶营养0.15、80分钟变质。

        ## 种田(成败全在整地)
        - 黑麦/棉花必须种在锄头翻出的耕土上,种别处立刻退化成野生;南瓜可种草地/泥土/耕土。
        - **整地铁律:浇水+施氮(用腐坏食物堆肥)的耕土上,作物永不枯死;裸耕土上80%概率半路变野生白种。**
        - 光照≥9才生长(露天白天自然满足;地下农场要打足光照)。
        - 低温(季节温度+海拔修正≤4)不禁止生长但大幅减速(黑麦一阶4分钟→8分钟)。
        - 只有长满第7阶的黑麦掉真种子(1-3颗)、长满的南瓜才能吃。
          面包链:9颗真种子→1面粉,+1桶水→面团,熔炉烤→面包(约4-9株麦一条面包,最耐放的主食)。

        ## 温度系统(三层,决策前分清)
        - **群系温度0-15**(每格固定):决定动物刷新(食草兽和多数鸟要>4)和野生植物;它是约千格波长的平滑场——
          找暖区要朝一个方向走几百格或传送跳点采样比较,原地打转没用;**下地洞不会让动物出现(刷新只看群系温度)**。
        - **季节修正(全局)**:夏0,深冬-24——深冬全图冰冻,一季=6个游戏日,熬得过去。
        - **海拔修正**:海平面y=64为0,越高越冷(y=128约-3),越低越暖(y=20约+4)——种田保暖选低处。
        - 识别标志:有冰=冻区;沙漠不下雨=热区;看到野南瓜=温暖湿润;骆驼/狮子/长颈鹿/斑马出没=热区;北极熊=冷区。

        ## 过冬战略
        - 深冬全球-24:种植近乎停摆、动物刷新本就稀少——正确做法是入冬前囤面包和熟食;
          冬天的食物来源:打鸵鸟/食火鸡、捡蛋烤蛋、挤奶、下水捞鱼;别指望冬天临时种田救急。
        """;

    /// <summary>挖矿与工具(引擎实测).md exactly as shipped in v0.8.4 (before the dirt-to-stone bootstrap note).</summary>
    private const string MiningGuideV084 =
        """
        # 挖矿与工具(引擎实测)
        > 何时读:接到挖矿/找矿/做工具任务时必读。全部数据来自引擎代码与数据表,含一处纠正游戏内置帮助的重要事实。

        ## 最重要的事实:工具不影响掉落
        - **所有矿物的掉落没有任何工具门槛——徒手挖钻石矿照样掉2颗钻石**(全部7种矿 RequiredToolLevel=0)。
          游戏内置帮助说"钻石或孔雀石需要更好的工具否则什么都得不到"——**那是旧版遗留的错误文案,别信**。
        - 工具只决定速度和耐久:徒手挖铁矿22秒/块,石锤4.4秒,铜镐2秒,铁镐1.7秒。
        - 挖掘时我会自动换背包里最快的工具;用错类型惩罚巨大(铁斧挖铁矿要14.7秒——斧的挖掘力只有1.5)。

        ## 工具链自举(从零开始)
        1. 徒手就能:砍树(5秒/块)、挖泥土(1.5秒)、挖砾石(3秒,**67%出石块——石器原料的捷径**)、
           硬挖花岗岩(14秒,出鹅卵石,应急用)。
        2. **石锤(石制工具,2木棒+1鹅卵石或石块,只要工作台不用熔炉)= 万能挖掘工具**:
           挖煤/铜2.8秒、铁/钻石4.4秒,耐久40。接到挖矿任务先确保有它,没有就地取材做一把。
        3. 孔雀石→熔炉(木头当燃料就够)→铜锭→铜镐(挖掘力11,耐久75);铁矿石→熔炉(**必须用煤当燃料**)→铁锭→铁镐(13,150)。
        4. 钻石工具=铁工具+3钻石升级(挖掘力15,耐久450)。
        - 武器同理分级:木/石矛枪棒(近战2-4)→铜砍刀(5)→铁砍刀(6)→钻石砍刀(7);打大型猎物(抗性≥50)至少要近战≥3。

        ## 矿层深度表(找不到矿=没挖到对应深度)
        | 矿 | 深度 | 宿主岩 |
        |---|---|---|
        | 煤 | y5-200 | 花岗岩(到处都是,最好找) |
        | 铜(孔雀石) | y20-65 | 花岗岩 |
        | 硝石 | y50-90 | 只在砂岩层(沙漠地下) |
        | 铁 / 硫 / 锗 | **y2-40** | **玄武岩(深层黑岩)** |
        | 钻石 | **y2-15** | 玄武岩最深层 |
        - **搜不到矿先看自己的y坐标**:站在地表(y≈65)搜铁矿基本搜不到——先 goto(dig_through=true)挖到目标深度再 mine_resource。
        - **岩浆窝集中在y15-20,恰好在挖钻石的路上**——导航会自动绕岩浆,但挖深层时留意脚下。
        - 水窝在y40-60;砾石/沙子会塌落,从下方挖它们时小心被埋。
        - 基岩(最底层)不可挖;水和岩浆不可挖(用桶或填方块处理)。

        ## 其他
        - 掉落物会自动吸进背包(1.75米);钻石矿掉3颗经验珠,别的矿掉1颗。
        - 耐久=可挖次数(石锤40次,铁镐150次),挖光工具直接消失——长途挖矿多带一把备用。
        - TNT炸矿零损耗(所有矿物有防爆掉落保护),但炸普通石头会丢料——火药充足时炸矿是效率手段。
        """;

    /// <summary>Earlier shipped contents of a built-in guide, for upgrade detection.</summary>
    private static string[] PreviousShippedVersions(string fileName) => fileName switch
    {
        "战斗与狩猎.md" => [CombatGuideV062, CombatGuideV063],
        "环境与生存.md" => [EnvironmentGuideV062, EnvironmentGuideV063],
        "食物与温度(引擎实测).md" => [FoodGuideV082, FoodGuideV083],
        "挖矿与工具(引擎实测).md" => [MiningGuideV084],
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
                return "error[unavailable]: knowledge folder missing";
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
            return $"error[internal]: {exception.Message}";
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
