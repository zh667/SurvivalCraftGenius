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

    /// <summary>
    /// Matches the engine's own content radius: TerrainUpdater.PrepareForDrawing
    /// asks for chunks 64 blocks around each player camera, and
    /// AllocateAndFreeChunks FREES everything past it. Searching wider than the
    /// terrain that exists just reads null chunks, so 64 is the real ceiling
    /// near a player — on a far expedition it is GeniusExpeditionKeeper.LoadRadius.
    /// </summary>
    private const int FindMaxRadius = 64;

    private const int FindMaxResults = 12;

    /// <summary>
    /// Cell reads allowed in one call. The scan runs on the game thread, so it
    /// is bounded rather than trusted: at ~2-4ns per read (chunk hoisted out of
    /// the column loop, Y contiguous in TerrainChunk.Cells) this stays inside a
    /// few milliseconds even in the worst case.
    /// </summary>
    private const int FindCellBudget = 1_500_000;

    /// <summary>Non-ore searches sweep this far up/down; ores use their band.</summary>
    private const int FindDefaultVerticalRange = 24;

    /// <summary>
    /// Enough near hits that further rings cannot change the answer — common
    /// blocks (dirt, granite) would otherwise pile up hundreds of thousands of
    /// coordinates nobody reads. Only applies past FindEnoughHitsRing, so a
    /// small deliberate radius is always honoured in full.
    /// </summary>
    private const int FindEnoughHits = 64;

    private const int FindEnoughHitsRing = 12;

    /// <summary>
    /// Pinpoint search: exact coordinates of every block matching a name in a
    /// wide box, nearest first. scan_surroundings only reaches 8m and hides
    /// common blocks behind counts, which left the model guessing where the ore
    /// was and tunnelling blind (playtest: "他挖矿的方式我没看懂").
    /// For known ores the vertical sweep is clamped to the ore's generation
    /// band, which both cuts the cell reads and makes an empty result
    /// meaningful ("not here" rather than "not in the 8 blocks I looked at").
    /// </summary>
    public static string FindBlocks(ComponentGeniusBrain brain, string query, int radius)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "error[invalid_argument]: give a block name to look for, e.g. 铁矿/coal/花岗岩";
        }

        radius = Math.Clamp(radius, 4, FindMaxRadius);
        var terrain = brain.SubsystemTerrain.Terrain;
        var myPosition = brain.Creature.ComponentBody.Position;
        var center = Terrain.ToCell(myPosition);
        var band = GeniusOreBands.Match(query);
        // Ore: sweep its whole generation band. Anything else: a slab around me
        // — a full 255-tall column over a 129x129 footprint would be four
        // million cell reads on the game thread.
        var minY = Math.Max(1, band?.MinY ?? center.Y - FindDefaultVerticalRange);
        var maxY = Math.Min(255, band?.MaxY ?? center.Y + FindDefaultVerticalRange);

        var hits = new List<(Point3 Cell, string Name, float DistanceSquared)>();
        var seenNames = new HashSet<string>();
        // Contents is 10 bits (Terrain.ExtractContents masks 0x3FF) and
        // BlocksManager.m_blocks is exactly Block[1024], so the per-cell
        // "does this block match?" test is an array index rather than a
        // dictionary probe. Underground almost every cell is solid, so this is
        // the hot path — a dictionary here costs more than reading the terrain.
        var matchState = new byte[1024];
        var matchNames = new string?[1024];
        var cellsRead = 0;
        var coveredRadius = 0;
        var loadedEdge = 0;
        var emptyRings = 0;
        var budgetHit = false;
        var enoughFound = false;

        // Nearest-first, ring by ring: the answer the model wants is the closest
        // match, so an interrupted scan still returns the right one and can say
        // honestly how far it got.
        for (var ring = 0; ring <= radius; ring++)
        {
            var ringHadTerrain = false;
            foreach (var (x, z) in GeniusScanGeometry.RingColumns(center.X, center.Z, ring))
            {
                // One chunk lookup per column instead of per cell, then a
                // straight walk down TerrainChunk.Cells (Y is contiguous there).
                var chunk = terrain.GetChunkAtCell(x, z);
                if (chunk is null)
                {
                    continue;
                }

                ringHadTerrain = true;
                var localX = x & 15;
                var localZ = z & 15;
                for (var y = minY; y <= maxY; y++)
                {
                    cellsRead++;
                    var value = chunk.GetCellValueFast(localX, y, localZ);
                    var contents = Terrain.ExtractContents(value);
                    if (contents == 0)
                    {
                        continue;
                    }

                    var state = matchState[contents];
                    if (state == 0)
                    {
                        var block = BlocksManager.Blocks[contents];
                        var displayName = block.GetDisplayName(brain.SubsystemTerrain, value);
                        var craftingId = block.GetCraftingId(value) ?? "";
                        seenNames.Add(displayName);
                        var matches = displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || craftingId.Contains(query, StringComparison.OrdinalIgnoreCase);
                        state = matches ? (byte)2 : (byte)1;
                        matchState[contents] = state;
                        matchNames[contents] = matches ? displayName : null;
                    }

                    if (state == 1)
                    {
                        continue;
                    }

                    var matchedName = matchNames[contents]!;

                    var ddx = x + 0.5f - myPosition.X;
                    var ddy = y + 0.5f - myPosition.Y;
                    var ddz = z + 0.5f - myPosition.Z;
                    hits.Add((new Point3(x, y, z), matchedName, ddx * ddx + ddy * ddy + ddz * ddz));
                }
            }

            coveredRadius = ring;
            if (ringHadTerrain)
            {
                loadedEdge = ring;
                emptyRings = 0;
            }
            else if (ring > 0 && ++emptyRings >= 2)
            {
                // Two solid rings of unallocated chunks: we have walked off the
                // edge of the loaded world, and nothing further will ever hit.
                break;
            }

            if (cellsRead >= FindCellBudget)
            {
                budgetHit = true;
                break;
            }

            if (hits.Count >= FindEnoughHits && ring >= FindEnoughHitsRing)
            {
                // Already far more matches than anyone can act on, all of them
                // closer than anything further out could be. Stopping here is a
                // shortcut, NOT the edge of the world — say so.
                enoughFound = true;
                break;
            }
        }

        // Horizontal reach only. The scan walks square rings around my column
        // while sweeping a whole ore band vertically, so a hit found in ring 12
        // can still be 29m away in 3-D — reporting "found 118 blocks within
        // 12m" next to a nearest hit of 29m made the model think the ore was
        // underfoot (playtest 7). Say which axis the number is about.
        var reach = Math.Min(coveredRadius, loadedEdge);
        var edgeNote = reach >= radius || enoughFound
            ? ""
            : budgetHit
                ? $"; stopped at {reach}m (search budget)"
                : $"; terrain only exists out to about {reach}m around me right now " +
                  "(the world loads around players, and around me on expeditions) — " +
                  "walk or teleport closer to search further";

        if (hits.Count == 0)
        {
            var suggestions = Agent.NameSuggest.Clause(query, seenNames);
            return $"no '{query}' within {reach}m horizontally (searched y{minY}-{maxY} around " +
                $"({center.X},{center.Y},{center.Z})){suggestions}{edgeNote}" +
                GeniusOreBands.Hint(query, myPosition.Y);
        }

        hits.Sort((a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
        var nearest = hits.Take(FindMaxResults).Select(hit =>
            $"{hit.Name}({hit.Cell.X},{hit.Cell.Y},{hit.Cell.Z}) {Math.Sqrt(hit.DistanceSquared):0}m");
        var deepest = hits.Min(hit => hit.Cell.Y);
        var shallowest = hits.Max(hit => hit.Cell.Y);
        var nearestCell = hits[0].Cell;
        var drop = center.Y - nearestCell.Y;
        // The per-hit numbers are true 3-D distances; make the follow-up move
        // explicit so a 29m-away hit does not get treated as arm's reach.
        var nextStep = drop >= 8
            ? $"; the nearest one is {drop} blocks BELOW me — descend_to(y={nearestCell.Y}, " +
              $"looking_for=\"{query}\") first, then mine_resource"
            : "; mine_resource digs these automatically, or goto/dig_block one by one";
        return $"found {hits.Count} matching blocks (searched {reach}m horizontally, " +
            $"they sit at y{deepest}-{shallowest}); " +
            $"nearest: {string.Join(", ", nearest)}" +
            (hits.Count > FindMaxResults ? $" (+{hits.Count - FindMaxResults} more)" : "") +
            edgeNote +
            nextStep;
    }


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
                    if (BlocksManager.Blocks[contents] is CraftingTableBlock or FurnaceBlock or ChestBlock)
                    {
                        // Seen stations become landmark memory (Numen's
                        // known_blocks): the model reuses these coordinates
                        // instead of re-scanning for a table next time.
                        brain.Landmarks?.Record(name, x, y, z);
                    }

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

        // Armour is worn straight out of the bag and does NOT render on the
        // model, so without this line neither the player nor the companion has
        // any way to tell whether it is protected — which would make the whole
        // feature unfalsifiable in a playtest.
        if (brain.Miner.Inventory is { } inventory)
        {
            myStatus["armor"] = GeniusArmor.Describe(inventory);
        }

        // Same reason: crouching on farmland is what stops the companion
        // trampling the field it is making, and a crouch is easy to miss.
        if (brain.Creature.ComponentBody.IsSneaking)
        {
            myStatus["crouching"] = true;
        }

        // Underground awareness: without this the model wandered a cave for
        // minutes "looking for animals" (playtest 2 lesson) — it simply did
        // not know a surface existed above.
        var surfaceY = terrain.GetTopHeight(centerCell.X, centerCell.Z);
        if (centerCell.Y < surfaceY - 2)
        {
            myStatus["underground"] = true;
            myStatus["surface_y"] = surfaceY;
            myStatus["note_underground"] =
                $"我在地下(头顶地表在 y={surfaceY});打猎/找动物/看天气都必须先上到地表," +
                "可用 goto 到地表坐标(dig_through=true 可挖竖井上去)";
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
            // Engine rule (SubsystemCreatureSpawn suitability): most food
            // animals require temperature>4 (ducks also humidity>8); cold
            // biomes spawn mostly wolves/ravens. Say it or the model wanders
            // a snowfield hunting dinner forever (Game.log lesson).
            world["note_cold"] = "严寒环境(会降体温/降雪),玩家久留会冻伤;" +
                "引擎规则:多数可猎食草动物(鸭/牛/猪等)只在温度>4的地带刷新,严寒区常见的只有狼和乌鸦——" +
                "想搞食物应前往温暖地带打猎,在雪原上游荡是等不到猎物的";
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
