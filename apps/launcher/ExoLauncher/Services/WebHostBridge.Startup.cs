using System.Diagnostics;
using ExoLauncher.Helpers;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// startup.list: everything that starts when you sign in (<see cref="StartupEntries"/>), each with what it's for, whether
/// it starts now and how much memory it holds while it runs, and what Exo removed (with when). startup.remove takes one
/// out of startup for good (Windows asks for a machine-wide one); startup.restore puts one Exo removed back. Each answers
/// with the list as it is after.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> StartupListAsync()
    {
        var (items, removed) = await Task.Run(StartupLists).ConfigureAwait(true);
        return new { ok = true, message = (string?)null, items, removed, extras = StartupEntries.LastExtras.Count };
    }

    private static async Task<object> StartupChangeAsync(JsonElement p, bool hasParams, bool restore)
    {
        if (HostMode.IsDisposable) return new { ok = false, message = (string?)HostMode.Refusal, items = Array.Empty<object>(), removed = Array.Empty<object>(), extras = 0 };
        var id = ReadString(p, hasParams, "id") ?? "";
        var message = await Task.Run(() => restore ? StartupEntries.Restore(id) : StartupEntries.Remove(id)).ConfigureAwait(true);
        var (items, removed) = await Task.Run(StartupLists).ConfigureAwait(true);
        return new { ok = message is null, message, items, removed, extras = StartupEntries.LastExtras.Count };
    }

    /// <summary>startup.clean: everything safe to remove out of startup at once (<see cref="StartupEntries.Clean"/>), with the lists after.</summary>
    private static async Task<object> StartupCleanAsync()
    {
        if (HostMode.IsDisposable) return new { ok = false, message = (string?)HostMode.Refusal, items = Array.Empty<object>(), removed = Array.Empty<object>(), cleaned = 0, extras = 0 };
        var (count, message) = await Task.Run(StartupEntries.Clean).ConfigureAwait(true);
        var (items, removed) = await Task.Run(StartupLists).ConfigureAwait(true);
        return new { ok = message is null, message, items, removed, cleaned = count, extras = StartupEntries.LastExtras.Count };
    }

    private static (object[] items, object[] removed) StartupLists()
    {
        var (entries, presentIds) = StartupEntries.ReadWithPresent(StartupEntries.Host);
        var shownIds = new HashSet<string>(entries.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
        var memory = Running(entries);
        var items = entries.Select(entry =>
        {
            // Its icon through apps.icons, from the program it starts.
            var icon = entry.Program is { } program && File.Exists(program) ? "startup:" + entry.Id : null;
            if (icon is not null) IconFiles[icon] = entry.Program;
            return (object)new
            {
                id = entry.Id,
                source = SourceOf(entry.Source),
                machine = entry.Machine,
                name = entry.Name,
                publisher = entry.Publisher,
                command = entry.Command,
                enabled = entry.Enabled,
                does = entry.Known?.Does,
                without = entry.Known?.Without,
                verdict = entry.Locked ? "locked" : entry.Known?.Verdict,
                unneeded = StartupEntries.Unneeded(entry),
                missing = entry.Missing,
                locked = entry.Locked,
                lockKind = entry.Locked ? entry.Lock.ToString().ToLowerInvariant() : null,
                readOnly = entry.ReadOnly,
                // One plain sentence for a row Exo leaves alone or can't hold, and the guard's state for the page.
                status = entry.Locked ? StartupLocks.Sentence(entry.Lock) : entry.Status,
                guard = entry.Guard == GuardState.Untracked ? null : entry.Guard.ToString().ToLowerInvariant(),
                memory = memory.GetValueOrDefault(entry.Id),
                icon,
                // The program in Apps it starts, so its row can say so.
                app = entry.Program is { } started ? AppOf(started) : null,
            };
        }).ToArray();
        var removed = StartupEntries.Kept()
            .Where(item => !shownIds.Contains(item.Id) && (!item.ValueKept || presentIds.Contains(item.Id)))
            .Select(item =>
            {
                var icon = item.Program is { } program && File.Exists(program) ? "startup:" + item.Id : null;
                if (icon is not null) IconFiles[icon] = item.Program;
                return (object)new
                {
                    id = item.Id,
                    name = item.Name,
                    at = item.At.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    // Removed by an older Exo although it is one Exo never turns off: it can be put back, and is locked from now on.
                    locked = StartupGuard.LockOf(StartupEntries.Host, item) != LockKind.None,
                    icon,
                };
            }).ToArray();
        return (items, removed);
    }

    /// <summary>The page knows four kinds of place; the other places (RunOnce, policies, services, Active Setup, Winlogon) read as the closest.</summary>
    private static string SourceOf(StartupSource source) => source switch
    {
        StartupSource.Folder => "folder",
        StartupSource.Package => "package",
        StartupSource.Task or StartupSource.Service or StartupSource.ActiveSetup or StartupSource.Winlogon => "task",
        _ => "run",
    };

    private static string? AppOf(string program) => Volatile.Read(ref _lastPrograms)?.Programs.FirstOrDefault(app =>
        !app.Store && app.Location is { } folder && InstalledPrograms.Own(folder) && program.StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))?.Id;

    private static readonly HashSet<string> GenericShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "rundll32", "svchost", "wscript", "cscript",
        "conhost", "explorer", "msiexec", "dllhost", "taskhostw", "backgroundtaskhost", "regsvr32", "schtasks",
    };

    /// <summary>Executable name without extension to sample RAM for, or null for generic Windows shell hosts.</summary>
    internal static string? TrackableProcessName(string command, string? program)
    {
        if (program is null) return null;
        var names = Helpers.StartupApps.ExecutableNames(command);
        var name = Path.GetFileNameWithoutExtension(names.Count > 0 ? names[^1] : program);
        return string.IsNullOrEmpty(name) || GenericShellHosts.Contains(name) ? null : name;
    }

    /// <summary>
    /// The memory each running entry's program holds now (private bytes, all its processes), by entry id: what it costs
    /// while a game runs. The program a launcher hands off to counts (Discord behind Update.exe), never a Store app's or a shared Windows shell host's.
    /// </summary>
    private static Dictionary<string, long> Running(IReadOnlyList<StartupEntry> entries)
    {
        var memory = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => entry.Enabled && entry.Program is not null))
        {
            if (TrackableProcessName(entry.Command, entry.Program) is not { } name) continue;
            long bytes = 0;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try { bytes += process.PrivateMemorySize64; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            if (bytes > 0) memory[entry.Id] = bytes;
        }
        return memory;
    }
}
