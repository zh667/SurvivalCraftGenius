// Ported from 工具人 1.1 (ComponentGuardCombat.FireBow / UpdateBowBehavior /
// MaintainRangedDistance) by 基岩, used with the author's permission — see
// docs/ATTRIBUTION.md.
// Changes: aiming goes through GeniusBallistics (ported from 铁器风云) instead
// of pointing straight at the target, because a straight shot cannot hit a
// flying bird; the arrow block is resolved by index rather than by scanning
// BlocksManager on every shot; and everything reports a typed reason so the
// caller can tell "no bow" from "cannot hit this" from "out of arrows".

using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>Why a shot did not happen — each demands a different response.</summary>
public enum ShotOutcome
{
    Fired,

    /// <summary>No bow, or no arrows to load one with.</summary>
    NoWeapon,

    /// <summary>Inside the minimum range, or beyond the maximum.</summary>
    BadRange,

    /// <summary>The ballistics have no solution: the target outruns the arrow.</summary>
    NoSolution,

    /// <summary>Fired recently; the draw is not ready.</summary>
    Cooling,
}

/// <summary>
/// The bow half of combat. The model never chooses this — <c>attack</c> takes
/// only a target, and the body decides how, because range, line of sight and
/// how many arrows are left are all invisible at the moment the tool is called.
/// That contract is 工具人's and Numen's alike, and it is the reason hunting
/// birds needed no new tool.
/// </summary>
public static class GeniusRanged
{
    /// <summary>Engine arrow speed.</summary>
    public const float ArrowSpeed = 28f;

    /// <summary>Below this the bow is the wrong tool — walk up and swing.</summary>
    public const float MinRange = 4f;

    /// <summary>Beyond this the arrow drops out of any useful solution.</summary>
    public const float MaxRange = 30f;

    /// <summary>Seconds between shots, matching the draw animation.</summary>
    public const double CooldownSeconds = 1.5;

    /// <summary>Where the body would rather stand while shooting.</summary>
    public const float PreferredRange = 12f;

    /// <summary>Spread by arrow type, from the ported implementation.</summary>
    private static float SpreadFor(ArrowBlock.ArrowType type) => type switch
    {
        ArrowBlock.ArrowType.WoodenArrow => 0.025f,
        ArrowBlock.ArrowType.StoneArrow => 0.01f,
        _ => 0.02f,
    };

    /// <summary>Is a bow (loaded or not) in the bag at all?</summary>
    public static bool HasBow(IInventory? inventory) => FindBow(inventory) is not null;

    /// <summary>Bow slot, preferring one that is already loaded.</summary>
    public static int? FindBow(IInventory? inventory)
    {
        if (inventory is null)
        {
            return null;
        }

        int? unloaded = null;
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            if (inventory.GetSlotCount(slot) <= 0
                || BlocksManager.Blocks[Terrain.ExtractContents(value)] is not BowBlock)
            {
                continue;
            }

            if (BowBlock.GetArrowType(Terrain.ExtractData(value)).HasValue)
            {
                return slot;
            }

            unloaded ??= slot;
        }

        return unloaded;
    }

    private static int? FindArrow(IInventory inventory)
    {
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            if (inventory.GetSlotCount(slot) > 0
                && BlocksManager.Blocks[
                    Terrain.ExtractContents(inventory.GetSlotValue(slot))] is ArrowBlock)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Take one shot at the target if everything lines up. Loading counts as
    /// progress, not a shot: a bow with no arrow nocked is loaded this tick and
    /// fired the next, exactly as the ported behaviour does.
    /// </summary>
    public static ShotOutcome TryShoot(ComponentGeniusBrain brain, ComponentCreature target)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null || FindBow(inventory) is not { } bowSlot)
        {
            return ShotOutcome.NoWeapon;
        }

        var muzzle = MuzzleOf(brain);
        var aimAt = target.ComponentBody.Position
            + (target.ComponentBody.StanceBoxSize * 0.5f);
        var distance = Vector3.Distance(muzzle, aimAt);
        if (distance < MinRange || distance > MaxRange)
        {
            return ShotOutcome.BadRange;
        }

        var bowValue = inventory.GetSlotValue(bowSlot);
        var bowData = Terrain.ExtractData(bowValue);
        if (BowBlock.GetArrowType(bowData) is not { } nocked)
        {
            return Load(brain, inventory, bowSlot, bowValue, bowData);
        }

        if (brain.GameTime - brain.LastShotTime < CooldownSeconds)
        {
            return ShotOutcome.Cooling;
        }

        // Lead and drop. Aiming straight at a moving bird misses twice over.
        if (GeniusBallistics.AimPoint(
                muzzle, aimAt, target.ComponentBody.Velocity, ArrowSpeed) is not { } aimPoint)
        {
            return ShotOutcome.NoSolution;
        }

        var spread = SpreadFor(nocked);
        var direction = Vector3.Normalize(aimPoint - muzzle);
        var jitter = new Vector3(
            brain.Random.Float(-spread, spread),
            brain.Random.Float(-spread, spread),
            brain.Random.Float(-spread, spread));
        var velocity = brain.Creature.ComponentBody.Velocity
            + ((direction + jitter) * ArrowSpeed);

        var arrowValue = Terrain.MakeBlockValue(
            ArrowBlock.Index, 0, ArrowBlock.SetArrowType(0, nocked));
        if (brain.SubsystemProjectiles.FireProjectile(
                arrowValue, muzzle, velocity, Vector3.Zero, brain.Creature) is null)
        {
            return ShotOutcome.NoSolution;
        }

        // Unload the bow and wear the string.
        var emptied = Terrain.MakeBlockValue(
            Terrain.ExtractContents(bowValue), 0, BowBlock.SetArrowType(bowData, null));
        inventory.RemoveSlotItems(bowSlot, 1);
        inventory.AddSlotItems(bowSlot, emptied, 1);
        brain.Miner.DamageActiveTool(1);
        brain.LastShotTime = brain.GameTime;
        return ShotOutcome.Fired;
    }

    private static ShotOutcome Load(
        ComponentGeniusBrain brain, IInventory inventory, int bowSlot, int bowValue, int bowData)
    {
        if (FindArrow(inventory) is not { } arrowSlot)
        {
            return ShotOutcome.NoWeapon;
        }

        var arrowType = ArrowBlock.GetArrowType(
            Terrain.ExtractData(inventory.GetSlotValue(arrowSlot)));
        var loaded = Terrain.MakeBlockValue(
            Terrain.ExtractContents(bowValue), 0, BowBlock.SetArrowType(bowData, arrowType));
        inventory.RemoveSlotItems(bowSlot, 1);
        inventory.AddSlotItems(bowSlot, loaded, 1);
        inventory.RemoveSlotItems(arrowSlot, 1);
        return ShotOutcome.Cooling;
    }

    /// <summary>Eye height, offset to the right hand — where the arrow leaves.</summary>
    private static Vector3 MuzzleOf(ComponentGeniusBrain brain)
    {
        var matrix = brain.Creature.ComponentBody.Matrix;
        return brain.Creature.ComponentCreatureModel.EyePosition
            + (matrix.Right * 0.3f)
            - (matrix.Up * 0.2f);
    }
}
