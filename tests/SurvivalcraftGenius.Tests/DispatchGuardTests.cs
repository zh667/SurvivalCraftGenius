using SurvivalcraftGenius.Mod.Tools;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The guard against re-sending a long job. Its first version refused any
/// second dispatch in a turn and deadlocked playtest 16: an agent-side timeout
/// left the order alive in the body, so every later tool — a different tool,
/// a different job — was refused for the rest of the turn, and the companion
/// could only ask to be re-summoned. So the tests here care as much about what
/// must NOT be blocked as about what must.
/// </summary>
public class DispatchGuardTests
{
    private const string Build9x9 = "build:9x9@10,64,10";
    private const string Build5x5 = "build:5x5@10,64,10";

    [Fact]
    public void TheSameJobTwiceInOneReplyIsRefused()
    {
        Assert.True(GeniusToolContext.IsDuplicateDispatch(
            Build9x9, runningSignature: Build9x9, runningTurn: 7, currentTurn: 7));
    }

    /// <summary>The deadlock: a different job must always get through.</summary>
    [Fact]
    public void ADifferentJobInTheSameReplyIsAllowed()
    {
        Assert.False(GeniusToolContext.IsDuplicateDispatch(
            Build5x5, runningSignature: Build9x9, runningTurn: 7, currentTurn: 7));
    }

    /// <summary>The player changing their mind later always wins.</summary>
    [Fact]
    public void TheSameJobInALaterTurnIsAllowed()
    {
        Assert.False(GeniusToolContext.IsDuplicateDispatch(
            Build9x9, runningSignature: Build9x9, runningTurn: 7, currentTurn: 8));
    }

    [Fact]
    public void NothingRunningIsAlwaysAllowed()
    {
        Assert.False(GeniusToolContext.IsDuplicateDispatch(
            Build9x9, runningSignature: null, runningTurn: 0, currentTurn: 7));
    }

    /// <summary>
    /// Cheap orders (goto, dig_block) carry no signature: restarting them costs
    /// nothing, so they must never be blocked.
    /// </summary>
    [Fact]
    public void OrdersWithoutASignatureAreNeverBlocked()
    {
        Assert.False(GeniusToolContext.IsDuplicateDispatch(
            null, runningSignature: Build9x9, runningTurn: 7, currentTurn: 7));
        Assert.False(GeniusToolContext.IsDuplicateDispatch(
            Build9x9, runningSignature: null, runningTurn: 7, currentTurn: 7));
    }

    /// <summary>Turn 0 means "no turn has begun" — auto-resume and restores.</summary>
    [Fact]
    public void WorkOutsideAnyTurnIsNeverBlocked()
    {
        Assert.False(GeniusToolContext.IsDuplicateDispatch(
            Build9x9, runningSignature: Build9x9, runningTurn: 0, currentTurn: 0));
    }
}
