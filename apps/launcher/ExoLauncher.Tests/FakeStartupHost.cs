using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// All of what start-up touches, in memory: the registry, scheduled tasks, Startup folders, Store tasks, files, programs
/// that run, signatures, security products, the window in front and the watch. Nothing here reaches this PC's own.
/// </summary>
internal sealed class FakeStartupHost : IStartupHost
{
    internal sealed class FakeTask
    {
        public string Path = "";
        public bool Enabled = true;
        public string Program = "";
        public string? Arguments;
        public string Stamp = "1";
    }

    private readonly Dictionary<(Hive, string, string), RegValue> _registry = new();
    internal List<FakeTask> TaskList { get; } = [];
    internal List<FolderFile> UserFolder { get; } = [];
    internal List<FolderFile> CommonFolder { get; } = [];
    internal List<PackageTaskDecl> Packages { get; } = [];
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<RunningProgram>> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> Signers { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<string> Security { get; } = [];
    internal Dictionary<string, int> FileWrites { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal string? ForegroundProgram { get; set; }
    internal DateTime Now { get; set; } = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>Windows' prompt is declined for what needs it.</summary>
    internal bool DeclineMachine { get; set; }
    /// <summary>Every change handed over, in order, one entry per Apply call.</summary>
    internal List<IReadOnlyList<StartupChange>> Applied { get; } = [];
    /// <summary>Runs inside <see cref="Apply"/>, as Windows' prompt waits: what the player does meanwhile.</summary>
    internal Action? DuringApply { get; set; }
    /// <summary>Runs after every change of one <see cref="Apply"/> was made: a program undoing it at once.</summary>
    internal Action? AfterApply { get; set; }
    internal int ElevatedRequests { get; private set; }
    internal int OpenWatches { get; private set; }
    private readonly List<Action> _watchers = [];

    public string DataFolder { get; } = @"C:\fake-exo-data";
    public DateTime UtcNow => Now;

    // ---- registry ----

    internal void Set(Hive hive, string key, string name, RegValue? value)
    {
        if (value is null) _registry.Remove((hive, key.ToLowerInvariant(), name.ToLowerInvariant()));
        else
        {
            _registry[(hive, key.ToLowerInvariant(), name.ToLowerInvariant())] = value;
            OriginalNames[(hive, key.ToLowerInvariant(), name.ToLowerInvariant())] = name;
        }
    }

    public RegValue? Get(Hive hive, string key, string name) => _registry.GetValueOrDefault((hive, key.ToLowerInvariant(), name.ToLowerInvariant()));

    public IReadOnlyList<string> ValueNames(Hive hive, string key) =>
        [.. _registry.Where(pair => pair.Key.Item1 == hive && pair.Key.Item2 == key.ToLowerInvariant()).Select(pair => pair.Key.Item3)
            .Select(lower => OriginalNames.GetValueOrDefault((hive, key.ToLowerInvariant(), lower), lower))];

    private Dictionary<(Hive, string, string), string> OriginalNames { get; } = new();

    internal void SetRun(Hive hive, string key, string name, string command) => Set(hive, key, name, RegValue.Str(command));

    public IReadOnlyList<string> SubKeys(Hive hive, string key)
    {
        var prefix = key.ToLowerInvariant() + "\\";
        return [.. _registry.Keys.Where(k => k.Item1 == hive && k.Item2.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Item2[prefix.Length..].Split('\\')[0]).Distinct().Select(lower => SubKeyNames.GetValueOrDefault((hive, prefix + lower), lower))];
    }

    private Dictionary<(Hive, string), string> SubKeyNames { get; } = new();

    internal void AddService(string name, string image, int start = 2, int type = 0x10, string? display = null)
    {
        var key = StartupKeys.Services + "\\" + name;
        SubKeyNames[(Hive.Machine, (StartupKeys.Services + "\\").ToLowerInvariant() + name.ToLowerInvariant())] = name;
        Set(Hive.Machine, key, "Start", RegValue.DWord(start));
        Set(Hive.Machine, key, "Type", RegValue.DWord(type));
        Set(Hive.Machine, key, "ImagePath", RegValue.Expand(image));
        if (display is not null) Set(Hive.Machine, key, "DisplayName", RegValue.Str(display));
    }

    // ---- changes ----

    /// <summary>Windows refuses these tasks to the user whatever it is asked (an installer made them as administrator).</summary>
    internal HashSet<string> AdminTasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times the task list was asked for, and how long each ask was allowed (null: the 15-second default).</summary>
    internal int TaskReads { get; private set; }
    internal List<TimeSpan?> TaskWaits { get; } = [];

    /// <summary>Counts the task-list reads from now: a test asks what one check costs.</summary>
    internal void ResetTaskReads()
    {
        TaskReads = 0;
        TaskWaits.Clear();
    }

    public ApplyResult Apply(IReadOnlyList<StartupChange> changes, bool elevate = true)
    {
        Applied.Add(changes);
        DuringApply?.Invoke();
        var needsWindows = changes.Any(change => NeedsWindows(change));
        if (needsWindows && !elevate) { ApplyUserOnly(changes); return new ApplyResult(true, "Windows' permission is needed for the rest.") { Left = [.. changes.Where(NeedsWindows)] }; }
        if (needsWindows)
        {
            ElevatedRequests++;
            if (DeclineMachine) { ApplyUserOnly(changes); return new ApplyResult(true, "Windows' permission was declined, so nothing changed."); }
        }
        foreach (var change in changes) One(change);
        AfterApply?.Invoke();
        return ApplyResult.Done;
    }

    private bool NeedsWindows(StartupChange change) => change switch
    {
        SetReg { Hive: Hive.Machine } => true,
        // `schtasks /change` as the user fails for a task an installer made with administrator rights.
        SetTask task => AdminTasks.Contains(task.Path),
        _ => false,
    };

    private void ApplyUserOnly(IReadOnlyList<StartupChange> changes)
    {
        foreach (var change in changes) if (!NeedsWindows(change)) One(change);
        AfterApply?.Invoke();
    }

    private void One(StartupChange change)
    {
        switch (change)
        {
            case SetReg set: Set(set.Hive, set.Key, set.Name, set.Value); break;
            case SetTask task: if (TaskList.FirstOrDefault(t => t.Path.Equals(task.Path, StringComparison.OrdinalIgnoreCase)) is { } found) found.Enabled = task.On; break;
            case DeleteTask task: TaskList.RemoveAll(t => t.Path.Equals(task.Path, StringComparison.OrdinalIgnoreCase)); break;
            case CreateTask task:
                var text = System.Text.Encoding.UTF8.GetString(Files.GetValueOrDefault(task.XmlFile) ?? []);
                var parts = text.Split('|');
                TaskList.Add(new FakeTask { Path = task.Path, Program = parts[0], Arguments = parts.Length > 1 ? parts[1] : null, Enabled = true });
                break;
            case MoveFile move:
                if (Files.Remove(move.From, out var bytes)) Files[move.To] = bytes;
                break;
        }
    }

    /// <summary>Another program writes a task as `schtasks /Create /F` does: a new, enabled one in the old one's place.</summary>
    internal void ReCreateTask(string path, bool enabled = true)
    {
        var old = TaskList.First(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        TaskList.Remove(old);
        TaskList.Add(new FakeTask { Path = path, Program = old.Program, Arguments = old.Arguments, Enabled = enabled, Stamp = old.Stamp + "x" });
    }

    internal FakeTask AddTask(string path, string program, string? arguments = null, bool enabled = true)
    {
        var task = new FakeTask { Path = path, Program = program, Arguments = arguments, Enabled = enabled };
        TaskList.Add(task);
        return task;
    }

    public IReadOnlyList<TaskInfo> Tasks(TimeSpan? wait = null)
    {
        TaskReads++;
        TaskWaits.Add(wait);
        return [.. TaskList.Select(t => new TaskInfo(t.Path, t.Enabled, t.Program, t.Arguments, t.Stamp))];
    }

    public byte[]? TaskXml(string path) => TaskList.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } t
        ? System.Text.Encoding.UTF8.GetBytes(t.Program + "|" + t.Arguments) : null;

    public IReadOnlyList<FolderFile> FolderFiles(bool machine) => machine ? CommonFolder : UserFolder;
    public IReadOnlyList<PackageTaskDecl> PackageTasks() => Packages;
    public string? Foreground() => ForegroundProgram;

    public IReadOnlyList<RunningProgram> Running(string processName)
    {
        Asked(processName);
        return Processes.GetValueOrDefault(processName) ?? [];
    }

    /// <summary>Runs on every <see cref="Running"/> question: a test can make the program start just before its switch is written.</summary>
    internal Action<string>? BeforeRunning { get; set; }

    private void Asked(string processName) => BeforeRunning?.Invoke(processName);

    internal HashSet<string> MissingPrograms { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, ProgramInfo> Infos { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProgramInfo Info(string path) => Infos.TryGetValue(path, out var info) ? info : new ProgramInfo(!MissingPrograms.Contains(path), null, null);

    public string? Signer(string path) => Signers.GetValueOrDefault(path);

    /// <summary>Whether this PC's security software could be listed; false as a failed read leaves it.</summary>
    internal bool SecurityKnown { get; set; } = true;
    public bool SecurityKnownNow => SecurityKnown;
    bool IStartupHost.SecurityKnown => SecurityKnown;

    public IReadOnlyList<string> SecurityPrograms() => Security;

    // ---- files ----

    /// <summary>Files whose read fails as a locked or unreadable file does (an <see cref="IOException"/>).</summary>
    internal HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose write fails as a locked or full disk does (an <see cref="IOException"/>), and how many more times.</summary>
    internal Dictionary<string, int> Unwritable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ReadFile(string path)
    {
        if (Unreadable.Contains(path)) throw new IOException($"The file {path} is in use.");
        return Files.TryGetValue(path, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    public void WriteFile(string path, string text)
    {
        if (Unwritable.TryGetValue(path, out var left) && left != 0)
        {
            if (left > 0) Unwritable[path] = left - 1;
            throw new IOException($"The file {path} is in use.");
        }
        Files[path] = System.Text.Encoding.UTF8.GetBytes(text);
        FileWrites[path] = FileWrites.GetValueOrDefault(path) + 1;
    }

    public byte[]? ReadBytes(string path) => Files.GetValueOrDefault(path);
    public void WriteBytes(string path, byte[] bytes) => Files[path] = bytes;
    public void DeleteFile(string path) => Files.Remove(path);
    public bool FileExists(string path) => Files.ContainsKey(path);

    /// <summary>Whether a file's drive is a local fixed one: on by default; a test answers false for a network share.</summary>
    internal Func<string, bool> DriveFixed { get; set; } = _ => true;
    public bool SettingsDriveFixed(string path) => DriveFixed(path);

    // ---- the watch ----

    public IDisposable Watch(Action changed)
    {
        OpenWatches++;
        _watchers.Add(changed);
        return new Closer(this, changed);
    }

    private sealed class Closer(FakeStartupHost host, Action changed) : IDisposable
    {
        private bool _closed;
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            host.OpenWatches--;
            host._watchers.Remove(changed);
        }
    }

    /// <summary>Windows says start-up changed (what RegNotifyChangeKeyValue or the folder watcher does).</summary>
    internal void Raise() { foreach (var watcher in _watchers.ToList()) watcher(); }

    // ---- a few ways to read it ----

    internal static byte[] Off(DateTime at) => StartupApps.Approved(false, at);
    internal static byte[] On() => StartupApps.Approved(true, DateTime.UtcNow);

    internal byte[]? Approval(Hive hive, string key, string name) => Get(hive, StartupKeys.Approved + key, name)?.Bytes;

    internal static string Expand(string text) => Environment.ExpandEnvironmentVariables(text);
}
