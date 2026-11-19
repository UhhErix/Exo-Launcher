using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// One thing a program already gone left. From: which program, empty when only a pointer at a missing exe found it. Truly: the
/// evidence says that program really is gone, on a drive that's here, so it may go without anyone asking.
/// </summary>
internal sealed record Orphan(Leftover Item, string From, bool Truly);

/// <summary>
/// What's left of programs already gone: read for PC › Apps, and cleared by itself with the junk while the PC is idle
/// (<see cref="JunkAutoClean"/>). The evidence: a program Exo remembers whose Windows entry has gone, or an entry whose
/// uninstaller has, and in both cases a folder that's gone or empty on a drive that's here; or a shortcut, service, task or App
/// Path pointing at an exe that isn't there on a drive that is (<see cref="LeftoverScan.FindDead"/>). Anything else (a folder
/// with files in it, a program still in its folder) isn't <see cref="Orphan.Truly"/> or isn't found at all.
/// </summary>
internal sealed class AppsOrphans(AppServices services)
{
    /// <summary>The name a clear of old leftovers goes by in Removed.</summary>
    internal const string Name = "Old leftovers";

    /// <summary>Every program but games: a game is uninstalled from its page in Library, by its store. A game is one in Library's folders, or a store's own entry for one.</summary>
    internal List<InstalledProgram> Programs()
    {
        var games = Games().Select(game => game.TrimEnd('\\') + '\\').ToList();
        return InstalledPrograms.List().Where(program => !IsGame(program, games)).ToList();
    }

    private static bool IsGame(InstalledProgram program, List<string> games) =>
        program.Uninstall is { } command && new[] { "steam://uninstall/", "--uninstall-product=", "uplay://uninstall/" }.Any(link => command.Contains(link, StringComparison.OrdinalIgnoreCase)) ||
        program.Key is { } key && new[] { @"\Steam App ", @"\Uplay Install " }.Any(store => key.Contains(store, StringComparison.OrdinalIgnoreCase)) ||
        program.Location is { } folder && games.Any(game => (folder.TrimEnd('\\') + '\\').StartsWith(game, StringComparison.OrdinalIgnoreCase));

    internal List<string> Games() => services.Library.PeekCachedLibrary().Where(game => !string.IsNullOrWhiteSpace(game.Path)).Select(game => game.Path!).ToList();

    // What the games here and Exo EQ need (AppsKeep), read again after five minutes: a game's runtimes come from its files.
    private (DateTime At, AppsKeep.Needs Needs)? _keepNeeds;

    internal AppsKeep.Needs KeepNeeds()
    {
        if (_keepNeeds is { } known && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(5)) return known.Needs;
        var installed = services.Library.PeekCachedLibrary().Where(game => game.Installed).ToList();
        var runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in installed)
            foreach (var id in DependencyService.RuntimesFor(game)) runtimes.TryAdd(id, game.Title);
        var needs = new AppsKeep.Needs(installed.GroupBy(game => game.Store).ToDictionary(group => group.Key, group => group.Count()), runtimes,
            ExoEq.Current.Outputs.Values.Any(output => output.Headphone is not null || output.Sound != ExoEqSounds.Flat));
        _keepNeeds = (DateTime.UtcNow, needs);
        return needs;
    }

    // An install folder named for what it holds, not for the app (Edge's is ...\Edge\Application): never a name to look for.
    private static readonly HashSet<string> FolderWords = new(StringComparer.OrdinalIgnoreCase) { "Application", "App", "Apps", "bin", "Program", "Current", "Client", "Launcher", "x64", "x86" };

    private static List<string> NamesOf(string? home, string name)
    {
        var names = new List<string>();
        if (home is not null && Path.GetFileName(home.TrimEnd('\\')) is { } folder && !FolderWords.Contains(folder)) names.Add(folder);
        if (WebHostBridge.BareName(name) is { Length: > 0 } bare) names.Add(bare);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A program's exes once, when it's first remembered: what its crash reports are named for.</summary>
    internal static List<string> ExesOf(InstalledProgram program)
    {
        if (program.Store || program.Location is null || !InstalledPrograms.Own(program.Location)) return [];
        try
        {
            return Directory.Exists(program.Location)
                ? Directory.EnumerateFiles(program.Location, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                    .Select(Path.GetFileName).OfType<string>().Where(exe => !exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase)).Take(40).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Who a program is, read before it goes: its folder (the deep clean's sure lead), its names (its folder's, and its
    /// own without version or bitness: leads by name only), the executables in its folder (crash reports), its publisher,
    /// its MSI product code, a Store app's family, and what else is installed (kept, with what holds it).
    /// </summary>
    internal Trail TrailFor(InstalledProgram program)
    {
        var home = program.Store || program.Location is null || !InstalledPrograms.Own(program.Location) ? null : program.Location;
        var exes = new List<string>();
        if (home is not null)
        {
            try
            {
                exes.AddRange(Directory.EnumerateFiles(home, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                    .Select(Path.GetFileName).OfType<string>()
                    .Where(exe => !exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !exe.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase))
                    .Take(40));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var others = InstalledPrograms.List().Where(other => other.Id != program.Id && !other.Store && other.Location is not null).Select(other => other.Location!);
        return new Trail(program.Name)
        {
            Homes = home is null ? [] : [home],
            Names = NamesOf(home, program.Name),
            Exes = exes,
            Ids = program.Store && program.Id.StartsWith("store:", StringComparison.Ordinal) ? [program.Id[6..] + '_'] : [],
            Entries = !program.Store && program.Key is { } key ? [key] : [],
            DisplayNames = program.Store ? [] : [program.Name],
            Publisher = program.Publisher,
            ProductCode = program.ProductCode,
            HomeHeld = program.Care == InstalledPrograms.Cares.Store ? StoreHome : null,
            Keep = [.. others, .. Games(), PathHelper.AppDataDir, .. Adapters.RecompInstallations.DataFolders(), .. EdgeRemoval.IsEdge(program) ? EdgeRemoval.Shared() : []],
        };
    }

    internal const string StoreHome = "Your games and saves may be inside";

    /// <summary>What's left of programs already gone on this PC now, and the remembered programs it came from.</summary>
    internal (List<Orphan> Found, List<string> Remembered) Read(LeftoverScan scan)
    {
        var programs = Programs();
        var games = Games();
        var needs = KeepNeeds();
        var memory = AppsMemory.ForThisPc;
        var gone = memory.Note(programs, ExesOf).Where(app => !IsGameFolder(app.Location, games)).ToList();
        var broken = programs.Where(program => program.Removable && !program.Store && program.Replaces is null && !(program.Uninstall is { } command && WebHostBridge.UninstallerThere(command))
            && AppsKeep.Why(program, needs) is null && !AppsMemory.StillHasProgram(program.Location)).ToList();
        var keep = new List<string>([.. programs.Where(program => !program.Store && program.Location is not null).Select(program => program.Location!), .. games, PathHelper.AppDataDir, .. Adapters.RecompInstallations.DataFolders()]);
        var (found, remembered, nothing) = Gather(scan, gone, broken.Select(program => (program, TrailFor(program) with { Entries = program.Key is { } key ? [key] : [] })).ToList(), keep, memory.Left);
        memory.Forget(nothing);
        return (found, remembered);
    }

    private static bool IsGameFolder(string? folder, List<string> games) =>
        folder is not null && games.Any(game => (folder.TrimEnd('\\') + '\\').StartsWith(game.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What each kind of evidence finds, and how far it can be trusted. Remembered programs still in their folders are left
    /// out; a program's folder that's gone or empty on a drive that's here makes all of it <see cref="Orphan.Truly"/>; what points
    /// at a missing exe is, by what it is. What the user chose to leave is never found again. Nothing: remembered programs that left nothing.
    /// </summary>
    internal static (List<Orphan> Found, List<string> Remembered, List<string> Nothing) Gather(LeftoverScan scan, IReadOnlyList<RememberedApp> gone,
        IReadOnlyList<(InstalledProgram Program, Trail Trail)> broken, IReadOnlyList<string> keep, Func<string, bool> left)
    {
        var found = new List<Orphan>();
        var remembered = new List<string>();
        var nothing = new List<string>();
        foreach (var app in gone.Where(app => !AppsMemory.StillHasProgram(app.Location)))
        {
            var home = app.Store || app.Location is null || !OwnFolder(app.Location, keep) ? null : app.Location;
            var truly = home is not null && scan.GoneOrEmpty(home);
            var items = scan.Find(new Trail(app.Name)
            {
                Homes = home is null ? [] : [home],
                Names = NamesOf(home, app.Name),
                Exes = app.Exes,
                Ids = app.Store && app.Id.StartsWith("store:", StringComparison.Ordinal) ? [app.Id[6..] + '_'] : [],
                DisplayNames = app.Store ? [] : [app.Name],
                Publisher = app.Publisher,
                ProductCode = app.ProductCode,
                HomeHeld = app.Care == InstalledPrograms.Cares.Store ? StoreHome : null,
                Keep = keep,
            }, deep: true);
            if (items.Count == 0) { nothing.Add(app.Id); continue; }
            remembered.Add(app.Id);
            found.AddRange(items.Select(item => new Orphan(item, app.Name, truly)));
        }
        foreach (var (program, trail) in broken)
        {
            var truly = program.Location is { } folder && OwnFolder(folder, keep) && scan.GoneOrEmpty(folder);
            found.AddRange(scan.Find(trail, deep: true).Select(item => new Orphan(item, program.Name, truly)));
        }
        found.AddRange(scan.FindDead(keep).Select(item => new Orphan(item, "", true)));
        // The same thing found twice: the surest finding of it, and none inside another that goes.
        var unique = found.Where(orphan => !left(Remembered(orphan.Item)))
            .GroupBy(orphan => (orphan.Item.Path.ToUpperInvariant(), orphan.Item.Value?.ToUpperInvariant())).Select(same => same.OrderByDescending(orphan => orphan.Truly).First()).ToList();
        var outermost = LeftoverScan.Outermost(unique.Select(orphan => orphan.Item).ToList());
        return (unique.Where(orphan => outermost.Contains(orphan.Item)).ToList(), remembered, nothing);
    }

    private static bool OwnFolder(string folder, IReadOnlyList<string> keep) =>
        folder.TrimEnd('\\').Count(c => c == '\\') >= 2 && !keep.Any(other => string.Equals(other.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>A leftover as Leave remembers it.</summary>
    internal static string Remembered(Leftover item) => $"{item.Path}|{item.Value}";

    /// <summary>
    /// The idle run: what the evidence ties to a program truly gone goes, as far as it needs no administrator, under one record
    /// Put back undoes. <paramref name="stillIdle"/> is asked again after the scan, which takes a moment: someone may be back.
    /// </summary>
    internal void ClearWhileIdle(Func<bool> stillIdle)
    {
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var (found, _) = Read(scan);
        var chosen = AutoClear.ForIdle(found);
        if (chosen.Count == 0 || !stillIdle()) return;
        var done = AutoClear.ClearAsync(scan, Name, chosen, admin: null, RemovalHistory.ForThisPc, DateTime.UtcNow, automatic: true).GetAwaiter().GetResult();
        AppLog.Info($"Apps: cleared {done.Removed.Count} old leftovers by itself; {done.Left.Count} stayed{(done.Why is null ? "" : $" ({done.Why})")}.");
    }
}
