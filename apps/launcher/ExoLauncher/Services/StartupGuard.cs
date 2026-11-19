using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>Where one removed start-up item stands with the guard (9-startup §B).</summary>
internal enum GuardState
{
    /// <summary>Not removed by Exo: nothing is guarded.</summary>
    Untracked,
    /// <summary>The player turned it off in Exo; Exo keeps it off.</summary>
    Off,
    /// <summary>Its program turned it back on once in the last 30 days; it was put off again, silently.</summary>
    Back,
    /// <summary>It came back again: the program's own switch (the catalog's) was used, once.</summary>
    Switched,
    /// <summary>It came back again and there is no switch Exo can use: marked, and Exo stops.</summary>
    Marked,
    /// <summary>Started by an updater service: the entry is kept off, and the row says why it still runs.</summary>
    Updater,
    /// <summary>Started by its anti-cheat (Riot Vanguard's tray): Exo stops fighting, writes nothing to it, and the row says so.</summary>
    AntiCheat,
    /// <summary>The player turned it on in Task Manager, Settings or Task Scheduler: left on, for good, until turned off in Exo again.</summary>
    PlayerOn,
    /// <summary>Removed by an older Exo although it is one Exo never turns off: it stays as the owner chose, and can only be put back.</summary>
    Locked,
    /// <summary>A machine-wide item came back: turning it off again needs the player's OK, so Exo left it on and says so.</summary>
    NeedsOk,
}

internal enum GuardEvent
{
    PlayerTurnedOff, PlayerTurnedOn, ProgramBack,
    RepeatSwitch, SwitchBlocked, SwitchReady, RepeatUpdater, RepeatAntiCheat, RepeatNothing,
    /// <summary>Found back on with no evidence of who did it: put off again once, silently.</summary>
    UnknownBack,
    /// <summary>Found back on again with no evidence, within 30 days: left on, and Exo stops.</summary>
    RepeatUnknown,
    RunsUpdater, RunsAntiCheat, PutBack, LockedRemoval,
    /// <summary>A machine-wide item came back: its off-change needs Windows' permission, which the guard never asks for.</summary>
    MachineBack,
}

internal enum GuardAction
{
    None, RecordOff, PutOffAgain, PutOffAndSwitch, ApplySwitch, UndoSwitchAndDrop, UndoSwitchAndForget, DropRemoval, Forget, LogOnly,
}

/// <summary>One row of the guard's table: in <see cref="State"/>, on <see cref="Event"/>, go to <see cref="Next"/> and do <see cref="Action"/>.</summary>
internal sealed record GuardRow(GuardState State, GuardEvent Event, GuardState Next, GuardAction Action);

/// <summary>
/// Watches what Exo took out of start-up and puts it off again when its program turns it back on. It checks at Exo's start,
/// when a game starts and ends, every ten minutes (scheduled tasks have no cheap notification, and Exo must not turn on the
/// Task Scheduler log, which is a Windows setting), and at once when Windows says the Run keys, StartupApproved, the Startup
/// folders or the Store apps' task states changed (RegNotifyChangeKeyValue and a FileSystemWatcher: no polling for those).
/// With nothing removed there is nothing to watch: no watch and no timer. All its state moves through <see cref="Table"/>.
/// </summary>
internal sealed class StartupGuard : IDisposable
{
    // ---- The table: the only place a state changes ----

    /// <summary>States, events, next state, action. A test walks every row and proves no state lacks an exit.</summary>
    internal static readonly IReadOnlyList<GuardRow> Table =
    [
        new(GuardState.Untracked, GuardEvent.PlayerTurnedOff, GuardState.Off, GuardAction.RecordOff),
        new(GuardState.Untracked, GuardEvent.LockedRemoval, GuardState.Locked, GuardAction.None),

        new(GuardState.Off, GuardEvent.PlayerTurnedOff, GuardState.Off, GuardAction.RecordOff),
        new(GuardState.Off, GuardEvent.ProgramBack, GuardState.Back, GuardAction.PutOffAgain),
        new(GuardState.Off, GuardEvent.UnknownBack, GuardState.Back, GuardAction.PutOffAgain),
        new(GuardState.Off, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.DropRemoval),
        new(GuardState.Off, GuardEvent.RunsUpdater, GuardState.Updater, GuardAction.None),
        new(GuardState.Off, GuardEvent.RunsAntiCheat, GuardState.AntiCheat, GuardAction.None),
        new(GuardState.Off, GuardEvent.LockedRemoval, GuardState.Locked, GuardAction.None),
        new(GuardState.Off, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),
        new(GuardState.Off, GuardEvent.MachineBack, GuardState.NeedsOk, GuardAction.LogOnly),

        new(GuardState.Back, GuardEvent.ProgramBack, GuardState.Back, GuardAction.PutOffAgain),
        new(GuardState.Back, GuardEvent.RepeatSwitch, GuardState.Switched, GuardAction.PutOffAndSwitch),
        new(GuardState.Back, GuardEvent.SwitchBlocked, GuardState.Back, GuardAction.PutOffAgain),
        new(GuardState.Back, GuardEvent.SwitchReady, GuardState.Switched, GuardAction.ApplySwitch),
        new(GuardState.Back, GuardEvent.RepeatUpdater, GuardState.Updater, GuardAction.PutOffAgain),
        new(GuardState.Back, GuardEvent.RepeatAntiCheat, GuardState.AntiCheat, GuardAction.None),
        new(GuardState.Back, GuardEvent.RepeatNothing, GuardState.Marked, GuardAction.PutOffAgain),
        new(GuardState.Back, GuardEvent.UnknownBack, GuardState.Back, GuardAction.PutOffAgain),
        new(GuardState.Back, GuardEvent.RepeatUnknown, GuardState.Marked, GuardAction.LogOnly),
        new(GuardState.Back, GuardEvent.RunsUpdater, GuardState.Updater, GuardAction.None),
        new(GuardState.Back, GuardEvent.RunsAntiCheat, GuardState.AntiCheat, GuardAction.None),
        new(GuardState.Back, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.DropRemoval),
        new(GuardState.Back, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),
        new(GuardState.Back, GuardEvent.MachineBack, GuardState.NeedsOk, GuardAction.LogOnly),

        new(GuardState.Switched, GuardEvent.ProgramBack, GuardState.Marked, GuardAction.PutOffAgain),
        // Found back on with no evidence of who put it there: left in its state, said once, and nothing is changed.
        new(GuardState.Switched, GuardEvent.UnknownBack, GuardState.Switched, GuardAction.LogOnly),
        new(GuardState.Switched, GuardEvent.RepeatUnknown, GuardState.Switched, GuardAction.LogOnly),
        new(GuardState.Switched, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.UndoSwitchAndDrop),
        new(GuardState.Switched, GuardEvent.PutBack, GuardState.Untracked, GuardAction.UndoSwitchAndForget),
        // A change Windows would not let the user make without its permission: said once, and nothing else changed.
        new(GuardState.Switched, GuardEvent.MachineBack, GuardState.Switched, GuardAction.LogOnly),

        new(GuardState.Marked, GuardEvent.ProgramBack, GuardState.Marked, GuardAction.LogOnly),
        new(GuardState.Marked, GuardEvent.UnknownBack, GuardState.Marked, GuardAction.LogOnly),
        new(GuardState.Marked, GuardEvent.RepeatUnknown, GuardState.Marked, GuardAction.LogOnly),
        new(GuardState.Marked, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.DropRemoval),
        new(GuardState.Marked, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),
        new(GuardState.Marked, GuardEvent.MachineBack, GuardState.Marked, GuardAction.LogOnly),

        new(GuardState.Updater, GuardEvent.ProgramBack, GuardState.Updater, GuardAction.PutOffAgain),
        new(GuardState.Updater, GuardEvent.UnknownBack, GuardState.Updater, GuardAction.LogOnly),
        new(GuardState.Updater, GuardEvent.RepeatUnknown, GuardState.Updater, GuardAction.LogOnly),
        new(GuardState.Updater, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.DropRemoval),
        new(GuardState.Updater, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),
        // Its own entry can't be turned off without Windows' permission: it stays in its state, and the row says why.
        new(GuardState.Updater, GuardEvent.MachineBack, GuardState.Updater, GuardAction.LogOnly),

        new(GuardState.AntiCheat, GuardEvent.ProgramBack, GuardState.AntiCheat, GuardAction.None),
        new(GuardState.AntiCheat, GuardEvent.UnknownBack, GuardState.AntiCheat, GuardAction.LogOnly),
        new(GuardState.AntiCheat, GuardEvent.RepeatUnknown, GuardState.AntiCheat, GuardAction.LogOnly),
        new(GuardState.AntiCheat, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.DropRemoval),
        new(GuardState.AntiCheat, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),
        new(GuardState.AntiCheat, GuardEvent.MachineBack, GuardState.AntiCheat, GuardAction.LogOnly),

        new(GuardState.PlayerOn, GuardEvent.PlayerTurnedOff, GuardState.Off, GuardAction.RecordOff),
        new(GuardState.PlayerOn, GuardEvent.ProgramBack, GuardState.PlayerOn, GuardAction.None),
        new(GuardState.PlayerOn, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),

        // A machine-wide item that came back waits for the player's OK, is said once, and does not keep coming up.
        new(GuardState.NeedsOk, GuardEvent.MachineBack, GuardState.NeedsOk, GuardAction.None),
        new(GuardState.NeedsOk, GuardEvent.ProgramBack, GuardState.NeedsOk, GuardAction.None),
        new(GuardState.NeedsOk, GuardEvent.UnknownBack, GuardState.NeedsOk, GuardAction.None),
        new(GuardState.NeedsOk, GuardEvent.RepeatUnknown, GuardState.NeedsOk, GuardAction.None),
        // The player pressed Remove again: that press is the OK, and it turned the item off.
        new(GuardState.NeedsOk, GuardEvent.PlayerTurnedOff, GuardState.Off, GuardAction.RecordOff),
        new(GuardState.NeedsOk, GuardEvent.PlayerTurnedOn, GuardState.PlayerOn, GuardAction.DropRemoval),
        new(GuardState.NeedsOk, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),

        new(GuardState.Locked, GuardEvent.PutBack, GuardState.Untracked, GuardAction.Forget),
    ];

    /// <summary>The row for an event in a state; null when the state has no such row (the event is ignored).</summary>
    internal static GuardRow? Step(GuardState state, GuardEvent ev) => Table.FirstOrDefault(row => row.State == state && row.Event == ev);

    // ---- The record: its own file for now, shaped to move into keep.json's task domain ----

    internal sealed class Item
    {
        public string Id { get; set; } = "";
        public string? Fingerprint { get; set; }
        public string Mechanism { get; set; } = "";
        public GuardState State { get; set; }
        public DateTime Since { get; set; }
        /// <summary>When it came back (UTC), the last ones; two within 30 days is a repeat.</summary>
        public List<DateTime> Comebacks { get; set; } = [];
        /// <summary>The real switch: what Exo changed and what it was, to put back exactly.</summary>
        public string? SwitchFile { get; set; }
        public string? SwitchPath { get; set; }
        /// <summary>The setting's value before (JSON text); null when it had none.</summary>
        public string? SwitchBefore { get; set; }
        public string? SwitchSet { get; set; }
        /// <summary>The program's file as it was, and as Exo wrote it: put back byte for byte when nothing else changed it.</summary>
        public string? SwitchFileBefore { get; set; }
        public string? SwitchFileWritten { get; set; }
        public bool SwitchPending { get; set; }
        /// <summary>The command the item's value had when last seen, so a rewrite is logged once.</summary>
        public string? Seen { get; set; }
        public bool LockedHandled { get; set; }
        /// <summary>Marked and told once: the log says it one time, not at every check.</summary>
        public bool Told { get; set; }
        /// <summary>Times the program's own setting could not be read or written this moment (a file being rewritten): the third marks the item.</summary>
        public int SwitchTries { get; set; }
    }

    internal sealed class Record
    {
        public int Version { get; set; } = 1;
        public List<Item> Items { get; set; } = [];
    }

    private static string RecordPath(IStartupHost host) => Path.Combine(host.DataFolder, "startup-guard.json");

    /// <summary>
    /// The record. A file that doesn't parse is moved aside as <c>.unreadable</c> and the last good one (<c>.bak</c>) is read
    /// instead, so no program switch Exo set is forgotten; a file that can't be READ is neither moved nor saved over, and
    /// this check leaves it alone.
    /// </summary>
    internal static Record Load(IStartupHost host)
    {
        Unreadable = false;
        string? text;
        try { text = host.ReadFile(RecordPath(host)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Unreadable = true; return new Record(); }
        if (text is null) return new Record();
        try { return JsonSerializer.Deserialize<Record>(text) ?? new Record(); }
        catch (JsonException)
        {
            AppLog.Warn("Startup: the guard's record didn't read; the last good one is used.");
            try { host.WriteFile(RecordPath(host) + ".unreadable", text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { AppLog.Warn("Startup: the damaged record could not be set aside: " + ex.Message); }
            try { return host.ReadFile(RecordPath(host) + ".bak") is { } good ? JsonSerializer.Deserialize<Record>(good) ?? new Record() : new Record(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Record(); }
        }
    }

    /// <summary>Whether the record could not be read this check: nothing is saved over a file Exo couldn't read.</summary>
    [ThreadStatic] private static bool s_unreadable;

    private static bool Unreadable
    {
        get => s_unreadable;
        set => s_unreadable = value;
    }

    /// <summary>Writes the record, keeping the last good one as <c>.bak</c>; never over a file that couldn't be read.</summary>
    private static void Save(IStartupHost host, Record record)
    {
        if (Unreadable) return;
        var path = RecordPath(host);
        // The last good copy goes to .bak first, so a half-written file can always fall back to it.
        SaveBackup(host, path);
        host.WriteFile(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        // A first save had nothing to copy yet: the .bak is this good file.
        if (!host.FileExists(path + ".bak")) SaveBackup(host, path);
    }

    /// <summary>The file as it is now, when it parses: kept as <c>.bak</c>.</summary>
    private static void SaveBackup(IStartupHost host, string path)
    {
        string? text;
        try { text = host.ReadFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (text is null) return;
        try { _ = JsonSerializer.Deserialize<Record>(text); }
        catch (JsonException) { return; }
        host.WriteFile(path + ".bak", text);
    }

    internal static string? StatusOf(Item item, StartupCatalog.Item? known) => item.State switch
    {
        GuardState.Updater => known?.RealSwitch?.Status ?? "Started by its updater service",
        GuardState.AntiCheat => known?.RealSwitch?.Status ?? "Starts with its anti-cheat when you play",
        GuardState.Marked => "It turns itself back on, so Exo stopped turning it off",
        GuardState.Switched => "Turned off in its own settings",
        GuardState.NeedsOk => "It came back, and turning it off again needs your OK",
        _ => null,
    };

    // ---- The guard ----

    private static readonly List<StartupGuard> Live = [];

    /// <summary>The guard the app runs; null until it starts.</summary>
    internal static StartupGuard? Shared { get; private set; }

    internal enum CheckKind { Start, Game, Tick, Change, Refresh }

    /// <summary>Every line the guard logs, for a test to read.</summary>
    internal Action<string>? Logged { get; set; }

    private readonly IStartupHost _host;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _tick;
    private readonly Lock _gate = new();
    private IDisposable? _watch;
    private Timer? _timer;
    private Timer? _later;
    private string? _liveForeground;
    private DateTime _liveAt;
    private bool _disposed;

    internal StartupGuard(IStartupHost host, TimeSpan? debounce = null, TimeSpan? tick = null)
    {
        _host = host;
        _debounce = debounce ?? TimeSpan.FromSeconds(1);
        _tick = tick ?? TimeSpan.FromMinutes(10);
        lock (Live) Live.Add(this);
    }

    /// <summary>The app's guard: checks now, then watches for as long as something is removed.</summary>
    internal static StartupGuard StartShared()
    {
        var guard = new StartupGuard(StartupEntries.Host);
        Shared = guard;
        // Windows Security's list is read in the background from the start, so the first check finds an answer at once.
        if (StartupEntries.Host is RealStartupHost real) real.WarmSecurity();
        guard.Start();
        return guard;
    }

    internal void Start() => Check(CheckKind.Start);

    /// <summary>A game started or ended: what its launcher or helper turned back on is found now.</summary>
    internal void NotifyGame() => Check(CheckKind.Game);

    /// <summary>Whether Exo's watch is open now (a test reads it; nothing removed means none).</summary>
    internal bool Watching { get { lock (_gate) return _watch is not null; } }

    private static void Notify(IStartupHost host)
    {
        StartupGuard[] guards;
        lock (Live) guards = [.. Live.Where(guard => guard._host == host)];
        foreach (var guard in guards) guard.Arm();
    }

    /// <summary>Opens or closes the watch and the timer to match whether anything is removed.</summary>
    private void Arm()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var record = Load(_host);
            var any = StartupEntries.Kept(_host).Any(removal => record.Items.FirstOrDefault(item => item.Id == removal.Id) is not { State: GuardState.PlayerOn or GuardState.Locked });
            if (any && _watch is null) _watch = _host.Watch(OnChanged);
            if (any && _timer is null) _timer = new Timer(_ => Check(CheckKind.Tick), null, _tick, _tick);
            if (!any)
            {
                _watch?.Dispose();
                _watch = null;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    /// <summary>Windows said something about start-up changed: who is in front now is the evidence of who did it.</summary>
    private void OnChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _liveForeground = _host.Foreground();
            _liveAt = _host.UtcNow;
            if (_debounce > TimeSpan.Zero)
            {
                _later ??= new Timer(_ => Check(CheckKind.Change));
                _later.Change(_debounce, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        Check(CheckKind.Change);
    }

    private static readonly string[] PlayerTools = ["taskmgr", "systemsettings"];
    private static readonly string[] SchedulerTools = ["mmc", "taskschd"];

    private static string ToolName(string tool) => tool switch { "taskmgr" => "Task Manager", "systemsettings" => "Settings", _ => "Task Scheduler" };

    /// <summary>One check at a time: a check that arrives while another runs (a tick during a prompt) is skipped.</summary>
    private int _checking;

    /// <summary>Set when no check is running: <see cref="Dispose"/> waits on it, so nothing writes after it returns.</summary>
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    /// <summary>Items a change did not hold for, each already told once: said once until a change for it holds.</summary>
    private readonly HashSet<string> _toldFailed = new(StringComparer.Ordinal);

    internal void Check(CheckKind kind)
    {
        if (HostMode.IsDisposable) return;
        // Two checks at once would count one comeback twice and hand the same change to Windows twice: the second is
        // skipped and the watch re-armed, and the next tick or change finds the state again.
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        _idle.Reset();
        try
        {
            List<string> logs;
            // Deciding, applying, verifying and saving all happen under the one lock. The guard never asks Windows for
            // permission, so its registry writes take milliseconds; the task list is read at most twice in a check (once at
            // its start, once to see what its own changes came to), each read bounded to five seconds, and a task change
            // that needs elevation is left for the player instead of being retried. The player's own press waits for that.
            lock (StartupEntries.Sync)
            {
                if (_disposed) return;
                string? live;
                lock (_gate) live = kind == CheckKind.Change && _host.UtcNow - _liveAt < TimeSpan.FromSeconds(10) ? _liveForeground : null;
                logs = Run(kind, live);
            }
            foreach (var line in logs) { AppLog.Info(line); Logged?.Invoke(line); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or InvalidOperationException)
        {
            AppLog.Warn("Startup: the guard's check didn't finish: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
            _idle.Set();
            // A timer's callback: a watch that can't be armed is said and left, never thrown out of the callback.
            try { Arm(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                AppLog.Warn("Startup: the guard's watch could not be set: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Under the lock: each removed item's decision this check, the changes handed over, what held, and the record written.
    /// Nothing is applied outside the lock.
    /// </summary>
    private List<string> Run(CheckKind kind, string? live)
    {
        var record = Load(_host);
        var before = JsonSerializer.Serialize(record);
        // One snapshot of the task list per check: `schtasks` is asked once, here, and everything this check decides reads
        // the same list (each read is a process Exo waits on, and the check holds the player's own press off while it runs).
        using var tasks = StartupEntries.UseTaskList(_host.Tasks(StartupEntries.GuardWait));
        var (plans, logs) = Decide(record, kind, live);
        Apply(plans);
        Commit(record, plans, logs);
        // Nothing in the record changed: nothing is written, and .bak is not copied over.
        if (JsonSerializer.Serialize(record) != before) Save(_host, record);
        return logs;
    }

    /// <summary>
    /// One removed item's decision this check: the changes it takes, what runs once they are made, the lines to log, and
    /// what it was before, so a change that did not hold can leave it exactly as it was.
    /// </summary>
    private sealed class Plan(StartupEntries.Removal removal, Item item)
    {
        internal sealed record Snapshot(GuardState State, List<DateTime> Comebacks, bool Told, bool SwitchPending, string? Seen, string? SwitchFile,
            string? SwitchPath, string? SwitchBefore, string? SwitchSet, string? SwitchFileBefore, string? SwitchFileWritten);

        internal StartupEntries.Removal Removal { get; } = removal;
        internal Item Item { get; } = item;
        internal List<StartupChange> Changes { get; } = [];
        internal List<Action> After { get; } = [];
        internal List<string> Logs { get; } = [];
        internal Snapshot Before { get; } = Snap(item);

        /// <summary>The comeback this check counted, when it counted one: added when the change held.</summary>
        internal DateTime? Comes { get; private set; }
        /// <summary>Which change did not hold, when one didn't.</summary>
        internal StartupChange? Bad { get; set; }
        /// <summary>The change Windows would not let the user make without its permission, when one was left: the item waits for the player's OK.</summary>
        internal StartupChange? Left { get; set; }
        /// <summary>The switch to set once the off-change held (nothing of the program's is written before then).</summary>
        internal RealSwitch? SwitchToSet { get; set; }
        /// <summary>The setting's current value, read when it was decided: what Put back puts back.</summary>
        internal (string? Before, string? Set, string? FileBefore)? SwitchWas { get; set; }
        /// <summary>The switch is wanted, and none could be read in <see cref="Switch"/>: the item is treated as having none.</summary>
        internal bool SwitchWanted { get; set; }
        /// <summary>The switch could not be read this moment: the item keeps the state it had, and the next comeback tries again.</summary>
        internal bool KeptAsIs { get; set; }

        /// <summary>
        /// It comes back this check within 30 days: the count that decides a first or a second return. Nothing is counted here:
        /// a comeback counts only once a row was found and its change held (<see cref="Counted"/>).
        /// </summary>
        internal bool Repeat(DateTime now) => Item.Comebacks.Count(at => now - at < TimeSpan.FromDays(30)) + 1 >= 2;

        /// <summary>The comeback this check counted, taken once the change that answered it held.</summary>
        internal void Counted(DateTime now) => Comes = now;

        /// <summary>The comeback is not counted after all: the switch that was to answer it could not be read this moment.</summary>
        internal void Uncounted() => Comes = null;

        internal void MovedTo(GuardState state) => Item.State = state;

        internal void Told() { }

        /// <summary>The entry the item was made as under another name: its id and what it takes to put it back, snapshotted in <see cref="Decide"/>, before anything is applied.</summary>
        internal void Also(StartupEntries.Also entry) => RenamedAs = entry;

        internal StartupEntries.Also? RenamedAs { get; private set; }

        /// <summary>The move is kept: the comeback it counted is counted now, and what it took to put the item back is saved.</summary>
        internal void Apply(DateTime now)
        {
            if (Comes is { } at) Item.Comebacks = [.. Item.Comebacks.Where(when => now - when < TimeSpan.FromDays(30)), at];
        }

        private static Snapshot Snap(Item item) =>
            new(item.State, [.. item.Comebacks], item.Told, item.SwitchPending, item.Seen, item.SwitchFile, item.SwitchPath, item.SwitchBefore, item.SwitchSet,
                item.SwitchFileBefore, item.SwitchFileWritten);

        /// <summary>Nothing happened: its state, its come-backs and its switch are what they were before the check.</summary>
        internal void Restore()
        {
            (Item.State, Item.Told, Item.SwitchPending, Item.Seen, Item.SwitchFile, Item.SwitchPath, Item.SwitchBefore, Item.SwitchSet, Item.SwitchFileBefore,
                Item.SwitchFileWritten) =
                (Before.State, Before.Told, Before.SwitchPending, Before.Seen, Before.SwitchFile, Before.SwitchPath, Before.SwitchBefore, Before.SwitchSet,
                    Before.SwitchFileBefore, Before.SwitchFileWritten);
            Item.Comebacks = Before.Comebacks;
        }
    }

    /// <summary>Under the lock: what each removed item's decision this check is, and the changes to hand over.</summary>
    private (List<Plan> Plans, List<string> Logs) Decide(Record record, CheckKind kind, string? live)
    {
        var removals = StartupEntries.Kept(_host);
        var entries = StartupEntries.ReadAll(_host);
        var now = _host.UtcNow;
        var logs = new List<string>();

        PutBackWrongOnes(record, removals, entries, logs);
        removals = StartupEntries.Kept(_host);
        // An item whose removal is gone was put back, dropped or forgotten already: nothing of it is kept.
        var kept = removals.Select(removal => removal.Id).ToHashSet(StringComparer.Ordinal);
        record.Items.RemoveAll(item => !kept.Contains(item.Id));

        var plans = new List<Plan>();
        foreach (var removal in removals)
        {
            var item = record.Items.FirstOrDefault(known => known.Id == removal.Id);
            if (item is null)
            {
                item = new Item { Id = removal.Id, Fingerprint = removal.Fingerprint, Mechanism = StartupEntries.Split(removal.Id).Kind, State = GuardState.Off, Since = removal.At, Seen = removal.Value };
                record.Items.Add(item);
            }
            if (item.State is GuardState.PlayerOn or GuardState.Locked) continue;
            var plan = new Plan(removal, item);
            Judge(plan, entries, kind, live, now);
            plans.Add(plan);
        }

        return (plans, logs);
    }

    /// <summary>
    /// Under the lock: the changes are handed over, never elevated, and read back to see which held. A change Windows will not
    /// let the user make without its permission (a task an installer created as administrator) is left by the hand-over and
    /// named: it is not a change that failed, and handing it over again every ten minutes changes nothing.
    /// </summary>
    private void Apply(List<Plan> plans)
    {
        var changes = plans.SelectMany(plan => plan.Changes).ToList();
        if (changes.Count == 0) return;
        var left = StartupEntries.LeftChanges(_host.Apply(changes, elevate: false)).ToHashSet();
        // The check's list was read before these changes: read it once more, here, for all of them. A task change that did
        // not need Windows' permission is seen in it; one that does need it may have been left, which is a different thing
        // from a change that failed (Commit answers it from what was left).
        var mayBeLeft = changes.Any(change => change is SetTask or DeleteTask or CreateTask or MoveFile or MachineValues);
        var after = mayBeLeft ? StartupEntries.ReadTasks(_host, StartupEntries.GuardWait) : null;
        foreach (var plan in plans)
        {
            plan.Left = plan.Changes.FirstOrDefault(left.Contains);
            plan.Bad = plan.Left is not null || plan.Changes.Count == 0 ? null : StartupEntries.Unverified(_host, plan.Changes, after, StartupEntries.GuardWait);
        }
    }

    /// <summary>Under the lock: what held is kept; what did not keeps its state and come-backs, and is told once.</summary>
    private void Commit(Record record, List<Plan> plans, List<string> logs)
    {
        var now = _host.UtcNow;
        foreach (var plan in plans)
        {
            if (plan.Bad is { } bad)
            {
                plan.Restore();
                ThisCheckFailed(plan, logs, bad.GetType().Name);
                continue;
            }
            // Windows would not let the user make this change without its permission: the row says so, once, and draws no
            // switch and counts no comeback. Whatever its state, it waits for the player's own press, which may ask.
            if (plan.Left is not null && Step(plan.Item.State, GuardEvent.MachineBack) is { } wait)
            {
                plan.MovedTo(wait.Next);
                if (!plan.Item.Told) logs.Add(ThisCheckWaits(plan, wait));
                plan.Item.Told = true;
                _toldFailed.Remove(plan.Removal.Id);
                continue;
            }
            // The program's own setting is written only now, after the off-change held.
            if (plan.SwitchToSet is { } route)
            {
                switch (WriteSwitch(record, plan, route, logs))
                {
                    case SwitchDone.Written:
                        logs.Add($"Startup: {plan.Removal.Name} kept coming back; turned it off in its own settings ({route.Path} = {route.Off}).");
                        break;
                    case SwitchDone.Running:
                        // Still running: nothing was written, and the switch waits for the program to close, as a held one does.
                        plan.Restore();
                        plan.Item.SwitchPending = true;
                        break;
                    case SwitchDone.NoSwitch:
                        // No switch can be used (a share, no off value, a file that never reads): the item is kept as one with none.
                        plan.MovedTo(GuardState.Marked);
                        break;
                    case SwitchDone.TryAgain:
                        // The setting could not be read this moment (the program is rewriting it): the item keeps the state
                        // its row gave it and its start-up entry is off again; its next comeback tries the switch again.
                        plan.Apply(now);
                        _toldFailed.Remove(plan.Removal.Id);
                        if (plan.RenamedAs is { } renamedLater) StartupEntries.NoteRenamed(_host, plan.Removal.Id, renamedLater);
                        logs.AddRange(plan.Logs);
                        foreach (var action in plan.After) action();
                        continue;
                    case SwitchDone.NotSaved:
                        // Nothing was written and nothing is left to undo: the item keeps what it had, and the switch is tried again.
                        plan.Restore();
                        continue;
                    case SwitchDone.Broken:
                        // The intent is in the record, so Put back can still undo it; Exo stops trying.
                        plan.MovedTo(GuardState.Marked);
                        break;
                }
            }
            else if (plan.SwitchWanted) plan.MovedTo(GuardState.Marked);
            plan.Apply(now);
            _toldFailed.Remove(plan.Removal.Id);
            if (plan.RenamedAs is { } renamed) StartupEntries.NoteRenamed(_host, plan.Removal.Id, renamed);
            logs.AddRange(plan.Logs);
            foreach (var action in plan.After) action();
        }
    }

    /// <summary>
    /// The one line for a change Windows would not let the user make without its permission: what the player has to do, said
    /// in the words the row already uses for a machine-wide item that came back, and for an item Exo stopped turning off.
    /// </summary>
    private string ThisCheckWaits(Plan plan, GuardRow wait) => wait.Next == GuardState.NeedsOk
        ? $"Startup: {plan.Removal.Name} came back; it is machine-wide, so Exo left it for you to turn it off again."
        : wait.Action == GuardAction.PutOffAgain
            ? $"Startup: {plan.Removal.Name} keeps turning itself back on, and turning it off again needs your OK; left on."
            : $"Startup: {plan.Removal.Name} keeps turning itself back on; Exo has stopped turning it off.";

    /// <summary>
    /// A change did not hold: the item keeps what it had, and the player is told once, not at every check. Said once until a
    /// change for it holds; kept for this run only, so a check that changes nothing in the record writes nothing.
    /// </summary>
    private void ThisCheckFailed(Plan plan, List<string> logs, string why = "it was not as expected afterwards")
    {
        if (!_toldFailed.Contains(plan.Removal.Id)) logs.Add($"Startup: {plan.Removal.Name} was not put off (a change did not hold: {why}).");
        _toldFailed.Add(plan.Removal.Id);
    }

    // ---- Items that older Exo versions removed although Exo never turns them off ----

    private void PutBackWrongOnes(Record record, IReadOnlyList<StartupEntries.Removal> removals, IReadOnlyList<StartupEntry> entries, List<string> logs)
    {
        var back = new List<StartupEntries.Removal>();
        foreach (var removal in removals)
        {
            var item = record.Items.FirstOrDefault(known => known.Id == removal.Id);
            if (item is { LockedHandled: true }) continue;
            var live = entries.FirstOrDefault(entry => entry.Id == removal.Id);
            var program = removal.Program ?? live?.Program ?? StartupEntries.ProgramOf(removal.Value ?? "");
            var command = removal.Value ?? live?.Command ?? program ?? "";
            var kind = LockOf(_host, removal, live);
            item ??= Add(record, removal);
            if (kind == LockKind.None) { item.LockedHandled = true; continue; }
            if (StartupLocks.OwnerChoice(program, command))
            {
                // The owner took Windows Security's tray out on purpose (2026-10-31): it stays out, shown under Removed, locked from now on.
                item.State = Step(item.State, GuardEvent.LockedRemoval)?.Next ?? item.State;
                item.LockedHandled = true;
                logs.Add($"Startup: {removal.Name} stays removed, as you chose; Exo won't remove it again.");
                continue;
            }
            // Putting a machine-wide one back needs Windows' permission, which the guard never asks for: it stays under
            // Removed with Put back (the player's own press), and is said once.
            if (StartupEntries.MachineWide(removal.Id))
            {
                item.LockedHandled = true;
                logs.Add($"Startup: {removal.Name} stays removed; putting it back needs your OK.");
                continue;
            }
            back.Add(removal);
        }
        if (back.Count == 0) return;
        var (done, _, _, left) = StartupEntries.PutBackMany(_host, back, elevate: false, StartupEntries.TaskList);
        foreach (var removal in done)
        {
            record.Items.RemoveAll(known => known.Id == removal.Id);
            logs.Add($"Startup: put {removal.Name} back, because Exo leaves it in startup.");
        }
        // Windows would not let the user put it back without its permission (a task an installer made as administrator): it
        // stays under Removed, is said once, and is not put back again at every check.
        foreach (var removal in left)
        {
            var item = record.Items.FirstOrDefault(known => known.Id == removal.Id) ?? Add(record, removal);
            item.LockedHandled = true;
            logs.Add($"Startup: {removal.Name} stays under Removed; putting it back needs your OK.");
        }
    }

    /// <summary>Whether what Exo removed is one Exo never turns off, by the evidence its record and the live item give.</summary>
    internal static LockKind LockOf(IStartupHost host, StartupEntries.Removal removal, StartupEntry? live = null)
    {
        var program = removal.Program ?? live?.Program ?? StartupEntries.ProgramOf(removal.Value ?? "");
        var command = removal.Value ?? live?.Command ?? program ?? "";
        var leaf = removal.Id[(removal.Id.LastIndexOf('\\') + 1)..];
        var known = StartupCatalog.Find(removal.Name, program is null ? [] : [Path.GetFileName(program).ToLowerInvariant()], program ?? command);
        var kind = StartupLocks.Classify(host, new LockCandidate(leaf.Length > 0 ? leaf : removal.Name, command, program), known);
        return kind != LockKind.None ? kind : StartupLocks.Classify(host, new LockCandidate(removal.Name, command, program), known);
    }

    private static Item Add(Record record, StartupEntries.Removal removal)
    {
        var item = new Item { Id = removal.Id, Fingerprint = removal.Fingerprint, Mechanism = StartupEntries.Split(removal.Id).Kind, State = GuardState.Off, Since = removal.At, Seen = removal.Value };
        record.Items.Add(item);
        return item;
    }

    // ---- One removed item: what happened to it, and what the table says to do ----

    /// <summary>
    /// Who turned a removed item back on, by the evidence there is, and what the table then does:
    /// - the player: the change was seen live with Task Manager or Settings in front (Task Scheduler for a task): his, left on
    ///   for good (<see cref="GuardEvent.PlayerTurnedOn"/>);
    /// - the program: seen live with another program in front, its value rewritten, the entry made again under a new name, or
    ///   a task, service or RunOnce value (which those tools don't show): off again once (<see cref="GuardEvent.ProgramBack"/>),
    ///   and a second return within 30 days is its own switch (<see cref="GuardEvent.RepeatSwitch"/>), or marked when it has none;
    /// - nobody seen: a visible item found back on with <c>live</c> null is off again once, silently, as a comeback
    ///   (<see cref="GuardEvent.UnknownBack"/>); found back on again within 30 days with still no evidence, it is left on with
    ///   <see cref="GuardState.Marked"/>, the record kept for Put back, and the program's own switch is NOT used: Exo does not
    ///   change a program's setting when it cannot tell the program from the player (<see cref="GuardEvent.RepeatUnknown"/>).
    /// </summary>
    private void Judge(Plan plan, IReadOnlyList<StartupEntry> entries, CheckKind kind, string? live, DateTime now)
    {
        var (removal, item) = (plan.Removal, plan.Item);
        var (changes, logs, after) = (plan.Changes, plan.Logs, plan.After);
        var mechanism = item.Mechanism;
        var current = entries.FirstOrDefault(entry => entry.Id == removal.Id);
        // The same item under another name: a task made again under a new GUID, a Run value with a new hash in its name.
        // One Exo leaves in start-up (Windows', security software, anti-cheat, a driver, Exo itself) is never the item's: left alone.
        var renamed = current is { Enabled: true } ? null
            : (removal.Fingerprint ?? item.Fingerprint) is { Length: > 0 } print
                ? entries.FirstOrDefault(entry => entry.Enabled && entry.Id != removal.Id && entry.Fingerprint == print && SameKind(entry.Id, removal.Id) && !entry.Locked)
                : null;
        var back = current is { Enabled: true } ? current : renamed;
        // The entry under another name as it is now: taken before anything is applied, or an off-change made this check
        // would be what Put back later restores as "on".
        if (renamed is not null && StartupEntries.Filled(_host, renamed) is { } also) plan.Also(also);
        var program = removal.Program ?? current?.Program ?? StartupEntries.ProgramOf(removal.Value ?? "");
        var route = RouteOf(removal, program);

        // A pending real switch waits for the program's own app to close.
        if (item.State == GuardState.Back && item.SwitchPending && route is { Kind: "jsonSetting" } pending && !Running(pending))
            Fire(GuardEvent.SwitchReady);

        if (back is null)
        {
            // Still off. A value its program wrote again while off is told once; a program that runs another way is told apart.
            if (current is { Fingerprint.Length: > 0 } && current.Command != item.Seen && StartupEntries.Split(removal.Id).Kind.StartsWith("run", StringComparison.Ordinal))
            {
                logs.Add($"Startup: {current.Name} wrote itself back into startup; still off.");
                item.Seen = current.Command;
            }
            if (route is { Kind: "antiCheat" or "updaterService" } && item.State is GuardState.Off or GuardState.Back && ProgramRuns(program))
                Fire(route.Kind == "antiCheat" ? GuardEvent.RunsAntiCheat : GuardEvent.RunsUpdater);
            else if (item.State is GuardState.Switched or GuardState.Marked or GuardState.Updater or GuardState.AntiCheat)
            {
                // Found back on with no evidence of who put it there after the switch was used or Exo had stopped: nothing
                // is changed, the player is told once, and no comeback is counted since no change answers it.
                Fire(plan.Repeat(now) ? GuardEvent.RepeatUnknown : GuardEvent.UnknownBack);
            }
            return;
        }

        // Back on. Who did it?
        var tool = live is not null && (PlayerTools.Contains(live) || SchedulerTools.Contains(live) && mechanism == "task") ? live : null;
        // A machine-wide item's off-change needs Windows' permission, which the guard never asks for: unless the player's
        // own tool was in front, it is left on and said once, and the player's own Remove (which may ask) turns it off.
        if (StartupEntries.MachineWide(removal.Id))
        {
            if (tool is not null && PlayerTools.Contains(tool))
            {
                logs.Add($"Startup: {back.Name} turned back on in {ToolName(tool)}; left on.");
                Fire(GuardEvent.PlayerTurnedOn);
            }
            else Fire(GuardEvent.MachineBack);
            return;
        }
        var visible = mechanism is "run:cu" or "run:lm" or "run32:lm" or "run32:cu" or "folder:cu" or "folder:lm" or "package";
        var rewritten = mechanism.StartsWith("run", StringComparison.Ordinal) && removal.Value is { } was && back.Command != was;
        var byProgram = renamed is not null || rewritten
            || !visible && tool is null                       // a task, service or RunOnce value: Task Manager and Settings don't show them
            || visible && tool is null && live is not null;   // seen at the moment of change, with no tool of the player's in front
        if (tool is not null)
        {
            logs.Add($"Startup: {back.Name} turned back on in {ToolName(tool)}; left on.");
            Fire(GuardEvent.PlayerTurnedOn);
            return;
        }
        if (!byProgram)
        {
            // Nobody was seen: off again once, silently; a second time within 30 days is left on and Exo stops.
            var again = plan.Repeat(now);
            Fire(again ? GuardEvent.RepeatUnknown : GuardEvent.UnknownBack, back,
                again ? null : $"Startup: {back.Name} turned itself back on ({MechanismName(mechanism)}); off again.");
            return;
        }

        var what = renamed is not null ? $"made again as {back.Id}" : rewritten ? "its entry was rewritten" : mechanism == "task" ? "task enabled" : "StartupApproved flipped on";
        var repeats = plan.Repeat(now);
        // Updater and anti-cheat items are kept off (or left alone) whatever the count; the others use the program's own switch the second time.
        var ev = item.State switch
        {
            GuardState.Switched or GuardState.Marked or GuardState.Updater or GuardState.AntiCheat => GuardEvent.ProgramBack,
            _ when !repeats => GuardEvent.ProgramBack,
            // No process named: Exo can't tell whether the program runs, so the switch is never used and the item is marked.
            _ when route is { Kind: "jsonSetting" } rs => rs.Processes.Count == 0 ? GuardEvent.RepeatNothing
                : Running(rs) ? GuardEvent.SwitchBlocked : GuardEvent.RepeatSwitch,
            _ when route is { Kind: "updaterService" } => GuardEvent.RepeatUpdater,
            _ when route is { Kind: "antiCheat" } => GuardEvent.RepeatAntiCheat,
            _ => GuardEvent.RepeatNothing,
        };
        if (item.State == GuardState.Off && repeats) plan.MovedTo(GuardState.Back);
        var line = $"Startup: {back.Name} turned itself back on ({MechanismName(mechanism)}, {what}); off again.";
        if (Fire(ev, back, line) && ev == GuardEvent.SwitchBlocked) item.SwitchPending = true;

        bool Fire(GuardEvent e, StartupEntry? target = null, string? message = null)
        {
            // No row: nothing happens, and nothing is counted. A comeback counts only when a change answers it.
            if (Step(item.State, e) is not { } row) return false;
            // The switch is wanted, and none could be set up in Switch (its file is on a network drive, its route is unusable,
            // or it could not be read three times): the item is kept as one with no switch, and marked.
            if (e is GuardEvent.RepeatSwitch or GuardEvent.SwitchReady && route is null) plan.SwitchWanted = true;
            // A comeback counts only where a change answers it: a row that only logs it changes nothing, so nothing counts.
            if (e is not (GuardEvent.PlayerTurnedOn or GuardEvent.RunsUpdater or GuardEvent.RunsAntiCheat or GuardEvent.PutBack)
                && row.Action is not (GuardAction.LogOnly or GuardAction.None)) plan.Counted(now);
            item.State = row.Next;
            plan.MovedTo(row.Next);
            switch (row.Action)
            {
                case GuardAction.PutOffAgain or GuardAction.PutOffAndSwitch:
                    if (target is not null && StartupEntries.OffChanges(_host, target.Id) is { } off) changes.AddRange(off);
                    if (message is not null) logs.Add(message);
                    if (row.Action == GuardAction.PutOffAndSwitch && route is not null && !plan.KeptAsIs) Switch(plan, route, logs);
                    // The setting could not be read this moment (the file is being rewritten): the item keeps the state it
                    // had, its start-up entry is still put off again below, and no comeback is counted for this one: the next
                    // comeback tries the switch again.
                    if (plan.KeptAsIs) Back();
                    break;
                case GuardAction.ApplySwitch:
                    if (route is not null && !plan.KeptAsIs) Switch(plan, route, logs);
                    if (plan.KeptAsIs) { item.SwitchPending = true; Back(); } else item.SwitchPending = false;
                    break;
                case GuardAction.UndoSwitchAndDrop:
                    UndoSwitch(item, logs);
                    after.Add(() => DropRemoval(removal.Id));
                    break;
                case GuardAction.DropRemoval:
                    after.Add(() => DropRemoval(removal.Id));
                    break;
                case GuardAction.LogOnly:
                    if (!item.Told) logs.Add(item.State switch
                    {
                        // The program's own setting is off and its entry is on again: the row says so, and Exo changes nothing.
                        GuardState.Switched => $"Startup: {removal.Name} is in startup again; Exo left it, its own settings are already off.",
                        // Machine-wide and back on: Exo never asks Windows for permission, so turning it off again is the player's press.
                        GuardState.NeedsOk => $"Startup: {removal.Name} came back; it is machine-wide, so Exo left it for you to turn it off again.",
                        // Windows won't let the user make this change without its permission: it stays in its state, said once.
                        _ when e == GuardEvent.MachineBack => ThisCheckWaits(plan, row),
                        GuardState.Marked or GuardState.Updater or GuardState.AntiCheat => e == GuardEvent.RepeatUnknown
                            ? $"Startup: {removal.Name} came back twice and Exo couldn't tell who turned it on; left on."
                            : $"Startup: {removal.Name} keeps turning itself back on; Exo has stopped turning it off.",
                        _ when e == GuardEvent.RepeatUnknown => $"Startup: {removal.Name} came back twice and Exo couldn't tell who turned it on; left on.",
                        _ => $"Startup: {removal.Name} keeps turning itself back on; Exo has stopped turning it off.",
                    });
                    item.Told = true;
                    plan.Told();
                    break;
            }
            // The switch could not be read this moment: the item keeps the state it had, and nothing of this comeback counts.
            void Back()
            {
                item.State = plan.Before.State;
                plan.MovedTo(plan.Before.State);
                plan.Uncounted();
            }

            // Into Marked: Exo says once that it has stopped.
            if (row.Action == GuardAction.PutOffAgain && item.State == GuardState.Marked && !item.Told)
            {
                logs.Add($"Startup: {removal.Name} keeps turning itself back on; Exo has stopped turning it off.");
                item.Told = true;
                plan.Told();
            }
            return true;
        }
    }

    private void DropRemoval(string id)
    {
        var all = StartupEntries.Kept(_host);
        StartupEntries.Drop(_host, all.Where(item => item.Id == id));
    }

    // The whole kind: the player's own Run value ("run:cu") is not the machine-wide twin ("run:lm") of the same command.
    private static bool SameKind(string a, string b) => StartupEntries.Split(a).Kind == StartupEntries.Split(b).Kind;

    private static string MechanismName(string mechanism) => mechanism switch
    {
        "task" => "scheduled task",
        "package" => "Store app's task",
        "folder:cu" or "folder:lm" => "Startup folder",
        "service" => "service",
        _ when mechanism.StartsWith("runonce", StringComparison.Ordinal) => "RunOnce",
        _ => "Run",
    };

    private bool ProgramRuns(string? program)
    {
        if (program is null) return false;
        var name = Path.GetFileNameWithoutExtension(program);
        return name.Length > 0 && _host.Running(name).Any(running => running.Path is null || string.Equals(running.Path, program, StringComparison.OrdinalIgnoreCase));
    }

    // No process named: Exo can't tell whether the program runs, so the switch is never used (the item is marked instead).
    private bool Running(RealSwitch route) => route.Processes.Count > 0 && route.Processes.Any(process => _host.Running(process).Count > 0);

    private static RealSwitch? RouteOf(StartupEntries.Removal removal, string? program)
    {
        var names = program is null ? [] : new List<string> { Path.GetFileName(program).ToLowerInvariant() };
        names.AddRange(StartupApps.ExecutableNames(removal.Value));
        return StartupCatalog.Find(removal.Name, names, program ?? removal.Value)?.RealSwitch;
    }

    // ---- The real switch: a setting of the program's own ----

    /// <summary>
    /// Decides the switch and reads what the setting holds now: nothing of the program's is written here. The write is
    /// <see cref="WriteSwitch"/> in <see cref="Commit"/>, once the off-change held.
    /// </summary>
    private void Switch(Plan plan, RealSwitch route, List<string> logs)
    {
        var name = plan.Removal.Name;
        // A switch that can't be used: the item is treated as one with no switch at all, and marked rather than retried.
        if (route.Kind != "jsonSetting" || route.File is null || route.Path is null || route.Off is null) { plan.SwitchWanted = true; return; }
        var file = Environment.ExpandEnvironmentVariables(route.File);
        // A settings file on a network share or a removable drive is never written: reading it can wait for as long as the
        // share takes, and the guard holds its lock while it does. The item is treated as having no switch, and marked.
        if (!_host.SettingsDriveFixed(file)) { plan.SwitchWanted = true; return; }
        try
        {
            if (_host.ReadFile(file) is not { } text || JsonNode.Parse(text) is not JsonObject root) { UnreadableForNow(plan, logs, "it could not be read"); return; }
            var parent = root;
            var parts = route.Path.Split('.');
            foreach (var part in parts[..^1]) parent = parent[part] as JsonObject ?? throw new InvalidOperationException("setting path");
            var key = parts[^1];
            plan.SwitchToSet = route;
            // The file's exact bytes, so Put back can put it back byte for byte when nothing else has changed it since.
            plan.SwitchWas = (parent[key]?.ToJsonString(), OffValue(route.Off)?.ToJsonString(), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            UnreadableForNow(plan, logs, ex.Message);
        }
    }

    /// <summary>
    /// The program's own setting could not be read this moment: it is being rewritten, or locked, or its folder isn't there
    /// yet. That is not a switch that can never be used: the item keeps the state it had (the comeback of this check is still
    /// answered by turning its start-up entry off again, as a first comeback is), and the next comeback tries the switch
    /// again. After the third such failure the item is marked, so a setting that never reads cannot loop for ever.
    /// </summary>
    private void UnreadableForNow(Plan plan, List<string> logs, string why)
    {
        plan.Item.SwitchTries++;
        logs.Add($"Startup: the setting for {plan.Removal.Name} could not be read just now ({why}); it will be tried again.");
        if (plan.Item.SwitchTries >= SwitchTries)
        {
            plan.SwitchWanted = true;
            logs.Add($"Startup: the setting for {plan.Removal.Name} still could not be read; Exo has stopped trying.");
            return;
        }
        plan.KeptAsIs = true;
    }

    /// <summary>How many reads of a program's own setting may fail this moment before the item is marked.</summary>
    private const int SwitchTries = 3;

    /// <summary>
    /// The setting could not be read or parsed just now, at the write: nothing of the program's is written, the item keeps the
    /// state it had, and the next comeback tries again. The third such failure marks it (the start-up entry stays off).
    /// </summary>
    private SwitchDone TryAgain(Plan plan, List<string> logs, string why)
    {
        UnreadableForNow(plan, logs, why);
        return plan.KeptAsIs ? SwitchDone.TryAgain : SwitchDone.NoSwitch;
    }

    /// <summary>
    /// Writes the program's own setting, in <see cref="Commit"/> and only after the off-change held, in this order: (1) the
    /// program is not running now; (2) the file is on a local fixed drive; (3) the intent (the file, its exact bytes before,
    /// the value before) goes into the item and the record is saved; (4) the file is written; (5) what was written is
    /// recorded and saved again. What it came to, so the item is left as the answer says.
    /// </summary>
    private SwitchDone WriteSwitch(Record record, Plan plan, RealSwitch route, List<string> logs)
    {
        var name = plan.Removal.Name;
        if (plan.SwitchWas is not { } was || route.File is null || route.Path is null) return SwitchDone.NoSwitch;
        var file = Environment.ExpandEnvironmentVariables(route.File);
        // (1) Running now: the program would write its own settings again as Exo writes them. Left as a held switch waits.
        if (Running(route))
        {
            logs.Add($"Startup: {name} is running; its own setting is left until it closes.");
            return SwitchDone.Running;
        }
        // (2) Only a local fixed drive: a share is never written.
        if (!_host.SettingsDriveFixed(file))
        {
            logs.Add($"Startup: the setting for {name} is on a network drive; left as it is.");
            return SwitchDone.NoSwitch;
        }
        var item = plan.Item;
        string text;
        JsonObject root;
        // Reading the file as it is now: a missing, locked or half-written one is tried again at the next comeback, never given up on here.
        try
        {
            if (_host.ReadFile(file) is not { } read || JsonNode.Parse(read) is not JsonObject parsed) return TryAgain(plan, logs, "it could not be read");
            (text, root) = (read, parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return TryAgain(plan, logs, ex.Message); }
        try
        {
            var parent = root;
            var parts = route.Path.Split('.');
            foreach (var part in parts[..^1]) parent = parent[part] as JsonObject ?? throw new InvalidOperationException("setting path");
            var key = parts[^1];
            var off = OffValue(route.Off ?? "");
            parent[key] = off;
            var written = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // (3) The intent is saved before the file is touched, so a check that stops between the file write and the save
            // is still undone by Put back from the state alone.
            item.SwitchFile = file;
            item.SwitchPath = route.Path;
            item.SwitchBefore = was.Before;
            item.SwitchFileBefore = text;
            item.SwitchSet = off?.ToJsonString();
            item.SwitchFileWritten = null;
            if (!SaveIntent(record, item, logs)) return SwitchDone.NotSaved;

            // (4) The file is written.
            _host.WriteFile(file, written);

            // (5) What was written is recorded, and saved again.
            item.SwitchFileWritten = written;
            return SaveIntent(record, item, logs) ? SwitchDone.Written : SwitchDone.Broken;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logs.Add($"Startup: the setting for {name} could not be changed: {ex.Message}");
            return SwitchDone.Broken;
        }
    }

    /// <summary>
    /// What came of the switch: <see cref="Written"/>; <see cref="Running"/> (the program runs, so nothing was written, and
    /// the switch waits); <see cref="NoSwitch"/> (the file is on a network drive, or the route has no off value: the item is
    /// treated as having no switch); <see cref="TryAgain"/> (the file could not be read or parsed this moment: nothing was
    /// written, the item keeps its state, and the next comeback tries again); <see cref="NotSaved"/> (nothing was written, and
    /// nothing is left to undo); or <see cref="Broken"/> (the intent is in the record, so Put back can undo it: Exo stops trying).
    /// </summary>
    private enum SwitchDone { Written, Running, NoSwitch, TryAgain, NotSaved, Broken }

    /// <summary>Saves the record as it is now (the intent for the switch is already in the item). False when it can't be saved.</summary>
    private bool SaveIntent(Record record, Item item, List<string> logs)
    {
        try { Save(_host, record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logs.Add($"Startup: {item.Id}'s own setting could not be recorded: {ex.Message}");
            return false;
        }
        return true;
    }

    private static JsonNode? OffValue(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    private static void UndoSwitch(IStartupHost host, Item item, List<string>? logs)
    {
        if (item.SwitchFile is not { } file || item.SwitchPath is not { } path) return;
        try
        {
            if (host.ReadFile(file) is not { } text || JsonNode.Parse(text) is not JsonObject root) return;
            // The record was saved with the intent before the file was written: the file may still be exactly as it was,
            // and then there is nothing to undo.
            if (item.SwitchFileBefore is { } before && text == before) { ForgetSwitch(item); return; }
            // Nothing else touched the file since Exo wrote it: its exact bytes go back, other formatting and all.
            if (item.SwitchFileWritten is { } written && text == written)
            {
                host.WriteFile(file, item.SwitchFileBefore!);
                logs?.Add($"Startup: put {path} back as it was.");
                ForgetSwitch(item);
                return;
            }
            var parent = root;
            var parts = path.Split('.');
            foreach (var part in parts[..^1]) parent = parent[part] as JsonObject ?? throw new InvalidOperationException("setting path");
            var key = parts[^1];
            // Only what Exo set goes back: a value the program or the player has changed since is theirs.
            if (parent[key]?.ToJsonString() != item.SwitchSet) return;
            if (item.SwitchBefore is null) parent.Remove(key);
            else parent[key] = JsonNode.Parse(item.SwitchBefore);
            host.WriteFile(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            logs?.Add($"Startup: put {path} back as it was.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        ForgetSwitch(item);
    }

    private static void ForgetSwitch(Item item)
    {
        item.SwitchFile = null;
        item.SwitchFileBefore = null;
        item.SwitchFileWritten = null;
    }

    private void UndoSwitch(Item item, List<string> logs) => UndoSwitch(_host, item, logs);

    // ---- What Remove, Put back and the page tell the guard ----

    /// <summary>The player turned something off in Exo: from now on it is guarded.</summary>
    internal static void OnRemoved(IStartupHost host, StartupEntries.Removal removal)
    {
        var record = Load(host);
        var item = record.Items.FirstOrDefault(known => known.Id == removal.Id);
        var state = item?.State ?? GuardState.Untracked;
        if (Step(state, GuardEvent.PlayerTurnedOff) is not { Action: GuardAction.RecordOff } row) return;
        record.Items.RemoveAll(known => known.Id == removal.Id);
        // A real switch an earlier removal of it set stays with its own record until put back; a fresh removal starts clean.
        // The record's first sight of it comes with the removal's time; a later one (the player pressing Remove again on an
        // item that came back) keeps when it was first removed.
        record.Items.Add(new Item
        {
            Id = removal.Id, Fingerprint = removal.Fingerprint, Mechanism = StartupEntries.Split(removal.Id).Kind, State = row.Next,
            Since = item is null ? removal.At : item.Since, Seen = removal.Value ?? item?.Seen, LockedHandled = true,
        });
        Save(host, record);
        Notify(host);
    }

    /// <summary>Put back: the real switch goes back as it was and the guard stops watching the item.</summary>
    internal static void OnPutBack(IStartupHost host, StartupEntries.Removal removal)
    {
        var record = Load(host);
        if (record.Items.FirstOrDefault(known => known.Id == removal.Id) is { } item)
        {
            if (Step(item.State, GuardEvent.PutBack) is { Action: GuardAction.UndoSwitchAndForget }) UndoSwitch(host, item, null);
            else if (item.SwitchFile is not null) UndoSwitch(host, item, null);
            record.Items.Remove(item);
            Save(host, record);
        }
        Notify(host);
    }

    /// <summary>The removal is gone for good (its program is uninstalled): nothing is guarded.</summary>
    internal static void OnForgotten(IStartupHost host, string id)
    {
        var record = Load(host);
        if (record.Items.RemoveAll(known => known.Id == id) > 0) Save(host, record);
        Notify(host);
    }

    public void Dispose()
    {
        lock (Live) Live.Remove(this);
        lock (_gate)
        {
            _disposed = true;
            _watch?.Dispose();
            _watch = null;
            _timer?.Dispose();
            _timer = null;
            _later?.Dispose();
            _later = null;
        }
        // A check already under way finishes before this returns: two bounded task reads of five seconds, and its writes.
        _idle.Wait(TimeSpan.FromSeconds(15));
        if (ReferenceEquals(Shared, this)) Shared = null;
    }
}
