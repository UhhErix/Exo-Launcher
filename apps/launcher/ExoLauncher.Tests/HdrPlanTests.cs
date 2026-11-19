using ExoLauncher.Services;
using Xunit;
namespace ExoLauncher.Tests;
/// <summary>
/// System > HDR decisions. Ways each could fail silently: the restore cuts a restart's HDR short or never comes (stuck on);
/// a switch lands on the wrong screen; a switch blanks a fullscreen game; a saved gamma ramp comes back wrong after a crash.
/// </summary>
public sealed class HdrPlanTests
{
    [Fact]
    public void The_restore_waits_out_the_hold_and_not_a_moment_less()
    {
        Assert.False(HdrPlan.RestoreDue(releasedAtMs: 1_000, nowMs: 1_000 + HdrPlan.RestoreHoldMs - 1));
        Assert.True(HdrPlan.RestoreDue(releasedAtMs: 1_000, nowMs: 1_000 + HdrPlan.RestoreHoldMs));
    }

    [Theory]
    [InlineData(false, false, true)]   // before the window opens: switch
    [InlineData(true, false, true)]    // windowed game: switch is allowed
    [InlineData(true, true, false)]    // fullscreen or borderless game: never switched under
    public void A_switch_never_blanks_a_fullscreen_game(bool windowUp, bool covers, bool expected) =>
        Assert.Equal(expected, HdrPlan.MaySwitchOn(windowUp, covers));

    [Fact]
    public void The_screen_is_the_first_preferred_one_that_can_show_hdr()
    {
        var capable = new[] { @"\\.\DISPLAY2", @"\\.\DISPLAY3" };
        // The game's own window first, then what it used last time, then the main screen.
        Assert.Equal(@"\\.\DISPLAY3", HdrPlan.PickScreen([@"\\.\DISPLAY3", @"\\.\DISPLAY2"], capable));
        Assert.Equal(@"\\.\display2", HdrPlan.PickScreen([null, @"\\.\display2", @"\\.\DISPLAY1"], capable), ignoreCase: true);
        Assert.Equal(@"\\.\DISPLAY2", HdrPlan.PickScreen(["", @"\\.\DISPLAY9", @"\\.\DISPLAY2"], capable));
    }

    [Fact]
    public void No_screen_is_picked_when_none_can_show_hdr()
    {
        Assert.Null(HdrPlan.PickScreen([@"\\.\DISPLAY1", null], []));
        Assert.Null(HdrPlan.PickScreen([], [@"\\.\DISPLAY1"]));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, false)]
    [InlineData(false, false, true)]
    public void The_switch_counts_only_once_windows_reports_it(bool wantOn, bool? reported, bool expected) =>
        Assert.Equal(expected, HdrPlan.Reached(wantOn, reported));

    [Fact]
    public void The_gamma_ramp_round_trips_through_settings_text()
    {
        var ramp = new ushort[3 * 256];
        for (var i = 0; i < ramp.Length; i++) ramp[i] = (ushort)(i * 251 % 65536);
        var text = HdrPlan.RampText(ramp);
        Assert.Equal(ramp, HdrPlan.RampFrom(text));
    }

    [Fact]
    public void A_missing_or_damaged_ramp_is_ignored_not_applied()
    {
        Assert.Equal("", HdrPlan.RampText(null));
        Assert.Equal("", HdrPlan.RampText(new ushort[10]));
        Assert.Null(HdrPlan.RampFrom(""));
        Assert.Null(HdrPlan.RampFrom(null));
        Assert.Null(HdrPlan.RampFrom("not base64!"));
        Assert.Null(HdrPlan.RampFrom(Convert.ToBase64String(new byte[10])));
    }

    [Theory]
    [InlineData("yes", "settings", false, "Its own settings have an HDR option, so it outputs HDR.")]
    [InlineData("yes", "learned", false, "It ran with Windows HDR on and kept its HDR option on, so it outputs HDR.")]
    [InlineData("yes", "wiki", false, "PCGamingWiki says it has HDR.")]
    [InlineData("yes", "files", false, "Its files ship HDR calibration art and HDR formats.")]
    [InlineData("no", "wiki", true, "PCGamingWiki says its HDR needs a mod, so Auto leaves it alone.")]
    [InlineData("no", "wiki", false, "PCGamingWiki says it has no HDR, so Auto leaves it alone.")]
    [InlineData("unknown", null, false, "Not known yet. Exo reads the game's settings each time it runs and checks PCGamingWiki.")]
    public void The_reason_says_where_the_verdict_came_from(string hdr, string? from, bool hackable, string expected) =>
        Assert.Equal(expected, HdrPlan.Reason(hdr, from, hackable));
}
