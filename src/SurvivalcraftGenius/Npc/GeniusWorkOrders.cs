using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>Recipe lookup and ingredient accounting shared by craft and smelt.</summary>
public static class GeniusCrafting
{
    public sealed record RecipeMatch(CraftingRecipe Recipe, Dictionary<(string Id, int? Data), int> Needs);

    /// <summary>3x3 recipes (any cell in row/col 2) need a crafting table nearby.</summary>
    public static bool NeedsCraftingTable(CraftingRecipe recipe)
    {
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (!string.IsNullOrEmpty(recipe.Ingredients[row * 3 + column]) && (row == 2 || column == 2))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static Dictionary<(string Id, int? Data), int> CountNeeds(CraftingRecipe recipe)
    {
        var needs = new Dictionary<(string, int?), int>();
        foreach (var ingredient in recipe.Ingredients)
        {
            if (string.IsNullOrEmpty(ingredient))
            {
                continue;
            }

            CraftingRecipesManager.DecodeIngredient(ingredient, out var craftingId, out var data);
            var key = (craftingId, data);
            needs[key] = needs.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return needs;
    }

    public static bool ValueMatches(int value, string craftingId, int? data)
    {
        var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
        if (!block.MatchCrafingId(craftingId))
        {
            return false;
        }

        return !data.HasValue || Terrain.ExtractData(value) == data.Value;
    }

    public static bool InventorySatisfies(
        IInventory inventory,
        Dictionary<(string Id, int? Data), int> needs)
    {
        foreach (var need in needs)
        {
            var available = 0;
            for (var slot = 0; slot < inventory.SlotsCount; slot++)
            {
                var value = inventory.GetSlotValue(slot);
                if (value != 0 && ValueMatches(value, need.Key.Id, need.Key.Data))
                {
                    available += inventory.GetSlotCount(slot);
                }
            }

            if (available < need.Value)
            {
                return false;
            }
        }

        return true;
    }

    public static void ConsumeNeeds(
        IInventory inventory,
        Dictionary<(string Id, int? Data), int> needs)
    {
        foreach (var need in needs)
        {
            var remaining = need.Value;
            for (var slot = 0; slot < inventory.SlotsCount && remaining > 0; slot++)
            {
                var value = inventory.GetSlotValue(slot);
                if (value == 0 || !ValueMatches(value, need.Key.Id, need.Key.Data))
                {
                    continue;
                }

                var take = Math.Min(inventory.GetSlotCount(slot), remaining);
                inventory.RemoveSlotItems(slot, take);
                remaining -= take;
            }
        }
    }

    /// <summary>
    /// Finds a recipe whose result name contains the query (localized display
    /// name or crafting id) and whose ingredients the inventory can cover.
    /// heatFilter: false = hand/table recipes, true = furnace recipes.
    /// </summary>
    public static RecipeMatch? FindRecipe(
        ComponentGeniusBrain brain,
        IInventory inventory,
        string query,
        bool furnaceRecipes)
    {
        // Rank by specificity: exact display-name match first, then shortest
        // containing name — "石块" must resolve to 石块, never to 钻石块.
        var candidates = new List<(CraftingRecipe Recipe, string DisplayName, bool Exact)>();
        foreach (var recipe in CraftingRecipesManager.Recipes)
        {
            if (furnaceRecipes != recipe.RequiredHeatLevel > 0f || recipe.ResultValue == 0)
            {
                continue;
            }

            var resultBlock = BlocksManager.Blocks[Terrain.ExtractContents(recipe.ResultValue)];
            var displayName = resultBlock.GetDisplayName(brain.SubsystemTerrain, recipe.ResultValue);
            var craftingId = resultBlock.GetCraftingId(recipe.ResultValue) ?? "";
            var exact = string.Equals(displayName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(craftingId, query, StringComparison.OrdinalIgnoreCase);
            if (exact
                || displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || craftingId.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((recipe, displayName, exact));
            }
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Exact)
            .ThenBy(candidate => candidate.DisplayName.Length)
            .ToList();
        foreach (var candidate in ordered)
        {
            var needs = CountNeeds(candidate.Recipe);
            if (InventorySatisfies(inventory, needs))
            {
                return new RecipeMatch(candidate.Recipe, needs);
            }
        }

        return ordered.Count > 0
            ? new RecipeMatch(ordered[0].Recipe, CountNeeds(ordered[0].Recipe))
            : null;
    }

    public static string DescribeNeeds(
        ComponentGeniusBrain brain,
        Dictionary<(string Id, int? Data), int> needs)
    {
        return string.Join(", ", needs.Select(need => $"{need.Key.Id} x{need.Value}"));
    }

    public static void DeliverResult(ComponentGeniusBrain brain, IInventory inventory, int value, int count)
    {
        var leftover = ComponentInventoryBase.AcquireItems(inventory, value, count);
        if (leftover > 0)
        {
            brain.SubsystemPickables.AddPickable(
                value,
                leftover,
                brain.Creature.ComponentBody.Position + new Vector3(0f, 1f, 0f),
                null,
                null);
        }
    }
}

/// <summary>Crafts count items by name using hand or a nearby crafting table.</summary>
public sealed class CraftOrder(string itemName, int count) : GeniusOrder
{
    private const float SecondsPerCraft = 1.2f;

    private GeniusCrafting.RecipeMatch? _match;
    private TunnelNavigator? _approach;
    private int _crafted;
    private float _progress;

    protected override float TimeoutSeconds => 180f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error: I have no inventory";
        }

        if (_match is null)
        {
            _match = GeniusCrafting.FindRecipe(brain, inventory, itemName, furnaceRecipes: false);
            if (_match is null)
            {
                return $"error: I don't know a crafting recipe for '{itemName}'";
            }

            if (!GeniusCrafting.InventorySatisfies(inventory, _match.Needs))
            {
                return $"error: missing ingredients for '{itemName}' — needs " +
                    GeniusCrafting.DescribeNeeds(brain, _match.Needs) + " per item";
            }
        }

        // Walk to a crafting table on my own instead of erroring out.
        if (GeniusCrafting.NeedsCraftingTable(_match.Recipe)
            && !IsBlockNearby<CraftingTableBlock>(brain, 5))
        {
            if (_approach is null)
            {
                var table = FindNearestBlock<CraftingTableBlock>(brain, 32);
                if (table is null)
                {
                    return "error: no crafting table within ~32 blocks — place one or lead me to one";
                }

                _approach = new TunnelNavigator(
                    new Vector3(table.Value.X + 0.5f, table.Value.Y + 0.5f, table.Value.Z + 0.5f),
                    allowDigging: false,
                    arriveDistance: 4f);
            }

            switch (_approach.Tick(brain, dt))
            {
                case NavStatus.Failed:
                    return $"error: cannot reach the crafting table ({_approach.FailureReason})";
                case NavStatus.Running:
                    return null;
            }

            _approach = null;
        }

        _progress += dt;
        if (_progress < SecondsPerCraft)
        {
            return null;
        }

        _progress = 0f;
        if (!GeniusCrafting.InventorySatisfies(inventory, _match.Needs))
        {
            return Summary(brain) + "; ran out of ingredients";
        }

        GeniusCrafting.ConsumeNeeds(inventory, _match.Needs);
        GeniusCrafting.DeliverResult(brain, inventory, _match.Recipe.ResultValue, _match.Recipe.ResultCount);
        if (_match.Recipe.RemainsValue != 0 && _match.Recipe.RemainsCount > 0)
        {
            GeniusCrafting.DeliverResult(brain, inventory, _match.Recipe.RemainsValue, _match.Recipe.RemainsCount);
        }

        _crafted += _match.Recipe.ResultCount;
        return _crafted >= Math.Max(1, count) ? Summary(brain) : null;
    }

    private string Summary(ComponentGeniusBrain brain)
    {
        if (_match is null || _crafted == 0)
        {
            return "crafted nothing";
        }

        var name = GeniusInventoryOps.ItemName(brain, _match.Recipe.ResultValue);
        return $"crafted {name} x{_crafted}";
    }

    internal static bool IsBlockNearby<TBlock>(ComponentGeniusBrain brain, int radius)
        where TBlock : Block
    {
        return FindNearestBlock<TBlock>(brain, radius, verticalRadius: 2) is not null;
    }

    internal static Point3? FindNearestBlock<TBlock>(
        ComponentGeniusBrain brain,
        int radius,
        int verticalRadius = 16)
        where TBlock : Block
    {
        var terrain = brain.SubsystemTerrain.Terrain;
        var center = Terrain.ToCell(brain.Creature.ComponentBody.Position);
        Point3? best = null;
        var bestDistanceSquared = float.MaxValue;
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                if (terrain.GetChunkAtCell(center.X + dx, center.Z + dz) is null)
                {
                    continue;
                }

                for (var dy = -verticalRadius; dy <= verticalRadius; dy++)
                {
                    var y = center.Y + dy;
                    if (y is < 1 or > 255)
                    {
                        continue;
                    }

                    var value = terrain.GetCellValue(center.X + dx, y, center.Z + dz);
                    if (BlocksManager.Blocks[Terrain.ExtractContents(value)] is not TBlock)
                    {
                        continue;
                    }

                    var distanceSquared = dx * dx + dy * dy + dz * dz;
                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = distanceSquared;
                        best = new Point3(center.X + dx, y, center.Z + dz);
                    }
                }
            }
        }

        return best;
    }
}

/// <summary>
/// Smelts items by furnace recipe. Requires a furnace block within ~5 blocks and
/// fuel in the inventory; consumes one fuel item per smelt (approximation).
/// </summary>
public sealed class SmeltOrder(string itemName, int count) : GeniusOrder
{
    private const float SecondsPerSmelt = 5f;

    private GeniusCrafting.RecipeMatch? _match;
    private TunnelNavigator? _approach;
    private int _smelted;
    private float _progress;

    protected override float TimeoutSeconds => 300f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null)
        {
            return "error: I have no inventory";
        }

        if (!CraftOrder.IsBlockNearby<FurnaceBlock>(brain, 5))
        {
            if (_approach is null)
            {
                var furnace = CraftOrder.FindNearestBlock<FurnaceBlock>(brain, 32);
                if (furnace is null)
                {
                    return "error: no furnace within ~32 blocks — place one or lead me to one";
                }

                _approach = new TunnelNavigator(
                    new Vector3(furnace.Value.X + 0.5f, furnace.Value.Y + 0.5f, furnace.Value.Z + 0.5f),
                    allowDigging: false,
                    arriveDistance: 4f);
            }

            switch (_approach.Tick(brain, dt))
            {
                case NavStatus.Failed:
                    return $"error: cannot reach the furnace ({_approach.FailureReason})";
                case NavStatus.Running:
                    return null;
            }

            _approach = null;
        }

        if (_match is null)
        {
            _match = GeniusCrafting.FindRecipe(brain, inventory, itemName, furnaceRecipes: true);
            if (_match is null)
            {
                return $"error: I don't know a smelting recipe for '{itemName}'";
            }

            if (!GeniusCrafting.InventorySatisfies(inventory, _match.Needs))
            {
                return $"error: missing ingredients — needs " +
                    GeniusCrafting.DescribeNeeds(brain, _match.Needs) + " per smelt";
            }

            if (FindFuelSlot(brain, inventory, _match.Recipe.RequiredHeatLevel) < 0)
            {
                return "error: I have no fuel hot enough (coal, wood, planks...)";
            }
        }

        _progress += dt;
        if (_progress < SecondsPerSmelt)
        {
            return null;
        }

        _progress = 0f;
        if (!GeniusCrafting.InventorySatisfies(inventory, _match.Needs))
        {
            return Summary(brain) + "; ran out of ingredients";
        }

        var fuelSlot = FindFuelSlot(brain, inventory, _match.Recipe.RequiredHeatLevel);
        if (fuelSlot < 0)
        {
            return Summary(brain) + "; ran out of fuel";
        }

        inventory.RemoveSlotItems(fuelSlot, 1);
        GeniusCrafting.ConsumeNeeds(inventory, _match.Needs);
        GeniusCrafting.DeliverResult(brain, inventory, _match.Recipe.ResultValue, _match.Recipe.ResultCount);
        _smelted += _match.Recipe.ResultCount;
        return _smelted >= Math.Max(1, count) ? Summary(brain) : null;
    }

    private string Summary(ComponentGeniusBrain brain)
    {
        if (_match is null || _smelted == 0)
        {
            return "smelted nothing";
        }

        var name = GeniusInventoryOps.ItemName(brain, _match.Recipe.ResultValue);
        return $"smelted {name} x{_smelted}";
    }

    private static int FindFuelSlot(ComponentGeniusBrain brain, IInventory inventory, float requiredHeat)
    {
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            if (value == 0 || inventory.GetSlotCount(slot) <= 0)
            {
                continue;
            }

            var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            if (block.GetFuelHeatLevel(value) >= requiredHeat)
            {
                return slot;
            }
        }

        return -1;
    }
}

/// <summary>Walks to the player and hands over (matching) inventory items.</summary>
public sealed class GiveToPlayerOrder(ComponentBody playerBody, string? nameFilter, int maxCount)
    : GeniusOrder
{
    private const float ReachDistance = 3f;
    private double _nextPathUpdateTime;

    protected override float TimeoutSeconds => 60f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        WalkTowards(brain, playerBody.Position, 2f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var distance = Vector3.Distance(brain.Creature.ComponentBody.Position, playerBody.Position);
        if (distance > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                return "error: cannot reach the player";
            }

            if (brain.m_subsystemTime.GameTime >= _nextPathUpdateTime)
            {
                _nextPathUpdateTime = brain.m_subsystemTime.GameTime + 0.5;
                WalkTowards(brain, playerBody.Position, 2f);
            }

            return null;
        }

        brain.m_componentPathfinding.Stop();
        var inventory = brain.Miner.Inventory;
        var playerInventory = playerBody.Entity.FindComponent<ComponentInventory>();
        if (inventory is null || playerInventory is null)
        {
            return "error: missing inventory";
        }

        var moved = GeniusInventoryOps.MoveItems(brain, inventory, playerInventory, nameFilter, maxCount);
        return GeniusInventoryOps.DescribeMoved(
            moved,
            "handed to player",
            "I had nothing (matching) to give, or the player's inventory is full");
    }
}

/// <summary>Chases and melee-attacks the named creature until it dies or escapes.</summary>
public sealed class AttackOrder(ComponentCreature target) : GeniusOrder
{
    private const float StrikeRange = 2.2f;
    private const float GiveUpRange = 30f;
    private double _nextPathUpdateTime;

    protected override float TimeoutSeconds => 60f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        WalkTowards(brain, target.ComponentBody.Position, 1.5f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var model = brain.Creature.ComponentCreatureModel;
        if (target.ComponentHealth.Health <= 0f || target.ComponentBody.Entity.Project is null)
        {
            model.AttackOrder = false;
            return $"defeated {target.DisplayName}";
        }

        var targetPosition = target.ComponentBody.Position;
        var myPosition = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(myPosition, targetPosition);
        if (distance > GiveUpRange)
        {
            model.AttackOrder = false;
            return $"error: {target.DisplayName} escaped";
        }

        model.LookAtOrder = target.ComponentCreatureModel.EyePosition;
        if (distance <= StrikeRange)
        {
            brain.m_componentPathfinding.Stop();
            model.AttackOrder = true;
            if (model.IsAttackHitMoment)
            {
                var direction = Vector3.Normalize(targetPosition - myPosition);
                brain.Miner.Hit(
                    target.ComponentBody,
                    targetPosition + new Vector3(0f, 1f, 0f),
                    direction);
            }
        }
        else
        {
            model.AttackOrder = false;
            if (brain.m_componentPathfinding.IsStuck)
            {
                return $"error: cannot reach {target.DisplayName}";
            }

            if (brain.m_subsystemTime.GameTime >= _nextPathUpdateTime)
            {
                _nextPathUpdateTime = brain.m_subsystemTime.GameTime + 0.4;
                WalkTowards(brain, targetPosition, 1.5f);
            }
        }

        return null;
    }
}
