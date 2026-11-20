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
}
