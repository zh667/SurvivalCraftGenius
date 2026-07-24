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
            "Scan blocks and creatures around yourself. Returns block counts, positions of uncommon blocks, nearby creatures and the player's position.",
            NoParameters));
        registry.Register(new GeniusToolDefinition(
            "goto",
            "Walk to the given block coordinate. Completes when arrived; fails if the terrain is impassable.",
            PointParameters));
        registry.Register(new GeniusToolDefinition(
            "follow_player",
            "Start following the player continuously. Stays active until another movement tool is used.",
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
        return registry;
    }
}
