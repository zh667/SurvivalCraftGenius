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
        var rareData = new Dictionary<Point3, int>();
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
                        rareData[new Point3(x, y, z)] = Terrain.ExtractData(value);
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

                var rareEntry = new JObject
                {
                    ["name"] = pair.Key,
                    ["pos"] = PointArray(point),
                };
                // Raw data bits carry variant state the display name hides
                // (crop growth stage, orientation, open/on state) — surface
                // them so the model can ask query_help what they mean.
                if (rareData.TryGetValue(point, out var data) && data != 0)
                {
                    rareEntry["data"] = data;
                }

                rareBlocks.Add(rareEntry);
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

            var creatureEntry = new JObject
            {
                ["name"] = creature.DisplayName,
                ["is_player"] = creature is ComponentPlayer,
                ["distance"] = Math.Round(distance, 1),
                ["pos"] = PointArray(Terrain.ToCell(body.Position)),
            };
            var health = creature.ComponentHealth;
            if (health is not null)
            {
                creatureEntry["hp_percent"] = (int)Math.Round(health.Health * 100f);
            }

            // Has an attack drive (wolves, bears...) vs. purely passive
            // (cows, the player's livestock) — so the model knows what is
            // safe to ignore and what must not be hit by mistake.
            creatureEntry["can_attack"] = body.Entity.FindComponent<ComponentChaseBehavior>() is not null;
            creatures.Add(creatureEntry);
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

        var myHealth = brain.Creature.ComponentHealth;
        var headValue = terrain.GetCellValue(centerCell.X, centerCell.Y + 1, centerCell.Z);
        var headInWater = BlocksManager.Blocks[Terrain.ExtractContents(headValue)] is WaterBlock;
        var myStatus = new JObject
        {
            ["hp_percent"] = myHealth is null ? 100 : (int)Math.Round(myHealth.Health * 100f),
        };
        if (headInWater)
        {
            myStatus["underwater"] = true;
            if (myHealth is not null)
            {
                myStatus["air_percent"] = (int)Math.Round(myHealth.Air * 100f);
            }
        }

        var onFire = brain.Creature.Entity.FindComponent<ComponentOnFire>();
        if (onFire?.IsOnFire == true)
        {
            myStatus["on_fire"] = true;
        }

        if (brain.Instincts.ActiveInstinct is { } instinct)
        {
            myStatus["instinct_active"] = instinct;
        }

        var result = new JObject
        {
            ["my_pos"] = PointArray(centerCell),
            ["my_status"] = myStatus,
            ["world"] = DescribeWorldState(brain, centerCell),
        };

        // Honest blindness: the server only keeps chunks loaded around
        // players. An unloaded area silently reads as all-air, which looks
        // like "nothing here" — say what is actually happening instead.
        if (terrain.GetChunkAtCell(centerCell.X, centerCell.Z) is null)
        {
            result["area_not_loaded"] = true;
            result["warning"] = "我所在区域还没加载完(远征时世界会在几秒内围绕我加载好)——" +
                "此刻的扫描结果是空的不可信,稍等几秒再 scan 一次";
        }
        else if (terrain.GetChunkAtCell(centerCell.X - HorizontalRadius, centerCell.Z - HorizontalRadius) is null
            || terrain.GetChunkAtCell(centerCell.X + HorizontalRadius, centerCell.Z + HorizontalRadius) is null
            || terrain.GetChunkAtCell(centerCell.X - HorizontalRadius, centerCell.Z + HorizontalRadius) is null
            || terrain.GetChunkAtCell(centerCell.X + HorizontalRadius, centerCell.Z - HorizontalRadius) is null)
        {
            result["scan_partial"] = "扫描范围有一部分未加载(那些方向读不到方块)";
        }

        result["block_counts_within_8m"] = blockCounts;
        result["uncommon_blocks"] = rareBlocks;
        result["creatures_within_16m"] = creatures;
        result["dropped_items_within_16m"] = droppedItems;
        if (playerBody is not null)
        {
            result["player_pos"] = PointArray(Terrain.ToCell(playerBody.Position));
            result["player_distance"] = Math.Round(Vector3.Distance(playerBody.Position, center), 1);
        }

        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// Mechanic-relevant world state, read straight from the engine so the LLM
    /// never has to guess (Numen: feed judgeable state, keep knowledge for the
    /// non-derivable): time of day, moon phase (shapeshifters spawn on
    /// full/new-moon nights), precipitation, and local seasonal temperature.
    /// </summary>
    private static JObject DescribeWorldState(ComponentGeniusBrain brain, Point3 centerCell)
    {
        var world = new JObject();
        var timeOfDay = brain.Project.FindSubsystem<SubsystemTimeOfDay>(throwOnError: false);
        if (timeOfDay is not null)
        {
            world["time_of_day"] = SubsystemTimeOfDay.GetTimeOfDayText(timeOfDay.TimeOfDay);
        }

        var sky = brain.Project.FindSubsystem<SubsystemSky>(throwOnError: false);
        if (sky is not null)
        {
            world["moon_phase"] = sky.MoonPhase;
            var isNight = sky.SkyLightIntensity < 0.1f;
            if (isNight)
            {
                world["is_night"] = true;
            }

            // Engine rule (ComponentShapeshifter): werewolves and other
            // shapeshifters appear on nights of the full moon (0) or new
            // moon (4) — surface the danger instead of hoping the model
            // remembers lunar mechanics.
            if (isNight && sky.MoonPhase is 0 or 4)
            {
                world["shapeshifter_night"] = true;
                world["warning"] = "满月/新月之夜,狼人等变身怪会出没,夜间远行务必小心";
            }
        }

        var weather = brain.Project.FindSubsystem<SubsystemWeather>(throwOnError: false);
        if (weather is not null && weather.PrecipitationIntensity > 0.1f)
        {
            world["is_precipitating"] = true;
        }

        var temperature = brain.SubsystemTerrain.Terrain.GetSeasonalTemperature(centerCell.X, centerCell.Z)
            + SubsystemWeather.GetTemperatureAdjustmentAtHeight(centerCell.Y);
        world["temperature_0_to_15"] = temperature;
        if (temperature <= 0)
        {
            world["note_cold"] = "严寒环境(会降体温/降雪),玩家久留会冻伤";
        }

        return world;
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
