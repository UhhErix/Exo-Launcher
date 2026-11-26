using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class RuneHostTrimTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(100u, 100, 200, true)]
    [InlineData(200u, 100, 200, true)]
    [InlineData(300u, 100, 200, false)]
    [InlineData(0u, 100, 0, false)]
    [InlineData(0u, 0, 0, false)]
    public void ExoIsInFrontOnlyForItsHostOrItsUiProcess(uint foreground, int host, int ui, bool expected) =>
        Assert.Equal(expected, RuneHostTrim.IsInFront(foreground, host, ui));

    [Theory]
    [InlineData(59, false, false, false)]
    [InlineData(60, false, false, true)]
    [InlineData(14, false, true, false)]
    [InlineData(15, false, true, true)]
    [InlineData(9, true, false, false)]
    [InlineData(10, true, false, true)]
    public void ExoStepsBackOnlyAfterTheGraceForItsState(int secondsAway, bool hidden, bool lean, bool expected) =>
        Assert.Equal(expected, RuneHostTrim.Away(Now - TimeSpan.FromSeconds(secondsAway), Now, hidden, lean));

    [Fact]
    public void ExoInFrontIsNeverAway() =>
        Assert.False(RuneHostTrim.Away(null, Now, hidden: true, lean: true));

    [Theory]
    [InlineData(true, true, true, true, 5)]
    [InlineData(false, true, true, false, 15)]
    [InlineData(false, true, false, false, 5)]
    [InlineData(false, false, false, true, 30)]
    [InlineData(false, true, false, true, 30)]
    public void TheLoopComesOftenWhileSomethingChangesAndRarelyWhileInFront(bool settling, bool lean, bool steppedBack, bool inFront, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RuneHostTrim.NextInterval(settling, lean, steppedBack, inFront));

    [Fact]
    public void TheUiProcessIsTrimmedOnlyWhileItsWindowIsHiddenAndAway()
    {
        var ui = new ManagedProcessIdentity(4243, 1);
        ManagedProcessGroup[] groups = [new("exo-ui", [ui])];
        Assert.Empty(ManagedProcessResources.TrimTargets(groups, new HashSet<int>(), new HostedTrimPolicy()));
        Assert.Contains(ui, ManagedProcessResources.TrimTargets(groups, new HashSet<int>(), new HostedTrimPolicy(ShellHidden: true)));
        Assert.Contains(ui, ManagedProcessResources.TrimTargets(groups, new HashSet<int>(), new HostedTrimPolicy(ShellHidden: true, LiveAudio: true)));
    }

    [Fact]
    public void ADisposableHostManagesOnlyItselfAndItsUi()
    {
        var host = new ManagedProcessIdentity(4242, 1);
        var ui = new ManagedProcessIdentity(4243, 2);
        using var forced = HostMode.ForceForTest(true);
        var groups = RuneHostTrim.ManagedGroups(host, ui);
        Assert.Equal(new[] { "exo", "exo-ui" }, groups.Select(group => group.Key).ToArray());
        Assert.Equal(host, Assert.Single(groups[0].Processes));
        Assert.Equal(ui, Assert.Single(groups[1].Processes));
    }

    [Fact]
    public void AMissingUiLeavesAnEmptyUiGroup()
    {
        using var forced = HostMode.ForceForTest(true);
        var groups = RuneHostTrim.ManagedGroups(new ManagedProcessIdentity(4242, 1), null);
        Assert.Empty(groups.Single(group => group.Key == "exo-ui").Processes);
    }

    [Fact]
    public void AnOrdinaryHostAlsoManagesTheStoreClientHelpers()
    {
        using var forced = HostMode.ForceForTest(false);
        var groups = RuneHostTrim.ManagedGroups(new ManagedProcessIdentity(4242, 1), new ManagedProcessIdentity(4243, 2));
        Assert.Equal(new[] { "exo", "exo-ui" }, groups.Take(2).Select(group => group.Key).ToArray());
    }

    [Fact]
    public void DisposingStopsTheLoopAtOnceAndTouchesNothing()
    {
        var trim = new RuneHostTrim(Environment.ProcessId);
        trim.Start();
        var started = System.Diagnostics.Stopwatch.StartNew();
        trim.Dispose();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TheRuneSessionStartsTheTrimWithItsUiAndStopsItInItsFinallyBlock()
    {
        var text = File.ReadAllText(Repo.PathOf("ExoLauncher", "Services", "RuneHostSession.cs"));
        var run = text[text.IndexOf("internal static int Run(Options options)", StringComparison.Ordinal)..];
        var started = run.IndexOf("trim = new RuneHostTrim(uiProcess.Id);", StringComparison.Ordinal);
        Assert.True(started >= 0);
        Assert.True(run.IndexOf("trim.Start();", started, StringComparison.Ordinal) > started);
        Assert.True(run.IndexOf("trim?.Dispose();", run.IndexOf("finally", StringComparison.Ordinal), StringComparison.Ordinal) > 0);
    }
}
