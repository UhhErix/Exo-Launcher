using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a followed Steam download can end wrongly in the Library:
/// - a failure is published and then overwritten by Idle at once, so the reason Steam gave never shows;
/// - a completed follow is published as Idle without Completed, so the library never re-reads the new state;
/// - another game's id is published as this one's ending.
/// </summary>
public sealed class SteamFollowEndingTests
{
    [Fact]
    public void AFailedFollowStaysFailedWithSteamsReason()
    {
        var ending = LaunchOrchestrator.FollowEnding("steam:570", new SteamFollowResult(SteamFollowOutcome.Failed, "Steam stopped before the move finished."));

        // The last published record is what the Library shows: it must still be Failed, with Steam's reason, never Idle.
        var last = ending[^1];
        Assert.Equal(InstallPhase.Failed, last.Phase);
        Assert.Equal("Steam stopped before the move finished.", last.Status);
        Assert.Equal("steam:570", last.GameId);
        Assert.DoesNotContain(ending, progress => progress.Phase == InstallPhase.Idle);
    }

    [Fact]
    public void ACompletedFollowIsCompletedThenIdle()
    {
        var ending = LaunchOrchestrator.FollowEnding("steam:570", new SteamFollowResult(SteamFollowOutcome.Completed));

        Assert.Equal([InstallPhase.Completed, InstallPhase.Idle], ending.Select(progress => progress.Phase).ToArray());
        Assert.All(ending, progress => Assert.Equal("steam:570", progress.GameId));
    }

    [Fact]
    public void AFollowThatMadeNoChangeEndsIdle()
    {
        var ending = LaunchOrchestrator.FollowEnding("steam:570", new SteamFollowResult(SteamFollowOutcome.Idle));
        Assert.Equal([InstallPhase.Idle], ending.Select(progress => progress.Phase).ToArray());
    }
}
