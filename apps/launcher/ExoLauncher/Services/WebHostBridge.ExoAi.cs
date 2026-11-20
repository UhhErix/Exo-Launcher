using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// exoAi.state / exoAi.set: unified real-time status and preferences for Exo AI
/// (silent safe Best Settings, Autopilot telemetry, and anti-cheat-safe NVML GPU fan curves).
/// </summary>
public sealed partial class WebHostBridge
{
    private static object ExoAiState() => ToDto(ExoAiCoordinator.Shared.Snapshot());

    private object ExoAiSet(JsonElement p, bool hasParams)
    {
        bool? autoApply = hasParams && p.TryGetProperty("autoApplyBestSettings", out var aa) && aa.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? aa.GetBoolean()
            : null;
        bool? driftCheck = hasParams && p.TryGetProperty("backgroundDriftCheck", out var dc) && dc.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? dc.GetBoolean()
            : null;
        var fanPreset = ReadString(p, hasParams, "fanPreset");
        var runNow = hasParams && p.TryGetProperty("runNow", out var rn) && rn.ValueKind == JsonValueKind.True;

        ExoAiSettings? saved = null;
        if (autoApply is not null || driftCheck is not null || fanPreset is not null)
        {
            _services.Settings.UpdateProfile(s =>
            {
                var next = (s.ExoAi ?? new()).Copy();
                if (autoApply is { } a) next.AutoApplyBestSettings = a;
                if (driftCheck is { } d) next.BackgroundDriftCheck = d;
                if (fanPreset is { } f) next.FanPreset = ExoAiSettings.NormalizePreset(f);
                s.ExoAi = next;
                saved = next;
            });
            if (saved is not null) ExoAiCoordinator.Shared.NotifySettings(saved);
        }

        var snapshot = runNow ? ExoAiCoordinator.Shared.RunNow() : ExoAiCoordinator.Shared.Snapshot();
        return ToDto(snapshot, ok: true, message: null);
    }

    private static object ToDto(ExoAiSnapshot snap, bool ok = true, string? message = null) => new
    {
        ok,
        message,
        autoApplyBestSettings = snap.AutoApplyBestSettings,
        backgroundDriftCheck = snap.BackgroundDriftCheck,
        lastCheckUtc = snap.LastCheckUtc,
        autoApplied = snap.AutoApplied,
        needsAttention = snap.NeedsAttention,
        gaming = new
        {
            active = snap.Gaming.IsGaming,
            gameId = snap.Gaming.GameId,
            gameTitle = snap.Gaming.GameTitle,
            demotedCount = snap.Gaming.DemotedCount,
            timerResActive = snap.Gaming.TimerResActive,
            altTabbed = snap.Gaming.AltTabbed,
            lookEveryMs = AutopilotEngine.LookEveryMs,
            hogCores = AutopilotRules.HogCores,
        },
        fans = new
        {
            supported = snap.Fans.Supported,
            preset = snap.Fans.Preset,
            tempC = snap.Fans.TempC,
            fanPercent = snap.Fans.FanPercent,
            targetPercent = snap.Fans.TargetPercent,
            fanCount = snap.Fans.FanCount,
            note = snap.Fans.Note,
        },
    };
}
