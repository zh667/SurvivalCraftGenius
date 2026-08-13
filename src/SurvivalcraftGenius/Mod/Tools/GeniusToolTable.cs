namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>
/// Name → handler. This replaced a 421-line switch that every new tool had to
/// be threaded into; adding a tool is now a static method plus one line here.
///
/// <para>Two guards borrowed from Numen's ToolRegistry, both of which turn a
/// silent whole-turn failure into a loud startup failure:</para>
/// <list type="bullet">
/// <item>the tool catalog rides on every request, so an illegal name earns a
/// 400 for the entire turn rather than "that tool is unavailable" — better to
/// die at load;</item>
/// <item>lookup falls back to lower case, because <c>Goto</c> for <c>goto</c>
/// is the LLM typo that actually happens.</item>
/// </list>
/// </summary>
public static class GeniusToolTable
{
    private static readonly Dictionary<string, GeniusToolFn> Handlers =
        new(StringComparer.Ordinal)
        {
            // Chat, knowledge and bookkeeping — the ones that need no body.
            ["say"] = ChatTools.Say,
            ["query_recipes"] = KnowledgeTools.QueryRecipes,
            ["query_help"] = KnowledgeTools.QueryHelp,
            ["read_knowledge"] = KnowledgeTools.ReadKnowledge,
            ["todowrite"] = PlanTools.TodoWrite,
            ["task_status"] = PlanTools.TaskStatus,
            ["task_stop"] = PlanTools.TaskStop,

            // Perception.
            ["scan_surroundings"] = PerceptionTools.ScanSurroundings,
            ["look_around"] = PerceptionTools.LookAround,
            ["get_inventory"] = PerceptionTools.GetInventory,
            ["find_blocks"] = PerceptionTools.FindBlocks,
            ["find_build_site"] = PerceptionTools.FindBuildSite,
            ["list_waypoints"] = PerceptionTools.ListWaypoints,

            // Movement.
            ["goto"] = MovementTools.Goto,
            ["follow_player"] = MovementTools.FollowPlayer,
            ["descend_to"] = MovementTools.DescendTo,
            ["teleport"] = MovementTools.Teleport,

            // Work on the world.
            ["dig_block"] = WorkTools.DigBlock,
            ["place_block"] = WorkTools.PlaceBlock,
            ["mine_resource"] = WorkTools.MineResource,
            ["craft"] = WorkTools.Craft,
            ["smelt"] = WorkTools.Smelt,

            // Farming.
            ["till_soil"] = FarmTools.TillSoil,
            ["plant_seed"] = FarmTools.PlantSeed,
            ["fertilize"] = FarmTools.Fertilize,
            ["use_bucket"] = FarmTools.UseBucket,
            ["harvest_crops"] = FarmTools.HarvestCrops,

            // Building.
            ["build_shelter"] = BuildTools.BuildShelter,
            ["build_prefab"] = BuildTools.BuildPrefab,
            ["list_prefabs"] = BuildTools.ListPrefabs,

            // Items.
            ["collect_items"] = ItemTools.CollectItems,
            ["take_from_chest"] = ItemTools.TakeFromChest,
            ["put_into_chest"] = ItemTools.PutIntoChest,
            ["give_to_player"] = ItemTools.GiveToPlayer,
            ["equip_tool"] = ItemTools.EquipTool,

            // Combat.
            ["attack"] = CombatTools.Attack,
        };

    /// <summary>
    /// Tools that answer without a summoned companion. Everything else is
    /// refused up front, so handlers can read <see cref="GeniusToolContext.Brain"/>
    /// without a null check.
    /// </summary>
    public static readonly HashSet<string> WorksWithoutBrain =
        new(StringComparer.Ordinal)
        {
            // task_status / task_stop answer honestly with no companion ("not
            // summoned, so nothing is running") — more useful than error[not_summoned].
            "say", "query_recipes", "query_help", "read_knowledge", "todowrite",
            "task_status", "task_stop",
        };

    static GeniusToolTable()
    {
        foreach (var name in Handlers.Keys)
        {
            if (!IsLegalName(name))
            {
                throw new InvalidOperationException(
                    $"illegal tool name '{name}' — only [a-z0-9_] is safe to put on the wire");
            }
        }
    }

    public static IReadOnlyCollection<string> Names => Handlers.Keys;

    /// <summary>Exact match first, then a lower-case retry for the LLM's case drift.</summary>
    public static GeniusToolFn? Resolve(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (Handlers.TryGetValue(name, out var exact))
        {
            return exact;
        }

        var lower = name.ToLowerInvariant();
        return lower != name && Handlers.TryGetValue(lower, out var relaxed) ? relaxed : null;
    }

    /// <summary>Same case-drift tolerance, for deciding whether a brain is needed.</summary>
    public static bool NeedsBrain(string name) =>
        !WorksWithoutBrain.Contains(name)
        && !WorksWithoutBrain.Contains(name.ToLowerInvariant());

    private static bool IsLegalName(string name) =>
        name.Length is > 0 and <= 64
        && name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
}
