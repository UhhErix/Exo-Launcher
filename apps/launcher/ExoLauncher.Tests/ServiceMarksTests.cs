using ExoLauncher.Helpers;
using Windows.UI;

namespace ExoLauncher.Tests;

/// <summary>
/// The marks in the title band and taskbar of Exo's service windows (sign-ins and pop-outs). Ways they could fail: a
/// service whose window opens has no entry, so the window throws instead of opening; a mark falls back to Exo's own or
/// to a made-up one; Simple Icons' compact path data (".4.4", an arc's "01.591") is read wrong, so the mark draws
/// scrambled or the parser throws; the mark's brand color vanishes on the page (Steam's black on Void); the nonzero fill
/// is read as even-odd, filling a mark's cut-outs; the icon file is malformed, so Windows shows a blank in the taskbar.
/// </summary>
public sealed class ServiceMarksTests
{
    private static readonly Color Void = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color Light = Color.FromArgb(255, 0xF5, 0xF5, 0xF5);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    // Every service a window of Exo's chrome opens for: the web music players and the stores' sign-in windows.
    private static readonly string[] Keys = ["youtube", "apple", "spotify", "steam", "epic", "gog", "xbox"];
    public static TheoryData<string> Services => new(Keys);

    [Theory]
    [MemberData(nameof(Services))]
    public void EveryServiceWindowHasAMarkOrItsNameAlone(string service)
    {
        var mark = ServiceMarks.For(service);
        Assert.False(string.IsNullOrWhiteSpace(mark.Name));
        Assert.True(mark.Width >= 400 && mark.Height >= 600, service + " opens too small for its sign-in page.");
        if (mark.Mark is null)
        {
            Assert.Null(ServiceMarks.Icon(mark));
            return;
        }
        var figures = SvgPath.Parse(mark.Mark);
        Assert.NotEmpty(figures);
        // Simple Icons' 24-unit box, nothing outside it.
        var points = figures.SelectMany(f => f.Segments.Select(s => s.To).Prepend(f.Start)).ToList();
        Assert.All(points, p => Assert.InRange(p.X, -0.01, 24.01));
        Assert.All(points, p => Assert.InRange(p.Y, -0.01, 24.01));
        Assert.True(points.Max(p => p.X) - points.Min(p => p.X) > 16, service + "'s mark lost its width.");
    }

    [Fact]
    public void TheWebMusicServicesAndTheStoreSignInsAreTheListedOnes()
    {
        Assert.All(new[] { "youtube", "apple" }, service => Assert.True(MusicWebPolicy.IsService(service)));
        Assert.Equal(Keys.Order(), ServiceMarks.All.Keys.Order());
        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceMarks.For("exo"));
    }

    [Fact]
    public void OnlyXboxIsShownByItsNameAlone()
    {
        Assert.Equal(new[] { "xbox" }, ServiceMarks.All.Where(entry => entry.Value.Mark is null).Select(entry => entry.Key));
    }

    [Theory]
    [InlineData("youtube", true)]
    [InlineData("apple", true)]
    [InlineData("steam", false)]
    [InlineData("epic", false)]
    [InlineData("gog", false)]
    public void ABrandColorTooDarkForVoidTakesTheInk(string service, bool brand)
    {
        var mark = ServiceMarks.For(service);
        Assert.Equal(brand ? mark.Brand : White, ServiceMarks.Fill(mark, Void, White));
    }

    [Fact]
    public void OnLightTheDarkMarksKeepTheirBrandColor()
    {
        var ink = Color.FromArgb(255, 0x1E, 0x1E, 0x1E);
        Assert.Equal(ServiceMarks.For("steam").Brand, ServiceMarks.Fill(ServiceMarks.For("steam"), Light, ink));
        Assert.Equal(ServiceMarks.For("youtube").Brand, ServiceMarks.Fill(ServiceMarks.For("youtube"), Light, ink));
    }

    [Fact]
    public void CompactNumbersAndArcFlagsReadAsSvgDoes()
    {
        // ".4.4" is two numbers; "0 01.591.108" is a rotation, two flags, then x and y.
        var figure = Assert.Single(SvgPath.Parse("M1 1h1a.4.4 0 0 0-.4.4a1.708 1.708 0 01.591.108z"));
        Assert.True(figure.Closed);
        Assert.Equal(new SvgPath.Pt(2, 1), figure.Segments[0].To);
        Assert.Equal(1.6, figure.Segments[^2].To.X, 6);
        Assert.Equal(1.4, figure.Segments[^2].To.Y, 6);
        Assert.Equal(1.6 + 0.591, figure.Segments[^1].To.X, 6);
        Assert.Equal(1.4 + 0.108, figure.Segments[^1].To.Y, 6);
    }

    [Fact]
    public void AHalfCircleArcPassesThroughItsTop()
    {
        var figure = Assert.Single(SvgPath.Parse("M0 12a12 12 0 0 1 24 0"));
        Assert.Equal(new SvgPath.Pt(24, 0 + 12), figure.Segments[^1].To);
        var outline = Assert.Single(SvgPath.Outlines([figure], 1, 0, 0));
        Assert.InRange(outline.Min(p => p.Y), -0.01, 0.05);
        Assert.All(outline, p => Assert.InRange(Math.Sqrt((p.X - 12) * (p.X - 12) + (p.Y - 12) * (p.Y - 12)), 11.9, 12.1));
    }

    [Fact]
    public void ACutOutWoundTheOtherWayStaysEmpty()
    {
        // A 24 square with a 12 square cut out of its middle, wound the other way: nonzero leaves the hole.
        var figures = SvgPath.Parse("M0 0h24v24H0z M6 6v12h12V6z");
        var cover = ServiceMarks.Coverage(SvgPath.Outlines(figures, 1, 0, 0), 24);
        Assert.Equal(1, cover[0], 3);
        Assert.Equal(1, cover[23 * 24 + 23], 3);
        Assert.Equal(0, cover[12 * 24 + 12], 3);
        Assert.Equal(24 * 24 - 12 * 12, cover.Sum(), 1);
    }

    [Fact]
    public void TheIconIsAWellFormedIcoOfEverySize()
    {
        var bytes = ServiceMarks.Icon(ServiceMarks.For("youtube"));
        Assert.NotNull(bytes);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(ServiceMarks.IconSizes.Length, BitConverter.ToUInt16(bytes, 4));
        for (var n = 0; n < ServiceMarks.IconSizes.Length; n++)
        {
            var entry = 6 + 16 * n;
            var size = ServiceMarks.IconSizes[n];
            Assert.Equal(size, bytes[entry]);
            Assert.Equal(32, BitConverter.ToUInt16(bytes, entry + 6));
            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            Assert.Equal(40 + size * size * 4 + (size + 31) / 32 * 4 * size, length);
            Assert.InRange(offset + length, 0, bytes.Length);
            Assert.Equal(size, BitConverter.ToInt32(bytes, offset + 4));
            Assert.Equal(size * 2, BitConverter.ToInt32(bytes, offset + 8));
            // Bottom-up BGRA: the corner is outside the rounded plate, the middle is on it.
            Assert.Equal(0, bytes[offset + 40 + 3]);
            var middle = offset + 40 + ((size / 2) * size + size / 2) * 4;
            Assert.Equal(255, bytes[middle + 3]);
        }
    }

    [Fact]
    public void TheMarkIsDrawnInItsColorOnThePlate()
    {
        // YouTube Music's disc: its red ring passes 1.5 units in from the left edge of the mark.
        var bytes = ServiceMarks.Icon(ServiceMarks.For("youtube"))!;
        var n = Array.IndexOf(ServiceMarks.IconSizes, 64);
        var offset = BitConverter.ToInt32(bytes, 6 + 16 * n + 12);
        var markLeft = (64 - 64 * 0.64) / 2;
        var x = (int)(markLeft + 64 * 0.64 * 1.5 / 24);
        var row = 64 - 1 - 32;
        var pixel = offset + 40 + (row * 64 + x) * 4;
        Assert.True(bytes[pixel + 2] > 200 && bytes[pixel + 1] < 60 && bytes[pixel] < 60, "The ring isn't red.");
    }
}
