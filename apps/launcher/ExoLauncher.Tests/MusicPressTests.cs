using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The trusted press on Spotify's play button. Ways it fails: the press is a script click (no user activation, so playback
/// is refused); the press has a bad coordinate; the press is sent as one event, not press then release.
/// </summary>
public sealed class MusicPressTests
{
    [Fact]
    public void APressIsMousePressedThenReleasedAtTheCentre()
    {
        var commands = MusicPress.ClickCommands(120.3, 48)!;
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Equal("Input.dispatchMouseEvent", c.Method));
        Assert.Equal("""{"type":"mousePressed","x":120.3,"y":48,"button":"left","clickCount":1}""", commands[0].Params);
        Assert.Equal("""{"type":"mouseReleased","x":120.3,"y":48,"button":"left","clickCount":1}""", commands[1].Params);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.PositiveInfinity)]
    [InlineData(-1, 10)]
    [InlineData(10, -0.5)]
    public void ABadCoordinateIsNotPressed(double x, double y) =>
        Assert.Null(MusicPress.ClickCommands(x, y));

    [Fact]
    public void TheCentreScriptReadsTheSpotifyPlayButtonAndNeverClicks()
    {
        Assert.Contains("control-button-playpause", MusicPress.SpotifyPlayButtonCentre);
        Assert.Contains("getBoundingClientRect", MusicPress.SpotifyPlayButtonCentre);
        Assert.DoesNotContain(".click(", MusicPress.SpotifyPlayButtonCentre);
    }
}
