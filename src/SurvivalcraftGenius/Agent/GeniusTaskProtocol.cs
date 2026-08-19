namespace SurvivalcraftGenius.Agent;

/// <summary>
/// Numen's async-task contract in the shape Genius already speaks (prose
/// results, <c>error[slug]</c>, injected markup): accept immediately, finish
/// via an event, never poll. Pure .NET — no game types.
/// </summary>
public static class GeniusTaskProtocol
{
    /// <summary>Tool result for a job that will keep the body busy.</summary>
    public static string Accept(int taskId, string toolName, bool standing = false)
    {
        if (standing)
        {
            return $"accepted task #{taskId} ({toolName}) async=true standing=true. " +
                   "This job has no end and will not send task_finished; " +
                   "a later body action replaces it. Do not poll.";
        }

        return $"accepted task #{taskId} ({toolName}) async=true. " +
               "The body is working in the background. A <event kind=\"task_finished\"> " +
               "arrives by itself when it ends — do not poll, do not re-send this call. " +
               "task_status reads live state; task_stop aborts. A different job next turn replaces it.";
    }

    /// <summary>The injected user-turn payload that wakes the brain.</summary>
    public static string FinishedEvent(int taskId, string toolName, string result)
    {
        var status = StatusOf(result);
        var body = (result ?? "").Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return $"<event kind=\"task_finished\" id=\"{taskId}\" tool=\"{toolName}\" status=\"{status}\">{body}</event>";
    }

    public static string StatusOf(string? result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return "done";
        }

        if (result.Contains("stopped on request", StringComparison.Ordinal))
        {
            return "stopped";
        }

        if (result.Contains("error[timeout]", StringComparison.Ordinal))
        {
            return "timeout";
        }

        if (result.Contains("error[superseded]", StringComparison.Ordinal))
        {
            return "interrupted";
        }

        return result.StartsWith("error[", StringComparison.Ordinal) ? "failed" : "done";
    }
}
