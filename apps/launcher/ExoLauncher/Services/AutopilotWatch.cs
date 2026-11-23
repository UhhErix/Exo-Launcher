using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// A session's report, as kept: the frames (average and lows), the stutters and what they came with, the peaks, what Autopilot did
/// for the game (<see cref="Did"/>, filled by <see cref="Autopilot"/>), the background apps it learned to hold back next time, and
/// how often ISLC's rule would have purged memory (the evidence for or against a standby-list cleaner on this PC).
/// </summary>
internal sealed record AutopilotReport(string Game, string Title, DateTime StartedUtc, DateTime EndedUtc, double Seconds, long Frames,
    double AverageFps, double Low1Fps, double Low01Fps, int Stutters, List<StutterFinding> Findings, SessionPeaks? Peaks,
    List<string> Did, List<string> Learned, int IslcSeconds, int IslcStutters);

/// <summary>
/// What a game's frames are watched with while it is in front: one second at a time from the session recorder's own look
/// (<see cref="SessionRecorderHost"/>, the same frames, no second trace), the PC's numbers read beside them
/// (<see cref="AutopilotSampler"/>), and at the session's end a report kept on this PC (<see cref="AutopilotReports"/>) and
/// told to the page (<see cref="Reported"/>), which shows it in PC › Autopilot afterwards: nothing is shown during play.
/// Read-only: nothing is opened in a game, nothing is changed, nothing is sent anywhere. The sampler exists only while a game
/// is in front and goes when it leaves. Ways it could fail: a report for a session of a few seconds (under a minute of frames
/// is none); the sampler left open after the game (it closes on <see cref="End"/> and when the game goes behind); a report
/// lost because the file is held (it is held in memory and saved at the next end).
/// </summary>
internal sealed class AutopilotWatch(Func<ISystemSampler?> open, Func<AutopilotReport, bool> save, Func<DateTime> now, Func<bool>? purgeStandby = null)
{
    /// <summary>A session needs this much of frames in front to get a report.</summary>
    internal const double ReportSeconds = 60;
    internal static readonly TimeSpan StandbyPurgeCooldown = TimeSpan.FromSeconds(30);

    internal static AutopilotWatch Shared { get; } = new(AutopilotSampler.Open, report => AutopilotReports.Add(AutopilotReports.DefaultPath, report, DateTime.UtcNow), () => DateTime.UtcNow, StandbyPurger.TryPurge)
    {
        OnSecond = FrameCapService.Shared.RecordSecond,
    };

    /// <summary>A report was kept; the bridge tells the page.</summary>
    internal static event Action<AutopilotReport>? Reported;

    internal event Action? LiveChanged;

    /// <summary>What Autopilot did for the game this session (set by <see cref="Autopilot"/>); nothing when it is off.</summary>
    internal Func<string, IReadOnlyList<string>> Did { get; set; } = _ => [];

    internal Action<string?, bool, IReadOnlyList<double>, SystemSecond?>? OnSecond { get; set; }

    private readonly Lock _gate = new();
    private StutterAnalyzer? _analyzer;
    private ISystemSampler? _sampler;
    private string? _game, _title;
    private DateTime _started;
    private DateTime? _lastStandbyPurge;
    private AutopilotReport? _pending;

    /// <summary>What is shown live: the game, the session's frames so far and the last second's numbers. Null with no game.</summary>
    internal (string Game, string Title, FrameStats.Result Frames, int Stutters, SystemSecond? Last)? Live
    {
        get
        {
            lock (_gate) return _analyzer is { } a && _game is not null ? (_game, _title ?? _game, a.Frames, a.Stutters, a.Last) : null;
        }
    }

    /// <summary>One look of the recorder: the game (null with none), whether it is in front, its frame times since the last look, its processes.</summary>
    internal void Second(string? game, string? title, bool inFront, IReadOnlyList<double> framesMs, IReadOnlyCollection<int> tree)
    {
        var notify = false;
        SystemSecond? recordedSystem = null;
        lock (_gate)
        {
            var hadLive = _analyzer is not null && _game is not null;
            var switched = game != _game;
            if (switched) EndLocked();
            if (game is null)
            {
                notify = hadLive;
            }
            else
            {
                if (_analyzer is null)
                {
                    _analyzer = new StutterAnalyzer();
                    (_game, _title, _started) = (game, title, now());
                }
                if (!inFront)
                {
                    // Behind: nothing is read, and the sampler goes so it holds nothing while the player is elsewhere.
                    _sampler?.Dispose();
                    _sampler = null;
                    notify = switched;
                }
                else
                {
                    SystemSecond? system = null;
                    try { system = (_sampler ??= open())?.Read(tree); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Autopilot: a read failed: " + ex.GetType().Name); }
                    recordedSystem = system;
                    _analyzer.Add(framesMs, system);
                    notify = true;
                    if (purgeStandby is not null && system is { Free: { } free, Standby: { } standby }
                        && free < StutterAnalyzer.IslcBytes && standby >= StutterAnalyzer.IslcBytes)
                    {
                        var utc = now();
                        if (_lastStandbyPurge is not { } prev || utc - prev >= StandbyPurgeCooldown)
                        {
                            _lastStandbyPurge = utc;
                            if (purgeStandby())
                                AppLog.Info("Autopilot: purged standby memory list (free < 1 GB, standby >= 1 GB).");
                        }
                    }
                }
            }
        }
        try { OnSecond?.Invoke(game, inFront, framesMs, recordedSystem); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Autopilot: OnSecond listener failed: " + ex.GetType().Name); }
        if (notify) NotifyLiveChanged();
    }

    /// <summary>The game closed, another took over, or Exo is closing: the session's report is kept.</summary>
    internal void End()
    {
        bool hadLive;
        lock (_gate)
        {
            hadLive = _analyzer is not null && _game is not null;
            EndLocked();
        }
        if (hadLive) NotifyLiveChanged();
    }

    private void NotifyLiveChanged()
    {
        if (LiveChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { AppLog.Warn("Autopilot: a live listener failed: " + ex.GetType().Name); }
        }
    }

    private void EndLocked()
    {
        _sampler?.Dispose();
        _sampler = null;
        if (_pending is { } waiting && Keep(waiting)) _pending = null;
        if (_analyzer is not { } analyzer || _game is not { } game)
        {
            (_analyzer, _game, _title) = (null, null, null);
            return;
        }
        var frames = analyzer.Frames;
        AutopilotReport? report = null;
        if (frames.Seconds >= ReportSeconds)
        {
            var title = _title ?? game;
            report = new AutopilotReport(game, title, _started, now(), Math.Round(frames.Seconds, 1), frames.Frames,
                Math.Round(frames.AverageFps, 1), Math.Round(frames.Low1Fps, 1), Math.Round(frames.Low01Fps, 1), analyzer.Stutters,
                analyzer.Findings(title), analyzer.Peaks, [.. Did(game)], analyzer.Learned(), analyzer.IslcSeconds, analyzer.IslcStutters);
        }
        (_analyzer, _game, _title) = (null, null, null);
        if (report is null) return;
        if (!Keep(report)) _pending = report;
    }

    private bool Keep(AutopilotReport report)
    {
        bool kept;
        try { kept = save(report); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { kept = false; }
        if (!kept) return false;
        AppLog.Info($"Autopilot: report for {report.Title}: {report.Stutters} stutters, {report.Findings.Count} findings.");
        try { Reported?.Invoke(report); } catch (Exception ex) { AppLog.Warn("Autopilot: a listener failed: " + ex.GetType().Name); }
        return true;
    }
}

/// <summary>One second of the PC, read; the seam the watch is tested through.</summary>
internal interface ISystemSampler : IDisposable
{
    SystemSecond? Read(IReadOnlyCollection<int> gameTree);
}

/// <summary>
/// The PC's numbers once a second, none needing administrator rights or a driver: processor use from GetSystemTimes, memory
/// from GlobalMemoryStatusEx, the graphics card's use, temperature and memory from Windows' graphics statistics
/// (<see cref="GpuStats"/>, what Task Manager reads), the standby list, free memory, DPC and interrupt time, the processor's
/// performance against its base clock and any ACPI thermal zone's passive limit from Windows' own counters (<see cref="PdhCounters"/>),
/// and each process's processor time, writes and hard faults from the kernel's process list (<see cref="ProcessSnapshot"/>).
/// There is no CPU temperature: Windows has no plain source for it, and the others need a kernel driver.
/// Its cost: one process list a second (BackgroundTrim.ProcessTable measured the same call at 4.6 ms for 240 processes) and one counter sample, only while a game is in front.
/// </summary>
internal sealed class AutopilotSampler : ISystemSampler
{
    internal const string StandbyReserve = @"\Memory\Standby Cache Reserve Bytes", StandbyNormal = @"\Memory\Standby Cache Normal Priority Bytes",
        StandbyCore = @"\Memory\Standby Cache Core Bytes", FreeZero = @"\Memory\Free & Zero Page List Bytes",
        Dpc = @"\Processor(*)\% DPC Time", Interrupt = @"\Processor(*)\% Interrupt Time",
        Performance = @"\Processor Information(_Total)\% Processor Performance", PassiveLimit = @"\Thermal Zone Information(*)\% Passive Limit";

    private readonly PdhCounters? _counters;
    private GpuStats? _gpu;
    private bool _noGpu;
    private long _idle, _kernel, _user, _at;
    private Dictionary<(int, long), ProcessRow> _last = [];
    private readonly int _self = Environment.ProcessId;

    private AutopilotSampler(PdhCounters? counters) => _counters = counters;

    internal static ISystemSampler? Open()
    {
        var counters = PdhCounters.Open([StandbyReserve, StandbyNormal, StandbyCore, FreeZero, Dpc, Interrupt, Performance, PassiveLimit]);
        counters?.Collect();
        return new AutopilotSampler(counters);
    }

    public SystemSecond? Read(IReadOnlyCollection<int> gameTree)
    {
        var at = Environment.TickCount64;
        var elapsedSeconds = _at == 0 ? 0 : (at - _at) / 1000.0;
        _at = at;
        var cpu = CpuUse();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        (int? Use, int? Temperature, long? MemoryBytes) gpu = default;
        if (!_noGpu)
        {
            _gpu ??= GpuStats.Open();
            _noGpu = _gpu is null;
            if (_gpu is { } card) gpu = card.Read(true, true, true);
        }
        var counted = _counters?.Collect() == true;
        double? Single(string path) => counted ? _counters!.Value(path) : null;
        double? Busiest(string path) => counted && _counters!.Values(path) is { Count: > 0 } all ? all.Values.Max() : null;
        double? Lowest(string path) => counted && _counters!.Values(path) is { Count: > 0 } all ? all.Values.Min() : null;
        var standby = counted ? Sum(Single(StandbyReserve), Single(StandbyNormal), Single(StandbyCore)) : null;

        var rows = ProcessSnapshot.Read();
        var next = new Dictionary<(int, long), ProcessRow>(rows.Count);
        var game = gameTree as IReadOnlySet<int> ?? gameTree.ToHashSet();
        double gameCores = 0;
        long gameWrites = 0, gameFaults = 0;
        var background = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ticks = elapsedSeconds * 1e7;
        foreach (var row in rows)
        {
            if (row.Pid == 0) continue;
            next[(row.Pid, row.CreateTime)] = row;
            if (ticks <= 0 || !_last.TryGetValue((row.Pid, row.CreateTime), out var before)) continue;
            var cores = Math.Max(0, row.CpuTime - before.CpuTime) / ticks;
            if (game.Contains(row.Pid))
            {
                gameCores += cores;
                gameWrites += Math.Max(0, row.WriteBytes - before.WriteBytes);
                gameFaults += Math.Max(0, row.HardFaults - before.HardFaults);
            }
            else if (row.Pid != _self && row.Name.Length > 0) background[row.Name] = background.GetValueOrDefault(row.Name) + cores;
        }
        _last = next;
        if (ticks <= 0 || cpu is null) return null;
        var top = background.Where(pair => pair.Value >= 0.01).OrderByDescending(pair => pair.Value).Take(8)
            .Select(pair => new AppLoad(pair.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pair.Key[..^4] : pair.Key, Math.Round(pair.Value, 3))).ToList();
        return new SystemSecond(cpu.Value, Environment.ProcessorCount, gpu.Use, gpu.Temperature, gpu.MemoryBytes, _gpu?.DedicatedBytes,
            (long)(memory.TotalPhys - memory.AvailPhys), (long)memory.TotalPhys, standby is { } s ? (long)s : null,
            Single(FreeZero) is { } free ? (long)free : null, Busiest(Dpc), Busiest(Interrupt), Single(Performance), Lowest(PassiveLimit),
            Math.Round(gameCores, 3), (long)(gameWrites / Math.Max(elapsedSeconds, 0.001)), (long)(gameFaults / Math.Max(elapsedSeconds, 0.001)), top);
    }

    private static double? Sum(params double?[] values) => values.All(v => v is null) ? null : values.Sum(v => v ?? 0);

    private double? CpuUse()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var seen = _kernel != 0;
        var (idled, all) = (idle - _idle, kernel + user - _kernel - _user);
        (_idle, _kernel, _user) = (idle, kernel, user);
        return seen && all > 0 ? Math.Round(Math.Clamp(1 - idled / (double)all, 0, 1) * 100, 1) : null;
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _gpu = null;
        _counters?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

/// <summary>
/// The session reports, in one file in Exo's data folder (this PC only: they describe this PC's hardware), never sent anywhere:
/// the 50 newest, nothing older than 90 days. Written beside and swapped in; a file that can't be read now is never written over.
/// </summary>
internal static class AutopilotReports
{
    internal const string FileName = "autopilot-reports.json";
    internal const int MaxReports = 50, MaxDays = 90;

    private sealed record CachedReports(DateTime LastWriteUtc, long Length, List<AutopilotReport> Reports);

    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, CachedReports> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal static void InvalidateCache(string? path = null)
    {
        lock (Gate)
        {
            if (path is null) Cache.Clear();
            else Cache.Remove(Path.GetFullPath(path));
        }
    }

    private sealed class Doc
    {
        public int Version { get; set; } = 1;
        public List<AutopilotReport> Reports { get; set; } = [];
    }

    /// <summary>Newest first.</summary>
    internal static List<AutopilotReport> Load(string path)
    {
        lock (Gate)
        {
            var key = Path.GetFullPath(path);
            var info = new FileInfo(key);
            if (!info.Exists)
            {
                Cache.Remove(key);
                return [];
            }
            if (Cache.TryGetValue(key, out var cached)
                && cached.LastWriteUtc == info.LastWriteTimeUtc
                && cached.Length == info.Length)
                return [.. cached.Reports];
            var reports = Read(key);
            if (reports is null) return [];
            var fresh = new FileInfo(key);
            if (fresh.Exists) Cache[key] = new CachedReports(fresh.LastWriteTimeUtc, fresh.Length, reports);
            return [.. reports];
        }
    }

    /// <summary>The apps the game's last five reports learned to hold back, most recent first, each once.</summary>
    internal static List<string> LearnedFor(IEnumerable<AutopilotReport> reports, string game) =>
        [.. reports.Where(r => r.Game == game).Take(5).SelectMany(r => r.Learned).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static List<AutopilotReport>? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var text = File.ReadAllText(path);
            return text.Length > 0 && JsonSerializer.Deserialize<Doc>(text, Json) is { } doc ? doc.Reports ?? [] : [];
        }
        catch (JsonException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool Add(string path, AutopilotReport report, DateTime nowUtc)
    {
        lock (Gate)
        {
            var key = Path.GetFullPath(path);
            if (Read(key) is not { } reports) return false;
            var oldest = nowUtc.AddDays(-MaxDays);
            var kept = reports.Prepend(report).Where(r => r.EndedUtc >= oldest).OrderByDescending(r => r.EndedUtc).Take(MaxReports).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(key)!);
                var temp = key + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Doc { Reports = kept }, Json));
                if (File.Exists(key)) File.Replace(temp, key, null);
                else File.Move(temp, key);
                var fresh = new FileInfo(key);
                if (fresh.Exists) Cache[key] = new CachedReports(fresh.LastWriteTimeUtc, fresh.Length, kept);
                else Cache.Remove(key);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Autopilot: the reports couldn't be written: " + ex.GetType().Name);
                return false;
            }
        }
    }
}
