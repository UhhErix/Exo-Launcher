using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

internal enum Hive { User, Machine }

/// <summary>A registry value as Windows keeps it: a string (expandable or not), bytes or a number. Strings are never expanded.</summary>
internal sealed record RegValue(RegistryValueKind Kind, string? Text = null, byte[]? Bytes = null, int Number = 0)
{
    internal static RegValue Str(string text) => new(RegistryValueKind.String, Text: text);
    internal static RegValue Expand(string text) => new(RegistryValueKind.ExpandString, Text: text);
    internal static RegValue Bin(byte[] bytes) => new(RegistryValueKind.Binary, Bytes: bytes);
    internal static RegValue DWord(int number) => new(RegistryValueKind.DWord, Number: number);

    internal static bool Same(RegValue? a, RegValue? b) =>
        a is null || b is null ? a is null && b is null
        : a.Kind == b.Kind && a.Text == b.Text && a.Number == b.Number && (a.Bytes ?? []).AsSpan().SequenceEqual(b.Bytes ?? []);

    public bool Equals(RegValue? other) => other is not null && Same(this, other);
    public override int GetHashCode() => HashCode.Combine(Kind, Text, Number, Bytes?.Length);
}

/// <summary>One change to what starts with Windows. A guard check or a press collects them and hands them over once.</summary>
internal abstract record StartupChange;
/// <summary>A registry value written, or deleted when <see cref="Value"/> is null.</summary>
internal sealed record SetReg(Hive Hive, string Key, string Name, RegValue? Value) : StartupChange;
internal sealed record SetTask(string Path, bool On) : StartupChange;
internal sealed record DeleteTask(string Path) : StartupChange;
internal sealed record CreateTask(string Path, string XmlFile) : StartupChange;
/// <summary>A file moved (an older Exo kept a removed Startup shortcut in its own folder; Put back moves it home).</summary>
internal sealed record MoveFile(string From, string To) : StartupChange;
/// <summary>A Startup folder file deleted permanently because its target app is gone.</summary>
internal sealed record DeleteFile(string Path) : StartupChange;
/// <summary>The machine's own values of one hand-over, left undone together as one request for permission: the changes those are.</summary>
internal sealed record MachineValues(IReadOnlyList<StartupChange> Values) : StartupChange;

/// <summary>What handing changes over came to: Windows' prompt declined (nothing was changed that needed it), or the line to log, and which changes were left undone for want of Windows' permission.</summary>
internal sealed record ApplyResult(bool Declined, string? Error)
{
    internal static readonly ApplyResult Done = new(false, null);

    /// <summary>
    /// The changes that were left undone because they need Windows' permission: one the user can't make either (a task an
    /// installer created with administrator rights) stays in this list however much it is handed over again.
    /// </summary>
    internal IReadOnlyList<StartupChange> Left { get; init; } = [];
}

internal sealed record TaskInfo(string Path, bool Enabled, string? Program, string? Arguments, string Stamp);
internal sealed record FolderFile(string Name, string? Target);
internal sealed record PackageTaskDecl(string Family, string TaskId, string Name, string Publisher, string? Exe, bool ManifestEnabled);
internal sealed record ProgramInfo(bool Exists, string? Company, string? Description);
internal sealed record RunningProgram(string? Path, DateTime? Started);

/// <summary>
/// Every effect the start-up list and its guard have on this PC, behind one seam: the registry, scheduled tasks, the
/// Startup folders, programs and their signatures, security software, the window in front, and the watch. The real one
/// is <see cref="RealStartupHost"/>; tests use a fake of all of it and never touch this PC's own.
/// </summary>
internal interface IStartupHost
{
    DateTime UtcNow { get; }
    RegValue? Get(Hive hive, string key, string name);
    IReadOnlyList<string> ValueNames(Hive hive, string key);
    IReadOnlyList<string> SubKeys(Hive hive, string key);
    /// <summary>
    /// Applies every change; what needs Windows' permission goes in one request (never when <paramref name="elevate"/> is false:
    /// that part is left undone). Callers read back to check.
    /// </summary>
    ApplyResult Apply(IReadOnlyList<StartupChange> changes, bool elevate = true);
    /// <summary>Exo's data folder: where the records of what it removed and what the guard watches are kept.</summary>
    string DataFolder { get; }
    /// <summary>Every scheduled task, one `schtasks /query`; waited on for <paramref name="wait"/>, or 15 seconds when null (the player's own press).</summary>
    IReadOnlyList<TaskInfo> Tasks(TimeSpan? wait = null);
    byte[]? TaskXml(string path);
    IReadOnlyList<FolderFile> FolderFiles(bool machine);
    string FolderPath(bool machine);
    IReadOnlyList<PackageTaskDecl> PackageTasks();
    /// <summary>The program in front of the others, as its file name without .exe in lower case; null when unknown.</summary>
    string? Foreground();
    IReadOnlyList<RunningProgram> Running(string processName);
    ProgramInfo Info(string path);
    /// <summary>Who signed the file, when its signature holds; null for an unsigned or unreadable one.</summary>
    string? Signer(string path);
    /// <summary>The programs of every security product Windows Security lists (antivirus, firewall, anti-spyware).</summary>
    IReadOnlyList<string> SecurityPrograms();
    void EnsureSecurityReady(TimeSpan timeout);
    /// <summary>
    /// Whether <see cref="SecurityPrograms"/> was read successfully: false until a CIM read succeeds.
    /// While Exo can't check this PC's security software no item it can't vouch for is turned off.
    /// </summary>
    bool SecurityKnown { get; }
    string? ReadFile(string path);
    void WriteFile(string path, string text);
    byte[]? ReadBytes(string path);
    void WriteBytes(string path, byte[] bytes);
    void DeleteFile(string path);
    bool FileExists(string path);
    /// <summary>
    /// Whether <paramref name="path"/> is on a local fixed drive: a program's own settings file on a network share or a
    /// removable one is never written, because a slow share could hold the guard for as long as it takes to answer.
    /// </summary>
    bool SettingsDriveFixed(string path);
    /// <summary>Calls <paramref name="changed"/> when anything start-up changes, until disposed. No polling.</summary>
    IDisposable Watch(Action changed);
}

/// <summary>The registry places Windows starts programs from, and the ones that say whether each is on.</summary>
internal static class StartupKeys
{
    internal const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string RunOnce32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string PolicyRun = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    internal const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    internal const string Winlogon = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    internal const string ActiveSetup = @"Software\Microsoft\Active Setup\Installed Components";
    internal const string Services = @"SYSTEM\CurrentControlSet\Services";
    internal const string Packaged = StartupApps.PackagedTasksPath;
}

/// <summary>This PC: the real registry, schtasks, folders and processes. Only the guard's and the list's own code reaches it.</summary>
internal sealed class RealStartupHost : IStartupHost
{
    public DateTime UtcNow => DateTime.UtcNow;

    private static RegistryKey Root(Hive hive) => hive == Hive.User ? Registry.CurrentUser : Registry.LocalMachine;

    public RegValue? Get(Hive hive, string key, string name)
    {
        try
        {
            using var k = Root(hive).OpenSubKey(key);
            if (k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) return null;
            return k.GetValueKind(name) switch
            {
                RegistryValueKind.String => RegValue.Str((string)value),
                RegistryValueKind.ExpandString => RegValue.Expand((string)value),
                RegistryValueKind.Binary => RegValue.Bin((byte[])value),
                RegistryValueKind.DWord => RegValue.DWord((int)value),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidCastException) { return null; }
    }

    public IReadOnlyList<string> ValueNames(Hive hive, string key)
    {
        try { using var k = Root(hive).OpenSubKey(key); return k?.GetValueNames() ?? []; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    public IReadOnlyList<string> SubKeys(Hive hive, string key)
    {
        try { using var k = Root(hive).OpenSubKey(key); return k?.GetSubKeyNames() ?? []; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    public string DataFolder => PathHelper.AppDataDir;

    private string KeptFolder => Path.Combine(DataFolder, "startup-removed");

    public ApplyResult Apply(IReadOnlyList<StartupChange> changes, bool elevate = true)
    {
        if (HostMode.IsDisposable) return new ApplyResult(false, HostMode.Refusal);
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var elevated = new List<string>();
        // What each elevated command was handed over for: a change that is left undone is named by it, not by the command line.
        var left = new Dictionary<string, StartupChange>(StringComparer.OrdinalIgnoreCase);
        var machine = new StringBuilder();
        var machineChanges = new List<StartupChange>();
        string? error = null;
        void Raised(string command, StartupChange change)
        {
            elevated.Add(command);
            left[command] = change;
        }
        foreach (var change in changes)
        {
            switch (change)
            {
                case SetReg { Hive: Hive.User } set:
                    try { WriteUser(set); }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { error ??= ex.Message; }
                    break;
                case SetReg set:
                    machine.Append(RegLines(set));
                    machineChanges.Add(set);
                    break;
                case SetTask task when StartupEntries.CmdSafe(task.Path):
                    // A change made as the user is made at once; one Windows refuses is handed over for permission.
                    if (Schtasks(["/change", "/tn", task.Path, task.On ? "/enable" : "/disable"], out _, null) != 0)
                        Raised($"\"{schtasks}\" /change /tn \"{task.Path}\" {(task.On ? "/enable" : "/disable")}", task);
                    break;
                case MoveFile move when StartupEntries.CmdSafe(move.From) && StartupEntries.CmdSafe(move.To):
                    try { Directory.CreateDirectory(Path.GetDirectoryName(move.To)!); File.Move(move.From, move.To, overwrite: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Raised($"move /y \"{move.From}\" \"{move.To}\"", move); }
                    break;
                case DeleteFile del when StartupEntries.CmdSafe(del.Path):
                    try { if (File.Exists(del.Path)) DeletionGate.DeleteFile(del.Path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Raised($"del /f /q \"{del.Path}\"", del); }
                    break;
                case DeleteTask task when StartupEntries.CmdSafe(task.Path):
                    if (Schtasks(["/delete", "/tn", task.Path, "/f"], out _, null) != 0) Raised($"\"{schtasks}\" /delete /tn \"{task.Path}\" /f", task);
                    break;
                case CreateTask task when StartupEntries.CmdSafe(task.Path) && StartupEntries.CmdSafe(task.XmlFile):
                    if (Schtasks(["/create", "/tn", task.Path, "/xml", task.XmlFile], out _, null) != 0) Raised($"\"{schtasks}\" /create /tn \"{task.Path}\" /xml \"{task.XmlFile}\"", task);
                    break;
                default:
                    error ??= "Exo can't change this one.";
                    break;
            }
        }
        if (machine.Length > 0)
        {
            Directory.CreateDirectory(KeptFolder);
            var file = Path.Combine(KeptFolder, "change.reg");
            if (!StartupEntries.CmdSafe(file)) return new ApplyResult(false, "Exo can't make this change from this folder.");
            File.WriteAllText(file, "Windows Registry Editor Version 5.00\r\n\r\n" + machine, Encoding.Unicode);
            var command = $"\"{Path.Combine(Environment.SystemDirectory, "reg.exe")}\" import \"{file}\"";
            elevated.Add(command);
            left[command] = new MachineValues([.. machineChanges]);
        }
        if (elevated.Count == 0) return new ApplyResult(false, error);
        if (!elevate) return new ApplyResult(true, "Windows' permission is needed for the rest.") { Left = [.. left.Values] };
        try
        {
            var why = ElevatedCommands.Run(elevated, "Startup");
            return why is null ? new ApplyResult(false, error) : new ApplyResult(why.Contains("declined", StringComparison.OrdinalIgnoreCase), why);
        }
        finally { try { DeletionGate.DeleteFile(Path.Combine(KeptFolder, "change.reg")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static void WriteUser(SetReg set)
    {
        using var key = Registry.CurrentUser.CreateSubKey(set.Key);
        if (set.Value is null) { key.DeleteValue(set.Name, throwOnMissingValue: false); return; }
        key.SetValue(set.Name, set.Value.Kind switch
        {
            RegistryValueKind.Binary => set.Value.Bytes ?? [],
            RegistryValueKind.DWord => set.Value.Number,
            _ => set.Value.Text ?? "",
        }, set.Value.Kind);
    }

    /// <summary>One value in a .reg file (HKLM): as it is, or deleted.</summary>
    private static string RegLines(SetReg set)
    {
        var data = set.Value switch
        {
            null => "-",
            { Kind: RegistryValueKind.Binary } v => StartupEntries.RegHex(v.Bytes ?? []),
            { Kind: RegistryValueKind.DWord } v => "dword:" + ((uint)v.Number).ToString("x8", CultureInfo.InvariantCulture),
            { Kind: RegistryValueKind.ExpandString } v => "hex(2):" + StartupEntries.RegHex(Encoding.Unicode.GetBytes((v.Text ?? "") + "\0"))[4..],
            var v => $"\"{StartupEntries.RegText(v.Text ?? "")}\"",
        };
        return $"[HKEY_LOCAL_MACHINE\\{set.Key}]\r\n\"{StartupEntries.RegText(set.Name)}\"={data}\r\n\r\n";
    }

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public IReadOnlyList<TaskInfo> Tasks(TimeSpan? wait = null)
    {
        var found = new List<TaskInfo>();
        // The guard's path gives a short wait (a check holds the player's own press off while it runs); a press reads with 15.
        if (Schtasks(["/query", "/xml", "ONE"], out var bytes, wait?.TotalMilliseconds ?? 15000) != 0 || bytes.Length < 8) return found;
        var text = bytes.Length > 1 && (bytes[1] == 0 || bytes[0] == 0xFF) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        var start = text.IndexOf("<Tasks", StringComparison.Ordinal);
        if (start < 0) return found;
        string? path = null;
        foreach (var node in XDocument.Parse(text[start..]).Root?.Nodes() ?? [])
        {
            if (node is XComment comment) { path = comment.Value.Trim(); continue; }
            if (node is not XElement task || path is null) continue;
            var on = !string.Equals(task.Element(TaskNs + "Settings")?.Element(TaskNs + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
            var exec = task.Element(TaskNs + "Actions")?.Element(TaskNs + "Exec");
            var program = exec?.Element(TaskNs + "Command")?.Value.Trim().Trim('"');
            var stamp = task.Element(TaskNs + "RegistrationInfo")?.Element(TaskNs + "Date")?.Value ?? "";
            found.Add(new TaskInfo(path, on, program is { Length: > 0 } ? Environment.ExpandEnvironmentVariables(program) : null, exec?.Element(TaskNs + "Arguments")?.Value.Trim(), stamp));
            path = null;
        }
        return found;
    }

    public byte[]? TaskXml(string path) => StartupEntries.CmdSafe(path) && Schtasks(["/query", "/tn", path, "/xml", "ONE"], out var xml, null) == 0 && xml.Length > 0 ? xml : null;

    public string FolderPath(bool machine) => Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);

    public IReadOnlyList<FolderFile> FolderFiles(bool machine)
    {
        var folder = FolderPath(machine);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return [];
        try
        {
            return [.. Directory.EnumerateFiles(folder).Select(file => new FolderFile(Path.GetFileName(file),
                file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LeftoverScan.ShortcutTarget(file) : file))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    public IReadOnlyList<PackageTaskDecl> PackageTasks()
    {
        var found = new List<PackageTaskDecl>();
        try
        {
            foreach (var package in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                string manifest;
                try { manifest = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { continue; }
                if (!File.Exists(manifest)) continue;
                var text = File.ReadAllText(manifest);
                if (!text.Contains("windows.startupTask", StringComparison.OrdinalIgnoreCase)) continue;
                var family = package.Id.FamilyName;
                foreach (var task in XDocument.Parse(text).Descendants().Where(element => element.Name.LocalName == "StartupTask"))
                {
                    if ((string?)task.Attribute("TaskId") is not { Length: > 0 } id) continue;
                    string name, publisher;
                    try { name = package.DisplayName; publisher = package.PublisherDisplayName; }
                    catch (COMException) { name = family; publisher = ""; }
                    found.Add(new PackageTaskDecl(family, id, name, publisher, (string?)task.Parent?.Attribute("Executable"),
                        string.Equals((string?)task.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.Xml.XmlException) { AppLog.Warn("Startup: Store apps not read: " + ex.Message); }
        return found;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    public string? Foreground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public IReadOnlyList<RunningProgram> Running(string processName)
    {
        var found = new List<RunningProgram>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string? path = null;
                DateTime? started = null;
                try { path = process.MainModule?.FileName; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                try { started = process.StartTime.ToUniversalTime(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                found.Add(new RunningProgram(path, started));
            }
        }
        return found;
    }

    public ProgramInfo Info(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return new ProgramInfo(false, null, null);
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new ProgramInfo(true, version.CompanyName?.Trim() is { Length: > 0 } company ? company : null,
                version.FileDescription?.Trim() is { Length: > 0 } words ? words : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new ProgramInfo(true, null, null); }
    }

    private readonly SignerCache _signers = new(AuthenticodeVerifier.Publisher, Written);

    public string? Signer(string path) => _signers.Of(path);

    /// <summary>When the file was last written; a file that can't be read is a file that may have changed.</summary>
    private static DateTime Written(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("its time could not be read", ex); }
    }

    private readonly object _gate = new();
    private (DateTime At, IReadOnlyList<string> Programs)? _security;
    private bool _securityKnown;
    private bool _reading;

    /// <summary>Whether the last read of Windows Security's list worked and had something to say.</summary>
    public bool SecurityKnown { get { lock (_gate) return _securityKnown; } }

    /// <summary>Reading this PC's security software: a test sets its own, so nothing here starts PowerShell.</summary>
    internal Func<(IReadOnlyList<string> Programs, bool Worked, string? Why)> SecurityRead { get; set; } = ReadSecurityFromWindows;

    /// <summary>The clock the security list's cache is kept by: a test moves it instead of waiting.</summary>
    internal Func<DateTime> SecurityClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Starts one read now, in the background, so the first check finds an answer at once. One read at a time.</summary>
    internal void WarmSecurity()
    {
        lock (_gate)
        {
            if (_reading) return;
            _reading = true;
        }
        _ = Task.Run(ReadSecurity);
    }

    /// <summary>
    /// Windows Security's list, read through PowerShell's CIM (the same data the Security Center shows). The read runs in the
    /// background, never on the caller's thread: this returns the last answer at once (nothing until there is one), and starts
    /// a read when the answer is older than ten minutes—one minute after a failed or empty one, so a PC whose security
    /// software can't be listed does not start PowerShell once per entry of every check. Exo never treats "I couldn't ask" as
    /// "nothing is installed": <see cref="SecurityKnown"/> stays false until an answer arrives.
    /// </summary>
    public IReadOnlyList<string> SecurityPrograms()
    {
        bool ask;
        IReadOnlyList<string>? programs;
        lock (_gate)
        {
            programs = _security?.Programs;
            var fresh = _security is { } answer
                && SecurityClock() - answer.At < (_securityKnown ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
            ask = !_reading && !fresh;
            if (ask) _reading = true;
        }
        if (ask) _ = Task.Run(ReadSecurity);
        return programs ?? [];
    }

    public void EnsureSecurityReady(TimeSpan timeout)
    {
        _ = SecurityPrograms();
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_reading)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                Monitor.Wait(_gate, left);
            }
        }
    }

    /// <summary>One read, in the background: its answer is kept, and said once when it failed.</summary>
    private void ReadSecurity()
    {
        (IReadOnlyList<string> Programs, bool Worked, string? Why) read;
        try { read = SecurityRead(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or JsonException)
        { read = ([], false, ex.Message); }
        lock (_gate)
        {
            _securityKnown = read.Worked;
            _security = (SecurityClock(), read.Programs);
            _reading = false;
            Monitor.PulseAll(_gate);
        }
        if (!_securityKnown)
            AppLog.Warn("Startup: this PC's security software could not be listed" + (read.Why is { Length: > 0 } reason ? ": " + reason : "")
                + "; Exo leaves what it can't vouch for in startup.");
    }

    /// <summary>Windows Security's list through PowerShell's CIM: what was found, whether the read worked, and why it didn't.</summary>
    private static (IReadOnlyList<string> Programs, bool Worked, string? Why) ReadSecurityFromWindows()
    {
        var programs = new List<string>();
        try
        {
            const string script = "foreach($c in 'AntiVirusProduct','FirewallProduct','AntiSpywareProduct'){Get-CimInstance -Namespace root/SecurityCenter2 -ClassName $c -ErrorAction SilentlyContinue|ForEach-Object{$_.pathToSignedProductExe;$_.pathToSignedReportingExe}}";
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            };
            var result = ProcessOutput.RunCaptureText(info, TimeSpan.FromSeconds(20));
            if (result.Completed)
            {
                programs.AddRange(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.Length > 3));
                return (programs, result.ExitCode == 0, null);
            }
            return (programs, false, "it did not answer in time");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (programs, false, ex.Message);
        }
    }

    public string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".exo.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    public byte[]? ReadBytes(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void DeleteFile(string path)
    {
        try { DeletionGate.DeleteFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool SettingsDriveFixed(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not { Length: > 0 }) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException) { return false; }
    }

    public IDisposable Watch(Action changed) => new Watching(changed);

    /// <summary>One schtasks call, waited on for <paramref name="wait"/> milliseconds (15 seconds when null): a call that answers in time is one the guard's lock is not held for long.</summary>
    private static int Schtasks(IEnumerable<string> arguments, out byte[] output, double? wait)
    {
        output = [];
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        var result = ProcessOutput.RunCaptureBytes(info, TimeSpan.FromMilliseconds(wait ?? 15000));
        if (!result.Completed) return -1;
        output = result.Stdout;
        return result.ExitCode;
    }

    /// <summary>
    /// RegNotifyChangeKeyValue on every place that starts programs, and a FileSystemWatcher on both Startup folders: one
    /// thread that sleeps until Windows says something changed. A key that doesn't exist yet is watched through its
    /// nearest parent. Disposing ends the thread and closes every handle.
    /// </summary>
    private sealed class Watching : IDisposable
    {
        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, int filter, SafeWaitHandle eventHandle, bool asynchronous);

        private const int Names = 1, LastSet = 4;
        private static readonly (Hive Hive, string Key)[] Places =
        [
            (Hive.User, StartupKeys.Run), (Hive.Machine, StartupKeys.Run), (Hive.User, StartupKeys.Run32), (Hive.Machine, StartupKeys.Run32),
            (Hive.User, StartupKeys.RunOnce), (Hive.Machine, StartupKeys.RunOnce), (Hive.Machine, StartupKeys.RunOnce32), (Hive.User, StartupKeys.RunOnce32),
            (Hive.User, StartupKeys.Approved.TrimEnd('\\')), (Hive.Machine, StartupKeys.Approved.TrimEnd('\\')),
            (Hive.User, StartupKeys.Packaged),
        ];

        private readonly Action _changed;
        private readonly ManualResetEvent _stop = new(false);
        private readonly List<FileSystemWatcher> _folders = [];
        private readonly Thread _thread;

        internal Watching(Action changed)
        {
            _changed = changed;
            foreach (var machine in new[] { false, true })
            {
                var folder = Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    watcher.Created += (_, _) => _changed();
                    watcher.Changed += (_, _) => _changed();
                    watcher.Deleted += (_, _) => _changed();
                    watcher.Renamed += (_, _) => _changed();
                    watcher.EnableRaisingEvents = true;
                    _folders.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { AppLog.Debug("Startup: folder not watched: " + ex.Message); }
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "Startup watch" };
            _thread.Start();
        }

        private sealed class Armed(RegistryKey key, ManualResetEvent signal)
        {
            internal RegistryKey Key { get; } = key;
            internal ManualResetEvent Signal { get; } = signal;
        }

        private static RegistryKey? Open(Hive hive, string path)
        {
            var root = hive == Hive.User ? Registry.CurrentUser : Registry.LocalMachine;
            while (path.Length > 0)
            {
                try { if (root.OpenSubKey(path) is { } key) return key; }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                var slash = path.LastIndexOf('\\');
                path = slash < 0 ? "" : path[..slash];
            }
            return null;
        }

        private void Loop()
        {
            var armed = new List<Armed>();
            try
            {
                foreach (var (hive, path) in Places)
                    if (Open(hive, path) is { } key) armed.Add(new Armed(key, new ManualResetEvent(false)));
                while (true)
                {
                    foreach (var one in armed.ToList())
                    {
                        one.Signal.Reset();
                        if (RegNotifyChangeKeyValue(one.Key.Handle, subtree: true, Names | LastSet, one.Signal.SafeWaitHandle, asynchronous: true) != 0)
                        {
                            one.Key.Dispose();
                            one.Signal.Dispose();
                            armed.Remove(one);
                        }
                    }
                    var handles = new WaitHandle[armed.Count + 1];
                    handles[0] = _stop;
                    for (var i = 0; i < armed.Count; i++) handles[i + 1] = armed[i].Signal;
                    if (handles.Length > 64) handles = handles[..64];
                    var which = WaitHandle.WaitAny(handles);
                    if (which == 0) return;
                    _changed();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                foreach (var one in armed) { one.Key.Dispose(); one.Signal.Dispose(); }
            }
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join(2000);
            foreach (var watcher in _folders) watcher.Dispose();
            _stop.Dispose();
        }
    }
}

/// <summary>
/// Signatures read once per file and not again until the file changes (its last write time): the guard asks for every
/// entry's signer on every check, and reading a signature is expensive.
/// </summary>
internal sealed class SignerCache(Func<string, string?> read, Func<string, DateTime> written)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Stamp, string? Signer)> _known = new(StringComparer.OrdinalIgnoreCase);

    internal string? Of(string path)
    {
        DateTime stamp;
        try { stamp = written(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return read(path); }
        lock (_gate)
        {
            if (_known.TryGetValue(path, out var seen) && seen.Stamp == stamp) return seen.Signer;
        }
        var signer = read(path);
        lock (_gate) _known[path] = (stamp, signer);
        return signer;
    }
}

/// <summary>A cheap stable fingerprint of text, for matching a task re-made under a new name.</summary>
internal static class StartupFingerprint
{
    /// <summary>The program path (variables filled in, lower case) plus arguments with white space collapsed.</summary>
    internal static string Of(string? program, string? arguments)
    {
        var path = Environment.ExpandEnvironmentVariables((program ?? "").Trim().Trim('"')).Replace('/', '\\').ToLowerInvariant();
        var args = string.Join(' ', (arguments ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return args.Length == 0 ? path : path + " " + args;
    }

    /// <summary>The fingerprint of a Run value or a shortcut's command line.</summary>
    internal static string OfCommand(string command)
    {
        var program = StartupEntries.ProgramOf(command);
        if (program is null) return Environment.ExpandEnvironmentVariables(command).Trim().ToLowerInvariant();
        var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        var at = expanded.IndexOf(program, StringComparison.OrdinalIgnoreCase);
        var rest = at >= 0 ? expanded[(at + program.Length)..].TrimStart('"') : "";
        return Of(program, rest);
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
