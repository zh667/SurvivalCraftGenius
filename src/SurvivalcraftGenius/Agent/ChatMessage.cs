namespace SurvivalcraftGenius.Agent;

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One entry of the OpenAI-style conversation. Role is "system", "user",
/// "assistant" (optionally carrying tool calls) or "tool" (a tool result).
/// </summary>
public sealed class ChatMessage
{
    public required string Role { get; init; }

    public string Content { get; init; } = "";

    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Set on role "tool": which call this message answers.</summary>
    public string ToolCallId { get; init; } = "";

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };

    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    public static ChatMessage Assistant(string content, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls ?? [] };

    public static ChatMessage ToolResult(string toolCallId, string content) =>
        new() { Role = "tool", Content = content, ToolCallId = toolCallId };
}
