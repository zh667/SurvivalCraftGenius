using Engine;
using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The lead-and-drop solver. Rather than assert the exact numbers the port
/// happens to produce, these fly the arrow: take the aim point, simulate the
/// parabola, and check it actually meets the target. That way the tests still
/// mean something if the solver is ever rewritten.
/// </summary>
public class BallisticsTests
{
    private const float Speed = 28f;   // the engine's arrow speed

    /// <summary>
    /// Fires at the aim point and returns how close the arrow passes to the
    /// moving target — the only question that matters.
    /// </summary>
    private static float ClosestApproach(
        Vector3 launch, Vector3 targetStart, Vector3 targetVelocity, Vector3 aim, float speed)
    {
        var direction = Vector3.Normalize(aim - launch);
        var velocity = direction * speed;
        var best = float.MaxValue;
        const float step = 0.005f;
        for (var t = 0f; t < 8f; t += step)
        {
            var arrow = launch + (velocity * t)
                + (0.5f * new Vector3(0f, -GeniusBallistics.Gravity, 0f) * t * t);
            var target = targetStart + (targetVelocity * t);
            best = Math.Min(best, Vector3.Distance(arrow, target));
        }

        return best;
    }

    [Fact]
    public void AStationaryTargetIsHit()
    {
        var launch = new Vector3(0f, 64f, 0f);
        var target = new Vector3(20f, 64f, 0f);

        var aim = GeniusBallistics.AimPoint(launch, target, Vector3.Zero, Speed);

        Assert.NotNull(aim);
        Assert.True(ClosestApproach(launch, target, Vector3.Zero, aim!.Value, Speed) < 0.5f);
    }

    /// <summary>Aiming straight at a distant target undershoots — that is the drop.</summary>
    [Fact]
    public void TheSolverAimsHigherThanTheTargetAtRange()
    {
        var launch = new Vector3(0f, 64f, 0f);
        var target = new Vector3(35f, 64f, 0f);

        var aim = GeniusBallistics.AimPoint(launch, target, Vector3.Zero, Speed)!.Value;

        Assert.True(aim.Y > target.Y, $"aim Y {aim.Y} should clear target Y {target.Y}");
        Assert.True(ClosestApproach(launch, target, Vector3.Zero, aim, Speed) < 0.5f);
        // Aiming flat at the same target misses low.
        Assert.True(ClosestApproach(launch, target, Vector3.Zero, target, Speed) > 1f);
    }

    /// <summary>The duck problem: a bird crossing at speed needs lead, not luck.</summary>
    [Fact]
    public void ACrossingBirdIsLed()
    {
        var launch = new Vector3(0f, 64f, 0f);
        var bird = new Vector3(18f, 70f, 0f);
        var flight = new Vector3(0f, 0f, 6f);

        var aim = GeniusBallistics.AimPoint(launch, bird, flight, Speed);

        Assert.NotNull(aim);
        Assert.True(aim!.Value.Z > 1f, "the aim point must lead the bird along its flight");
        Assert.True(ClosestApproach(launch, bird, flight, aim.Value, Speed) < 0.8f);
    }

    [Fact]
    public void AClimbingBirdIsStillHit()
    {
        var launch = new Vector3(0f, 64f, 0f);
        var bird = new Vector3(12f, 68f, 4f);
        var flight = new Vector3(2f, 3f, -1f);

        var aim = GeniusBallistics.AimPoint(launch, bird, flight, Speed);

        Assert.NotNull(aim);
        Assert.True(ClosestApproach(launch, bird, flight, aim!.Value, Speed) < 0.8f);
    }

    /// <summary>
    /// Null is a real answer, not a failure: it tells the caller to close in
    /// rather than spend arrows on a shot that cannot land.
    /// </summary>
    [Fact]
    public void AnUnreachableTargetReturnsNull()
    {
        var launch = new Vector3(0f, 64f, 0f);

        // Far beyond the range of a 28 m/s arrow.
        Assert.Null(GeniusBallistics.AimPoint(
            launch, new Vector3(400f, 64f, 0f), Vector3.Zero, Speed));

        // Outrunning the arrow.
        Assert.Null(GeniusBallistics.AimPoint(
            launch, new Vector3(20f, 64f, 0f), new Vector3(0f, 0f, 60f), Speed));
    }

    [Fact]
    public void AZeroSpeedProjectileHasNoSolution()
    {
        Assert.Null(GeniusBallistics.AimPoint(
            Vector3.Zero, new Vector3(10f, 0f, 0f), Vector3.Zero, 0f));
    }

    /// <summary>Without gravity it degenerates to plain interception.</summary>
    [Fact]
    public void WithoutGravityItSolvesPureInterception()
    {
        var launch = new Vector3(0f, 64f, 0f);
        var target = new Vector3(20f, 64f, 0f);
        var velocity = new Vector3(0f, 0f, 5f);

        var aim = GeniusBallistics.AimPoint(launch, target, velocity, Speed, gravity: 0f);

        Assert.NotNull(aim);
        Assert.Equal(64f, aim!.Value.Y, 3);
        Assert.True(aim.Value.Z > 1f);
    }

    [Fact]
    public void ATargetStraightOverheadIsHandled()
    {
        var launch = new Vector3(0f, 64f, 0f);
        var target = new Vector3(0f, 78f, 0f);

        var aim = GeniusBallistics.AimPoint(launch, target, Vector3.Zero, Speed);

        Assert.NotNull(aim);
        Assert.True(ClosestApproach(launch, target, Vector3.Zero, aim!.Value, Speed) < 0.6f);
    }
}
