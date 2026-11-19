using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways suspending the shell's WebView2 can break something the user hears or sees, or never happen:
/// - Exo sleeps while visible, so the page stops under the user's eyes;
/// - Exo sleeps while closing;
/// - Exo sleeps during a Discord voice call, music, a capture or recording, or a download or install;
/// - any one of those gates is missed, so only the first check is honored;
/// - a gate that is on is reported as no reason, so the log says nothing when the shell stayed awake.
/// </summary>
public sealed class ShellSleepRulesTests
{
    private static ShellSleepInputs Hidden() => new(HiddenOrMinimized: true, Closing: false, VoiceConnected: false,
        MusicPlaying: false, CaptureRecording: false, DownloadRunning: false);

    [Fact]
    public void Hidden_with_no_gate_on_may_sleep()
    {
        Assert.True(ShellSleepRules.MaySleep(Hidden()));
        Assert.Null(ShellSleepRules.Blocker(Hidden()));
    }

    [Fact]
    public void Visible_never_sleeps()
    {
        var visible = Hidden() with { HiddenOrMinimized = false };
        Assert.False(ShellSleepRules.MaySleep(visible));
        Assert.Equal("shown", ShellSleepRules.Blocker(visible));
    }

    [Fact]
    public void Closing_never_sleeps()
    {
        var closing = Hidden() with { Closing = true };
        Assert.False(ShellSleepRules.MaySleep(closing));
        Assert.Equal("closing", ShellSleepRules.Blocker(closing));
    }

    [Theory]
    [InlineData(nameof(ShellSleepInputs.VoiceConnected), "voice call")]
    [InlineData(nameof(ShellSleepInputs.MusicPlaying), "music")]
    [InlineData(nameof(ShellSleepInputs.CaptureRecording), "capture")]
    [InlineData(nameof(ShellSleepInputs.DownloadRunning), "download")]
    public void Each_gate_alone_blocks_the_sleep_and_says_why(string gate, string reason)
    {
        var on = gate switch
        {
            nameof(ShellSleepInputs.VoiceConnected) => Hidden() with { VoiceConnected = true },
            nameof(ShellSleepInputs.MusicPlaying) => Hidden() with { MusicPlaying = true },
            nameof(ShellSleepInputs.CaptureRecording) => Hidden() with { CaptureRecording = true },
            _ => Hidden() with { DownloadRunning = true },
        };
        Assert.False(ShellSleepRules.MaySleep(on));
        Assert.Equal(reason, ShellSleepRules.Blocker(on));
    }

    /// <summary>
    /// The whole table, all 64 rows: the shell sleeps only when hidden, not closing and with every gate off, and the
    /// reason it stays awake is the first of shown, closing, voice call, music, capture, download that holds.
    /// (A running game is not an input: sleep is no longer tied to a game.)
    /// </summary>
    [Fact]
    public void Every_row_of_the_table_sleeps_or_names_the_first_reason_it_cannot()
    {
        for (var row = 0; row < 64; row++)
        {
            bool Bit(int i) => (row & (1 << i)) != 0;
            var inputs = new ShellSleepInputs(HiddenOrMinimized: Bit(0), Closing: Bit(1), VoiceConnected: Bit(2),
                MusicPlaying: Bit(3), CaptureRecording: Bit(4), DownloadRunning: Bit(5));
            var expected = !Bit(0) ? "shown" : Bit(1) ? "closing" : Bit(2) ? "voice call" : Bit(3) ? "music"
                : Bit(4) ? "capture" : Bit(5) ? "download" : null;
            Assert.Equal(expected, ShellSleepRules.Blocker(inputs));
            Assert.Equal(expected is null, ShellSleepRules.MaySleep(inputs));
        }
    }

    /// <summary>
    /// Every row of the step table: 64 inputs times asleep or not. A blocker that starts while asleep wakes the page;
    /// one that ends while hidden lets it sleep again; nothing else moves it.
    /// </summary>
    [Fact]
    public void Every_row_of_the_step_table_wakes_or_sleeps_by_the_blocker_not_by_the_window_event()
    {
        for (var row = 0; row < 64; row++)
        {
            bool Bit(int i) => (row & (1 << i)) != 0;
            var inputs = new ShellSleepInputs(HiddenOrMinimized: Bit(0), Closing: Bit(1), VoiceConnected: Bit(2),
                MusicPlaying: Bit(3), CaptureRecording: Bit(4), DownloadRunning: Bit(5));
            var may = ShellSleepRules.MaySleep(inputs);
            Assert.Equal(may ? ShellSleepRules.Step.StartTimer : ShellSleepRules.Step.Stay, ShellSleepRules.Next(inputs, asleepOrPending: false));
            Assert.Equal(may ? ShellSleepRules.Step.Stay : ShellSleepRules.Step.Wake, ShellSleepRules.Next(inputs, asleepOrPending: true));
        }
    }

    [Fact]
    public void A_call_that_starts_and_ends_while_hidden_wakes_then_sleeps_the_page_again()
    {
        var asleep = true;
        void Apply(ShellSleepInputs now)
        {
            switch (ShellSleepRules.Next(now, asleep))
            {
                case ShellSleepRules.Step.Wake: asleep = false; break;
                case ShellSleepRules.Step.StartTimer: asleep = true; break;
            }
        }
        Apply(Hidden() with { VoiceConnected = true });
        Assert.False(asleep);
        Apply(Hidden());
        Assert.True(asleep);
        Apply(Hidden() with { DownloadRunning = true });
        Assert.False(asleep);
        Apply(Hidden() with { DownloadRunning = true, MusicPlaying = true });
        Assert.False(asleep);
        Apply(Hidden() with { MusicPlaying = true });
        Assert.False(asleep);
        Apply(Hidden());
        Assert.True(asleep);
    }
}