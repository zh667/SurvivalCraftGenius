namespace SurvivalcraftGenius.Npc;

/// <summary>
/// When a crop is worth cutting, read out of each block's own
/// <c>GetDropValues</c> rather than copied from another mod.
///
/// The numbers matter because harvesting early is not just slower, it is
/// strictly worse — the plant is gone either way:
///
///  - <b>Rye</b> (RyeBlock.GetDropValues): planted rye drops 1 seed at size 5,
///    1-2 at size 6, and 1-3 <i>grain</i> (data 5, not seed data 4) at size 7,
///    plus a 50% bonus item. Wild rye drops one seed at size &gt; 2 with 33%
///    probability and nothing below that.
///  - <b>Cotton</b> (CottonBlock.GetDropValues): the check is <c>size == 2</c>
///    exactly — nothing at all below it. Planted cotton also gives back 1-2
///    seeds, wild cotton does not.
///  - <b>Pumpkin</b> (BasePumpkinBlock): drops at any size ≥ 1, but
///    GetNutritionalValue returns 0 unless size == 7, so an early pumpkin is
///    food that does not feed anyone.
/// </summary>
public static class GeniusHarvestRules
{
    public const int RyeContents = 174;
    public const int CottonContents = 204;
    public const int PumpkinContents = 131;

    /// <summary>Planted rye reaches grain, not just seed, at this size.</summary>
    public const int RyeRipeSize = 7;

    /// <summary>Below this a wild rye drops nothing at all.</summary>
    public const int WildRyeMinSize = 3;

    /// <summary>Cotton's drop check is an equality, and 2 is its maximum.</summary>
    public const int CottonRipeSize = 2;

    /// <summary>A pumpkin under full size has zero nutritional value.</summary>
    public const int PumpkinRipeSize = 7;

    /// <summary>
    /// Whether cutting this plant now yields its full drop. Engine-free so the
    /// thresholds are testable: callers pass the block contents plus the size
    /// and wild flag already decoded from the cell data.
    /// </summary>
    public static bool IsRipe(int contents, int size, bool isWild) => contents switch
    {
        RyeContents => isWild ? size >= WildRyeMinSize : size >= RyeRipeSize,
        CottonContents => size >= CottonRipeSize,
        PumpkinContents => size >= PumpkinRipeSize,
        _ => false,
    };

    /// <summary>Whether this block is a crop we know how to harvest at all.</summary>
    public static bool IsCrop(int contents) =>
        contents is RyeContents or CottonContents or PumpkinContents;

    /// <summary>Why a crop was left standing — shown back to the player.</summary>
    public static string NotRipeReason(int contents, int size, bool isWild) => contents switch
    {
        RyeContents when isWild => $"野黑麦还太小({size}/{WildRyeMinSize})",
        RyeContents => $"黑麦还没熟({size}/{RyeRipeSize})",
        CottonContents => $"棉花还没熟({size}/{CottonRipeSize})",
        PumpkinContents => $"南瓜还没熟({size}/{PumpkinRipeSize})",
        _ => "不是作物",
    };
}
