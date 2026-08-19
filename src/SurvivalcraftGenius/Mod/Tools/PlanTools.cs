using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>
/// The companion's own bookkeeping. Needs no body — a plan is worth keeping
/// even before the companion is summoned.
/// </summary>
public static class PlanTools
{
    public static Task<string> TodoWrite(GeniusToolContext context, JObject arguments)
    {
        if (arguments["todos"] is not JArray todos)
        {
            return Task.FromResult(
                "error[invalid_argument]: give the complete list as `todos`");
        }

        var items = new List<PlanItem>();
        foreach (var entry in todos.OfType<JObject>())
        {
            var content = (string?)entry["content"];
            if (string.IsNullOrWhiteSpace(content))
            {
                return Task.FromResult(
                    "error[invalid_argument]: every step needs a non-empty `content`");
            }

            items.Add(new PlanItem(
                content.Trim(), GeniusPlan.ParseStatus((string?)entry["status"])));
        }

        if (context.Player.Plan.Replace(items) is { } rejection)
        {
            return Task.FromResult(rejection);
        }

        if (items.Count == 0)
        {
            return Task.FromResult("plan cleared");
        }

        var done = items.Count(item => item.Status == PlanStatus.Completed);
        var running = items.FirstOrDefault(item => item.Status == PlanStatus.InProgress);
        return Task.FromResult(
            $"plan saved: {done}/{items.Count} done" +
            (running is null
                ? ". Nothing is in_progress — start the next step"
                : $", now on \"{running.Content}\""));
    }

    /// <summary>
    /// What the body is doing right now. Exists so the model can ASK instead of
    /// re-dispatching to find out — re-dispatching was how three houses got
    /// restarted from zero.
    /// </summary>
    public static Task<string> TaskStatus(GeniusToolContext context, JObject arguments)
    {
        if (context.BrainOrNull is not { } brain)
        {
            return Task.FromResult("not summoned, so nothing is running");
        }

        if (brain.CurrentOrder is not { } order)
        {
            return Task.FromResult(brain.IsFollowing
                ? "no task running — I am following the player"
                : "no task running — the body is free");
        }

        var seconds = brain.GameTime - order.StartedAt;
        return Task.FromResult(
            $"task #{order.TaskId} ({order.GetType().Name}) has been running {seconds:0}s. " +
            "Wait for its task_finished — do not poll and do not re-dispatch. " +
            "task_stop aborts it if you want the body back now");
    }

    public static Task<string> TaskStop(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(context.BrainOrNull is { } brain
            ? brain.StopCurrentOrder()
            : "not summoned, so nothing to stop");
}
