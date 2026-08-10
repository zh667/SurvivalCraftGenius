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
/// HOW IT KEEPS THINGS — and why it must NOT set Skip=true (v0.9.4):
/// ComponentHealth's death block does four things together. Skipping it does
/// cancel the drops, but it also skips `DeathTime = TotalElapsedGameTime`, and
/// the corpse cleanup one line below reads
/// `TotalElapsedGameTime - DeathTime > CorpseDuration` on a `double?` that is
/// still null — a null comparison is always false, so the body NEVER despawns.
/// ComponentBehaviorSelector then picks no behavior at all while Health == 0,
/// leaving every behavior IsActive=false. That is exactly the playtest
/// symptom: the companion sat at 0 HP for 20 minutes, still able to chat but
/// answering every order with "endangered".
/// So instead of skipping the death, we EMPTY the inventories first and let
/// the engine's own death path run in full: the vanilla drop pass then finds
/// nothing to drop, DeathTime is set, the corpse despawns, and the player's
/// entity is saved normally.
///
/// On experience: death drops no XP orbs, but SurvivalCraftModLoader.OnPlayerDead
/// runs `Level = max(floor(Level / 2), 1)` — the level is HALVED, permanently.
/// Our own OnPlayerDead hook cannot see the pre-death value (the game's loader
/// registers first and therefore halves first), so the snapshot is taken here,
/// at the death moment, a frame before PlayerData's state machine reaches
/// "PlayerDead".
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
    /// Runs at the death moment, before the vanilla drop pass. Empties whatever
    /// the rule protects so the drop pass has nothing left to scatter, and
    /// always returns false — the engine's death sequence must run to the end
    /// (see the class comment).
    /// </summary>
    public static bool ShouldKeepInventory(ComponentHealth componentHealth)
    {
        if (componentHealth is null || CommonLib.WorkType == WorkType.Client)
        {
            return false;
        }

        var entity = componentHealth.Entity;
        if (entity is null || Mode == GeniusSettings.KeepInventoryOff)
        {
            return false;
        }

        if (entity.FindComponent<Npc.ComponentGeniusBrain>() is { } brain)
        {
            var kept = brain.StashCarriedItems();
            Engine.Log.Information(
                $"[Genius] keep-inventory: companion died, {kept} items kept for the next summon.");
            return false;
        }

        if (Mode == GeniusSettings.KeepInventoryAll
            && entity.FindComponent<ComponentPlayer>() is { } componentPlayer)
        {
            // Emptying is not optional: the engine destroys the player entity
            // and respawn builds a fresh empty one, so anything still in the
            // slots at this point is gone either way. Stash it for the respawn.
            RecordLevelAtDeath(componentPlayer.PlayerData);
            StashPlayerInventory(componentPlayer);
            Engine.Log.Information("[Genius] keep-inventory: player died, belongings stashed for respawn.");
        }

        return false;
    }

    /// <summary>
    /// Level recorded at death, per player GUID. Death halves the level in
    /// SurvivalCraftModLoader.OnPlayerDead; in "all" mode we put it back on
    /// respawn. Costs nothing when nothing was lost.
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

    /// <summary>
    /// Snapshots the pre-halving level. Called from the death moment, not from
    /// our OnPlayerDead hook: hooks dispatch in registration order and the
    /// game's own loader registers first, so by the time we see OnPlayerDead
    /// the level has already been halved.
    /// </summary>
    public static void RecordLevelAtDeath(PlayerData? playerData)
    {
        if (playerData is not null && Mode == GeniusSettings.KeepInventoryAll)
        {
            LevelsAtDeath[playerData.PlayerGUID] = playerData.Level;
        }
    }

    /// <summary>
    /// Returns the restored level when a loss was repaired, else null. Called
    /// twice — right after the death (so the death screen and any level-gated
    /// UI already read the right number) and again on respawn, whichever wins.
    /// </summary>
    public static float? RestoreLevel(PlayerData? playerData, bool consume)
    {
        if (playerData is null
            || Mode != GeniusSettings.KeepInventoryAll
            || !LevelsAtDeath.TryGetValue(playerData.PlayerGUID, out var levelAtDeath))
        {
            return null;
        }

        if (consume)
        {
            LevelsAtDeath.TryRemove(playerData.PlayerGUID, out _);
        }

        if (playerData.Level >= levelAtDeath)
        {
            return null;
        }

        playerData.Level = levelAtDeath;
        return levelAtDeath;
    }
}
