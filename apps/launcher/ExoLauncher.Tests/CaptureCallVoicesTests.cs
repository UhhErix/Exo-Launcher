using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could tell a Discord call it's recorded (CLIPS_ENABLED, flag 1) wrongly, from Exo Capture's state, Capture
/// settings › Record the call's voices, and being in a call:
/// - it flags while Exo Capture leaves the call out (everyone in the call told they may be recorded when they aren't);
/// - it doesn't flag while a replay or recording holds the call's voices (people recorded without being told);
/// - the flag stays once the replay and recording have stopped, or the add-on isn't running;
/// - an older add-on that doesn't say whether it takes the call (every one before 3.18 did) is taken as leaving it out;
/// - flag 2 (Discord's own clips may record this voice) is lost;
/// - a settings file from before the setting turns the call's voices on.
/// </summary>
public sealed class CaptureCallVoicesTests
{
    private static int Flags(string? state, bool inCall) =>
        DiscordSession.VoiceFlags(inCall, MainWindow.CaptureHoldsCall(state is null ? null : JsonNode.Parse(state)));

    [Theory]
    // Replay or recording, the call left out (the default): never flagged.
    [InlineData("""{"buffering":true,"recording":false,"callVoices":false}""", true, 2)]
    [InlineData("""{"buffering":false,"recording":true,"callVoices":false}""", true, 2)]
    [InlineData("""{"buffering":true,"recording":true,"callVoices":false}""", true, 2)]
    // Record the call's voices on: flagged while it buffers or records.
    [InlineData("""{"buffering":true,"recording":false,"callVoices":true}""", true, 3)]
    [InlineData("""{"buffering":false,"recording":true,"callVoices":true}""", true, 3)]
    [InlineData("""{"buffering":true,"recording":true,"callVoices":true}""", true, 3)]
    // On, but nothing buffered or recorded: not flagged.
    [InlineData("""{"buffering":false,"recording":false,"callVoices":true}""", true, 2)]
    [InlineData("""{"buffering":false,"recording":false}""", true, 2)]
    // The add-on not running (exited, or never started).
    [InlineData(null, true, 2)]
    // An add-on from before 3.18 always took the call: flagged while it captures.
    [InlineData("""{"buffering":true,"recording":false}""", true, 3)]
    [InlineData("""{"buffering":false,"recording":true}""", true, 3)]
    // Out of a call (leaving one): never flagged.
    [InlineData("""{"buffering":true,"recording":true,"callVoices":true}""", false, 2)]
    [InlineData("""{"buffering":true,"recording":false,"callVoices":false}""", false, 2)]
    public void TheCallIsToldOnlyWhileACaptureHoldsItsVoices(string? state, bool inCall, int flags) =>
        Assert.Equal(flags, Flags(state, inCall));

    [Fact]
    public void SettingsFromBeforeLeaveTheCallOut()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var old = JsonSerializer.Deserialize<MainWindow.CaptureSettings>("""{"quality":50,"replayMode":"games","autoShrink":true}""", options)!;
        Assert.False(old.CallVoices);
        Assert.False(new MainWindow.CaptureSettings().CallVoices);
        var on = JsonSerializer.Deserialize<MainWindow.CaptureSettings>(JsonSerializer.Serialize(new MainWindow.CaptureSettings { CallVoices = true }, options), options)!;
        Assert.True(on.CallVoices);
    }
}
