using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class ToolCatalogTests
{
    [Fact]
    public void Catalog_ContainsAllV1AndV2Tools()
    {
        var names = ToolCatalog.CreateDefaultRegistry().Tools.Select(t => t.Name).ToList();

        string[] expected =
        [
            "say", "todowrite", "task_status", "task_stop", "scan_surroundings", "look_around", "goto", "follow_player", "dig_block",
            "place_block", "get_inventory", "collect_items", "take_from_chest",
            "put_into_chest", "craft", "smelt", "give_to_player", "equip_tool", "attack", "mine_resource",
            "list_waypoints", "teleport", "query_recipes", "query_help", "read_knowledge",
            "find_blocks", "descend_to",
            "till_soil", "plant_seed", "fertilize", "harvest_crops", "use_bucket", "tend_farm", "find_build_site", "build_shelter", "list_prefabs", "build_prefab",
        ];
        Assert.Equal(expected.OrderBy(n => n), names.OrderBy(n => n));
    }

    [Fact]
    public void AllSchemas_AreValidJsonObjects()
    {
        foreach (var tool in ToolCatalog.CreateDefaultRegistry().Tools)
        {
            var schema = JObject.Parse(tool.ParametersJsonSchema);
            Assert.Equal("object", (string?)schema["type"]);
        }
    }
}
