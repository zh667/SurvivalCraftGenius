namespace SurvivalcraftGenius.Agent;

/// <summary>
/// Structured failure taxonomy for tool results (Numen's FailureType lesson):
/// every failure the model sees carries a stable machine-readable category in
/// front of the human-teaching prose, so prompts can route by category, code
/// can count failures, and the future tool benchmark can assert on categories
/// instead of prose.
/// </summary>
public enum FailureType
{
    // 自愈类 — try a different approach yourself (route/spot/timing).
    /// <summary>No viable route / blocked / boxed in / knocked off course.</summary>
    NoPath,

    /// <summary>Terrain not loaded here yet; wait or move closer first.</summary>
    AreaNotLoaded,

    /// <summary>No matching block/creature/chest/recipe/waypoint (did-you-mean attached).</summary>
    NotFound,

    /// <summary>The target existed but got away or vanished mid-task.</summary>
    TargetLost,

    // 先决条件类 — acquire the prerequisite, then retry.
    /// <summary>Not enough ingredients or fuel.</summary>
    MissingMaterial,

    /// <summary>No crafting table / furnace in range.</summary>
    MissingStation,

    /// <summary>Digging this needs a better tool than anything carried.</summary>
    ToolTooWeak,

    // 参数类 — fix the call itself and retry.
    /// <summary>Bad/missing arguments, unknown tool, invalid slot index.</summary>
    InvalidArgument,

    /// <summary>The coordinate/item can't serve this action (air, occupied, unplaceable).</summary>
    InvalidTarget,

    /// <summary>Right goal, wrong tool — e.g. the item is smelted, not crafted.</summary>
    WrongMethod,

    // 状态类 — read the message and follow its instruction.
    /// <summary>Self-preservation (fleeing) outbid the order for the body.</summary>
    Endangered,

    /// <summary>The NPC died on the job.</summary>
    Died,

    /// <summary>The companion is not in the world right now.</summary>
    NotSummoned,

    /// <summary>A newer order/instruction replaced this one.</summary>
    Superseded,

    /// <summary>The order's deadline ran out (partial progress may be attached).</summary>
    Timeout,

    /// <summary>The same call was repeated verbatim too many times.</summary>
    LoopDetected,

    /// <summary>An optional capability (mod/knowledge folder) is absent.</summary>
    Unavailable,

    /// <summary>Unexpected exception — a bug, not a world condition.</summary>
    Internal,
}

public static class GeniusFailure
{
    /// <summary>Builds the canonical failure string: <c>error[slug]: message</c>.</summary>
    public static string Format(FailureType type, string message) =>
        $"error[{Slug(type)}]: {message}";

    public static string Slug(FailureType type) => type switch
    {
        FailureType.NoPath => "no_path",
        FailureType.AreaNotLoaded => "area_not_loaded",
        FailureType.NotFound => "not_found",
        FailureType.TargetLost => "target_lost",
        FailureType.MissingMaterial => "missing_material",
        FailureType.MissingStation => "missing_station",
        FailureType.ToolTooWeak => "tool_too_weak",
        FailureType.InvalidArgument => "invalid_argument",
        FailureType.InvalidTarget => "invalid_target",
        FailureType.WrongMethod => "wrong_method",
        FailureType.Endangered => "endangered",
        FailureType.Died => "died",
        FailureType.NotSummoned => "not_summoned",
        FailureType.Superseded => "superseded",
        FailureType.Timeout => "timeout",
        FailureType.LoopDetected => "loop_detected",
        FailureType.Unavailable => "unavailable",
        FailureType.Internal => "internal",
        _ => "internal",
    };

    /// <summary>True when the tool result signals a failure (tagged or legacy prose).</summary>
    public static bool IsError(string result) =>
        result.StartsWith("error", StringComparison.Ordinal);

    /// <summary>
    /// Extracts the failure category from a tool result. Also finds a tag
    /// embedded mid-string ("mined 3 blocks; error[timeout]: …") so partial
    /// successes still get counted. Untagged or unknown → null.
    /// </summary>
    public static FailureType? TryParse(string result)
    {
        var start = result.IndexOf("error[", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += "error[".Length;
        var end = result.IndexOf(']', start);
        if (end < 0)
        {
            return null;
        }

        var slug = result[start..end];
        foreach (var type in Enum.GetValues<FailureType>())
        {
            if (Slug(type) == slug)
            {
                return type;
            }
        }

        return null;
    }
}
