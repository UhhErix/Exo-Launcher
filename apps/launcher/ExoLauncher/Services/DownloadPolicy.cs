using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Settings > Downloads, applied: downloads wait while a game runs and
/// outside the download hours (<see cref="LaunchOrchestrator.Hold"/>), and
/// games from the stores Exo downloads itself (Epic, GOG, Amazon) update by
/// themselves once the PC is really idle (<see cref="PcIdle"/>: nobody at it,
/// nothing playing, no call, no other program at work, no game, not on battery),
/// and so does AMD's or Intel's chipset driver (<see cref="ChipsetUpdate"/>).
/// Steam updates and pauses its own downloads.
/// </summary>
internal sealed class DownloadPolicy : IDisposable
{
    private const string Playing = "playing";
    private const string Hours = "hours";

    private readonly AppServices _services;
    private readonly object _gate = new();
    private Timer? _tick;
    private Timer? _accounts;
    private DateTime _lastUpdateSweep = DateTime.MinValue;

    private bool _accountsArmed;

    internal DownloadPolicy(AppServices services) => _services = services;

    internal void Start()
    {
        var launcher = _services.Launcher;
        launcher.GameLaunched += OnGameLaunched;
        launcher.GameSessionCompleted += OnGameEnded;
        launcher.DownloadQueueChanged += SyncAccountTimer;
        launcher.ProgressChanged += OnDownloadProgress;
        lock (_gate)
        {
            _tick ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
            // A switch made in the store's own app is seen within seconds; armed only while downloads exist.
            _accounts ??= new Timer(_ => CheckAccounts(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        SyncAccountTimer();
        Apply();
    }

    /// <summary>The settings changed: holds follow them at once.</summary>
    internal void Apply()
    {
        var settings = _services.Settings.Current;
        if (!settings.PauseDownloadsWhilePlaying) _services.Launcher.Release(Playing);
        ApplyHours(settings);
    }

    private void OnGameLaunched(GameEntry game)
    {
        // Only a game holds downloads: an application or tool started from the Library does not.
        if (_services.Settings.Current.PauseDownloadsWhilePlaying && HoldsDownloads(game))
            _services.Launcher.Hold(Playing, $"Downloads are paused while you play {game.Title}.");
    }

    private void OnGameEnded(GameEntry game)
    {
        // Another game may still run; the next tick releases once none does.
        if (!GamePlaying()) _services.Launcher.Release(Playing);
    }

    /// <summary>A game Exo started still runs or starts (an application or tool it started does not count).</summary>
    private bool GamePlaying() => _services.Launcher.AnyRunning(HoldsDownloads);

    /// <summary>
    /// Whether playing this pauses downloads: a game does; what Steam calls an application, a tool, a soundtrack or a video
    /// (Wallpaper Engine, a benchmark, an editor), started from the Library, does not. Store apps, Exo's own windows and
    /// programs opened outside Exo never reach here: only Exo's own Play raises GameLaunched.
    /// </summary>
    internal static bool HoldsDownloads(GameEntry game) =>
        game.Store != StoreKind.Steam ||
        (game.SteamAppType?.Trim().ToLowerInvariant() ?? "") is not ("application" or "tool" or "config" or "music" or "video"
            or "series" or "episode" or "hardware" or "advertising" or "dedicated server" or "dedicatedserver" or "dedicated_server");

    private void OnDownloadProgress(InstallProgress _) => SyncAccountTimer();

    private void SyncAccountTimer()
    {
        var launcher = _services.Launcher;
        var active = launcher.IsBusy || launcher.HasQueuedJobs || launcher.PausedDownloads.Count > 0;
        lock (_gate)
        {
            if (_accounts is null || active == _accountsArmed) return;
            _accountsArmed = active;
            try
            {
                _accounts.Change(
                    active ? TimeSpan.FromSeconds(5) : Timeout.InfiniteTimeSpan,
                    active ? TimeSpan.FromSeconds(5) : Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }
        }
    }

    private void CheckAccounts()
    {
        var launcher = _services.Launcher;
        if (!launcher.IsBusy && !launcher.HasQueuedJobs && launcher.PausedDownloads.Count == 0)
        {
            SyncAccountTimer();
            return;
        }
        try { launcher.CheckDownloadAccounts(); }
        catch (Exception ex) { AppLog.Debug("Download account check failed: " + ex.Message); }
    }

    private void Tick()
    {
        try
        {
            var settings = _services.Settings.Current;
            if (!GamePlaying()) _services.Launcher.Release(Playing);
            ApplyHours(settings);
            if (settings.AutoUpdateGames) SweepUpdates();
            if (settings.AutoUpdateChipset) UpdateChipset();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Download policy tick failed: " + ex.Message);
        }
    }

    private void ApplyHours(AppSettings settings)
    {
        if (settings.DownloadHoursStart is not { } start || settings.DownloadHoursEnd is not { } end || InHours(DateTime.Now.Hour, start, end))
        {
            _services.Launcher.Release(Hours);
            return;
        }
        _services.Launcher.Hold(Hours, $"Downloads start at {Clock(start)}, your download hours.");
    }

    /// <summary>9 to 17 is a day window; 1 to 7, or 22 to 6, wraps past midnight.</summary>
    internal static bool InHours(int hour, int start, int end) =>
        start < end ? hour >= start && hour < end : hour >= start || hour < end;

    internal static string Clock(int hour) =>
        new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", System.Globalization.CultureInfo.CurrentCulture).Replace(" ", " ");

    /// <summary>Queues the updates Exo downloads itself, at most every 30 minutes, only while idle and nothing else downloads.</summary>
    private void SweepUpdates()
    {
        var launcher = _services.Launcher;
        if (DateTime.UtcNow - _lastUpdateSweep < TimeSpan.FromMinutes(30)) return;
        if (launcher.AnyGameRunning || launcher.HoldReason is not null || !Idle()) return;
        // A paused download waits for the user; it must not turn auto-update off for every other game.
        if (launcher.HasQueuedJobs || launcher.IsBusy) return;
        _lastUpdateSweep = DateTime.UtcNow;
        var paused = launcher.PausedDownloads.Select(p => p.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var due = _services.Library.PeekCachedLibrary()
            .Where(g => g.Installed && g.UpdateAvailable && LaunchOrchestrator.ExoDownloads(g.Store) && !paused.Contains(g.Id) &&
                        !_services.Settings.AutoUpdateOff(g.Id))
            .ToList();
        if (due.Count == 0) return;
        AppLog.Info($"Auto-update while idle: {due.Count} game(s): {string.Join(", ", due.Select(g => g.Title))}");
        foreach (var game in due)
            _ = Task.Run(async () =>
            {
                try { await launcher.UpdateAsync(game).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Info($"Auto-update of {game.Title} failed: {ex.Message}"); }
            });
    }

    /// <summary>AMD's or Intel's chipset driver, in the same idle window as games (<see cref="ChipsetUpdate"/> adds its own gates).</summary>
    private void UpdateChipset()
    {
        if (!Idle() || ChipsetUpdate.ThisPc is not { } chipset || chipset.Installed() is null) return;
        // The maker's page at most every 12 hours; this tick uses the answer once it's in.
        var latest = chipset.LatestAsync();
        if (latest.IsCompletedSuccessfully) chipset.Tick(true, idle: true, latest.Result, ChipsetUpdate.BootedAt());
    }

    /// <summary>
    /// The PC is really idle (<see cref="PcIdle"/>), with Exo's own word on a game running: one it launched, or one the
    /// run-state poll found (a game started from its store and played on a controller is minutes without input, and would
    /// otherwise start PcIdle's two-second sampler for the whole game).
    /// </summary>
    private bool Idle() => PcIdle.Now(() => _services.Launcher.AnyGameRunning || LeanSignal.Active);

    public void Dispose()
    {
        var launcher = _services.Launcher;
        launcher.GameLaunched -= OnGameLaunched;
        launcher.GameSessionCompleted -= OnGameEnded;
        launcher.DownloadQueueChanged -= SyncAccountTimer;
        launcher.ProgressChanged -= OnDownloadProgress;
        Timer? tick;
        Timer? accounts;
        lock (_gate)
        {
            _accountsArmed = false;
            tick = _tick;
            accounts = _accounts;
            _tick = _accounts = null;
        }
        tick?.Dispose();
        accounts?.Dispose();
    }
}
