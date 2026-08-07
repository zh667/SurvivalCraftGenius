using System.Xml.Linq;
using Game;

namespace SurvivalcraftGenius.Mod;

public sealed class GeniusModLoader : ModLoader
{
    public override void OnXdbLoad(XElement database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var injected = false;
        Entity.GetFiles(".netxdb", (_, stream) =>
        {
            try
            {
                var source = XElement.Load(stream, LoadOptions.None);
                GeniusDatabaseInjector.Inject(source, database);
                injected = true;
                Engine.Log.Information("[Genius] Entity templates injected.");
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or FormatException)
            {
                Engine.Log.Warning($"[Genius] Database injection failed: {exception.Message}");
            }
        });

        if (!injected)
        {
            Engine.Log.Warning("[Genius] mod.netxdb missing or rejected; the companion is unavailable.");
        }
    }

    /// <summary>
    /// Engine hook at the death moment (ComponentHealth, server side). Skip=true
    /// cancels the vanilla drop pass — this is what makes keep-inventory apply
    /// to every player, including ones who join later.
    /// </summary>
    public override void DeadBeforeDrops(Game.ComponentHealth componentHealth, out bool Skip)
    {
        Skip = false;
        try
        {
            Skip = GeniusKeepInventory.ShouldKeepInventory(componentHealth);
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] keep-inventory check failed: {exception.Message}");
        }
    }

    /// <summary>Snapshots the level so a respawn can restore it (keep-all mode).</summary>
    public override void OnPlayerDead(Game.PlayerData playerData)
    {
        try
        {
            GeniusKeepInventory.RecordLevelAtDeath(playerData);
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] level snapshot failed: {exception.Message}");
        }
    }

    /// <summary>Restores the pre-death level if respawn came back lower.</summary>
    public override bool OnPlayerSpawned(
        Game.PlayerData.SpawnMode spawnMode, Game.ComponentPlayer componentPlayer, Engine.Vector3 position)
    {
        try
        {
            if (spawnMode != Game.PlayerData.SpawnMode.Respawn)
            {
                return false;
            }

            var restoredItems = GeniusKeepInventory.RestoreInventoryAfterRespawn(componentPlayer);
            if (restoredItems > 0)
            {
                Engine.Log.Information($"[Genius] keep-inventory: restored {restoredItems} items after respawn.");
                componentPlayer.ComponentGui?.DisplaySmallMessage(
                    "死亡不掉落:背包和装备已归还",
                    Engine.Color.White,
                    blinking: false,
                    playNotificationSound: false);
            }

            if (GeniusKeepInventory.RestoreLevelAfterRespawn(componentPlayer) is { } level)
            {
                Engine.Log.Information($"[Genius] keep-inventory: restored player level {level:0.##} after respawn.");
            }
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] level restore failed: {exception.Message}");
        }

        return false;
    }

    public override void __ModInitialize()
    {
        // Manual registration is mandatory: the game's auto-registration of
        // mod packages never fires (IsSubclassOf on an interface).
        GeniusNetwork.TryRegisterPackage();
        // Hook overrides are DEAD CODE until registered: ModsManager.HookAction
        // only walks loaders registered for that hook name (ModsManager.cs:156).
        // v0.9.0 shipped the override without this line, so keep-inventory
        // silently did nothing.
        ModsManager.RegisterHook("DeadBeforeDrops", this);
        ModsManager.RegisterHook("OnPlayerDead", this);
        ModsManager.RegisterHook("OnPlayerSpawned", this);
        var version = typeof(GeniusModLoader).Assembly.GetName().Version?.ToString(3) ?? "?";
        Engine.Log.Information($"[Genius] Mod initialized (v{version}).");
    }
}
