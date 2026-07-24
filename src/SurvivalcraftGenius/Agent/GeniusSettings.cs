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

    /// <summary>Upper bound of LLM round-trips per user message.</summary>
    public int MaxToolSteps { get; set; } = 8;

    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>Per-tool completion timeout (goto/dig can take a while).</summary>
    public int ToolTimeoutSeconds { get; set; } = 120;

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
