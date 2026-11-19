using System.Text.Json.Nodes;

namespace ExoLauncher.Services;

internal readonly record struct CaptureReplayBudget(int Seconds, long? MaxBytes)
{
    private const long MiB = 1 << 20;
    private const long LegacyBytesPerSecond = 16_000_000;

    internal static CaptureReplayBudget For(int requestedSeconds, ulong physicalMemory, bool supportsBudget)
    {
        var bytes = physicalMemory == 0 ? 512 * MiB : Math.Clamp((long)(physicalMemory / 8), 128 * MiB, 1_536 * MiB);
        var seconds = Math.Clamp(requestedSeconds, 30, 600);
        // Older add-ons only take seconds. Size that window at 128 Mb/s; this is not a byte cap.
        return supportsBudget ? new(seconds, bytes) : new(Math.Min(seconds, Math.Max(30, (int)(bytes / LegacyBytesPerSecond))), null);
    }

    internal static int? RetainedMs(JsonNode? state)
    {
        if (state is JsonObject obj && obj["buffering"] is JsonValue buffering
            && buffering.TryGetValue<bool>(out var active) && active
            && obj["replayRetainedMs"] is JsonValue retained && retained.TryGetValue<double>(out var ms)
            && ms is >= 0 and <= 601_000)
            return (int)ms;
        return null;
    }
}
