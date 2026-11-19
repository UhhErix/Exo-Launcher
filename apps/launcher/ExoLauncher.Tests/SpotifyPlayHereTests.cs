using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Spotify's music can be refused, and what Exo does about the Spotify desktop app:
/// - a Spotify process that stays after WM_CLOSE is ended by its exact PID, never one Spotify started after the close;
/// - the refusal names its cause: another active Connect device (offer Play here, to the web player's device id),
///   or the autoplay policy or the play selector (not separable from the page), or unknown when devices cannot be read.
/// </summary>
public sealed class SpotifyPlayHereTests
{
    [Fact]
    public void OnlyTheProcessesThatStayAfterCloseAreEnded()
    {
        // 10 closed on WM_CLOSE, 11 stayed, 12 started after the snapshot (not ours to end).
        Assert.Equal([11], SpotifyApp.PidsToEnd([10, 11], [11, 12]));
    }

    [Fact]
    public void NothingStaysWhenEverythingClosed()
    {
        Assert.Empty(SpotifyApp.PidsToEnd([10, 11], [12]));
    }

    [Fact]
    public void AnotherActiveDeviceIsTheCauseAndCarriesThisDeviceForPlayHere()
    {
        using var doc = JsonDocument.Parse("""{"devices":[{"id":"desk","name":"Erix's PC","active":true,"local":false},{"id":"web","name":"Exo","active":false,"local":true}]}""");
        var refusal = MusicControlRefusal.Classify(doc.RootElement);
        Assert.Equal("another-device", refusal.Name);
        Assert.Equal("web", refusal.DeviceId);
        Assert.Equal("Erix's PC", refusal.DeviceName);
    }

    [Fact]
    public void AnotherActiveDeviceWithoutThisOneHasNoPlayHereTarget()
    {
        using var doc = JsonDocument.Parse("""{"devices":[{"id":"desk","name":"Desk","active":true,"local":false}]}""");
        var refusal = MusicControlRefusal.Classify(doc.RootElement);
        Assert.Equal("another-device", refusal.Name);
        Assert.Null(refusal.DeviceId);
    }

    [Fact]
    public void ThisPlayerActiveMeansAutoplayOrSelector()
    {
        using var doc = JsonDocument.Parse("""{"devices":[{"id":"web","name":"Exo","active":true,"local":true}]}""");
        Assert.Equal("autoplay-or-selector", MusicControlRefusal.Classify(doc.RootElement).Name);
    }

    [Fact]
    public void AnUnreadableDeviceListIsUnknown()
    {
        Assert.Equal("unknown", MusicControlRefusal.Classify(null).Name);
        using var doc = JsonDocument.Parse("""{"error":"signed-out"}""");
        Assert.Equal("unknown", MusicControlRefusal.Classify(doc.RootElement).Name);
    }
}
