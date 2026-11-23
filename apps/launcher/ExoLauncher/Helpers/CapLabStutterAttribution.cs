using System.Globalization;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal sealed record CapLabTraceSecond(
    IReadOnlyList<double> FrameTimesMs,
    SystemSecond? System,
    int? ActiveCapFps = null,
    string? CapBackend = null,
    double RefreshHz = 0,
    bool VrrEnabled = false,
    long? ProcessVramBytes = null,
    long? ProcessVramBudgetBytes = null,
    long? BackgroundDiskBytesPerSec = null);

internal sealed record StutterCauseBreakdown(
    string Cause,
    string Label,
    int Spikes,
    double SharePercent,
    string Detail,
    string Hint);

internal sealed record CapLabStutterReport(
    int TotalSpikes,
    string TopCause,
    string TopCauseLabel,
    string Summary,
    bool PerProcessVramAvailable,
    IReadOnlyList<StutterCauseBreakdown> Breakdown);

internal static class CapLabStutterAttribution
{
    internal const string CauseNone = "none";
    internal const string CauseShaderCompile = "shader_compile";
    internal const string CauseLoadingGap = "loading_gap";
    internal const string CauseBackgroundCpu = "background_cpu";
    internal const string CauseBackgroundDisk = "background_disk";
    internal const string CauseVramBudget = "vram_budget";
    internal const string CauseFrameCap = "frame_cap";
    internal const string CauseGpuThermal = "gpu_thermal";
    internal const string CauseCpuThrottle = "cpu_throttle";
    internal const string CauseDriverDpc = "driver_dpc";
    internal const string CauseGameEngine = "game_engine";

    internal const double LoadingGapMs = 150.0;
    internal const long BackgroundDiskBurstBytes = 20L << 20;
    internal const double ProcessVramBudgetRatio = 0.95;

    internal static CapLabStutterReport Analyze(IReadOnlyList<CapLabTraceSecond> seconds)
    {
        if (seconds.Count == 0)
        {
            return new CapLabStutterReport(
                0,
                CauseNone,
                "None",
                "No gameplay frame trace recorded yet.",
                PerProcessVramAvailable: false,
                Breakdown: []);
        }

        var recent = new Queue<double>();
        var appBaselines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double? gameCoresBaseline = null;
        double? cpuPerfBaseline = null;
        SystemSecond? previousSystem = null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalSpikes = 0;
        var hasPerProcessVram = seconds.Any(s => s.ProcessVramBytes is not null && s.ProcessVramBudgetBytes is > 0);

        for (var idx = 0; idx < seconds.Count; idx++)
        {
            var sec = seconds[idx];
            var valid = new List<double>();
            var loadingGaps = 0;
            for (var i = 0; i < sec.FrameTimesMs.Count; i++)
            {
                var ms = sec.FrameTimesMs[i];
                if (!(ms > 0) || double.IsNaN(ms) || double.IsInfinity(ms)) continue;
                if (ms >= LoadingGapMs && ms < FrameStats.NotAFrameMs) loadingGaps++;
                if (ms < FrameStats.NotAFrameMs) valid.Add(ms);
            }

            if (valid.Count == 0)
            {
                previousSystem = sec.System ?? previousSystem;
                continue;
            }

            var avgMs = valid.Average();
            var spikes = 0;
            double typicalMs = avgMs;
            if (recent.Count >= StutterAnalyzer.TypicalMinimum)
            {
                typicalMs = Median(recent);
                var line = StutterAnalyzer.Line(typicalMs);
                for (var i = 0; i < valid.Count; i++)
                    if (valid[i] > line) spikes++;
            }

            if (spikes > 0)
            {
                totalSpikes += spikes;
                var causes = AttributeSecond(
                    sec,
                    previousSystem,
                    valid,
                    typicalMs,
                    loadingGaps,
                    appBaselines,
                    gameCoresBaseline,
                    cpuPerfBaseline);
                foreach (var (cause, detail) in causes)
                {
                    counts[cause] = counts.GetValueOrDefault(cause) + spikes;
                    if (!details.ContainsKey(cause) && !string.IsNullOrWhiteSpace(detail))
                        details[cause] = detail;
                }
            }
            else if (sec.System is not null)
            {
                LearnBaseline(sec.System, appBaselines, ref gameCoresBaseline, ref cpuPerfBaseline);
            }

            recent.Enqueue(avgMs);
            while (recent.Count > StutterAnalyzer.TypicalWindow) recent.Dequeue();
            previousSystem = sec.System ?? previousSystem;
        }

        if (totalSpikes == 0)
        {
            return new CapLabStutterReport(
                0,
                CauseNone,
                "Smooth",
                "No frame-time spikes detected in the recorded session.",
                hasPerProcessVram,
                []);
        }

        var breakdown = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var share = Math.Round(kv.Value * 100.0 / totalSpikes, 1);
                var (label, hint) = DescribeCause(kv.Key);
                var detail = details.TryGetValue(kv.Key, out var d) ? d : label;
                return new StutterCauseBreakdown(kv.Key, label, kv.Value, share, detail, hint);
            })
            .ToList();

        var top = breakdown[0];
        var summary = BuildTopSummary(top, totalSpikes);
        return new CapLabStutterReport(totalSpikes, top.Cause, top.Label, summary, hasPerProcessVram, breakdown);
    }

    private static List<(string Cause, string Detail)> AttributeSecond(
        CapLabTraceSecond sec,
        SystemSecond? before,
        List<double> validFrames,
        double typicalMs,
        int loadingGaps,
        Dictionary<string, double> appBaselines,
        double? gameCoresBaseline,
        double? cpuPerfBaseline)
    {
        var found = new List<(string Cause, string Detail)>();
        var sys = sec.System;
        var windows = sys is null ? Array.Empty<SystemSecond>() : before is null ? [sys] : [sys, before];

        var gameUsual = Math.Max(gameCoresBaseline ?? 0, StutterAnalyzer.BaselineFloorCores);
        var shaderHit = gameCoresBaseline is not null && windows.Any(w =>
            w.GameWriteBytes >= StutterAnalyzer.ShaderWriteBytes && w.GameCores >= gameUsual * StutterAnalyzer.ShaderCpuRatio);
        if (shaderHit)
            found.Add((CauseShaderCompile, "Game wrote shader cache while CPU usage surged."));

        if (loadingGaps > 0 && !shaderHit)
            found.Add((CauseLoadingGap, "Long PresentTrace frame gap during asset loading or shader compile."));

        string? topBgApp = null;
        double topBgCores = 0;
        foreach (var w in windows)
        {
            foreach (var app in w.Background)
            {
                var usual = Math.Max(appBaselines.GetValueOrDefault(app.Name), StutterAnalyzer.BaselineFloorCores);
                if (app.Cores >= StutterAnalyzer.SpikeCores && app.Cores >= usual * StutterAnalyzer.SpikeRatio && app.Cores > topBgCores)
                {
                    topBgCores = app.Cores;
                    topBgApp = app.Name;
                }
            }
        }
        if (topBgApp is not null)
            found.Add((CauseBackgroundCpu, string.Create(CultureInfo.InvariantCulture, $"{topBgApp} spiked to {topBgCores:0.0} CPU cores in the background.")));

        var hardFaults = windows.Length > 0 ? windows.Max(w => w.GameHardFaults) : 0;
        if (hardFaults >= StutterAnalyzer.PagingFaultsPerSecond || sec.BackgroundDiskBytesPerSec >= BackgroundDiskBurstBytes)
        {
            var detail = hardFaults >= StutterAnalyzer.PagingFaultsPerSecond
                ? string.Create(CultureInfo.InvariantCulture, $"Disk paging hit {hardFaults} hard faults/s.")
                : "Heavy background disk activity overlapped with frame spikes.";
            found.Add((CauseBackgroundDisk, detail));
        }

        var procVramOver = sec.ProcessVramBytes is { } pv && sec.ProcessVramBudgetBytes is { } pb and > 0
            && pv / (double)pb >= ProcessVramBudgetRatio;
        var adapterVramOver = windows.Length > 0 && windows.Any(w =>
            w.VramUsed is { } used && w.VramTotal is { } total and > 0
            && used / (double)total >= StutterAnalyzer.VideoMemoryFullShare);
        if (procVramOver || adapterVramOver)
        {
            found.Add((CauseVramBudget, procVramOver
                ? "Game VRAM exceeded its GPU memory budget."
                : "GPU VRAM filled past 97% and spilled to system RAM."));
        }

        if (windows.Length > 0 && windows.Max(w => w.GpuTemperature ?? 0) >= StutterAnalyzer.GpuHotDegrees)
            found.Add((CauseGpuThermal, "GPU temperature reached thermal throttling range."));

        var thermalLimit = windows.Length > 0 ? windows.Min(w => w.ThermalLimit ?? 100) : 100;
        var cpuSlowed = cpuPerfBaseline is { } usualPerf and > 0 && windows.Any(w =>
            w.CpuPercent >= StutterAnalyzer.ThrottledBusyPercent && w.CpuPerformance is { } p && p <= usualPerf * StutterAnalyzer.ThrottledShare);
        if (thermalLimit < 100 || cpuSlowed)
            found.Add((CauseCpuThrottle, "CPU clock dropped during load."));

        if (windows.Length > 0 && windows.Max(w => Math.Max(w.DpcPercent ?? 0, w.InterruptPercent ?? 0)) >= StutterAnalyzer.DpcPercentOnOneCpu)
            found.Add((CauseDriverDpc, "Driver DPC or interrupt latency spiked on a core."));

        if (found.Count == 0 && IsCapInducedPacingIssue(sec, validFrames, typicalMs))
        {
            var capLabel = sec.ActiveCapFps is > 0 ? $"{sec.ActiveCapFps} fps" : "the frame cap";
            found.Add((CauseFrameCap, $"Frame delivery oscillated around {capLabel}."));
        }

        if (found.Count == 0)
            found.Add((CauseGameEngine, "Game engine main-thread or streaming hitch with no external PC cause."));

        return found;
    }

    private static bool IsCapInducedPacingIssue(CapLabTraceSecond sec, List<double> validFrames, double typicalMs)
    {
        if (sec.ActiveCapFps is not > 0 || validFrames.Count < 8) return false;
        var targetMs = 1000.0 / sec.ActiveCapFps.Value;
        var nearCeiling = sec.RefreshHz > 0 && sec.VrrEnabled && sec.ActiveCapFps.Value > FrameCapPlanner.VrrCap(sec.RefreshHz);
        var line = StutterAnalyzer.Line(typicalMs);
        var doubledFrames = 0;
        var spikeCount = 0;
        for (var i = 0; i < validFrames.Count; i++)
        {
            var ms = validFrames[i];
            if (ms > line)
            {
                spikeCount++;
                var ratio = ms / targetMs;
                if (ratio is >= 1.75 and <= 3.35) doubledFrames++;
            }
        }
        if (spikeCount == 0) return false;
        return nearCeiling || doubledFrames >= Math.Max(1, spikeCount / 2);
    }

    private static void LearnBaseline(
        SystemSecond system,
        Dictionary<string, double> apps,
        ref double? gameCores,
        ref double? cpuPerf)
    {
        const double smoothing = 0.1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in system.Background)
        {
            seen.Add(app.Name);
            var before = apps.GetValueOrDefault(app.Name);
            apps[app.Name] = before + (app.Cores - before) * smoothing;
        }
        foreach (var name in apps.Keys.Where(k => !seen.Contains(k)).ToList())
            apps[name] *= 1 - smoothing;
        gameCores = gameCores is { } g ? g + (system.GameCores - g) * smoothing : system.GameCores;
        if (system.CpuPerformance is { } perf)
            cpuPerf = cpuPerf is { } p ? p + (perf - p) * smoothing : perf;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0
            : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    private static (string Label, string Hint) DescribeCause(string cause) => cause switch
    {
        CauseShaderCompile => ("Shader compile", "Let shaders finish compiling or precompile with Exo Shaders."),
        CauseLoadingGap => ("Loading / streaming gap", "Normal during level loads; an NVMe drive reduces streaming stalls."),
        CauseBackgroundCpu => ("Background CPU spike", "Autopilot can hold noisy background apps back while you play."),
        CauseBackgroundDisk => ("Background disk / paging", "Close memory-heavy apps or pause background downloads."),
        CauseVramBudget => ("VRAM over budget", "Lower texture quality or resolution one step."),
        CauseFrameCap => ("Frame cap pacing", "Drop one cap tier or switch to the in-game limiter."),
        CauseGpuThermal => ("GPU heat", "Improve airflow or lower the cap to reduce GPU load."),
        CauseCpuThrottle => ("CPU throttling", "Check cooling and power plan settings."),
        CauseDriverDpc => ("Driver DPC latency", "Update chipset, network, and audio drivers."),
        _ => ("Game engine hitch", "Try Balanced or Smooth cap tier to absorb frame-time spikes."),
    };

    private static string BuildTopSummary(StutterCauseBreakdown top, int totalSpikes)
    {
        var spikesWord = totalSpikes == 1 ? "1 spike" : $"{totalSpikes} spikes";
        return $"{top.Label} — {top.Detail} ({top.Spikes} of {spikesWord}).";
    }
}
