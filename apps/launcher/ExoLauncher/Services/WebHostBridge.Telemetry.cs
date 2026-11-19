using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Settings > Privacy: the last payloads, Delete my data, and the one event the page reports (the room it shows). App data
/// and crash reports have no switch (owner, 2026-11-09). The sender and its schema are
/// in <see cref="Telemetry"/>; the page never sends an event name or a value of its own.
/// </summary>
public sealed partial class WebHostBridge
{

    private async Task<object> TelemetryDeleteAsync(CancellationToken ct)
    {
        var message = await Telemetry.DeleteAsync(ct).ConfigureAwait(true);
        AppLog.Info(message is null ? "Telemetry: Delete my data done; a new install ID was made." : "Telemetry: Delete my data did not reach the server.");
        return new { ok = message is null, message };
    }

    /// <summary>telemetry.track: the room the page shows, the only event the page reports (room.opened, schema-checked).</summary>
    private static object TelemetryTrack(JsonElement p, bool hasParams)
    {
        if (ReadString(p, hasParams, "room") is { } room) Telemetry.Track("room.opened", ("room", room));
        return new { ok = true };
    }
}
