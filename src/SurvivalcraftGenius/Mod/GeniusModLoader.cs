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
    /// Engine hook at the death moment (ComponentHealth, server side). We empty
    /// the protected inventories here and always leave Skip=false: cancelling
    /// the drop pass also cancels DeathTime, which leaves the corpse undespawnable
    /// and every behavior inactive (see GeniusKeepInventory).
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

    /// <summary>
    /// Undoes the vanilla level halving (SurvivalCraftModLoader.OnPlayerDead
    /// runs Level = max(floor(Level/2), 1)). Only effective if our hook runs
    /// after the game's own — the respawn pass below is the guaranteed one.
    /// </summary>
    public override void OnPlayerDead(Game.PlayerData playerData)
    {
        try
        {
            if (GeniusKeepInventory.RestoreLevel(playerData, consume: false) is { } level)
            {
                Engine.Log.Information($"[Genius] keep-inventory: level kept at {level:0.##} through death.");
            }
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] level restore failed: {exception.Message}");
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

            if (GeniusKeepInventory.RestoreLevel(componentPlayer?.PlayerData, consume: true) is { } level)
            {
                Engine.Log.Information($"[Genius] keep-inventory: restored player level {level:0.##} after respawn.");
                componentPlayer?.ComponentGui?.DisplaySmallMessage(
                    $"死亡不掉落:等级已保留({level:0.##})",
                    Engine.Color.White,
                    blinking: false,
                    playNotificationSound: false);
            }
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] level restore failed: {exception.Message}");
        }

        return false;
    }

    /// <summary>
    /// Engine hook, fired before the vanilla armour step in
    /// ComponentMiner.AttackBody. That step resolves a ComponentClothing, which
    /// a creature can never carry (it requires a ComponentPlayer), so this is
    /// the only place the companion's armour can act.
    /// </summary>
    public override bool AttackBody(
        ComponentBody target,
        ComponentCreature attacker,
        Engine.Vector3 hitPoint,
        Engine.Vector3 hitDirection,
        ref float attackPower,
        bool isMeleeAttack)
    {
        if (target?.Entity.FindComponent<Npc.ComponentGeniusBrain>() is not { } brain
            || brain.Miner?.Inventory is not { } inventory)
        {
            return false;
        }

        attackPower = Npc.GeniusArmor.ApplyProtection(
            inventory, attackPower, s_random, target.Position, target.Project);
        return false;
    }

    private static readonly Engine.Random s_random = new();

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
        ModsManager.RegisterHook("AttackBody", this);
        var version = typeof(GeniusModLoader).Assembly.GetName().Version?.ToString(3) ?? "?";
        Engine.Log.Information($"[Genius] Mod initialized (v{version}).");
    }
}
