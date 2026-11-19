using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>What Exo remembers of a program while it's here, so it can still look for what it left once it's gone.</summary>
internal sealed record RememberedApp(string Id, string Name, string? Publisher, string? Location, string? ProductCode, List<string> Exes, bool Store, DateTime FirstSeen, DateTime? GoneAt = null, string? Care = null);

/// <summary>
/// PC › Apps' memory (apps-seen.json, native only): every program Exo has seen, for when one is uninstalled outside Exo (from
/// Windows' Settings, by its own uninstaller); when each exe last ran, since Windows' own record forgets within days; and the
/// leftovers the user chose to leave, so they aren't offered again.
/// </summary>
internal sealed class AppsMemory(string file)
{
    /// <summary>A program missing for less than this may be mid-update: an updater that removes and reinstalls.</summary>
    internal static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(5);

    internal sealed record Saved(List<RememberedApp> Apps, Dictionary<string, DateTime> Runs, HashSet<string> Left, DateTime? Started);

    internal static AppsMemory ForThisPc { get; } = new(Path.Combine(PathHelper.AppDataDir, "apps-seen.json"));

    private readonly Lock _gate = new();
    private Saved? _saved;

    private Saved State()
    {
        if (_saved is not null) return _saved;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is { Apps: not null, Runs: not null, Left: not null } read)
                return _saved = read with { Runs = new(read.Runs, StringComparer.OrdinalIgnoreCase), Left = new(read.Left, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Debug("Apps memory: not read back: " + ex.GetType().Name); }
        return _saved = new([], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), null);
    }

    private void Save(Saved state)
    {
        _saved = state;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Apps memory: not saved: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Today's programs against the last: a new one is remembered, one back again is no longer gone, and one missing is marked.
    /// What seems gone but has a program in its folder, or one with its name and publisher, was updated: it's that one now,
    /// and forgotten. <paramref name="exesOf"/> reads a program's exes once, when it is first seen.
    /// </summary>
    internal static List<RememberedApp> Compare(IReadOnlyList<RememberedApp> known, IReadOnlyList<InstalledProgram> now, DateTime at, Func<InstalledProgram, List<string>> exesOf)
    {
        var next = new List<RememberedApp>();
        foreach (var app in known)
        {
            if (now.FirstOrDefault(program => program.Id == app.Id) is { } here)
            {
                next.Add(app with { Name = here.Name, Publisher = here.Publisher, Location = here.Location ?? app.Location, ProductCode = here.ProductCode, GoneAt = null });
                continue;
            }
            var updated = now.Any(program => program.Id != app.Id && (
                program.Location is { } folder && app.Location is { } was && folder.TrimEnd('\\').Equals(was.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                WebHostBridge.BareName(program.Name).Equals(WebHostBridge.BareName(app.Name), StringComparison.OrdinalIgnoreCase) && string.Equals(program.Publisher, app.Publisher, StringComparison.OrdinalIgnoreCase)));
            if (!updated) next.Add(app.GoneAt is null ? app with { GoneAt = at } : app);
        }
        foreach (var program in now)
            if (known.All(app => app.Id != program.Id) && Worth(program))
                next.Add(new(program.Id, program.Name, program.Publisher, program.Location, program.ProductCode, exesOf(program), program.Store, at, null, program.Care));
        return next;
    }

    /// <summary>Exo, anti-cheat, drivers and runtimes are never looked for: what they leave isn't anyone's to clear.</summary>
    internal static bool Worth(InstalledProgram program) =>
        program.Care is not (InstalledPrograms.Cares.Exo or InstalledPrograms.Cares.AntiCheat or InstalledPrograms.Cares.Driver or InstalledPrograms.Cares.Runtime or InstalledPrograms.Cares.ExoUses)
        && !LeftoverScan.AntiCheatIn(program.Name) && !LeftoverScan.AntiCheatIn(program.Location);

    /// <summary>
    /// Whether a program is still really in its folder: an exe that isn't an uninstaller, updater or helper. A program that
    /// only lost its entry or its uninstaller may work fine, so it is never treated as gone; only asked for by hand.
    /// </summary>
    internal static bool StillHasProgram(string? folder)
    {
        try
        {
            return folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                .Any(exe => AppsUsage.Opened(Path.GetFileName(exe)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>What has been missing long enough to call gone.</summary>
    internal static List<RememberedApp> Gone(IEnumerable<RememberedApp> known, DateTime at) => known.Where(app => app.GoneAt is { } since && at - since >= GoneAfter).ToList();

    internal List<RememberedApp> Note(IReadOnlyList<InstalledProgram> now, Func<InstalledProgram, List<string>> exesOf)
    {
        lock (_gate)
        {
            var state = State();
            var at = DateTime.UtcNow;
            var apps = Compare(state.Apps, now, at, exesOf);
            Save(state with { Apps = apps, Started = state.Started ?? at });
            return Gone(apps, at);
        }
    }

    /// <summary>It was uninstalled here, or what it left was looked at: not to be offered again.</summary>
    internal void Forget(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var state = State();
            var gone = ids.ToHashSet();
            Save(state with { Apps = state.Apps.Where(app => !gone.Contains(app.Id)).ToList() });
        }
    }

    internal bool Left(string item)
    {
        lock (_gate) return State().Left.Contains(item);
    }

    /// <summary>The user chose to leave these: Exo stops offering them.</summary>
    internal void Leave(IEnumerable<string> items)
    {
        lock (_gate)
        {
            var state = State();
            foreach (var item in items) state.Left.Add(item);
            Save(state);
        }
    }

    /// <summary>When it was first seen, or when Exo started remembering runs if that's later: how long Exo can vouch it hasn't run.</summary>
    internal DateTime? WatchedSince(string id)
    {
        lock (_gate)
        {
            var state = State();
            if (state.Apps.FirstOrDefault(app => app.Id == id) is not { } app || state.Started is not { } started) return null;
            return app.FirstSeen > started ? app.FirstSeen : started;
        }
    }

    /// <summary>What ran, kept: Windows' record joined to what Exo already kept, the newest of each. Only an actual run is ever added.</summary>
    internal Dictionary<string, DateTime> Runs(IReadOnlyDictionary<string, DateTime> seen)
    {
        lock (_gate)
        {
            var state = State();
            var changed = false;
            foreach (var (path, at) in seen)
            {
                if (state.Runs.TryGetValue(path, out var known) && known >= at) continue;
                state.Runs[path] = at;
                changed = true;
            }
            if (changed) Save(state);
            return new(state.Runs, StringComparer.OrdinalIgnoreCase);
        }
    }
}
