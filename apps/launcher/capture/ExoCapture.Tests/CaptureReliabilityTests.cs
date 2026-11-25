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
    public void TryCaptureStepsFollowsOrderStopsOnSuccessAndCollectsAllFailureReasons()
    {
        var order = new List<string>();
        var logs = new List<string>();

        var okFirst = Controller.TryCaptureSteps(
            [
                new("duplication", () => { order.Add("duplication"); return true; }),
                new("Windows.Graphics.Capture", () => { order.Add("wgc"); return true; }),
                new("GDI", () => { order.Add("gdi"); return true; }),
            ],
            out var firstReason,
            logs.Add);
        Assert.True(okFirst);
        Assert.Null(firstReason);
        Assert.Equal(["duplication"], order);
        Assert.Empty(logs);

        order.Clear();
        var okWgc = Controller.TryCaptureSteps(
            [
                new("duplication", () => { order.Add("duplication"); throw new CaptureException("capture the screen (0x80070005)"); }),
                new("Windows.Graphics.Capture", () => { order.Add("wgc"); return true; }),
                new("GDI", () => { order.Add("gdi"); return true; }),
            ],
            out var wgcReason,
            logs.Add);
        Assert.True(okWgc);
        Assert.Null(wgcReason);
        Assert.Equal(["duplication", "wgc"], order);
        Assert.Single(logs);
        Assert.Contains("0x80070005", logs[0]);

        order.Clear();
        logs.Clear();
        var allFailed = Controller.TryCaptureSteps(
            [
                new("duplication", () => { order.Add("duplication"); throw new CaptureException("capture the screen (0x80070005)"); }),
                new("Windows.Graphics.Capture", () => { order.Add("wgc"); return false; }),
                new("GDI", () => { order.Add("gdi"); throw new CaptureException("GDI returned a black image"); }),
            ],
            out var failReason,
            logs.Add);
        Assert.False(allFailed);
        Assert.Equal(["duplication", "wgc", "gdi"], order);
        Assert.NotNull(failReason);
        Assert.Contains("0x80070005", failReason);
        Assert.Contains("Windows.Graphics.Capture: no frame", failReason);
        Assert.Contains("GDI returned a black image", failReason);
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public void DuplicatorRejectsUnpresentedFramesAndRetriesTransientAccessErrors()
    {
        Assert.False(Duplicator.HasDesktopImage(0));
        Assert.True(Duplicator.HasDesktopImage(1));

        Assert.True(Duplicator.IsTransientAccessError(Duplicator.ErrorAccessLost));
        Assert.True(Duplicator.IsTransientAccessError(Duplicator.ErrorAccessDenied));
        Assert.False(Duplicator.IsTransientAccessError(unchecked((int)0x80070057)));
        Assert.False(Duplicator.IsTransientAccessError(0));

        var sleeps = new List<int>();
        var attempts = 0;
        var recovered = Duplicator.RetryAccess(
            () =>
            {
                attempts++;
                if (attempts == 1) throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(Duplicator.ErrorAccessLost));
                if (attempts == 2) throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(Duplicator.ErrorAccessDenied));
                return "ok";
            },
            maxAttempts: 3,
            delayMs: 25,
            sleep: sleeps.Add);
        Assert.Equal("ok", recovered);
        Assert.Equal(3, attempts);
        Assert.Equal([25, 50], sleeps);

        var nonTransientAttempts = 0;
        Assert.Throws<SharpGen.Runtime.SharpGenException>(() => Duplicator.RetryAccess<string>(
            () =>
            {
                nonTransientAttempts++;
                throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(unchecked((int)0x80070057)));
            },
            maxAttempts: 3,
            delayMs: 10,
            sleep: _ => { }));
        Assert.Equal(1, nonTransientAttempts);
    }

    [Fact]
    public void CopyMonitorBgraWritesOpaqueAlphaAndFormatsFreezeFailure()
    {
        var screen = new ExoCapture.Native.Win32.RECT { Left = 0, Top = 0, Right = 4, Bottom = 2 };
        var monitor = new ExoCapture.Native.Win32.RECT { Left = 2, Top = 0, Right = 4, Bottom = 2 };
        var bgra = new byte[4 * 2 * 4];
        var src = new byte[2 * 2 * 4];
        src[0] = 10; src[1] = 20; src[2] = 30; src[3] = 0;
        src[4] = 40; src[5] = 50; src[6] = 60; src[7] = 0;

        Controller.CopyMonitorBgra(bgra, screen, monitor, src, 2, 2);

        Assert.Equal((10, 20, 30, 255), (bgra[8], bgra[9], bgra[10], bgra[11]));
        Assert.Equal((40, 50, 60, 255), (bgra[12], bgra[13], bgra[14], bgra[15]));
        Assert.Contains("duplication", Controller.FormatFreezeFailure(["duplication: 0x80070005"]));
        Assert.Equal("Couldn't capture the screen.", Controller.FormatFreezeFailure([]));
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

    [Fact]
    public void DeviceLostHresultsAndErrorDialogSuppressionFlagsAreConfigured()
    {
        Assert.True(GpuDevice.IsDeviceLost(unchecked((int)0x887A0005)));
        Assert.True(GpuDevice.IsDeviceLost(unchecked((int)0x887A0007)));
        Assert.False(GpuDevice.IsDeviceLost(unchecked((int)0x887A0026)));
        Assert.False(GpuDevice.IsDeviceLost(0));

        Assert.Equal(0x8003u, ExoCapture.Native.Win32.ErrorModeNoDialogs);
        Assert.Equal(0x0020u, ExoCapture.Native.Win32.WER_FAULT_REPORTING_NO_UI);
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

    [Fact]
    public void DeviceLostHresultsAndErrorDialogSuppressionFlagsAreConfigured()
    {
        Assert.True(GpuDevice.IsDeviceLost(unchecked((int)0x887A0005)));
        Assert.True(GpuDevice.IsDeviceLost(unchecked((int)0x887A0007)));
        Assert.False(GpuDevice.IsDeviceLost(unchecked((int)0x887A0026)));
        Assert.False(GpuDevice.IsDeviceLost(0));

        Assert.Equal(0x8003u, ExoCapture.Native.Win32.ErrorModeNoDialogs);
        Assert.Equal(0x0020u, ExoCapture.Native.Win32.WER_FAULT_REPORTING_NO_UI);
    }
}
