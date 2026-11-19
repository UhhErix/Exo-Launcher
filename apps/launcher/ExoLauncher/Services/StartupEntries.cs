using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>Where a start-up entry lives: a Run value, a Startup folder's file, a scheduled task run at sign-in, a Store app's start-up task, a RunOnce value, a policy's Run value, Winlogon, Active Setup, or a service.</summary>
internal enum StartupSource { Run, Folder, Task, Package, RunOnce, Policy, Winlogon, ActiveSetup, Service }

/// <summary>
/// One thing Windows starts when you sign in. Id names where it lives: "run:cu:Name", "run:lm:Name", "run32:lm:Name",
/// "run32:cu:Name", "runonce:cu:Name" (and lm, runonce32), "folder:cu:File.lnk", "folder:lm:File.lnk", "task:\Path\Name",
/// "package:Family!TaskId", "service:Name", "policy:cu:Name", "active:lm:Guid", "winlogon:lm:Shell". Machine: removing it
/// needs Windows' permission. Program: the program it starts when one can be named (null for a value that isn't a command).
/// </summary>
internal sealed record StartupEntry(string Id, StartupSource Source, bool Machine, string Name, string Command, string? Program, bool Enabled)
{
    public string? Publisher { get; init; }
    public StartupCatalog.Item? Known { get; init; }
    /// <summary>The program it names is gone: nothing starts from it.</summary>
    public bool Missing { get; init; }
    /// <summary>Why Exo leaves it in start-up (<see cref="StartupLocks"/>): listed, never turned off.</summary>
    public LockKind Lock { get; init; }
    public bool Locked => Lock != LockKind.None;
    /// <summary>Listed so the player sees it; Exo turns off only what the catalog names.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>The program path plus its arguments: the same item under another name matches by it.</summary>
    public string Fingerprint { get; init; } = "";
    /// <summary>What the guard knows of it (<see cref="GuardState"/>), and the one plain sentence to show on its row; null when there is none.</summary>
    public GuardState Guard { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// PC › Apps › Startup: everything that starts when you sign in, from every place Windows starts things (the Run and
/// RunOnce keys of both hives and both views, the Startup folders, Store apps' startup tasks, scheduled tasks run at
/// sign-in outside Windows' own, and services the catalog names), each with whether it starts now. Remove takes it out of
/// startup the way Task Manager and Settings do, and is undone exactly by Put back: a Run value or a Startup folder's
/// shortcut stays where it is and StartupApproved is marked off; a scheduled task is turned off (an app's updater would
/// only make a deleted one again), or deleted when its program is gone; a Store app's task is turned off by the user, which
/// the app can't undo; a RunOnce value is taken out and kept. What it was is kept in startup-removed.json, and it leaves the
/// list for Removed from startup. <see cref="StartupGuard"/> watches what was removed and puts it off again when its program
/// turns it back on. Machine-wide entries ask Windows once. Windows' own, security software, anti-cheat, drivers, input and
/// audio, Exo and WebView2's updater are listed and never removed (<see cref="StartupLocks"/>). Riot Vanguard's tray icon
/// (vgtray) can be removed and put back; the driver and service are not touched.
/// </summary>
internal static partial class StartupEntries
{
    private static readonly AsyncLocal<IStartupHost?> s_host = new();
    private static readonly RealStartupHost s_real = new();

    /// <summary>This PC's by default; a test swaps in a fake of all of it for its own async flow.</summary>
    internal static IStartupHost Host => s_host.Value ?? s_real;

    internal static IDisposable UseHost(IStartupHost host)
    {
        var previous = s_host.Value;
        s_host.Value = host;
        return new HostScope(previous);
    }

    private sealed class HostScope(IStartupHost? previous) : IDisposable
    {
        public void Dispose() => s_host.Value = previous;
    }

    private static readonly AsyncLocal<IReadOnlyList<TaskInfo>?> s_tasks = new();

    /// <summary>
    /// The scheduled task list one operation already read, or null: a guard check reads it once (each `schtasks` is up to
    /// five seconds, and the check holds the player's own press off while it runs), and everything inside that check sees
    /// the same list. Never set, a caller reads it itself.
    /// </summary>
    internal static IReadOnlyList<TaskInfo>? TaskList => s_tasks.Value;

    internal static IDisposable UseTaskList(IReadOnlyList<TaskInfo>? tasks)
    {
        var previous = s_tasks.Value;
        s_tasks.Value = tasks;
        return new TaskScope(previous);
    }

    private sealed class TaskScope(IReadOnlyList<TaskInfo>? previous) : IDisposable
    {
        public void Dispose() => s_tasks.Value = previous;
    }

    /// <summary>The guard's own wait for a task list it has to read: a check gives each read five seconds, not fifteen.</summary>
    internal static readonly TimeSpan GuardWait = TimeSpan.FromSeconds(5);

    /// <summary>The task list as it is now. <paramref name="wait"/> is how long a read may take (null: fifteen seconds, the player's own press).</summary>
    internal static IReadOnlyList<TaskInfo> Tasks(IStartupHost host, TimeSpan? wait = null) => TaskList ?? host.Tasks(wait);

    /// <summary>The task list read afresh, whatever an outer operation read: a check's own changes must be seen.</summary>
    internal static IReadOnlyList<TaskInfo> ReadTasks(IStartupHost host, TimeSpan? wait = null) => host.Tasks(wait);

    /// <summary>A path cmd reads as it is: no quote, variable, escape, pipe or redirect.</summary>
    internal static bool CmdSafe(string text) => text.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!', '\r', '\n']) < 0;

    // ---- Reading ----

    private sealed record RunPlace(Hive Hive, string Key, string Prefix, string? ApprovedKey, StartupSource Source, bool ReadOnly = false);

    private static readonly RunPlace[] RunPlaces =
    [
        new(Hive.User, StartupKeys.Run, "run:cu:", "Run", StartupSource.Run),
        new(Hive.Machine, StartupKeys.Run, "run:lm:", "Run", StartupSource.Run),
        new(Hive.Machine, StartupKeys.Run32, "run32:lm:", "Run32", StartupSource.Run),
        new(Hive.User, StartupKeys.Run32, "run32:cu:", "Run32", StartupSource.Run),
        new(Hive.User, StartupKeys.RunOnce, "runonce:cu:", null, StartupSource.RunOnce),
        // A machine-wide RunOnce value is a one-time step an installer left to finish: removing it deletes it, and the
        // uninstaller can't put it back. Shown, like services, never removed.
        new(Hive.Machine, StartupKeys.RunOnce, "runonce:lm:", null, StartupSource.RunOnce, ReadOnly: true),
        new(Hive.Machine, StartupKeys.RunOnce32, "runonce32:lm:", null, StartupSource.RunOnce, ReadOnly: true),
        new(Hive.User, StartupKeys.RunOnce32, "runonce32:cu:", null, StartupSource.RunOnce),
        new(Hive.User, StartupKeys.PolicyRun, "policy:cu:", null, StartupSource.Policy, ReadOnly: true),
        new(Hive.Machine, StartupKeys.PolicyRun, "policy:lm:", null, StartupSource.Policy, ReadOnly: true),
    ];

    /// <summary>What the list shows: every entry that starts, those that start first, and what Exo removed that still runs another way or is the guard's to show.</summary>
    internal static IReadOnlyList<StartupEntry> Read() => Read(Host);

    internal static IReadOnlyList<StartupEntry> Read(IStartupHost host)
    {
        var removed = Kept(host);
        var guard = StartupGuard.Load(host);
        var list = ReadAll(host).Select(entry => WithGuard(entry, removed, guard)).ToList();
        // What Exo removed is under Removed from startup, not here, unless its program turned it back on or runs another way
        // (an updater service, Riot Vanguard). What an uninstalled app left (its program gone) shows until it's deleted for good.
        return [.. list.Where(entry => entry.Enabled || entry.Missing || entry.Guard is GuardState.Updater or GuardState.AntiCheat || removed.All(item => item.Id != entry.Id))
            .OrderByDescending(entry => entry.Enabled).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static StartupEntry WithGuard(StartupEntry entry, IReadOnlyList<Removal> removed, StartupGuard.Record guard)
    {
        var item = guard.Items.FirstOrDefault(known => known.Id == entry.Id);
        if (item is null || removed.All(r => r.Id != entry.Id) && item.State != GuardState.PlayerOn) return entry;
        var status = StartupGuard.StatusOf(item, entry.Known);
        // Riot Vanguard starts its own tray with the game: the row says so, as on.
        return entry with { Guard = item.State, Status = status, Enabled = entry.Enabled || item.State == GuardState.AntiCheat };
    }

    /// <summary>Every entry, on or off, read from the places Windows starts programs.</summary>
    internal static IReadOnlyList<StartupEntry> ReadAll(IStartupHost host)
    {
        var list = new List<StartupEntry>();
        foreach (var place in RunPlaces) list.AddRange(RunEntries(host, place));
        list.AddRange(FolderEntries(host, machine: false));
        list.AddRange(FolderEntries(host, machine: true));
        try { list.AddRange(TaskEntries(host)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Xml.XmlException) { AppLog.Warn("Startup: scheduled tasks not read: " + ex.Message); }
        list.AddRange(PackageEntries(host));
        list.AddRange(ServiceEntries(host));
        list.AddRange(ActiveSetupEntries(host));
        list.AddRange(WinlogonEntries(host));
        return list;
    }

    private static IEnumerable<StartupEntry> RunEntries(IStartupHost host, RunPlace place)
    {
        foreach (var name in host.ValueNames(place.Hive, place.Key))
        {
            if (host.Get(place.Hive, place.Key, name) is not { Text: { } command, Kind: RegistryValueKind.String or RegistryValueKind.ExpandString }) continue;
            var on = place.ApprovedKey is null || StartupApps.IsEnabled(host.Get(place.Hive, StartupKeys.Approved + place.ApprovedKey, name)?.Bytes);
            yield return Describe(host, place.Prefix + name, place.Source, place.Hive == Hive.Machine, name, command, ProgramOf(command), on) with
            {
                ReadOnly = place.ReadOnly,
                Fingerprint = StartupFingerprint.OfCommand(command),
            };
        }
    }

    private static IEnumerable<StartupEntry> FolderEntries(IStartupHost host, bool machine)
    {
        var hive = machine ? Hive.Machine : Hive.User;
        var prefix = machine ? "folder:lm:" : "folder:cu:";
        foreach (var file in host.FolderFiles(machine))
        {
            if (file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var on = StartupApps.IsEnabled(host.Get(hive, StartupKeys.Approved + "StartupFolder", file.Name)?.Bytes);
            // Named by the file, not by the program it runs: a shortcut to PowerShell is not "Windows PowerShell".
            yield return Describe(host, prefix + file.Name, StartupSource.Folder, machine, Path.GetFileNameWithoutExtension(file.Name), file.Target ?? file.Name, file.Target, on)
                with { Fingerprint = StartupFingerprint.OfCommand(file.Target ?? file.Name) };
        }
    }

    /// <summary>
    /// Windows' own tasks that only send Microsoft data about this PC or nag (compatibility and usage telemetry, the
    /// Customer Experience Improvement Program, feedback prompts, map updates), each safe to turn off: Clean up's extras,
    /// never listed. Every other task under \Microsoft\ is Windows' business and left alone.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Does)> WindowsTasks = new(StringComparer.OrdinalIgnoreCase)
    {
        // Compatibility telemetry (winutil, privacy.sexy, Sophia, Optimizer, Win11Debloat). Not PcaPatchDbTask: game fixes.
        [@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"] = ("Compatibility telemetry scan", "Scans installed programs and sends compatibility telemetry to Microsoft."),
        [@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp"] = ("Compatibility telemetry (new)", "Collects program telemetry for Microsoft."),
        [@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"] = ("Program telemetry updater", "Collects program usage telemetry for the Customer Experience Improvement Program."),
        [@"\Microsoft\Windows\Application Experience\AitAgent"] = ("App impact telemetry agent", "Gathers and uploads application impact telemetry."),
        [@"\Microsoft\Windows\Application Experience\StartupAppTask"] = ("Startup apps nag", "Measures startup apps and shows a notice when there are too many."),
        [@"\Microsoft\Windows\Application Experience\MareBackup"] = ("App backup data gathering", "Collects data about installed desktop apps for Microsoft's app backup."),
        // Customer Experience Improvement Program.
        [@"\Microsoft\Windows\Autochk\Proxy"] = ("Disk check telemetry upload", "Collects and uploads disk-check usage data."),
        [@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] = ("CEIP usage data upload", "Collects and sends Windows usage data to Microsoft."),
        [@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"] = ("USB usage telemetry", "Collects USB statistics and sends them to Microsoft."),
        [@"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask"] = ("Kernel usage telemetry", "Collects kernel and system usage data."),
        [@"\Microsoft\Windows\Customer Experience Improvement Program\BthSQM"] = ("Bluetooth usage telemetry", "Collects Bluetooth usage data."),
        [@"\Microsoft\Windows\Customer Experience Improvement Program\Uploader"] = ("CEIP data uploader", "Uploads collected usage data to Microsoft."),
        [@"\Microsoft\Windows\PI\Sqm-Tasks"] = ("Boot security telemetry", "Gathers TPM and Secure Boot information for usage reporting."),
        // Diagnostics collected and sent.
        [@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"] = ("Disk diagnostic telemetry", "Reports disk and system information to Microsoft."),
        [@"\Microsoft\Windows\DiskFootprint\Diagnostics"] = ("Storage use diagnostics", "Records which processes write to storage, for diagnostic data."),
        [@"\Microsoft\Windows\Device Information\Device"] = ("Device info collection", "Collects hardware and device information for Microsoft."),
        [@"\Microsoft\Windows\Device Information\Device User"] = ("Per-user device info", "Collects device information in the signed-in user's session."),
        [@"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem"] = ("Power diagnostics scan", "Analyzes the system for high energy use."),
        [@"\Microsoft\Windows\NetTrace\GatherNetworkInfo"] = ("Network info gathering", "Collects network configuration information for diagnostics."),
        [@"\Microsoft\Windows\Diagnosis\RecommendedTroubleshootingScanner"] = ("Recommended troubleshooting check", "Checks Microsoft for recommended troubleshooters."),
        [@"\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReporting"] = ("Feature usage reporting", "Reports feature usage data to Microsoft."),
        [@"\Microsoft\Windows\Flighting\OneSettings\RefreshCache"] = ("OneSettings config refresh", "Downloads Microsoft's remote settings and experiments for this PC."),
        [@"\Microsoft\Windows\Sustainability\SustainabilityTelemetry"] = ("Sustainability telemetry", "Sends power and sustainability telemetry to Microsoft."),
        [@"\Microsoft\Windows\Sustainability\PowerGridForecastTask"] = ("Power grid forecast", "Downloads power-grid carbon forecasts."),
        [@"\Microsoft\Windows\Windows Error Reporting\QueueReporting"] = ("Queued error report upload", "Sends queued crash and error reports to Microsoft."),
        // Feedback prompts and nags.
        [@"\Microsoft\Windows\Feedback\Siuf\DmClient"] = ("Feedback data client", "Collects data for Feedback Hub and its survey prompts."),
        [@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"] = ("Feedback scenario download", "Downloads scenarios that trigger survey prompts."),
        [@"\Microsoft\Windows\PerformanceTrace\ShowFeedbackToast"] = ("Performance feedback popup", "Shows a pop-up asking for performance feedback."),
        [@"\Microsoft\Windows\PerformanceTrace\WhesvcToast"] = ("Trace capture popup", "Shows a pop-up after a performance trace is captured."),
        [@"\Microsoft\Windows\CloudExperienceHost\CreateObjectTask"] = ("Finish-setup prompts", "Runs the 'finish setting up' prompts."),
        [@"\Microsoft\Windows\AccountHealth\RecoverabilityToastTask"] = ("Account recovery nag", "Shows pop-ups asking you to add Microsoft account recovery info."),
        [@"\Microsoft\Windows\Location\WindowsActionDialog"] = ("Location notice dialog", "Shows location-related notices."),
        // Maps.
        [@"\Microsoft\Windows\Maps\MapsUpdateTask"] = ("Offline maps updater", "Checks for updates to downloaded offline maps."),
        [@"\Microsoft\Windows\Maps\MapsToastTask"] = ("Maps notifications", "Shows notifications from the Maps app."),
        // Family Safety monitoring.
        [@"\Microsoft\Windows\Shell\FamilySafetyMonitor"] = ("Family Safety monitor", "Starts Family Safety monitoring."),
        [@"\Microsoft\Windows\Shell\FamilySafetyRefreshTask"] = ("Family Safety sync", "Syncs Family Safety settings with Microsoft."),
        [@"\Microsoft\Windows\Shell\FamilySafetyRefresh"] = ("Family Safety sync (old)", "Syncs Family Safety settings with Microsoft."),
        [@"\Microsoft\Windows\Shell\FamilySafetyUpload"] = ("Family Safety activity upload", "Uploads activity reports for Family Safety."),
        // Office telemetry (not its updater or license heartbeat).
        [@"\Microsoft\Office\OfficeTelemetryAgentLogOn"] = ("Office telemetry at logon", "Collects Office usage telemetry at sign-in."),
        [@"\Microsoft\Office\OfficeTelemetryAgentFallBack"] = ("Office telemetry fallback", "Collects Office telemetry when the logon task didn't run."),
        [@"\Microsoft\Office\OfficeTelemetryAgentLogOn2016"] = ("Office 2016 telemetry logon", "Collects Office 2016 usage telemetry at sign-in."),
        [@"\Microsoft\Office\OfficeTelemetryAgentFallBack2016"] = ("Office 2016 telemetry fallback", "Collects Office 2016 telemetry when the logon task didn't run."),
    };

    /// <summary>
    /// Scheduled tasks that start something: every app's own, whatever starts them (sign-in, boot, a timer, idle); the
    /// <see cref="WindowsTasks"/> on are noted as Clean up's extras. A task that runs a program of Windows' own outside
    /// \Microsoft\ (Explorer's) is Windows' and isn't listed. schtasks lists them all as one XML, no administrator rights.
    /// </summary>
    private static IEnumerable<StartupEntry> TaskEntries(IStartupHost host)
    {
        var found = new List<StartupEntry>();
        var extras = new List<string>();
        try
        {
            foreach (var task in Tasks(host, GuardWait))
                if (TaskEntry(host, task, extras) is { } entry) found.Add(entry);
            return found;
        }
        finally { LastExtras = extras; }
    }

    /// <summary>
    /// Clean up's extras as the last read found them: <see cref="WindowsTasks"/> on this PC and on. They aren't listed,
    /// and Clean up turns them off without keeping them for Put back.
    /// </summary>
    internal static IReadOnlyList<string> LastExtras { get; private set; } = [];

    /// <summary>One task as an entry, or null when it isn't listed (Windows' own, or one that starts no program).</summary>
    private static StartupEntry? TaskEntry(IStartupHost host, TaskInfo task, List<string> extras)
    {
        var name = task.Path;
        var leaf = name[(name.LastIndexOf('\\') + 1)..];
        var program = task.Program ?? "";
        var arguments = task.Arguments;
        var command = program.Length == 0 ? name : string.IsNullOrEmpty(arguments) ? $"\"{program}\"" : $"\"{program}\" {arguments}";
        // Windows' own: never a row. The ones that only send Microsoft data are Clean up's extras.
        if (name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
        {
            if (task.Enabled && WindowsTasks.ContainsKey(name)) extras.Add(name);
            return null;
        }
        if (program.Length == 0) return null;
        var fingerprint = StartupFingerprint.Of(program, arguments);
        var system = program.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) && !StartupLocks.IsHost(program);
        if (!system) return Describe(host, "task:" + name, StartupSource.Task, machine: true, leaf, command, program, task.Enabled) with { Fingerprint = fingerprint };
        // Internet Explorer's daily RSS feed check, made per user outside \Microsoft\; nothing reads those feeds now.
        if (!leaf.StartsWith("User_Feed_Synchronization", StringComparison.OrdinalIgnoreCase)) return null;
        var feeds = new StartupCatalog.Item([], "Internet Explorer feed sync", "Checks old Internet Explorer RSS feeds for new posts once a day.",
            "Nothing you'd notice: nothing on this PC reads those feeds anymore.", "remove");
        return Describe(host, "task:" + name, StartupSource.Task, machine: true, leaf, command, program, task.Enabled, feeds) with { Publisher = "Microsoft", Fingerprint = fingerprint, Lock = LockKind.None };
    }

    /// <summary>Store apps that start at sign-in: a startupTask in the package's manifest, on unless the user or the app turned it off (its State).</summary>
    private static IEnumerable<StartupEntry> PackageEntries(IStartupHost host)
    {
        foreach (var task in host.PackageTasks())
        {
            var state = host.Get(Hive.User, $@"{StartupKeys.Packaged}\{task.Family}\{task.TaskId}", "State");
            var on = state is null ? task.ManifestEnabled : state.Number switch { 2 or 4 => true, 1 => false, _ => (bool?)null };
            // Off by the app or by policy, never asked to start: not a startup app of this user's.
            if (on is null || on == false && state?.Number != 1) continue;
            var publisher = task.Publisher;
            yield return Describe(host, $"package:{task.Family}!{task.TaskId}", StartupSource.Package, machine: false, task.Name, task.Exe ?? task.Family, null, on.Value)
                with { Publisher = publisher.Length > 0 ? publisher : null, Fingerprint = "package:" + task.Family.ToLowerInvariant() + "!" + task.TaskId.ToLowerInvariant() };
        }
    }

    /// <summary>
    /// Auto-start services. Only what the catalog names can be turned off (an updater's service); the rest that Microsoft
    /// didn't sign are listed so the player sees the real switch ("updater service" rows). Drivers' and Windows' own never.
    /// </summary>
    private static IEnumerable<StartupEntry> ServiceEntries(IStartupHost host)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var service in host.SubKeys(Hive.Machine, StartupKeys.Services))
        {
            var key = StartupKeys.Services + "\\" + service;
            var start = host.Get(Hive.Machine, key, "Start")?.Number ?? -1;
            if (start is not (2 or 4)) continue;
            var type = host.Get(Hive.Machine, key, "Type")?.Number ?? 0;
            if (host.Get(Hive.Machine, key, "ImagePath")?.Text is not { Length: > 0 } image) continue;
            var program = ProgramOf(image);
            var known = StartupCatalog.ForService(service);
            // Cheap first: a service that doesn't start by itself, isn't its own process, or is Windows' own is no row.
            if (known is null && (start != 2 || type is not (0x10 or 0x20 or 0x110 or 0x120) || program is null
                || program.StartsWith(windir, StringComparison.OrdinalIgnoreCase) && !program.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase))) continue;
            var display = host.Get(Hive.Machine, key, "DisplayName")?.Text;
            var name = known?.Name ?? (display is { Length: > 0 } && !display.StartsWith('@') ? display : service);
            var lockKind = StartupLocks.Classify(host, new LockCandidate(name, image, program, service, type, image), known);
            if (known is null)
            {
                // Only an own-process service that starts by itself is worth a row, and not Windows' own or Microsoft's.
                if (lockKind == LockKind.Windows) continue;
                if (lockKind == LockKind.None && host.Signer(program!) is "Microsoft Windows" or "Microsoft Corporation") continue;
            }
            yield return Describe(host, "service:" + service, StartupSource.Service, machine: true, name, image, program, start == 2, known)
                with { ReadOnly = known is null || known.Verdict is "keep" or "locked" || known.RealSwitch?.Kind == "updaterService", Lock = lockKind, Fingerprint = "service:" + service.ToLowerInvariant() };
        }
    }

    /// <summary>Active Setup: a program that runs once per user at sign-in, listed when Microsoft didn't make it. Read only.</summary>
    private static IEnumerable<StartupEntry> ActiveSetupEntries(IStartupHost host)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var guid in host.SubKeys(Hive.Machine, StartupKeys.ActiveSetup))
        {
            var key = StartupKeys.ActiveSetup + "\\" + guid;
            if (host.Get(Hive.Machine, key, "StubPath")?.Text is not { Length: > 0 } stub) continue;
            var program = ProgramOf(stub);
            if (program is null || program.StartsWith(windir, StringComparison.OrdinalIgnoreCase) || host.Signer(program) is "Microsoft Windows" or "Microsoft Corporation") continue;
            var name = host.Get(Hive.Machine, key, "")?.Text is { Length: > 0 } title ? title : guid;
            yield return Describe(host, "active:lm:" + guid, StartupSource.ActiveSetup, machine: true, name, stub, program, on: true)
                with { ReadOnly = true, Fingerprint = StartupFingerprint.OfCommand(stub) };
        }
    }

    /// <summary>Winlogon's Userinit and Shell when they aren't Windows' own. Read only.</summary>
    private static IEnumerable<StartupEntry> WinlogonEntries(IStartupHost host)
    {
        foreach (var (name, standard) in new[] { ("Userinit", "userinit.exe"), ("Shell", "explorer.exe") })
        {
            if (host.Get(Hive.Machine, StartupKeys.Winlogon, name)?.Text is not { Length: > 0 } value) continue;
            var files = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => !Path.GetFileName(part.Trim('"')).Equals(standard, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var extra in files)
                yield return Describe(host, "winlogon:lm:" + name, StartupSource.Winlogon, machine: true, name, extra, ProgramOf(extra), on: true)
                    with { ReadOnly = true, Fingerprint = StartupFingerprint.OfCommand(extra) };
        }
    }

    /// <summary>The program a command starts: its first .exe (or other file) with the environment's variables filled in; null for a value that names none.</summary>
    internal static string? ProgramOf(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        if (expanded.Length == 0) return null;
        string first;
        if (expanded[0] == '"')
        {
            var end = expanded.IndexOf('"', 1);
            first = end > 0 ? expanded[1..end] : expanded[1..];
        }
        else
        {
            var exe = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            first = exe > 0 ? expanded[..(exe + 4)] : expanded.Split(' ', 2)[0];
        }
        return first.Contains('\\') || first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? first : null;
    }

    /// <summary>An entry with its name, maker and explanation: the catalog's when it knows it, else the item's own name, or the program's own description.</summary>
    private static StartupEntry Describe(IStartupHost host, string id, StartupSource source, bool machine, string name, string command, string? program, bool on,
        StartupCatalog.Item? item = null)
    {
        var names = StartupApps.ExecutableNames(command).Select(n => n.ToLowerInvariant()).ToList();
        if (program is not null && Path.GetFileName(program) is { Length: > 0 } file && !names.Contains(file.ToLowerInvariant())) names.Add(file.ToLowerInvariant());
        var known = item ?? StartupCatalog.Find(name, names, program ?? command);
        string? publisher = null, description = null;
        var missing = false;
        if (program is not null && Path.IsPathFullyQualified(program))
        {
            var info = host.Info(program);
            if (info.Exists) { publisher = info.Company; description = info.Description; }
            else missing = true;
        }
        var lockKind = StartupLocks.Classify(host, new LockCandidate(name, command, program), known);
        // A program that only starts other programs (PowerShell, cmd) says nothing of the item: the item's own name does.
        var own = StartupLocks.IsHost(program);
        var title = known?.Name ?? (lockKind == LockKind.Exo ? "Exo" : own ? name : description ?? name);
        return new StartupEntry(id, source, machine, title, command, program, on)
        {
            Publisher = publisher,
            Known = known,
            Missing = missing,
            Lock = lockKind,
            Fingerprint = StartupFingerprint.OfCommand(command),
        };
    }
}

/// <summary>
/// A start-up item's real switch: what the catalog says to change when the program keeps turning itself back on. Process names
/// every process the program runs under (comma-separated, "SCSKiller,scskillerw"): the switch waits while any of them runs.
/// A switch with no process at all is never used: Exo can't tell whether the program is running, and doesn't change a
/// program's setting behind its back.
/// </summary>
internal sealed record RealSwitch(string Kind, string? File = null, string? Path = null, string? Off = null, string? Process = null, string? Service = null, string? Status = null)
{
    /// <summary>The processes whose running holds this switch, each a name <c>Running</c> matches; none: the switch isn't used.</summary>
    internal IReadOnlyList<string> Processes => (Process ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// What common startup entries are for, in plain words (StartupCatalog.json, built into Exo): matched by the program a
/// command starts ("steam.exe", "discord.exe" behind Squirrel's Update.exe) or, for a Run value that isn't a program, by
/// its name ("~af_" for AppsFlyer's AF_uuid_ and AF_counter_). A generic program name ("scheduler.exe") counts only
/// under its own folder (Under). Verdict: remove, optional, keep, or locked. RealSwitch: what to change when the program
/// keeps coming back (a setting of its own, an updater service Exo leaves alone, or anti-cheat that starts it). Service:
/// the Windows service an updater's entry belongs to, which the catalog may turn off.
/// </summary>
internal static class StartupCatalog
{
    internal sealed record Item(string[] Match, string Name, string Does, string Without, string Verdict, string? Under = null, RealSwitch? RealSwitch = null, string? Service = null);

    private static readonly Lazy<IReadOnlyList<Item>> Items = new(() =>
    {
        try
        {
            using var stream = typeof(StartupCatalog).Assembly.GetManifestResourceStream("StartupCatalog.json");
            return stream is null ? [] : JsonSerializer.Deserialize<List<Item>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException ex) { AppLog.Warn("Startup: the catalog didn't read: " + ex.Message); return []; }
    });

    /// <summary>A test's own catalog item, ahead of the real ones, until the scope ends.</summary>
    internal static IDisposable UseTestItem(Item item)
    {
        TestItems.Add(item);
        return new Scope(item);
    }

    private static readonly List<Item> TestItems = [];

    private sealed class Scope(Item item) : IDisposable
    {
        public void Dispose() => TestItems.Remove(item);
    }

    // A test's item is ahead of the real ones while its scope lasts: computed each time, never cached with one in it.
    private static IReadOnlyList<Item> AllItems => TestItems.Count == 0 ? Items.Value : [.. TestItems, .. Items.Value];

    /// <summary>Every item, for the test that reads the whole catalog.</summary>
    internal static IReadOnlyList<Item> All => AllItems;

    /// <summary>The item for a service of that name, when the catalog names it.</summary>
    internal static Item? ForService(string service) => AllItems.FirstOrDefault(item => string.Equals(item.Service, service, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The item for an entry named <paramref name="name"/> that starts <paramref name="programs"/> (file names, lower case)
    /// from <paramref name="path"/>, the program it hands off to first.
    /// </summary>
    internal static Item? Find(string name, IReadOnlyList<string> programs, string? path)
    {
        var lower = name.ToLowerInvariant();
        foreach (var program in programs.Reverse())
            if (AllItems.FirstOrDefault(item => item.Match.Contains(program, StringComparer.OrdinalIgnoreCase)
                    && (item.Under is null || path?.Contains(item.Under, StringComparison.OrdinalIgnoreCase) == true)) is { } byProgram) return byProgram;
        return AllItems.FirstOrDefault(item => item.Match.Any(match => match.StartsWith('~') && lower.StartsWith(match[1..], StringComparison.Ordinal)));
    }
}
