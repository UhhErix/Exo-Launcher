using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// System > Display offering a depth the link can't carry, and what it does when one doesn't stick. It fails silently if:
/// - a color's rate is counted without blanking or with the wrong bits a pixel (4:2:2 on HDMI rides a 24-bit container),
///   or a link's without line coding, so 10-bit is offered at 1440p 165/170 Hz over DisplayPort 1.2 (the Radeon friend's
///   VG27AQ1A) or 8-bit is hidden where it fits;
/// - compression is assumed where the screen or link lacks it (4K 144 Hz over DisplayPort 1.4 needs DSC) or ignored where both have it;
/// - a color that didn't stick is offered again (after a restart too), or keeps another format at the same rate offered,
///   or takes 8-bit, a lower refresh rate or another screen's link with it;
/// - a later failure at a higher rate raises the learned limit, or a color seen running at the limit doesn't lift it;
/// - Best picks a depth the link failed to carry instead of the next one down.
/// </summary>
public sealed class DisplayLinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-link-").FullName;
    private string LinksPath => Path.Combine(_dir, "display-links.json");

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    private static OutputColor Rgb(int bpc) => new("rgb", bpc, true);

    /// <summary>
    /// The pixel rate (Hz) of a mode in VESA CVT 1.2 reduced blanking v2, what high-refresh screens list: 80 pixels of
    /// horizontal blanking and at least 460 µs (and 15 lines) of vertical blanking.
    /// </summary>
    private static long Cvt(int width, int height, int hz)
    {
        var line = (1e6 / hz - 460) / height;
        var vblank = Math.Max((int)Math.Floor(460 / line) + 1, 15);
        return (long)hz * (height + vblank) * (width + 80);
    }

    [Fact]
    public void TheModesPixelRatesCountTheirBlanking()
    {
        Assert.Equal(701_474_400, Cvt(2560, 1440, 170));
        Assert.Equal(679_100_400, Cvt(2560, 1440, 165));
        Assert.Equal(583_200_000, Cvt(1920, 1080, 240));
        Assert.Equal(1_306_206_720, Cvt(3840, 2160, 144));
    }

    [Theory]
    // 1440p 170 Hz: 8-bit 16.8 Gbps, 10-bit 21.0 Gbps.
    [InlineData(170, 8, "dp12", true)]
    [InlineData(170, 10, "dp12", false)]
    [InlineData(170, 10, "dp14", true)]
    [InlineData(170, 8, "hdmi20", false)] // 701 MHz TMDS over HDMI 2.0's 600: HDMI holds this screen to 144 Hz
    [InlineData(170, 10, "hdmi21", true)]
    // 1440p 165 Hz: 8-bit 16.3 Gbps, 10-bit 20.4 Gbps.
    [InlineData(165, 8, "dp12", true)]
    [InlineData(165, 10, "dp12", false)]
    [InlineData(165, 10, "dp14", true)]
    [InlineData(165, 8, "hdmi20", false)]
    [InlineData(165, 10, "hdmi20", false)]
    [InlineData(165, 10, "hdmi21", true)]
    public void At1440pTenBitNeedsMoreThanDisplayPort12(int hz, int bpc, string link, bool fits) =>
        Assert.Equal(fits, DisplayLink.Fits(Rgb(bpc), Cvt(2560, 1440, hz), Link(link)));

    [Fact]
    public void At1080p240HzTenBitJustMissesDisplayPort12AndHdmi20()
    {
        var rate = Cvt(1920, 1080, 240);
        Assert.True(DisplayLink.Fits(Rgb(8), rate, DisplayLink.Dp12));
        Assert.True(DisplayLink.Fits(Rgb(8), rate, DisplayLink.Hdmi20));
        Assert.False(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Dp12)); // 17.50 over 17.28
        Assert.False(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Hdmi20));
        Assert.True(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Dp14));
        // HDMI 2.0's limit as the EDID states it (600 MHz TMDS) says the same.
        var caps = new MonitorCaps(48, 240, new Dictionary<string, int[]> { ["rgb"] = [8, 10, 12] }, 10, 600_000_000);
        Assert.True(caps.Takes(Rgb(8), rate));
        Assert.False(caps.Takes(Rgb(10), rate));
    }

    [Fact]
    public void FourKAt144HzOverDisplayPort14NeedsCompression()
    {
        var rate = Cvt(3840, 2160, 144);
        Assert.False(DisplayLink.Fits(Rgb(8), rate, DisplayLink.Dp14));                 // 31.3 Gbps
        Assert.False(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Dp14));
        Assert.True(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Dp14, dsc: true));      // 12 bits a pixel: 15.7 Gbps
        Assert.False(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Dp12, dsc: true));     // DisplayPort 1.2 has no DSC
        Assert.True(DisplayLink.Fits(Rgb(10), rate, DisplayLink.Hdmi21, dsc: true));
    }

    [Fact]
    public void FourTwoTwoRidesHdmisTwentyFourBitContainer()
    {
        var ycc10 = new OutputColor("422", 10, false);
        Assert.Equal(24, DisplayLink.Bits(ycc10, hdmi: true));
        Assert.Equal(20, DisplayLink.Bits(ycc10, hdmi: false));
        Assert.Equal(15, DisplayLink.Bits(new OutputColor("420", 10, false), hdmi: false));
    }

    // ---- Learned limits ----

    private const string Port = "dp|\\\\?\\DISPLAY#AUS27A1#5&1a2b&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void ADepthThatDidntStickIsntOfferedAgainAtThatRateOrMore()
    {
        var memory = new DisplayLink.Memory(LinksPath);
        long at170 = Cvt(2560, 1440, 170), at165 = Cvt(2560, 1440, 165), at120 = Cvt(2560, 1440, 120);
        Assert.True(memory.Takes(Port, Rgb(10), at170, hdmi: false)); // nothing known: try it
        Assert.True(memory.Failed(Port, Rgb(10), at170, hdmi: false));

        Assert.False(memory.Takes(Port, Rgb(10), at170, hdmi: false));
        Assert.False(memory.Takes(Port, new OutputColor("444", 10, false), at170, hdmi: false)); // same rate, any format
        Assert.False(memory.Takes(Port, Rgb(12), at165, hdmi: false));
        Assert.True(memory.Takes(Port, Rgb(8), at170, hdmi: false));
        Assert.True(memory.Takes(Port, Rgb(10), at120, hdmi: false));
        Assert.True(memory.Takes(Port, new OutputColor("422", 10, false), at170, hdmi: false)); // 2 x 10 bits a pixel fits under it
        Assert.True(memory.Takes(Port.Replace("dp|", "hdmi|"), Rgb(10), at170, hdmi: true)); // the screen's other input is another link

        // After a restart too.
        Assert.False(new DisplayLink.Memory(LinksPath).Takes(Port, Rgb(10), at170, hdmi: false));
    }

    [Fact]
    public void TheLimitOnlyComesDownAndARunningColorLiftsIt()
    {
        var memory = new DisplayLink.Memory(LinksPath);
        long at170 = Cvt(2560, 1440, 170), at144 = Cvt(2560, 1440, 144);
        memory.Failed(Port, Rgb(10), at144, hdmi: false);
        memory.Failed(Port, Rgb(10), at170, hdmi: false); // a higher rate failing says nothing new
        Assert.False(memory.Takes(Port, Rgb(10), at144, hdmi: false));

        // Running 8-bit at 170 Hz, under the limit, changes nothing.
        memory.Ran(Port, Rgb(8), at170, hdmi: false);
        Assert.False(memory.Takes(Port, Rgb(10), at144, hdmi: false));
        // 10-bit running at 144 Hz (a better cable): the limit was wrong and goes.
        memory.Ran(Port, Rgb(10), at144, hdmi: false);
        Assert.True(memory.Takes(Port, Rgb(10), at170, hdmi: false));
        Assert.True(new DisplayLink.Memory(LinksPath).Takes(Port, Rgb(10), at170, hdmi: false));
    }

    [Fact]
    public void WithoutAPixelRateNothingIsLearnedAndNothingHidden()
    {
        var memory = new DisplayLink.Memory(LinksPath);
        Assert.False(memory.Failed(Port, Rgb(10), 0, hdmi: false));
        Assert.True(memory.Takes(Port, Rgb(10), 0, hdmi: false));
        Assert.False(File.Exists(LinksPath));
    }

    [Fact]
    public void BestPicksTheDeepestDepthTheLinkStillCarries()
    {
        var memory = new DisplayLink.Memory(LinksPath);
        var at170 = Cvt(2560, 1440, 170);
        // What AMD's driver answers for the VG27AQ1A on DisplayPort: depth and format apart, so 10-bit too.
        OutputColor[] driver = [Rgb(6), Rgb(8), Rgb(10), new("rgb", 8, false), new("rgb", 10, false), new("444", 8, false), new("444", 10, false)];
        bool Link(OutputColor color) => memory.Takes(Port, color, at170, hdmi: false);

        Assert.Equal(Rgb(10), DisplayOutput.BestColor(DisplayOutput.Offered(driver, null, at170, Link), 170, panelBpc: 10).Color);
        memory.Failed(Port, Rgb(10), at170, hdmi: false);
        var offered = DisplayOutput.Offered(driver, null, at170, Link);
        Assert.DoesNotContain(offered, color => color.Bpc == 10);
        var (best, why) = DisplayOutput.BestColor(offered, 170, panelBpc: 10);
        Assert.Equal(Rgb(8), best);
        Assert.Null(why);
    }

    private static DisplayLink.Link Link(string name) => name switch
    {
        "dp12" => DisplayLink.Dp12,
        "dp14" => DisplayLink.Dp14,
        "hdmi20" => DisplayLink.Hdmi20,
        _ => DisplayLink.Hdmi21,
    };
}
