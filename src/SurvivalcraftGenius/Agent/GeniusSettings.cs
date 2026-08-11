using Newtonsoft.Json;

namespace SurvivalcraftGenius.Agent;

/// <summary>
/// Device-level LLM backend configuration. Stored as plain JSON outside the world
/// save so the API key never travels with worlds or network packets.
/// </summary>
public sealed class GeniusSettings
{
    public string ApiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "deepseek-chat";

    /// <summary>Optional extra text appended to the built-in system prompt.</summary>
    public string SystemPromptExtra { get; set; } = "";

    /// <summary>
    /// Prompt caching. Every agent step re-sends the same system prompt and tool
    /// schemas (~6k tokens); with caching on, the provider bills those at a
    /// fraction. "auto" marks them only for Claude-family models — OpenAI caches
    /// automatically and needs no marker, and an unknown gateway might reject one.
    /// "on" forces the markers, "off" never sends them.
    /// </summary>
    public string PromptCache { get; set; } = PromptCacheAuto;

    public const string PromptCacheAuto = "auto";
    public const string PromptCacheOn = "on";
    public const string PromptCacheOff = "off";

    /// <summary>Whether this request should carry cache_control markers.</summary>
    public bool UsePromptCache => PromptCache?.Trim().ToLowerInvariant() switch
    {
        PromptCacheOn => true,
        PromptCacheOff => false,
        _ => Model.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || Model.Contains("anthropic", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Tool calls allowed per budget round (auto-extended up to 2x).</summary>
    public int MaxToolSteps { get; set; } = 32;

    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>Per-tool completion timeout (goto/dig can take a while).</summary>
    public int ToolTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Who keeps their items on death. Server-side rule: it is enforced at the
    /// engine's death moment, so it covers every player — including ones who
    /// join later — without any per-player setup.
    /// "off" = vanilla, "companion" = only the Genius NPC, "all" = everyone.
    /// </summary>
    public string KeepInventoryOnDeath { get; set; } = KeepInventoryCompanion;

    public const string KeepInventoryOff = "off";
    public const string KeepInventoryCompanion = "companion";
    public const string KeepInventoryAll = "all";

    /// <summary>Normalized value; unknown strings fall back to companion-only.</summary>
    public string KeepInventoryMode => KeepInventoryOnDeath?.Trim().ToLowerInvariant() switch
    {
        KeepInventoryOff => KeepInventoryOff,
        KeepInventoryAll => KeepInventoryAll,
        _ => KeepInventoryCompanion,
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public string ChatCompletionsUrl =>
        ApiBaseUrl.TrimEnd('/') + "/chat/completions";

    public static GeniusSettings FromJson(string json)
    {
        return JsonConvert.DeserializeObject<GeniusSettings>(json) ?? new GeniusSettings();
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.Indented);
    }
}
