using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Riot;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// JSON-RPC bridge between the React UI (WebView2) and native services.
/// UI owns pixels; host owns discovery, install/progress, launch, and deps.
/// </summary>
public sealed partial class WebHostBridge
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _queue;
    private IHostTransport? _transport;
    private readonly DlssSwapService _dlss = new();
    private readonly FolderArtStore _folderArt = new();
    private readonly CancellationTokenSource _bridgeLifetime = new();
    private readonly BridgeRequestRegistry _rpcRequests = new();
    private readonly EarlyCancelTracker _earlyCancels = new();
    private readonly RpcTrace _trace = RpcTrace.Default;
    private bool _detached;
    private int _backgroundMonitorsStarted;
    private Action<UpdateFlow.State>? _updaterStateChanged;
    private Action? _libraryAntiCheatWarm;
    private Action<string, System.Text.Json.Nodes.JsonNode?>? _shadersHostEvent;
    internal Action? RequestHostShutdown { get; set; }

    private CancellationTokenSource? _keyWatchCts;
    private int _libraryPushScheduled;
    private int _libraryPushQueued;
    private long _libraryPushedAtMs;
    // The last library push's hash, not the push: the whole library's JSON (a large-object-heap string) stayed alive between pushes.
    private byte[]? _libraryPushedHash;
    private int _riotHoursRefreshing;
    // One timer for the run-state scan: due after an event (debounced), repeating only in the fallback (RunStatePlan).
    private System.Threading.Timer? _runStatePoll;
    private int _runStatePolling;
    private int _runStateQueued;
    private volatile bool _runStateUnwatched;
    private volatile bool _runStateSessionOnly;
    private long _sessionOnlySince; // Environment.TickCount64 of the first scan with a session open and no game process; 0 when none
    // Window events: the process image per pid and the installed folders as a set, rebuilt only when the library changes.
    private readonly GameWindowWatch _windowWatch;
    private readonly ConcurrentDictionary<int, byte> _exitWatches = new();
    private static readonly TimeSpan RunStateDebounce = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public WebHostBridge(AppServices services, DispatcherQueue queue)
    {
        _services = services;
        _queue = queue;
        SystemSpecs.WarmInBackground();
        // The adapter and the store opener start Steam without the bridge at hand; the waiting settings go in first.
        SteamPendingSettings.Attach(this);
        SteamSignIn.SteamWebsiteToken.Fetch = SteamWebsiteTokenAsync;
        _windowWatch = new GameWindowWatch(pid => ProcessHelper.TryGetExecutablePath((int)pid),
            new GameFolderSet(() => InstalledGames().Select(game => game.Path!).ToArray()));
        // The update's state as the host holds it (UpdateFlow): the UI draws the update from this alone.
        _updaterStateChanged = state => PostEvent("app.updateState", new { state = UpdateStateName(state) });
        _services.Updater.StateChanged += _updaterStateChanged;
        _services.Launcher.ProgressChanged += OnProgress;
        _services.Launcher.DownloadQueueChanged += OnDownloadQueueChanged;
        _services.Launcher.GameSessionCompleted += OnGameSessionCompleted;
        _services.Launcher.GameLaunched += OnGameLaunched;
        _services.Launcher.GameRunStateChanged += OnGameRunStateChanged;
        _services.Shaders.Changed += OnShadersChanged;
        _shadersHostEvent = PostShadersEvent;
        _services.ShadersHost.Event += _shadersHostEvent;
        _services.HdrHost.WarningRaised += OnHdrWarning;
        _services.ShaderPrepare.Changed += OnShadersChanged;
        ShaderRebuildWatch.Changed += OnShadersRebuildChanged;
        _services.Store.Changed += OnStoreChanged;
        LauncherBackup.StopAnnounced += OnBackupStopped;
        _services.Achievements.SummaryChanged += OnAchievementsChanged;
        _services.Store.SaleFound += OnStoreSale;
        _services.Library.LibraryUpdated += OnLibraryUpdated;
        // Play and Properties find each installed game's anti-cheat answer kept, walked behind everything else.
        _libraryAntiCheatWarm = () => AntiCheatCheck.Warm(_services.Library.PeekCachedLibrary());
        _services.Library.LibraryUpdated += _libraryAntiCheatWarm;
        _services.Library.LibraryUpdated += FollowSteamTransfers;
        _services.Library.LibraryUpdated += SteamOverlaySoon;
        InstalledSizeCache.MeasurementUpdated += OnLibraryUpdated;
        ExoComponents.FailedTwice += OnComponentFailed;
        GameSettings.AppliedAgain += OnOptimizedAgain;
        AutopilotWatch.Reported += OnAutopilotReported;
        AutopilotWatch.Shared.LiveChanged += OnAutopilotLiveChanged;
        FrameCapService.Shared.CapLabChanged += OnCapLabChanged;
        InitGpuAndDisplayCaches();
        GameSettings.WindowModeHeld += OnOptimizeHeld;
        Account.AccountSessionStore.Changed += OnAccountChanged;
        _runStatePoll = new System.Threading.Timer(_ => PollRunStates(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ForegroundWatcher.WindowActivity += OnWindowActivity;
        _services.Library.LibraryUpdated += OnLibraryChangedForGames;
        _services.Launcher.GameLaunched += OnGameLaunchedScan;
        _services.Launcher.GameLaunching += OnGameLaunchingAllowance;
        _services.Launcher.GameSessionCompleted += OnGameSessionEndedScan;
        _services.Launcher.GameLaunchFailed += OnGameSessionEndedScan;
        // One scan shortly after launch finds games already running; then only events (or the fallback) wake it.
        ArmRunStatePoll(TimeSpan.FromSeconds(10));
        _services.Launcher.GameSessionCompleted += ReapplyCalibrationsAfter;
        _services.Launcher.GameSessionCompleted += SteamOnlineAfterFamily;
        _services.Launcher.GameLaunching += ApplyGameProfile;
        _services.Launcher.LaunchNotice += OnLaunchNotice;
        _services.Dependencies.DeferredProgress += PostRuntimeProgress;
        _services.Dependencies.DeferredCompleted += PostRuntimeDone;
        GpuManualTuningService.Shared.StateChanged += PostGpuTuningChanged;
        GpuManualTuningService.Shared.TestProgressChanged += PostGpuTuningTestProgress;
    }

    /// <summary>
    /// Starts background peripheral, Steam client, shader prepare, version check, and EQ watchers after
    /// WebView navigation begins so constructor work stays off the critical first-paint path.
    /// </summary>
    internal void StartBackgroundMonitors()
    {
        if (_detached || Interlocked.Exchange(ref _backgroundMonitorsStarted, 1) != 0) return;
        WatchDevices();
        WatchGpuAndDisplayHardware();
        KeepSteamClientCurrent();
        KeepShadersPrepared();
        WatchVersions();
        WatchEq();
        ExoAiCoordinator.Shared.Start(() => _services.Settings.Current);
        _ = Task.Run(() => EpicQuietSettings.HandToExo(EpicQuietWanted()));
    }

    /// <summary>
    /// The run-state scan: one process snapshot matched against the installed games' folders, so a start or exit reaches
    /// game.runState (Exo Mouse's game profiles, Play/Stop) within moments. It runs after a game window shows or comes to
    /// the front (<see cref="OnWindowActivity"/>), after a watched game process exits (<see cref="WatchExitAsync"/>), after
    /// Exo launches a game, and every 15 s only when window events are unavailable (<see cref="RunStatePlan"/>). A request
    /// that lands during a scan is queued, not dropped.
    /// </summary>
    private void PollRunStates()
    {
        // Read before the detached check: Detach sets _detached before Exo's shutdown clears the signal, so a scan that
        // passes the check with this generation and publishes after the clear is dropped (GameRunningSignal.Clear).
        var generation = GameRunningSignal.Generation;
        if (Volatile.Read(ref _detached)) return;
        if (Interlocked.Exchange(ref _runStatePolling, 1) == 1)
        {
            Volatile.Write(ref _runStateQueued, 1);
            return;
        }
        try
        {
            var installed = InstalledGames();
            var states = installed.Length > 0 ? _services.Launcher.GetGameRunStates(installed, underRootOnly: true) : null;
            var runningIds = states?.Where(row => row.Value.IsRunning).Select(row => row.Key).ToHashSet(StringComparer.Ordinal) ?? [];
            _runStateUnwatched = false;
            foreach (var game in installed.Where(game => runningIds.Contains(game.Id)))
            {
                var pids = _services.Launcher.GameProcessIds(game);
                if (pids.Count == 0) _runStateUnwatched = true;
                foreach (var pid in pids) ArmExitWatch(pid);
            }
            var found = runningIds.Count > 0;
            var sessionOpen = _services.Launcher.AnyGameRunning;
            var now = Environment.TickCount64;
            if (found || !sessionOpen) Interlocked.Exchange(ref _sessionOnlySince, 0);
            else Interlocked.CompareExchange(ref _sessionOnlySince, now, 0);
            var since = Interlocked.Read(ref _sessionOnlySince);
            var sessionOnlyFor = since == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(now - since);
            var running = RunStatePlan.GameRunning(found, sessionOpen, sessionOnlyFor);
            if (!running && sessionOpen && GameRunningSignal.Running)
                AppLog.Info($"Game running signal: no game process for {sessionOnlyFor.TotalSeconds:0} s; the open session no longer counts.");
            _runStateSessionOnly = running && !found;
            GameRunningSignal.Update(running, generation);
            var playing = installed.Where(game => runningIds.Contains(game.Id) && !_services.ShaderPrepare.Preparing(game.Id)).ToArray();
            FpsCounter.Follow(_services.Launcher, playing);
            Autopilot.Follow(_services.Launcher, playing);
        }
        catch (Exception ex) { AppLog.Debug("Run-state poll failed: " + ex.GetType().Name); }
        finally
        {
            Interlocked.Exchange(ref _runStatePolling, 0);
            ArmRunStatePoll(Interlocked.Exchange(ref _runStateQueued, 0) == 1 ? RunStateDebounce : PeriodicOrNever());
        }
    }

    private GameEntry[] InstalledGames() => _services.Library.PeekCachedLibrary()
        .SelectMany(g => g.Variants.Count == 0 ? [g] : g.Variants
            .Where(v => v.Installed)
            .Select(v => v.ToGameEntry(g)))
        .Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path))
        .ToArray();

    private TimeSpan PeriodicOrNever() =>
        RunStatePlan.PeriodicEvery(ForegroundWatcher.ActivityHooked, _runStateUnwatched, _runStateSessionOnly) ?? Timeout.InfiniteTimeSpan;

    private void ArmRunStatePoll(TimeSpan due) => _runStatePoll?.Change(due, PeriodicOrNever());

    private void RequestRunStateScan(TimeSpan? delay = null)
    {
        if (_detached) return;
        Volatile.Write(ref _runStateQueued, 1);
        ArmRunStatePoll(delay ?? RunStateDebounce);
    }

    /// <summary>
    /// A top-level window showed or came to the front. Its process is resolved once (<see cref="_windowImages"/>), and a
    /// process whose image is in an installed game's folder asks for a scan. Limit: a game with no window (a dedicated
    /// server, a launcher-only helper) raises no window event. It is found by the scan on Exo's own launch
    /// (<see cref="OnGameLaunchedScan"/>) and tracked by its exit watch, but a windowless game that a store starts
    /// on its own is not seen until something else scans.
    /// </summary>
    private void OnWindowActivity(uint pid)
    {
        if (_detached || pid == 0 || pid == (uint)Environment.ProcessId) return;
        if (_windowWatch.IsGameWindow(pid)) RequestRunStateScan();
    }

    /// <summary>The library changed: the installed folders are read again on the next window event.</summary>
    private void OnLibraryChangedForGames() => _windowWatch.LibraryChanged();

    /// <summary>Exo launched a game: one scan a few seconds on, once its process has appeared.</summary>
    private void OnGameLaunchedScan(GameEntry game) => RequestRunStateScan(TimeSpan.FromSeconds(5));

    /// <summary>A launch begins: its session gets its own <see cref="RunStatePlan.SessionOnlyLimit"/>, not what an
    /// earlier session left of it.</summary>
    private void OnGameLaunchingAllowance(GameEntry game) => Interlocked.Exchange(ref _sessionOnlySince, 0);

    /// <summary>
    /// A session Exo started closed (the game ended, or its launch never reached it). The exit watch's scan ran inside
    /// the session's exit debounce, while the session still counted the game as running; this scan is the one that
    /// clears the signal.
    /// </summary>
    private void OnGameSessionEndedScan(GameEntry game) => RequestRunStateScan();

    private void ArmExitWatch(int pid)
    {
        if (_exitWatches.TryAdd(pid, 0)) _ = WatchExitAsync(pid);
    }

    /// <summary>
    /// Waits on the process handle, no polling. When it exits, a scan decides whether the game is still running. A process
    /// that can't be opened (access denied) marks the run unwatched, so the 15 s fallback covers it.
    /// </summary>
    private async Task WatchExitAsync(int pid)
    {
        var lifetime = _bridgeLifetime.Token;
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(lifetime);
        }
        catch (ArgumentException) { /* already exited: the scan sees it */ }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            AppLog.Debug("Game exit watch unavailable: " + ex.GetType().Name);
            _runStateUnwatched = true;
            ArmRunStatePoll(RunStatePlan.FallbackEvery);
            return;
        }
        finally { _exitWatches.TryRemove(pid, out _); }
        RequestRunStateScan();
    }

    private async Task<object> SaveWebPreferencesAsync(JsonElement patch)
    {
        var preferences = WebClientPreferences.Save(patch);
        if (preferences.MusicService is { } service) _services.MusicEngine.SelectService(service);
        if (new[] { "microphone", "camera", "clipboard", "notifications" }.Any(key => patch.TryGetProperty(key, out _)))
            await _services.MusicEngine.ResetPermissionGrantsAsync().ConfigureAwait(false);
        return preferences;
    }

    public void Attach(CoreWebView2 web)
    {
        ObjectDisposedException.ThrowIf(_detached, this);
        Attach(new WebViewHostTransport(web, _queue));
    }

    public void Attach(IHostTransport transport)
    {
        ObjectDisposedException.ThrowIf(_detached, this);
        HangWatchdog.PageAttached();
        _services.GogAuth.AttachDispatcher(_queue);
        _services.EpicAuth.AttachDispatcher(_queue);
        _services.MusicEngine.Attach(_queue, PostEvent);
        _services.MousePanel.Attach(_queue, this);
        _services.HeadlessMusic.Attach(PostEvent, () => _services.MusicEngine.IsActive || App.MainAppWindow is not null);
        _services.HeadlessCapture.Attach(PostCaptureEvent);
        if (Interlocked.Exchange(ref _transport, transport) is { } previous)
        {
            previous.MessageReceived -= OnMessage;
            previous.SessionReset -= OnTransportReset;
            try { previous.Detach(); } catch { }
        }
        transport.MessageReceived += OnMessage;
        transport.SessionReset += OnTransportReset;
    }

    private void OnTransportReset() => UiGuard.Run(() =>
    {
        _rpcRequests.CancelAll();
        _earlyCancels.Clear();
        // A new session has none of the rows the last push carried.
        Volatile.Write(ref _libraryPushedHash, null);
        try { Interlocked.Exchange(ref _keyWatchCts, null)?.Cancel(); } catch { }
        // A reloaded session never turns off the controller's live view it asked for (60 events a second, up to 10 minutes).
        StopWatch(Interlocked.Exchange(ref _controllerWatch, null));
        _services.MousePanel.Close();
    });

    public void Detach()
    {
        if (_detached) return;
        Volatile.Write(ref _detached, true);
        _services.MousePanel.Close();
        _services.HeadlessMusic.Detach();
        _services.HeadlessCapture.Detach();
        try { _bridgeLifetime.Cancel(); } catch { }
        try { Interlocked.Exchange(ref _keyWatchCts, null)?.Cancel(); } catch { }
        StopWatch(Interlocked.Exchange(ref _controllerWatch, null));
        if (Interlocked.Exchange(ref _transport, null) is { } transport)
        {
            transport.MessageReceived -= OnMessage;
            transport.SessionReset -= OnTransportReset;
            try { transport.Detach(); } catch { }
        }
        SteamPendingSettings.Detach(this);
        if (_updaterStateChanged is { } updaterChanged) _services.Updater.StateChanged -= updaterChanged;
        _updaterStateChanged = null;
        _services.Launcher.ProgressChanged -= OnProgress;
        _services.Launcher.DownloadQueueChanged -= OnDownloadQueueChanged;
        _services.Launcher.GameSessionCompleted -= OnGameSessionCompleted;
        _services.Launcher.GameSessionCompleted -= ReapplyCalibrationsAfter;
        _services.Launcher.GameSessionCompleted -= SteamOnlineAfterFamily;
        _services.Launcher.GameLaunching -= ApplyGameProfile;
        _services.Launcher.LaunchNotice -= OnLaunchNotice;
        _services.Launcher.GameLaunched -= OnGameLaunched;
        _services.Launcher.GameRunStateChanged -= OnGameRunStateChanged;
        _services.Shaders.Changed -= OnShadersChanged;
        if (_shadersHostEvent is { } shadersEvt) _services.ShadersHost.Event -= shadersEvt;
        _services.HdrHost.WarningRaised -= OnHdrWarning;
        _shadersHostEvent = null;
        UnwireCompress();
        _services.ShaderPrepare.Changed -= OnShadersChanged;
        ShaderRebuildWatch.Changed -= OnShadersRebuildChanged;
        _services.Store.Changed -= OnStoreChanged;
        LauncherBackup.StopAnnounced -= OnBackupStopped;
        _services.Achievements.SummaryChanged -= OnAchievementsChanged;
        _services.Store.SaleFound -= OnStoreSale;
        _services.Library.LibraryUpdated -= OnLibraryUpdated;
        if (_libraryAntiCheatWarm is { } warmHandler) _services.Library.LibraryUpdated -= warmHandler;
        _libraryAntiCheatWarm = null;
        _services.Library.LibraryUpdated -= FollowSteamTransfers;
        _services.Library.LibraryUpdated -= SteamOverlaySoon;
        InstalledSizeCache.MeasurementUpdated -= OnLibraryUpdated;
        ExoComponents.FailedTwice -= OnComponentFailed;
        GameSettings.AppliedAgain -= OnOptimizedAgain;
        AutopilotWatch.Reported -= OnAutopilotReported;
        AutopilotWatch.Shared.LiveChanged -= OnAutopilotLiveChanged;
        FrameCapService.Shared.CapLabChanged -= OnCapLabChanged;
        DetachGpuAndDisplayCaches();
        GameSettings.WindowModeHeld -= OnOptimizeHeld;
        Account.AccountSessionStore.Changed -= OnAccountChanged;
        if (Volatile.Read(ref _driverEvents) is { } driverEvents)
        {
            NvidiaDriverService.Changed -= driverEvents;
            GpuDriverUpdate.Changed -= driverEvents;
            DriverInstallSignal.Changed -= PostDriverInstall;
        }
        if (Volatile.Read(ref _hidWired) == 1)
        {
            _services.Hid.Input -= OnHidInput;
            _services.Hid.Changed -= OnHidChanged;
        }
        // Static events: a bridge left subscribed would keep posting to its page, and stay alive with it.
        if (_discordEvents)
        {
            DiscordClient.DiscordSession.Event -= PostEvent;
            DiscordClient.DiscordSession.VideoFrame -= PostVideoFrame;
        }
        StopGiftsTimer();
        DetachAppsCache();
        UnwatchDevices();
        UnwatchEq();
        Interlocked.Exchange(ref _versionWatch, null)?.Dispose();
        Interlocked.Exchange(ref _steamClientPoll, null)?.Dispose();
        Interlocked.Exchange(ref _shaderPreparePoll, null)?.Dispose();
        Interlocked.Exchange(ref _steamOutagePoll, null)?.Dispose();
        StopPendingWriteCheck();
        if (SteamSignIn.SteamWebsiteToken.Fetch == SteamWebsiteTokenAsync) SteamSignIn.SteamWebsiteToken.Fetch = null;
        _runStatePoll?.Dispose();
        _runStatePoll = null;
        ForegroundWatcher.WindowActivity -= OnWindowActivity;
        _services.Library.LibraryUpdated -= OnLibraryChangedForGames;
        _services.Launcher.GameLaunched -= OnGameLaunchedScan;
        _services.Launcher.GameLaunching -= OnGameLaunchingAllowance;
        _services.Launcher.GameSessionCompleted -= OnGameSessionEndedScan;
        _services.Launcher.GameLaunchFailed -= OnGameSessionEndedScan;
        _services.Dependencies.DeferredProgress -= PostRuntimeProgress;
        _services.Dependencies.DeferredCompleted -= PostRuntimeDone;
        GpuManualTuningService.Shared.StateChanged -= PostGpuTuningChanged;
        GpuManualTuningService.Shared.TestProgressChanged -= PostGpuTuningTestProgress;
        FpsCounter.Stop();
    }

    // Store tools report a sample per output line; the UI repaints at 4 Hz.
    // Running samples of one job post at most every 250 ms (the latest wins);
    // a new job, phase, cancel state or end posts at once and drops any older
    // pending sample so events never arrive out of order.
    private const int ProgressPostIntervalMs = 250;
    private readonly object _progressGate = new();
    // Games whose download asked for a banner this run (under _progressGate).
    private readonly HashSet<string> _bannerAsked = new(StringComparer.OrdinalIgnoreCase);
    private InstallProgress? _progressPosted;
    private InstallProgress? _progressPending;
    private long _progressPostedAtMs;

    private void OnProgress(InstallProgress p)
    {
        lock (_progressGate)
        {
            var last = _progressPosted;
            var now = Environment.TickCount64;
            var urgent = !p.IsActive || last is null || !last.IsActive || last.Phase != p.Phase ||
                last.CanCancel != p.CanCancel ||
                !string.Equals(last.GameId, p.GameId, StringComparison.OrdinalIgnoreCase);
            if (urgent || now - _progressPostedAtMs >= ProgressPostIntervalMs)
            {
                PostProgressLocked(p, now);
                return;
            }
            var scheduled = _progressPending is not null;
            _progressPending = p;
            if (scheduled) return;
            var wait = (int)Math.Max(1, ProgressPostIntervalMs - (now - _progressPostedAtMs));
            _ = Task.Delay(wait).ContinueWith(_ =>
            {
                lock (_progressGate)
                {
                    if (_progressPending is { } pending && !_detached)
                        PostProgressLocked(pending, Environment.TickCount64);
                    _progressPending = null;
                }
            }, TaskScheduler.Default);
        }
    }

    private void PostProgressLocked(InstallProgress p, long now)
    {
        _progressPending = null;
        _progressPosted = p;
        _progressPostedAtMs = now;
        // The page reloads the library the moment it hears "completed": the library must already know it changed,
        // or that reload reuses the store's last answer and the new game shows as not installed.
        if (p.Phase == InstallPhase.Completed) _services.Library.Invalidate();
        else if (_bannerAsked.Add(p.GameId)) _ = WarmBannerAsync(p.GameId);
        PostEvent("install.progress", MapProgress(p));
    }

    /// <summary>
    /// A game that starts downloading gets its banner: Continue shows it wide, and library warms fetch banners for
    /// installed games only, so a download showed its poster blurred into a wash.
    /// </summary>
    private async Task WarmBannerAsync(string id)
    {
        try
        {
            if (_services.Library.AllSourceEntries().FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } game) return;
            if (await CoverArtService.WarmBannerAsync(game, _bridgeLifetime.Token).ConfigureAwait(false))
                await _services.Library.PublishArtworkChangeAsync(game.Id, recomputeComputedCovers: false, _bridgeLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Debug($"Banner for a download failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // A Steam download Steam started itself gets live progress too (see LaunchOrchestrator.FollowSteamTransfers).
    private void FollowSteamTransfers()
    {
        try { _services.Launcher.FollowSteamTransfers(_services.Library.PeekCachedLibrary()); }
        catch (Exception ex) { AppLog.Debug("Following Steam downloads failed: " + ex.Message); }
    }

    private void OnDownloadQueueChanged() => PostEvent("install.queue", new
    {
        queuedGameIds = _services.Launcher.QueuedGameIds,
        pausedDownloads = _services.Launcher.PausedDownloads,
        held = _services.Launcher.HoldReason,
    });

    // Run state goes out before launch.status: the UI counts each run-state
    // push as a newer read, so the game.get that launch.status triggers must
    // be issued after it or it would be dropped as stale.
    /// <summary>A store client needs the player (a prompt): the sentence goes out on the launch status the page already shows.</summary>
    private void OnLaunchNotice(GameEntry game, string message) =>
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message, phase = "preparing" });

    private void OnGameLaunched(GameEntry game)
    {
        OnGameRunStateChanged(game.Id, _services.Launcher.GetGameRunState(game));
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = "Game started.", phase = "running" });
    }

    private void OnGameSessionCompleted(GameEntry game)
    {
        OnGameRunStateChanged(game.Id, _services.Launcher.GetGameRunState(game));
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = "Game closed.", phase = "stopped" });
        // Foreground tracking selects the next game or desktop after the window closes.
    }

    /// <summary>Same flags game.get projects for this exact id.</summary>
    private void OnGameRunStateChanged(string gameId, GameRunState state) =>
        PostEvent("game.runState", new { id = gameId, isRunning = state.IsRunning, canStop = state.CanStop });

    private void OnLibraryUpdated()
    {
        // Cover warm used to fire this once per downloaded poster, each time
        // mapping and serialising the whole library on the UI thread. Throttle
        // to one push per 80 ms; a trailing pass still lands the last cover.
        Interlocked.Exchange(ref _libraryPushQueued, 1);
        if (Interlocked.CompareExchange(ref _libraryPushScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _libraryPushQueued, 0);
                    var wait = 80 - (Environment.TickCount64 - Interlocked.Read(ref _libraryPushedAtMs));
                    if (wait > 0 && wait < 1_000)
                        await Task.Delay((int)wait).ConfigureAwait(false);
                    if (_detached) return;
                    Interlocked.Exchange(ref _libraryPushedAtMs, Environment.TickCount64);
                    try
                    {
                        // LibraryService.OnWarmBatch already reapplies cache URLs
                        // before it raises LibraryUpdated. Publish that snapshot;
                        // do not regroup the entire library a second time here.
                        var games = _services.Library.PeekCachedLibrary();
                        var json = ExoBridgeProtocol.SerializeEvent("library.updated", new
                        {
                            games = games.Select(game => MapGame(game)).ToList(),
                            count = games.Count,
                        });
                        // Cover warms, size measurements and derived refreshes
                        // often settle on the rows the page already has. The
                        // page re-parses and re-merges every row it is sent, so
                        // an unchanged library is not sent again.
                        var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(json.AsSpan()));
                        if (Volatile.Read(ref _libraryPushedHash) is not { } pushed || !pushed.AsSpan().SequenceEqual(hash))
                        {
                            Volatile.Write(ref _libraryPushedHash, hash);
                            PostSerialized(json);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug("library.updated failed: " + ex.Message);
                    }
                }
                while (!_detached && Interlocked.CompareExchange(ref _libraryPushQueued, 0, 1) == 1);
            }
            finally
            {
                Interlocked.Exchange(ref _libraryPushScheduled, 0);
                if (!_detached && Volatile.Read(ref _libraryPushQueued) != 0)
                    OnLibraryUpdated();
            }
        });
    }

    private void OnMessage(string raw) => UiGuard.Run(() =>
    {
        if (_detached || string.IsNullOrWhiteSpace(raw)) return;
        _ = HandleAsync(InboundForTransport(_transport, raw));
    });

    private async Task HandleAsync(string raw)
    {
        string? id = null;
        string? method = null;
        string? perfKey = null;
        BridgeRequestRegistry.Lease? scope = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!ExoBridgeProtocol.TryParseRequest(raw, out var request))
            {
                // Well formed but too large: answer, so the page does not wait for a reply that never comes.
                if (request.Id.Length > 0 && !_detached)
                {
                    AppLog.Warn($"RPC {request.Method}: request of {raw.Length} characters is over the limit.");
                    PostResponse(request.Id, ok: false, error: "That is too large for Exo to take.", elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                return;
            }
            id = request.Id;
            method = request.Method;
            var paramsEl = request.Params;
            var hasParams = request.HasParams;
            perfKey = hasParams ? PerfKey(paramsEl) : null;
            if (_detached) return;
            if (_trace.IsEnabled)
                _trace.RecordRequest(RpcTrace.ClientKind(_transport), id, method, paramsEl, hasParams);
            if (_trace.IsEnabled && method == "debug.mark")
            {
                PostResponse(id, ok: true, result: new { ok = true }, elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return;
            }
            if (method == "rpc.cancel")
            {
                var target = ReadString(paramsEl, hasParams, "requestId");
                if (target is { Length: > 0 and <= 128 })
                {
                    _earlyCancels.Record(target);
                    if (_rpcRequests.Cancel(target)) _earlyCancels.Consume(target);
                }
                return;
            }
            // Any Discord request means the page wants Discord's events (calls, video), not only Home or sign-in.
            if (method.StartsWith("discord.", StringComparison.Ordinal)) DiscordEvents();
            scope = _rpcRequests.Begin(id, method, _bridgeLifetime.Token);
            if (_earlyCancels.Consume(id)) throw new OperationCanceledException();
            var requestToken = scope?.Token ?? _bridgeLifetime.Token;
            requestToken.ThrowIfCancellationRequested();

            object? result = method switch
            {
                // The library's account checks (registry, store config files) run before its first await: off the UI thread.
                "library.get" => await Task.Run(() => LibraryGetAsync(paramsEl, hasParams)).WaitAsync(requestToken).ConfigureAwait(false),
                "library.refresh" => await Task.Run(() => LibraryGetAsync(paramsEl, hasParams: true, force: true,
                    full: ReadBool(paramsEl, hasParams, "full") == true)).WaitAsync(requestToken).ConfigureAwait(false),
                "keys.preview" => await KeysPreviewAsync().ConfigureAwait(true),
                "keys.activate" => await KeysActivateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.get" => await GameGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.launch" => await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.stop" => await GameStopAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.install" => await GameInstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.update" => await GameUpdateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.resumeDownload" => await GameResumeDownloadAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.uninstall" => await GameUninstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.repair" => await GameRepairAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.extras" => await GameExtrasAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.openFolder" => GameOpenFolder(paramsEl, hasParams),
                "game.properties" => await GamePropertiesAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.properties.set" => await GamePropertiesSetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.replace" => await ArtworkReplaceAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.reset" => await ArtworkResetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.refetch" => await ArtworkRefetchAsync(paramsEl, hasParams).ConfigureAwait(true),
                // Answered at once, so it reaches the page before the library does (cover art the page names itself).
                "art.base" => BuildArtBase(),
                "library.folderArt" => await LibraryFolderArtAsync(paramsEl, hasParams).ConfigureAwait(true),
                "library.refreshAll" => await LibraryRefreshAllAsync(_bridgeLifetime.Token).ConfigureAwait(true),
                "game.cancelInstall" => _services.Launcher.Cancel(ReadString(paramsEl, hasParams, "id")),
                "game.pauseInstall" => _services.Launcher.Pause(ReadString(paramsEl, hasParams, "id") ?? ""),
                "game.dequeue" => _services.Launcher.Dequeue(ReadString(paramsEl, hasParams, "id") ?? ""),
                "downloads.list" => DownloadsList(),
                "downloads.move" => _services.Launcher.MoveQueued(ReadString(paramsEl, hasParams, "id") ?? "",
                    hasParams && paramsEl.TryGetProperty("by", out var by) && by.ValueKind == JsonValueKind.Number && by.TryGetInt32(out var places) ? Math.Clamp(places, -500, 500) : 0),
                "downloads.pauseAll" => _services.Launcher.PauseAll(),
                "downloads.resumeAll" => _services.Launcher.ResumeAll(),
                "game.autoUpdate" => GameAutoUpdate(paramsEl, hasParams),
                "game.progress" => GameProgress(paramsEl, hasParams),
                "stores.auth" => await StoresAuthAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.state" => ExoAccountState(),
                "exoAccount.create" => await ExoAccountCreateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.signIn" => await ExoAccountSignInAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.markAsked" => ExoAccountMarkAsked(),
                "exoAccount.saveCode" => await ExoAccountSaveCodeAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.overview" => ExoAccountOverview(),
                "exoAccount.sync" => await ExoAccountSyncAsync().ConfigureAwait(true),
                "exoAccount.sessions" => await ExoAccountSessionsAsync().ConfigureAwait(true),
                "exoAccount.revoke" => await ExoAccountRevokeAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.changePassword" => await ExoAccountChangePasswordAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.newRecoveryCode" => await ExoAccountNewRecoveryCodeAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.export" => await ExoAccountExportAsync().ConfigureAwait(true),
                "exoAccount.delete" => await ExoAccountDeleteAsync(paramsEl, hasParams).ConfigureAwait(true),
                "exoAccount.signOut" => await ExoAccountSignOutAsync().ConfigureAwait(true),
                "exoAccount.reset" => await ExoAccountResetAsync(paramsEl, hasParams).ConfigureAwait(true),
            "exoAccount.probe" => await ExoAccountProbeAsync().ConfigureAwait(true),
            "exoAccount.codeConfirmed" => ExoAccountCodeConfirmed(),
                "stores.outages" => StoresOutages(),
                "stores.playOffline" => await StoresPlayOfflineAsync(paramsEl, hasParams).ConfigureAwait(true),
                "steam.setOffline" => await SteamSetOfflineAsync(paramsEl, hasParams).ConfigureAwait(true),
                "steam.account" => SteamAccountState(),
                "steam.downloads" => OpenSteamProtocol("steam://nav/downloads"),
                "steam.signInQr" => await SteamSignInQrAsync(requestToken).ConfigureAwait(true),
                "steam.signInCancel" => SteamSignInCancel(),
                "steam.signInPoll" => await SteamSignInPollAsync(requestToken).ConfigureAwait(true),
                "steam.signInPassword" => await SteamSignInPasswordAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "steam.signInGuard" => await SteamSignInGuardAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "steam.signOut" => await SteamSignOutAsync(requestToken).ConfigureAwait(true),
                "playtime.riotRefresh" => await RiotHoursRefreshAsync(paramsEl, hasParams).ConfigureAwait(true),
                "playtime.importRiotExport" => await ImportRiotExportAsync(paramsEl, hasParams).ConfigureAwait(true),
                "playtime.setRiotBaseline" => await SetRiotBaselineAsync(paramsEl, hasParams).ConfigureAwait(true),
                "web.preferences.get" => WebClientPreferences.Reload(),

                "web.preferences.set" => await SaveWebPreferencesAsync(paramsEl),
                "shell.heartbeat" => HeartbeatReply(paramsEl, hasParams),
                "shell.painted" => App.MainAppWindow is { } paintedWindow
                    && paintedWindow.DispatcherQueue.Enqueue(paintedWindow.RevealPainted)
                    ? new { ok = true }
                    : new { ok = false },
                // The UI reached a usable start (Library or first-run setup): an update's startup receipt (UpdateStartupHealth).
                "shell.ready" => ShellReady(),
                "shell.bootstrap" => await ShellBootstrapAsync(requestToken).ConfigureAwait(false),
                "shell.prepare" => App.MainAppWindow is { } prepareWindow
                    ? await prepareWindow.PrepareConnectedRoomsAsync()
                    : new { ok = false },
                "music.playback" => _services.MusicEngine.IsActive || App.MainAppWindow is not null
                    ? await _services.MusicEngine.PlaybackAsync().ConfigureAwait(false)
                    : await _services.HeadlessMusic.PlaybackAsync().ConfigureAwait(false),
                "music.match" => hasParams ? MusicMatch.Answer(paramsEl) : new { ok = false, message = "Nothing to match." },
                "music.control" => _services.MusicEngine.IsActive || App.MainAppWindow is not null
                    ? await _services.MusicEngine.ControlAsync(ReadString(paramsEl, hasParams, "action"),
                        ReadDouble(paramsEl, hasParams, "positionMs")).ConfigureAwait(false)
                    : await _services.HeadlessMusic.ControlAsync(ReadString(paramsEl, hasParams, "action"),
                        ReadDouble(paramsEl, hasParams, "positionMs")).ConfigureAwait(false),
                "music.volume" => _services.MusicEngine.IsActive || App.MainAppWindow is not null
                    ? await _services.MusicEngine.VolumeAsync(ReadDouble(paramsEl, hasParams, "level"),
                        ReadBool(paramsEl, hasParams, "muted")).ConfigureAwait(false)
                    : await _services.HeadlessMusic.VolumeAsync(ReadDouble(paramsEl, hasParams, "level"),
                        ReadBool(paramsEl, hasParams, "muted")).ConfigureAwait(false),
                "spotify.start" => await _services.MusicEngine.StartAsync("spotify").ConfigureAwait(false),
                "spotify.link" => await SpotifyLinks.LinkAsync(ReadString(paramsEl, hasParams, "url")).ConfigureAwait(true),
                "spotify.view" => await _services.MusicEngine.SpotifyViewAsync(ReadString(paramsEl, hasParams, "view"),
                    ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "query"),
                    (int)(ReadDouble(paramsEl, hasParams, "offset") ?? 0)).ConfigureAwait(false),
                "spotify.act" => await _services.MusicEngine.SpotifyActAsync(ReadString(paramsEl, hasParams, "action"),
                    ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "context"),
                    ReadDouble(paramsEl, hasParams, "index") is { } index ? (int)index : null,
                    ReadBool(paramsEl, hasParams, "on"), ReadString(paramsEl, hasParams, "uid"),
                    ReadString(paramsEl, hasParams, "query"), ReadString(paramsEl, hasParams, "name"),
                    ReadString(paramsEl, hasParams, "description"), ReadString(paramsEl, hasParams, "image")).ConfigureAwait(false),
                "ytmusic.start" or "applemusic.start" => await _services.MusicEngine.StartAsync(
                    method == "ytmusic.start" ? "youtube" : "apple").ConfigureAwait(false),
                "ytmusic.view" or "applemusic.view" => await _services.MusicEngine.WebMusicViewAsync(
                    method == "ytmusic.view" ? "youtube" : "apple", ReadString(paramsEl, hasParams, "view"),
                    ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "query"),
                    (int)(ReadDouble(paramsEl, hasParams, "offset") ?? 0)).ConfigureAwait(false),
                "ytmusic.act" or "applemusic.act" => await _services.MusicEngine.WebMusicActAsync(
                    method == "ytmusic.act" ? "youtube" : "apple", ReadString(paramsEl, hasParams, "action"),
                    ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "context"),
                    ReadDouble(paramsEl, hasParams, "index") is { } webIndex ? (int)webIndex : null,
                    ReadBool(paramsEl, hasParams, "on"), ReadString(paramsEl, hasParams, "uid"),
                    ReadString(paramsEl, hasParams, "query"), ReadString(paramsEl, hasParams, "text")).ConfigureAwait(false),
                "music.signIn" => await _services.MusicEngine.SignInAsync(ReadString(paramsEl, hasParams, "service")).ConfigureAwait(false),
                "music.warm" => await _services.MusicEngine.WarmAsync().ConfigureAwait(false),
                "shaders.addon" => await Task.Run(_services.ShadersHost.Status).ConfigureAwait(true),
                "shaders.call" => await _services.ShadersHost.CallAsync(ReadString(paramsEl, hasParams, "method"),
                        hasParams && paramsEl.TryGetProperty("params", out var shadersParams) ? System.Text.Json.Nodes.JsonNode.Parse(shadersParams.GetRawText()) : null,
                        requestToken).ConfigureAwait(true),
                "capture.status" => App.MainAppWindow?.CaptureStatus() ?? _services.HeadlessCapture.Status(),
                "capture.settings" => App.MainAppWindow is { } captureWindow
                    ? await captureWindow.CaptureSetSettingsAsync(paramsEl).ConfigureAwait(true)
                    : await _services.HeadlessCapture.UpdateSettingsAsync(paramsEl).ConfigureAwait(false),
                "capture.act" => App.MainAppWindow is { } actWindow
                    ? await actWindow.CaptureActAsync(ReadString(paramsEl, hasParams, "action")).ConfigureAwait(true)
                    : await _services.HeadlessCapture.ActAsync(ReadString(paramsEl, hasParams, "action"), requestToken).ConfigureAwait(false),
                "capture.list" => App.MainAppWindow is { } listWindow
                    ? await Task.Run(() => listWindow.CaptureList(ReadString(paramsEl, hasParams, "gameId"), (int?)ReadDouble(paramsEl, hasParams, "limit"))).ConfigureAwait(true)
                    : await Task.Run(() => _services.HeadlessCapture.List(ReadString(paramsEl, hasParams, "gameId"), (int?)ReadDouble(paramsEl, hasParams, "limit"))).ConfigureAwait(false),
                // Each finds its file by scanning the captures folder, then moves, writes or decodes it: off the UI thread.
                "capture.item" => App.MainAppWindow is { } itemWindow
                    ? await Task.Run(() => itemWindow.CaptureItemAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "action"))).ConfigureAwait(false)
                    : await Task.Run(() => _services.HeadlessCapture.ItemAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "action"))).ConfigureAwait(false),
                "capture.edit" => App.MainAppWindow is { } editWindow
                    ? await Task.Run(() => editWindow.CaptureEditAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "preset"),
                        ReadDouble(paramsEl, hasParams, "startMs"), ReadDouble(paramsEl, hasParams, "endMs"))).ConfigureAwait(false)
                    : await Task.Run(() => _services.HeadlessCapture.EditAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "preset"),
                        ReadDouble(paramsEl, hasParams, "startMs"), ReadDouble(paramsEl, hasParams, "endMs"), requestToken)).ConfigureAwait(false),
                "capture.reel" => App.MainAppWindow is { } reelWindow && hasParams
                    ? await Task.Run(() => reelWindow.CaptureReelStart(paramsEl)).ConfigureAwait(false) : new { ok = false },
                "capture.reel.cancel" => App.MainAppWindow?.CaptureReelCancel() ?? new { ok = false },
                "capture.text" => App.MainAppWindow is { } textWindow
                    ? await Task.Run(() => textWindow.CaptureTextAsync(ReadString(paramsEl, hasParams, "id"), ReadDouble(paramsEl, hasParams, "x"), ReadDouble(paramsEl, hasParams, "y"),
                        ReadDouble(paramsEl, hasParams, "width"), ReadDouble(paramsEl, hasParams, "height"))).ConfigureAwait(false) : new { ok = false },
                "capture.send" => await CaptureSendAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "capture.saveImage" => App.MainAppWindow is { } imageWindow
                    ? await Task.Run(() => imageWindow.CaptureSaveImageAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "png"))).ConfigureAwait(false) : new { ok = false },
                "friends.badge" => App.MainAppWindow?.FriendsBadge() ?? new { count = 0 },
                "deps.list" => await Task.Run(DepsList).ConfigureAwait(false),
                "runtimes.list" => await RuntimesListAsync(requestToken).ConfigureAwait(false),
                "runtimes.install" => await RuntimesInstallAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "runtimes.cancel" => _services.Dependencies.Cancel(),
                "notes.list" => await Task.Run(() => NotesList(ReadString(paramsEl, hasParams, "query"))).ConfigureAwait(false),
                "notes.read" => await Task.Run(() => NotesRead(ReadString(paramsEl, hasParams, "id"))).ConfigureAwait(false),
                "notes.photo" => await NotesPhotoAsync(paramsEl, hasParams).ConfigureAwait(true),
                "notes.save" => await Task.Run(() => NotesSave(paramsEl, hasParams)).ConfigureAwait(false),
                "notes.act" => await NotesActAsync(paramsEl, hasParams).ConfigureAwait(true),
                "notes.session" => await Task.Run(() => NotesSession(paramsEl, hasParams)).ConfigureAwait(false),
                "notes.open" => await NotesOpenAsync().ConfigureAwait(true),
                "notes.saveAs" => await NotesSaveAsAsync(paramsEl, hasParams).ConfigureAwait(true),
                "notes.pending" => NotesPending(),
                "backup.status" => await Task.Run(BackupStatus).ConfigureAwait(false),
                "backup.decline" => BackupDecline(),
                "backup.now" => await Task.Run(BackupNow).ConfigureAwait(false),
                "backup.export" => await BackupExportAsync().ConfigureAwait(true),
                "backup.import" => await BackupImportAsync().ConfigureAwait(true),
                "backup.restore" => await BackupRestoreOfferAsync(paramsEl, hasParams).ConfigureAwait(true),
                "app.pendingOpen" => AppPendingOpen(),
                "notes.spell" => await NotesSpellAsync(paramsEl, hasParams).ConfigureAwait(true),
                "notes.textFiles" => await NotesTextFilesAsync(ReadString(paramsEl, hasParams, "action")).ConfigureAwait(true),
                "replaced.scan" => await Task.Run(() => ReplacedScan(ReadBool(paramsEl, hasParams, "clean") == true)).ConfigureAwait(false),
                "replaced.remove" => await ReplacedRemoveAsync(ReadString(paramsEl, hasParams, "id") ?? "", requestToken).ConfigureAwait(true),
                "apps.list" => await Task.Run(AppsList).ConfigureAwait(false),
                "apps.icons" => await Task.Run(() => AppsIconsAsync(paramsEl, hasParams)).ConfigureAwait(false),
                "apps.confirm" => await AppsConfirmAsync(paramsEl, hasParams).ConfigureAwait(true),
                "telemetry.sent" => new { ok = true, payloads = Telemetry.Sent() },
                "telemetry.delete" => await TelemetryDeleteAsync(requestToken).ConfigureAwait(true),
                "telemetry.track" => TelemetryTrack(paramsEl, hasParams),
                "apps.uninstall" => await AppsUninstallAsync(ReadString(paramsEl, hasParams, "id") ?? "", ReadBool(paramsEl, hasParams, "quiet") == true, requestToken).ConfigureAwait(true),
                "apps.restorePoint" => await AppsRestorePointAsync(requestToken).ConfigureAwait(true),
                "apps.installerCache" => await AppsInstallerCacheAsync().ConfigureAwait(false),
                "apps.installerCacheClean" => await AppsInstallerCacheCleanAsync(requestToken).ConfigureAwait(true),
                "apps.scan" => await AppsScanAsync(ReadString(paramsEl, hasParams, "id") ?? "", requestToken).ConfigureAwait(true),
                "apps.clean" => await AppsCleanAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "apps.allowEdge" => await AppsAllowEdgeAsync(requestToken).ConfigureAwait(true),
                "apps.openDefaults" => AppsOpenDefaults(),
                "apps.packages" => await AppsPackagesAsync().ConfigureAwait(false),
                "apps.packagesPlan" => await AppsPackagesPlanAsync(paramsEl, hasParams).ConfigureAwait(false),
                "apps.packagesRemove" => await AppsPackagesRemoveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "apps.packageAddBack" => await AppsPackageAddBackAsync(ReadString(paramsEl, hasParams, "id") ?? "", requestToken).ConfigureAwait(true),
                "apps.tidy" => await AppsTidyAsync(ReadBool(paramsEl, hasParams, "fresh") == true).ConfigureAwait(false),
                "apps.leave" => AppsLeave(paramsEl, hasParams),
                "apps.history" => await Task.Run(AppsHistory).ConfigureAwait(false),
                "apps.undo" => await AppsUndoAsync(ReadString(paramsEl, hasParams, "id") ?? "", requestToken).ConfigureAwait(true),
                "junk.scan" => await JunkScanAsync().ConfigureAwait(false),
                "compress.state" => await CompressStateAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "compress.list" => await CompressListAsync(requestToken).ConfigureAwait(false),
                "compress.act" => await CompressActAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "compress.all" => await CompressAllAsync(requestToken).ConfigureAwait(true),
                "compress.settings" => CompressSettings(paramsEl, hasParams),
                "junk.clean" => await JunkCleanAsync(paramsEl, hasParams).ConfigureAwait(true),
                "startup.list" => await StartupListAsync().ConfigureAwait(false),
                "bios.update" => await BiosUpdateAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "achievements.preview" => AchievementsPreview(paramsEl, hasParams),
                "fps.counter" => await FpsCounterRpcAsync(paramsEl, hasParams).ConfigureAwait(true),
                "startup.remove" => await StartupChangeAsync(paramsEl, hasParams, restore: false).ConfigureAwait(true),
                "startup.restore" => await StartupChangeAsync(paramsEl, hasParams, restore: true).ConfigureAwait(true),
                "startup.clean" => await StartupCleanAsync().ConfigureAwait(true),
                "stores.check" => await StoresCheckAsync().ConfigureAwait(false),
                "stores.matrix" => await Task.Run(StoreMatrixMapped).ConfigureAwait(false),
                "settings.get" => BuildSettings(),
                "settings.set" => SetSettings(paramsEl, hasParams),
                "accounts.list" => await Task.Run(AccountsList).ConfigureAwait(false),
                "gifts.list" => GiftsList(ReadBool(paramsEl, hasParams, "force") == true),
                "gifts.answer" => await GiftsAnswerAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "gifts.steamSignIn" => await GiftsSteamSignInAsync(requestToken).ConfigureAwait(true),
                "gifts.steamSignOut" => await GiftsSteamSignOutAsync(requestToken).ConfigureAwait(true),
                "accounts.switch" => await AccountsSwitchAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.add" => await AccountsAddAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.remove" => await AccountsRemoveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.signOut" => await AccountsSignOutAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.epicImport" => await AccountsEpicImportAsync(requestToken).ConfigureAwait(true),
                "accounts.epicAdd" => await AccountsEpicAddAsync(requestToken).ConfigureAwait(true),
                "accounts.epicNewLauncher" => await AccountsEpicNewLauncherAsync(requestToken).ConfigureAwait(true),
                "clients.status" => await Task.Run(ClientsStatus).ConfigureAwait(false),
                "clients.fix" => await ClientsFixAsync(requestToken).ConfigureAwait(true),
                "accounts.epicRemove" => await AccountsEpicRemoveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "shell.minimize" => MinimizeWindow(),
                "shell.maximize" => ToggleMaximize(),
                "shell.windowState" => WindowState(),
                "shell.close" => CloseWindow(),
                "shell.openUrl" => OpenUrl(paramsEl, hasParams),
                "shell.showStore" => await ShowStoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "shell.pickFolder" => await PickFolderAsync().ConfigureAwait(true),
                "shell.pickProgram" => await PickProgramAsync().ConfigureAwait(true),
                "shell.setChromeLayout" => SetChrome(paramsEl, hasParams),
                "app.log" => LogFromUi(paramsEl, hasParams),
                "app.repairCache" => await RepairCacheAsync(paramsEl, hasParams).ConfigureAwait(false),
                "app.openLogs" => OpenLogsFolder(),
                "app.logsFor" => await LogsForAsync(paramsEl, hasParams).ConfigureAwait(true),
                "report.preview" => await ReportPreviewAsync().ConfigureAwait(true),
                "report.send" => await ReportSendAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "app.checkUpdate" => await CheckUpdateAsync(ReadBool(paramsEl, hasParams, "manual") == true, requestToken).ConfigureAwait(true),
                "app.installUpdate" => await InstallUpdateAsync(requestToken).ConfigureAwait(true),
                "dlss.status" => await DlssStatusAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "dlss.updateAll" => await DlssUpdateAllAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "dlss.newer" => DlssNewer(),
                "dlss.downloadNewer" => await DlssDownloadNewerAsync(requestToken).ConfigureAwait(true),
                "dlss.restore" => await DlssRestoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "dlss.consent" => await DlssConsentAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "dlss.frameGen" => await DlssFrameGenAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "game.frameGen" => await GameFrameGenAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "game.frameCap" => await GameFrameCapAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "game.capLab.run" => await GameCapLabRunAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "game.capLab.cancel" => GameCapLabCancel(paramsEl, hasParams),
                // Windows' administrator prompt blocks the thread that asks until it is answered.
                "shaders.status" => await ShadersStatusAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "shaders.prepare.games" => await ShaderPrepareGamesAsync(requestToken).ConfigureAwait(false),
                "shaders.prepare.start" => await ShaderPrepareStartAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "shaders.prepare.cancel" => ShaderPrepareCancel(paramsEl, hasParams),
                "shaders.estimates" => await ShadersEstimatesAsync(requestToken).ConfigureAwait(false),
            "shaders.rebuild" => ShadersRebuildStatus(),
                "hdr.state" => await Task.Run(HdrState).ConfigureAwait(false),
                "hdr.game" => await HdrGameAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "hdr.calibrate" => await HdrCalibrateAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "game.settings" => await GameSettingsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "game.graphicsFeatures" => await GameGraphicsFeaturesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "game.graphicsFeatures.set" => await GameGraphicsFeaturesSetAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "keep.status" => KeepStatusForPage(paramsEl, hasParams),
                "gpu.display" => await GpuDeadlineAsync(Task.Run(() => GpuDisplayAsync(paramsEl, hasParams, requestToken), requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "discord.status" => DiscordStatus(paramsEl, hasParams),
                "discord.signIn" => DiscordSignIn(paramsEl, hasParams),
                "discord.accounts" => DiscordAccountsCall(paramsEl, hasParams),
                "discord.signOut" => await DiscordSignOutAsync().ConfigureAwait(true),
                "app.notify" => hasParams ? AppNotify(paramsEl) : new { ok = false },
                "discord.home" => await Task.Run(DiscordHome).ConfigureAwait(false),
                "discord.channels" => await Task.Run(() => DiscordChannels(paramsEl, hasParams)).ConfigureAwait(false),
                "discord.messages" => await DiscordMessagesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.send" => await DiscordSendAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.edit" => await DiscordEditAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.delete" => await DiscordDeleteAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.ack" => await DiscordAckAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.thread" => await DiscordThreadAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.react" => await DiscordReactAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.emojis" => DiscordEmojis(paramsEl, hasParams),
                "discord.view" => DiscordView(paramsEl, hasParams),
                "discord.upload" => DiscordUpload(paramsEl, hasParams),
                "discord.voice" => await DiscordVoiceAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.audio" => DiscordAudio(paramsEl, hasParams),
                "discord.ring" => await DiscordRingAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.friends" => DiscordFriends(),
                "discord.callSongs" => DiscordCallSongs(),
                "discord.friend" => await DiscordFriendAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.report" => await DiscordReportAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.markRead" => await DiscordMarkReadAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.setStatus" => await DiscordSetStatusAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.members" => await Task.Run(() => DiscordMembers(paramsEl, hasParams)).ConfigureAwait(false),
                "discord.mentionable" => DiscordMentionable(paramsEl, hasParams),
                "discord.colors" => DiscordColors(paramsEl, hasParams),
                "discord.pins" => await DiscordPinsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.messageAction" => await DiscordMessageActionAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.search" => await DiscordSearchAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.gifs" => await DiscordGifsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.stream" => DiscordStream(paramsEl, hasParams),
                "discord.golive" => await DiscordGoLiveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.camera" => DiscordCamera(paramsEl, hasParams),
                "discord.gifFavorites" => await DiscordGifFavoritesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
            "discord.emojiFavorites" => await DiscordEmojiFavoritesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.server" => await DiscordServerAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.admin" => await DiscordAdminAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.profile" => await DiscordProfileAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.threads" => await DiscordThreadsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.stickers" => await DiscordStickersAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.stickerData" => await DiscordStickerDataAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.poll" => await DiscordPollAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.soundboard" => await DiscordSoundboardAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.sticker" => await DiscordSendStickerAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.mentions" => await DiscordMentionsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.saved" => await DiscordSavedAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.forYou" => await DiscordForYouAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.unreads" => DiscordUnreads(),
                "discord.reactors" => await DiscordReactorsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.fileText" => await DiscordFileTextAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.image" => await DiscordImageAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.channel" => await DiscordChannelActionAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.apps" => await DiscordAppsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.typing" => await DiscordTypingAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.userSettings" => await DiscordUserSettingsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.account" => await DiscordAccountAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.shop" => await DiscordShopAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "discord.font" => await DiscordFontAsync(paramsEl, hasParams).ConfigureAwait(false),
                "discord.clientSettings" => DiscordClientSettings(paramsEl, hasParams),
                "discord.micTest" => DiscordMicTest(paramsEl, hasParams),
                "discord.micMonitor" => DiscordMicMonitor(paramsEl, hasParams),
                "discord.myVoice" => await DiscordMyVoiceAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "eq" => await Task.Run(() => EqAsync(paramsEl, hasParams)).ConfigureAwait(false),
                "eq.headphones" => await Task.Run(EqHeadphonesAsync).ConfigureAwait(false),
                "devices.image" => await Task.Run(() => DevicesImageAsync(paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                "controller.read" => await Task.Run(() => ControllerReadAsync(paramsEl, hasParams)).ConfigureAwait(false),
                "controller.measure" => await Task.Run(() => ControllerMeasureAsync(paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                "controller.test" => await Task.Run(() => ControllerTestAsync(paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                "controller.quality" => await Task.Run(() => ControllerQualityAsync(paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                "controller.watch" => await Task.Run(() => ControllerWatch(paramsEl, hasParams)).ConfigureAwait(false),
                "steamInput.state" => await Task.Run(SteamInputState).ConfigureAwait(false),
                "controller.calibrate" => await Task.Run(() => ControllerCalibrateAsync(paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                "controller.settings" => await Task.Run(() => ControllerSettingsAsync(paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                "controller.sound" => await ControllerSoundAsync(paramsEl, hasParams).ConfigureAwait(false),
                "controller.gameProfiles" => ControllerGameProfiles(),
                "controller.gameProfilesSave" => await Task.Run(() => ControllerGameProfilesSave(paramsEl, hasParams)).ConfigureAwait(false),
                "discord.sound" => DiscordSound(paramsEl, hasParams),
                "gpu.driver" => await GpuDeadlineAsync(Task.Run(() => GpuDriverAsync(paramsEl, hasParams, requestToken), requestToken), TimeSpan.FromSeconds(55), requestToken).ConfigureAwait(false),
                "gpu.profile" => await GpuDeadlineAsync(Task.Run(() => GpuProfileAsync(paramsEl, hasParams, requestToken), requestToken), GpuProfileDeadline(paramsEl, hasParams), requestToken).ConfigureAwait(false),
                "gpu.afmf3" => await GpuDeadlineAsync(GpuAfmf3Async(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.game" => await GpuDeadlineAsync(GpuGameAsync(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.controlPanel" => await GpuDeadlineAsync(Task.Run(GpuControlPanel.Open, requestToken), TimeSpan.FromSeconds(15), requestToken).ConfigureAwait(true),
                "gpu.processes" => await Task.Run(() => GpuProcesses(paramsEl, hasParams), requestToken).ConfigureAwait(false),
                "gpu.tuning.state" => await GpuDeadlineAsync(GpuTuningStateAsync(requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.tuning.apply" => await GpuDeadlineAsync(GpuTuningApplyAsync(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.tuning.reset" => await GpuDeadlineAsync(GpuTuningResetAsync(requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.tuning.waiver" => await GpuDeadlineAsync(GpuTuningWaiverAsync(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.tuning.test" => await GpuDeadlineAsync(GpuTuningTestAsync(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "gpu.tuning.undervolt" => await GpuDeadlineAsync(GpuTuningUndervoltAsync(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(false),
                "system.health" => await GpuDeadlineAsync(Task.Run(() => SystemHealthAsync(paramsEl, hasParams, requestToken), requestToken), SystemHealthDeadline(paramsEl, hasParams), requestToken).ConfigureAwait(false),
                "system.about" => await SystemAboutAsync(requestToken).ConfigureAwait(false),
                "system.tune" => await Task.Run(() => AutoTuneService.Act(ReadString(paramsEl, hasParams, "action"), ReadString(paramsEl, hasParams, "part"), ReadString(paramsEl, hasParams, "mode"))).ConfigureAwait(false),
                "autopilot.state" => await Task.Run(AutopilotState).ConfigureAwait(false),
                "autopilot.set" => await Task.Run(() => AutopilotSet(paramsEl, hasParams)).ConfigureAwait(false),
                "exoAi.state" => await Task.Run(ExoAiState).ConfigureAwait(false),
                "exoAi.set" => await Task.Run(() => ExoAiSet(paramsEl, hasParams)).ConfigureAwait(false),
                "system.interrupts" => await SystemInterruptsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "system.tuning" => await SystemTuningAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "system.qos" => await SystemQosAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "system.overlays" => await SystemOverlaysAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "system.mouseCore" => await SystemMouseCoreAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "mouse.diag.status" => await MouseDiagStatusAsync(requestToken).ConfigureAwait(false),
                "mouse.diag.measure" => await MouseDiagMeasureAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "mouse.diag.cancel" => MouseDiagCancel(),
                "mouse.diag.fix" => await MouseDiagFixAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "system.drives" => await SystemDrivesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "system.drivers" => await SystemDriversAsync(paramsEl, hasParams, requestToken).WaitAsync(TimeSpan.FromMinutes(4), requestToken).ConfigureAwait(false),
                "store.home" => await StoreHomeAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "store.calendar" => await StoreCalendarAsync(requestToken).ConfigureAwait(false),
                "store.search" => await StoreSearchAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "store.game" => await StoreGameAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "store.gameOffers" => await StoreGameOffersAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "store.gameReviews" => await StoreGameReviewsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "store.wishlist" => await StoreWishlistAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "store.open" => await StoreOpenAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                // Listing opens every device to read its reports: off the UI thread, like the calls below.
                "hid.list" => await Task.Run(() => HidList(paramsEl, hasParams)).ConfigureAwait(false),
                "devices.list" => await Task.Run(DevicesList).ConfigureAwait(false),
                "devices.usbMap" => await Task.Run(DevicesUsbMap).ConfigureAwait(false),
                "keyboard.read" => await Task.Run(() => KeyboardRead(paramsEl, hasParams)).ConfigureAwait(false),
                "keyboard.profile" => await Task.Run(() => KeyboardProfile(paramsEl, hasParams)).ConfigureAwait(false),
                "keyboard.settings" => await Task.Run(() => KeyboardSettings(paramsEl, hasParams)).ConfigureAwait(false),
                "keyboard.openApp" => await Task.Run(KeyboardOpenApp).ConfigureAwait(false),
                "keyboard.backup" => await Task.Run(() => KeyboardBackup(paramsEl, hasParams)).ConfigureAwait(false),
                "keyboard.backups" => await Task.Run(() => KeyboardBackups(paramsEl, hasParams)).ConfigureAwait(false),
                "keyboard.restore" => await Task.Run(() => KeyboardRestore(paramsEl, hasParams)).ConfigureAwait(false),
                "audio.read" => await Task.Run(() => AudioRead(paramsEl, hasParams)).ConfigureAwait(false),
                "audio.formats" => await Task.Run(() => AudioFormats(paramsEl, hasParams)).ConfigureAwait(false),
                "audio.setVolume" => await Task.Run(() => AudioSetVolume(paramsEl, hasParams)).ConfigureAwait(false),
                "audio.setMute" => await Task.Run(() => AudioSetMute(paramsEl, hasParams)).ConfigureAwait(false),
                "audio.setFormat" => await Task.Run(() => AudioSetFormat(paramsEl, hasParams)).ConfigureAwait(false),
                "mouse.profiles.get" => await Task.Run(() => MouseFileGet("profiles.json", "profiles")).ConfigureAwait(false),
                "mouse.profiles.set" => MouseFileSet("profiles.json", "profiles", paramsEl, hasParams),
                "mouse.presets.get" => await Task.Run(() => MouseFileGet("presets.json", "presets")).ConfigureAwait(false),
                "mouse.presets.set" => MouseFileSet("presets.json", "presets", paramsEl, hasParams),
                "mouse.panel.open" => await MousePanelOpenAsync(paramsEl, hasParams, requestToken).ConfigureAwait(false),
                "mouse.panel.bounds" => await MousePanelBoundsAsync(paramsEl, hasParams).ConfigureAwait(false),
                "mouse.panel.close" => await MousePanelCloseAsync().ConfigureAwait(false),
                "hid.open" or "hid.close" or "hid.sendReport" or "hid.sendFeatureReport" or "hid.receiveFeatureReport" or "hid.receiveInputReport"
                    => await Task.Run(() => HidCallAsync(method, paramsEl, hasParams, requestToken)).ConfigureAwait(false),
                _ => throw new UserFacingException($"Unknown method: {method}")
            };

            if (!_detached && scope?.Token.IsCancellationRequested != true)
                PostResponse(id!, ok: true, result: result, elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            if (!_detached && id is not null) PostResponse(id, ok: false, error: "The request was canceled or timed out.", elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (BridgeRequestRegistry.DuplicateRequestException)
        {
            // Not answered under that id: it belongs to the request still running, whose own answer is coming.
            AppLog.Warn($"RPC {method}: a request reused the id of one still running; dropped.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"RPC {method} {id}: {ex.Message}");
            // A failure nobody planned for is reported on its own, so nobody has to copy a log (the daily limits apply).
            if (FailureReportRules.IsHostCallBug(ex)) CrashReport.Submit("HostCallFailed", FailureReportRules.HostCallReason(method ?? "unknown", ex), "rpc");
            // Handlers throw InvalidOperation/InvalidData with a line meant for the user; file and
            // Windows errors carry paths, which stay in native code (AGENTS.md).
            if (!_detached && id is not null)
                PostResponse(id, ok: false, error: UserMessage(ex, "Exo could not finish that. The details are in the log."), elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            scope?.Dispose();
            // A slow answer is what the UI waited on; installs, launches and
            // store sign-ins are long by nature and are left out.
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed >= 250 && method is not null && !IsLongRunningRpc(method))
                AppLog.Info($"PERF rpc method={method} elapsedMs={elapsed:F0} pool={ThreadPool.ThreadCount}/{ThreadPool.PendingWorkItemCount}{(perfKey is null ? "" : " key=" + perfKey)}");
        }
    }

    private static bool IsLongRunningRpc(string method) =>
        method.StartsWith("game.", StringComparison.Ordinal) && method is not "game.get" ||
        method.Contains("install", StringComparison.OrdinalIgnoreCase) ||
        method.Contains("Update", StringComparison.Ordinal) && method is not "app.checkUpdate" ||
        method is "shell.pickFolder" or "shell.pickProgram" or "library.refresh" or "dlss.restore" or "library.refreshAll";
    private int _libraryTimingLogged;

    private async Task<object> LibraryGetAsync(JsonElement p, bool hasParams, bool force = false, bool full = false)
    {
        if (hasParams && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True)
            force = true;

        var libraryStarted = Stopwatch.GetTimestamp();
        var games = await _services.Library.GetLibraryAsync(force, full).ConfigureAwait(false);
        var libraryMs = Stopwatch.GetElapsedTime(libraryStarted).TotalMilliseconds;
        var settings = _services.Settings.Current;
        var projectionStarted = Stopwatch.GetTimestamp();
        var mapped = await Task.Run(() => games.Select(game => MapGame(game)).ToList()).ConfigureAwait(false);
        var projectionMs = Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds;
        if (Interlocked.Exchange(ref _libraryTimingLogged, 1) == 0)
        {
            AppLog.Info($"PERF first library.get libraryMs={libraryMs:F0} projectionMs={projectionMs:F0} count={mapped.Count}");
            Telemetry.Track("library.loaded", ("ms", (long)(libraryMs + projectionMs)), ("games", Telemetry.GameCountBucket(games.Count)),
                ("stores", games.Select(game => game.Store).Distinct().Count()));
        }
        return BuildLibraryResponse(
            mapped,
            games.Count,
            _services.Library.PeekStoreMatrix() ?? Array.Empty<LibraryService.StoreBackendStatus>(),
            _services.Launcher.CurrentProgress,
            _services.Launcher.QueuedGameIds,
            _services.Launcher.PausedDownloads,
            settings.SortMode,
            settings.Recent);
    }

    internal static object BuildLibraryResponse(
        IReadOnlyList<object> mappedGames,
        int count,
        IReadOnlyList<LibraryService.StoreBackendStatus> storeMatrix,
        InstallProgress progress,
        IReadOnlyList<string> queuedGameIds,
        IReadOnlyList<PausedDownload> pausedDownloads,
        string sortMode,
        IReadOnlyList<string> recent) =>
        new
        {
            games = mappedGames,
            count,
            stores = MapStoreMatrix(storeMatrix),
            progress = MapProgress(progress),
            queuedGameIds,
            pausedDownloads,
            sortMode,
            recent,
        };

    private async Task<object> GameGetAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found." };

        // Finding a game started outside Exo reads every process on the PC: off the UI thread.
        var mapped = await Task.Run(() => MapGame(game, discoverExternalRunningGame: true)).ConfigureAwait(true);
        return new { ok = true, game = mapped };
    }

    private const int UiLogMaxChars = 4000;

    /// <summary>
    /// The web UI's error channel. Bounded, tagged, and written to the same
    /// rolling app.log so a render crash or swallowed RPC failure is diagnosable
    /// from the crash folder without any analytics.
    /// </summary>
    private static object LogFromUi(JsonElement p, bool hasParams)
    {
        var level = ReadString(p, hasParams, "level")?.Trim().ToLowerInvariant();
        var message = ReadString(p, hasParams, "message")?.Trim();
        if (string.IsNullOrEmpty(message)) return new { ok = false, message = "Missing message." };
        var context = ReadString(p, hasParams, "context")?.Trim();
        var stack = ReadString(p, hasParams, "stack")?.Trim();
        var line = "UI " + (string.IsNullOrEmpty(context) ? "" : "[" + context + "] ") + message;
        if (!string.IsNullOrEmpty(stack)) line += " | " + stack.ReplaceLineEndings(" ");
        if (line.Length > UiLogMaxChars) line = line[..UiLogMaxChars] + "…";
        switch (level)
        {
            case "error":
                AppLog.Error(line);
                // A page error is reported on its own (an uncaught error, a rejected promise, a failure a room caught and
                // showed): nobody has to copy a log. The scrubber and the daily limits apply as to every report.
                CrashReport.Submit("UiError", FailureReportRules.PageErrorReason(context, message), "ui");
                break;
            case "warn": AppLog.Warn(line); break;
            default: AppLog.Info(line); break;
        }
        return new { ok = true };
    }

    /// <summary>A failed open for the page: what could not open, never the system message (it can carry a path). The log has the cause.</summary>
    private static object OpenFailed(string what, Exception ex)
    {
        AppLog.Warn($"Could not open {what}: {ex.GetType().Name}: {ex.Message}");
        return new { ok = false, message = $"Windows couldn’t open {what}." };
    }

    /// <summary>
    /// app.logsFor: what an error's Copy logs button copies (<see cref="AppLog.ReportFor"/>), put on Windows' clipboard here
    /// (still on the UI thread, which owns it): the page's own clipboard write fails without a word when the window lost
    /// the focus while the report was built, which left the old clipboard to be pasted. copied says it took.
    /// </summary>
    private async Task<object> LogsForAsync(JsonElement p, bool hasParams)
    {
        var error = ReadString(p, hasParams, "error") ?? "";
        var area = ReadString(p, hasParams, "area") ?? "";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (text, report) = await Task.Run(() =>
        {
            var built = AppLog.ReportFor(error.Length > 2000 ? error[..2000] : error, area, AppLog.RecentLines(), DateTime.UtcNow,
                _services.AppVersion, Environment.OSVersion.Version.ToString(), Environment.UserName, profile);
            CrashReport.Payload? payload = CrashReport.Build(error, built, _services.AppVersion, Environment.OSVersion.Version.ToString(), Environment.UserName, profile, Environment.MachineName);
            return (built, payload);
        }).ConfigureAwait(true);
        if (report is not null) _ = CrashReport.SendAsync(report);
        var copied = false;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            copied = true;
        }
        catch (Exception ex) { AppLog.Warn("Copy logs: the clipboard refused the report: " + ex.Message); }
        AppLog.Info($"Logs {(copied ? "copied" : "built")} for an error in {(area.Length == 0 ? "the app" : area)}.");
        return new { ok = true, text, copied };
    }

    /// <summary>report.preview: the log excerpt Report a problem's Attach logs sends, scrubbed as a crash report's (UserReport.Excerpt).</summary>
    private async Task<object> ReportPreviewAsync()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var excerpt = await Task.Run(() => UserReport.Excerpt(AppLog.RecentLines(), DateTime.UtcNow, _services.AppVersion,
            Environment.OSVersion.Version.ToString(), Environment.UserName, profile, Environment.MachineName)).ConfigureAwait(true);
        return new { ok = true, excerpt };
    }

    /// <summary>
    /// report.send: Report a problem's Send. The excerpt is the preview the player saw (null with Attach logs off); the host
    /// scrubs it again before it leaves (UserReport.ToJson). Answers the issue's number in Exo-Reports, or its reference.
    /// </summary>
    private async Task<object> ReportSendAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var message = ReadString(p, hasParams, "message") ?? "";
        var result = await UserReport.SendAsync(message, ReadString(p, hasParams, "excerpt"), ReadString(p, hasParams, "contact"), _services.AppVersion, ct).ConfigureAwait(true);
        return new { ok = result.Ok, issue = result.Issue, @ref = result.Ref, message = result.Message };
    }

    private static object OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(PathHelper.LogsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PathHelper.LogsDir) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return OpenFailed("the logs folder", ex);
        }
    }

    private async Task<object> GameLaunchAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        if (game.Store == StoreKind.Epic)
        {
            // A disk-hydrated card may still name Legendary's old folder.
            // Refresh the manifest overlay before launch and process tracking.
            await _services.Library.GetLibraryAsync(force: true).ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
            if (game is null)
                return new { ok = false, message = "Game not found. Refresh the library." };
        }

        await RecorderOutOfTheWayAsync(game).ConfigureAwait(true);
        if (ScsKillerStatus.LooksUnreal(game.Path))
            await Task.Run(() => UnrealShaderIni.Apply(game, _dlss.AntiCheatAnswer(game, _bridgeLifetime.Token), () => _services.Launcher.GetGameRunState(game).IsRunning)).ConfigureAwait(true);

        // Steam can't sign in because its servers are down: Play offers its offline mode instead of waiting on it.
        // Both Steam checks read its logs, process table and appinfo: off the UI thread.
        if (await Task.Run(() => SteamPlayOfflineFirst(game)).ConfigureAwait(true) is { } offlineFirst) return offlineFirst;

        // Controller settings wait for Steam's next close while it runs (never closing it).
        if (game.Store == StoreKind.Steam && ExoLauncher.Adapters.Cli.SteamProtocol.IsValidAppId(game.LaunchTarget) &&
            await Task.Run(SteamDialogPending).ConfigureAwait(true))
        {
            try { await ApplySteamOverlayAsync(_bridgeLifetime.Token).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Warn($"Steam: settings before {game.Title} not written: {ex.Message}");
            }
        }

        // A game that isn't Steam's, sent through Steam Input: Steam starts it from its shortcut.
        if (await PlayThroughSteamAsync(game).ConfigureAwait(true) is { } throughSteam) return throughSteam;

        // A Steam Family game that plays offline: Steam goes offline first, and back online once it closes (SteamMode).
        var (offlineForFamily, steamNote) = await Task.Run(() => SteamModeForPlayAsync(game)).ConfigureAwait(true);

        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = true,
            message = "Preparing launch…",
            phase = "preparing",
        });

        var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;
        var result = await _services.Launcher.LaunchAsync(game, skipDeps).ConfigureAwait(true);
        NoteSteamPlayAnswer(result);
        // A saved launch option this Play left out for the game's anti-cheat: said once on the game's page as it runs.
        if (result.Ok && _services.Launcher.TakeDroppedNote(game.Id) is { } dropped) steamNote ??= dropped;
        if (offlineForFamily && !result.Ok && !result.NeedsDependencies)
        {
            result = await PlayOnlineAfterOfflineAsync(game, skipDeps, result).ConfigureAwait(true);
            steamNote = RunningOnline(SteamOfflineMode.Words(SteamOfflineMode.Online.WouldNotStart));
        }
        if (result.Ok && steamNote is not null) result = new LaunchResult
        {
            Ok = true, Message = steamNote, ProcessId = result.ProcessId, BackendStarted = result.BackendStarted, HandoffOnly = result.HandoffOnly,
        };

        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = result.Ok,
            message = result.Ok
                ? (string.IsNullOrWhiteSpace(result.Message) ? "Running" : result.Message)
                : result.Message,
            processId = result.ProcessId,
            backendStarted = result.BackendStarted,
            handoffOnly = result.HandoffOnly,
            needsDependencies = result.NeedsDependencies,
            phase = result.HandoffOnly ? "handoff" : (result.Ok ? "running" : (result.NeedsDependencies ? "needsDeps" : "failed")),
        });

        return MapDepAwareResult(result);
    }

    private async Task<object> GameStopAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        var result = await _services.Launcher.StopGameAsync(game).ConfigureAwait(true);
        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = result.Ok,
            message = result.Message,
            phase = result.Ok ? "stopped" : "stopFailed",
        });
        return new { ok = result.Ok, message = result.Message };
    }

    private async Task<object> KeysPreviewAsync()
    {
        string? raw = null;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                raw = await content.GetTextAsync();
        }
        catch
        {
            /* clipboard can be empty or locked */
        }

        var key = GameKeyActivation.Normalize(raw);
        if (!GameKeyActivation.LooksLikeKey(key))
            return new { ok = true, looksLikeKey = false, store = "unknown", key = "" };

        return new
        {
            ok = true,
            looksLikeKey = true,
            store = GameKeyActivation.DetectStore(key),
            key,
        };
    }

    private async Task<object> KeysActivateAsync(JsonElement p, bool hasParams)
    {
        var key = GameKeyActivation.Normalize(ReadString(p, hasParams, "key"));
        if (key.Length == 0)
            return new { ok = false, message = "Paste a product key first." };

        var store = GameKeyActivation.ResolveStore(key, ReadString(p, hasParams, "store"));
        if (store is not ("steam" or "epic" or "gog"))
            return new { ok = false, message = "Choose Steam, Epic, or GOG." };

        var targetStore = store switch { "epic" => StoreKind.Epic, "gog" => StoreKind.Gog, _ => StoreKind.Steam };
        var ownedBefore = _services.Library.PeekCachedLibrary()
            .Where(game => game.Store == targetStore && game.Owned).Select(game => game.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var url = GameKeyActivation.RedeemUrl(store, key);
        if (store == "steam") OpenSteamProtocol(url);
        else if (!ExoUrlLaunch.TryStart(url))
            return new { ok = false, message = "Could not open the redeem page." };

        StartKeyWatch(targetStore, ownedBefore);
        return new
        {
            ok = true,
            store,
            message = store switch
            {
                "steam" => "Steam's key page opened with your key filled in. Accept Steam's agreement and press Continue.",
                "gog" => "GOG's redeem page opened in your browser with your key filled in. Press Continue, then Redeem.",
                _ => "Epic's redeem page opened in your browser. Paste your key there and press Redeem.",
            },
        };
    }

    private void StartKeyWatch(StoreKind store, HashSet<string> owned)
    {
        if (_detached) return;
        try { _keyWatchCts?.Cancel(); } catch { /* previous watch */ }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_bridgeLifetime.Token);
        _keyWatchCts = cts;
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Three bounded, store-specific reads replace fifteen full
                // library invalidations. Keys never overwrite the clipboard.
                foreach (var delay in new[] { 5, 10, 15 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    var next = await _services.Library.RefreshStoreAsync(store, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var added = next.FirstOrDefault(game =>
                        game.Store == store && game.Owned && !owned.Contains(game.Id));
                    if (added is null) continue;
                    PostEvent("keys.found", new
                    {
                        ok = true,
                        gameId = added.Id,
                        title = added.Title,
                        message = added.Title + " is in your library.",
                    });
                    return;
                }

                ct.ThrowIfCancellationRequested();
                PostEvent("keys.found", new
                {
                    ok = false,
                    message = "No new game was confirmed. Finish redeeming in the store, then refresh the library.",
                });
            }
            catch (OperationCanceledException)
            {
                /* dismissed */
            }
            catch (Exception ex)
            {
                AppLog.Debug("Key watch failed: " + ex.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref _keyWatchCts, null, cts);
                cts.Dispose();
            }
        });
    }

    private async Task<object> GameInstallAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        // Auto root: Settings override → %LOCALAPPDATA%\ExoLauncher\Games
        var requested = ReadString(p, hasParams, "path");
        // Add a game registers a program that Exo will run: only one the user just chose in Windows' picker.
        if (gameId == Adapters.LocalAdapter.AddPortableId && !Helpers.PickedPaths.Consume(requested))
            return new { ok = false, message = "Choose the game in the file picker first." };
        if (string.IsNullOrWhiteSpace(requested))
            requested = _services.Settings.Current.DefaultInstallRoot;
        string path;
        if (string.IsNullOrWhiteSpace(requested))
            path = Helpers.PathHelper.GamesRoot;
        else if (NormalizeInstallRoot(requested) is { } validRoot)
            path = validRoot;
        // Add a game: the game's own .exe, its folder held to the same rules as a folder.
        else if (gameId == Adapters.LocalAdapter.AddPortableId && ProgramInAllowedFolder(requested) is { } program)
            path = program;
        else
            return new { ok = false, message = InstallRootRejected };

        var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!)
                   ?? TrySynthesizeFromId(gameId!, p, hasParams);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };
        if (!game.Owned && !OfficialClientCatalog.IsOfficialClientCatalogInstall(game))
            return new { ok = false, message = "This title is not owned by the active store account. Buy it from the store first." };

        // A drive left nearly full slows down and can't patch: said on the game's line with the roomiest SSD named, never
        // asked a second time (the Install press already asked, DESIGN.md › Confirm); one it can't fit on is refused.
        var room = gameId == Adapters.LocalAdapter.AddPortableId ? null : await InstallRoomAsync(game, path).ConfigureAwait(true);
        if (room is { Fits: false }) return new { ok = false, message = room.Value.Words };

        var result = await _services.Launcher.InstallAsync(game, path, skipDeps).ConfigureAwait(true);
        if (result.Ok)
            _services.Library.Invalidate();

        return new
        {
            ok = result.Ok,
            message = result.Ok && room is { Words: var low } ? string.Join(" ", new[] { result.Message, low }.Where(words => !string.IsNullOrEmpty(words))) : result.Message,
            handoffOnly = result.HandoffOnly,
            needsDependencies = result.NeedsDependencies,
            missingDependencies = MapMissingDeps(result.MissingDependencies),
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    /// <summary>An existing .exe whose folder <see cref="NormalizeInstallRoot"/> takes; its full path, else null.</summary>
    private static string? ProgramInAllowedFolder(string? path)
    {
        try
        {
            var full = Path.GetFullPath(path!.Trim());
            return string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full) &&
                NormalizeInstallRoot(Path.GetDirectoryName(full)) is not null ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    private const string InstallRootRejected =
        "Choose a folder on a local drive, outside Windows and not Program Files itself.";

    /// <summary>
    /// Install roots from the UI reach store clients as targets. Only an
    /// absolute folder on a local fixed or removable drive is accepted: not a
    /// share, not under Windows, not a Program Files root, and either existing
    /// or creatable (no path segment is a file). Returns the full path.
    /// </summary>
    private static string? NormalizeInstallRoot(string? path)
    {
        try
        {
            var trimmed = path?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed) ||
                trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
                return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                return null;
            static bool Same(string left, string right) => string.Equals(left,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows) && (Same(full, windows) || ProcessHelper.IsPathUnderRoot(full, windows)))
                return null;
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrEmpty(programFiles) && Same(full, programFiles))
                    return null;
            }
            for (var probe = full; probe is not null; probe = Path.GetDirectoryName(probe))
            {
                if (Directory.Exists(probe)) return full;
                if (File.Exists(probe)) return null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<object> GameResumeDownloadAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId)) return new { ok = false, message = "Missing game id." };
        var result = await _services.Launcher.ResumeDownloadAsync(gameId).ConfigureAwait(true);
        if (result.Ok && !result.Queued) _services.Library.Invalidate();
        return new
        {
            ok = result.Ok, message = result.Message, queued = result.Queued,
            handoffOnly = result.HandoffOnly, queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    /// <summary>
    /// Install target the library lookup missed: an owned or installed source
    /// row from the cached library, or an official store client Exo can install.
    /// </summary>
    private GameEntry? TrySynthesizeFromId(string gameId, JsonElement p, bool hasParams)
    {
        var title = ReadString(p, hasParams, "title") ?? gameId;
        var fromLibrary = TryLibraryOwnedSource(gameId);
        if (fromLibrary is not null)
        {
            return new GameEntry
            {
                Id = fromLibrary.Id,
                Title = string.IsNullOrWhiteSpace(title) || title == gameId ? fromLibrary.Title : title,
                Store = fromLibrary.Store,
                Installed = fromLibrary.Installed,
                Owned = fromLibrary.Owned,
                EntitlementState = fromLibrary.EntitlementState,
                CanInstall = !fromLibrary.Installed && fromLibrary.Owned && fromLibrary.CanInstall,
                Path = fromLibrary.Path,
                LaunchTarget = fromLibrary.LaunchTarget,
                CoverUrl = fromLibrary.CoverUrl,
                Status = fromLibrary.Installed ? fromLibrary.Status : "Not installed",
                Deps = fromLibrary.Deps,
                LaunchNote = fromLibrary.LaunchNote,
            };
        }

        // Every owned Steam app is a library row keyed steam:<appid>, so the
        // library lookup above already covers Steam; nothing else is synthesized.
        return OfficialClientCatalog.TrySynthesizeOfficialClientInstall(gameId, title);
    }

    private GameEntry? TryLibraryOwnedSource(string gameId)
    {
        foreach (var game in _services.Library.PeekCachedLibrary())
        {
            if (string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase) &&
                (game.Owned || game.Installed))
                return game;
            var variant = game.Variants.FirstOrDefault(item =>
                string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (variant is not null && (variant.Owned || variant.Installed))
                return variant.ToGameEntry(game);
        }
        return null;
    }

    private async Task<object> GameUpdateAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        try
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            var game = _services.Library.Find(gameId!);
            if (game is null)
                return new { ok = false, message = "Game not found. Refresh the library." };

            var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;
            var result = await _services.Launcher.UpdateAsync(game, skipDeps).ConfigureAwait(true);
            if (result.Ok)
                _services.Library.Invalidate();

            return new
            {
                ok = result.Ok,
                message = result.Message,
                    handoffOnly = result.HandoffOnly,
                needsDependencies = result.NeedsDependencies,
                missingDependencies = MapMissingDeps(result.MissingDependencies),
                queued = result.Queued,
                queuedGameIds = _services.Launcher.QueuedGameIds,
                pausedDownloads = _services.Launcher.PausedDownloads,
                progress = MapProgress(_services.Launcher.CurrentProgress),
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"game.update failed: {ex}");
            return new
            {
                ok = false,
                // A file or Windows error carries a path: it stays in the log (AGENTS.md).
                message = UserMessage(ex, "") is { Length: > 0 } line ? "Update failed: " + line : "Update failed. The details are in the log.",
                progress = MapProgress(_services.Launcher.CurrentProgress),
            };
        }
    }

    private async Task<object> GameUninstallAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        // Who the game is, read while it is still there: what Windows keeps about it goes once it is gone.
        var trail = await Task.Run(() => GameTrail(game)).ConfigureAwait(true);
        var antiCheatBefore = await Task.Run(DependencyService.AntiCheatPresent).ConfigureAwait(true);
        var result = await _services.Launcher.UninstallAsync(game).ConfigureAwait(true);
        if (result.Ok && !result.Queued && antiCheatBefore.Count > 0) _ = WatchAntiCheatAsync(game, antiCheatBefore);
        if (result.Ok && !result.Queued && !result.HandoffOnly)
        {
            _services.Library.Invalidate();
            if (trail is not null) _ = Task.Run(() => ClearGameTraces(game.Title, trail));
        }
        if (result.Ok && result.HandoffOnly)
            _ = FollowStoreRemovalAsync(game, trail);
        return new
        {
            ok = result.Ok,
            queued = result.Queued,
            handoffOnly = result.HandoffOnly,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            message = result.Message,
        };
    }

    /// <summary>
    /// A store's uninstall can take Easy Anti-Cheat with the game: it runs the anti-cheat's own uninstall, and the service
    /// is the PC's, shared by every game and every store's copy (Rocket League from Steam took it from Epic's copy). For
    /// ten minutes after a removal, while the store finishes it, a service that was there and is gone is said at once,
    /// with Repair, for an installed game that ships the setup and so needs it; else Play asks before that game starts.
    /// </summary>
    private async Task WatchAntiCheatAsync(GameEntry removed, IReadOnlySet<string> before)
    {
        var ct = _bridgeLifetime.Token;
        try
        {
            for (var deadline = DateTimeOffset.UtcNow.AddMinutes(10); DateTimeOffset.UtcNow < deadline;)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var gone = before.Where(service => !DependencyService.ServiceExists(service)).ToList();
                if (gone.Count == 0) continue;
                var games = await _services.Library.GetLibraryAsync().ConfigureAwait(false);
                var needing = games.Where(g => g.Installed && !string.Equals(g.Id, removed.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(g => (Game: g, Need: DependencyService.MissingAntiCheat(g)))
                    .FirstOrDefault(x => x.Need is not null);
                AppLog.Info($"Anti-cheat: {string.Join(", ", gone)} went with {removed.Title}; {(needing.Need is null ? "no installed game needs it" : needing.Game.Title + " needs it")}.");
                if (needing.Need is { } need)
                {
                    // Two copies of one game (Steam's removed, Epic's left): each named with its store.
                    var same = string.Equals(removed.Title, needing.Game.Title, StringComparison.OrdinalIgnoreCase);
                    PostEvent("anticheat.missing", new
                    {
                        gameId = needing.Game.Id,
                        title = same ? $"{needing.Game.Title} on {needing.Game.Store}" : needing.Game.Title,
                        removed = same ? $"{removed.Title} on {removed.Store}" : removed.Title,
                        dependencyId = need.Id,
                        name = need.Name,
                    });
                }
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Anti-cheat watch: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>
    /// A removal handed to the store's own app finishes there, after Exo answered. Its install is checked until it
    /// is gone (20 minutes at most), then that store is re-read so the card changes by itself: GOG, Ubisoft and
    /// Battle.net keep installs in the registry, where no library watcher sees one go.
    /// </summary>
    private async Task FollowStoreRemovalAsync(GameEntry game, Trail? trail)
    {
        Func<bool> stillInstalled = game.Store == StoreKind.Gog && GogAdapter.ExtractGogId(game) is { Length: > 0 } gogId
            ? () => GogAdapter.GogRegistryStillInstalled(gogId)
            : () => OfficialInstalledLibraries.IsStillInstalled(game);
        var ct = _bridgeLifetime.Token;
        try
        {
            for (var deadline = DateTimeOffset.UtcNow.AddMinutes(20); DateTimeOffset.UtcNow < deadline;)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (await Task.Run(stillInstalled, ct).ConfigureAwait(false)) continue;
                AppLog.Info($"Store removal finished: gameId={game.Id}.");
                if (trail is not null) ClearGameTraces(game.Title, trail);
                await _services.Library.RefreshStoreAsync(game.Store, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) { /* Exo is closing */ }
        catch (Exception ex) { AppLog.Debug($"Following the removal of {game.Id} stopped: {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<object> GameRepairAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var result = await _services.Launcher.RepairAsync(game).ConfigureAwait(true);
        return new
        {
            ok = result.Ok,
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            message = result.Message,
            handoffOnly = result.HandoffOnly,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    private async Task<object> GameExtrasAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };
        if (_bannerAsked.Add(game.Id)) _ = WarmBannerAsync(game.Id);

        // Cached counts only; a played game with none (or old ones) is read in
        // the background and pushed as game.achievements.
        var achievements = await Task.Run(() => _services.Achievements.GetSummary(game.Id)).ConfigureAwait(true);
        if (game.PlaytimeMinutes > 0 || game.LastPlayedUtc is not null)
            _services.Achievements.RefreshInBackgroundIfStale(game, achievements);
        var hasAchievements = achievements is { Total: > 0 };
        return new
        {
            ok = true,
            achievementsUnlocked = hasAchievements ? achievements!.Unlocked : (int?)null,
            achievementsTotal = hasAchievements ? achievements!.Total : (int?)null,
            driveHeat = DriveHeat.Last(game.Id) is { } heat ? new { peak = heat.PeakC, warn = heat.WarnC, slowedSeconds = heat.SlowedSeconds } : null,
        };
    }

    // Raised on a worker thread; PostEvent marshals to the dispatcher.
    private void OnAchievementsChanged(string gameId)
    {
        var summary = _services.Achievements.GetSummary(gameId);
        if (summary is not { Total: > 0 }) return;
        PostEvent("game.achievements", new { id = gameId, unlocked = summary.Unlocked, total = summary.Total });
    }

    private object GameOpenFolder(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        var path = game.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
                path = Path.GetDirectoryName(game.LaunchTarget);
        }

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new { ok = false, message = "Install folder not found." };

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return OpenFailed("the game folder", ex);
        }
    }

    private async Task<object> ArtworkReplaceAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        var card = _services.Library.FindVisualCard(gameId);
        if (card is null)
            return new { ok = false, cancelled = false, message = "That title is not in your library." };
        if (string.Equals(card.Id, "local:add", StringComparison.OrdinalIgnoreCase) ||
            (!card.Owned && !card.Installed))
            return new { ok = false, cancelled = false, message = "Artwork controls are available for library titles." };

        // The RPC has no path parameter. Only this native picker can produce the
        // path passed to the image store.
        var picked = await PickImageFileAsync().ConfigureAwait(true);
        if (picked.Cancelled)
            return new { ok = false, cancelled = true, message = "No changes made." };
        if (picked.Path is null)
            return new { ok = false, cancelled = false, message = "The image picker could not open." };

        var result = await _services.Artwork.ReplaceAsync(gameId, picked.Path).ConfigureAwait(true);
        return MapArtworkMutation(result);
    }

    private async Task<object> ArtworkResetAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        return MapArtworkMutation(await _services.Artwork.ResetAsync(gameId).ConfigureAwait(true));
    }

    private async Task<object> ArtworkRefetchAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        return MapArtworkMutation(await _services.Artwork.RefetchAsync(gameId).ConfigureAwait(true));
    }

    /// <summary>
    /// library.folderArt: the folder sheet's cropped picture (base64), stored for the folder's id; the sheet's Save then
    /// names it in the folder. Any picture no folder holds (one picked before and cancelled) goes.
    /// </summary>
    private async Task<object> LibraryFolderArtAsync(JsonElement p, bool hasParams)
    {
        var image = ReadString(p, hasParams, "image") ?? "";
        if (image.Length > ExoBridgeProtocol.MaxFolderArtCharacters) return new { ok = false, message = "Pictures have to be under 15 MB." };
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image); }
        catch (FormatException) { return new { ok = false, message = "That file isn't a picture." }; }
        var saved = await _folderArt.SaveAsync(ReadString(p, hasParams, "folderId"), bytes).ConfigureAwait(true);
        if (!saved.Ok) return new { ok = false, message = saved.Message };
        _folderArt.DeleteUnreferenced([.. _services.Settings.Current.LibraryFolders.Select(folder => folder.Art), saved.FileName]);
        return new { ok = true, art = _folderArt.ResolveUrl(saved.FileName) };
    }

    private object MapArtworkMutation(GameArtworkService.MutationResult result) => new
    {
        result.Ok,
        result.Cancelled,
        result.Message,
        game = result.Game is null ? null : MapGame(result.Game),
        result.ArtRevision,
    };

    private async Task<object> StoresAuthAsync(JsonElement p, bool hasParams)
    {
        var storeId = ReadString(p, hasParams, "store");
        if (string.IsNullOrWhiteSpace(storeId))
            return new { ok = false, message = "Missing store id." };

        var adapter = _services.FindAdapterById(storeId!);
        if (adapter is null)
            return new { ok = false, message = "Unknown store." };

        AppLog.Info($"stores.auth: {adapter.Id} sign-in started.");
        var result = await adapter.AuthenticateAsync().ConfigureAwait(true);
        if (result.Handoff)
        {
            // The store signs in inside its own app. Only claim the handoff once
            // that app's window is actually open; nothing is connected yet.
            var opened = await OpenStoreClientAsync(adapter.Id).ConfigureAwait(true);
            AppLog.Info($"stores.auth: {adapter.Id} handoff to its own app: {(opened.Ok ? "opened" : opened.Message)}");
            return opened.Ok
                ? new { ok = true, message = result.Message, requiresUserAction = true, handoff = true }
                : new { ok = false, message = opened.Message, requiresUserAction = true, handoff = false };
        }
        if (result.Ok)
        {
            AppLog.Info($"stores.auth: {adapter.Id} connected. {result.Message}");
            _services.Library.InvalidateStoreMatrix();
        }
        else
            AppLog.Warn($"stores.auth: {adapter.Id} did not connect: {result.Message}");

        return new
        {
            ok = result.Ok,
            message = result.Message,
            requiresUserAction = result.RequiresUserAction,
            handoff = false,
        };
    }

    /// <summary>
    /// Riot hours from tracker.gg, asked for only by hand. A Riot ID saved in Settings imports its lifetime total once (one
    /// that already has a total keeps it); Settings › Stores › Riot ID › Import hours (no ID) reads the saved one again. After
    /// that Exo's own session log is the only source.
    /// </summary>
    private Task<object> RiotHoursRefreshAsync(JsonElement p, bool hasParams)
    {
        var typed = hasParams &&
                    p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("riotId", out var id) &&
                    id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        return ImportRiotHoursAsync(typed, CancellationToken.None);
    }

    private async Task<object> ImportRiotHoursAsync(string? typedId, CancellationToken ct)
    {
        var riotId = RiotIdStore.Normalize(typedId) ?? (typedId is null ? RiotIdStore.TryRead() : null);
        if (riotId is null)
            return new { ok = false, profilePrivate = false, source = (string?)null, clientNeeded = false, message = "Save a Riot ID first (Name#TAG)." };
        var account = RiotLedger.AccountKey(riotId)!;

        if (typedId is not null) RiotIdStore.Save(riotId);

        if (Interlocked.CompareExchange(ref _riotHoursRefreshing, 1, 0) != 0)
            return new { ok = false, profilePrivate = false, source = (string?)null, clientNeeded = false, message = "Hours refresh is already running." };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            var local = await RiotLocalMatchHistory.SyncAsync(account, ct: timeout.Token).ConfigureAwait(false);
            var cloud = await RiotHoursClient.LoadAsync(riotId, timeout.Token).ConfigureAwait(false);

            var cloudWrote = false;
            if (cloud.Ok)
            {
                var now = DateTimeOffset.UtcNow;
                var importedAt = cloud.LatestMatchUtc() is { } latest && latest <= now ? latest : now;
                cloudWrote = RiotLedger.SeedIfHigher(account, cloud.ToSeeds(), importedAt, RiotLedger.CloudSource);
            }

            if (!local.Ok && !cloud.Ok)
            {
                var clientNeeded = !local.ClientAvailable && (cloud.Unconfigured || cloud.TransientFailure);
                var message = clientNeeded
                    ? "Open Riot Client (signed in) and press Sync hours again, or import your Riot data export."
                    : local.AuthorizationRejected
                        ? "Riot denied local match history lookup. Try importing your Riot data export instead."
                        : cloud.Message;
                return new
                {
                    ok = false,
                    profilePrivate = false,
                    unconfigured = cloud.Unconfigured,
                    source = (string?)null,
                    clientNeeded,
                    message,
                    profileUrl = TrackerGgPlaytime.ProfilePageUrl(riotId),
                    baseline = RiotBaselineFor(riotId),
                };
            }

            var wrote = local.SeedUpdated || cloudWrote;
            if (wrote)
            {
                try
                {
                    await _services.Library.RefreshDerivedStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Riot hours library refresh skipped: " + ex.GetType().Name);
                }
            }

            var baselines = RiotLedger.Baselines(account);
            baselines.TryGetValue("riot:valorant", out var valMinutes);
            baselines.TryGetValue("riot:league_of_legends", out var lolMinutes);
            baselines.TryGetValue("riot:teamfighttactics", out var tftMinutes);
            var source = local.SeedUpdated || (local.Ok && !cloudWrote) ? "local" : "service";
            var okMessage = !wrote
                ? "Your saved Riot hours are already up to date."
                : source == "local"
                    ? local.Message
                    : cloud.Message;

            return new
            {
                ok = true,
                skipped = !wrote,
                profilePrivate = false,
                partial = cloud.Ok ? cloud.Readings.Values.Any(r => r.Partial) : local.Seeds.Values.Any(s => s.Partial),
                unconfigured = false,
                source,
                clientNeeded = false,
                message = okMessage,
                valorantMinutes = valMinutes > 0 ? valMinutes : (int?)null,
                leagueMinutes = lolMinutes > 0 ? lolMinutes : (int?)null,
                tftMinutes = tftMinutes > 0 ? tftMinutes : (int?)null,
                profileUrl = TrackerGgPlaytime.ProfilePageUrl(riotId),
                baseline = RiotBaselineFor(riotId),
            };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, profilePrivate = false, source = (string?)null, clientNeeded = false, message = "Riot hours sync did not answer in time." };
        }
        finally
        {
            Interlocked.Exchange(ref _riotHoursRefreshing, 0);
        }
    }

    private async Task<object> ImportRiotExportAsync(JsonElement p, bool hasParams)
    {
        var typed = ReadString(p, hasParams, "riotId");
        var riotId = RiotIdStore.Normalize(typed) ?? (typed is null ? RiotIdStore.TryRead() : null);
        if (riotId is null)
            return new { ok = false, cancelled = false, message = "Save a Riot ID first (Name#TAG)." };
        var account = RiotLedger.AccountKey(riotId)!;
        if (typed is not null) RiotIdStore.Save(riotId);

        var picked = await PickRiotExportFileAsync().ConfigureAwait(false);
        if (picked.Cancelled || string.IsNullOrWhiteSpace(picked.Path))
            return new { ok = false, cancelled = true, message = picked.Message };

        var imported = await RiotDataExportImporter.ImportFromFileAsync(picked.Path, CancellationToken.None).ConfigureAwait(false);
        if (!imported.Ok)
            return new { ok = false, cancelled = false, message = imported.Message };

        var now = DateTimeOffset.UtcNow;
        var importedAt = imported.LatestMatchUtc is { } latest && latest <= now ? latest : now;
        if (!RiotLedger.Seed(account, imported.Seeds, importedAt, RiotLedger.ExportSource))
            return new { ok = false, cancelled = false, message = "Riot export hours could not be saved." };

        try
        {
            await _services.Library.RefreshDerivedStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot export library refresh skipped: " + ex.GetType().Name);
        }

        return new
        {
            ok = true,
            cancelled = false,
            message = imported.Message,
            matchCounts = imported.MatchCounts,
            baseline = RiotBaselineFor(riotId),
        };
    }

    private async Task<PickedFile> PickRiotExportFileAsync()
    {
        var tcs = new TaskCompletionSource<PickedFile>();
        void Run() => _ = RunPickRiotExportAsync(tcs);
        if (!_queue.HasThreadAccess)
            _queue.Enqueue(Run);
        else
            Run();
        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickRiotExportAsync(TaskCompletionSource<PickedFile> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, "The file picker could not open."));
                return;
            }
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.FileTypeFilter.Add(".zip");
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, null));
                return;
            }
            tcs.TrySetResult(new PickedFile(file.Path, false, null));
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(new PickedFile(null, false, UserMessage(ex, "Exo could not open the picker.")));
        }
    }

    private async Task<object> SetRiotBaselineAsync(JsonElement p, bool hasParams)
    {
        var typed = ReadString(p, hasParams, "riotId");
        var riotId = RiotIdStore.Normalize(typed) ?? (typed is null ? RiotIdStore.TryRead() : null);
        if (riotId is null)
            return new { ok = false, message = "Save a Riot ID first (Name#TAG)." };
        var account = RiotLedger.AccountKey(riotId)!;
        if (typed is not null) RiotIdStore.Save(riotId);

        if (!TryParseBaselineMinutes(p, hasParams, "valorantHours", "valorantMinutes", out var valorant, out var err)
            || !TryParseBaselineMinutes(p, hasParams, "leagueHours", "leagueMinutes", out var league, out err)
            || !TryParseBaselineMinutes(p, hasParams, "tftHours", "tftMinutes", out var tft, out err)
            || !TryParseBaselineMinutes(p, hasParams, "lorHours", "lorMinutes", out var lor, out err)
            || !TryParseBaselineMinutes(p, hasParams, "twoXkoHours", "twoXkoMinutes", out var twoXko, out err))
        {
            return new { ok = false, message = err ?? "Enter a valid number of hours." };
        }

        var seeds = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
        if (valorant > 0) seeds["riot:valorant"] = new GameSeed(valorant);
        if (league > 0) seeds["riot:league_of_legends"] = new GameSeed(league);
        if (tft > 0) seeds["riot:teamfighttactics"] = new GameSeed(tft);
        if (lor > 0) seeds["riot:bacon"] = new GameSeed(lor);
        if (twoXko > 0) seeds["riot:lion"] = new GameSeed(twoXko);
        if (seeds.Count == 0)
            return new { ok = false, message = "Enter starting hours for at least one Riot game." };

        if (!RiotLedger.Seed(account, seeds, DateTimeOffset.UtcNow, RiotLedger.ManualSource))
            return new { ok = false, message = "Starting hours could not be saved." };

        try
        {
            await _services.Library.RefreshDerivedStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot baseline library refresh skipped: " + ex.GetType().Name);
        }

        return new
        {
            ok = true,
            message = "Starting hours saved.",
            baseline = RiotBaselineFor(riotId),
        };
    }

    private static bool TryParseBaselineMinutes(
        JsonElement p,
        bool hasParams,
        string hoursProp,
        string minutesProp,
        out int minutes,
        out string? error)
    {
        minutes = 0;
        error = null;
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return true;
        if (p.TryGetProperty(hoursProp, out var hoursEl) && hoursEl.ValueKind != JsonValueKind.Null)
        {
            if (hoursEl.ValueKind != JsonValueKind.Number || !hoursEl.TryGetDouble(out var hours) || double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0)
            {
                error = "Enter a valid number of hours.";
                return false;
            }
            if (hours > 0)
            {
                var scaled = hours * 60d;
                if (scaled > RiotLedger.MaxSeedMinutes)
                {
                    error = "Starting hours is too large.";
                    return false;
                }
                minutes = Math.Clamp((int)Math.Round(scaled), 1, RiotLedger.MaxSeedMinutes);
            }
            return true;
        }
        if (p.TryGetProperty(minutesProp, out var minsEl) && minsEl.ValueKind != JsonValueKind.Null)
        {
            if (minsEl.ValueKind != JsonValueKind.Number || !minsEl.TryGetInt32(out var raw) || raw < 0 || raw > RiotLedger.MaxSeedMinutes)
            {
                error = "Starting hours is out of range.";
                return false;
            }
            minutes = raw;
        }
        return true;
    }

    private static object? RiotBaselineFor(string? riotId)
    {
        var normalized = RiotIdStore.Normalize(riotId);
        if (normalized is null) return null;
        var account = RiotLedger.AccountKey(normalized);
        if (account is null) return null;
        var baselines = RiotLedger.Baselines(account);
        baselines.TryGetValue("riot:valorant", out var valorant);
        baselines.TryGetValue("riot:league_of_legends", out var league);
        baselines.TryGetValue("riot:teamfighttactics", out var tft);
        baselines.TryGetValue("riot:bacon", out var lor);
        baselines.TryGetValue("riot:lion", out var twoXko);
        return new
        {
            valorantMinutes = valorant > 0 ? valorant : (int?)null,
            leagueMinutes = league > 0 ? league : (int?)null,
            tftMinutes = tft > 0 ? tft : (int?)null,
            lorMinutes = lor > 0 ? lor : (int?)null,
            twoXkoMinutes = twoXko > 0 ? twoXko : (int?)null,
        };
    }

    /// <summary>The picked file's path, or why there is none. Only the host may name a file.</summary>
    private sealed record PickedFile(string? Path, bool Cancelled, string? Message);

    private async Task<PickedFile> PickImageFileAsync()
    {
        var tcs = new TaskCompletionSource<PickedFile>();
        void Run() => _ = RunPickImageAsync(tcs);

        if (!_queue.HasThreadAccess)
            _queue.Enqueue(Run);
        else
            Run();

        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickImageAsync(TaskCompletionSource<PickedFile> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, "The file picker could not open."));
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
                picker.FileTypeFilter.Add(extension);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, null));
                return;
            }

            tcs.TrySetResult(new PickedFile(file.Path, false, null));
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(new PickedFile(null, false, UserMessage(ex, "Exo could not open the picker.")));
        }
    }

    private async Task<object> StoresCheckAsync()
    {
        var check = await Task.Run(_services.Library.CheckStoresLocal).ConfigureAwait(false);
        return new
        {
            check.state,
            checkedAtUtc = check.checkedAtUtc.ToString("O"),
            check.code,
            stores = check.stores.Select(store => new
            {
                store.store,
                store.client,
                store.backend,
                store.session,
                store.cache,
                store.readiness,
                store.code,
            }).ToList(),
        };
    }

    /// <summary>
    /// Every caller uses this mapper; library.get cannot race out a status-only row.
    /// </summary>
    private object StoreMatrixMapped() =>
        MapStoreMatrix(_services.Library.StoreMatrix());

    private static object MapStoreMatrix(IReadOnlyList<LibraryService.StoreBackendStatus> statuses) =>
        statuses.Select(status => new
        {
            status.store,
            status.displayName,
            status.agentPresent,
            status.clientPresent,
            status.backendPresent,
            status.signedIn,
            status.cachePresent,
            status.detail,
            status.checkCode,
            checkedAtUtc = status.checkedAtUtc.ToString("O"),
        }).ToList();

    private object GameProgress(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        return MapProgress(_services.Launcher.GetProgress(gameId));
    }

    private object DepsList() => new
    {
        items = _services.Dependencies.DetectAll().Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            detail = d.Detail,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToList(),
    };

    private object BuildSettings()
    {
        var s = _services.Settings.Current;
        var musicService = WebClientPreferences.EffectiveMusicService(WebClientPreferences.Read().MusicService);
        var riotId = RiotIdStore.TryRead();
        return BuildSettingsResponse(
            s,
            _services.AppVersion,
            musicService,
            riotId,
            RiotBaselineFor(riotId),
            DlssSwapService.FrameGenEverywhereCard(SystemSpecs.FastFrameGenGpu) is not null,
            ChipsetUpdate.ThisPc is { } update && update.Installed() is not null ? update.Vendor : null,
            _folderArt.ResolveUrl);
    }

    internal static object BuildSettingsResponse(
        AppSettings s,
        string appVersion,
        string musicService,
        string? riotId,
        object? riotBaseline,
        bool frameGenEverywhereCard,
        string? chipset,
        Func<string?, string?> resolveFolderArt) =>
        new
        {
            appVersion,
            whatsNewSeen = s.WhatsNewSeen,
            reduceMotion = s.ReduceMotion,
            startMaximized = s.StartMaximized,
            startWithWindows = s.StartWithWindows,
            framelessPinTop = s.FramelessPinTop,
            framelessPinSide = s.FramelessPinSide,
            textColour = s.TextColour,
            textSize = s.TextSize,
            background = s.Background,
            musicService,
            uiPrefs = WebClientPreferences.BuildSettingsUiPrefs(s.UiPrefs, musicService),
            sortMode = s.SortMode,
            defaultInstallRoot = s.DefaultInstallRoot,
            preferredGameSources = s.PreferredGameSources,
            recent = s.Recent,
            onboardingComplete = s.OnboardingComplete,
            riotId,
            riotBaseline,
            showLibraryTools = s.ShowLibraryTools,
            achievementBanners = s.AchievementBanners,
            achievementVolume = s.AchievementVolume,
            fpsCounter = s.FpsCounter,
            fpsCounterPosition = FpsOverlay.PositionOf(s.FpsCounterPosition),
            fpsCounterItems = s.FpsCounterItems,
            fpsCounterHideOverBuiltIn = s.FpsCounterHideOverBuiltIn,
            hdrForGames = s.HdrForGames,
            frameGenEverywhere = s.FrameGenEverywhere,
            frameGenEverywhereCard,
            graphicsFeaturesBestSettings = s.GraphicsFeaturesBestSettings,
            hdrSdrGamma = s.HdrSdrGamma,
            discordNoiseSuppression = s.DiscordNoiseSuppression,
            discordQuietWhilePlaying = s.DiscordQuietWhilePlaying,
            pauseDownloadsWhilePlaying = s.PauseDownloadsWhilePlaying,
            downloadHoursStart = s.DownloadHoursStart,
            downloadHoursEnd = s.DownloadHoursEnd,
            autoUpdateGames = s.AutoUpdateGames,
            autoUpdateChipset = s.AutoUpdateChipset,
            autoUpdateUpscalers = s.AutoUpdateUpscalers,
            upscalerAntiCheatEverywhere = s.UpscalerAntiCheatEverywhere,
            keepAwakeDownloads = s.KeepAwakeDownloads,
            storeClientsQuiet = s.StoreClientsQuiet,
            chipset,
            libraryFolders = s.LibraryFolders.Select(folder => new { id = folder.Id, name = folder.Name, color = folder.Color, sort = folder.Sort, art = resolveFolderArt(folder.Art), games = folder.Games }),
            hiddenGames = s.HiddenGames,
        };

    private object SetSettings(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object)
            return BuildSettings();

        // Locked/dead keys (closeStoreClientsAfterLaunch, autoInstallRedistributables,
        // minimizeWhilePlaying, antiCheatSafeMode, framelessMode, theme,
        // copyPortableIntoLibrary, allowResize, checkForUpdates,
        // libraryShowInstalled, libraryShowOwned) are ignored if an older UI sends them.
        bool? onboard = null;
        bool? reduceMotion = null;
        bool? startMaximized = null;
        bool? framelessPinTop = null, framelessPinSide = null;
        bool? showLibraryTools = null;
        string? textColour = null, textSize = null;
        string? sort = null, root = null;
        Dictionary<string, string>? preferredGameSources = null;

        if (p.TryGetProperty("preferredGameSources", out var preferredSources))
        {
            if (preferredSources.ValueKind != JsonValueKind.Object)
                throw new UserFacingException("Preferred game sources must be an object.");
            preferredGameSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in preferredSources.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String || preferredGameSources.Count >= 5_000)
                    throw new UserFacingException("Invalid preferred game sources.");
                preferredGameSources[entry.Name] = entry.Value.GetString()!;
            }
        }

        if (p.TryGetProperty("onboardingComplete", out var ob) &&
            (ob.ValueKind is JsonValueKind.True or JsonValueKind.False))
            onboard = ob.GetBoolean();
        if (p.TryGetProperty("sortMode", out var sm) && sm.ValueKind == JsonValueKind.String)
            sort = sm.GetString();
        if (p.TryGetProperty("defaultInstallRoot", out var dr))
            root = dr.ValueKind == JsonValueKind.String ? dr.GetString() : (dr.ValueKind == JsonValueKind.Null ? "" : null);
        if (!string.IsNullOrWhiteSpace(root))
            root = NormalizeInstallRoot(root) ?? throw new UserFacingException(InstallRootRejected);
        if (p.TryGetProperty("reduceMotion", out var rm) &&
            (rm.ValueKind is JsonValueKind.True or JsonValueKind.False))
            reduceMotion = rm.GetBoolean();
        if (p.TryGetProperty("startMaximized", out var startMaximizedEl) &&
            (startMaximizedEl.ValueKind is JsonValueKind.True or JsonValueKind.False))
            startMaximized = startMaximizedEl.GetBoolean();
        if (p.TryGetProperty("framelessPinTop", out var fpt) &&
            (fpt.ValueKind is JsonValueKind.True or JsonValueKind.False))
            framelessPinTop = fpt.GetBoolean();
        if (p.TryGetProperty("framelessPinSide", out var fps) &&
            (fps.ValueKind is JsonValueKind.True or JsonValueKind.False))
            framelessPinSide = fps.GetBoolean();
        if (p.TryGetProperty("showLibraryTools", out var slt) &&
            (slt.ValueKind is JsonValueKind.True or JsonValueKind.False))
            showLibraryTools = slt.GetBoolean();
        bool? hdrForGames = null;
        if (p.TryGetProperty("hdrForGames", out var hdr) &&
            (hdr.ValueKind is JsonValueKind.True or JsonValueKind.False))
            hdrForGames = hdr.GetBoolean();
        bool? frameGenEverywhere = null;
        if (p.TryGetProperty("frameGenEverywhere", out var fge) &&
            (fge.ValueKind is JsonValueKind.True or JsonValueKind.False))
            frameGenEverywhere = fge.GetBoolean();
        bool? graphicsFeaturesBestSettings = null;
        if (p.TryGetProperty("graphicsFeaturesBestSettings", out var gfbs) &&
            (gfbs.ValueKind is JsonValueKind.True or JsonValueKind.False))
            graphicsFeaturesBestSettings = gfbs.GetBoolean();
        bool? discordNoiseSuppression = null;
        if (p.TryGetProperty("discordNoiseSuppression", out var dns) &&
            (dns.ValueKind is JsonValueKind.True or JsonValueKind.False))
            discordNoiseSuppression = dns.GetBoolean();
        bool? discordQuietWhilePlaying = null;
        if (p.TryGetProperty("discordQuietWhilePlaying", out var dq) &&
            (dq.ValueKind is JsonValueKind.True or JsonValueKind.False))
            discordQuietWhilePlaying = dq.GetBoolean();
        bool? hdrSdrGamma = null;
        if (p.TryGetProperty("hdrSdrGamma", out var sdrGamma) &&
            (sdrGamma.ValueKind is JsonValueKind.True or JsonValueKind.False))
            hdrSdrGamma = sdrGamma.GetBoolean();

        if (p.TryGetProperty("textColour", out var tc) && tc.ValueKind == JsonValueKind.String)
            textColour = tc.GetString();
        if (p.TryGetProperty("textSize", out var tsz) && tsz.ValueKind == JsonValueKind.String)
            textSize = tsz.GetString();
        string? whatsNewSeen = p.TryGetProperty("whatsNewSeen", out var wn) && wn.ValueKind == JsonValueKind.String ? wn.GetString() : null;
        var previousShowLibraryTools = _services.Settings.Current.ShowLibraryTools;
        // Settings validate and commit first; a rejected patch must not leave
        // the Riot ID or developer secrets half-applied.
        _services.Settings.ApplyPatch(
            sortMode: sort,
            defaultInstallRoot: root,
            onboardingComplete: onboard,
            reduceMotion: reduceMotion,
            startMaximized: startMaximized,
            textColour: textColour,
            textSize: textSize,
            framelessPinTop: framelessPinTop,
            framelessPinSide: framelessPinSide,
            showLibraryTools: showLibraryTools,
            preferredGameSources: preferredGameSources,
            hdrForGames: hdrForGames,
            hdrSdrGamma: hdrSdrGamma,
            discordNoiseSuppression: discordNoiseSuppression,
            discordQuietWhilePlaying: discordQuietWhilePlaying,
            whatsNewSeen: whatsNewSeen,
            frameGenEverywhere: frameGenEverywhere,
            graphicsFeaturesBestSettings: graphicsFeaturesBestSettings);
        if (hdrForGames is not null || hdrSdrGamma is not null) _services.HdrHost.SyncSoon();
        if (frameGenEverywhere is not null) DlssSwapService.FrameGenEverywhereSoon();
        if (textSize is not null || reduceMotion is not null) _services.MousePanel.NotifySettingsChanged();
        if (graphicsFeaturesBestSettings == true)
            _ = Task.Run(() => GameGraphicsFeatures.ApplyBestSettingsSafe(_services.Library.PeekCachedLibrary(), GameSettings.ThisPc()));
        if (showLibraryTools is { } tools && tools != previousShowLibraryTools)
            RefreshLibraryAfterToolsToggle();
        ApplyDownloadSettings(p);
        ApplyStoreQuietSetting(p);
        ApplyStartWithWindowsSetting(p);
        ApplyAchievementSettings(p);
        ApplyFpsCounterSettings(p);
        if (p.TryGetProperty("libraryFolders", out var folders) && folders.ValueKind == JsonValueKind.Array)
        {
            static string? Text(JsonElement folder, string key) =>
                folder.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var kept = _services.Settings.SetLibraryFolders(folders.EnumerateArray().Where(folder => folder.ValueKind == JsonValueKind.Object).Select(folder => new LibraryFolder
            {
                Id = Text(folder, "id") ?? "",
                Name = Text(folder, "name") ?? "",
                Color = Text(folder, "color") ?? "gray",
                Sort = Text(folder, "sort") ?? "folder",
                Art = Text(folder, "art"),
                Games = folder.TryGetProperty("games", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(game => game.ValueKind == JsonValueKind.String).Select(game => game.GetString()!).ToList()
                    : [],
            }).ToList());
            // A deleted folder's picture, and a removed or replaced one, goes with it.
            _folderArt.DeleteUnreferenced(kept.Select(folder => folder.Art));
        }
        string? requestedMusicService = p.TryGetProperty("musicService", out var musicServiceProp) && musicServiceProp.ValueKind == JsonValueKind.String
            ? musicServiceProp.GetString()
            : null;
        if (p.TryGetProperty("uiPrefs", out var uiPrefs) && uiPrefs.ValueKind == JsonValueKind.Object)
        {
            var (fromUiPrefs, remaining) = WebClientPreferences.ExtractMusicServiceFromUiPrefs(uiPrefs);
            requestedMusicService ??= fromUiPrefs;
            if (remaining.Count > 0) _services.Settings.SetUiPrefs(remaining);
        }
        // A choice the page kept before Exo did, moved once: it never counts as a newer change than the account's.
        if (p.TryGetProperty("uiPrefsMoved", out var moved) && moved.ValueKind == JsonValueKind.Object)
        {
            foreach (var pref in moved.EnumerateObject())
            {
                if (pref.Name is WebClientPreferences.UiPrefMusicServiceKey or "musicService")
                {
                    if (pref.Value.ValueKind == JsonValueKind.String && WebClientPreferences.Read().MusicService is null &&
                        WebClientPreferences.TryNormalizeMusicService(pref.Value.GetString()) is { } movedService)
                        WebClientPreferences.ApplySyncedMusicService(movedService);
                    continue;
                }
                _services.Settings.MoveUiPref(pref.Name, pref.Value);
            }
        }
        if (requestedMusicService is not null)
        {
            var normalized = WebClientPreferences.TryNormalizeMusicService(requestedMusicService)
                ?? throw new UserFacingException("Choose a music service.");
            WebClientPreferences.SaveMusicService(normalized);
            _services.MusicEngine.SelectService(normalized);
        }
        if (p.TryGetProperty("hiddenGames", out var hidden) && hidden.ValueKind == JsonValueKind.Array)
            _services.Settings.SetHiddenGames(hidden.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()));

        if (p.TryGetProperty("riotId", out var riotId) && riotId.ValueKind == JsonValueKind.String)
        {
            if (!RiotIdStore.Save(riotId.GetString()))
                throw new UserFacingException("Riot ID was not saved.");
            if (!string.IsNullOrWhiteSpace(riotId.GetString()))
                _ = Task.Run(() => PlaytimeService.TryAutoSeedRiotHoursAsync(() => _services.Library.RefreshDerivedStateAsync(CancellationToken.None)));
        }
        return BuildSettings();
    }

    /// <summary>
    /// showLibraryTools is applied while a scan filters adapter rows, so the
    /// cached library still reflects the old value. Every adapter is re-read
    /// in the background; the scan raises LibraryUpdated, which pushes the
    /// existing <c>library.updated</c> event and replaces the UI's list.
    /// </summary>
    private void RefreshLibraryAfterToolsToggle()
    {
        _services.Library.Invalidate();
        _ = Task.Run(async () =>
        {
            try { await _services.Library.GetLibraryAsync(force: true).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Library refresh after showLibraryTools change failed: " + ex.Message); }
        });
    }


    public void NotifyWindowState(bool maximized) =>
        PostEvent("shell.window", new { maximized });


    /// The maximize button is a native region (snap layouts), so the page
    /// never sees the pointer over it; the host reports its hover instead.
    public void NotifyMaximizeHover(bool maximized, bool hover) =>
        PostEvent("shell.window", new { maximized, maximizeHover = hover });

    private object ToggleMaximize()
    {
        var maximized = false;
        try { maximized = App.MainAppWindow?.ToggleMaximize() ?? false; } catch { }
        return new { ok = true, maximized };
    }

    private object WindowState()
    {
        var maximized = false;
        try { maximized = App.MainAppWindow?.IsMaximized ?? false; } catch { }
        return new { ok = true, maximized };
    }

    /// The title band's controls in CSS px: every other point of the band drags.
    private object SetChrome(JsonElement p, bool hasParams)
    {
        Windows.Graphics.RectInt32[]? interactiveRegions = null;
        if (hasParams && p.TryGetProperty("interactiveRegions", out var regions) &&
            regions.ValueKind == JsonValueKind.Array && regions.GetArrayLength() <= 128)
        {
            var parsed = new List<Windows.Graphics.RectInt32>();
            foreach (var region in regions.EnumerateArray())
                if (ReadChromeRect(region) is { } rect) parsed.Add(rect);
            if (parsed.Count > 0) interactiveRegions = parsed.ToArray();
        }
        var maximizeButton = hasParams && p.TryGetProperty("maximizeButton", out var max) ? ReadChromeRect(max) : null;
        void Go()
        {
            try { App.MainAppWindow?.SetChromeLayout(interactiveRegions, maximizeButton); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.Enqueue(Go); else Go();
        return new { ok = true };
    }

    private static Windows.Graphics.RectInt32? ReadChromeRect(JsonElement region)
    {
        if (region.ValueKind != JsonValueKind.Object ||
            !region.TryGetProperty("x", out var rx) || rx.ValueKind != JsonValueKind.Number || !rx.TryGetInt32(out var x) || x is < 0 or > 32768 ||
            !region.TryGetProperty("y", out var ry) || ry.ValueKind != JsonValueKind.Number || !ry.TryGetInt32(out var y) || y < 0 ||
            !region.TryGetProperty("width", out var rw) || rw.ValueKind != JsonValueKind.Number || !rw.TryGetInt32(out var width) || width is <= 0 or > 32768 ||
            !region.TryGetProperty("height", out var rh) || rh.ValueKind != JsonValueKind.Number || !rh.TryGetInt32(out var height) || height <= 0)
            return null;
        // The title band is 52 CSS px; a slightly overflowing control still
        // needs its hole. Clip instead of dropping it.
        if (y > 96) return null;
        if (y + height > 96) height = 96 - y;
        return height <= 0 ? null : new Windows.Graphics.RectInt32(x, y, width, height);
    }

    private object MinimizeWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.MinimizeWindow(); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.Enqueue(Go); else Go();
        return new { ok = true };
    }

    private object CloseWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.CloseWindow(); } catch { }
            // WinUI's dispatcher can outlive its final unpackaged window. End
            // the application loop explicitly after Closed has flushed state;
            // otherwise the user sees no window but Exo keeps its files locked.
            try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
        }
        // This RPC starts synchronously inside WebMessageReceived even on the
        // UI thread. Let that native callback return before destroying its
        // WebView and exiting WinUI's message loop.
        var queued = _queue.Enqueue(Go);
        return new { ok = queued };
    }

    private static DateTimeOffset _lastSteamProtocolUtc = DateTimeOffset.MinValue;
    private static string? _lastSteamProtocolUri;

    private object OpenUrl(JsonElement p, bool hasParams)
    {
        var url = ReadString(p, hasParams, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            AppLog.Warn("shell.openUrl rejected: empty URL.");
            return new { ok = false, message = "Could not open that link." };
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ExoUrlLaunch.IsAllowed(uri))
        {
            var label = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                ? $"{parsed.Scheme}://{parsed.Host}{parsed.AbsolutePath}"
                : "invalid URI";
            AppLog.Warn($"shell.openUrl rejected disallowed URI: {label}");
            return new { ok = false, message = "Exo cannot open that link." };
        }
        // A link from a message or a note: the page asks first (guard) and opens again once the person confirmed (confirmed).
        if (ReadBool(p, hasParams, "guard") == true && ReadBool(p, hasParams, "confirmed") != true && LinkLookalike.Check(uri) is { } finding)
        {
            AppLog.Info("Links: warned before opening a look-alike of " + (finding.Brand ?? "an address"));
            return new { ok = false, lookalike = finding.Brand ?? "" };
        }
        try
        {
            if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase))
                return OpenSteamProtocol(uri.AbsoluteUri);
            if (uri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase))
            {
                var outcome = ExoUrlLaunch.OpenStoreUri(uri);
                return outcome.Ok
                    ? new { ok = true }
                    : new { ok = false, message = outcome.Message ?? "Windows couldn’t open Microsoft Store." };
            }

            Process.Start(new ProcessStartInfo(ExoUrlLaunch.LaunchUri(uri)) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return OpenFailed("that link", ex);
        }
    }

    /// <summary>
    /// One protocol handoff + reveal main Steam chrome only. Do not restore
    /// steamwebhelper windows — that spawned multiple Steam taskbar entries.
    /// </summary>
    private static object OpenSteamProtocol(string absoluteUri)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(absoluteUri, _lastSteamProtocolUri, StringComparison.OrdinalIgnoreCase) &&
            now - _lastSteamProtocolUtc < TimeSpan.FromSeconds(2))
            return new { ok = true, message = "Already opening Steam." };

        _lastSteamProtocolUri = absoluteUri;
        _lastSteamProtocolUtc = now;

        HiddenStoreRuntime.AllowUserOpen(StoreKind.Steam);

        // Process snapshots and window walks used to run here, on the RPC thread,
        // so Buy sat still until they finished. Queue all of it and answer now,
        // the same way ShowStoreAsync does.
        _ = Task.Run(async () =>
        {
            ProcessHelper.StartProtocol(absoluteUri);

            // Main steam.exe window only — helpers stay off the taskbar.
            var chrome = StoreWindowHider.SteamMainProcessNames;
            StoreWindowHider.RestoreStoreWindows(chrome);
            // Brief settle while Steam creates the store HWND; two nudges max.
            for (var i = 0; i < 2; i++)
            {
                await Task.Delay(600).ConfigureAwait(false);
                StoreWindowHider.RestoreStoreWindows(chrome);
            }
        });
        return new { ok = true };
    }


    /// <summary>
    /// Settings → Open a verified installed store client. This is deliberately
    /// an Open action only; it does not imply library or game control support.
    /// </summary>
    private async Task<object> ShowStoreAsync(JsonElement p, bool hasParams)
    {
        // Yield the WebView RPC turn so Settings can paint "Opening…" first.
        await Task.Yield();
        var store = (ReadString(p, hasParams, "store") ?? "steam").Trim().ToLowerInvariant();
        var result = await OpenStoreClientAsync(store).ConfigureAwait(true);
        return new { ok = result.Ok, message = result.Message };
    }

    /// <summary>Opens a verified installed store client and reveals its window.</summary>
    private async Task<StoreClientOpenResult> OpenStoreClientAsync(string store)
    {
        try
        {
            return store.Trim().ToLowerInvariant() switch
            {
                "steam" => await OpenVendorClient(
                    StoreKind.Steam,
                    StoreWindowHider.SteamMainProcessNames,
                    ExecutableCommand(SteamAdapter.TryResolveSteamExePublic()),
                    missing: "Steam is not installed.",
                    // Steam may be closed: what waited in Exo's record goes in before it starts to read its settings.
                    beforeStart: async () =>
                    {
                        await SteamPendingSettings.BeforeStartAsync(_bridgeLifetime.Token).ConfigureAwait(false);
                        return (string?)null;
                    }),
                "microsoft-store" => OpenMicrosoftStoreClient(),
                "epic" => await OpenVendorClient(
                    StoreKind.Epic,
                    StoreWindowHider.EpicProcessNames,
                    ExecutableCommand(ResolveEpicLauncherExe()),
                    missing: "Epic Games Launcher is not installed."),
                "gog" => await OpenVendorClient(
                    StoreKind.Gog,
                    StoreWindowHider.GalaxyProcessNames,
                    ExecutableCommand(ResolveGalaxyExe()),
                    missing: "GOG Galaxy is not installed."),
                "riot" => await OpenVendorClient(
                    StoreKind.Riot,
                    StoreWindowHider.RiotUiProcessNames,
                    ExecutableCommand(ResolveRiotClientExe()),
                    missing: "Riot Client is not installed.",
                    beforeStart: () => PrepareRiotVisibleLaunchAsync(_bridgeLifetime.Token)),
                "xbox" => await OpenOfficialClient("xbox", StoreKind.Xbox, "Xbox app is not installed."),
                "ea" => await OpenOfficialClient("ea", StoreKind.Ea, "EA app is not installed."),
                "ubisoft" => await OpenOfficialClient("ubisoft", StoreKind.Ubisoft, "Ubisoft Connect is not installed."),
                "battlenet" => await OpenOfficialClient("battlenet", StoreKind.BattleNet, "Battle.net is not installed."),
                "amazon" => await OpenOfficialClient("amazon", StoreKind.Amazon, "Amazon Games is not installed."),
                "rockstar" => await OpenOfficialClient("rockstar", StoreKind.Rockstar, "Rockstar Games Launcher is not installed."),
                "itch" => await OpenOfficialClient("itch", StoreKind.Itch, "The itch app is not installed."),
                "minecraft" => await OpenOfficialClient("minecraft", StoreKind.Minecraft, "Minecraft Launcher is not installed."),
                "roblox" => await OpenOfficialClient("roblox", StoreKind.Roblox, "Roblox is not installed."),
                "paradox" => await OpenOfficialClient("paradox", StoreKind.Paradox, "Paradox Launcher is not installed."),
                "wargaming" => await OpenOfficialClient("wargaming", StoreKind.Wargaming, "Wargaming Game Center is not installed."),
                _ => new StoreClientOpenResult(false, "Unknown store."),
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Open store {store}: {ex.GetType().Name}: {ex.Message}");
            return new StoreClientOpenResult(false, "Could not open the store app.");
        }
    }

    private static StoreClientOpenResult OpenMicrosoftStoreClient()
    {
        var outcome = ExoUrlLaunch.OpenStoreUri(new Uri(ExoUrlLaunch.MicrosoftStoreHomeUri));
        return new StoreClientOpenResult(
            outcome.Ok,
            outcome.Ok ? "Opened Microsoft Store." : outcome.Message ?? "Windows couldn’t open Microsoft Store.");
    }

    private static async Task<StoreClientOpenResult> OpenVendorClient(
        StoreKind kind,
        string[] processNames,
        StoreClientLaunchCommand? command,
        string missing,
        Func<Task<string?>>? beforeStart = null)
    {
        if (command is null ||
            (!command.IsAppx && (string.IsNullOrWhiteSpace(command.FileName) || !File.Exists(command.FileName))))
            return new StoreClientOpenResult(false, missing);

        var label = kind switch
        {
            StoreKind.Epic => "Epic Games Launcher", StoreKind.Gog => "GOG Galaxy", StoreKind.Riot => "Riot Client",
            StoreKind.Ea => "EA app", StoreKind.Ubisoft => "Ubisoft Connect", StoreKind.BattleNet => "Battle.net",
            StoreKind.Amazon => "Amazon Games", StoreKind.Rockstar => "Rockstar Games Launcher",
            StoreKind.Wargaming => "Wargaming Game Center", StoreKind.Paradox => "Paradox Launcher",
            StoreKind.Minecraft => "Minecraft Launcher", StoreKind.Itch => "itch", _ => kind.ToString(),
        };
        if (beforeStart is not null && await beforeStart().ConfigureAwait(false) is { } failure)
            return new StoreClientOpenResult(false, failure);
        return await Task.Run(() => StoreClientOpener.OpenAsync(kind, label, command, processNames)).ConfigureAwait(false);
    }

    private Task<StoreClientOpenResult> OpenOfficialClient(string adapterId, StoreKind kind, string missing)
    {
        var adapter = _services.FindAdapterById(adapterId) as IOfficialStoreClient;
        return adapter is null
            ? Task.FromResult(new StoreClientOpenResult(false, "Could not open the store app."))
            : OpenVendorClient(kind, OfficialClientUiProcessNames(kind), adapter.GetClientLaunchCommand(), missing);
    }

    /// <summary>
    /// Settings → Open is allowed to reveal only named launcher chrome. Keep
    /// this independent from an adapter's wider process observation list:
    /// helpers and services can be necessary for a game and must never be
    /// foregrounded merely because the user opened the launcher.
    /// </summary>
    private static string[] OfficialClientUiProcessNames(StoreKind kind) => kind switch
    {
        StoreKind.Xbox => StoreWindowHider.XboxClientProcessNames,
        StoreKind.Ea => StoreWindowHider.EaClientProcessNames,
        StoreKind.Ubisoft => StoreWindowHider.UbisoftClientProcessNames,
        StoreKind.BattleNet => StoreWindowHider.BattleNetClientProcessNames,
        StoreKind.Amazon => StoreWindowHider.AmazonClientProcessNames,
        StoreKind.Rockstar => StoreWindowHider.RockstarClientProcessNames,
        StoreKind.Itch => StoreWindowHider.ItchClientProcessNames,
        StoreKind.Minecraft => StoreWindowHider.MinecraftClientProcessNames,
        StoreKind.Roblox => StoreWindowHider.RobloxClientProcessNames,
        StoreKind.Paradox => StoreWindowHider.ParadoxClientProcessNames,
        StoreKind.Wargaming => StoreWindowHider.WargamingClientProcessNames,
        _ => [],
    };

    private static StoreClientLaunchCommand? ExecutableCommand(string? executable) =>
        string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            ? null
            : new StoreClientLaunchCommand(executable);

    private static string? ResolveEpicLauncherExe() =>
        EpicAdapter.ResolveEpicLauncher();

    private static string? ResolveGalaxyExe() =>
        GogAdapter.ResolveGalaxy();

    private static string? ResolveRiotClientExe() =>
        RiotAdapter.TryResolveRiotClientServicesPublic();

    // FolderPicker has no title, so the UI's "title" param is not read.
    /// <summary>A game's program (.exe), for the shader add-on's Add a game.</summary>
    private async Task<object> PickProgramAsync()
    {
        var tcs = new TaskCompletionSource<object>();
        async Task Run()
        {
            try
            {
                if (App.MainAppWindow is not { } window) { tcs.TrySetResult(new { ok = false, cancelled = true, message = "The file picker could not open." }); return; }
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (file is not null) Helpers.PickedPaths.Remember(file.Path);
                tcs.TrySetResult(file is null ? new { ok = false, cancelled = true, message = "Canceled." } : new { ok = true, cancelled = false, path = file.Path });
            }
            catch (Exception ex) { AppLog.Warn("File picker: " + ex.Message); tcs.TrySetResult(new { ok = false, cancelled = false, message = "The file picker could not open." }); }
        }
        if (!_queue.HasThreadAccess) _queue.Enqueue(() => _ = Run());
        else _ = Run();
        return await tcs.Task.ConfigureAwait(true);
    }

    private async Task<object> PickFolderAsync()
    {
        var tcs = new TaskCompletionSource<object>();
        void Run() => _ = RunPickFolderAsync(tcs);

        if (!_queue.HasThreadAccess)
            _queue.Enqueue(Run);
        else
            Run();

        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickFolderAsync(TaskCompletionSource<object> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new { ok = false, cancelled = true, message = "The folder picker could not open." });
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                tcs.TrySetResult(new { ok = false, cancelled = true, message = "Canceled." });
                return;
            }

            Helpers.PickedPaths.Remember(folder.Path);
            tcs.TrySetResult(new { ok = true, cancelled = false, path = folder.Path });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Folder picker: " + ex.Message);
            tcs.TrySetResult(new { ok = false, cancelled = false, message = "The folder picker could not open." });
        }
    }

    private async Task<GameEntry?> FindGameAsync(string? gameId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        var game = _services.Library.Find(gameId);
        if (game is not null) return game;
        await _services.Library.GetLibraryAsync().WaitAsync(ct).ConfigureAwait(false);
        return _services.Library.Find(gameId);
    }

    private async Task<object> DlssStatusAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Pick a game first.", items = Array.Empty<object>() };

        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found.", items = Array.Empty<object>() };

        var refresh = ReadBool(p, hasParams, "refresh") == true;
        var consented = _services.Settings.UpscalerConsent(game.Id);
        // The folder check too: a sleeping or network drive would hold the UI thread while it answers.
        return await Task.Run(() => DlssSwapService.ResolveInstallRoot(game.Path) is null
            ? new { ok = false, message = "The install folder is unavailable. Refresh the library or reconnect the drive.", items = Array.Empty<object>() }
            : MapDlssStatus(_dlss.GetStatus(game, refresh, consented, ct)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The game sheet's confirmation. <c>allow</c> true with the
    /// <c>antiCheat</c> the page showed: saves the user's acceptance of the ban
    /// risk for this game, only if a fresh walk finds that same anti-cheat and
    /// it is not a Riot game or Riot Vanguard. <c>allow</c> false: withdraws it.
    /// </summary>
    private async Task<object> DlssConsentAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        if (ReadBool(p, hasParams, "allow") != true)
        {
            _services.Settings.SetUpscalerConsent(game.Id, null);
            AppLog.Info($"Upscaler consent withdrawn: gameId={game.Id}.");
            return new { ok = true, message = "Exo will not change this game's files." };
        }

        var shown = ReadString(p, hasParams, "antiCheat");
        var antiCheat = await Task.Run(() => _dlss.AntiCheatNow(game, ct), ct).ConfigureAwait(false);
        if (antiCheat is null)
            return new { ok = false, message = "Exo found no anti-cheat in this game, so there is nothing to allow." };
        if (DlssSwapService.NeverAllowed(game, antiCheat))
        {
            AppLog.Info($"Upscaler consent refused: gameId={game.Id}; antiCheat={antiCheat}.");
            return new { ok = false, message = DlssSwapService.AntiCheatMessage(game, antiCheat, null) };
        }
        if (!string.Equals(shown, antiCheat, StringComparison.OrdinalIgnoreCase))
            return new { ok = false, message = $"This game now shows {antiCheat}. Check the warning again before allowing changes." };

        _services.Settings.SetUpscalerConsent(game.Id, antiCheat);
        AppLog.Info($"Upscaler consent: gameId={game.Id}; anti-cheat {antiCheat}; user accepted the risk.");
        return new { ok = true, message = "Allowed for this game. Any ban is your risk." };
    }

    /// <summary>
    /// No <c>file</c>: every file to the newest release. With <c>file</c> (the
    /// row key from dlss.status) and <c>release</c>: that file to exactly that
    /// release, or <c>latest</c>. Anti-cheat games are refused by the service
    /// unless this game's saved consent names the anti-cheat found under the
    /// write lock.
    /// </summary>
    /// <summary>What the last check listed as newer than what is installed, for the notice that offers the download.</summary>
    private static object DlssNewer() => new
    {
        ok = true,
        items = DlssSwapService.AvailableUpscalers().Select(item => new { family = item.Family, version = item.Version }).ToArray(),
    };

    /// <summary>The notice's button: downloads and verifies what was listed. <c>message</c> is the failure, null when it ran.</summary>
    private async Task<object> DlssDownloadNewerAsync(CancellationToken ct)
    {
        var message = await Task.Run(() => DlssSwapService.DownloadAvailableUpscalersAsync(ct), ct).ConfigureAwait(false);
        return new { ok = message is null, message };
    }

    private async Task<object> DlssUpdateAllAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null)
            return new { ok = false, message = "Game not found." };
        // A store writing the game's files would leave a half-written original behind.
        if (_services.Launcher.IsGameBusy(game))
            return new { ok = false, message = "Wait for the game's update to finish." };

        try
        {
            var file = ReadString(p, hasParams, "file");
            var release = ReadString(p, hasParams, "release");
            var result = await Task.Run(() => _dlss.ApplyAsync(game, file, release, () => _services.Settings.UpscalerConsent(game.Id), ct), ct).ConfigureAwait(false);
            return MapDlssRun(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("dlss.updateAll failed: " + ex.Message);
            return new { ok = false, updated = 0, skipped = 0, failed = 0, message = "Could not update." };
        }
    }

    private async Task<object> DlssRestoreAsync(JsonElement p, bool hasParams)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), _bridgeLifetime.Token).ConfigureAwait(true);
        if (game is null)
            return new { ok = false, message = "Game not found." };
        if (_services.Launcher.IsGameBusy(game))
            return new { ok = false, message = "Wait for the game's update to finish." };

        // Once a restore has started, finish its owned file transaction even if
        // the requesting page goes away. The next status read observes the result.
        var file = ReadString(p, hasParams, "file");
        var result = await Task.Run(() => _dlss.RestoreGame(game, file), _bridgeLifetime.Token).ConfigureAwait(false);
        return MapDlssRun(result);
    }

    /// <summary>One press, reported per file with its version before and after.</summary>
    private static object MapDlssRun(DlssSwapService.UpdateResult result) => new
    {
        ok = result.Ok,
        updated = result.Updated,
        skipped = result.Skipped,
        failed = result.Failed,
        message = result.Message,
        files = (result.Files ?? []).Select(file => new
        {
            fileName = file.FileName,
            folder = file.Folder,
            state = file.State,
            before = file.Before,
            after = file.After,
            message = file.Message,
        }).ToList(),
    };

    /// <summary>
    /// Bounded DTO: a file's place in the game is its path relative to the
    /// install folder, never an absolute path.
    /// </summary>
    private static object MapDlssStatus(DlssSwapService.StatusResult status) => new
    {
        ok = status.Ok,
        message = status.Message,
        antiCheat = status.AntiCheat,
        consent = status.Consent,
        items = status.Rows.Select(row => new
        {
            key = row.Dll.RelativePath,
            fileName = row.Dll.FileName,
            folder = DlssSwapService.FolderOf(row.Dll.RelativePath),
            displayName = DlssSwapService.DisplayName(row.Dll.FileName),
            api = DlssSwapService.ApiName(row.Dll.FileName),
            currentVersion = DlssSwapService.DisplayVersion(row.Dll),
            fileVersion = row.Dll.CurrentVersion,
            currentRelease = row.Dll.MatchedRelease,
            latestVersion = row.Latest?.Display,
            latestRelease = row.Latest?.Release,
            state = row.State,
            origin = row.Dll.Origin,
            originalVersion = DlssSwapService.TrimVersion(row.Dll.OriginalVersion),
            originalState = row.Dll.OriginalState,
            canUpdate = row.CanUpdate,
            canRestore = row.CanRestore,
            skipReason = row.Dll.SkipReason,
            mod = row.Dll.Mod,
        }).ToList(),
    };

    /// <summary>A launch target the page may see: a store id or a store URI, never a file path (AGENTS.md).</summary>
    internal static string? PageTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile && !uri.IsUnc && uri.Scheme.Length > 1)
            return target;
        return target.IndexOfAny(['\\', '/', ':']) >= 0 ? null : target;
    }

    private object MapGame(GameEntry g, bool discoverExternalRunningGame = false) =>
        GameForPage(g, _services.Launcher.GetGameRunState(g, discoverExternalRunningGame),
            variant => _services.Launcher.GetGameRunState(variant.ToGameEntry(g), discoverExternalRunningGame));

    /// <summary>
    /// The line a failed call may show the player. A <see cref="UserFacingException"/>, or an InvalidOperation or InvalidData
    /// that Exo's own code threw (<see cref="ThrownByExo"/>), keeps its sentence unless it holds a path, the Windows user name or
    /// a token. A full disk and a network failure get their own plain sentence. Every other exception (JSON, COM, file,
    /// Windows, anything a framework or library threw) carries paths, hosts or internals, so the page gets <paramref name="generic"/>.
    /// </summary>
    internal static string UserMessage(Exception ex, string generic) => ex switch
    {
        _ when ExoSentence(ex) is { } line => line,
        IOException io when (io.HResult & 0xFFFF) is 0x27 or 0x70 => DriveFull,
        // A sharing or lock violation: a game, its store or an app still has the file open.
        IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 => FileInUse,
        UnauthorizedAccessException => AccessRefused,
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } => AccessRefused,
        // ERROR_CANCELLED: Windows' prompt (UAC) was answered No.
        System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => PromptDeclined,
        HttpRequestException or System.Net.Sockets.SocketException => NoInternet,
        TimeoutException => TookTooLong,
        _ => generic,
    };

    // The page's errorWords.ts says the same sentences for the same failures reaching it from elsewhere.
    internal const string DriveFull = "The drive is full. Free up some space, then try again.";
    internal const string FileInUse = "A file is in use. Close the game or app using it, then try again.";
    internal const string AccessRefused = "Windows refused access. Try again, and allow Windows’ prompt if it asks.";
    internal const string PromptDeclined = "Windows’ prompt was declined. Try again and choose Yes.";
    internal const string NoInternet = "Exo couldn’t reach the internet. Check your connection, then try again.";
    internal const string TookTooLong = "That is taking too long. Try again.";

    private static string? ExoSentence(Exception ex) =>
        (ex is UserFacingException || ex is InvalidOperationException or InvalidDataException && ThrownByExo(ex)) && Safe(ex.Message) is { Length: > 0 } line ? line : null;

    /// <summary>The method that threw is in one of Exo's own assemblies (a lambda or async method counts through its compiler class).</summary>
    internal static bool ThrownByExo(Exception ex) =>
        ex.TargetSite?.DeclaringType?.Assembly.GetName().Name is { } name && name.StartsWith("ExoLauncher", StringComparison.Ordinal);

    private static readonly System.Text.RegularExpressions.Regex TokenLike = new(@"[A-Za-z0-9_\-]{32,}|Bearer\s", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A sentence for the page: no path, no Windows user name, nothing that looks like a token.</summary>
    internal static string Safe(string? text)
    {
        var line = PageText(text);
        if (line.Length == 0 || TokenLike.IsMatch(line)) return "";
        var user = Environment.UserName;
        return user.Length >= 3 && line.Contains(user, StringComparison.OrdinalIgnoreCase) ? "" : line;
    }

    // A drive path (not the "s:/" of "https://"), a share (either slash), or a user-profile path written with forward slashes.
    private static readonly System.Text.RegularExpressions.Regex PathInText = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/]|(?<![:/\w])(?:\\\\|//)[^\s\\/]+[\\/]|(?<![\w])(?:~|/Users|/home)/[^\s/]+/",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Words from an adapter for the page: a text with a file path in it is dropped whole (a path can hold spaces, so cutting it out would leave its tail).</summary>
    internal static string PageText(string? text) =>
        string.IsNullOrEmpty(text) || PathInText.IsMatch(text) ? "" : text;

    /// <summary>What the page may know about one game: ids, flags and words, never a file path (AGENTS.md).</summary>
    internal static object GameForPage(GameEntry g, GameRunState runState, Func<GameVariant, GameRunState> variantRunState)
    {
        return new
        {
        id = g.Id,
        title = g.Title,
        store = g.Store.ToString().ToLowerInvariant(),
        // `store` remains the deterministic selected source. `stores` and
        // `variants` let a card show alternate active sources without exposing
        // account identity or replacing any exact action ids.
        stores = (g.Variants.Count == 0 ? new[] { g.Store } : g.Variants.Select(v => v.Store))
            .Distinct()
            .Select(store => store.ToString().ToLowerInvariant())
            .ToArray(),
        canonicalTitleKey = g.CanonicalTitleKey,
        selectedVariantId = g.SelectedVariantId ?? g.Id,
        variants = g.Variants.Select(variant =>
        {
            // game.get is the deliberate, selected-card reconciliation point.
            // Scan every exact source on that card there, otherwise a running
            // alternate-store copy stays invisible until the user happens to
            // switch sources and triggers another round trip. library.get keeps
            // this false so the grid never enumerates every installed process.
            var variantRun = variantRunState(variant);
            return new
            {
                id = variant.Id,
                store = variant.Store.ToString().ToLowerInvariant(),
                installed = variant.Installed,
                owned = variant.Owned,
                entitlementState = variant.EntitlementState,
                updateAvailable = variant.UpdateAvailable,
                canInstall = variant.CanInstall,
                primaryAction = variant.PrimaryAction,
                // Paths stay in native code (AGENTS.md): a store id goes to the page, a program's path does not.
                path = (string?)null,
                launchTarget = PageTarget(variant.LaunchTarget),
                playtimeMinutes = variant.PlaytimeMinutes ?? 0,
                lastPlayedUtc = variant.LastPlayedUtc?.ToString("O"),
                status = PageText(variant.Status),
                isRunning = variantRun.IsRunning,
                canStop = variantRun.CanStop,
            };
        }).ToArray(),
        installed = g.Installed,
        owned = g.Owned,
        entitlementState = g.EntitlementState,
        updateAvailable = g.UpdateAvailable,
        canInstall = g.CanInstall,
        primaryAction = g.PrimaryAction,
        path = (string?)null,
        coverUrl = g.CoverUrl,
        coverSource = g.CoverSource,
        artRevision = g.ArtRevision,
        playtimeMinutes = g.PlaytimeMinutes ?? 0,
        sizeBytes = g.SizeBytes ?? InstalledSizeCache.Peek(g.Path)?.Bytes,
        sizeApproximate = g.SizeBytes is null && InstalledSizeCache.Peek(g.Path) is { Complete: false, Bytes: not null },
        status = PageText(g.Status),
        deps = g.Deps.Select(PageText).ToArray(),
        launchNote = PageText(g.LaunchNote),
        familyShared = g.FamilyShared,
        // A family member's Steam name, for the game page's "Shared by"; never an id.
        sharedBy = g.FamilyShared && PageText(g.SharedBy) is { Length: > 0 } lender ? lender : null,
        launchTarget = PageTarget(g.LaunchTarget),
        includes = g.Includes?.Select(game => new { appId = game.AppId, title = game.Title }),
        lastPlayedUtc = g.LastPlayedUtc?.ToString("O"),
        isAddPortable = string.Equals(g.Id, "local:add", StringComparison.OrdinalIgnoreCase),
        isRunning = runState.IsRunning,
        canStop = runState.CanStop,
        buyUrl = Storefront.BuyUrl(g),
        // The Library's search and Filter menu (racing, co-op ...): Steam's own record, already in memory.
        tags = SteamAppTags.For(g),
        };
    }

    private static object MapProgress(InstallProgress p) => new
    {
        gameId = p.GameId,
        phase = p.Phase.ToString().ToLowerInvariant(),
        percent = p.Percent,
        bytesDownloaded = p.BytesDownloaded,
        bytesToDownload = p.BytesToDownload,
        bytesPerSecond = p.BytesPerSecond,
        status = p.Status,
        canCancel = p.CanCancel,
        isActive = p.IsActive,
    };

    /// <summary>The params that only name a page or a kind of call, never what it is about.</summary>
    private static readonly string[] PerfKeyParams = ["view", "action", "kind", "mode", "part", "section", "tab", "type", "service", "scope", "step"];

    /// <summary>
    /// What makes a slow call traceable in the PERF line: the params that name a page or kind of call ("view:playlist,action:play"),
    /// and only a plain lowercase word for each (no ids, uris, paths, names or text: those begin with a digit or hold a colon, slash or space).
    /// </summary>
    internal static string? PerfKey(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        List<string>? words = null;
        foreach (var name in PerfKeyParams)
        {
            if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 24 } word) continue;
            if (word.Length == 0 || !char.IsAsciiLetterLower(word[0]) || !word.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) continue;
            (words ??= []).Add(name + ":" + word);
        }
        return words is null ? null : string.Join(',', words);
    }

    private static string? ReadString(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    /// <summary>The page's heartbeat (ui/src/lib/heartbeat.ts): recorded for the hang watchdog; nothing else.</summary>
    private static object HeartbeatReply(JsonElement p, bool hasParams)
    {
        HangWatchdog.PageBeat(ReadBool(p, hasParams, "visible") != false);
        return new { ok = true };
    }

    private static bool? ReadBool(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        return null;
    }

    private static double? ReadDouble(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetDouble(out var parsed) ? parsed : null;
    }

    private static object MapDepAwareResult(LaunchResult result) => new
    {
        ok = result.Ok,
        message = result.Message,
        processId = result.ProcessId,
        backendStarted = result.BackendStarted,
        handoffOnly = result.HandoffOnly,
        needsDependencies = result.NeedsDependencies,
        missingDependencies = MapMissingDeps(result.MissingDependencies),
        offline = result.OfflineStore,
    };

    private static object MapMissingDeps(IReadOnlyList<DependencyInfo> deps) =>
        deps.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToArray();

    internal static object BuildArtBaseResponse(
        bool isNamedPipe,
        string covers,
        string coversDir,
        string devicesDir,
        string notesDir,
        string capturesDir,
        string captureThumbsDir) =>
        isNamedPipe
            ? new
            {
                covers,
                coversDir,
                devicesDir,
                notesDir,
                capturesDir,
                captureThumbsDir,
            }
            : (object)new { covers };

    private object BuildArtBase() =>
        BuildArtBaseResponse(
            _transport is NamedPipeHostTransport,
            LocalFileHost.Url(CoverArtService.VirtualHost, ""),
            CoverArtService.CacheRoot,
            DeviceImages.CacheRoot,
            NoteStore.Shared.PhotosFolder,
            _services.HeadlessCapture.CaptureFolder,
            HeadlessCaptureService.ThumbsDir);

    internal static object BuildShellBootstrapResponse(
        object artBase,
        object settings,
        object library,
        object musicPlayback,
        object discordStatus,
        string? binaryMediaPipe) =>
        new
        {
            ok = true,
            artBase,
            settings,
            library,
            musicPlayback,
            discordStatus,
            binaryMediaPipe,
        };

    private async Task<object> ShellBootstrapAsync(CancellationToken ct)
    {
        var libraryTask = Task.Run(() => LibraryGetAsync(default, false), ct);
        var settingsTask = Task.Run(BuildSettings, ct);
        var musicTask = Task.Run(
            () => _services.MusicEngine.IsActive || App.MainAppWindow is not null
                ? _services.MusicEngine.PlaybackAsync()
                : _services.HeadlessMusic.PlaybackAsync(),
            ct);
        var discordTask = Task.Run(() => DiscordStatus(default, false), ct);
        UpdateStartupHealth.UiReady(_services.AppVersion);
        var artBase = BuildArtBase();
        await Task.WhenAll(libraryTask, settingsTask, musicTask, discordTask).WaitAsync(ct).ConfigureAwait(false);
        return BuildShellBootstrapResponse(
            artBase,
            await settingsTask.ConfigureAwait(false),
            await libraryTask.ConfigureAwait(false),
            await musicTask.ConfigureAwait(false),
            await discordTask.ConfigureAwait(false),
            (_transport as NamedPipeHostTransport)?.MediaPipeName);
    }

    private void PostResponse(string id, bool ok, object? result = null, string? error = null, double elapsedMs = 0)
    {
        var transport = _transport;
        if (transport is null || _detached) return;
        if (_trace.IsEnabled)
            _trace.RecordReply(RpcTrace.ClientKind(transport), id, ok, elapsedMs);
        if (transport is NamedPipeHostTransport pipeTransport)
        {
            try { pipeTransport.PostJsonUtf8(ExoBridgeProtocol.SerializeResponseUtf8(id, ok, result, error)); }
            catch { }
            return;
        }
        PostSerialized(ExoBridgeProtocol.SerializeResponse(id, ok, result, error));
    }

    /// <summary>An event from the shader add-on's engine, for its page.</summary>
    internal void PostShadersEvent(string name, System.Text.Json.Nodes.JsonNode? data) =>
        PostEvent("shaders.event", new { name, data });

    /// <summary>capture.state and capture.saved: from Exo Capture, through MainWindow.Capture.</summary>
    internal void PostCaptureEvent(string name, object? data) => PostEvent(name, data);

    /// <summary>components.failed: one of Exo's parts (<see cref="ExoComponents"/>) failed to fetch twice in a row.</summary>
    private void OnComponentFailed(string feature) => PostEvent("components.failed", new { feature });

    private void PostEvent(string name, object? data)
    {
        var transport = _transport;
        if (transport is null || _detached) return;
        if (_trace.IsEnabled)
            _trace.RecordEvent(RpcTrace.ClientKind(transport), name, data);
        if (transport is NamedPipeHostTransport pipeTransport)
        {
            try { pipeTransport.PostJsonUtf8(ExoBridgeProtocol.SerializeEventUtf8(name, data)); }
            catch { }
            return;
        }
        PostSerialized(ExoBridgeProtocol.SerializeEvent(name, data));
    }

    /// <summary>
    /// Cover art on its way to the page: every cover cache and thumbnail address with this run's token, which
    /// LocalFileHost requires (MainWindow). Exo keeps and compares them token-free (the library's saved state, settings,
    /// TryResolveLocalFile), so the token is added here, once, for every response and event.
    /// </summary>
    internal static string ToPage(string json) =>
        LocalFileHost.WithToken(LocalFileHost.WithToken(json, CoverArtService.VirtualHost), CoverArtService.ThumbnailHost);

    /// <summary>A request from the page with the run's token taken back out of its art addresses (a folder's picture saved, say).</summary>
    internal static string FromPage(string raw) =>
        LocalFileHost.WithoutToken(LocalFileHost.WithoutToken(raw, CoverArtService.VirtualHost), CoverArtService.ThumbnailHost);

    internal static string OutboundForTransport(IHostTransport transport, string json) =>
        transport is NamedPipeHostTransport ? json : ToPage(json);

    internal static string InboundForTransport(IHostTransport? transport, string raw) =>
        transport is NamedPipeHostTransport ? raw : FromPage(raw);

    private void PostSerialized(string json)
    {
        try
        {
            var transport = _transport;
            if (transport is null || _detached) return;
            transport.PostJson(OutboundForTransport(transport, json));
        }
        catch { }
    }

    internal sealed class EarlyCancelTracker
    {
        private const int MaxEntries = 256;
        private readonly long _ttlMs;
        private readonly Func<long> _clockMs;
        private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.Ordinal);

        internal EarlyCancelTracker(long ttlMs = 5_000, Func<long>? clockMs = null)
        {
            _ttlMs = ttlMs;
            _clockMs = clockMs ?? (() => Environment.TickCount64);
        }

        internal void Record(string id)
        {
            var now = _clockMs();
            if (_pending.Count >= MaxEntries) Prune(now);
            if (_pending.Count >= MaxEntries) _pending.Clear();
            _pending[id] = now;
        }

        internal bool Consume(string id)
        {
            if (!_pending.TryRemove(id, out var at)) return false;
            return _clockMs() - at <= _ttlMs;
        }

        internal void Clear() => _pending.Clear();

        private void Prune(long now)
        {
            foreach (var pair in _pending)
            {
                if (now - pair.Value > _ttlMs) _pending.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>A Windows notification from Exo's own UI, when it is in the background: a short title and line.</summary>
    private static object AppNotify(JsonElement p)
    {
        static string? Text(JsonElement p, string name, int max) =>
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? (s.Length > max ? s[..max] : s) : null;
        var title = Text(p, "title", 80);
        var body = Text(p, "body", 200);
        if (title is null || body is null) return new { ok = false };
        DiscordClient.DiscordNotifier.ShowApp(title, body);
        return new { ok = true };
    }
}
