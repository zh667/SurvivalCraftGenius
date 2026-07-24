using Engine;
using Game;
using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Immediate (single-frame) perception tools. Output is compact JSON aimed at
/// LLM consumption: aggregate counts plus positions only for uncommon blocks.
/// </summary>
public static class GeniusPerception
{
    private const int HorizontalRadius = 8;
    private const int VerticalRadius = 4;
    private const int RareBlockThreshold = 8;
    private const int MaxRarePositions = 25;
    private const float CreatureScanRange = 16f;

    public static string ScanSurroundings(ComponentGeniusBrain brain, ComponentBody? playerBody)
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var center = brain.Creature.ComponentBody.Position;
        var centerCell = Terrain.ToCell(center);

        var countsByName = new Dictionary<string, int>();
        var positionsByName = new Dictionary<string, List<Point3>>();
        for (var dx = -HorizontalRadius; dx <= HorizontalRadius; dx++)
        {
            for (var dy = -VerticalRadius; dy <= VerticalRadius; dy++)
            {
                for (var dz = -HorizontalRadius; dz <= HorizontalRadius; dz++)
                {
                    var x = centerCell.X + dx;
                    var y = centerCell.Y + dy;
                    var z = centerCell.Z + dz;
                    if (y is < 0 or > 255)
                    {
                        continue;
                    }

                    var value = terrain.GetCellValue(x, y, z);
                    var contents = Terrain.ExtractContents(value);
                    if (contents == 0)
                    {
                        continue;
                    }

                    var name = BlocksManager.Blocks[contents].GetDisplayName(brain.SubsystemTerrain, value);
                    countsByName[name] = countsByName.TryGetValue(name, out var count) ? count + 1 : 1;
                    if (!positionsByName.TryGetValue(name, out var positions))
                    {
                        positions = [];
                        positionsByName[name] = positions;
                    }

                    if (positions.Count <= RareBlockThreshold)
                    {
                        positions.Add(new Point3(x, y, z));
                    }
                }
            }
        }

        var blockCounts = new JObject();
        foreach (var pair in countsByName.OrderByDescending(p => p.Value))
        {
            blockCounts[pair.Key] = pair.Value;
        }

        var rareBlocks = new JArray();
        foreach (var pair in countsByName.Where(p => p.Value <= RareBlockThreshold))
        {
            foreach (var point in positionsByName[pair.Key])
            {
                if (rareBlocks.Count >= MaxRarePositions)
                {
                    break;
                }

                rareBlocks.Add(new JObject
                {
                    ["name"] = pair.Key,
                    ["pos"] = PointArray(point),
                });
            }
        }

        var creatures = new JArray();
        foreach (var body in brain.SubsystemBodies.Bodies)
        {
            if (body == brain.Creature.ComponentBody)
            {
                continue;
            }

            var distance = Vector3.Distance(body.Position, center);
            if (distance > CreatureScanRange)
            {
                continue;
            }

            var creature = body.Entity.FindComponent<ComponentCreature>();
            if (creature is null)
            {
                continue;
            }

            creatures.Add(new JObject
            {
                ["name"] = creature.DisplayName,
                ["is_player"] = creature is ComponentPlayer,
                ["distance"] = Math.Round(distance, 1),
                ["pos"] = PointArray(Terrain.ToCell(body.Position)),
            });
        }

        var droppedItems = new JArray();
        foreach (var pickable in brain.SubsystemPickables.Pickables)
        {
            if (pickable.ToRemove)
            {
                continue;
            }

            var distance = Vector3.Distance(pickable.Position, center);
            if (distance > CreatureScanRange || droppedItems.Count >= 20)
            {
                continue;
            }

            droppedItems.Add(new JObject
            {
                ["name"] = BlocksManager.Blocks[Terrain.ExtractContents(pickable.Value)]
                    .GetDisplayName(brain.SubsystemTerrain, pickable.Value),
                ["count"] = pickable.Count,
                ["pos"] = PointArray(Terrain.ToCell(pickable.Position)),
            });
        }

        var result = new JObject
        {
            ["my_pos"] = PointArray(centerCell),
            ["block_counts_within_8m"] = blockCounts,
            ["uncommon_blocks"] = rareBlocks,
            ["creatures_within_16m"] = creatures,
            ["dropped_items_within_16m"] = droppedItems,
        };
        if (playerBody is not null)
        {
            result["player_pos"] = PointArray(Terrain.ToCell(playerBody.Position));
            result["player_distance"] = Math.Round(Vector3.Distance(playerBody.Position, center), 1);
        }

        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static string DescribeInventory(ComponentGeniusBrain brain)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return """{"slots":[]}""";
        }

        var slots = new JArray();
        for (var i = 0; i < inventory.SlotsCount; i++)
        {
            var value = inventory.GetSlotValue(i);
            var count = inventory.GetSlotCount(i);
            if (value == 0 || count <= 0)
            {
                continue;
            }

            var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            var entry = new JObject
            {
                ["slot_index"] = i,
                ["name"] = block.GetDisplayName(brain.SubsystemTerrain, value),
                ["count"] = count,
            };
            // Tool efficiency by mechanics, not by name — a 石锤 quarries like a
            // stone pick and the model must judge by these numbers.
            AddPower(entry, "quarry_power", block.GetQuarryPower(value));
            AddPower(entry, "shovel_power", block.GetShovelPower(value));
            AddPower(entry, "hack_power", block.GetHackPower(value));
            AddPower(entry, "melee_power", block.GetMeleePower(value));
            slots.Add(entry);
        }

        return new JObject
        {
            ["slots"] = slots,
            ["note"] = slots.Count == 0
                ? "inventory is empty"
                : "quarry=挖石/矿效率, shovel=挖土效率, hack=砍木效率, melee=攻击力; 数值>1 才算工具",
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static void AddPower(JObject entry, string key, float power)
    {
        if (power > 1f)
        {
            entry[key] = Math.Round(power, 1);
        }
    }

    private static JArray PointArray(Point3 point)
    {
        return [point.X, point.Y, point.Z];
    }
}
