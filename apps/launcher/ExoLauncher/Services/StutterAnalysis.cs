namespace ExoLauncher.Services;

/// <summary>A background app's share of the processor over one second, in cores (1.0 is one logical processor kept busy).</summary>
internal readonly record struct AppLoad(string Name, double Cores);

/// <summary>
/// One second of the PC while the game was in front, as Autopilot's telemetry read it (<see cref="AutopilotSampler"/>). Null is
/// "this PC doesn't say": no ACPI thermal zone, a card Windows' graphics statistics don't cover. Memory in bytes; the game's
/// numbers are its whole process tree's over that second.
/// </summary>
internal sealed record SystemSecond(
    double CpuPercent, int Logical, int? GpuPercent, int? GpuTemperature, long? VramUsed, long? VramTotal,
    long RamUsed, long RamTotal, long? Standby, long? Free, double? DpcPercent, double? InterruptPercent,
    double? CpuPerformance, double? ThermalLimit, double GameCores, long GameWriteBytes, long GameHardFaults,
    IReadOnlyList<AppLoad> Background);

/// <summary>What a stutter is put down to. More than one can hold for the same stutter.</summary>
internal enum StutterCause { BackgroundSpike, VideoMemoryFull, GpuHot, CpuThrottled, MemoryPaging, DriverLatency, ShaderCompile, Unexplained }

/// <summary>One finding of a session: a cause, how many stutters it was seen with, the app for a background spike, and what to do.</summary>
internal sealed record StutterFinding(string Kind, int Stutters, string? App, string Detail, string Suggestion);

/// <summary>The highest of what was read over the session: what the report shows beside the findings.</summary>
internal sealed record SessionPeaks(double CpuPercent, int? GpuPercent, int? GpuTemperature, long? VramUsed, long? VramTotal, long RamUsed, long RamTotal, double? DpcPercent);

/// <summary>
/// What a session's stutters came with, from its frames and its seconds alone (pure; <see cref="AutopilotWatch"/> feeds it).
/// A stutter is a frame over 2.5 times the typical frame time (CapFrameX's stutter factor, its default) and at least 8 ms over it,
/// so a 400 fps game's 6 ms frame is not one; typical is the median of the last 30 seconds' average frame times, so a scene that is
/// slower for a while moves the line with it. A stutter is put down to each cause that held in its second or the one before:
/// - a background app using at least half a core and three times what it used in the session's smooth seconds;
/// - the card's own memory 97% full or more (the rest spills into system memory over PCIe);
/// - the card at 87 °C or more, about where GeForce and Radeon cards start lowering their clocks;
/// - the processor held below its clock by an ACPI thermal zone's passive limit, or at 85% or less of its session's
///   usual performance while at least half busy;
/// - the game's own processes reading 150 pages a second or more back from disk (hard faults): memory was short;
/// - one processor spending 20% or more of the second in drivers' deferred calls or interrupts (DPC latency);
/// - likely shader compiling: the game writing at least a megabyte (a shader cache) while using 30% more processor than usual.
/// None of those: unexplained. Ways it could be wrong, each with a test: a slow scene counted as stutters; a fast game's small
/// spike counted; a background app that is always busy blamed; the first seconds blamed before there is a typical frame time.
/// </summary>
internal sealed class StutterAnalyzer
{
    internal const double StutterFactor = 2.5, StutterMinOverMs = 8;
    internal const int TypicalWindow = 30, TypicalMinimum = 5;
    internal const double SpikeCores = 0.5, SpikeRatio = 3, BaselineFloorCores = 0.05;
    internal const double VideoMemoryFullShare = 0.97;
    internal const int GpuHotDegrees = 87;
    internal const double ThrottledShare = 0.85, ThrottledBusyPercent = 50;
    internal const long PagingFaultsPerSecond = 150;
    internal const double DpcPercentOnOneCpu = 20;
    internal const long ShaderWriteBytes = 1 << 20;
    internal const double ShaderCpuRatio = 1.3;
    internal const long IslcBytes = 1L << 30;

    /// <summary>A background app blamed this many stutters in a session is held back the next time that game runs.</summary>
    internal const int LearnAfter = 2;

    private const double Smoothing = 0.1;

    private readonly Queue<double> _recent = new();
    private readonly FrameStats.Accumulator _frames = new();
    private readonly Dictionary<string, double> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(StutterCause Cause, string? App), int> _counts = [];
    private readonly Dictionary<(StutterCause Cause, string? App), double> _worst = [];
    private double? _gameCores, _performance;
    private SystemSecond? _previous;
    private SessionPeaks? _peaks;

    internal int Stutters { get; private set; }
    internal int Seconds { get; private set; }
    /// <summary>Seconds in which ISLC's default rule (free memory under 1 GB with a standby list of 1 GB or more) would have purged.</summary>
    internal int IslcSeconds { get; private set; }
    /// <summary>Stutters that came in such a second.</summary>
    internal int IslcStutters { get; private set; }

    internal FrameStats.Result Frames => _frames.Snapshot();
    internal SessionPeaks? Peaks => _peaks;
    internal SystemSecond? Last => _previous;

    /// <summary>One second in front: its frame times in ms and the PC's numbers (null when they couldn't be read).</summary>
    internal void Add(IReadOnlyList<double> framesMs, SystemSecond? system)
    {
        double sum = 0;
        var count = 0;
        foreach (var ms in framesMs)
        {
            _frames.Add(ms);
            if (!(ms > 0) || ms >= FrameStats.NotAFrameMs) continue;
            sum += ms;
            count++;
        }
        if (system is not null) Peak(system);
        if (count == 0)
        {
            _previous = system ?? _previous;
            return;
        }
        Seconds++;
        var average = sum / count;
        var stutters = 0;
        if (_recent.Count >= TypicalMinimum)
        {
            var line = Line(Median(_recent));
            foreach (var ms in framesMs)
                if (ms > line && ms < FrameStats.NotAFrameMs) stutters++;
        }
        var islc = system is { Free: { } free, Standby: { } standby } && free < IslcBytes && standby >= IslcBytes;
        if (islc) IslcSeconds++;
        if (stutters > 0)
        {
            Stutters += stutters;
            if (islc) IslcStutters += stutters;
            foreach (var (cause, app, measure) in Causes(system, _previous, _apps, _gameCores, _performance))
            {
                var key = (cause, app);
                _counts[key] = _counts.GetValueOrDefault(key) + stutters;
                if (measure > _worst.GetValueOrDefault(key)) _worst[key] = measure;
            }
        }
        else if (system is not null) Learn(system);
        _recent.Enqueue(average);
        while (_recent.Count > TypicalWindow) _recent.Dequeue();
        _previous = system ?? _previous;
    }

    /// <summary>The frame time over which a frame is a stutter, for a typical frame time.</summary>
    internal static double Line(double typicalMs) => Math.Max(typicalMs * StutterFactor, typicalMs + StutterMinOverMs);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>A smooth second: what each app, the game and the processor's clocks usually do.</summary>
    private void Learn(SystemSecond system)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in system.Background)
        {
            seen.Add(app.Name);
            // From nothing: an app first seen busy in a smooth second is not taken as always busy at once.
            var before = _apps.GetValueOrDefault(app.Name);
            _apps[app.Name] = before + (app.Cores - before) * Smoothing;
        }
        foreach (var name in _apps.Keys.Where(name => !seen.Contains(name)).ToList()) _apps[name] *= 1 - Smoothing;
        _gameCores = _gameCores is { } game ? game + (system.GameCores - game) * Smoothing : system.GameCores;
        if (system.CpuPerformance is { } perf) _performance = _performance is { } p ? p + (perf - p) * Smoothing : perf;
    }

    private void Peak(SystemSecond s)
    {
        var p = _peaks;
        _peaks = new SessionPeaks(
            Math.Max(p?.CpuPercent ?? 0, s.CpuPercent),
            Max(p?.GpuPercent, s.GpuPercent), Max(p?.GpuTemperature, s.GpuTemperature),
            Max(p?.VramUsed, s.VramUsed), s.VramTotal ?? p?.VramTotal,
            Math.Max(p?.RamUsed ?? 0, s.RamUsed), s.RamTotal,
            Max(p?.DpcPercent, s.DpcPercent));
    }

    private static T? Max<T>(T? a, T? b) where T : struct, IComparable<T> =>
        a is null ? b : b is null ? a : a.Value.CompareTo(b.Value) >= 0 ? a : b;

    /// <summary>
    /// What held in a stutter's second (<paramref name="now"/>) or the one before, with the number that showed it (cores, °C,
    /// share, faults or percent). Unexplained when nothing did, or when the PC's numbers couldn't be read.
    /// </summary>
    internal static List<(StutterCause Cause, string? App, double Measure)> Causes(SystemSecond? now, SystemSecond? before,
        IReadOnlyDictionary<string, double> appBaseline, double? gameBaseline, double? performanceBaseline)
    {
        var found = new List<(StutterCause, string?, double)>();
        if (now is null) return [(StutterCause.Unexplained, null, 0)];
        SystemSecond[] seconds = before is null ? [now] : [now, before];

        var spikes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var second in seconds)
            foreach (var app in second.Background)
            {
                var usual = Math.Max(appBaseline.GetValueOrDefault(app.Name), BaselineFloorCores);
                if (app.Cores >= SpikeCores && app.Cores >= usual * SpikeRatio && app.Cores > spikes.GetValueOrDefault(app.Name)) spikes[app.Name] = app.Cores;
            }
        foreach (var (name, cores) in spikes.OrderByDescending(pair => pair.Value)) found.Add((StutterCause.BackgroundSpike, name, cores));

        if (seconds.Select(s => s.VramUsed is { } used && s.VramTotal is { } total and > 0 ? used / (double)total : 0).Max() is var share && share >= VideoMemoryFullShare)
            found.Add((StutterCause.VideoMemoryFull, null, share));
        if (seconds.Max(s => s.GpuTemperature ?? 0) is var hot && hot >= GpuHotDegrees)
            found.Add((StutterCause.GpuHot, null, hot));
        var limited = seconds.Min(s => s.ThermalLimit ?? 100);
        var slowed = performanceBaseline is { } usualPerf && usualPerf > 0
            ? seconds.Where(s => s.CpuPercent >= ThrottledBusyPercent && s.CpuPerformance is { } perf && perf <= usualPerf * ThrottledShare).Select(s => s.CpuPerformance!.Value / usualPerf).DefaultIfEmpty(1).Min()
            : 1;
        if (limited < 100 || slowed < 1) found.Add((StutterCause.CpuThrottled, null, limited < 100 ? limited : slowed * 100));
        if (seconds.Max(s => s.GameHardFaults) is var faults && faults >= PagingFaultsPerSecond)
            found.Add((StutterCause.MemoryPaging, null, faults));
        if (seconds.Max(s => Math.Max(s.DpcPercent ?? 0, s.InterruptPercent ?? 0)) is var dpc && dpc >= DpcPercentOnOneCpu)
            found.Add((StutterCause.DriverLatency, null, dpc));
        var gameUsual = Math.Max(gameBaseline ?? 0, BaselineFloorCores);
        if (gameBaseline is not null && seconds.Any(s => s.GameWriteBytes >= ShaderWriteBytes && s.GameCores >= gameUsual * ShaderCpuRatio))
            found.Add((StutterCause.ShaderCompile, null, seconds.Max(s => s.GameWriteBytes)));

        if (found.Count == 0) found.Add((StutterCause.Unexplained, null, 0));
        return found;
    }

    /// <summary>The findings, most stutters first: each cause (each app for a spike) with what it means and what to do.</summary>
    internal List<StutterFinding> Findings(string game)
    {
        var list = new List<StutterFinding>();
        foreach (var ((cause, app), stutters) in _counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.App, StringComparer.OrdinalIgnoreCase))
        {
            var worst = _worst.GetValueOrDefault((cause, app));
            var (detail, suggestion) = Explain(cause, app, worst, stutters, _peaks, game);
            list.Add(new StutterFinding(Kind(cause), stutters, app, detail, suggestion));
        }
        return list;
    }

    /// <summary>Background apps to hold back the next time this game runs: each blamed for <see cref="LearnAfter"/> stutters or more.</summary>
    internal List<string> Learned() =>
        [.. _counts.Where(pair => pair.Key.Cause == StutterCause.BackgroundSpike && pair.Key.App is not null && pair.Value >= LearnAfter)
            .OrderByDescending(pair => pair.Value).Select(pair => pair.Key.App!)];

    internal static string Kind(StutterCause cause) => cause switch
    {
        StutterCause.BackgroundSpike => "background",
        StutterCause.VideoMemoryFull => "vram",
        StutterCause.GpuHot => "gpuHeat",
        StutterCause.CpuThrottled => "cpuThrottle",
        StutterCause.MemoryPaging => "paging",
        StutterCause.DriverLatency => "dpc",
        StutterCause.ShaderCompile => "shaders",
        _ => "unexplained",
    };

    private static string Times(int n) => n == 1 ? "1 stutter" : $"{n} stutters";

    private static (string Detail, string Suggestion) Explain(StutterCause cause, string? app, double worst, int stutters, SessionPeaks? peaks, string game) => cause switch
    {
        StutterCause.BackgroundSpike => ($"{Times(stutters)} came as {app} jumped to {worst:0.0} cores in the background.",
            $"Autopilot holds {app} back the next time you play {game}."),
        StutterCause.VideoMemoryFull => ($"{Times(stutters)} came with the graphics card's memory {worst:P0} full" +
            (peaks?.VramTotal is { } total ? $" ({total / (double)(1L << 30):0.#} GB)." : "."),
            "Lower texture quality or the resolution a step: what doesn't fit is read over PCIe from system memory."),
        StutterCause.GpuHot => ($"{Times(stutters)} came with the graphics card at {worst:0} °C, where cards lower their clocks.",
            "Check the card's fans and the case's airflow, or cap the frame rate a little below your screen's refresh."),
        StutterCause.CpuThrottled => ($"{Times(stutters)} came while the processor ran below its usual clocks ({worst:0}%).",
            "The processor slowed down, most often from heat: check its cooler, or that the power plan isn't capping it."),
        StutterCause.MemoryPaging => ($"{Times(stutters)} came while Windows read the game back from disk ({worst:0} pages a second).",
            "Memory ran short: close memory-hungry apps while you play, or add memory."),
        StutterCause.DriverLatency => ($"{Times(stutters)} came while a driver kept a processor busy ({worst:0}% of a second).",
            "Update the network, audio and chipset drivers in PC › Updates; a USB device or Wi-Fi adapter is the usual cause."),
        StutterCause.ShaderCompile => ($"{Times(stutters)} came while the game wrote to its shader cache: most likely compiling shaders.",
            "These go away as the shaders are kept; Exo Shaders can compile them ahead (PC › Shaders)."),
        _ => ($"{Times(stutters)} came with nothing Exo can see on the PC.",
            "Most likely the game itself: streaming a new area, or its own work on the main thread."),
    };
}
