using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// Why Spotify's web player refused a play or pause (its page answered false). The page's own devices list says whether
/// another device is the active Spotify Connect device: then the web player cannot take the control, and "Play here"
/// moves playback to it. Otherwise the cause is the autoplay policy or the play selector, which the page cannot tell apart.
/// </summary>
internal static class MusicControlRefusal
{
    internal sealed record Cause(string Name, string? DeviceId, string? DeviceName);

    internal static Cause Classify(JsonElement? devicesPage)
    {
        if (devicesPage is not { ValueKind: JsonValueKind.Object } page ||
            !page.TryGetProperty("devices", out var list) || list.ValueKind != JsonValueKind.Array)
            return new Cause("unknown", null, null);

        string? thisDevice = null;
        foreach (var device in list.EnumerateArray())
            if (Flag(device, "local") && Text(device, "id") is { } id) thisDevice = id;
        foreach (var device in list.EnumerateArray())
            if (Flag(device, "active") && !Flag(device, "local"))
                return new Cause("another-device", thisDevice, Text(device, "name"));
        return new Cause("autoplay-or-selector", thisDevice, null);
    }

    private static bool Flag(JsonElement value, string name) =>
        value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
}
