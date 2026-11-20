using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// "Best for this screen" picks what GPU > Display applies. It fails silently if it: takes a
/// YCbCr format while RGB Full fits, takes a lower depth than the link carries, picks 16-bit
/// (no screen shows it; drivers refuse it), offers Full range on YCbCr, gives up when only
/// YCbCr fits instead of taking the fullest one, or stays quiet about why it isn't RGB.
/// </summary>
public sealed class DisplayOutputTests
{
    private static OutputColor C(string format, int bpc, bool full = false) => new(format, bpc, full);

    // The main screen runs at its highest rate; any other at 60 Hz (or the rate it has closest to it), never higher.
    [Fact]
    public void EveryScreenTakesItsHighestRefresh()
    {
        int[] rates = [60, 100, 120, 144, 165];
        Assert.Equal(165, DisplayOutput.BestRefresh(main: true, rates, 60));
        Assert.Equal(165, DisplayOutput.BestRefresh(main: false, rates, 60));
        Assert.Equal(144, DisplayOutput.BestRefresh(main: false, [50, 59, 144], 60));
        Assert.Equal(75, DisplayOutput.BestRefresh(main: false, [59, 60, 75], 60));
        Assert.Equal(144, DisplayOutput.BestRefresh(main: false, [], 144));
    }

    [Theory]
    [InlineData(60000u, 1001u, 59, "59.94 Hz")]
    [InlineData(120000u, 1001u, 119, "119.88 Hz")]
    [InlineData(24000u, 1001u, 23, "23.98 Hz")]
    [InlineData(165000u, 1000u, 165, "165 Hz")]
    [InlineData(144000u, 1000u, 144, "144 Hz")]
    [InlineData(0u, 0u, 60, "60 Hz")]
    public void RationalRefreshFormatsCleanly(uint num, uint den, int fallbackHz, string expected)
    {
        var exact = WindowsHdr.ExactRefreshHz(num, den);
        Assert.Equal(expected, DisplayOutput.FormatRefreshLabel(exact, fallbackHz));
    }

    [Fact]
    public void LinkRetrainBackoffStopsOnceSettledAndCapsAtMaxWait()
    {
        var polls = 0;
        var slept = 0;
        Assert.True(DisplayOutput.WaitForRefreshSettle(() => ++polls >= 3 ? 165 : 60, 165, ms => slept += ms, 3000));
        Assert.Equal(150 + 250 + 400, slept);

        var capped = 0;
        Assert.False(DisplayOutput.WaitForRefreshSettle(() => 60, 165, ms => capped += ms, 3000));
        Assert.Equal(3000, capped);
    }

    [Fact]
    public void MonitorDdcClampsLevelsAndHandlesMissingMonitorGracefully()
    {
        Assert.Equal(0u, MonitorDdc.ClampLevel(-10, 0, 100));
        Assert.Equal(100u, MonitorDdc.ClampLevel(200, 0, 100));
        Assert.Equal(45u, MonitorDdc.ClampLevel(45, 10, 90));
        Assert.Equal(50u, MonitorDdc.ClampLevel(25, 50, 50));
        Assert.Null(MonitorDdc.Read("\\\\.\\DISPLAY_MISSING_99"));
    }

    // What an RTX 3070 reported for a 1080p 144 Hz DisplayPort screen.
    private static readonly OutputColor[] DisplayPort =
    [
        C("rgb", 6, true), C("rgb", 8, true), C("rgb", 10, true), C("rgb", 12, true), C("rgb", 16, true),
        C("rgb", 6), C("rgb", 8), C("rgb", 10), C("rgb", 12),
        C("444", 8), C("444", 10), C("444", 12), C("422", 8), C("422", 10), C("422", 12),
    ];

    [Fact]
    public void Takes_the_deepest_rgb_full_up_to_12_bit()
    {
        var (color, why) = DisplayOutput.BestColor(DisplayPort, 144);
        Assert.Equal(C("rgb", 12, true), color);
        Assert.Null(why);
    }

    [Fact]
    public void Says_when_a_deeper_color_only_fits_as_ycbcr()
    {
        var (color, why) = DisplayOutput.BestColor([C("rgb", 8, true), C("rgb", 8), C("422", 10), C("420", 12)], 240);
        Assert.Equal(C("rgb", 8, true), color);
        Assert.Contains("12-bit only fits as YCbCr 4:2:0", why);
    }

    [Fact]
    public void Takes_the_fullest_ycbcr_when_the_link_cannot_carry_rgb_and_says_why()
    {
        var (color, why) = DisplayOutput.BestColor([C("420", 10), C("422", 8), C("422", 10), C("420", 12)], 144);
        Assert.Equal(C("422", 10), color);
        Assert.Contains("At 144 Hz", why);
        Assert.Contains("can't carry RGB", why);
    }

    [Fact]
    public void Takes_rgb_limited_on_a_screen_that_only_takes_limited()
    {
        var (color, why) = DisplayOutput.BestColor([C("rgb", 8), C("rgb", 10), C("444", 12)], 60);
        Assert.Equal(C("rgb", 10), color);
        Assert.NotNull(why);
    }

    [Fact]
    public void Nothing_supported_means_no_choice()
    {
        var (color, why) = DisplayOutput.BestColor([], 60);
        Assert.Null(color);
        Assert.Null(why);
    }

    [Theory]
    [InlineData("rgb", 10, true, "RGB 10-bit full")]
    [InlineData("rgb", 8, false, "RGB 8-bit limited")]
    [InlineData("422", 12, false, "YCbCr 4:2:2 12-bit")]
    public void Labels_read_plainly(string format, int bpc, bool full, string expected) =>
        Assert.Equal(expected, DisplayOutput.Label(new OutputColor(format, bpc, full)));

    [Fact]
    public void BestStopsAtThePanelsOwnDepth()
    {
        var (color, _) = DisplayOutput.BestColor([C("rgb", 8, true), C("rgb", 10, true), C("rgb", 12, true)], 165, panelBpc: 8);
        Assert.Equal(8, color!.Bpc);
        (color, _) = DisplayOutput.BestColor([C("rgb", 8, true), C("rgb", 10, true), C("rgb", 12, true)], 165, panelBpc: 10);
        Assert.Equal(10, color!.Bpc);
        (color, _) = DisplayOutput.BestColor([C("rgb", 8, true), C("rgb", 10, true)], 165, panelBpc: 6);
        Assert.Equal(8, color!.Bpc);
    }

    [Theory]
    [InlineData(0xA5, 8)]   // digital, 8 bits per color, DisplayPort
    [InlineData(0xB5, 10)]  // digital, 10 bits
    [InlineData(0x80, null)] // digital, depth not stated
    public void PanelDepthComesFromEdidByte20(int input, int? bpc)
    {
        var edid = MonitorCapsTests.Base(4, (byte)input, 0, null);
        Assert.Equal(bpc, Edid.Parse(edid)!.PanelBpc);
        edid[20] = 0x25; // analogue input: nothing digital to say
        Assert.Null(Edid.Parse(edid));
    }

    // A screen named by a bare number, or "Screen n" while Windows names its maker, fails; so does a real EDID name
    // losing out to the maker.
    [Theory]
    [InlineData("VG248", @"\\?\DISPLAY#AUS24AC#5&1&0&UID1#{e6f07b5f}", 1, "VG248")]
    [InlineData("100002487", @"\\?\DISPLAY#ONN0101#5&1&0&UID2#{e6f07b5f}", 2, "onn 100002487")] // an onn monitor's EDID name is its part number
    [InlineData("", @"\\?\DISPLAY#ONN0101#5&1&0&UID2#{e6f07b5f}", 2, "onn 0101")]
    [InlineData(null, @"\\?\DISPLAY#XYZ1234#5&1&0&UID3#{e6f07b5f}", 3, "XYZ 1234")]
    [InlineData("100002487", "", 2, "Screen 2")]
    [InlineData("", null, 3, "Screen 3")]
    public void ScreensWithoutARealNameTakeTheirMaker(string? edid, string? devicePath, int number, string expected) =>
        Assert.Equal(expected, DisplayOutput.ScreenName(edid, devicePath, number));

    // The ramp fails if NVIDIA's defaults are not a straight line (the screen would change on
    // "Use NVIDIA color settings" alone), if it runs backwards, or if brightness goes the wrong way.
    [Fact]
    public void RampAtDefaultsIsStraightAndSlidersMoveItTheRightWay()
    {
        var straight = DisplayOutput.Ramp(50, 50, 100);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(i * 257, straight[i]);
            Assert.Equal(straight[i], straight[512 + i]);
        }
        foreach (var ramp in new[] { DisplayOutput.Ramp(0, 100, 30), DisplayOutput.Ramp(100, 0, 280), DisplayOutput.Ramp(70, 60, 120) })
            for (var i = 1; i < 256; i++) Assert.True(ramp[i] >= ramp[i - 1]);
        Assert.True(DisplayOutput.Ramp(70, 50, 100)[128] > straight[128]);
        Assert.True(DisplayOutput.Ramp(50, 50, 150)[128] > straight[128]); // gamma above 1 lifts mid-grey, as in NVIDIA's panel
        Assert.True(DisplayOutput.Ramp(50, 80, 100)[64] < straight[64]);
    }

    // Saved NVIDIA color must survive a restart: values and the ramp to put back.
    [Fact]
    public void NvidiaColorRoundTrips()
    {
        var before = DisplayOutput.Ramp(40, 55, 90);
        var stored = NvidiaColor.Format(new NvidiaColor.Values(60, 45, 110, 70, 12), before);
        var parsed = NvidiaColor.Parse(stored);
        Assert.Equal(new NvidiaColor.Values(60, 45, 110, 70, 12), parsed!.Value.Values);
        Assert.Equal(before, parsed.Value.Before);
        Assert.Null(NvidiaColor.Parse("1,2,3"));
        Assert.Null(NvidiaColor.Parse(NvidiaColor.Format(new NvidiaColor.Values(50, 50, 100, 50, 0), null))!.Value.Before);
    }
}
