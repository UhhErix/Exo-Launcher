using ExoLauncher.Services;
using static ExoLauncher.Services.UpdateFlow;

namespace ExoLauncher.Tests;

/// <summary>
/// The update's state table (UpdateFlow). Ways it could fail: a row that does not land where it says; a state with no way out
/// (an update stuck "downloading" or "waiting" forever); a row that applies an update while a game runs; a state that can never
/// be reached, so the code that handles it is dead; a step the table does not list moving the state anyway.
/// </summary>
public sealed class UpdateFlowTests
{
    [Fact]
    public void EveryRowLandsWhereItSays()
    {
        foreach (var row in Table)
            foreach (var game in row.Game is { } only ? [only] : new[] { false, true })
                Assert.Equal(row.To, Next(row.From, row.On, game));
    }

    [Fact]
    public void EveryStateHasAWayOut()
    {
        foreach (var state in Enum.GetValues<State>())
            Assert.True(Table.Any(row => row.From == state && row.To != state), $"{state} has no way out");
    }

    [Fact]
    public void NothingIsAppliedWhileAGameRuns()
    {
        foreach (var row in Table.Where(row => row.To == State.Applying))
            Assert.False(row.Game ?? true, $"{row.From} + {row.On} applies an update whatever a game is doing");
        foreach (var state in Enum.GetValues<State>())
            foreach (var step in Enum.GetValues<Step>())
                if (state != State.Applying) Assert.NotEqual(State.Applying, Next(state, step, gameRunning: true));
    }

    [Fact]
    public void EveryStateIsReachedFromIdle()
    {
        var seen = new HashSet<State> { State.Idle };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var row in Table.Where(row => seen.Contains(row.From)))
                grew |= seen.Add(row.To);
        }
        Assert.Equal(Enum.GetValues<State>().ToHashSet(), seen);
    }

    [Fact]
    public void AStepWithNoRowLeavesTheStateAsItIs()
    {
        foreach (var state in Enum.GetValues<State>())
            foreach (var step in Enum.GetValues<Step>())
                foreach (var game in new[] { false, true })
                    if (!Table.Any(row => row.From == state && row.On == step && (row.Game is null || row.Game == game)))
                        Assert.Equal(state, Next(state, step, game));
    }

    [Fact]
    public void ADownloadFinishedDuringAGameWaitsThenIsReadyThenGoesIn()
    {
        var state = Next(State.Idle, Step.Install, gameRunning: true);
        Assert.Equal(State.Downloading, state);
        state = Next(state, Step.Verified, gameRunning: true);
        Assert.Equal(State.WaitingForGame, state);
        Assert.Equal(State.WaitingForGame, Next(state, Step.Apply, gameRunning: true));
        state = Next(state, Step.GameEnded, gameRunning: false);
        Assert.Equal(State.Ready, state);
        Assert.Equal(State.Restarting, Next(Next(state, Step.Apply, gameRunning: false), Step.InstallerStarted, gameRunning: false));
    }

    [Fact]
    public void TheStateNamesTheHostSendsAreTheOnesThePageKnows()
    {
        // ui/src/lib/updatePhase.ts › UpdateState lists the same names; the page refuses any other.
        Assert.Equal(["idle", "checking", "downloading", "waitingForGame", "ready", "applying", "restarting", "failed"],
            Enum.GetValues<State>().Select(WebHostBridge.UpdateStateName));
    }

    [Fact]
    public void UpdateNowDuringACheckInstallsAndOnceTheInstallerRunsStartsNothingMore()
    {
        Assert.Equal(State.Downloading, Next(State.Checking, Step.Install, gameRunning: false));
        Assert.Equal(State.Restarting, Next(State.Restarting, Step.Install, gameRunning: false));
        Assert.Equal(State.Idle, Next(State.Restarting, Step.Exited, gameRunning: false));
    }

    [Fact]
    public void ACheckThatCannotAnswerGoesBackToIdleNotFailed()
    {
        Assert.Equal(State.Idle, Next(State.Checking, Step.CheckFailed, gameRunning: false));
        Assert.DoesNotContain(Table, row => row.On == Step.CheckFailed && row.To == State.Failed);
    }
}