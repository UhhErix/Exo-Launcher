using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Ui;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace ExoLauncher;

/// <summary>
/// Thin native shell: default 1400×900 AMOLED window + WebView2 product UI.
/// The window is resizable and maximizable, with a 960×640 floor.
/// Launch/discovery stay in C# via <see cref="WebHostBridge"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private Microsoft.UI.Windowing.AppWindowPresenter? _beforeFullScreen;
    private const int DefaultWindowWidth = 1400;
    private const int DefaultWindowHeight = 900;
    private const int MinWindowWidth = 960;
    private const int MinWindowHeight = 640;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private WebHostBridge? _bridge;
    private LocalFileHost? _files;
    private NotificationAreaIcon? _notificationAreaIcon;
    private bool _movingToNotificationArea;
    private bool _webReady;
    private bool _windowActive = true;
    private bool _closingWindow;
    private Task? _ensureWebTask;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _webViewWindowGuard;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;
        ApplyAppearance();
        try { StartWebViewWindowGuard(); } catch { }

        try
        {
            ApplyWindowChrome();
            ApplyInitialWindowBounds();
            RestoreWindowPlacement();
            TrySetWindowIcon();
        }
        catch { }

        ExtendsContentIntoTitleBar = true;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }
        catch { }

        try
        {
            var tb = AppWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar = true;
            tb.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var black = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var ink = Windows.UI.Color.FromArgb(255, 233, 233, 236);
            var hover = Windows.UI.Color.FromArgb(255, 26, 26, 31);
            tb.BackgroundColor = black;
            tb.InactiveBackgroundColor = black;
            // Focus inside a hosted client deactivates Exo's own window while
            // the user is still in Exo; the caption must not dim for that.
            tb.ForegroundColor = ink;
            tb.InactiveForegroundColor = ink;
            tb.ButtonBackgroundColor = transparent;
            tb.ButtonInactiveBackgroundColor = transparent;
            tb.ButtonForegroundColor = ink;
            tb.ButtonInactiveForegroundColor = ink;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonHoverForegroundColor = ink;
            tb.ButtonPressedBackgroundColor = hover;
            tb.ButtonPressedForegroundColor = ink;
        }
        catch { }

        try { AttachCaptionInput(); } catch { }
        try { UpdateCaptionDragRegions(); } catch { }

        try { AppWindow.Changed += OnAppWindowChanged; } catch { }

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyWindowChrome();
            // The page first: WebView2 starts its browser while the launcher (every adapter, the library, the
            // orchestrator) is built on the pool, instead of after it on this thread. The game hooks join once it's built.
            var web = EnsureWebAsync();
                var launcher = Task.Run(() => App.Services.Launcher);
            _ = Task.Run(() => NvidiaColor.ApplySaved(App.Services.Settings));
            _ = TrimLoopAsync();
            Services.DiscordClient.Voice.LiveAudio.Changed += OnLiveAudioChanged;
            Services.LeanSignal.Changed += OnLeanChanged;
            await launcher;
            if (!_closingWindow)
            {
                StartHdrForGames();
                StartGpuPreference();
                StartSafeModeGuard();
                GameSettings.StartKeeping();
                StartGameAwake();
                DriveHeat.Start();
                _ = Task.Run(Services.ExoLinks.Register);
                _ = Task.Run(Services.GpuPreference.WindowedGamesOptimized);
                Services.Achievements.AchievementWatch.Start();
                AchievementBanner.Start();
                Services.FpsCounter.Start();
                Services.Achievements.AchievementSource.Start();
                StartDlssCheck();
            }
            await web;
        };
        LogStartupMilestone("window-constructed");
        WireDiscordClient();
        Activated += (_, e) =>
        {
            if (_closingWindow) return;
            // Activated flaps when focus moves into a WebView2 child; trust the
            // real foreground window instead. A hosted client is an owned
            // popup of this window, so typing in it counts as Exo.
            SetWindowActive(ForegroundWatcher.ExoInFront(WindowNative.GetWindowHandle(this)));
            if (_windowActive)
            {
                _ = EnsureWebAsync();
                // Maximize can stop answering after an activation (Cinema, a611ac5): the regions are set again, cleared first.
                UpdateCaptionDragRegions();
            }
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            ApplyHostPriority();
        };
        AppWindow.Closing += (_, _) => PrepareWindowClose();
        Closed += (_, _) =>
        {
            PrepareWindowClose();
            App.MainAppWindow = null;
            ForegroundWatcher.WindowActivity -= OnWindowActivityForWebViewGuard;
            _webViewWindowGuard?.Stop();
        };
        // Before the window first shows, so the front it takes then (or takes when it shouldn't) is seen.
        _foregroundWatcher = ForegroundWatcher.Start(WindowNative.GetWindowHandle(this), inFront =>
        {
            if (_closingWindow) return;
            SetWindowActive(inFront);
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            ApplyHostPriority();
        });
        // Without the show hook nothing reports a WebView2 window starting: the one-second timer stands in.
        if (!ForegroundWatcher.ActivityHooked) StartWebViewWindowGuardTimer();
        // After the first paint's work: Explorer answers this call, and the window never waits on it.
        DispatcherQueue.Enqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closingWindow) return;
            EnsureNotificationAreaIcon();
            _notificationAreaIcon!.Show();
        });
    }

    internal void CloseWindow()
    {
        PrepareWindowClose();
        Close();
    }

    private int _runningGames;
    private ForegroundWatcher? _foregroundWatcher;
    // Fast while there is something to release (Exo backgrounded, a hidden
    // embedded renderer, a visibility change just now); slow otherwise.
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrimIdleInterval = TimeSpan.FromSeconds(30);
    // While a game runs and Exo has stepped back already: each tick only re-checks what it released (a UI-thread wake,
    // a window walk for the store clients), so it comes a third as often.
    private static readonly TimeSpan TrimGameInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrimSettleTime = TimeSpan.FromSeconds(15);
    // How long Exo is out of front before it steps back (host efficiency mode, the tree's memory trimmed and lowered):
    // a quick switch to another window and back must cost nothing (Discord's calls ran 4 to 10 s slow after each one,
    // paged out and throttled). Minimized or hidden, it steps back sooner.
    private static readonly TimeSpan BackgroundGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HiddenGrace = TimeSpan.FromSeconds(10);
    // While a game runs or streaming software is open (Services.LeanSignal), Exo behind them is the expected state:
    // it steps back after 15 s, not 60. A call keeps its host hot either way.
    private static readonly TimeSpan LeanGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrimSummaryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StoreRetrimInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HostCloseGrace = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _trimWake = new(0, 1);
    private long _trimChangedAt;
    private bool _launcherEventsAttached;
    private volatile TrimSnapshot _trimSnapshot = new(InFront: true, HiddenOrMinimized: false, MusicPopout: false, MusicPlaying: false);
    private volatile IReadOnlyList<EmbeddedRendererInfo> _embeddedRenderers = Array.Empty<EmbeddedRendererInfo>();
    private volatile bool _backgroundTreeLow;
    private int? _shellUiInactive;

    private sealed record TrimSnapshot(bool InFront, bool HiddenOrMinimized, bool MusicPopout, bool MusicPlaying);
    private sealed record EmbeddedRendererInfo(int Pid, EmbeddedRendererKind Kind, bool Music);

    // Subscribing builds the launcher (every adapter, the library and the
    // orchestrator) on this thread, so it waits until the shell has painted.
    private void AttachLauncherEvents()
    {
        if (_launcherEventsAttached || _closingWindow) return;
        _launcherEventsAttached = true;
        App.Services.Launcher.GameLaunched += OnGameLaunched;
        App.Services.Launcher.GameSessionCompleted += OnGameSessionCompleted;
        App.Services.Launcher.GameLaunching += OnGameLaunchingHold;
        AttachShellSleepTriggers();
    }

    // A game Exo starts (or the store client it starts for it) would inherit a lowered host's priority, so the host
    // holds Normal from just before the launch until a minute after it (InheritedPriority puts back any other).
    private static readonly TimeSpan LaunchPriorityHold = TimeSpan.FromSeconds(60);
    private long _launchHoldUntil;

    private void OnGameLaunchingHold(Models.GameEntry game)
    {
        Interlocked.Exchange(ref _launchHoldUntil, Environment.TickCount64 + (long)LaunchPriorityHold.TotalMilliseconds);
        ApplyHostPriority();
        _ = Task.Delay(LaunchPriorityHold + TimeSpan.FromSeconds(1)).ContinueWith(_ => { if (!_closingWindow) ApplyHostPriority(); }, TaskScheduler.Default);
    }

    private void OnGameLaunched(Models.GameEntry game)
    {
        Interlocked.Increment(ref _runningGames);
        ApplyHostPriority();
        DispatcherQueue.Enqueue(() => { if (!_closingWindow) ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized()); });
    }

    /// <summary>
    /// The foreground hook can miss Exo coming to the front (started without
    /// foreground rights, as a WMI launch is, or Activated flapping after the
    /// hook already saw it), which left Exo throttled and its shell paused while
    /// in front. While Exo thinks it is behind, each trim tick asks Windows.
    /// </summary>
    private void ReconcileForeground()
    {
        if (_closingWindow) return;
        var inFront = ForegroundWatcher.ExoInFront(WindowNative.GetWindowHandle(this));
        if (inFront == _windowActive) return;
        SetWindowActive(inFront);
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        ApplyHostPriority();
    }

    private void OnGameSessionCompleted(Models.GameEntry game)
    {
        if (Interlocked.Decrement(ref _runningGames) < 0)
            Interlocked.Exchange(ref _runningGames, 0);
        ApplyHostPriority();
        DispatcherQueue.Enqueue(() => { if (!_closingWindow) ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized()); });
    }

    // A call (or other live sound) started or stopped: the host's priority and the trim loop follow at once.
    private void OnLiveAudioChanged()
    {
        if (_closingWindow) return;
        ApplyHostPriority();
        if (_trimWake.CurrentCount == 0)
            try { _trimWake.Release(); } catch (SemaphoreFullException) { }
    }

    // A game started anywhere, or streaming software, opened or closed: the host's priority, the shell's flags and
    // the trim loop follow at once.
    private void OnLeanChanged()
    {
        if (_closingWindow) return;
        ApplyHostPriority();
        DispatcherQueue.Enqueue(() => { if (!_closingWindow) ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized()); });
    }

    // Only this process and the hosted music tree; other processes are
    // never touched. Exo itself yields only while a game (launched by Exo or
    // found running) or streaming software holds the foreground. Hosted clients yield while Exo is behind something and
    // they carry no sound; a selected room holds Normal except while a game
    // runs. Songs always keep Normal (see ApplyForeground).
    private void ApplyHostPriority()
    {
        try
        {
            var gameRunning = Volatile.Read(ref _runningGames) > 0 || Services.LeanSignal.Active;
            var throttle = HostPriorityPolicy.Wanted(gameRunning, _windowActive, Services.DiscordClient.Voice.LiveAudio.Active)
                && Environment.TickCount64 >= Interlocked.Read(ref _launchHoldUntil);
            Process.GetCurrentProcess().PriorityClass = throttle
                ? ProcessPriorityClass.BelowNormal
                : ProcessPriorityClass.Normal;
            InheritedPriority.HostLowered(throttle);
        }
        catch { }
    }

    // One loop, two classes: while Exo is in front, hidden embedded renderers
    // are trimmed once when they become eligible; while it is not, the whole
    // process tree is. The timer continues on a thread-pool thread, so nothing
    // here may touch WebView2 or XAML — UI state arrives through the volatile
    // snapshot and the renderer map, both published from the UI thread.
    private async Task TrimLoopAsync()
    {
        var lastSummary = DateTime.MinValue;
        var lastRetrim = DateTime.MinValue;
        var lastStoreRetrim = DateTime.MinValue;
        var policy = new ManagedProcessPolicyState();
        var interval = TrimInterval;
        // When Exo last left the front (null while it is in front).
        DateTime? awaySince = null;
        try
        {
            while (true)
            {
                // A visibility change wakes the loop early; the short settle
                // lets a burst of changes land as one tick.
                if (await _trimWake.WaitAsync(interval, _lifetimeCts.Token).ConfigureAwait(false))
                    await Task.Delay(500, _lifetimeCts.Token).ConfigureAwait(false);
                // One failed tick is logged and the loop goes on: trimming, priorities and the idle-music close
                // must not stop for the rest of the session.
                try
                {
                var state = _trimSnapshot;
                if (!state.InFront) DispatcherQueue.Enqueue(ReconcileForeground);
                var now = DateTime.UtcNow;
                awaySince = state.InFront ? null : awaySince ?? now;
                var away = awaySince is { } since && now - since >= (state.HiddenOrMinimized ? HiddenGrace : Services.LeanSignal.Active ? LeanGrace : BackgroundGrace);
                if (!away)
                {
                    if (_backgroundTreeLow)
                    {
                        _backgroundTreeLow = false;
                        BackgroundTrim.SetHostEfficiencyMode(false);
                        AppLog.Info("Trim: foreground mode (hidden Music renderers only).");
                    }
                }
                else if (!_backgroundTreeLow)
                {
                    _backgroundTreeLow = true;
                    BackgroundTrim.SetHostEfficiencyMode(true);
                    AppLog.Info("Trim: background mode (tree trimmed once per inactivity; memory priority low, host efficiency on).");
                }
                var musicVisible = state.MusicPopout;
                var groups = ManagedResourceGroups();
                var live = groups.SelectMany(group => group.Processes).ToHashSet();
                policy.Retain(live);
                InheritedPriority.Restore(groups.Where(group => group.Key is not ("exo" or "webview"))
                    .SelectMany(group => group.Processes).Select(identity => identity.Pid).ToHashSet());
                var musicPlaying = state.MusicPlaying;
                var hiddenRendererPids = _embeddedRenderers
                    .Where(renderer => EmbeddedRendererClass.ShouldTrimInForeground(renderer.Kind, musicVisible, musicPlaying))
                    .Select(renderer => renderer.Pid).ToHashSet();
                var liveAudio = Services.DiscordClient.Voice.LiveAudio.Active;
                var hostedPolicy = new HostedTrimPolicy(ShellHidden: state.HiddenOrMinimized && away, LiveAudio: liveAudio);
                var targets = ManagedProcessResources.TrimTargets(groups, hiddenRendererPids, hostedPolicy).ToHashSet();
                // Store clients' UI helpers while their window is out of sight or left behind other windows.
                var quietStores = Adapters.StoreClientHelpers.QuietGroupKeys(groups);
                targets.UnionWith(groups.Where(group => quietStores.Contains(group.Key)).SelectMany(group => group.Processes));
                var retrim = now - lastRetrim >= TimeSpan.FromSeconds(30);
                if (retrim) lastRetrim = now;
                // A store helper is retrimmed rarely: the Steam overlay draws in
                // steamwebhelper, and refaulting it every 30s would stutter it.
                var storeRetrim = now - lastStoreRetrim >= StoreRetrimInterval;
                if (storeRetrim) lastStoreRetrim = now;
                foreach (var group in groups)
                foreach (var identity in group.Processes)
                {
                    var eligible = targets.Contains(identity);
                    if (Adapters.StoreClientHelpers.IsGroupKey(group.Key))
                    {
                        // Memory only: no EcoQoS or priority class, so a shown client and its overlay run at full speed.
                        var storeStep = policy.Next(identity, eligible, eligible, storeRetrim && eligible && policy.GrewSinceTrim(identity));
                        if (storeStep.Apply && BackgroundTrim.ApplyManaged(identity, eligible, storeStep.Trim))
                            policy.Accept(identity, eligible, eligible, storeStep.Trim);
                        continue;
                    }
                    // A call keeps the host at normal memory priority.
                    var low = away && !(group.Key is "exo" && liveAudio);
                    var step = policy.Next(identity, low, eligible, retrim && eligible && policy.GrewSinceTrim(identity));
                    if (step.Apply && BackgroundTrim.ApplyManaged(identity, low, step.Trim))
                        policy.Accept(identity, low, eligible, step.Trim);
                }
                if (now - lastSummary >= TrimSummaryInterval)
                {
                    lastSummary = now;
                    var stores = groups.Where(group => Adapters.StoreClientHelpers.IsGroupKey(group.Key)).ToArray();
                    if (stores.Length > 0)
                    {
                        var storeSnapshot = ManagedProcessResources.Capture(stores);
                        AppLog.Info("Trim: store client helpers (working set/private MB): " + string.Join(", ",
                            storeSnapshot.Groups.Select(group =>
                                $"{group.Key} {group.WorkingSetBytes / 1048576d:N0}/{group.PrivateBytes / 1048576d:N0} across {group.ProcessCount}" +
                                (quietStores.Contains(group.Key) ? " (trimmed)" : " (shown)"))));
                    }
                    var snapshot = ManagedProcessResources.Capture(groups.Except(stores));
                    AppLog.Info($"Trim: managed total {snapshot.WorkingSetBytes / 1048576d:N0} MB working set, " +
                        $"{snapshot.PrivateBytes / 1048576d:N0} MB private across {snapshot.ProcessCount} processes" +
                        (snapshot.Complete ? "." : $" ({snapshot.ReadableCount}/{snapshot.ProcessCount} readable).") +
                        " By group (working set/private MB): " + string.Join(", ", snapshot.Groups.Select(group =>
                            $"{group.Key} {group.WorkingSetBytes / 1048576d:N0}/{group.PrivateBytes / 1048576d:N0} across {group.ProcessCount}")));
                }
                var settling = Environment.TickCount64 - Interlocked.Read(ref _trimChangedAt) < TrimSettleTime.TotalMilliseconds;
                interval = settling ? TrimInterval
                    : Services.LeanSignal.Active && _backgroundTreeLow ? TrimGameInterval
                    : !state.InFront || hiddenRendererPids.Count > 0 ? TrimInterval : TrimIdleInterval;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    AppLog.Info("Background trim tick failed: " + e.GetType().Name + ".");
                    interval = TrimInterval;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { AppLog.Info("Background trim failed: " + e.GetType().Name + "."); }
    }

    private bool _rendererRefreshBusy;
    private bool _rendererRefreshAgain;

    // UI thread: WebView2 environment APIs are dispatcher-bound. Runs when
    // the environment's processes change or the player's document does; the
    // renderer map only matters while the YouTube Music view exists.
    private async Task RefreshEmbeddedRendererMapAsync()
    {
        if (_rendererRefreshBusy) { _rendererRefreshAgain = true; return; }
        _rendererRefreshBusy = true;
        try
        {
            do
            {
                _rendererRefreshAgain = false;
                if (_closingWindow) return;
                RefreshRuntimeResourceIdentities();
                if (_musicWeb is null)
                {
                    _embeddedRenderers = Array.Empty<EmbeddedRendererInfo>();
                    continue;
                }
                var environment = await WebViewEnvironmentFactory.GetAsync();
                if (_closingWindow) return;
                var infos = await environment.GetProcessExtendedInfosAsync();
                var service = _musicWebService;
                var list = new List<EmbeddedRendererInfo>();
                foreach (var info in infos)
                {
                    if (info.ProcessInfo is not { Kind: CoreWebView2ProcessKind.Renderer } process) continue;
                    var sources = info.AssociatedFrameInfos.Select(frame => frame.Source).ToArray();
                    list.Add(new EmbeddedRendererInfo(
                        process.ProcessId,
                        EmbeddedRendererClass.From(sources),
                        EmbeddedRendererClass.HasMusicHost(sources, service)));
                }
                _embeddedRenderers = list;
            } while (_rendererRefreshAgain);
        }
        catch { }
        finally { _rendererRefreshBusy = false; }
    }

    private void PrepareWindowClose()
    {
        if (_closingWindow) return;
        SaveWindowPlacement();
        _closingWindow = true;
        _windowActive = false;
        AppLog.Info("Shutdown: window cleanup started.");
        _lifetimeCts.Cancel();
        try { _foregroundWatcher?.Dispose(); } catch { }
        StopHdrForGames();
        if (_launcherEventsAttached)
        {
            try { App.Services.Launcher.GameLaunched -= OnGameLaunched; } catch { }
            try { App.Services.Launcher.GameSessionCompleted -= OnGameSessionCompleted; } catch { }
        }
        try { AppWindow.Changed -= OnAppWindowChanged; } catch { }
        try { _notificationAreaIcon?.Dispose(); } catch { }
        try { _bridge?.Detach(); } catch { }
        AppLog.Info("Shutdown: bridge detached.");
        try { Services.GameRunningSignal.Clear(); } catch { }
        try { Services.FpsCounter.Shutdown(); } catch { }
        // The hosted sessions close side
        // by side under one bounded wait.
        CloseMusicWeb();
        var closing = new List<Task>();
        // The shader engine stops its queue so the driver writes its cache, then exits.
        if (_shadersAddon is { } shadersAddon) closing.Add(Task.Run(shadersAddon.Dispose));
        // Exo Capture closes a recording in progress, then exits.
        if (_captureAddon is { } captureAddon) closing.Add(Task.Run(captureAddon.Dispose));
        try { Task.WaitAll(closing.ToArray(), HostCloseGrace + TimeSpan.FromSeconds(1)); } catch { }
        try { WebHost.Close(); } catch { }
        AppLog.Info("Shutdown: shell webview closed.");
        // Steam left in Exo's offline mode for an outage or a family game: that reason ends with Exo. Written only while
        // Steam is closed; a running Steam is never closed or waited on for it (the next start settles the record).
        try
        {
            if (Adapters.SteamOfflineOwed.PutBackWhenSteamClosed(System.Diagnostics.Process.GetProcessesByName("steam").Length > 0))
                AppLog.Info("Shutdown: Steam goes back online.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Shutdown: Steam's offline mode left for the next start: " + ex.Message);
        }
        try { App.Services.Shutdown(); } catch { }
        App.Services.Settings.Flush();
        AppLog.Info("Shutdown: window cleanup completed.");
    }

    /// <summary>The title bar's minimize: an ordinary taskbar minimize. Hiding to the tray is for gameplay only.</summary>
    public void MinimizeWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    public void HideForGameplay()
    {
        if (_movingToNotificationArea) return;
        _movingToNotificationArea = true;
        try
        {
            EnsureNotificationAreaIcon();
            if (!_notificationAreaIcon!.Show())
            {
                if (AppWindow.Presenter is OverlappedPresenter fallbackPresenter)
                    fallbackPresenter.Minimize();
                ApplyWebViewMemoryTarget(hiddenOrMinimized: true);
                return;
            }

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();
            ApplyWebViewMemoryTarget(hiddenOrMinimized: true);
        }
        catch
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Minimize();
            }
            catch { }
        }
        finally
        {
            _movingToNotificationArea = false;
        }
    }

    private void EnsureNotificationAreaIcon()
    {
        if (_notificationAreaIcon is not null) return;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ExoLauncher.ico");
        _notificationAreaIcon = new NotificationAreaIcon(
            iconPath,
            RestoreFromNotificationArea,
            // Queued: closing destroys the icon's window, which is still answering this message. As the title bar's close
            // does it (WebHostBridge.CloseWindow): WinUI's loop can outlive its last window, leaving Exo running with none.
            () => DispatcherQueue.Enqueue(() =>
            {
                try { CloseWindow(); } catch { }
                try { Application.Current?.Exit(); } catch { }
            }));
    }

    private void RestoreFromNotificationArea()
        => RestoreAndActivate("its tray icon was pressed");

    /// Back from the tray or the taskbar and in front, for <paramref name="why"/> (logged).
    public void RestoreAndActivate(string why)
    {
        try
        {
            ForegroundWatcher.Asked(why);
            AppWindow.IsShownInSwitchers = true;
            AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            {
                if (_normalPlacement?.Maximized == true) presenter.Maximize();
                else presenter.Restore();
            }
            Activate();
            ApplyWebViewMemoryTarget(hiddenOrMinimized: false);
        }
        catch { }
    }

    /// <summary>
    /// Every window of Exo's own WebView2 browser (the shell's host and the hidden music player) is kept out of
    /// Alt+Tab and the taskbar: a WebView2 surface creates its own top-level window there, which made the running app
    /// look like several blank Exo Launchers. Done when a window of that browser process is shown (the foreground
    /// watcher's show hook), so a hidden Exo wakes for nothing. Only when the hook could not be installed does a
    /// one-second timer stand in (<see cref="StartWebViewWindowGuardTimer"/>).
    /// </summary>
    private void StartWebViewWindowGuard()
    {
        ForegroundWatcher.WindowActivity += OnWindowActivityForWebViewGuard;
        HideWebViewWindows();
    }

    private void OnWindowActivityForWebViewGuard(uint pid)
    {
        if (_closingWindow) return;
        var shell = WebHost.CoreWebView2?.BrowserProcessId ?? 0;
        var music = _musicWeb?.CoreWebView2?.BrowserProcessId ?? 0;
        if (pid != 0 && (pid == shell || pid == music)) HideWebViewWindows();
    }

    private void StartWebViewWindowGuardTimer()
    {
        if (_webViewWindowGuard is not null) return;
        _webViewWindowGuard = DispatcherQueue.CreateTimer();
        _webViewWindowGuard.Interval = TimeSpan.FromSeconds(1);
        _webViewWindowGuard.Tick += (_, _) => UiGuard.Run(HideWebViewWindows);
        _webViewWindowGuard.Start();
    }
    private void HideWebViewWindows()
    {
        if (WebHost.CoreWebView2 is { } shell) WebViewWindowHider.Hide(shell.BrowserProcessId);
        if (_musicWeb?.CoreWebView2 is { } music) WebViewWindowHider.Hide(music.BrowserProcessId);
    }

    private Task EnsureWebAsync()
    {
        if (_closingWindow || _webReady) return Task.CompletedTask;
        return _ensureWebTask ??= EnsureWebCoreAsync();
    }

    private async Task EnsureWebCoreAsync()
    {
        if (_webReady) return;
        // Plain black until the page draws: its splash is the first thing on screen, no text before it. A new WebView2
        // after a failure keeps saying it recovers.
        if (!_recreatingWeb) ShowBootPanel(null);
        LogStartupMilestone("webview-init-start");
        try
        {
            // 0x80070002 (file not found) is usually a runtime mid-update or a
            // stale evergreen pv: back off (0.5s, 2s, 5s) and, from the second
            // attempt on, name the installed runtime folder explicitly.
            for (var attempt = 0; ; attempt++)
            {
                CoreWebView2Environment? webViewEnvironment = null;
                try
                {
                    webViewEnvironment = await WebViewEnvironmentFactory.GetAsync(explicitInstalledRuntime: attempt > 0);
                    if (_closingWindow) return;
                    await WebHost.EnsureCoreWebView2Async(webViewEnvironment);
                    if (_closingWindow) return;
                    if (!_processWatchAttached)
                    {
                        _processWatchAttached = true;
                        webViewEnvironment.ProcessInfosChanged += (_, _) => UiGuard.Run(() =>
                        {
                            if (!_closingWindow) _ = RefreshEmbeddedRendererMapAsync();
                        });
                        // Raised once every process of this browser is gone: a new one may then take the user data folder.
                        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        webViewEnvironment.BrowserProcessExited += (_, _) => UiGuard.Run(() => gone.TrySetResult());
                        _shellBrowserGone = gone.Task;
                    }
                    _shellEnvironment = webViewEnvironment;
                    LogStartupMilestone("webview-core-ready");
                    break;
                }
                catch (Exception ex)
                {
                    if (_closingWindow) return;
                    var fileNotFound = WebViewRuntimeLocator.IsFileNotFound(ex);
                    AppLog.Warn($"webview-init-failed attempt={attempt + 1} hr=0x{ex.HResult:X8}: {ex.Message}");
                    if (fileNotFound && attempt == 0)
                        AppLog.Info("webview-runtime-candidates: " + WebViewRuntimeLocator.DescribeCandidates());
                    if (!fileNotFound || attempt >= WebViewRuntimeLocator.RetryDelays.Length)
                    {
                        ShowWebFallback();
                        _ensureWebTask = null;
                        return;
                    }
                    WebViewEnvironmentFactory.Reset(webViewEnvironment);
                    await Task.Delay(WebViewRuntimeLocator.RetryDelays[attempt]);
                    if (_closingWindow) return;
                }
            }

            var core = WebHost.CoreWebView2;
            if (core is not null) _ = WebExtensions.PruneAsync(core, "shell");
            if (core is null)
            {
                ShowWebFallback();
                _ensureWebTask = null;
                return;
            }
            HideWebViewWindows();

            core.NavigationCompleted += (sender, args) => UiGuard.Run(() =>
            {
                if (_closingWindow) return;
                if (args.IsSuccess)
                {
                    ApplyPageScale(again: true);
                    _shellUiInactive = null;
                    _gpuLostSinceLoad = false;
                    RefreshRuntimeResourceIdentities();
                    LogStartupMilestone("webview-navigation-complete");
                    // Shown once the page says its first frame is drawn (shell.painted), so the
                    // runtime's own surface never flashes; a page that never says so shows anyway.
                    _revealPending = true;
                    // Window.DispatcherQueue is null once the window has closed; the queue itself just refuses the work.
                    var queue = DispatcherQueue;
                    _ = Task.Delay(RevealFallback).ContinueWith(_ => queue.Enqueue(RevealPainted), TaskScheduler.Default);
                    if (PackagedSmoke.Enabled) _ = PackagedSmoke.RunAsync(core, this);
                }
                // A navigation Exo itself blocked (an untrusted main-frame address) ends here as cancelled; the page is still alive.
                else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) ShowWebFallback();
            });

            var www = ResolveWwwRoot();
            if (www is null)
            {
                core.NavigateToString(
                    "<html><body style='background:#000;color:#fff;font-family:Segoe UI;padding:24px'>" +
                    "<h2>Exo Launcher UI not built</h2><p>Run: <code>cd ui &amp;&amp; npm ci &amp;&amp; npm run build</code></p></body></html>");
                _webReady = true;
                return;
            }

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            WebHost.SizeChanged += (_, _) => ApplyPageScale();
            try { core.Settings.IsPasswordAutosaveEnabled = false; } catch { }
            try { core.Settings.IsGeneralAutofillEnabled = false; } catch { }
            try { core.Settings.IsWebMessageEnabled = true; } catch { }
            try { core.Settings.AreHostObjectsAllowed = false; } catch { }
            // Release: F5, Ctrl+R, Ctrl+P, Ctrl+F and the like do nothing (a reload or print dialog on the shell is never wanted).
            // Copy, paste and select-all are not accelerators and keep working. Debug keeps them for development.
#if !DEBUG
            try { core.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
#endif
            try { core.Settings.IsSwipeNavigationEnabled = false; } catch { }
            try { core.Settings.IsPinchZoomEnabled = false; } catch { }
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
#if DEBUG
            try { core.Settings.AreDevToolsEnabled = true; } catch { }
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                WebViewTrustPolicy.TrustedAppHost,
                www,
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += (sender, e) => UiGuard.Run(() => OnWebNavigationStarting(sender, e));
            core.NewWindowRequested += (sender, e) => UiGuard.Run(() => OnWebNewWindowRequested(sender, e));
            core.ProcessFailed += (sender, e) => UiGuard.Run(() => OnWebProcessFailed(sender, e));
            core.PermissionRequested += (sender, e) => UiGuard.Run(() => OnShellPermissionRequested(sender, e));
            // A page element in fullscreen (a stream, a trailer) takes the whole screen, not only Exo's window; leaving it
            // puts the window back as it was (maximized or not). From a maximized window the full-screen presenter keeps
            // the maximized bounds, 8 px past every edge of the screen, and the page spilled past them: the window is put
            // on exactly the screen's bounds.
            core.ContainsFullScreenElementChanged += (sender, _) => UiGuard.Run(() =>
            {
                try
                {
                    if (sender.ContainsFullScreenElement)
                    {
                        _beforeFullScreen ??= AppWindow.Presenter;
                        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is { } screen) AppWindow.MoveAndResize(screen.OuterBounds);
                    }
                    else if (_beforeFullScreen is { } before) { _beforeFullScreen = null; AppWindow.SetPresenter(before); }
                }
                catch (Exception ex) { AppLog.Warn("Fullscreen: " + ex.GetType().Name); }
            });

            // What is the user's own, served only to a request that names this run's token (Helpers/LocalFileHost): the
            // page also frames players from YouTube, Twitch, Spotify and others, and a mapping would answer them too.
            // Device pictures and game art are public pictures, but which ones are on this PC tells what you own. The
            // cover cache and its thumbnails are drawn everywhere, so the run's browser cache keeps them (a new token
            // next run); the bridge adds the token to every cover address it sends (WebHostBridge.PostSerialized).
            try
            {
                _files = new LocalFileHost(core, DispatcherQueue);
                Directory.CreateDirectory(Services.CoverArtService.CacheRoot);
                _files.Serve(Services.CoverArtService.VirtualHost, () => Services.CoverArtService.CacheRoot, LocalFileHost.Pictures, LocalFileHost.KeptForRun);
                // Every frame: Exo Mouse's page shows the same thumbnails.
                _files.ServeMade(Services.CoverArtService.ThumbnailHost, ReadThumbnailAsync, LocalFileHost.KeptForRun);
                _files.Serve(Services.DeviceImages.VirtualHost, () => Services.DeviceImages.CacheRoot, LocalFileHost.Pictures);
                _files.Serve(WebHostBridge.NotePhotosHost, () => Services.NoteStore.Shared.PhotosFolder, LocalFileHost.Pictures);
                MapCaptureHosts(_files);
            }
            catch (Exception ex) { AppLog.Warn("Exo's own files could not be served: " + ex.Message); }

            // Exo Mouse likewise: another origin in its frame, so it reaches Exo only through mouseRelay.
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    WebViewTrustPolicy.MouseHost,
                    Path.Combine(www, "mouse"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch (Exception ex) { AppLog.Warn("Exo Mouse page could not be mapped: " + ex.Message); }
            // The Discord Shop's card form: another origin, which alone may load Stripe's card fields.
            try
            {
                core.SetVirtualHostNameToFolderMapping(WebViewTrustPolicy.PayHost, Path.Combine(www, "pay"), CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch (Exception ex) { AppLog.Warn("Card form page could not be mapped: " + ex.Message); }

            try { _bridge?.Detach(); } catch { }
            _bridge = new WebHostBridge(App.Services, DispatcherQueue);
            _bridge.Attach(core);
            _bridge.NotifyWindowState(IsMaximized);
            LogStartupMilestone("bridge-attached");


            WebHost.Source = new Uri(WebViewTrustPolicy.StartUri);
            LogStartupMilestone("webview-navigation-start");
            _webReady = true;
        }
        catch
        {
            _ensureWebTask = null;
            ShowWebFallback();
        }
    }

    private static void OnWebNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebViewTrustPolicy.IsTrustedAppUri(e.Uri)) return;

        e.Cancel = true;
        AppLog.Warn("Blocked an untrusted main-frame WebView navigation.");
    }

    private bool _processWatchAttached;

    /// <summary>
    /// The camera for Discord calls in Exo's own page: Exo's own origin may use it (Windows' camera privacy still
    /// decides, and its first use is the Turn On Camera click), every other frame (Exo Mouse, Shaders) may not.
    /// Other permissions keep WebView2's own handling.
    /// </summary>
    private static void OnShellPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind != CoreWebView2PermissionKind.Camera) return;
        e.SavesInProfile = false;
        e.State = WebViewTrustPolicy.IsTrustedAppUri(e.Uri) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>"commit 34.9/35.5 GB; most: cl x8 18.2 GB, …": Windows' commit charge and the programs holding the most.</summary>
    private static string SystemCommitLine()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var commit = GlobalMemoryStatusEx(ref status)
            ? FormattableString.Invariant($"commit {(status.TotalPageFile - status.AvailPageFile) / 1e9:0.0}/{status.TotalPageFile / 1e9:0.0} GB")
            : "commit unknown";
        var rows = new List<(string, long)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { rows.Add((process.ProcessName, process.PrivateMemorySize64)); }
                catch { /* gone, or another user's */ }
            }
        }
        return commit + "; most: " + CommitHolders(rows, 3);
    }

    /// <summary>The programs holding the most private memory, each name's processes summed: "cl x8 18.2 GB, node 1.1 GB".</summary>
    internal static string CommitHolders(IEnumerable<(string Name, long Bytes)> processes, int top) =>
        string.Join(", ", processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Bytes: g.Sum(p => p.Bytes)))
            .OrderByDescending(g => g.Bytes)
            .Take(top)
            .Select(g => FormattableString.Invariant($"{g.Name}{(g.Count > 1 ? " x" + g.Count : "")} {g.Bytes / 1e9:0.0} GB")));

    private static void OnWebNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        AppLog.Warn("Blocked a WebView new-window request.");
    }

    /// Hides the page behind black, with a status line only when there is something to say (a recovery).
    private void ShowBootPanel(string? text)
    {
        WebBootText.Text = text ?? "";
        WebBootProgressRing.IsActive = text is not null;
        WebBootPanel.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        WebViewFallback.Visibility = Visibility.Collapsed;
        // Startup keeps the page on screen: it is black until it draws, and showing a hidden
        // WebView2 later flashes one frame of its own surface.
        if (text is not null) WebHost.Visibility = Visibility.Collapsed;
    }

    private static readonly TimeSpan RevealFallback = TimeSpan.FromSeconds(3);
    private bool _revealPending;

    /// shell.painted, or the fallback: shows the page once per navigation.
    internal void RevealPainted()
    {
        if (!_revealPending || _closingWindow) return;
        _revealPending = false;
        RevealWeb();
    }

    private void RevealWeb()
    {
        WebBootProgressRing.IsActive = false;
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Visible;
        // Transparent until now (XAML), never collapsed at startup: a collapsed WebView2 shown later,
        // or one shown before its first frame, flashes its own gray surface.
        WebHost.Opacity = 1;
        UpdateCaptionDragRegions();
        LogStartupMilestone("webview-visible");
        App.Services.StartDeferredServices();
        AttachLauncherEvents();
        OnPageBack();
    }

    private void LogStartupMilestone(string milestone) =>
        AppLog.Info($"PERF startup milestone={milestone} elapsedMs={_startupStopwatch.ElapsedMilliseconds}");

    private void ShowWebFallback(bool crashed = false)
    {
        if (crashed)
        {
            // The runtime is there and ran; it kept stopping (MainWindow.WebRecovery). Restart is the way back, not a WebView2 install.
            WebViewFallbackTitle.Text = "Exo needs a restart";
            WebViewFallbackBody.Text = "Its window stopped and couldn't come back by itself.";
            WebViewFallbackButton.Visibility = Visibility.Collapsed;
        }
        WebBootProgressRing.IsActive = false;
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Visible;
        App.Services.StartDeferredServices();
        AttachLauncherEvents();
        WebViewRestartButton.Focus(FocusState.Programmatic);
    }

    private void WebViewRestartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The SDK releases the old single-instance process before starting
            // its replacement. On success this call does not return.
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
            AppLog.Warn("App restart failed: " + reason);
            WebViewFallbackBody.Text = "Exo could not restart automatically. Close Exo and open it again.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("App restart failed: " + ex.Message);
            WebViewFallbackBody.Text = "Exo could not restart automatically. Close Exo and open it again.";
        }
    }

    private void WebViewFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static string? ResolveWwwRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
                return c;
        }
        return null;
    }

    /// <summary>
    /// A grid thumbnail for https://thumbs.exo-launcher.local/&lt;token&gt;/&lt;cover&gt;?w=&lt;px&gt;[&amp;dim=1], on the thread pool
    /// (LocalFileHost checks the token and the page first); null for a width the host doesn't make or a cover it hasn't.
    /// </summary>
    private static async Task<(byte[] Bytes, string Type)?> ReadThumbnailAsync(string name, string query)
    {
        var width = 0;
        var dim = false;
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("w=", StringComparison.Ordinal) && int.TryParse(pair.AsSpan(2), out var w)) width = w;
            else if (pair is "dim=1" or "dim") dim = true;
        }
        if (!Services.CoverArtService.IsThumbnailWidth(width)) return null;
        return await Services.CoverArtService.ReadThumbnailAsync(name, width, dim).ConfigureAwait(false) is { } body ? (body.Bytes, body.ContentType) : null;
    }

    public bool IsMaximized =>
        AppWindow.Presenter is OverlappedPresenter presenter &&
        presenter.State == OverlappedPresenterState.Maximized;

    public bool ToggleMaximize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return false;
        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            return false;
        }
        presenter.Maximize();
        return true;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closingWindow) return;
        if (!args.DidPresenterChange && !args.DidPositionChange && !args.DidSizeChange &&
            !args.DidVisibilityChange) return;

        // AppWindow minimums are physical pixels. Moving between monitors can
        // change DPI without changing the logical 960×640 product floor.
        ApplyWindowMinimumSize();
        // Another screen can call for another zoom; the same one returns at once.
        if (args.DidPositionChange) ApplyPageScale();
        if (args.DidPresenterChange || args.DidSizeChange)
        {
            _bridge?.NotifyWindowState(IsMaximized);
            // Regions are client-relative: a move alone never changes them,
            // and re-setting them on every step of a drag only adds work.
            UpdateCaptionDragRegions();
        }
        if (args.DidPresenterChange || args.DidPositionChange || args.DidSizeChange)
            RememberWindowPlacement();
        // Minimized and restored keep the same presenter and visibility: only the size and place change. A restore that
        // doesn't take the focus (shown without activating it) left the page marked hidden, and a watched stream showed no picture.
        if (args.DidPresenterChange || args.DidVisibilityChange || args.DidSizeChange || args.DidPositionChange)
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
    }

    private bool WindowIsHiddenOrMinimized()
    {
        try
        {
            if (!AppWindow.IsVisible) return true;
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
                return true;
        }
        catch { }
        return false;
    }

    /// Three flags for the shell: exoInactive (Exo is not the focused window, or
    /// hidden) pauses background work; exoHidden (hidden or minimized) is all
    /// that stops something the user can still see, like the mini player's clock;
    /// exoStreaming (streaming software is open, Services.LeanSignal) keeps personal details off screen.
    private async Task UpdateShellActivityAsync(bool inactive, bool hidden)
    {
        var core = WebHost.CoreWebView2;
        var streaming = Services.LeanSignal.StreamingOpen;
        var state = (inactive ? 1 : 0) | (hidden ? 2 : 0) | (streaming ? 4 : 0);
        if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source) || _shellUiInactive == state) return;
        _shellUiInactive = state;
        try
        {
            await core.ExecuteScriptAsync("document.documentElement.dataset.exoInactive='" + (inactive ? "true" : "false") +
                "';document.documentElement.dataset.exoHidden='" + (hidden ? "true" : "false") +
                "';document.documentElement.dataset.exoStreaming='" + (streaming ? "true" : "false") +
                "';document.dispatchEvent(new Event('exo:activity'));");
        }
        catch { _shellUiInactive = null; }
    }

    private void ApplyWebViewMemoryTarget(bool hiddenOrMinimized)
    {
        _ = UpdateShellActivityAsync(hiddenOrMinimized || !_windowActive, hiddenOrMinimized);
        HangWatchdog.Refresh();
        if (!hiddenOrMinimized) CheckPaintWhenShown();
        var snapshot = new TrimSnapshot(_windowActive, hiddenOrMinimized,
            _musicPopouts.Count > 0, Volatile.Read(ref _musicWebAudio));
        if (snapshot != _trimSnapshot)
        {
            _trimSnapshot = snapshot;
            Interlocked.Exchange(ref _trimChangedAt, Environment.TickCount64);
            if (_trimWake.CurrentCount == 0)
                try { _trimWake.Release(); } catch (SemaphoreFullException) { }
        }
        NotifyConnectedPermissionStateChanged();
        ApplyMusicMemoryTarget();
        ApplyShellSleep(hiddenOrMinimized);
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null) return;
            SetWebViewMemoryTarget(core, hiddenOrMinimized
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal);
        }
        catch { }
    }

    /// <summary>The window's own background in the page's pure black, so a resize or the first frame never flashes another color.</summary>
    internal void ApplyAppearance()
    {
        var color = Shell.Scene.Tokens.Bg;
        var words = Shell.Scene.Tokens.Fg;
        var secondary = Shell.Scene.Tokens.Muted;
        try
        {
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            WebHost.DefaultBackgroundColor = color;
            WebViewFallbackTitle.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(words);
            WebViewFallbackBody.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(secondary);
            WebBootText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(secondary);
        }
        catch (Exception ex) { AppLog.Debug("Appearance: window background: " + ex.Message); }
    }

    private void ApplyWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            ApplyWindowMinimumSize();
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
            catch { }
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }
            catch { }
        }
        UpdateCaptionDragRegions();
    }

    private void ApplyWindowMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        // The smallest page, not window: at the page's zoom (MainWindow.PageScale) it is that much bigger, so a 4K screen
        // at 100% can't shrink the page to 480 pixels wide where a 1080p one stops at 960. The screen still caps it.
        var scale = GetWindowScale() * PageZoom;
        var width = Math.Max(1, (int)Math.Round(MinWindowWidth * scale));
        var height = Math.Max(1, (int)Math.Round(MinWindowHeight * scale));
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area is not null) { width = Math.Min(width, area.WorkArea.Width); height = Math.Min(height, area.WorkArea.Height); }
        }
        catch { }
        presenter.PreferredMinimumWidth = width;
        presenter.PreferredMinimumHeight = height;
    }

    private void ApplyInitialWindowBounds()
    {
        try
        {
            var scale = 1.0;
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }

            var w = (int)Math.Round(DefaultWindowWidth * scale);
            var h = (int)Math.Round(DefaultWindowHeight * scale);

            try
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                if (area is not null)
                {
                    w = Math.Min(w, area.WorkArea.Width);
                    h = Math.Min(h, area.WorkArea.Height);
                }
            }
            catch { }

            AppWindow.Resize(new SizeInt32(w, h));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void TryCenterOnScreen()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            var display = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Nearest);
            if (display is null) return;
            var work = display.WorkArea;
            var x = work.X + (work.Width - appWindow.Size.Width) / 2;
            var y = work.Y + (work.Height - appWindow.Size.Height) / 2;
            appWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            ContentHost.ClearValue(FrameworkElement.WidthProperty);
            ContentHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        catch { }
        ApplyWindowMinimumSize();
        UpdateCaptionDragRegions();
    }

    private double GetWindowScale()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "ExoLauncher.ico");
            if (File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch { }
    }
}
