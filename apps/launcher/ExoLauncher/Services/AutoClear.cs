namespace ExoLauncher.Services;

internal sealed record Cleared(List<Leftover> Removed, List<Leftover> Left, bool Declined, string? Why, string? Undo, LeftoverSummary What, string? Point = null);

internal sealed record Uninstalled(bool Gone, string? Problem)
{
    internal bool MayClear => Gone && Problem is null;
}

internal static class AutoClear
{
    internal static List<Leftover> Its(IEnumerable<Leftover> found) => found.Where(item => item.Selected).ToList();

    internal static List<Leftover> ForUserUninstall(IEnumerable<Leftover> found) =>
        found.Where(item => item.Selected || (!item.Loose && item.Held is null && item.Kind != LeftoverKind.Driver)).ToList();

    internal static List<Leftover> ItsWithoutAsking(IEnumerable<Leftover> found) => found.Where(item => item.Selected && !item.Machine).ToList();

    internal static List<Leftover> ForIdle(IEnumerable<Orphan> found) => found.Where(GoesWhileIdle).Select(orphan => orphan.Item).ToList();

    internal static bool GoesWhileIdle(Orphan orphan) => orphan.Truly && orphan.Item.Selected && !orphan.Item.Machine;

    internal static List<Leftover> Waiting(IEnumerable<Orphan> found) => found.Where(orphan => orphan.Truly && orphan.Item.Selected && orphan.Item.Machine).Select(orphan => orphan.Item).ToList();

    internal static List<Leftover> WithWaiting(List<Leftover> its, IEnumerable<Leftover> waiting) =>
        its.Any(item => item.Machine) ? [.. its, .. waiting.Where(item => !its.Any(mine => mine.IsKey == item.IsKey && string.Equals(mine.Path, item.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(mine.Value, item.Value, StringComparison.OrdinalIgnoreCase)))] : its;

    internal static List<Orphan> StillWaiting(IReadOnlyCollection<Orphan> found)
    {
        var rest = found.Where(orphan => !GoesWhileIdle(orphan)).ToList();
        return rest.Any(orphan => orphan.Item.Selected) ? rest : [];
    }

    internal static async Task<T> AfterUninstallAsync<T>(Func<Task<string?>> uninstall, Func<Task<bool>> stillThere, Func<Task> pause, int tries,
        Func<Uninstalled, Task<T>> gone, Func<Uninstalled, T> still)
    {
        var problem = await uninstall().ConfigureAwait(true);
        for (var tried = 0; tried < tries && await stillThere().ConfigureAwait(true); tried++) await pause().ConfigureAwait(true);
        return await stillThere().ConfigureAwait(true) ? still(new(false, problem)) : await gone(new(true, problem)).ConfigureAwait(true);
    }

    internal static async Task<Cleared> ClearAsync(LeftoverScan scan, string name, IReadOnlyList<Leftover> chosen, Func<IReadOnlyList<string>, Task<bool>>? admin, RemovalHistory history, DateTime now, bool automatic = false,
        bool restore = false, bool requireRestorePoint = true)
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
                var (ran, made) = restore ? await RestorePoint.BehindAsync(commands, admin, requireRestorePoint).ConfigureAwait(false) : (await admin(commands).ConfigureAwait(false), null);
                if (ran)
                {
                    if (requireRestorePoint && made is { } outcome && RestorePoint.Problem(outcome) is { } problem)
                        point = problem.Replace("Nothing was removed.", "What needs Windows' permission stayed.", StringComparison.Ordinal);
                    removed.AddRange(machine.Where(item => !scan.Exists(item)));
                    tasks.AddRange(saving.Where(pair => removed.Contains(pair.Item) && File.Exists(pair.Task.File)).Select(pair => pair.Task));
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
