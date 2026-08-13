using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Agent;

public enum PlanStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled,
}

public sealed record PlanItem(string Content, PlanStatus Status);

/// <summary>
/// The companion's durable plan for a multi-step job.
///
/// <para>Until this existed the agent had nowhere to keep "where am I in this",
/// so every step re-derived it from the conversation. Anything that cut a job
/// short — an interrupt, a preemption, a timeout, a player death — meant coming
/// back and guessing, and a wrong guess restarted the job. In game that reads as
/// walking in circles, which is exactly what playtests 11 through 15
/// complained about.</para>
///
/// <para>Whole-list replacement (Numen's todowrite contract): the model sends
/// the complete list every time, so there is no partial-update protocol to get
/// wrong. This type only enforces the invariants that make the list trustworthy
/// as memory — see <see cref="Replace"/>. Pure .NET, no game types.</para>
/// </summary>
public sealed class GeniusPlan
{
    /// <summary>Long enough for a real job, short enough to stay cheap in the prompt.</summary>
    public const int MaxItems = 20;

    private readonly object _gate = new();
    private List<PlanItem> _items = [];

    public IReadOnlyList<PlanItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Replaces the whole list, returning null on success or a message for the
    /// model when the update is not a legal plan.
    ///
    /// <para>Two invariants, both there because breaking them destroys the
    /// list's value as memory rather than merely looking untidy:</para>
    /// <list type="bullet">
    /// <item><b>At most one in_progress.</b> The body does one job at a time; a
    /// list claiming two is no longer a record of where we are.</item>
    /// <item><b>Completed work never reverts.</b> The failure this prevents is
    /// the specific one that caused the circling: on "continue", re-sending the
    /// old list with everything back to pending, and redoing finished work
    /// forever.</item>
    /// </list>
    /// </summary>
    public string? Replace(IReadOnlyList<PlanItem> items)
    {
        if (items.Count > MaxItems)
        {
            return $"error[invalid_argument]: {items.Count} steps is more than a plan, it is a novel — " +
                   $"keep it under {MaxItems}; split the job or describe phases, not individual tool calls";
        }

        var running = items.Count(item => item.Status == PlanStatus.InProgress);
        if (running > 1)
        {
            return $"error[invalid_argument]: {running} steps are in_progress at once — " +
                   "I only have one body. Exactly one step may be in_progress";
        }

        lock (_gate)
        {
            foreach (var previous in _items.Where(item => item.Status == PlanStatus.Completed))
            {
                var now = items.FirstOrDefault(item =>
                    string.Equals(item.Content, previous.Content, StringComparison.Ordinal));
                if (now is not null && now.Status is not (PlanStatus.Completed or PlanStatus.Cancelled))
                {
                    return $"error[invalid_argument]: '{previous.Content}' was already completed — " +
                           "do not reset finished steps when resuming, or I will redo them forever. " +
                           "Send the list with that one still completed";
                }
            }

            _items = [.. items];
        }

        return null;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items = [];
        }
    }

    /// <summary>
    /// The plan as it rides in the per-turn context, or empty when there is no
    /// plan. Deliberately terse — this is re-sent every turn.
    /// </summary>
    public string Describe()
    {
        var items = Items;
        if (items.Count == 0)
        {
            return "";
        }

        var done = items.Count(item => item.Status == PlanStatus.Completed);
        var lines = items.Select(item => $"{Marker(item.Status)} {item.Content}");
        return $"当前计划({done}/{items.Count} 已完成):\n" + string.Join("\n", lines);
    }

    private static string Marker(PlanStatus status) => status switch
    {
        PlanStatus.Completed => "[x]",
        PlanStatus.InProgress => "[>]",
        PlanStatus.Cancelled => "[-]",
        _ => "[ ]",
    };

    public static PlanStatus ParseStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "in_progress" => PlanStatus.InProgress,
        "completed" => PlanStatus.Completed,
        "cancelled" or "canceled" => PlanStatus.Cancelled,
        _ => PlanStatus.Pending,
    };

    private static string StatusName(PlanStatus status) => status switch
    {
        PlanStatus.InProgress => "in_progress",
        PlanStatus.Completed => "completed",
        PlanStatus.Cancelled => "cancelled",
        _ => "pending",
    };

    public JArray ToJson()
    {
        return new JArray(Items.Select(item => new JObject
        {
            ["content"] = item.Content,
            ["status"] = StatusName(item.Status),
        }));
    }

    public void Restore(JArray? array)
    {
        if (array is null)
        {
            return;
        }

        var items = new List<PlanItem>();
        foreach (var entry in array.OfType<JObject>())
        {
            var content = (string?)entry["content"];
            if (!string.IsNullOrWhiteSpace(content))
            {
                items.Add(new PlanItem(content, ParseStatus((string?)entry["status"])));
            }
        }

        lock (_gate)
        {
            _items = items.Count > MaxItems ? [.. items.Take(MaxItems)] : items;
        }
    }
}
