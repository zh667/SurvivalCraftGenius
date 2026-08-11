using Engine;
using Game;
using GameEntitySystem;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Armour for the companion, worn straight out of its inventory.
///
/// The engine's own armour path (<c>ComponentMiner.AttackBody</c> →
/// <c>ComponentClothing.ApplyArmorProtection</c>) is closed to us: that
/// component resolves a <c>ComponentPlayer</c> with throwOnError, so no creature
/// can carry one. Rather than build a parallel clothing store with its own slots,
/// save format and UI, we treat any clothing the companion is carrying as worn —
/// the best piece per body part. Putting a chestplate in its bag is putting it on.
///
/// The trade-off is honest: the armour does not show on the model, because the
/// model reads ComponentOuterClothingModel, which reads ComponentClothing.
/// </summary>
public static class GeniusArmor
{
    /// <summary>ClothingBlock — the one block that carries ClothingData.</summary>
    private const int ClothingContents = 203;

    /// <summary>Engine convention: durability + 1 is the scale everything divides by.</summary>
    private static float MaxDurability =>
        BlocksManager.Blocks[ClothingContents].Durability + 1;

    /// <summary>
    /// Reduces an incoming blow by what the carried armour stops, spends the
    /// matching durability, and destroys pieces that wear out. Returns the
    /// damage that got through.
    /// </summary>
    public static float ApplyProtection(
        IInventory inventory, float attackPower, Engine.Random random, Vector3 position, Project project)
    {
        if (attackPower <= 0f)
        {
            return attackPower;
        }

        var slot = GeniusArmorMath.SlotForRoll(random.Float(0f, 1f));
        var maxDurability = MaxDurability;
        var remaining = attackPower;

        // Layered pieces stack: each one takes its bite out of what is left.
        foreach (var index in SlotsWearing(inventory, slot))
        {
            var value = inventory.GetSlotValue(index);
            var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            var clothing = block.GetClothingData(Terrain.ExtractData(value));
            if (clothing is null || clothing.Sturdiness <= 0f)
            {
                continue;
            }

            var capacity = GeniusArmorMath.AbsorbCapacity(
                clothing.Sturdiness, block.GetDamage(value), maxDurability);
            var absorbed = GeniusArmorMath.Absorbed(remaining, clothing.ArmorProtection, capacity);
            if (absorbed <= 0f)
            {
                continue;
            }

            remaining -= absorbed;
            var cost = GeniusArmorMath.DurabilityCost(absorbed, clothing.Sturdiness, maxDurability);
            var points = (int)MathUtils.Floor(cost)
                + (random.Bool(MathUtils.Remainder(cost, 1f)) ? 1 : 0);
            if (points > 0)
            {
                Wear(inventory, index, value, points, position, project);
            }

            if (!string.IsNullOrEmpty(clothing.ImpactSoundsFolder))
            {
                project.FindSubsystem<SubsystemAudio>()?.PlayRandomSound(
                    clothing.ImpactSoundsFolder, 1f, random.Float(-0.3f, 0.3f), position, 4f, 0.15f);
            }

            if (remaining <= 0f)
            {
                break;
            }
        }

        var stopped = attackPower - Math.Max(remaining, 0f);
        if (stopped > 0.01f)
        {
            // Falsifiable evidence in Game.log: the armour renders nowhere and
            // absorbs silently, so without this a playtest cannot tell "armour
            // worked" from "armour did nothing".
            Log.Information(
                $"[Genius] armor {slot} absorbed {stopped:0.##} of {attackPower:0.##}");
        }

        return Math.Max(remaining, 0f);
    }

    /// <summary>
    /// Inventory slots holding clothing for this body part, best protection
    /// first. Everything carried counts — the companion has no separate "worn"
    /// state to get out of sync with its bag.
    /// </summary>
    public static IEnumerable<int> SlotsWearing(IInventory inventory, GeniusArmorSlot slot)
    {
        var found = new List<(int Index, float Protection)>();
        for (var index = 0; index < inventory.SlotsCount; index++)
        {
            if (inventory.GetSlotCount(index) <= 0)
            {
                continue;
            }

            var value = inventory.GetSlotValue(index);
            var contents = Terrain.ExtractContents(value);
            if (contents != ClothingContents)
            {
                continue;
            }

            var clothing = BlocksManager.Blocks[contents].GetClothingData(Terrain.ExtractData(value));
            if (clothing is not null && (int)clothing.Slot == (int)slot)
            {
                found.Add((index, clothing.ArmorProtection));
            }
        }

        return found.OrderByDescending(entry => entry.Protection).Select(entry => entry.Index);
    }

    /// <summary>Best armour the companion is carrying, for the status line.</summary>
    public static string Describe(IInventory inventory)
    {
        var parts = new List<string>();
        foreach (var slot in Enum.GetValues<GeniusArmorSlot>())
        {
            var index = SlotsWearing(inventory, slot).Cast<int?>().FirstOrDefault();
            if (index is not { } worn)
            {
                continue;
            }

            var value = inventory.GetSlotValue(worn);
            var block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            parts.Add(block.GetDisplayName(null, value));
        }

        return parts.Count == 0 ? "无护甲" : string.Join("/", parts);
    }

    private static void Wear(
        IInventory inventory, int index, int value, int points, Vector3 position, Project project)
    {
        var damaged = BlocksManager.DamageItem(value, points);
        if (Terrain.ExtractContents(damaged) != ClothingContents)
        {
            // Worn through: it is gone, and the player should see it go.
            inventory.RemoveSlotItems(index, 1);
            project.FindSubsystem<SubsystemParticles>()?.AddParticleSystem(
                new BlockDebrisParticleSystem(
                    project.FindSubsystem<SubsystemTerrain>(throwOnError: true),
                    position, 1f, 1f, Color.White, 0));
            return;
        }

        inventory.RemoveSlotItems(index, 1);
        inventory.AddSlotItems(index, damaged, 1);
    }
}
