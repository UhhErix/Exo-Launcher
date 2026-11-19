using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ExoCapture.Capture;
using ExoCapture.Encode;
using ExoCapture.Live;
using ExoCapture.Native;

namespace ExoCapture.Tests;

public sealed class CaptureTests
{
    private sealed class FakeSource(string kind) : IFrameSource
    {
        public string Kind => kind;
        public int Width => 1920;
        public int Height => 1080;
        public bool Hdr => false;
        public string? Ended => null;
        public bool HadFrame => false;
        public bool Disposed { get; private set; }
        public Frame? Latest() => null;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void OpenCaptureSourceUsesWgcWhenAvailableAndHealthy()
    {
        var wgc = new FakeSource("(wgc)");
        var fallbackCalled = false;

        var chosen = CaptureSources.OpenCaptureSource(
            () => true,
            () => wgc,
            () => { fallbackCalled = true; return new FakeSource("(dxgi)"); });

        Assert.Same(wgc, chosen);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void OpenCaptureSourceFallsBackToDuplicationOnCaptureException()
    {
        var fallback = new FakeSource("(dxgi)");
        string? logged = null;

        var chosen = CaptureSources.OpenCaptureSource(
            () => true,
            () => throw new CaptureException("capture the window (0x80070422)"),
            () => fallback,
            msg => logged = msg);

        Assert.Same(fallback, chosen);
        Assert.NotNull(logged);
        Assert.Contains("0x80070422", logged);
    }

    [Fact]
    public void OpenCaptureSourceSkipsWgcWhenUnavailable()
    {
        var wgcCalled = false;
        var fallback = new FakeSource("(dxgi)");

        var chosen = CaptureSources.OpenCaptureSource(
            () => false,
            () => { wgcCalled = true; return new FakeSource("(wgc)"); },
            () => fallback);

        Assert.False(wgcCalled);
        Assert.Same(fallback, chosen);
    }

    [Fact]
    public void WgcServiceDisabledFailureCachesAvailability()
    {
        WgcSource.ResetAvailabilityCacheForTests();
        try
        {
            Assert.True(WgcSource.RecordFailureHr(WgcSource.ErrorServiceDisabled));
            Assert.False(WgcSource.RecordFailureHr(unchecked((int)0x80004005)));
            Assert.False(WgcSource.Available(out var why));
            Assert.NotNull(why);
            Assert.Contains("disabled", why!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            WgcSource.ResetAvailabilityCacheForTests();
        }
    }

    [Fact]
    public void StreamTableTracksCurrentIdAndNeverStopsLastWhenIdIsZero()
    {
        var table = new StreamTable<string>();
        table.Add(1, "first");
        table.Add(2, "second");
        Assert.Equal("second", table.Current);

        // id: 0 without all:true must NEVER stop an active stream.
        var stopped = table.TakeStop(JsonNode.Parse("""{"id":0}"""));
        Assert.Empty(stopped);
        Assert.Equal("second", table.Current);

        // Stopping explicit id 1 preserves current stream 2.
        var one = table.TakeStop(JsonNode.Parse("""{"id":1}"""));
        Assert.Equal(["first"], one);
        Assert.Equal("second", table.Current);

        // Removing current stream clears/updates Current cleanly; all:true stops everything.
        table.Add(3, "third");
        Assert.Equal("third", table.Current);
        var all = table.TakeStop(JsonNode.Parse("""{"id":0,"all":true}"""));
        Assert.Equal(2, all.Count);
        Assert.Null(table.Current);
    }

    [Theory]
    [InlineData(3440, 1440, 1280, 536)]
    [InlineData(5120, 1440, 1280, 360)]
    [InlineData(2560, 1440, 1280, 720)]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(800, 600, 800, 600)]
    public void GoLivePlanScalesUltrawideInside1280x720ForSoftwareFallback(int width, int height, int expectedW, int expectedH)
    {
        var (steps, _) = GoLive.BuildPlan(width, height, 60, Codec.H264, allowFallback: true);
        var fallbackStep = steps[^1];
        Assert.Equal(expectedW, fallbackStep.W);
        Assert.Equal(expectedH, fallbackStep.H);
        Assert.True(fallbackStep.W <= 1280);
        Assert.True(fallbackStep.H <= 720);
        Assert.Equal(0, fallbackStep.W % 2);
        Assert.Equal(0, fallbackStep.H % 2);
    }

    [Fact]
    public void GoLivePlanDeduplicatesCodecsAndHonorsAllowFallback()
    {
        var (_, h264Only) = GoLive.BuildPlan(1920, 1080, 60, Codec.H264, allowFallback: true);
        Assert.Equal([Codec.H264], h264Only);

        var (_, hevcWithFallback) = GoLive.BuildPlan(1920, 1080, 60, Codec.Hevc, allowFallback: true);
        Assert.Equal([Codec.Hevc, Codec.H264], hevcWithFallback);

        var (_, hevcStrict) = GoLive.BuildPlan(1920, 1080, 60, Codec.Hevc, allowFallback: false);
        Assert.Equal([Codec.Hevc], hevcStrict);
    }

    [Fact]
    public void IntersectWindowReturnsNullForMinimizedOrOffMonitorBounds()
    {
        var desktop = new Win32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        // Minimized iconic bounds (-32000,-32000) must not clamp to a 2px sliver.
        Assert.Null(DuplicationSource.IntersectWindow(new Win32.RECT { Left = -32000, Top = -32000, Right = -31840, Bottom = -31972 }, desktop, 1920, 1080));
        // Completely off-right monitor.
        Assert.Null(DuplicationSource.IntersectWindow(new Win32.RECT { Left = 2000, Top = 100, Right = 2800, Bottom = 600 }, desktop, 1920, 1080));
        // Partial overlap clips cleanly without slivering.
        var partial = DuplicationSource.IntersectWindow(new Win32.RECT { Left = 1720, Top = -20, Right = 2120, Bottom = 480 }, desktop, 1920, 1080);
        Assert.Equal(new Win32.RECT { Left = 1720, Top = 0, Right = 1920, Bottom = 480 }, partial);
    }

    [Fact]
    public void MonochromeAndColorCursorsBlendOntoBgraPatch()
    {
        // 4x2 BGRA buffer filled with blue (B=200, G=100, R=50, A=255)
        var bgra = new byte[4 * 2 * 4];
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (200, 100, 50, 255);

        // Monochrome 2x1 cursor at (1, 0): top row AND=(0b01000000 => pixel0 AND=0, pixel1 AND=1), bottom XOR=(0b10000000 => pixel0 XOR=1 (white), pixel1 XOR=0 (transparent))
        var monoShape = new byte[] { 0b0100_0000, 0b1000_0000 };
        DuplicationSource.BlendCursorPatch(
            bgra, patchX: 0, patchY: 0, patchW: 4, patchH: 2, rowPitch: 16, hdr: false,
            cursorX: 1, cursorY: 0, cursorW: 2, cursorH: 1, cursorPitch: 1,
            DuplicationSource.PointerMonochrome, monoShape);

        // Pixel (1,0): AND=0, XOR=1 -> opaque white (255,255,255,255)
        var p1 = 1 * 4;
        Assert.Equal((255, 255, 255, 255), (bgra[p1], bgra[p1 + 1], bgra[p1 + 2], bgra[p1 + 3]));
        // Pixel (2,0): AND=1, XOR=0 -> untouched blue (200,100,50,255)
        var p2 = 2 * 4;
        Assert.Equal((200, 100, 50, 255), (bgra[p2], bgra[p2 + 1], bgra[p2 + 2], bgra[p2 + 3]));

        // Color 1x1 cursor opaque red (B=0, G=0, R=255, A=255) at (0,0)
        var colorShape = new byte[] { 0, 0, 255, 255 };
        DuplicationSource.BlendCursorPatch(
            bgra, patchX: 0, patchY: 0, patchW: 4, patchH: 2, rowPitch: 16, hdr: false,
            cursorX: 0, cursorY: 0, cursorW: 1, cursorH: 1, cursorPitch: 4,
            DuplicationSource.PointerColor, colorShape);
        Assert.Equal((0, 0, 255, 255), (bgra[0], bgra[1], bgra[2], bgra[3]));
    }

    [Fact]
    public void HdrFloatPixelsConvertToSdrBgraBeforeShrinking()
    {
        // 2x2 scRGB FP16 pixels (1.0f white = 80 nits, 8 bytes/pixel) -> cropped to 2x1 and tone-mapped to 4 bpp BGRA8
        var halfs = new Half[2 * 2 * 4];
        for (var i = 0; i < halfs.Length; i += 4)
        {
            halfs[i] = (Half)1.0f;
            halfs[i + 1] = (Half)1.0f;
            halfs[i + 2] = (Half)1.0f;
            halfs[i + 3] = (Half)1.0f;
        }
        var raw = MemoryMarshal.AsBytes(halfs.AsSpan()).ToArray();
        var (bgra, w, h) = GoLive.ToSdrBgra(raw, 2, 2, bpp: 8, new Win32.RECT { Left = 0, Top = 0, Right = 2, Bottom = 1 }, sdrWhiteNits: 80f, peakNits: 1000f);

        Assert.Equal(2, w);
        Assert.Equal(1, h);
        Assert.Equal(2 * 1 * 4, bgra.Length);
        Assert.InRange(bgra[2], (byte)240, (byte)255);
        Assert.Equal(bgra[2], bgra[1]);
        Assert.Equal(bgra[2], bgra[0]);
        Assert.Equal((byte)255, bgra[3]);
    }
}
