using ExoLauncher.Models;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>A process setting Autopilot reads and writes, each put back exactly as it was.</summary>
internal enum Knob
{
    /// <summary>The priority class (GetPriorityClass's value). Realtime is never written.</summary>
    Priority,
    /// <summary>Power throttling's execution speed: 0 Windows decides, 1 never throttled, 3 EcoQoS (efficiency mode).</summary>
    Eco,
    /// <summary>I/O priority: 0 very low, 1 low, 2 normal.</summary>
    Io,
    /// <summary>Memory priority: 1 very low to 5 normal.</summary>
    Memory,
    /// <summary>Suspended (1) or running (0); written only by the player's own list.</summary>
    Suspended,
}

/// <summary>What Autopilot needs of Windows, behind one seam so every decision and every put-back is tested with fakes.</summary>
internal interface IAutopilotSystem
{
    IReadOnlyList<ProcessRow> Processes();
    /// <summary>The process now in front (the foreground window's), or 0.</summary>
    int Front();
    /// <summary>The knob's value, or null when the process is gone, is another one by now (its start time), or won't say.</summary>
    int? Read(Knob knob, int pid, long created);
    /// <summary>Writes the knob; false when the process is gone, is another one by now, or Windows refused.</summary>
    bool Write(Knob knob, int pid, long created, int value);
    /// <summary>The active plan's AC value of a setting, or null.</summary>
    uint? PowerRead(Guid scheme, Guid subgroup, Guid setting);
    /// <summary>Writes the active plan's AC value and makes it take (the plan is set active again); false when Windows refused.</summary>
    bool PowerWrite(Guid scheme, Guid subgroup, Guid setting, uint value);
    Guid? ActiveScheme();
    bool OnBattery();
    /// <summary>Acquires 1 ms system timer resolution while a game runs in foreground (winmm timeBeginPeriod).</summary>
    void BeginHighResTimer() { }
    /// <summary>Releases 1 ms system timer resolution (winmm timeEndPeriod).</summary>
    void EndHighResTimer() { }
    IReadOnlyList<CpuSetInfo> CpuSets() => [];
    uint[]? ReadCpuSets(int pid, long created) => null;
    bool WriteCpuSets(int pid, long created, uint[]? ids) => false;
}

/// <summary>
/// One change Autopilot made, written to the journal before the change is made, and taken out once it is put back. Process
/// changes name the process by id and start time (a FILETIME), so a reused id is never another program's value. A power
/// setting names its plan, subgroup and setting. Session changes go back when the game ends (or at Exo's next start, after a
/// crash); Held ones (Windows Game Mode) stay while Autopilot is on.
/// </summary>
internal sealed record JournalEntry(string Kind, int Pid, long Created, string Name, int Before, int Wrote, string? Scheme = null,
    string? Subgroup = null, string? Setting = null, bool Held = false, string? Note = null)
{
    internal bool SameTarget(JournalEntry other) =>
        Kind == other.Kind && Pid == other.Pid && Created == other.Created && Scheme == other.Scheme && Subgroup == other.Subgroup && Setting == other.Setting;
}

/// <summary>
/// The restore journal: every change Autopilot makes, on disk before the change, so Exo crashing, being ended or the PC losing
/// power leaves the way back. Written beside and swapped in with a flush (a torn write leaves the old file). Ways it could fail,
/// each with a test: a change made before its entry is on disk; a damaged file stopping Exo (it reads as empty, set aside as .bad
/// so nothing it held is written over); an entry left after it was put back, which would put it back twice.
/// </summary>
internal sealed class AutopilotJournal(string path)
{
    internal const string FileName = "autopilot-journal.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private List<JournalEntry>? _entries;

    private sealed class Doc
    {
        public int Version { get; set; } = 1;
        public List<JournalEntry> Entries { get; set; } = [];
    }

    internal IReadOnlyList<JournalEntry> Entries
    {
        get { lock (_gate) return [.. Loaded()]; }
    }

    private List<JournalEntry> Loaded()
    {
        if (_entries is not null) return _entries;
        try
        {
            if (!File.Exists(path)) return _entries = [];
            var text = File.ReadAllText(path);
            return _entries = text.Length > 0 && JsonSerializer.Deserialize<Doc>(text, Json) is { } doc ? doc.Entries ?? [] : [];
        }
        catch (JsonException)
        {
            AppLog.Warn("Autopilot: the journal is damaged, set aside as .bad.");
            try { File.Move(path, path + ".bad", overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return _entries = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Autopilot: the journal couldn't be read: " + ex.GetType().Name);
            return [];
        }
    }

    /// <summary>Records a change about to be made; false when it couldn't be kept on disk, and then the change must not be made.</summary>
    internal bool Add(JournalEntry entry)
    {
        lock (_gate)
        {
            var entries = Loaded();
            var next = entries.Where(other => !other.SameTarget(entry)).Append(entry).ToList();
            if (!Write(next)) return false;
            _entries = next;
            return true;
        }
    }

    /// <summary>Forgets a change that was put back (or can't be: its process is gone).</summary>
    internal void Remove(JournalEntry entry)
    {
        lock (_gate)
        {
            var entries = Loaded();
            var next = entries.Where(other => !other.SameTarget(entry)).ToList();
            if (next.Count == entries.Count) return;
            if (Write(next)) _entries = next;
        }
    }

    private bool Write(List<JournalEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Doc { Entries = entries }, Json);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Autopilot: the journal couldn't be written: " + ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>The game Autopilot works for: its library id, its name, its processes, and whether Exo may open its process at all.</summary>
internal sealed record AutopilotGame(string Id, string Title, IReadOnlyCollection<int> Tree, bool MayTouchGame, IReadOnlyList<string> Learned);

/// <summary>
/// Autopilot's decisions, pure. Each is the smallest change with evidence behind it (docs/architecture/autopilot.md has the
/// sources); anything else is left as it is.
/// </summary>
internal static class AutopilotRules
{
    internal const int Idle = 0x40, BelowNormal = 0x4000, Normal = 0x20, AboveNormal = 0x8000, High = 0x80, Realtime = 0x100;
    internal const int EcoDefault = 0, EcoOff = 1, EcoOn = 3;
    internal const int IoLow = 1, IoNormal = 2;
    internal const int MemoryLow = 2, MemoryNormal = 5;

    /// <summary>A background app using this many cores on average between two looks is held back while the game is in front.</summary>
    internal const double HogCores = 0.25;

    /// <summary>At most this many processes are held back in one session.</summary>
    internal const int MaxDemoted = 48;

    /// <summary>
    /// The class the game's processes get: above normal by default, high when the player chose it; never realtime (it can starve
    /// the mouse, sound and Windows itself). Null leaves the game alone: the player chose normal, or Exo may not open the game's
    /// process (anti-cheat, or a game whose anti-cheat Exo can't read, counts as having it: AGENTS.md's hard stop).
    /// </summary>
    internal static int? GamePriority(string choice, bool mayTouchGame) =>
        !mayTouchGame ? null : choice switch { "high" => High, "normal" => null, _ => AboveNormal };

    /// <summary>Only ever lower: a class that is normal goes to below normal; idle, below normal and anything raised stay.</summary>
    internal static int? Lower(int? current) => current == Normal ? BelowNormal : null;

    /// <summary>
    /// Why a process is not held back, or null when it may be: outside the player's session, the game's or Exo's own, in front,
    /// or on <see cref="AutopilotSafety"/>'s list.
    /// </summary>
    internal static string? LeftAlone(ProcessRow row, int session, IReadOnlyCollection<int> game, int self, int front)
    {
        if (row.Pid <= 4 || row.Session != session) return "not yours";
        if (game.Contains(row.Pid)) return "the game";
        if (row.Pid == self) return "Exo";
        if (row.Pid == front) return "in front";
        return AutopilotSafety.WhyLeftAlone(row.Name);
    }

    /// <summary>Whether a background process is held back now: named by the player or learned for this game, or using <see cref="HogCores"/> or more.</summary>
    internal static bool Hog(string bare, double cores, IReadOnlySet<string> named) => named.Contains(bare) || cores >= HogCores;
}

/// <summary>
/// The processor's cache layout as Autopilot reads it (GetLogicalProcessorInformationEx, <see cref="MouseCore.Parse"/>): one L3
/// (one CCD, or a monolithic die), two or more of one size (a dual-CCD Ryzen), or one larger than the rest (a 3D V-Cache CCD).
/// Autopilot never pins a game to a CCD: on an X3D part AMD's driver parks the other CCD itself in Game Mode, which Autopilot
/// then keeps on, and on a dual-CCD part without V-Cache Windows' own scheduler already prefers the fastest cores (CPPC);
/// nobody has measured a gain from pinning over either. PC › Task Manager's affinity is there for a player who wants it.
/// </summary>
internal sealed record CcdLayout(int Caches, ulong VCacheMask, long VCacheBytes)
{
    internal bool VCache => VCacheMask != 0;

    internal static CcdLayout Of(MouseCore.Topology topology)
    {
        var caches = topology.L3;
        if (caches.Count < 2) return new(caches.Count, 0, 0);
        var largest = caches.MaxBy(cache => cache.Bytes)!;
        var vcache = caches.Count(cache => cache.Bytes == largest.Bytes) == 1;
        return new(caches.Count, vcache ? largest.Mask : 0, vcache ? largest.Bytes : 0);
    }

    /// <summary>In the player's words: "One CCD", "Two CCDs", "Two CCDs; the V-Cache one is CPUs 0 to 15 (96 MB)".</summary>
    internal string Describe()
    {
        if (Caches <= 1) return "One CCD: every core shares one cache, so there is nothing to pin";
        var count = Caches == 2 ? "Two CCDs" : $"{Caches} CCDs";
        if (!VCache) return $"{count} of one kind: Windows already prefers the fastest cores";
        var cpus = Enumerable.Range(0, 64).Where(cpu => (VCacheMask >> cpu & 1) != 0).ToList();
        return $"{count}; the V-Cache one is CPUs {cpus.First()} to {cpus.Last()} ({VCacheBytes / (1 << 20)} MB), which AMD's driver gives games";
    }
}

/// <summary>
/// Autopilot while a game runs (the Game Mode replacement): the game's processes get above normal priority (never realtime,
/// and never a game with anti-cheat: its process is not opened), background apps that hog the processor, that the player named
/// or that a past session blamed for stutters are held back (efficiency mode, below normal priority, low disk and memory
/// priority, each only lowered), the apps the player chose are paused, and the active plan keeps every core awake when it
/// would park them. Every change goes into the journal before it is made and is put back exactly when the game ends, when Exo
/// closes, or at Exo's next start after a crash; a value something else changed since is left as it is. Nothing is touched
/// outside the player's session, in the game, in Exo, or on <see cref="AutopilotSafety"/>'s list.
/// Ways it could fail, each with a test: a change made with no entry on disk; a change left after the game; a reused process id
/// given another program's value back; realtime priority; an anti-cheat, sound or call process touched; the app the player
/// is using demoted; core parking changed on a dual-CCD X3D (AMD's driver parks the second CCD itself) or on battery.
/// </summary>
internal sealed record ExoAiAutopilotSnapshot(bool IsGaming, string? GameId, string? GameTitle, int DemotedCount, bool TimerResActive, bool AltTabbed);

internal sealed class AutopilotEngine(IAutopilotSystem system, AutopilotJournal journal, Func<AutopilotSettings> settings,
    Func<PowerProfiles.CpuClass> cpuClass, Func<Action, IDisposable> startTimer, Action<string>? logLine = null, int? self = null, Func<CcdLayout?>? ccdLayout = null)
{
    internal const int LookEveryMs = 2000;
    internal const int AltTabReleaseTicks = 2;

    private readonly Action<string> log = logLine ?? AppLog.Info;
    private readonly Lock _gate = new();
    private readonly int _self = self ?? Environment.ProcessId;
    private int _session = -1;
    private AutopilotGame? _game;
    private IDisposable? _timer;
    private Dictionary<(int, long), long> _cpu = [];
    private long _cpuAt;
    private readonly List<string> _did = [];
    private readonly HashSet<(int, long)> _demoted = [];
    private readonly HashSet<string> _demotedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedNames = new(StringComparer.OrdinalIgnoreCase);
    private uint[] _bgCpuSets = [];
    private bool _cpuSetsUsed;
    private string? _gameSaid, _parkingSaid;
    private bool _timerActive;
    private int _altTabTicks;
    private bool _altTabbed;

    internal string? Game
    {
        get { lock (_gate) return _game?.Id; }
    }

    internal ExoAiAutopilotSnapshot Snapshot()
    {
        lock (_gate)
            return new(_game is not null, _game?.Id, _game?.Title, _demoted.Count, _timerActive, _altTabbed);
    }

    /// <summary>What it did for the game (for the session's report), in the player's words.</summary>
    internal IReadOnlyList<string> Did(string game)
    {
        lock (_gate)
        {
            if (_game?.Id != game) return [.. _did];
            return Summary();
        }
    }

    private List<string> Summary()
    {
        var lines = new List<string>();
        if (_gameSaid is not null) lines.Add(_gameSaid);
        if (_demotedNames.Count > 0)
            lines.Add(_cpuSetsUsed
                ? $"Held back {Names(_demotedNames)} (efficiency mode, lower priority, background CPU sets, low disk and memory priority)"
                : $"Held back {Names(_demotedNames)} (efficiency mode, lower priority, low disk and memory priority)");
        if (_pausedNames.Count > 0) lines.Add($"Paused {Names(_pausedNames)}, as you chose, and started them again after");
        if (_parkingSaid is not null) lines.Add(_parkingSaid);
        return lines;
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count <= 4 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(4))} and {list.Count - 4} more";
    }

    /// <summary>From the run-state poll: the game that runs now (null with none). A new game starts its session; none ends it.</summary>
    internal void Follow(AutopilotGame? game)
    {
        lock (_gate)
        {
            var on = settings().On;
            if (!on || game is null)
            {
                if (_game is not null) EndLocked();
                return;
            }
            if (_game?.Id == game.Id)
            {
                _game = game; // its processes, read again
                return;
            }
            if (_game is not null) EndLocked();
            StartLocked(game);
        }
    }

    /// <summary>One look, as the timer makes it: newly started game processes, and background apps that started hogging.</summary>
    internal void Look()
    {
        lock (_gate)
        {
            if (_game is null) return;
            try { LookLocked(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Autopilot: a look failed: " + ex.GetType().Name); }
        }
    }

    /// <summary>Exo is closing, or Autopilot was turned off: the session's changes go back now.</summary>
    internal void End()
    {
        lock (_gate) EndLocked();
    }

    /// <summary>
    /// At Exo's start: what a crash left in the journal goes back (only session changes; Game Mode stays while Autopilot is on).
    /// A process that is gone, or one that is another program by now, has nothing to put back.
    /// </summary>
    internal int Recover()
    {
        lock (_gate)
        {
            var left = journal.Entries.Where(entry => !entry.Held).ToList();
            foreach (var entry in left) PutBack(entry);
            if (left.Count > 0) log($"Autopilot: put back {left.Count} changes a closed or crashed Exo left.");
            return left.Count;
        }
    }

    private void AcquireTimerLocked()
    {
        if (_timerActive) return;
        system.BeginHighResTimer();
        _timerActive = true;
    }

    private void ReleaseTimerLocked()
    {
        if (!_timerActive) return;
        system.EndHighResTimer();
        _timerActive = false;
    }

    private void RestoreProcessEntriesLocked(bool includePowerAndSuspended)
    {
        var entries = journal.Entries
            .Where(entry => !entry.Held && (includePowerAndSuspended || (entry.Kind != "Power" && entry.Kind != nameof(Knob.Suspended))))
            .ToList();
        foreach (var entry in entries) PutBack(entry);
    }

    private void StartLocked(AutopilotGame game)
    {
        _game = game;
        _did.Clear();
        _demoted.Clear();
        _demotedNames.Clear();
        _pausedNames.Clear();
        _cpuSetsUsed = false;
        _bgCpuSets = AutopilotCpuSets.BackgroundSetIds(system.CpuSets(), cpuClass(), ccdLayout?.Invoke());
        (_gameSaid, _parkingSaid) = (null, null);
        _cpu = [];
        _cpuAt = 0;
        _altTabTicks = 0;
        _altTabbed = false;
        _session = system.Processes().FirstOrDefault(row => row.Pid == _self).Session;
        log($"Autopilot: {game.Title} started.");
        Parking();
        LookLocked();
        Pause();
        _timer ??= startTimer(Look);
    }

    private void EndLocked()
    {
        _timer?.Dispose();
        _timer = null;
        ReleaseTimerLocked();
        _altTabTicks = 0;
        _altTabbed = false;
        var count = journal.Entries.Count(entry => !entry.Held);
        RestoreProcessEntriesLocked(includePowerAndSuspended: true);
        if (_game is not null)
        {
            _did.Clear();
            _did.AddRange(Summary());
            log($"Autopilot: {_game.Title} ended; {count} changes put back.");
        }
        _game = null;
    }

    private void LookLocked()
    {
        var game = _game!;
        var options = settings();
        if (!options.On)
        {
            EndLocked();
            return;
        }
        var rows = system.Processes();
        var now = Environment.TickCount64;
        var seconds = _cpuAt == 0 ? 0 : (now - _cpuAt) / 1000.0;
        var cpu = new Dictionary<(int, long), long>(rows.Count);
        foreach (var row in rows) cpu[(row.Pid, row.CreateTime)] = row.CpuTime;
        var before = _cpu;
        (_cpu, _cpuAt) = (cpu, now);

        var tree = game.Tree as IReadOnlySet<int> ?? game.Tree.ToHashSet();
        var front = system.Front();
        var inFront = tree.Contains(front);

        if (!inFront)
        {
            // Keep brief switches uninterrupted, then restore priorities and 1 ms timer if the player stays alt-tabbed.
            if (++_altTabTicks >= AltTabReleaseTicks && !_altTabbed)
            {
                _altTabbed = true;
                ReleaseTimerLocked();
                RestoreProcessEntriesLocked(includePowerAndSuspended: false);
                _demoted.Clear();
            }
            return;
        }

        _altTabTicks = 0;
        if (_altTabbed)
        {
            _altTabbed = false;
            _demoted.Clear();
        }
        AcquireTimerLocked();

        // The game: its own processes, as they start or when focus returns from alt-tab.
        if (game.MayTouchGame)
        {
            var wanted = AutopilotRules.GamePriority(options.GamePriority, true);
            var set = 0;
            foreach (var row in rows.Where(row => tree.Contains(row.Pid) && row.Session == _session && !ProcessTermination.IsAntiCheat(row.Name)))
            {
                if (wanted is { } priority
                    && Change(Knob.Priority, row, current => current is AutopilotRules.Normal or AutopilotRules.AboveNormal && current != priority ? priority : null))
                    set++;
                Change(Knob.Eco, row, current => current == AutopilotRules.EcoDefault ? AutopilotRules.EcoOff : null);
            }
            if (set > 0 && wanted is { } appliedPriority)
                _gameSaid = $"Gave {game.Title} {(appliedPriority == AutopilotRules.High ? "high" : "above normal")} priority";
        }
        else _gameSaid ??= $"Left {game.Title} itself alone: it runs with anti-cheat, so Exo doesn't open it";

        // Background apps: named, learned or hogging, while the game is in front.
        var named = new HashSet<string>(options.AlwaysDemote.Concat(game.Learned).Select(AutopilotSafety.Bare), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (_demoted.Count >= AutopilotRules.MaxDemoted) break;
            if (_demoted.Contains((row.Pid, row.CreateTime)) || AutopilotRules.LeftAlone(row, _session, tree, _self, front) is not null) continue;
            var cores = seconds > 0 && before.TryGetValue((row.Pid, row.CreateTime), out var was) ? (row.CpuTime - was) / (seconds * 1e7) : 0;
            var bare = AutopilotSafety.Bare(row.Name);
            if (!AutopilotRules.Hog(bare, cores, named)) continue;
            _demoted.Add((row.Pid, row.CreateTime));
            if (Demote(row)) _demotedNames.Add(bare);
        }
    }

    /// <summary>Efficiency mode, below normal, low disk and memory priority: each only from Windows' default, so nothing the app or the player set is overridden.</summary>
    private bool Demote(ProcessRow row)
    {
        var any = Change(Knob.Eco, row, current => current == AutopilotRules.EcoDefault ? AutopilotRules.EcoOn : null);
        any |= Change(Knob.Priority, row, AutopilotRules.Lower);
        any |= Change(Knob.Io, row, current => current == AutopilotRules.IoNormal ? AutopilotRules.IoLow : null);
        any |= Change(Knob.Memory, row, current => current == AutopilotRules.MemoryNormal ? AutopilotRules.MemoryLow : null);
        if (_bgCpuSets.Length > 0 && ApplyCpuSets(row))
        {
            _cpuSetsUsed = true;
            any = true;
        }
        return any;
    }

    private bool ApplyCpuSets(ProcessRow row)
    {
        var current = system.ReadCpuSets(row.Pid, row.CreateTime);
        if (current is null || current.Length != 0) return false;
        var entry = new JournalEntry("CpuSets", row.Pid, row.CreateTime, AutopilotSafety.Bare(row.Name), 0, _bgCpuSets.Length);
        if (!journal.Add(entry)) return false;
        if (system.WriteCpuSets(row.Pid, row.CreateTime, _bgCpuSets)) return true;
        journal.Remove(entry);
        return false;
    }

    /// <summary>The apps the player chose to pause while playing (never one on the safety list), once at the game's start.</summary>
    private void Pause()
    {
        var chosen = new HashSet<string>(settings().Suspend.Select(AutopilotSafety.Bare), StringComparer.OrdinalIgnoreCase);
        if (chosen.Count == 0) return;
        var tree = _game!.Tree;
        var front = system.Front();
        foreach (var row in system.Processes())
        {
            var bare = AutopilotSafety.Bare(row.Name);
            if (!chosen.Contains(bare) || AutopilotRules.LeftAlone(row, _session, tree, _self, front) is not null) continue;
            if (Change(Knob.Suspended, row, current => current == 1 ? null : 1, assumeBefore: 0)) _pausedNames.Add(bare);
        }
    }

    /// <summary>
    /// Reads a knob, asks <paramref name="next"/> what to write (null: leave it), journals, then writes; a write Windows refused
    /// takes its entry back out. <paramref name="assumeBefore"/>: a knob Windows doesn't read back (suspended) starts from this.
    /// </summary>
    private bool Change(Knob knob, ProcessRow row, Func<int?, int?> next, int? assumeBefore = null)
    {
        var current = assumeBefore ?? system.Read(knob, row.Pid, row.CreateTime);
        if (current is null || next(current) is not { } value || value == current) return false;
        if (knob == Knob.Priority && value == AutopilotRules.Realtime) return false;
        var entry = new JournalEntry(knob.ToString(), row.Pid, row.CreateTime, AutopilotSafety.Bare(row.Name), current.Value, value);
        if (!journal.Add(entry)) return false;
        if (system.Write(knob, row.Pid, row.CreateTime, value)) return true;
        journal.Remove(entry);
        return false;
    }

    /// <summary>
    /// Core parking off for the session on a plan that parks (Exo's own plan already doesn't): the active plan's minimum unparked
    /// cores to 100%, on AC only, never on battery and never on a dual-CCD X3D or Zen 5 part, whose AMD driver parks the second
    /// CCD in games on purpose. On an Intel hybrid only the performance cores' setting changes, so Thread Director keeps the E-cores.
    /// </summary>
    private void Parking()
    {
        if (!settings().CoreParking || system.OnBattery() || system.ActiveScheme() is not { } scheme) return;
        if (ParkingSetting(cpuClass()) is not { } setting) return;
        var before = system.PowerRead(scheme, PowerProfiles.Processor, setting);
        if (before is null or >= 100) return;
        var entry = new JournalEntry("Power", 0, 0, "Core parking", (int)before.Value, 100, scheme.ToString(), PowerProfiles.Processor.ToString(), setting.ToString());
        if (!journal.Add(entry)) return;
        if (!system.PowerWrite(scheme, PowerProfiles.Processor, setting, 100))
        {
            journal.Remove(entry);
            return;
        }
        _parkingSaid = $"Kept every processor core awake (your power plan parks down to {before}%)";
    }

    internal static readonly Guid MinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    internal static readonly Guid MinCoresPerformant = new("0cc5b647-c1df-4637-891a-dec35c318584");

    /// <summary>The minimum-cores setting Autopilot may raise for this processor class, or null where parking is the vendor's job.</summary>
    internal static Guid? ParkingSetting(PowerProfiles.CpuClass cpu) => cpu switch
    {
        PowerProfiles.CpuClass.AmdDualCcdX3d or PowerProfiles.CpuClass.AmdDualCcdZen5 => null,
        PowerProfiles.CpuClass.IntelHybrid => MinCoresPerformant,
        _ => MinCores,
    };

    /// <summary>One change back, only where it is still what Autopilot wrote; the entry goes either way.</summary>
    private void PutBack(JournalEntry entry)
    {
        try
        {
            if (entry.Kind == "CpuSets")
            {
                var current = system.ReadCpuSets(entry.Pid, entry.Created);
                if (current is { Length: > 0 } && current.Length == entry.Wrote)
                    system.WriteCpuSets(entry.Pid, entry.Created, null);
            }
            else if (entry.Kind == "Power")
            {
                if (Guid.TryParse(entry.Scheme, out var scheme) && Guid.TryParse(entry.Subgroup, out var subgroup) && Guid.TryParse(entry.Setting, out var setting)
                    && system.PowerRead(scheme, subgroup, setting) == (uint)entry.Wrote
                    && !system.PowerWrite(scheme, subgroup, setting, (uint)entry.Before))
                {
                    log("Autopilot: Windows didn't put core parking back; it stays in the journal.");
                    return;
                }
            }
            else if (Enum.TryParse<Knob>(entry.Kind, out var knob))
            {
                var now = knob == Knob.Suspended ? entry.Wrote : system.Read(knob, entry.Pid, entry.Created);
                if (now == entry.Wrote) system.Write(knob, entry.Pid, entry.Created, entry.Before);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Autopilot: a put back failed: " + ex.GetType().Name); }
        journal.Remove(entry);
    }
}
