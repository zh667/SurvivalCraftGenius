using Game;
using Game.NetWork;
using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// Server-side keep-inventory rule, enforced at the engine's death moment
/// (ModLoader.DeadBeforeDrops fires inside ComponentHealth when any creature
/// hits 0 HP). Because it intercepts the death event itself rather than
/// tagging players, it automatically covers everyone — including players who
/// join after the server started — and only the server evaluates it, so
/// clients need no matching setting.
///
/// On experience: death drops no XP orbs, but the player's LEVEL does come
/// back lower (playtest-confirmed; level-gated recipes lock up afterwards) —
/// the static sweep found no code that subtracts it, so we snapshot and
/// restore instead of trusting the read.
/// </summary>
public static class GeniusKeepInventory
{
    private static string _mode = GeniusSettings.KeepInventoryCompanion;

    /// <summary>Set from the settings store when a world loads (server side).</summary>
    public static string Mode
    {
        get => _mode;
        set
        {
            if (!string.Equals(_mode, value, StringComparison.Ordinal))
            {
                _mode = value;
                // Logged so a playtest can confirm the rule is live from
                // Game.log alone (v0.9.0 shipped it silently broken).
                Engine.Log.Information($"[Genius] keep-inventory rule = {value}");
            }
        }
    }

    /// <summary>
    /// Returns true when this death should skip the vanilla drop pass.
    /// Companion deaths are stashed by the brain itself; players simply keep
    /// what they carry (inventory and clothing alike).
    /// </summary>
    public static bool ShouldKeepInventory(ComponentHealth componentHealth)
    {
        if (componentHealth is null || CommonLib.WorkType == WorkType.Client)
        {
            return false;
        }

        var entity = componentHealth.Entity;
        if (entity is null)
        {
            return false;
        }

        var mode = Mode;
        if (mode == GeniusSettings.KeepInventoryOff)
        {
            return false;
        }

        if (entity.FindComponent<Npc.ComponentGeniusBrain>() is not null)
        {
            // The brain's own removal handler stashes the inventory for the
            // next summon; skipping the drop pass keeps it from spilling too.
            Engine.Log.Information("[Genius] keep-inventory: companion death, drops skipped.");
            return true;
        }

        if (mode == GeniusSettings.KeepInventoryAll
            && entity.FindComponent<ComponentPlayer>() is { } componentPlayer)
        {
            // Skipping the drop pass is not enough: the engine destroys the
            // player entity and respawn builds a fresh empty one, so the
            // items would simply vanish. Stash them for the respawn.
            StashPlayerInventory(componentPlayer);
            Engine.Log.Information("[Genius] keep-inventory: player death, drops skipped and inventory stashed.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Level recorded at death, per player GUID. The static source read shows
    /// no level-loss code in this build, yet players report losing levels
    /// across a death (level-gated recipes locking up afterwards) — so in
    /// "all" mode we snapshot the level at death and restore it on respawn if
    /// it came back lower. Costs nothing when nothing was lost.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, float> LevelsAtDeath = new();

    /// <summary>Everything a player carried: backpack slots plus worn layers.</summary>
    private sealed class PlayerStash
    {
        public List<(int Slot, int Value, int Count)> Items { get; } = [];

        public Dictionary<ClothingSlot, List<int>> Clothes { get; } = [];

        public int ItemCount => Items.Sum(entry => entry.Count)
            + Clothes.Values.Sum(layers => layers.Count);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid, PlayerStash> InventoriesAtDeath = new();

    /// <summary>
    /// Copies the real carried goods and empties them off the doomed entity.
    /// Deliberately NOT a generic IInventory sweep: the player entity also
    /// carries ComponentCreativeInventory (the whole block palette, 9999 per
    /// slot — that is where the absurd "15968537 items" came from) and
    /// ComponentFurnitureInventory (furniture designs, not belongings).
    /// Clothing needs its own path too: ComponentClothing.AddSlotItems is an
    /// empty method and GetSlotValue only reports the outermost layer, so
    /// worn gear must go through GetClothes/SetClothes.
    /// </summary>
    private static void StashPlayerInventory(ComponentPlayer componentPlayer)
    {
        if (componentPlayer.PlayerData is not { } playerData)
        {
            return;
        }

        var stash = new PlayerStash();
        if (componentPlayer.Entity.FindComponent<ComponentInventory>() is { } inventory)
        {
            for (var slot = 0; slot < inventory.SlotsCount; slot++)
            {
                var value = inventory.GetSlotValue(slot);
                var count = inventory.GetSlotCount(slot);
                if (value != 0 && count > 0)
                {
                    stash.Items.Add((slot, value, count));
                    inventory.RemoveSlotItems(slot, count);
                }
            }
        }

        if (componentPlayer.Entity.FindComponent<ComponentClothing>() is { } clothing)
        {
            foreach (ClothingSlot clothingSlot in Enum.GetValues<ClothingSlot>())
            {
                var layers = clothing.GetClothes(clothingSlot).ToList();
                if (layers.Count > 0)
                {
                    stash.Clothes[clothingSlot] = layers;
                    clothing.SetClothes(clothingSlot, []);
                }
            }
        }

        if (stash.ItemCount > 0)
        {
            InventoriesAtDeath[playerData.PlayerGUID] = stash;
        }
    }

    /// <summary>Puts the stash back into the freshly respawned player entity.</summary>
    public static int RestoreInventoryAfterRespawn(ComponentPlayer? componentPlayer)
    {
        if (componentPlayer?.PlayerData is not { } playerData
            || !InventoriesAtDeath.TryRemove(playerData.PlayerGUID, out var stash))
        {
            return 0;
        }

        var restored = 0;
        if (componentPlayer.Entity.FindComponent<ComponentInventory>() is { } inventory)
        {
            foreach (var (slot, value, count) in stash.Items)
            {
                try
                {
                    if (slot < inventory.SlotsCount && inventory.GetSlotCount(slot) == 0)
                    {
                        inventory.AddSlotItems(slot, value, count);
                        restored += count;
                    }
                    else
                    {
                        // Slot taken by respawn starting gear: put it anywhere.
                        restored += count - ComponentInventoryBase.AcquireItems(inventory, value, count);
                    }
                }
                catch (Exception exception)
                {
                    Engine.Log.Warning($"[Genius] keep-inventory: item restore failed: {exception.Message}");
                }
            }
        }

        if (componentPlayer.Entity.FindComponent<ComponentClothing>() is { } clothing)
        {
            foreach (var (clothingSlot, layers) in stash.Clothes)
            {
                try
                {
                    clothing.SetClothes(clothingSlot, layers);
                    restored += layers.Count;
                }
                catch (Exception exception)
                {
                    Engine.Log.Warning($"[Genius] keep-inventory: clothing restore failed: {exception.Message}");
                }
            }
        }

        return restored;
    }

    public static void RecordLevelAtDeath(PlayerData? playerData)
    {
        if (playerData is not null && Mode == GeniusSettings.KeepInventoryAll)
        {
            LevelsAtDeath[playerData.PlayerGUID] = playerData.Level;
        }
    }

    /// <summary>Returns the restored level when a loss was repaired, else null.</summary>
    public static float? RestoreLevelAfterRespawn(ComponentPlayer? componentPlayer)
    {
        if (componentPlayer?.PlayerData is not { } playerData
            || !LevelsAtDeath.TryRemove(playerData.PlayerGUID, out var levelAtDeath))
        {
            return null;
        }

        if (Mode != GeniusSettings.KeepInventoryAll || playerData.Level >= levelAtDeath)
        {
            return null;
        }

        playerData.Level = levelAtDeath;
        return levelAtDeath;
    }
}
