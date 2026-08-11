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
    /// <summary>
    /// Rough token count for mixed Chinese/JSON text. There is no tokenizer in
    /// this repo, so this is a calibrated approximation (CJK ≈ 0.75 tok/char,
    /// everything else ≈ 0.30) — good to about ±20%, which is enough to compare
    /// a before and an after.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        var cjk = text.Count(c => c is >= '　' and <= '鿿' or >= '＀' and <= '￯');
        return (int)Math.Round(cjk * 0.75 + (text.Length - cjk) * 0.30);
    }

    public static void Report(ToolRegistry registry)
    {
        var payload = LlmClient.BuildPayload([], registry.Tools, "measure");
        var toolsJson = payload["tools"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
        var indented = payload["tools"]?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "";
        var prompt = GeniusAgent.DefaultSystemPrompt;

        var promptTokens = EstimateTokens(prompt);
        var toolTokens = EstimateTokens(toolsJson);
        var fixedTokens = promptTokens + toolTokens;

        Console.WriteLine("每一步请求的固定开销(系统提示词 + 工具 schema,逐步重发):");
        Console.WriteLine($"  system prompt   {prompt.Length,7} chars  ~{promptTokens,6} tok");
        Console.WriteLine($"  tool schemas    {toolsJson.Length,7} chars  ~{toolTokens,6} tok   ({registry.Tools.Count} 个工具)");
        Console.WriteLine($"  固定小计                        ~{fixedTokens,6} tok/步");
        Console.WriteLine($"  (若 JSON 缩进发送:tool schemas ~{EstimateTokens(indented)} tok — 白花 " +
            $"{EstimateTokens(indented) - toolTokens} tok/步)");
        Console.WriteLine();

        Console.WriteLine("最贵的工具 schema:");
        foreach (var tool in registry.Tools
                     .Select(t => (t.Name, Tokens: EstimateTokens(t.Description) + EstimateTokens(t.ParametersJsonSchema)))
                     .OrderByDescending(t => t.Tokens)
                     .Take(8))
        {
            Console.WriteLine($"  {tool.Name,-18} ~{tool.Tokens,4} tok");
        }

        Console.WriteLine();
        Console.WriteLine("按 40 步(实测盖一间房的量级)折算,仅固定开销:");
        var inputTokens = 40L * fixedTokens;
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
