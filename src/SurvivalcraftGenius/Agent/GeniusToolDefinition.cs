namespace SurvivalcraftGenius.Agent;

public sealed record GeniusToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema) : IGeniusTool;

/// <summary>
/// The v1 tool set offered to the LLM. Execution lives game-side; this catalog
/// only carries the contracts, so the agent layer stays engine-free.
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
            "Say something to the player. Use this for all conversational replies and progress reports.",
            """
            {"type":"object","properties":{
              "text":{"type":"string","description":"What to say, in the player's language."}},
             "required":["text"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "scan_surroundings",
            "Scan blocks and creatures around yourself. Returns block counts, positions of uncommon blocks, nearby creatures and the player's position. For terrain shape and walkability use look_around instead.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "look_around",
            "Top-down ASCII terrain map centered on me: walkable ground, steps, drops, walls, water, lava hazards — computed with the same rules as my pathfinding. Use for movement planning, terrain questions, and danger checks; scan_surroundings lists things instead.",
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
            "Autonomous mining trip: search for blocks matching the name within ~24m (down to 28 below), tunnel to them, dig them, collect the drops, repeat until count is reached, then walk back. May take minutes; returns a final summary.",
            """
            {"type":"object","properties":{
              "resource_name":{"type":"string","description":"Block name substring, e.g. 铁/iron/煤/coal."},
              "count":{"type":"integer","description":"How many blocks to mine; default 1."}},
             "required":["resource_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "find_blocks",
            "Pinpoint search: exact coordinates of every block with this name within up to 64m, nearest first. "
            + "Use this BEFORE mining to know where the ore actually is instead of digging blindly. "
            + "For ores it automatically searches that ore's whole generation depth band and tells you the band if nothing is there. "
            + "64m is the engine's own limit — terrain does not exist further out, and the answer says how far it really reached.",
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"Block name substring, e.g. 铁矿/coal/花岗岩."},
              "radius":{"type":"integer","description":"Horizontal search radius in blocks, 4-64. Default 32."}},
             "required":["name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "descend_to",
            "Dig a walkable spiral staircase straight down to a target depth, stopping short of lava or water. "
            + "This is the way to reach an ore band — goto with dig_through cannot plan a route that deep. "
            + "Give looking_for to have me run find_blocks the moment I arrive.",
            """
            {"type":"object","properties":{
              "y":{"type":"integer","description":"Target depth (block Y) to reach."},
              "looking_for":{"type":"string","description":"Optional block name to search for on arrival."}},
             "required":["y"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "find_build_site",
            "Survey the ground nearby and return the best spot for a building or a field: flat, solid all "
            + "the way under, no lava or water, lit, and not on the player's property. Call this BEFORE "
            + "building or tilling instead of guessing a coordinate — it is the only way to know a patch "
            + "is not hollow or a cliff edge.",
            """
            {"type":"object","properties":{
              "width":{"type":"integer","description":"Footprint along X, default 5."},
              "length":{"type":"integer","description":"Footprint along Z, default 5."},
              "purpose":{"type":"string","enum":["build","farm"],"description":"farm also demands soil and light>=9."},
              "radius":{"type":"integer","description":"How far to search, default 16."}},
             "required":[]}
            """));
        registry.Register(new GeniusToolDefinition(
            "build_shelter",
            "Build a complete enclosed shelter in one call: filled floor (never floating), four walls, "
            + "a doorway and a roof, using bulk blocks from my inventory. Give x/y/z to place it, or omit "
            + "them to have me pick a surveyed spot. Never hand-place a house with place_block — that is "
            + "how you get a pile of loose blocks instead of a building.",
            """
            {"type":"object","properties":{
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"},
              "width":{"type":"integer","description":"Outer size along X, 3-9. Default 5."},
              "length":{"type":"integer","description":"Outer size along Z, 3-9. Default 5."},
              "wall_height":{"type":"integer","description":"Wall height, 2-4. Default 3."},
              "material":{"type":"string","description":"Preferred block name, e.g. 木板/鹅卵石."}},
             "required":[]}
            """));
        registry.Register(new GeniusToolDefinition(
            "till_soil",
            "Rake a rectangle of ground into farmland with a rake. This is the only way to make farmland — "
            + "dig_block just removes the dirt. Grass needs two passes (grass to dirt, dirt to farmland) and "
            + "this handles that. Nothing may sit on top of the cells.",
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
            "Sow seeds on nearby tilled farmland. Sowing is not the same as place_block: a seed turns into a "
            + "different block (cotton seed becomes a cotton plant). Finds bare farmland around the given cell.",
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
            "Spread saltpeter over the 3x3 of farmland centred on a cell, setting nitrogen to 3. "
            + "Saltpeter is this game's fertilizer; it speeds growth and each harvest spends one nitrogen.",
            PointParameters));
        registry.Register(new GeniusToolDefinition(
            "follow_player",
            "Start following the player continuously. Ends when any new task order (goto/dig/craft/...) starts or teleport is used; call again to resume.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "dig_block",
            "Walk near and dig the block at the given coordinate. Drops fall on the ground. Fails for undiggable blocks or unreachable spots.",
            PointParameters));
        registry.Register(new GeniusToolDefinition(
            "place_block",
            "Place a block from your inventory slot at the given empty coordinate. Check get_inventory first to pick slot_index.",
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
            "Walk around and pick up all dropped items within ~14m into your inventory. Use after digging.",
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
            "Craft items from your inventory ingredients. 3x3 recipes need a crafting table within ~5 blocks. Check get_inventory first.",
            """
            {"type":"object","properties":{
              "item_name":{"type":"string","description":"Result item name (display name or English crafting id substring)."},
              "count":{"type":"integer","description":"How many result items to craft; default 1."}},
             "required":["item_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "smelt",
            "Smelt items using a furnace recipe. Needs a furnace within ~5 blocks and fuel (coal/wood/planks) in your inventory.",
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
              "max_count":{"type":"integer","description":"Optional cap; omit for all."}},
             "required":[]}
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
            "Chase and melee-attack the named creature until it dies or escapes. Cannot target the player.",
            """
            {"type":"object","properties":{
              "target_name":{"type":"string","description":"Creature display name substring, e.g. wolf/狼."},
              "sneak":{"type":"boolean","description":"Sneak-approach from behind the target: silent footsteps, no jumping. REQUIRED for birds and other easily-startled animals (they flee on hearing normal steps within 8m or seeing a predator in front of them within 16m). Slower but the only way to reach them."}},
             "required":["target_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "query_recipes",
            "Look up REAL crafting/smelting recipes in this game (includes mods). Always use this before crafting anything you are not sure about — this game's recipes differ from Minecraft.",
            """
            {"type":"object","properties":{
              "item_name":{"type":"string","description":"Item name or keyword; matches results and ingredients."}},
             "required":["item_name"]}
            """));
        registry.Register(new GeniusToolDefinition(
            "query_help",
            "Search the game's built-in help pages for mechanics (hunting, farming, weather, combat...). Empty keyword lists all topics.",
            """
            {"type":"object","properties":{
              "keyword":{"type":"string"}},
             "required":[]}
            """));
        registry.Register(new GeniusToolDefinition(
            "read_knowledge",
            "Read the local knowledge folder (player-curated tips and guides). No topic lists the files; with a topic returns the matching file.",
            """
            {"type":"object","properties":{
              "topic":{"type":"string"}},
             "required":[]}
            """));
        registry.Register(new GeniusToolDefinition(
            "list_waypoints",
            "List the waypoints saved on the player's TravelMap mod (name + coordinates).",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "teleport",
            "Instantly teleport yourself to a TravelMap waypoint (by name) or to coordinates. The y you give is honoured, including underground: solid rock is opened into a small pocket to stand in, so this is the fastest way down to an ore band. Refused only next to lava or inside player-built blocks. Also the way out when you are walled in and may not dig.",
            """
            {"type":"object","properties":{
              "waypoint_name":{"type":"string","description":"Waypoint name substring; takes priority over x/y/z."},
              "x":{"type":"integer"},
              "y":{"type":"integer"},
              "z":{"type":"integer"}},
             "required":[]}
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
