using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Copy text could read the wrong thing, or fail where it shouldn't:
/// - a part dragged right to left or bottom to top reads nothing, or a part past the picture's edge asks Windows for pixels
///   that aren't there (it throws);
/// - a click with no drag (a part too small for a letter) reads the whole picture instead of saying there is nothing;
/// - no part asked is treated as nothing, not the whole picture;
/// - a picture larger than Windows reads is sent as it is (it refuses), or a small one is enlarged (memory for nothing);
/// - the text comes back with blank lines and stray spaces from the recognizer.
/// </summary>
public sealed class CaptureTextTests
{
    [Fact]
    public void NoPartIsTheWholePicture()
    {
        Assert.Null(CaptureText.Clamp(null, null, null, null, 1920, 1080, out var empty));
        Assert.False(empty);
    }

    [Fact]
    public void APartIsKeptInsideThePicture()
    {
        Assert.Equal(new CaptureText.Area(1800, 1000, 120, 80), CaptureText.Clamp(1800, 1000, 500, 500, 1920, 1080, out var empty));
        Assert.False(empty);
        Assert.Equal(new CaptureText.Area(0, 0, 50, 40), CaptureText.Clamp(-20, -10, 70, 50, 1920, 1080, out _));
    }

    [Fact]
    public void ADragInAnyDirectionIsTheSamePart() =>
        Assert.Equal(CaptureText.Clamp(100, 100, 200, 50, 1920, 1080, out _), CaptureText.Clamp(300, 150, -200, -50, 1920, 1080, out _));

    [Fact]
    public void FractionsGrowToWholePixels() =>
        Assert.Equal(new CaptureText.Area(10, 20, 31, 31), CaptureText.Clamp(10.6, 20.2, 30, 30.5, 1920, 1080, out _));

    [Theory]
    [InlineData(500d, 500d, 2d, 2d)]
    [InlineData(5000d, 100d, 50d, 50d)]
    [InlineData(10d, 10d, double.NaN, 40d)]
    public void APartWithNoRoomForALetterIsNothing(double x, double y, double w, double h)
    {
        Assert.Null(CaptureText.Clamp(x, y, w, h, 1920, 1080, out var empty));
        Assert.True(empty);
    }

    [Theory]
    [InlineData(1000u, 500u, 10000u, 1)]
    [InlineData(20000u, 5000u, 10000u, 0.5)]
    [InlineData(5000u, 30000u, 10000u, 1 / 3d)]
    [InlineData(0u, 0u, 10000u, 1)]
    public void APictureIsScaledDownToWhatWindowsReadsNeverUp(uint w, uint h, uint max, double scale) =>
        Assert.Equal(scale, CaptureText.Scale(w, h, max), 6);

    [Fact]
    public void LinesAreTrimmedAndBlankOnesGo() =>
        Assert.Equal("gg wp\nnext round", CaptureText.Join(["  gg wp ", "", null, "   ", "next round"]));

    [Fact]
    public void NoLinesIsNoText() => Assert.Equal("", CaptureText.Join([]));
}
