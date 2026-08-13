namespace SurvivalcraftGenius.Agent;

public sealed record GeniusToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema) : IGeniusTool;

/// <summary>
/// The v1 tool set offered to the LLM. Execution lives game-side; this catalog
/// only carries the contracts, so the agent layer stays engine-free.
///
/// Descriptions are re-sent on every single agent step, so they are billed once
/// per step of every task — keep them to what the model cannot infer from the
/// name: what the tool does, why to pick it over its confusable sibling, and the
/// gotchas that cost us a real playtest. Measure with
/// <c>dotnet run --project tools/ToolBench -- --budget</c>.
/// </summary>
public static class ToolCatalog
{
    private const string NoParameters = """{"type":"object","properties":{}}""";

    private const string PointParameters = """
        {"type":"object","properties":{
          "x":{"type":"integer"},
          "y":{"type":"integer"},
          "z":{"type":"integer"}},
         "required":["x","y","z"]}
        """;

    public static ToolRegistry CreateDefaultRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new GeniusToolDefinition(
            "say",
            "Talk to the player: replies and progress reports.",
            """
            {"type":"object","properties":{
              "text":{"type":"string","description":"What to say, in the player's language."}},
             "required":["text"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "todowrite",
            "Keep the plan for a multi-step job (3+ real phases). Skip it for single actions and chit-chat. Call it BEFORE the first physical step, then again after every finished step to mark that one completed and move exactly ONE to in_progress. Each call REPLACES the whole list. Never reset a completed step when resuming — that redoes finished work forever and is refused. If stuck, leave the step in_progress and add a concrete recovery step.",
            """
            {"type":"object","properties":{
              "todos":{"type":"array","description":"The complete updated list.","items":{
                "type":"object","properties":{
                  "content":{"type":"string","description":"One phase, in the player's language."},
                  "status":{"type":"string","enum":["pending","in_progress","completed","cancelled"]}},
                "required":["content","status"]}}},
             "required":["todos"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "task_status",
            "What the body is doing right now, and for how long. Use this instead of re-sending a long job to find out whether it is still going — re-sending restarts it from zero.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "task_stop",
            "Abort the running job and free the body. For when the player changes their mind or the job is clearly not working. A new action tool already replaces the running job, so you rarely need this first.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "scan_surroundings",
            "List what is around me: block counts, positions of uncommon blocks, creatures, player position. For terrain shape and walkability use look_around.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "look_around",
            "Top-down ASCII terrain map around me, scored by my pathfinding's own rules: walkable ground, steps, drops, walls, water, lava. For movement planning and danger checks; scan_surroundings lists objects instead.",
            """
            {"type":"object","properties":{
              "radius":{"type":"integer","description":"Map radius in blocks, 4-16. Default 8."}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "goto",
            "Walk to the given block coordinate. With dig_through=true I will tunnel/build steps through terrain to get there.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "dig_through":{"type":"boolean","description":"Allow digging tunnels and placing step blocks to reach the target. Default false."}},
             "required":["x","y","z"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "mine_resource",
            "Autonomous mining trip: find blocks by name within ~24m (28 below), tunnel, dig, collect drops, repeat to count, walk back. Takes minutes; returns a summary.",
            """
            {"type":"object","properties":{
              "resource_name":{"type":"string","description":"Block name substring, e.g. 铁/iron/煤/coal."},
              "count":{"type":"integer","description":"How many blocks to mine; default 1."}},
             "required":["resource_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "find_blocks",
            "Exact coordinates of every block with this name, nearest first, out to the engine's 64m limit. "
            + "Use BEFORE mining instead of digging blindly. For ores it sweeps that ore's whole depth "
            + "band and names the band when nothing is there.",
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"Block name substring, e.g. 铁矿/coal/花岗岩."},
              "radius":{"type":"integer","description":"Horizontal search radius in blocks, 4-64. Default 32."}},
             "required":["name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "descend_to",
            "Dig a walkable staircase down to a target depth, stopping short of lava and water. The way to "
            + "reach an ore band — goto with dig_through cannot plan a route that deep. looking_for runs "
            + "find_blocks on arrival.",
            """
            {"type":"object","properties":{
              "y":{"type":"integer","description":"Target depth (block Y) to reach."},
              "looking_for":{"type":"string","description":"Optional block name to search for on arrival."}},
             "required":["y"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "find_build_site",
            "Best nearby spot for a building or a field: solid all the way under, no lava or water, lit, "
            + "not the player's property. Uneven ground is fine — up to 4 blocks of slope gets levelled "
            + "before work starts, and the answer says how much digging and filling that is. Call "
            + "BEFORE building or tilling — guessing a coordinate is how you end up on a hollow.",
            """
            {"type":"object","properties":{
              "width":{"type":"integer","description":"Footprint along X, default 5."},
              "length":{"type":"integer","description":"Footprint along Z, default 5."},
              "purpose":{"type":"string","enum":["build","farm"],"description":"farm also demands soil and light>=9."},
              "radius":{"type":"integer","description":"How far to search, default 16."}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "build_shelter",
            "Build a whole shelter in one call: level the ground first, then filled floor (never floating), "
            + "four walls, a doorway and a roof, from bulk blocks in my inventory. Give x/y/z, or omit "
            + "to have me survey a spot. Never hand-place a house with place_block — that yields loose "
            + "blocks, not a building.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "width":{"type":"integer","description":"Outer size along X, 3-9. Default 5."},
              "length":{"type":"integer","description":"Outer size along Z, 3-9. Default 5."},
              "wall_height":{"type":"integer","description":"Wall height, 2-4. Default 3."},
              "material":{"type":"string","description":"Preferred block name, e.g. 木板/鹅卵石."}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "list_prefabs",
            "Named building designs on hand, with footprints. Prefer these over build_shelter when one fits: a prefab is a design someone already made look good, and it costs one call instead of deciding the shape yourself.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "build_prefab",
            "Build a named design from list_prefabs at x/y/z (its lowest-north-west corner; get one from find_build_site). Survival mode checks the WHOLE bill of materials first and refuses without placing anything if short — a half-built house is worse than none. Never builds over the player's own blocks.",
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"Design name from list_prefabs."},
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"}},
             "required":["name","x","y","z"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "till_soil",
            "Rake a rectangle of ground into farmland. The only way to make farmland — dig_block just removes "
            + "the dirt. Levels the plot first (with soil, never stone) so the field is flat rather than "
            + "terraced, and handles grass's two passes itself. Nothing may sit on top of the cells.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer","description":"Y of the ground cell itself, not the air above it."},
              "z":{"type":"integer"},
              "width":{"type":"integer","description":"Size along +X, 1-16. Default 1."},
              "length":{"type":"integer","description":"Size along +Z, 1-16. Default 1."}},
             "required":["x","y","z"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "plant_seed",
            "Sow seeds on bare farmland near the given cell. Not place_block: a seed becomes a different "
            + "block (cotton seed becomes a cotton plant).",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer","description":"Y of the farmland cell."},
              "z":{"type":"integer"},
              "seed_name":{"type":"string","description":"Seed to sow, e.g. 棉花种子/黑麦种子."},
              "count":{"type":"integer","description":"How many to sow. Default 1."}},
             "required":["x","y","z","seed_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "fertilize",
            "Spread saltpeter — this game's fertilizer — over the 3x3 of farmland around a cell, nitrogen to 3. "
            + "Speeds growth; each harvest spends one.",
            PointParameters));
        registry.Register(new GeniusToolDefinition(
            "use_bucket",
            "Fill or empty a bucket at a cell — the interaction dig_block/place_block cannot do. "
            + "Point at a water SOURCE (not a flowing edge) to fill; point at an empty cell to pour. "
            + "Direction is chosen from what the cell holds. This is the only way to move water.",
            PointParameters));
        registry.Register(new GeniusToolDefinition(
            "harvest_crops",
            "Cut every ripe PLANTED crop nearby and collect the drops. Only cuts what is fully grown "
            + "— an early rye gives seed instead of grain and an early pumpkin feeds nobody — and "
            + "leaves wild plants alone unless asked. Reports what was left standing and why. "
            + "Omit x/y/z to work around where I stand.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "radius":{"type":"integer","description":"Search radius in blocks, 1-16. Default 8."},
              "include_wild":{"type":"boolean","description":"Also cut wild plants. Default false — wild rye never yields grain, only a 1-in-3 seed."}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "follow_player",
            "Follow the player continuously. Ends when any task order or a teleport starts; call again to resume.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "dig_block",
            "Walk over and dig the block at the given coordinate. Drops fall on the ground.",
            PointParameters));
        registry.Register(new GeniusToolDefinition(
            "place_block",
            "Place a block from an inventory slot at the given empty coordinate. get_inventory gives slot_index.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "slot_index":{"type":"integer","description":"Inventory slot to take the block from."}},
             "required":["x","y","z","slot_index"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "get_inventory",
            "List your inventory slots with item names and counts.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "collect_items",
            "Pick up all dropped items within ~14m. Use after digging.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "take_from_chest",
            "Walk to the chest at the given coordinate and move items from it into your inventory.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "item_name":{"type":"string","description":"Optional name substring filter; omit to take everything."},
              "max_count":{"type":"integer","description":"Optional cap on items to take; omit for all."}},
             "required":["x","y","z"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "craft",
            "Craft from inventory ingredients. 3x3 recipes need a crafting table within ~5 blocks.",
            """
            {"type":"object","properties":{
              "item_name":{"type":"string","description":"Result item name (display name or English crafting id substring)."},
              "count":{"type":"integer","description":"How many result items to craft; default 1."}},
             "required":["item_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "smelt",
            "Smelt with a furnace recipe. Needs a furnace within ~5 blocks and fuel (coal/wood/planks) in inventory.",
            """
            {"type":"object","properties":{
              "item_name":{"type":"string","description":"Result or input item name substring, e.g. iron."},
              "count":{"type":"integer","description":"How many results to smelt; default 1."}},
             "required":["item_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "give_to_player",
            "Walk to the player and hand over (matching) items from your inventory.",
            """
            {"type":"object","properties":{
              "item_name":{"type":"string","description":"Optional name substring filter; omit to give everything."},
              "max_count":{"type":"integer","description":"Optional cap; omit for all."}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "equip_tool",
            "Set which inventory slot is in your hand (used for digging and fighting).",
            """
            {"type":"object","properties":{
              "slot_index":{"type":"integer"}},
             "required":["slot_index"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "attack",
            "Chase and melee the named creature until it dies or escapes. Cannot target the player.",
            """
            {"type":"object","properties":{
              "target_name":{"type":"string","description":"Creature display name substring, e.g. wolf/狼."},
              "sneak":{"type":"boolean","description":"Silent approach, no jumping. Birds no longer flee from the sight of me, but they still startle at noise within 8m — keep this on for birds and other skittish animals."}},
             "required":["target_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "query_recipes",
            "Look up this game's real crafting/smelting recipes (mods included). Use before crafting anything uncertain — recipes differ from Minecraft.",
            """
            {"type":"object","properties":{
              "item_name":{"type":"string","description":"Item name or keyword; matches results and ingredients."}},
             "required":["item_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "query_help",
            "Search the game's built-in help pages (hunting, farming, weather, combat...). Empty keyword lists all topics.",
            """
            {"type":"object","properties":{
              "keyword":{"type":"string"}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "read_knowledge",
            "Read the player-curated knowledge folder. No topic lists the files; a topic returns that file.",
            """
            {"type":"object","properties":{
              "topic":{"type":"string"}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "list_waypoints",
            "List the waypoints saved on the player's TravelMap mod (name + coordinates).",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "teleport",
            "Emergency travel, not commuting: 60s cooldown, refused under 20 blocks — walk those with goto. Takes a TravelMap waypoint name or coordinates; y is honoured underground (solid rock opens into a pocket), so it is the way OUT when walled in or stranded, and the way to a far waypoint. SET player_asked=true WHENEVER THE PLAYER ASKED TO BE TELEPORTED TO: their instruction overrides both limits, and refusing it is never right. Still refused beside lava or inside player-built blocks.",
            """
            {"type":"object","properties":{
              "waypoint_name":{"type":"string","description":"Waypoint name substring; takes priority over x/y/z."},
              "player_asked":{"type":"boolean","description":"True when the PLAYER asked to be teleported to. Skips the cooldown and the 20-block minimum."},
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"}}}
            """));
        registry.Register(new GeniusToolDefinition(
            "put_into_chest",
            "Walk to the chest at the given coordinate and store your inventory items into it.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "item_name":{"type":"string","description":"Optional name substring filter; omit to store everything."}},
             "required":["x","y","z"]}
            """));
        return registry;
    }
}
