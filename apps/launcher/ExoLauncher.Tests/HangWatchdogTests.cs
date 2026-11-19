using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The hang watchdog's decision, tested as a pure function: no report under five seconds; a visible stall over five seconds
/// reports once; a minimized, hidden or covered window never reports; a stall reports once however long it lasts; a stall
/// that ends re-arms, so the next stall reports again.
/// </summary>
public sealed class HangWatchdogTests
{
    [Fact]
    public void UnderFiveSecondsNeverReports()
    {
        var reported = false;
        Assert.False(HangWatchdog.ShouldReport(TimeSpan.FromSeconds(4.9), visible: true, reportedThisStall: false));
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(4.9), visible: true, ref reported));
        Assert.False(reported);
    }

    [Fact]
    public void ExactlyFiveSecondsIsNotYetAStall()
    {
        var reported = false;
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(5), visible: true, ref reported));
    }

    [Fact]
    public void VisibleStallOverFiveSecondsReportsOnce()
    {
        var reported = false;
        Assert.True(HangWatchdog.Evaluate(TimeSpan.FromSeconds(6), visible: true, ref reported));
        Assert.True(reported);
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(7), visible: true, ref reported));
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(30), visible: true, ref reported));
    }

    [Fact]
    public void MinimizedOrHiddenOrCoveredWindowNeverReports()
    {
        // The caller passes visible = shown, not minimized, and Exo in front; any of those false means "not on screen".
        var reported = false;
        for (var seconds = 6; seconds <= 60; seconds += 6)
            Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(seconds), visible: false, ref reported));
        Assert.False(reported);
        Assert.False(HangWatchdog.ShouldReport(TimeSpan.FromSeconds(30), visible: false, reportedThisStall: false));
    }

    [Fact]
    public void OnScreenNeedsFrontExceptForTheHangSmoke()
    {
        // Normal use: shown, not minimized, and in front.
        Assert.True(HangWatchdog.Shown(visible: true, minimized: false, inFront: true, smokeHang: false));
        Assert.False(HangWatchdog.Shown(visible: true, minimized: false, inFront: false, smokeHang: false));
        // The hang smoke (window opened without activation) counts without front...
        Assert.True(HangWatchdog.Shown(visible: true, minimized: false, inFront: false, smokeHang: true));
        // ...but a minimized or hidden window still never does.
        Assert.False(HangWatchdog.Shown(visible: true, minimized: true, inFront: true, smokeHang: true));
        Assert.False(HangWatchdog.Shown(visible: false, minimized: false, inFront: true, smokeHang: true));
    }

    [Fact]
    public void SecondCheckDuringTheSameStallDoesNotReportAgain()
    {
        Assert.False(HangWatchdog.ShouldReport(TimeSpan.FromSeconds(8), visible: true, reportedThisStall: true));
        var reported = false;
        Assert.True(HangWatchdog.Evaluate(TimeSpan.FromSeconds(5.5), visible: true, ref reported));
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(6.5), visible: true, ref reported));
    }

    [Fact]
    public void AfterRecoveryANewStallReportsAgain()
    {
        var reported = false;
        Assert.True(HangWatchdog.Evaluate(TimeSpan.FromSeconds(6), visible: true, ref reported));
        // The stamp is fresh again: the stall is over and re-arms.
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(0.5), visible: true, ref reported));
        Assert.False(reported);
        Assert.True(HangWatchdog.Evaluate(TimeSpan.FromSeconds(6), visible: true, ref reported));
    }

    [Fact]
    public void StallThatEndsWhileHiddenStillRearms()
    {
        var reported = false;
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(9), visible: false, ref reported));
        Assert.False(HangWatchdog.Evaluate(TimeSpan.FromSeconds(1), visible: false, ref reported));
        Assert.True(HangWatchdog.Evaluate(TimeSpan.FromSeconds(6), visible: true, ref reported));
    }

    /// <summary>
    /// Ways this could fail: the watchdog keeps running while Exo is hidden (wake-ups for nothing), or comes back with the
    /// old beat and calls the page frozen for the time it spent hidden.
    /// </summary>
    [Fact]
    public void ArmedOnlyWhileOnScreenAndComingBackForgetsTheOldPageBeat()
    {
        HangWatchdog.SetArmed(false);
        Assert.False(HangWatchdog.IsArmed);
        HangWatchdog.PageBeat(visible: true);
        Assert.True(HangWatchdog.PageBeatSeen);
        HangWatchdog.SetArmed(true);
        Assert.True(HangWatchdog.IsArmed);
        Assert.False(HangWatchdog.PageBeatSeen, "arming starts the page's clock from nothing");
        HangWatchdog.PageBeat(visible: true);
        HangWatchdog.SetArmed(true);
        Assert.True(HangWatchdog.PageBeatSeen, "arming again while armed changes nothing");
        HangWatchdog.SetArmed(false);
        Assert.False(HangWatchdog.IsArmed);
        HangWatchdog.SetArmed(true);
        Assert.False(HangWatchdog.PageBeatSeen);
        HangWatchdog.SetArmed(false);
    }

    private static HangWatchdog.PageWatchInput Page(bool armed = true, bool uiAnswering = true, bool beat = true, bool visible = true, double silent = 1, bool reported = false) =>
        new(armed, uiAnswering, beat, visible, TimeSpan.FromSeconds(silent), reported);

    /// <summary>
    /// Every row of the page watch's table: 2^5 inputs times a short and a long silence, each judged against the table's own
    /// words (report exactly when armed, the UI thread answers, the silence is past the threshold, nothing was reported yet and
    /// the page either owes its first beat or last said it was visible).
    /// </summary>
    [Fact]
    public void EveryRowOfThePageWatchTable()
    {
        for (var row = 0; row < 64; row++)
        {
            bool Bit(int i) => (row & (1 << i)) != 0;
            var input = Page(Bit(0), Bit(1), Bit(2), Bit(3), Bit(4) ? 6 : 1, Bit(5));
            var (state, report) = HangWatchdog.PageCheck(input);
            var late = input.Silent > HangWatchdog.Threshold;
            var shouldReport = input.Armed && input.UiAnswering && late && !input.Reported && (!input.BeatSinceArm || input.LastBeatVisible);
            Assert.Equal(shouldReport, report);
            if (!input.Armed) Assert.Equal(HangWatchdog.PageWatchState.Disarmed, state);
            else if (report) Assert.Equal(HangWatchdog.PageWatchState.Reported, state);
        }
    }

    [Fact]
    public void AnotherExoWindowInFrontKeepsTheWatchArmedAndAPageThatSaidNotVisibleIsNeverReported()
    {
        // Exo's own file picker in front: Exo counts as in front (armed). The page last said it was not visible: silence is no verdict.
        Assert.True(HangWatchdog.Shown(visible: true, minimized: false, inFront: true, smokeHang: false));
        Assert.Equal((HangWatchdog.PageWatchState.Beating, false), HangWatchdog.PageCheck(Page(visible: false, silent: 60)));
        // A page that said visible and went silent is reported, once.
        Assert.Equal((HangWatchdog.PageWatchState.Reported, true), HangWatchdog.PageCheck(Page(visible: true, silent: 6)));
        Assert.Equal((HangWatchdog.PageWatchState.Reported, false), HangWatchdog.PageCheck(Page(visible: true, silent: 7, reported: true)));
    }

    [Fact]
    public void ExoShownAfterALongHideOwesAFirstBeatAndAPageThatHungWhileHiddenIsReported()
    {
        HangWatchdog.SetArmed(false);
        HangWatchdog.PageBeat(visible: true);
        HangWatchdog.SetArmed(true); // shown again: the old beat is forgotten
        Assert.False(HangWatchdog.PageBeatSeen);
        Assert.Equal((HangWatchdog.PageWatchState.WaitingForFirstBeat, false), HangWatchdog.PageCheck(Page(beat: false, silent: 3)));
        Assert.Equal((HangWatchdog.PageWatchState.Reported, true), HangWatchdog.PageCheck(Page(beat: false, silent: 6)));
        // The first beat arrives in time: nothing to report afterwards.
        Assert.Equal((HangWatchdog.PageWatchState.Beating, false), HangWatchdog.PageCheck(Page(beat: true, silent: 1)));
        HangWatchdog.SetArmed(false);
    }

    [Fact]
    public void AStuckUiThreadGivesNoPageVerdictAndAReportedPageStaysReported()
    {
        Assert.Equal((HangWatchdog.PageWatchState.Beating, false), HangWatchdog.PageCheck(Page(uiAnswering: false, silent: 60)));
        Assert.Equal((HangWatchdog.PageWatchState.WaitingForFirstBeat, false), HangWatchdog.PageCheck(Page(uiAnswering: false, beat: false, silent: 60)));
        Assert.Equal((HangWatchdog.PageWatchState.Reported, false), HangWatchdog.PageCheck(Page(uiAnswering: false, reported: true)));
        Assert.Equal((HangWatchdog.PageWatchState.Disarmed, false), HangWatchdog.PageCheck(Page(armed: false, silent: 60)));
    }
}
