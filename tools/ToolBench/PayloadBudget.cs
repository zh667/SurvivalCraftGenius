using System.Globalization;
using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.ToolBench;

/// <summary>
/// Measures what every single agent step pays for before it says anything:
/// the system prompt and the tool schemas are re-sent verbatim on each request,
/// so they are multiplied by the step count of the whole task.
///
/// Run with <c>--budget</c>. No API key needed — it only serializes the payload.
/// </summary>
public static class PayloadBudget
{
    /// <summary>Measured against the relay, 2026-08-11: CJK costs ~4x ASCII.</summary>
    public const double CjkTokensPerChar = 1.90;

    public const double OtherTokensPerChar = 0.44;

    /// <summary>
    /// JSON is far denser than prose — `{"type":"integer"}` is a handful of
    /// tokens, not a token every two characters. Fitted from the measured tool
    /// block: 12,171 chars → 1,990 tokens. Using the prose coefficient here
    /// overstated the schemas 2.7x and sent me optimising the wrong block.
    /// </summary>
    public const double JsonTokensPerChar = 0.164;

    /// <summary>
    /// Tokens every request pays before our first character — measured at 4,394 /
    /// 4,396 / 4,396 for gpt-5.4-mini / 5.6-sol / 5.4 with a two-word body, so it
    /// is the gateway's, not ours. Nothing we write can shrink it; only taking
    /// fewer steps can.
    /// </summary>
    public const int GatewayBaselineTokens = 4395;

    /// <summary>
    /// Token count for mixed Chinese/JSON text. There is no tokenizer in this
    /// repo, so the coefficients were fitted by posting known strings to the
    /// relay and reading prompt_tokens back:
    ///
    ///   system="hi"          → 4,394   (the baseline above)
    ///   +4,500 ASCII chars   → 6,393   ⇒ 0.44 tok/char
    ///   +4,725 CJK chars     → 13,393  ⇒ 1.90 tok/char
    ///
    /// The first cut of this used 0.75/0.30 and was wrong in BOTH directions at
    /// once: it undercounted the Chinese system prompt by 2.5x and overcounted
    /// the English tool schemas by 1.8x, which pointed the optimisation work at
    /// the wrong block. Numbers are relay-specific; re-fit if the backend changes.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        var cjk = text.Count(c => c is >= '　' and <= '鿿' or >= '＀' and <= '￯');
        return (int)Math.Round(cjk * CjkTokensPerChar + (text.Length - cjk) * OtherTokensPerChar);
    }

    /// <summary>JSON-density variant of <see cref="EstimateTokens"/>.</summary>
    public static int EstimateJsonTokens(string text) =>
        (int)Math.Round(text.Length * JsonTokensPerChar);

    /// <summary>
    /// Writes the exact wire payload (system prompt + tool schemas) so it can be
    /// posted to the provider and the real prompt_tokens read back. Our own
    /// count is an estimate; this is how it gets calibrated against truth.
    /// </summary>
    public static void Dump(ToolRegistry registry, string path)
    {
        var payload = LlmClient.BuildPayload(
            [ChatMessage.System(GeniusAgent.DefaultSystemPrompt), ChatMessage.User("说一个字")],
            registry.Tools,
            "measure");
        File.WriteAllText(path, payload.ToString(Newtonsoft.Json.Formatting.None));
        Console.WriteLine($"payload written to {path}");
    }

    public static void Report(ToolRegistry registry)
    {
        var payload = LlmClient.BuildPayload([], registry.Tools, "measure");
        var toolsJson = payload["tools"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
        var indented = payload["tools"]?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "";
        var prompt = GeniusAgent.DefaultSystemPrompt;

        var promptTokens = EstimateTokens(prompt);
        var toolTokens = EstimateJsonTokens(toolsJson);
        var fixedTokens = promptTokens + toolTokens;

        Console.WriteLine("每一步请求的固定开销(系统提示词 + 工具 schema,逐步重发):");
        Console.WriteLine($"  system prompt   {prompt.Length,7} chars  ~{promptTokens,6} tok");
        Console.WriteLine($"  tool schemas    {toolsJson.Length,7} chars  ~{toolTokens,6} tok   ({registry.Tools.Count} 个工具)");
        Console.WriteLine($"  固定小计                        ~{fixedTokens,6} tok/步");
        Console.WriteLine($"  + 中转站基线(与我们无关,删不掉)  {GatewayBaselineTokens,6} tok/步");
        Console.WriteLine($"  = 每步实际计费                  ~{fixedTokens + GatewayBaselineTokens,6} tok/步");
        Console.WriteLine($"  (若 JSON 缩进发送:tool schemas ~{EstimateJsonTokens(indented)} tok — 白花 " +
            $"{EstimateJsonTokens(indented) - toolTokens} tok/步)");
        Console.WriteLine();

        Console.WriteLine("最贵的工具 schema:");
        foreach (var tool in registry.Tools
                     .Select(t => (t.Name,
                         Tokens: EstimateTokens(t.Description) + EstimateJsonTokens(t.ParametersJsonSchema)))
                     .OrderByDescending(t => t.Tokens)
                     .Take(8))
        {
            Console.WriteLine($"  {tool.Name,-18} ~{tool.Tokens,4} tok");
        }

        Console.WriteLine();
        Console.WriteLine("按 40 步(实测盖一间房的量级)折算,仅固定开销:");
        var inputTokens = 40L * (fixedTokens + GatewayBaselineTokens);
        Console.WriteLine($"  {inputTokens} tok 输入");
        foreach (var (name, usdPerMillion) in new[]
                 {
                     ("claude-opus-5 @claude组", 1.050),
                     ("gpt-5.6-sol   @gpt组", 1.750),
                     ("gpt-5.4-mini  @gpt组", 0.2625),
                 })
        {
            var usd = inputTokens / 1_000_000.0 * usdPerMillion;
            Console.WriteLine($"  {name,-24} ${usd.ToString("F3", CultureInfo.InvariantCulture)}" +
                $"  ¥{(usd * 7.3).ToString("F2", CultureInfo.InvariantCulture)}");
        }
    }
}
