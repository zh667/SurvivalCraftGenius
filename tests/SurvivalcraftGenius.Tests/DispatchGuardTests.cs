using SurvivalcraftGenius.Mod.Tools;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// Numen setTask: refuse a second body job in the SAME turn (the model fired
/// twice without waiting). A later turn always replaces. Playtest 16's
/// deadlock was "any second dispatch refused WHILE the first call was still
/// awaiting" — after async accept, the first call returns immediately, so a
/// same-turn refusal no longer freezes the companion for the rest of the reply.
/// </summary>
public class DispatchGuardTests
{
    [Fact]
    public void ASecondDispatchInTheSameTurnIsRefused()
    {
        Assert.True(GeniusToolContext.IsSameTurnDispatch(runningTurn: 7, currentTurn: 7));
    }

    [Fact]
    public void ALaterTurnReplaces()
    {
        Assert.False(GeniusToolContext.IsSameTurnDispatch(runningTurn: 7, currentTurn: 8));
    }

    [Fact]
    public void NothingRunningIsAlwaysAllowed()
    {
        Assert.False(GeniusToolContext.IsSameTurnDispatch(runningTurn: 0, currentTurn: 7));
    }

    [Fact]
    public void WorkOutsideAnyTurnIsNeverBlocked()
    {
        Assert.False(GeniusToolContext.IsSameTurnDispatch(runningTurn: 0, currentTurn: 0));
    }
}
