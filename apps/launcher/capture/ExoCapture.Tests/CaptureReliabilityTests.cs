using ExoCapture.Capture;
using ExoCapture.Gpu;
using ExoCapture.Live;
using Vortice.DXGI;

namespace ExoCapture.Tests;

public sealed class CaptureReliabilityTests
{
    [Fact]
    public void IsAllBlackDetectsEmptyZeroOpaqueBlackAndVisiblePixels()
    {
        Assert.True(GoLive.IsAllBlack([]));
        Assert.True(GoLive.IsAllBlack(new byte[256]));

        var opaqueBlack = new byte[256];
        for (var i = 3; i < opaqueBlack.Length; i += 4) opaqueBlack[i] = 255;
        Assert.True(GoLive.IsAllBlack(opaqueBlack));

        var firstPixelLit = new byte[256];
        firstPixelLit[2] = 12;
        Assert.False(GoLive.IsAllBlack(firstPixelLit));

        var midStrideLit = new byte[256];
        midStrideLit[128] = 42;
        Assert.False(GoLive.IsAllBlack(midStrideLit));

        var lastPixelLit = new byte[256];
        lastPixelLit[^3] = 255;
        Assert.False(GoLive.IsAllBlack(lastPixelLit));
    }

    [Fact]
    public void NeedsNewConverterDetectsSizeAndFormatChanges()
    {
        Assert.False(CaptureSession.NeedsNewConverter(1920, 1080, Format.B8G8R8A8_UNorm, 1920, 1080, Format.B8G8R8A8_UNorm));
        Assert.True(CaptureSession.NeedsNewConverter(1920, 1080, Format.B8G8R8A8_UNorm, 2560, 1080, Format.B8G8R8A8_UNorm));
        Assert.True(CaptureSession.NeedsNewConverter(1920, 1080, Format.B8G8R8A8_UNorm, 1920, 1440, Format.B8G8R8A8_UNorm));
        Assert.True(CaptureSession.NeedsNewConverter(1920, 1080, Format.B8G8R8A8_UNorm, 1920, 1080, Format.R16G16B16A16_Float));
    }

    [Fact]
    public void CanReuseCursorFrameMatchesIdenticalFramePositionAndShape()
    {
        var shapeA = new object();
        var shapeB = new object();

        Assert.True(DuplicationSource.CanReuseCursorFrame(1000, 10, 20, shapeA, 1000, 10, 20, shapeA));
        Assert.False(DuplicationSource.CanReuseCursorFrame(1000, 10, 20, shapeA, 1001, 10, 20, shapeA));
        Assert.False(DuplicationSource.CanReuseCursorFrame(1000, 10, 20, shapeA, 1000, 11, 20, shapeA));
        Assert.False(DuplicationSource.CanReuseCursorFrame(1000, 10, 20, shapeA, 1000, 10, 21, shapeA));
        Assert.False(DuplicationSource.CanReuseCursorFrame(1000, 10, 20, shapeA, 1000, 10, 20, shapeB));
        Assert.False(DuplicationSource.CanReuseCursorFrame(1000, 10, 20, null, 1000, 10, 20, shapeA));
    }

    [Fact]
    public void NeedsHdrToSdrToneMapOnlyWhenScRgbConvertedForSdrOutput()
    {
        Assert.True(VideoConverter.NeedsHdrToSdrToneMap(Format.R16G16B16A16_Float, hdr: false));
        Assert.False(VideoConverter.NeedsHdrToSdrToneMap(Format.R16G16B16A16_Float, hdr: true));
        Assert.False(VideoConverter.NeedsHdrToSdrToneMap(Format.B8G8R8A8_UNorm, hdr: false));
        Assert.False(VideoConverter.NeedsHdrToSdrToneMap(Format.B8G8R8A8_UNorm, hdr: true));
    }

    [Fact]
    public void TryGrabOrFallbackFallsBackOnCaptureExceptionOrFalseAndSkipsOnSuccess()
    {
        var fallbackCalls = 0;
        var logs = new List<string>();

        var okPrimary = Controller.TryGrabOrFallback(
            () => true,
            () => { fallbackCalls++; return true; },
            logs.Add);
        Assert.True(okPrimary);
        Assert.Equal(0, fallbackCalls);
        Assert.Empty(logs);

        var okFalse = Controller.TryGrabOrFallback(
            () => false,
            () => { fallbackCalls++; return true; },
            logs.Add);
        Assert.True(okFalse);
        Assert.Equal(1, fallbackCalls);
        Assert.Empty(logs);

        var okException = Controller.TryGrabOrFallback(
            () => throw new CaptureException("capture the screen (0x80070005)"),
            () => { fallbackCalls++; return true; },
            logs.Add);
        Assert.True(okException);
        Assert.Equal(2, fallbackCalls);
        Assert.Single(logs);
        Assert.Contains("0x80070005", logs[0]);
    }

    [Fact]
    public void SelectAdapterIndexMatchesLuidFallsBackOnStaleLuidAndSkipsSoftwareAdapters()
    {
        GpuDevice.AdapterInfo[] adapters =
        [
            new(0x1001, "Microsoft Basic Render Driver", Software: true),
            new(0x2002, "Intel UHD Graphics", Software: false),
            new(0x3003, "NVIDIA GeForce RTX 4090", Software: false),
        ];

        Assert.Equal(2, GpuDevice.SelectAdapterIndex(adapters, 0x3003, out var exactFallback));
        Assert.False(exactFallback);

        Assert.Equal(1, GpuDevice.SelectAdapterIndex(adapters, 0x9999, out var staleFallback));
        Assert.True(staleFallback);

        Assert.Equal(1, GpuDevice.SelectAdapterIndex(adapters, null, out var defaultFallback));
        Assert.False(defaultFallback);

        GpuDevice.AdapterInfo[] softwareOnly = [new(0x1001, "Microsoft Basic Display Adapter", Software: true)];
        Assert.Null(GpuDevice.SelectAdapterIndex(softwareOnly, null, out var softwareFallback));
        Assert.False(softwareFallback);
        Assert.Null(GpuDevice.SelectAdapterIndex([], 0x1001, out _));
    }

    [Fact]
    public void GpuDeviceCreateFailureDiagnosticsAndFeatureLevelRetryAreFormatted()
    {
        Assert.True(GpuDevice.ShouldRetryWithout11_1(unchecked((int)0x80070057)));
        Assert.False(GpuDevice.ShouldRetryWithout11_1(unchecked((int)0x887A0005)));

        var line = GpuDevice.FormatCreateFailure(
            "Microsoft Basic Display Adapter",
            unchecked((int)0x887A0004),
            Vortice.Direct3D11.DeviceCreationFlags.BgraSupport | Vortice.Direct3D11.DeviceCreationFlags.VideoSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0]);
        Assert.Contains("Microsoft Basic Display Adapter", line);
        Assert.Contains("0x887A0004", line);
        Assert.Contains("VideoSupport", line);
        Assert.Contains("Level_11_1", line);
        Assert.Contains("Level_11_0", line);
    }

    [Fact]
    public void ProbeCodecAvailabilityNeverThrowsLogsOnceAndRecoversWhenGpuReturns()
    {
        var logs = new List<string>();
        string? error = null;
        var logged = false;

        Assert.False(Controller.ProbeCodecAvailability(
            () => throw new CaptureException("Capture unavailable on this GPU."),
            logs.Add,
            ref error,
            ref logged));
        Assert.Equal("Capture unavailable on this GPU.", error);
        Assert.True(logged);
        Assert.Single(logs);

        Assert.False(Controller.ProbeCodecAvailability(
            () => throw new CaptureException("Capture unavailable on this GPU."),
            logs.Add,
            ref error,
            ref logged));
        Assert.Single(logs);

        Assert.True(Controller.ProbeCodecAvailability(() => true, logs.Add, ref error, ref logged));
        Assert.Null(error);
        Assert.False(logged);
    }
}
