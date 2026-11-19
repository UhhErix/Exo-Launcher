using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The junk that goes by itself (<see cref="JunkFiles"/>), so nobody cleans it by hand. Each day: temporary files a day
/// old, crash dumps and error reports, Explorer's thumbnail cache, and Exo's old app backups once the version that replaced
/// them has run for <see cref="BackupKept"/> (the way back from a bad update). Each week (<see cref="Weekly"/>): old
/// (shader caches are Exo Shaders' alone and are never cleaned here) and what has
/// sat in the Recycle Bin for <see cref="BinKept"/>, as Windows' Storage Sense would. The whole Recycle Bin stays a press.
/// Old leftovers of programs already gone go the same way, at most once a day (<see cref="AppsOrphans.ClearWhileIdle"/>).
/// It runs only while the PC is
/// really idle (<see cref="PcIdle"/>: nobody at it, nothing playing, no call, no other program at work, no game, not on
/// battery) and no game, Discord call, download, install or update of Exo's runs; at most once a day
/// (<see cref="Every"/>), and at the next idle once a scan finds more than <see cref="Big"/> of it. Nothing it does asks
/// Windows for permission: what is in use or out of reach stays. What went is in app.log; when, in junk-clean.json.
/// </summary>
internal sealed class JunkAutoClean(AppServices services) : IDisposable
{
    /// <summary>
    /// The owner's switch for sweeping old leftovers of programs already gone while the PC is idle (<see cref="AppsOrphans.ClearWhileIdle"/>).
    /// False until it has been seen working on a real PC (3.23.0 shipped it checked in code only): with it off nothing walks the drive for
    /// leftovers by itself and nothing is removed without a press. The junk kinds below are unaffected, and so is Uninstall's own clear-up.
    /// </summary>
    internal const bool OrphansBySelf = false;

    /// <summary>The kinds that go each day, in the order they go.</summary>
    internal static readonly IReadOnlyList<string> Kinds = ["temp", "crash", "thumbs", "exo"];

    /// <summary>The kinds that go each week, after the day's: "bin-old" is the Recycle Bin's month-old items, never all of it.</summary>
    internal static readonly IReadOnlyList<string> WeeklyKinds = ["bin-old"];

    internal static readonly TimeSpan Every = TimeSpan.FromDays(1), Weekly = TimeSpan.FromDays(7), BackupKept = TimeSpan.FromDays(3), BinKept = TimeSpan.FromDays(30);
    internal const long Big = 1_000_000_000;

    /// <summary>A kind that goes by itself; every other one (the whole Recycle Bin), and any added later, waits for a press.</summary>
    internal static bool Auto(string id) => Kinds.Contains(id) || WeeklyKinds.Contains(id);

    /// <summary>A clean is due: none yet, a day since the last (or the clock set back before it), or a scan found a lot.</summary>
    internal static bool Due(DateTime? last, bool big, DateTime now) => big || last is not { } at || now - at >= Every || at > now;

    /// <summary>
    /// A scan found more than <see cref="Big"/> of what goes by itself. Exo's backups don't count: they go by age, and one
    /// still kept would make every scan call for a clean that leaves it.
    /// </summary>
    internal static bool BigFind(IEnumerable<JunkFiles.Found> found) => found.Where(item => Kinds.Contains(item.Id) && item.Id != "exo").Sum(item => item.Bytes) > Big;

    /// <summary>
    /// An old Exo backup goes once it has been one for <see cref="BackupKept"/>: an app.previous (made before the running
    /// app) since the running app arrived, an app.incoming (an update that never finished, made after it) since it was made.
    /// </summary>
    internal static bool BackupOld(DateTime made, DateTime appArrived, DateTime now) => now - (made > appArrived ? made : appArrived) >= BackupKept;

    /// <summary>An automatic clean: when (UTC) and how much went; WeekAt, when the weekly kinds last went (null before the first).</summary>
    internal sealed record Last(DateTime At, long Bytes, DateTime? WeekAt = null, DateTime? OrphansAt = null);

    /// <summary>
    /// Old leftovers go at most once a day (the clock set back counts as due), never with a game running and only while the PC
    /// is idle: <paramref name="clear"/> runs only past all three. Whether it ran.
    /// </summary>
    internal static bool ClearOrphansIfDue(DateTime? last, DateTime now, Func<bool> gameRunning, Func<bool> idle, Action clear)
    {
        if (last is { } at && now - at < Every && at <= now) return false;
        if (gameRunning() || !idle()) return false;
        clear();
        return true;
    }

    /// <summary>The weekly kinds are due: never gone yet, a week since, or the clock set back before it.</summary>
    internal static bool WeekDue(DateTime? weekAt, DateTime now) => weekAt is not { } at || now - at >= Weekly || at > now;

    private static string StatePath => Path.Combine(PathHelper.AppDataDir, "junk-clean.json");
    private static readonly Lazy<Last?> Saved = new(() =>
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<Last>(File.ReadAllText(StatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    });
    private static Last? _newer;
    private static int _big;

    /// <summary>The last automatic clean; null before the first.</summary>
    internal static Last? Latest() => Volatile.Read(ref _newer) ?? Saved.Value;

    /// <summary>junk.scan's finds: past <see cref="Big"/>, the next idle moment cleans.</summary>
    internal static void Scanned(IReadOnlyList<JunkFiles.Found> found)
    {
        if (BigFind(found)) Volatile.Write(ref _big, 1);
    }

    private AppsOrphans? _orphans;
    private Timer? _tick;
    private int _running;

    internal void Start() => _tick ??= new Timer(_ => Tick(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));

    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            AppsUsage.Remember();
            ShaderCacheWatch.Observe(services.Library.PeekCachedLibrary().Where(game => game.Installed), services.Shaders.For, DateTime.UtcNow);
            if (Due(Latest()?.At, Volatile.Read(ref _big) == 1, DateTime.UtcNow) && Quiet()) Clean();
            var now = DateTime.UtcNow;
            if (OrphansBySelf && ClearOrphansIfDue(Latest()?.OrphansAt, now, () => services.Launcher.AnyGameRunning || LeanSignal.Active || MainWindow.RunningLibraryGames().Count > 0, Quiet,
                    () => (_orphans ??= new AppsOrphans(services)).ClearWhileIdle(Quiet)))
                Remember((Latest() ?? new Last(DateTime.MinValue, 0)) with { OrphansAt = now });
        }
        catch (Exception ex) { AppLog.Warn("Junk: the automatic clean failed: " + ex.Message); }   // a timer thread never throws
        finally { Volatile.Write(ref _running, 0); }
    }

    /// <summary>Nothing holds it (cheapest first) and the PC is really idle.</summary>
    private bool Quiet()
    {
        var launcher = services.Launcher;
        return !launcher.AnyGameRunning && !LeanSignal.Active && DiscordClient.DiscordSession.Current?.InCall != true
            && !launcher.IsBusy && !launcher.HasQueuedJobs && !services.Updater.Installing && !services.Dependencies.Busy
            && !DriverInstallSignal.Installing && GpuDriverUpdate.Updating is null && !DriverCatalog.Shared.Running
            && NvidiaDriverService.Current.Stage is not ("downloading" or "unpacking" or "waiting" or "installing")
            && PcIdle.Now(() => launcher.AnyGameRunning || LeanSignal.Active);
    }

    private void Clean()
    {
        var now = DateTime.UtcNow;
        var appArrived = Directory.GetCreationTimeUtc(AppContext.BaseDirectory);
        var went = new List<string>();
        long bytes = 0;
        // What went is the change in free bytes on the system drive, measured, never the sum of the sizes.
        var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        var freeBefore = DeletionGate.FreeBytesOf(root);
        long Freed() => Math.Max(0, DeletionGate.FreeBytesOf(root) - freeBefore);
        string Said() => went.Count == 0 ? "nothing to delete" : $"{Freed() / 1_000_000} MB: {string.Join(", ", went)}";
        var weekAt = Latest()?.WeekAt;
        var week = WeekDue(weekAt, now);
        IReadOnlyList<string> kinds = week ? [.. Kinds, .. WeeklyKinds] : Kinds;
        for (var i = 0; i < kinds.Count; i++)
        {
            // Someone came back, or a game or a download started: the rest waits for the next idle moment.
            if (i > 0 && !Quiet())
            {
                AppLog.Info($"Junk: the automatic clean stopped, the PC is in use; {Said()}.");
                return;
            }
            var (gone, count) = kinds[i] switch
            {
                "exo" => JunkFiles.CleanExoBackups(folder => BackupOld(Directory.GetCreationTimeUtc(folder), appArrived, now)),
                "bin-old" => JunkFiles.CleanOldBin(BinKept),
                var kind => JunkFiles.Clean(kind),
            };
            if (count > 0) went.Add($"{Named(kinds[i])} {gone / 1_000_000} MB ({count} {(kinds[i] is "exo" ? "folders" : kinds[i] == "bin-old" ? "items" : "files")})");
            bytes += gone;
        }
        Volatile.Write(ref _big, 0);
        Remember(new Last(now, Freed(), week ? now : weekAt, Latest()?.OrphansAt));
        AppLog.Info($"Junk: cleaned by itself, {Said()}.");
    }

    private static void Remember(Last last)
    {
        Volatile.Write(ref _newer, last);
        try
        {
            File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(last));
            File.Move(StatePath + ".tmp", StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Junk: when it last cleaned wasn't saved: " + ex.Message); }
    }

    private static string Named(string id) => id switch
    {
        "temp" => "temporary files",
        "crash" => "crash dumps and reports",
        "thumbs" => "thumbnail cache",
        "bin-old" => "Recycle Bin items a month old",
        _ => "Exo's old backups",
    };

    public void Dispose()
    {
        _tick?.Dispose();
        _tick = null;
    }
}
