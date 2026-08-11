namespace SurvivalcraftGenius.Npc;

/// <summary>
/// The armour arithmetic, lifted from <c>ComponentClothing.ApplyArmorProtection</c>
/// so it can be tested without the engine.
///
/// The engine only ever runs that method for entities carrying a
/// <c>ComponentClothing</c>, and that component does
/// <c>FindComponent&lt;ComponentPlayer&gt;(throwOnError: true)</c> — so a
/// non-player creature can never have one. Hence our own copy, applied from the
/// AttackBody hook.
/// </summary>
public static class GeniusArmorMath
{
    /// <summary>Which body part a hit lands on. The engine's own distribution.</summary>
    public const float FeetThreshold = 0.1f;
    public const float LegsThreshold = 0.3f;
    public const float TorsoThreshold = 0.9f;

    /// <summary>Feet 10%, legs 20%, torso 60%, head 10%.</summary>
    public static GeniusArmorSlot SlotForRoll(float roll) => roll switch
    {
        < FeetThreshold => GeniusArmorSlot.Feet,
        < LegsThreshold => GeniusArmorSlot.Legs,
        < TorsoThreshold => GeniusArmorSlot.Torso,
        _ => GeniusArmorSlot.Head,
    };

    /// <summary>
    /// How much of the blow this piece can still stop. A worn-out piece protects
    /// less: the cap scales with remaining durability, so armour degrades in
    /// effectiveness rather than dropping to zero the moment it breaks.
    /// </summary>
    public static float AbsorbCapacity(float sturdiness, int damage, float maxDurability)
    {
        if (maxDurability <= 0f)
        {
            return 0f;
        }

        var remaining = (maxDurability - damage) / maxDurability;
        return Math.Max(0f, remaining) * sturdiness;
    }

    /// <summary>
    /// Absorbed damage, capped both by the piece's protection rating and by what
    /// its remaining durability can still take.
    /// </summary>
    public static float Absorbed(float attackPower, float armorProtection, float capacity)
    {
        var byRating = attackPower * Math.Clamp(armorProtection, 0f, 1f);
        return Math.Max(0f, Math.Min(byRating, capacity));
    }

    /// <summary>
    /// Durability points spent absorbing that much. The fractional remainder is
    /// the probability of one extra point — the caller rolls it.
    /// </summary>
    public static float DurabilityCost(float absorbed, float sturdiness, float maxDurability)
    {
        if (sturdiness <= 0f)
        {
            return 0f;
        }

        return (absorbed / sturdiness * maxDurability) + 0.001f;
    }
}

public enum GeniusArmorSlot
{
    Head,
    Torso,
    Legs,
    Feet,
}
