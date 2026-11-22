using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// autopilot.state: PC › Autopilot. The switch and its choices, where Windows Game Mode stands, this processor's cache layout,
/// what Autopilot does for the game running now, the game being watched (its frames so far and the last second's numbers) and the
/// session reports kept on this PC, newest first. autopilot.set changes the switch or a choice (Game Mode follows the switch at
/// once; turning it off puts back what the running game's session changed). autopilot.report tells the page a new report was
/// kept; nothing is said during play.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly Lazy<string> CcdText = new(() =>
    {
        try { return CcdLayout.Of(MouseCore.Sampler.ReadTopology()).Describe(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "The processor's layout couldn't be read"; }
    });

    private static object AutopilotState()
    {
        var settings = Autopilot.Settings;
        var keptOn = AutopilotGameMode.KeptOnBecause(settings, Autopilot.CpuClass);
        var game = Autopilot.Engine.Game;
        var integrityItem = SystemTuning.Shared.ReadMemoryIntegrityOnly();
        var integrityBlocked = integrityItem is { On: true, Restart: false } ? SystemTuning.Shared.IntegrityBlocked() : null;
        return new
        {
            on = settings.On,
            gamePriority = settings.GamePriority,
            keepGameMode = settings.KeepGameMode,
            coreParking = settings.CoreParking,
            alwaysDemote = settings.AlwaysDemote,
            suspend = settings.Suspend,
            gameMode = new { off = Autopilot.GameMode.Held is not null, keptOn = settings.On ? keptOn : null },
            mouseAccel = new { off = Autopilot.MouseAccel.IsOff(), managed = Autopilot.MouseAccel.Held is not null },
            memoryIntegrity = DescribeMemoryIntegrity(integrityItem, integrityBlocked),
            cpu = CcdText.Value,
            now = game is null ? null : new { game, did = Autopilot.Engine.Did(game) },
            live = AutopilotLive(),
            reports = AutopilotReports.Load(AutopilotReports.DefaultPath).Take(10).Select(AutopilotReportDto).ToList(),
        };
    }

    internal static object DescribeMemoryIntegrity(SystemTuning.State state, string? blocked) =>
        DescribeMemoryIntegrity(state.Items.FirstOrDefault(i => i.Key == SystemTuning.IntegrityKey), blocked);

    internal static object DescribeMemoryIntegrity(SystemTuning.Item? item, string? blocked)
    {
        if (item is null)
            return new { status = "off", reason = "Off on this PC for best gaming performance.", canUndo = false, needsRestart = false };
        if (item.On)
        {
            if (item.Restart)
                return new { status = "pending-off", reason = "Off after you restart Windows.", canUndo = item.CanUndo, needsRestart = true };
            if (blocked is not null)
                return new { status = "conflict", reason = blocked, canUndo = item.CanUndo, needsRestart = false };
            return new { status = "off", reason = "Off for +5–10% FPS. None of your installed games require it.", canUndo = item.CanUndo, needsRestart = false };
        }
        if (item.Restart)
            return new { status = "pending-on", reason = "Back on after you restart Windows.", canUndo = false, needsRestart = true };
        if (item.Blocked is not null)
        {
            var isLocked = item.Blocked.Contains("policy", StringComparison.OrdinalIgnoreCase)
                || item.Blocked.Contains("firmware", StringComparison.OrdinalIgnoreCase);
            return new { status = isLocked ? "locked" : "required", reason = item.Blocked, canUndo = false, needsRestart = false };
        }
        return new
        {
            status = "unlocked",
            reason = "None of your installed games require Memory Integrity. Turning it off can add 5–10% FPS (lowers kernel driver protection; takes a restart).",
            canUndo = false,
            needsRestart = true,
        };
    }

    /// <summary>Updates per-app demote/suspend lists while keeping Autopilot always active with Exo's best settings.</summary>
    private object AutopilotSet(JsonElement p, bool hasParams)
    {
        List<string>? Names(string name) => hasParams && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => AutopilotSafety.Bare(item.GetString())).Where(item => item.Length is > 0 and <= 128)]
            : null;
        var demote = Names("alwaysDemote");
        var suspend = Names("suspend");
        _services.Settings.UpdateProfile(s =>
        {
            var next = Autopilot.LiveSettings(s.Autopilot);
            if (demote is not null) next.AlwaysDemote = demote;
            // An app on the safety list is never paused, whatever the page sent.
            if (suspend is not null) next.Suspend = [.. suspend.Where(name => AutopilotSafety.WhyLeftAlone(name) is null)];
            s.Autopilot = next.Copy();
        });
        Autopilot.HandGameMode();
        Autopilot.HandMouseAccel();
        ArmRunStatePoll(RunStateDebounce);
        AppLog.Info("Autopilot: app rules updated.");
        return new { ok = true, message = (string?)null, state = AutopilotState() };
    }

    private static object? AutopilotLive()
    {
        if (AutopilotWatch.Shared.Live is not { } live) return null;
        var s = live.Last;
        return new
        {
            game = live.Game,
            title = live.Title,
            seconds = Math.Round(live.Frames.Seconds),
            fps = Math.Round(live.Frames.AverageFps, 1),
            low1 = Math.Round(live.Frames.Low1Fps, 1),
            low01 = Math.Round(live.Frames.Low01Fps, 1),
            stutters = live.Stutters,
            cpu = s?.CpuPercent,
            gpu = s?.GpuPercent,
            gpuTemp = s?.GpuTemperature,
            vramGb = Gb(s?.VramUsed),
            vramTotalGb = Gb(s?.VramTotal),
            ramGb = Gb(s?.RamUsed),
            ramTotalGb = Gb(s?.RamTotal),
            standbyGb = Gb(s?.Standby),
            freeGb = Gb(s?.Free),
            dpc = s?.DpcPercent is { } dpc ? Math.Round(dpc, 1) : (double?)null,
            clock = s?.CpuPerformance is { } perf ? Math.Round(perf) : (double?)null,
        };
    }

    private static double? Gb(long? bytes) => bytes is { } b ? Math.Round(b / (double)(1L << 30), 1) : null;

    private static object AutopilotReportDto(AutopilotReport r) => new
    {
        id = $"{r.Game}:{r.StartedUtc:O}",
        game = r.Game,
        title = r.Title,
        started = r.StartedUtc,
        minutes = Math.Round(r.Seconds / 60),
        fps = r.AverageFps,
        low1 = r.Low1Fps,
        low01 = r.Low01Fps,
        stutters = r.Stutters,
        findings = r.Findings.Select(f => new { kind = f.Kind, stutters = f.Stutters, app = f.App, detail = f.Detail, suggestion = f.Suggestion }).ToList(),
        did = r.Did,
        learned = r.Learned,
        peaks = r.Peaks is { } p ? new
        {
            cpu = Math.Round(p.CpuPercent),
            gpu = p.GpuPercent,
            gpuTemp = p.GpuTemperature,
            vramGb = Gb(p.VramUsed),
            vramTotalGb = Gb(p.VramTotal),
            ramGb = Gb(p.RamUsed),
            ramTotalGb = Gb(p.RamTotal),
        } : null,
        islcSeconds = r.IslcSeconds,
    };

    private void OnAutopilotReported(AutopilotReport report)
    {
        AutopilotReports.InvalidateCache(AutopilotReports.DefaultPath);
        PostEvent("autopilot.report", new { game = report.Game, title = report.Title });
    }

    private void OnAutopilotLiveChanged() => PostEvent("autopilot.live", AutopilotLive());
}
