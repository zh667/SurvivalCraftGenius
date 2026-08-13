using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The durable plan. Its whole job is to be trustworthy as memory, so the
/// tests are about the two ways a list stops being trustworthy: claiming two
/// jobs at once, and un-finishing finished work.
/// </summary>
public class GeniusPlanTests
{
    private static PlanItem Step(string content, PlanStatus status = PlanStatus.Pending) =>
        new(content, status);

    [Fact]
    public void AnEmptyPlanDescribesAsNothing()
    {
        Assert.Equal("", new GeniusPlan().Describe());
        Assert.True(new GeniusPlan().IsEmpty);
    }

    [Fact]
    public void ReplaceSwapsTheWholeList()
    {
        var plan = new GeniusPlan();
        Assert.Null(plan.Replace([Step("找地方"), Step("盖房")]));
        Assert.Null(plan.Replace([Step("挖矿")]));

        Assert.Equal(["挖矿"], plan.Items.Select(item => item.Content));
    }

    [Fact]
    public void OnlyOneStepMayBeInProgress()
    {
        var plan = new GeniusPlan();

        var error = plan.Replace([
            Step("挖矿", PlanStatus.InProgress),
            Step("盖房", PlanStatus.InProgress),
        ]);

        Assert.NotNull(error);
        Assert.Contains("one body", error);
        Assert.True(plan.IsEmpty);
    }

    /// <summary>
    /// The exact failure that produced the circling: on "continue", re-sending
    /// the old list with everything reset, and redoing finished work forever.
    /// </summary>
    [Fact]
    public void CompletedWorkCannotBeReset()
    {
        var plan = new GeniusPlan();
        plan.Replace([Step("整地", PlanStatus.Completed), Step("盖房", PlanStatus.InProgress)]);

        var error = plan.Replace([Step("整地"), Step("盖房", PlanStatus.InProgress)]);

        Assert.NotNull(error);
        Assert.Contains("整地", error);
        // The good list survives the bad update.
        Assert.Equal(PlanStatus.Completed, plan.Items[0].Status);
    }

    [Fact]
    public void CompletedWorkMayStillBeCancelled()
    {
        var plan = new GeniusPlan();
        plan.Replace([Step("整地", PlanStatus.Completed)]);

        Assert.Null(plan.Replace([Step("整地", PlanStatus.Cancelled)]));
    }

    /// <summary>Dropping a finished step is how a plan legitimately shrinks.</summary>
    [Fact]
    public void CompletedWorkMayBeDroppedEntirely()
    {
        var plan = new GeniusPlan();
        plan.Replace([Step("整地", PlanStatus.Completed), Step("盖房")]);

        Assert.Null(plan.Replace([Step("盖房", PlanStatus.InProgress)]));
    }

    [Fact]
    public void AnOverlongPlanIsRefused()
    {
        var plan = new GeniusPlan();
        var many = Enumerable.Range(0, GeniusPlan.MaxItems + 1)
            .Select(i => Step($"第{i}步")).ToList();

        Assert.NotNull(plan.Replace(many));
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void DescribeShowsProgressAndMarksTheRunningStep()
    {
        var plan = new GeniusPlan();
        plan.Replace([
            Step("整地", PlanStatus.Completed),
            Step("盖房", PlanStatus.InProgress),
            Step("插火把"),
        ]);

        var text = plan.Describe();

        Assert.Contains("1/3 已完成", text);
        Assert.Contains("[x] 整地", text);
        Assert.Contains("[>] 盖房", text);
        Assert.Contains("[ ] 插火把", text);
    }

    [Fact]
    public void SurvivesARoundTripThroughJson()
    {
        var plan = new GeniusPlan();
        plan.Replace([Step("整地", PlanStatus.Completed), Step("盖房", PlanStatus.InProgress)]);

        var restored = new GeniusPlan();
        restored.Restore(plan.ToJson());

        Assert.Equal(plan.Describe(), restored.Describe());
    }

    [Fact]
    public void RestoreIgnoresJunkAndMissingContent()
    {
        var plan = new GeniusPlan();
        plan.Restore(JArray.Parse("""[{"status":"completed"},{"content":"  "},{"content":"盖房"}]"""));

        Assert.Single(plan.Items);
        Assert.Equal("盖房", plan.Items[0].Content);
        // Unknown or absent status is the safe default, not "already done".
        Assert.Equal(PlanStatus.Pending, plan.Items[0].Status);
    }

    [Theory]
    [InlineData("in_progress", PlanStatus.InProgress)]
    [InlineData("COMPLETED", PlanStatus.Completed)]
    [InlineData("canceled", PlanStatus.Cancelled)]
    [InlineData("cancelled", PlanStatus.Cancelled)]
    [InlineData("nonsense", PlanStatus.Pending)]
    [InlineData(null, PlanStatus.Pending)]
    public void StatusParsingToleratesTheModelsSpelling(string? raw, PlanStatus expected)
    {
        Assert.Equal(expected, GeniusPlan.ParseStatus(raw));
    }
}
