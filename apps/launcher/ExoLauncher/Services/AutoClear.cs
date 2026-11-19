namespace ExoLauncher.Services;

/// <summary>What a clear did: what went, what's still there and why, and the record Put back undoes it by.</summary>
/// <paramref name="Point"/>: why what needed Windows' permission stayed when a restore point was asked for and none could be made.
internal sealed record Cleared(List<Leftover> Removed, List<Leftover> Left, bool Declined, string? Why, string? Undo, LeftoverSummary What, string? Point = null);

/// <summary>How an uninstall ended: whether the program is gone, and what its uninstaller said went wrong.</summary>
internal sealed record Uninstalled(bool Gone, string? Problem)
{
    /// <summary>The press on Uninstall covers what's left only when it finished as it should: gone, and with no problem said.</summary>
    internal bool MayClear => Gone && Problem is null;
}

/// <summary>
/// Pressing Uninstall is the consent for everything after it: once the program's own uninstaller has finished, what the scan
/// says is that program's goes by itself, to the Recycle Bin and a saved copy of its registry entries, so Put back undoes it.
/// Only ever <see cref="Leftover.Selected"/>: what's matched by name alone, shared, in use, a store's own folder, a driver (held
/// for a press of its own), and what the scan never lists at all (anti-cheat, boot drivers, Windows' own) aren't its leftovers. What programs already gone left has no
/// press behind it: it goes with the junk while the PC is idle (<see cref="JunkAutoClean"/>), only where the evidence says
/// truly gone (<see cref="AppsOrphans"/>), and only what needs no administrator; the rest waits for the next uninstall's prompt.
/// </summary>
internal static class AutoClear
{
    /// <summary>What goes by itself after an uninstall here.</summary>
    internal static List<Leftover> Its(IEnumerable<Leftover> found) => found.Where(item => item.Selected).ToList();

    /// <summary>What goes by itself after an uninstall outside Exo: no Windows prompt out of nowhere.</summary>
    internal static List<Leftover> ItsWithoutAsking(IEnumerable<Leftover> found) => found.Where(item => item.Selected && !item.Machine).ToList();

    /// <summary>What goes by itself with the junk while the PC is idle: tied to a program truly gone, and nothing Windows has to ask about.</summary>
    internal static List<Leftover> ForIdle(IEnumerable<Orphan> found) => found.Where(GoesWhileIdle).Select(orphan => orphan.Item).ToList();

    internal static bool GoesWhileIdle(Orphan orphan) => orphan.Truly && orphan.Item.Selected && !orphan.Item.Machine;

    /// <summary>The same but for Windows' prompt, which never comes by surprise: these wait for the next one.</summary>
    internal static List<Leftover> Waiting(IEnumerable<Orphan> found) => found.Where(orphan => orphan.Truly && orphan.Item.Selected && orphan.Item.Machine).Select(orphan => orphan.Item).ToList();

    /// <summary>An uninstall's own, and with them what waits, only when its own already need the prompt: never a prompt just for those.</summary>
    internal static List<Leftover> WithWaiting(List<Leftover> its, IEnumerable<Leftover> waiting) =>
        its.Any(item => item.Machine) ? [.. its, .. waiting.Where(item => !its.Any(mine => mine.IsKey == item.IsKey && string.Equals(mine.Path, item.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(mine.Value, item.Value, StringComparison.OrdinalIgnoreCase)))] : its;

    /// <summary>What the page still lists for a press: not what goes by itself, and only if one press would take something.</summary>
    internal static List<Orphan> StillWaiting(IReadOnlyCollection<Orphan> found)
    {
        var rest = found.Where(orphan => !GoesWhileIdle(orphan)).ToList();
        return rest.Any(orphan => orphan.Item.Selected) ? rest : [];
    }

    /// <summary>
    /// Runs the uninstaller and waits for the program to leave (a store's game goes in its app after the uninstaller hands it
    /// over). <paramref name="gone"/> only runs once it has left; a program still there, cancelled or failed, clears nothing.
    /// </summary>
    internal static async Task<T> AfterUninstallAsync<T>(Func<Task<string?>> uninstall, Func<Task<bool>> stillThere, Func<Task> pause, int tries,
        Func<Uninstalled, Task<T>> gone, Func<Uninstalled, T> still)
    {
        var problem = await uninstall().ConfigureAwait(true);
        for (var tried = 0; tried < tries && await stillThere().ConfigureAwait(true); tried++) await pause().ConfigureAwait(true);
        return await stillThere().ConfigureAwait(true) ? still(new(false, problem)) : await gone(new(true, problem)).ConfigureAwait(true);
    }

    /// <summary>
    /// Clears what was chosen: first the registry written down, then what this user may remove, then what only an administrator
    /// may, all in one prompt. <paramref name="admin"/> null leaves those alone; false means it was declined, and they stay.
    /// <paramref name="restore"/>: a System Restore point first, in the same prompt (<see cref="RestorePoint.Gated"/>); without
    /// one, nothing that needs the prompt goes. What this user may remove needs none: it goes to the Recycle Bin and a saved
    /// .reg, which Put back undoes.
    /// </summary>
    internal static async Task<Cleared> ClearAsync(LeftoverScan scan, string name, IReadOnlyList<Leftover> chosen, Func<IReadOnlyList<string>, Task<bool>>? admin, RemovalHistory history, DateTime now, bool automatic = false,
        bool restore = false)
    {
        var sizes = LeftoverScan.Sizes(chosen);
        var backup = scan.Backup(name, chosen);
        var removed = chosen.Where(item => !item.Machine).Where(scan.Delete).ToList();
        var machine = chosen.Where(item => item.Machine).ToList();
        var declined = false;
        var tasks = new List<RemovedTask>();
        var drivers = new List<RemovedDriver>();
        string? point = null;
        if (admin is not null)
        {
            // Files first, to the bin; then entries, services and tasks by name, each task written down just before it goes.
            var files = machine.Where(item => item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut).ToList();
            var saving = new List<(Leftover Item, RemovedTask Task)>();
            var commands = scan.RecycleAsAdmin(files).ToList();
            foreach (var item in machine.Except(files))
            {
                if (scan.AdminCommand(item) is not { } command) continue;
                if (item.Kind == LeftoverKind.Task && scan.TaskExe(item.Path) is { } exe && scan.TaskBackupFile(name, item.Path) is { } file &&
                    LeftoverScan.TaskExportStep(item.Path, file) is { } export)
                {
                    commands.Add(export);
                    saving.Add((item, new RemovedTask(item.Path, exe, file)));
                }
                commands.Add(command);
            }
            if (commands.Count > 0)
            {
                var (ran, made) = restore ? await RestorePoint.BehindAsync(commands, admin).ConfigureAwait(false) : (await admin(commands).ConfigureAwait(false), null);
                if (ran)
                {
                    if (made is { } outcome && RestorePoint.Problem(outcome) is { } problem) point = problem.Replace("Nothing was removed.", "What needs Windows' permission stayed.", StringComparison.Ordinal);
                    removed.AddRange(machine.Where(item => !scan.Exists(item)));
                    tasks.AddRange(saving.Where(pair => removed.Contains(pair.Item) && File.Exists(pair.Task.File)).Select(pair => pair.Task));
                    // A driver's store package, exported before it went: Put back adds it again.
                    foreach (var item in removed.Where(item => item.Kind == LeftoverKind.Driver))
                        if (scan.DriverBackups.TryGetValue(item, out var saved) && DriverSaved(saved.Folder)) drivers.Add(new RemovedDriver(saved.Folder, [.. saved.Infs]));
                }
                else declined = true;
            }
        }
        var left = chosen.Where(scan.Exists).ToList();
        var what = LeftoverScan.Summarize(removed, sizes);
        var undo = what.Count == 0 ? null : history.Add(name, what,
            removed.Where(item => !item.IsKey && item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut).Select(item => new RemovedPath(item.Path, item.Machine)),
            backup, removed.Where(item => item.IsKey).Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase), now, automatic, tasks, drivers);
        return new(removed, left, declined, scan.Why(left), undo, what, point);
    }

    private static bool DriverSaved(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
