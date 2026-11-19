using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The Controller Lab's trigger, button and drift readings. The ways they could mislead silently:
/// - a trigger barely touched reads as a short one, or a quick pull's few values as its real resolution, or a digital one as analog;
/// - a finger's ordinary press counts as chatter, a real bounce as two presses, or a held button as many;
/// - a hat's diagonal reads as a third button, or Sony's report counter as a button pressed;
/// - a stick's noise reads as drift, or a steady creep is hidden by its own average;
/// - a pad that sends only on a change has its idle gaps read as stutter.
/// </summary>
public sealed class ControllerQualityTests
{
    private const long F = 10_000_000;

    private static IEnumerable<byte> Ramp(int from, int to, int step = 1)
    {
        for (var v = from; v <= to; v += step) yield return (byte)v;
    }

    [Fact]
    public void A_full_slow_pull_covers_its_travel_and_counts_its_values()
    {
        var sweep = ControllerLab.Swept(Ramp(0, 255).ToList())!;
        Assert.True(sweep.Full);
        Assert.Equal(256, sweep.Steps);
        Assert.Equal(0, sweep.Low);
        Assert.Equal(100, sweep.High);
    }

    [Fact]
    public void A_trigger_short_of_its_travel_says_so()
    {
        var sweep = ControllerLab.Swept(Ramp(0, 200).ToList())!;
        Assert.False(sweep.Full);
        Assert.Equal(78, sweep.High);
    }

    [Fact]
    public void A_digital_trigger_shows_its_two_values()
    {
        var values = Enumerable.Repeat((byte)0, 30).Concat(Enumerable.Repeat((byte)255, 30)).ToList();
        Assert.Equal(2, ControllerLab.Swept(values)!.Steps);
    }

    [Fact]
    public void A_trigger_barely_touched_is_not_measured()
    {
        Assert.Null(ControllerLab.Swept(Ramp(0, 30).ToList()));
        Assert.Null(ControllerLab.Swept([0, 255]));
    }

    /// <summary>States at 250 Hz, button 0 held in the given ms stretches.</summary>
    private static List<(long, int)> Pressed(int ms, params (int From, int To)[] held)
    {
        var states = new List<(long, int)>();
        for (var t = 0; t < ms; t += 4) states.Add((t * F / 1000, held.Any(h => t >= h.From && t < h.To) ? 1 : 0));
        return states;
    }

    [Fact]
    public void Ordinary_presses_are_not_chatter()
    {
        var check = ControllerLab.Bounces(Pressed(2000, (100, 160), (400, 480), (800, 840)), F, _ => "Cross")!;
        var press = Assert.Single(check.Buttons);
        Assert.Equal(3, press.Presses);
        Assert.Equal(0, press.Bounces);
        Assert.Null(press.Shortest);
        Assert.Equal(4, check.ReportMs);
    }

    [Fact]
    public void A_bounce_faster_than_a_finger_is_chatter_and_not_a_press()
    {
        // Down, up 8 ms later, down again 8 ms after that: one press that chattered.
        var check = ControllerLab.Bounces(Pressed(2000, (100, 108), (116, 200)), F, _ => "Cross")!;
        var press = Assert.Single(check.Buttons);
        Assert.Equal(1, press.Presses);
        Assert.Equal(1, press.Bounces);
        Assert.Equal(8, press.Shortest);
    }

    [Fact]
    public void A_button_held_throughout_is_no_press()
    {
        var check = ControllerLab.Bounces(Pressed(1000, (0, 1000)), F, _ => "Cross")!;
        Assert.Empty(check.Buttons);
    }

    [Fact]
    public void Too_little_to_read_says_nothing() => Assert.Null(ControllerLab.Bounces([(0, 0), (1, 1)], F, _ => "Cross"));

    [Theory]
    [InlineData(0, 0b0001)] // up
    [InlineData(1, 0b0011)] // up and right, not a third button
    [InlineData(4, 0b0100)]
    [InlineData(7, 0b1001)]
    [InlineData(8, 0)]      // the hat at rest
    [InlineData(0x28, 0x20)] // the hat at rest, Cross pressed
    public void The_hat_is_four_directions(int first, int expected) => Assert.Equal(expected, ControllerLab.SonyButtons(first, 0, 0) & 0xFF);

    [Fact]
    public void Sonys_report_counter_is_no_button()
    {
        // A DualShock 4's third byte holds PS and Touchpad, the rest a counter.
        var report = new byte[64];
        report[0] = 0x01;
        report[5] = 8; // hat at rest
        report[7] = 0b1111_1100;
        Assert.Equal(0, ControllerLab.ParseDualShock4(report, usb: true)!.Value.Buttons);
    }

    [Theory]
    [InlineData("DualSense", 5, "Cross")]
    [InlineData("DualShock4", 16, "PS")]
    [InlineData("Xbox", 12, "A")]
    [InlineData("Xbox", 10, "Button 11")]
    [InlineData("Hid", 0, "Button 1")]
    public void Buttons_go_by_their_own_names(string family, int bit, string name) => Assert.Equal(name, ControllerLab.ButtonName(Enum.Parse<ControllerLab.Family>(family), bit));

    [Fact]
    public void Noise_is_not_drift_and_a_creep_is()
    {
        var noisy = Enumerable.Range(0, 300).Select(i => ((byte)(127 + i % 3), (byte)128)).ToList();
        Assert.True(ControllerLab.AtRest(noisy)!.Creep < 1);
        var creeping = Enumerable.Range(0, 300).Select(i => ((byte)(120 + i / 30), (byte)128)).ToList();
        Assert.True(ControllerLab.AtRest(creeping)!.Creep >= 4);
    }

    [Fact]
    public void A_pad_sending_only_on_change_is_a_lower_bound_and_not_stutter()
    {
        // Bursts at 250 Hz while a stick moves, with 200 ms alone between them.
        var reports = new List<(long, uint?)>();
        var at = 0L;
        for (var burst = 0; burst < 6; burst++)
        {
            for (var i = 0; i < 40; i++)
            {
                at += F / 250;
                reports.Add((at, null));
            }
            at += F / 5;
        }
        var polling = ControllerLab.Measure(reports, F)!;
        Assert.True(polling.Changes);
        Assert.Equal(250, polling.Rate);
    }

    [Fact]
    public void A_steady_pad_is_not_change_driven()
    {
        var reports = Enumerable.Range(0, 750).Select(i => (i * F / 250, (uint?)(uint)i)).ToList();
        var polling = ControllerLab.Measure(reports, F)!;
        Assert.False(polling.Changes);
        Assert.Equal(0, polling.Batched);
    }

    [Fact]
    public void Reports_handed_over_together_read_as_batched()
    {
        var reports = new List<(long, uint?)>();
        var at = 0L;
        for (var i = 0; i < 400; i++)
        {
            at += i % 4 == 3 ? F / 100 : F / 100000;
            reports.Add((at, null));
        }
        var polling = ControllerLab.Measure(reports, F)!;
        Assert.False(polling.Changes);
        Assert.True(polling.Batched >= 70, $"batched {polling.Batched}");
        Assert.InRange(polling.Rate, 390, 410); // the mean's, not a tiny gap's
    }
}
