// The three-priority loop, the action cooldown and the state timeout are
// ported from 工具人 1.1 (ComponentGuardFarmer) by 基岩, used with the author's
// permission — see docs/ATTRIBUTION.md.
// Changes: the decision half is separated from the engine so it can be tested
// on Linux; the flat 10s timeout is combined with our approach retry rather
// than replacing it; the mode carries the parameters an LLM set for it (what
// to plant, how far to range, when to stop), which is the whole difference
// between this and a button the player has to toggle.

namespace SurvivalcraftGenius.Npc;

/// <summary>What the standing mode wants to do next. In priority order.</summary>
public enum FarmAction
{
    /// <summary>Nothing to do — hold still and wait for a crop to ripen.</summary>
    Idle,

    /// <summary>Something is on the ground within reach; grab it.</summary>
    PickUp,

    /// <summary>A ripe crop is in range; harvest it.</summary>
    Harvest,

    /// <summary>Bare farmland and seeds in the bag; plant.</summary>
    Plant,
}

/// <summary>
/// One tick's worth of world facts the decision needs. Keeping this a plain
/// record is what makes the priority rule testable without a game running.
/// </summary>
public readonly record struct FarmSnapshot(
    bool PickableInRange,
    bool RipeCropInRange,
    bool BareFarmlandInRange,
    bool HasSeeds,
    bool InventoryFull,
    bool UnderAttack);

/// <summary>
/// A standing order to keep a field: pick up drops, harvest what is ripe,
/// replant what is bare, forever, at no token cost.
///
/// <para>This is the largest saving available to us. One maintenance round —
/// scan, harvest, collect, plant — costs four-plus LLM steps at ~13k tokens
/// each and has to be walked again every time the player comes back. 工具人
/// does the same work for free because it never asks anyone. The difference we
/// keep is that the LLM sets the mode up from one sentence — what to plant,
/// how far to range, when to stop — instead of the player toggling a button.</para>
/// </summary>
public static class GeniusFarmMode
{
    /// <summary>Seconds between actions, so the body does not thrash every frame.</summary>
    public const float ActionCooldownSeconds = 0.2f;

    /// <summary>A step that makes no progress for this long is abandoned.</summary>
    public const float StateTimeoutSeconds = 10f;

    /// <summary>How far the mode looks for work, in blocks.</summary>
    public const int DefaultRadius = 12;

    /// <summary>
    /// The priority rule, ported verbatim in spirit from TryStartNewTask:
    /// drops first (they despawn), then ripe crops (they can be trampled or
    /// rot), then replanting (never urgent).
    ///
    /// <para>Combat outbids all of it — the body cannot farm while something is
    /// biting it, and 工具人 handles that by simply yielding whenever its chase
    /// behaviour has a target.</para>
    /// </summary>
    public static FarmAction Decide(FarmSnapshot world)
    {
        if (world.UnderAttack)
        {
            return FarmAction.Idle;
        }

        if (world.PickableInRange && !world.InventoryFull)
        {
            return FarmAction.PickUp;
        }

        if (world.RipeCropInRange)
        {
            return FarmAction.Harvest;
        }

        if (world.BareFarmlandInRange && world.HasSeeds)
        {
            return FarmAction.Plant;
        }

        return FarmAction.Idle;
    }

    /// <summary>
    /// Why the mode is idle, in the player's language. An idle standing order
    /// that cannot say why is indistinguishable from a broken one — and the
    /// answer is usually something the player can fix in one action.
    /// </summary>
    public static string ExplainIdle(FarmSnapshot world)
    {
        if (world.UnderAttack)
        {
            return "正在应付袭击,打完再回来干活";
        }

        if (world.InventoryFull && world.PickableInRange)
        {
            return "背包满了,地上的东西捡不动——收走一些我再继续";
        }

        if (world.BareFarmlandInRange && !world.HasSeeds)
        {
            return "有空耕地但我没种子了——给我点种子就接着种";
        }

        return "田里没有熟的、也没有空地要种,我先守着";
    }
}
