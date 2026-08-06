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
/// Note: this build has no death XP/level penalty at all (PlayerData.Level is
/// only ever raised by ComponentLevel or loaded from the save), so "keep
/// experience" needs no code — dying never costs levels.
/// </summary>
public static class GeniusKeepInventory
{
    /// <summary>Set from the settings store when a world loads (server side).</summary>
    public static string Mode { get; set; } = GeniusSettings.KeepInventoryCompanion;

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
            return true;
        }

        return mode == GeniusSettings.KeepInventoryAll
            && entity.FindComponent<ComponentPlayer>() is not null;
    }
}
