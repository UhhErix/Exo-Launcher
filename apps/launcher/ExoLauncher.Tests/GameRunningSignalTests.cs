using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the game running signal (Local\ExoLauncher.GameRunning, read by Exo Cinema and Exo's own background work) can
/// stay set with no game running, each silent (Cinema's upscalers, HDR switching and warm-ups just never run):
/// - the game exits, the exit watch's scan runs inside the session's 12 s exit debounce while the session still counts
///   the game, and nothing scans again once the session closes (seen on 2026-11-19: Rocket League ended at 06:32, the
///   signal stayed set for the rest of the session);
/// - a session left open with no game process (its watch stuck on a helper) holds the signal forever;
/// - the periodic recheck runs during normal play, when every game's exit is watched;
/// - the event outlives an Exo that crashed during a game (a reader held a handle) and opens still set;
/// - Exo closes during a game and leaves its state saying a game runs;
/// - a scan already running when Exo closes (Detach disposes its timer without waiting) publishes after the clear and
///   sets the event again, or an Update and the clear interleave and leave the event set with the state cleared.
/// </summary>
[Collection(ProcessWideSignals.Name)]
public sealed class GameRunningSignalTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static void Reset() => GameRunningSignal.Update(false, GameRunningSignal.Generation, _ => { }, _ => { }, _ => { });

    [Fact]
    public void The_scan_inside_the_exit_debounce_keeps_the_signal_but_schedules_a_recheck()
    {
        // Rocket League's process is gone; the session Exo started is still open for its 12 s debounce.
        Assert.True(RunStatePlan.GameRunning(gameProcessFound: false, sessionOpen: true, sessionOnlyFor: TimeSpan.Zero));
        Assert.Equal(RunStatePlan.FallbackEvery,
            RunStatePlan.PeriodicEvery(eventsAvailable: true, anyRunningGameUnwatched: false, heldBySessionOnly: true));
    }

    [Fact]
    public void The_stuck_case_clears_on_the_scan_after_the_session_closes()
    {
        var signals = new List<bool>();
        void Scan(bool found, bool sessionOpen, TimeSpan sessionOnlyFor) =>
            GameRunningSignal.Update(RunStatePlan.GameRunning(found, sessionOpen, sessionOnlyFor), GameRunningSignal.Generation, _ => { }, _ => { }, signals.Add);
        try
        {
            Scan(found: true, sessionOpen: true, TimeSpan.Zero);       // playing
            Scan(found: false, sessionOpen: true, TimeSpan.Zero);      // exit watch, inside the debounce
            Assert.True(GameRunningSignal.Running);
            Scan(found: false, sessionOpen: false, 13 * Second);       // the scan the session's end now asks for
            Assert.False(GameRunningSignal.Running);
            Assert.Equal([true, false], signals);
        }
        finally { Reset(); }
    }

    [Fact]
    public void A_session_left_open_without_a_game_process_stops_counting_after_the_limit()
    {
        Assert.True(RunStatePlan.GameRunning(false, true, RunStatePlan.SessionOnlyLimit - Second));
        Assert.False(RunStatePlan.GameRunning(false, true, RunStatePlan.SessionOnlyLimit));
        Assert.False(RunStatePlan.GameRunning(false, false, TimeSpan.Zero));
        Assert.True(RunStatePlan.GameRunning(true, false, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Normal_play_with_every_exit_watched_does_not_poll()
    {
        Assert.Null(RunStatePlan.PeriodicEvery(eventsAvailable: true, anyRunningGameUnwatched: false, heldBySessionOnly: false));
    }

    [Fact]
    public void An_event_left_set_by_an_earlier_Exo_opens_cleared()
    {
        var name = @"Local\ExoLauncher.GameRunning.Test." + Guid.NewGuid().ToString("N");
        using var reader = new EventWaitHandle(true, EventResetMode.ManualReset, name); // a reader kept it alive, set
        using var opened = GameRunningSignal.Open(name);
        Assert.NotNull(opened);
        Assert.False(reader.WaitOne(0));
    }

    [Fact]
    public void Closing_clears_the_state_without_creating_the_event()
    {
        // The real event is the installed Exo's; this test never opens it (Clear touches only an event this process made).
        try
        {
            GameRunningSignal.Update(true, GameRunningSignal.Generation, _ => { }, _ => { }, _ => { });
            GameRunningSignal.Clear();
            Assert.False(GameRunningSignal.Running);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task A_scan_in_flight_when_Exo_closes_does_not_set_the_signal_again()
    {
        var signals = new List<bool>();
        using var scanning = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        try
        {
            // The poll read its generation and is mid process scan when the bridge detaches and Exo clears the signal.
            var scan = Task.Run(() =>
            {
                var generation = GameRunningSignal.Generation;
                scanning.Set();
                finish.Wait(TimeSpan.FromSeconds(10));
                return GameRunningSignal.Update(true, generation, _ => { }, _ => { }, signals.Add);
            }, TestContext.Current.CancellationToken);
            Assert.True(scanning.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            GameRunningSignal.Clear();
            finish.Set();
            Assert.False(await scan);
            Assert.False(GameRunningSignal.Running);
            Assert.Empty(signals);
        }
        finally { finish.Set(); Reset(); }
    }

    [Fact]
    public async Task Closing_waits_for_an_update_already_publishing_then_clears()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var update = Task.Run(() => GameRunningSignal.Update(true, GameRunningSignal.Generation, _ =>
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }, _ => { }, _ => { }), TestContext.Current.CancellationToken);
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var clear = Task.Run(GameRunningSignal.Clear, TestContext.Current.CancellationToken);
            var first = await Task.WhenAny(clear, Task.Delay(200, TestContext.Current.CancellationToken));
            Assert.NotSame(clear, first); // held until the update is done
            release.Set();
            Assert.True(await update);
            await clear;
            Assert.False(GameRunningSignal.Running);
        }
        finally { release.Set(); Reset(); }
    }
}
