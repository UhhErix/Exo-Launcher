using ExoLauncher.Services.Account;
using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class AppServices
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _deferredServicesStarted;
    public SettingsService Settings { get; } = new();
    public DependencyService Dependencies { get; } = new();
    public AppUpdateService Updater { get; } = new();
    public HiddenStoreRuntime HiddenStores { get; } = new();
    public AchievementService Achievements { get; } = new();
    public GogAuthService GogAuth { get; } = new();
    public EpicAuthService EpicAuth { get; } = new();
    public GogOwnedLibraryService GogOwnedLibrary { get; } = new();
    /// <summary>The game sheet's Shaders line, read from the Exo Shaders engine.</summary>
    internal ScsKillerStatus Shaders { get; } = new();
    internal ShadersHostService ShadersHost => _shadersHost ??= new ShadersHostService(this);
    private ShadersHostService? _shadersHost;
    internal HdrHostService HdrHost => _hdrHost ??= new HdrHostService(this, _lifetime.Token);
    private HdrHostService? _hdrHost;
    internal HeadlessCaptureService HeadlessCapture => _headlessCapture ??= new HeadlessCaptureService(this);
    private HeadlessCaptureService? _headlessCapture;
    internal HeadlessMusicService HeadlessMusic => _headlessMusic ??= new HeadlessMusicService();
    private HeadlessMusicService? _headlessMusic;
    internal MusicEngineService MusicEngine => _musicEngine ??= new MusicEngineService();
    private MusicEngineService? _musicEngine;
    internal MousePanelService MousePanel => _mousePanel ??= new MousePanelService(this);
    private MousePanelService? _mousePanel;

    /// <summary>The Shaders page's Prepare, for Unreal games.</summary>
    internal ShaderPrepare ShaderPrepare => _shaderPrepare ??= new ShaderPrepare(() => Launcher);
    private ShaderPrepare? _shaderPrepare;
    /// <summary>The Store room: deals, prices, free games and the wishlist.</summary>
    internal StoreService Store => _store.Value;
    /// <summary>Settings > Downloads applied: holds while playing and outside the download hours, idle auto-updates.</summary>
    internal DownloadPolicy Downloads => _downloads ??= new DownloadPolicy(this);
    private DownloadPolicy? _downloads;
    /// <summary>Temporary files, crash reports, the thumbnail cache and Exo's old backups, cleaned while the PC is idle.</summary>
    private JunkAutoClean? _junk;
    /// <summary>Compress games: Windows' own file compression for a game's install, by a press or while the PC is idle.</summary>
    internal GameCompressor Compressor => _compressor ??= new GameCompressor(this);
    private GameCompressor? _compressor;
    private readonly Lazy<StoreService> _store = new(() => new StoreService(RunningVersion));
    /// <summary>HID devices for the Devices room's mouse module.</summary>
    internal Hid.HidService Hid => _hid.Value;
    private readonly Lazy<Hid.HidService> _hid = new(() => new Hid.HidService());

    private readonly Lazy<IReadOnlyList<IStoreAdapter>> _adapters;
    private readonly Lazy<LibraryService> _library;
    private readonly Lazy<LaunchOrchestrator> _launcher;
    private readonly Lazy<GameArtworkService> _artwork;

    public IReadOnlyList<IStoreAdapter> Adapters => _adapters.Value;
    public LibraryService Library => _library.Value;
    public LaunchOrchestrator Launcher => _launcher.Value;
    public GameArtworkService Artwork => _artwork.Value;
    public AppServices()
    {
        _adapters = new Lazy<IReadOnlyList<IStoreAdapter>>(BuildAdapters);
        _library = new Lazy<LibraryService>(BuildLibrary);
        _artwork = new Lazy<GameArtworkService>(() => new GameArtworkService(Library, Settings));
        _launcher = new Lazy<LaunchOrchestrator>(BuildLauncher);
    }

    public void Initialize()
    {
        Settings.Load();
        AccountSyncRuntime.Start(Settings, () => Store);
        // Always overwrite settings.json version with the running build (not a stale 1.0.0).
        Settings.SyncAppVersion(AppVersion);
        // An update kept for after a game is ready again when the game ends (UpdateFlow).
        LeanSignal.Changed += Updater.GameChanged;
        LeanSignal.Changed += Dependencies.OnGameStateChanged;

        // Adapters, Library, and Launcher are created on first access so the
        // shell can paint before any official-client probing runs.
        AppLog.Info($"Exo Launcher {AppVersion} services initialized; adapters will load on first use.");
    }

    private IReadOnlyList<IStoreAdapter> BuildAdapters()
    {
        if (PackagedSmoke.Enabled) return [new LocalAdapter(Settings)];
        var sw = Stopwatch.StartNew();
        var factories = new (string Name, Func<IStoreAdapter> Factory)[]
        {
            ("steam", () => new SteamAdapter()),
            ("epic", () => new EpicAdapter(EpicAuth)),
            ("gog", () => new GogAdapter(GogAuth, GogOwnedLibrary)),
            ("riot", () => new RiotAdapter()),
            ("xbox", () => new XboxAdapter()),
            ("ea", () => new EaAdapter()),
            ("ubisoft", () => new UbisoftAdapter()),
            ("battlenet", () => new BattleNetAdapter()),
            ("amazon", () => new AmazonAdapter()),
            ("rockstar", () => new RockstarAdapter()),
            ("itch", () => new ItchAdapter()),
            ("minecraft", () => new MinecraftAdapter()),
            ("roblox", () => new RobloxAdapter()),
            ("paradox", () => new ParadoxAdapter()),
            ("wargaming", () => new WargamingAdapter()),
            ("local", () => new LocalAdapter(Settings)),
        };

        var built = new List<IStoreAdapter>(factories.Length);
        var perAdapter = new List<(string Name, double Ms)>();
        foreach (var (name, factory) in factories)
        {
            var t = Stopwatch.StartNew();
            try
            {
                built.Add(factory());
                perAdapter.Add((name, t.Elapsed.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                // A failing adapter constructor must not take down the rest of
                // the store list. Log it and continue with a null-safe gap.
                AppLog.Warn($"Adapter '{name}' constructor failed: {ex.GetType().Name}: {ex.Message}");
                perAdapter.Add((name, t.Elapsed.TotalMilliseconds));
            }
        }

        sw.Stop();
        // Report the slowest three and the total so startup audit is concrete.
        var topSlow = perAdapter.OrderByDescending(x => x.Ms).Take(3)
            .Select(x => $"{x.Name}={x.Ms:F1}ms")
            .ToList();
        AppLog.Info($"Built {built.Count} store adapters in {sw.Elapsed.TotalMilliseconds:F1}ms; slowest: {string.Join(", ", topSlow)}");

        // Wire cross-adapter cache events now that the instances exist.
        GogOwnedLibrary.CacheUpdated += OnGogOwnedLibraryUpdated;
        EpicAdapter.OwnedLibraryCacheUpdated += OnEpicOwnedLibraryUpdated;
        EpicPlaytime.CachedMinutesUpdated += OnEpicPlaytimeUpdated;
        SteamClientReady.ClientReady += OnSteamClientReady;
        SteamSignIn.SteamWebsiteToken.Arrived += OnSteamWebsiteToken;

        return built;
    }

    private LibraryService BuildLibrary()
    {
        var lib = new LibraryService(Adapters, Settings);
        return lib;
    }

    private LaunchOrchestrator BuildLauncher()
    {
        var launcher = new LaunchOrchestrator(
            Adapters,
            Settings,
            Dependencies,
            Achievements,
            revalidateQueuedGame: Library.RevalidateActionGameAsync);
        return launcher;
    }

    /// <summary>
    /// Starts background-only process and filesystem observation after the main
    /// shell has painted. Neither service is required to construct the first
    /// library response, and both remain in-process for the rest of the run.
    /// </summary>
    /// <summary>
    /// What Exo took out of start-up is watched from Exo's start (StartupGuard): checked now, when a game starts or ends, every ten
    /// minutes and at once when Windows says start-up changed. With nothing removed there is no watch.
    /// </summary>
    private void StartStartupGuard()
    {
        StartupLocks.GameFolders = () => [.. Library.PeekCachedLibrary().Where(game => game.Installed && !string.IsNullOrEmpty(game.Path))
            .Select(game => Directory.Exists(game.Path) ? game.Path! : System.IO.Path.GetDirectoryName(game.Path!)).OfType<string>()];
        _ = Task.Run(() =>
        {
            try { StartupGuard.StartShared(); }
            catch (Exception ex) { AppLog.Warn("Startup: the guard did not start: " + ex.GetType().Name); }
        });
        Launcher.GameLaunched += game => { _ = Task.Run(() => StartupGuard.Shared?.NotifyGame()); };
        Launcher.GameSessionCompleted += game => { _ = Task.Run(() => StartupGuard.Shared?.NotifyGame()); };
    }

    public void StartDeferredServices()
    {
        if (PackagedSmoke.Enabled) return;
        if (Interlocked.Exchange(ref _deferredServicesStarted, 1) != 0) return;
        Shaders.Start();
        StartStartupGuard();
        _ = Task.Run(() => PlaytimeService.TryAutoSeedRiotHoursAsync(() => Library.RefreshDerivedStateAsync(CancellationToken.None)));
        // Today's backup of the device-only data, once a day (a second start the same day writes nothing). Only once setup is
        // done: that closes the restore offer for this install, so a fresh install's own first backup is never offered back.
        if (Settings.Current.OnboardingComplete)
            _ = Task.Run(() =>
            {
                LauncherBackup.CloseOffer(PathHelper.AppDataDir);
                BackUpToday(LauncherBackup.Moment.Start);
            });
        // The day's play is in today's backup once the last game running closes, not only at the next day's start.
        Launcher.GameSessionCompleted += ended => _ = Task.Run(() => BackUpToday(LauncherBackup.Moment.GameClosed));
        // Exo EQ: the move from Lucid once (it needs the library, for Lucid's per-game EQ), then exo.txt as it should be now.
        _ = Task.Run(async () =>
        {
            LucidRetire.Hosted = () => Settings.Current.StartLucidWithExo;
            LucidRetire.Games = () => [.. Library.PeekCachedLibrary().Where(game => game.Installed).Select(game => (game.Id, game.Title))];
            try { if (!ExoEq.Current.LucidImported) await Library.GetLibraryAsync().ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Exo EQ: the library wasn't read for Lucid's games: " + ex.GetType().Name); }
            ExoEq.Start();
            if (Settings.Current.StartLucidWithExo) Settings.UpdateProfile(s => s.StartLucidWithExo = false);
            await LucidRetire.ResolveAsync().ConfigureAwait(false);
        });
        Store.Start();
        Downloads.Start();
        (_junk = new JunkAutoClean(this)).Start();
        Compressor.Start();
        // Exo's parts setup didn't fetch (or that no longer match their pins), quietly, when downloads aren't held.
        ExoComponents.FillInBackground(() => Launcher.HoldReason, _lifetime.Token);
        NvidiaTrayIcon.Soon();
        // What a driver changed outside Exo: RTX Video once per driver, interrupts a driver put back (30 s in, off this thread).
        _ = DriverRestore.AtStartAsync(_lifetime.Token);
        // Exo's driver settings come back after a driver installed by Windows Update, the vendor's app or by hand: a look
        // when an install anyone ran says it is done, and one every ten minutes (one registry read when nothing changed).
        DriverKeepWatch.Start(_lifetime.Token);
        // The games' shaders are compiled again for a new driver, by itself, while nobody uses the PC (no scheduled task).
        ShaderRebuildWatch.Start(this, _lifetime.Token);
        // PC › Drivers read once in the background (45 s in), so the room has it when it opens.
        _ = Task.Run(() => DriverCatalog.Shared.WarmAsync(() => Launcher.AnyGameRunning || LeanSignal.Active, _lifetime.Token));
        // Exo's power plan: switched to by itself on Windows' own plans, refreshed while it's on (PowerPlan.Auto). Game Mode on, and kept on.
        _ = Task.Run(() =>
        {
            // After a Windows update, what it reset is looked at once, before the writes below (WindowsKeep).
            try { WindowsKeep.Shared.Look("on start"); }
            catch (Exception ex) { AppLog.Warn("Windows: the look after an update failed: " + ex.Message); }
            // A Setup holding Exo's plan from before a restart takes it back itself (Setups.Start).
            try { if (!Setups.HoldsPlan() && PowerPlan.Shared.Auto(PcHealth.ThisPcProfile()) is { } why) AppLog.Info("Power plan: " + why); }
            catch (Exception ex) { AppLog.Warn("Power plan: " + ex.Message); }
            WindowsGameMode.KeepOn();
            // Autopilot after the keeper: what a crash left goes back, and Game Mode follows Autopilot's switch.
            Autopilot.Start();
            WindowsGameBar.KeepOff();
        });
        RadeonProfileService.Shared.Start(Launcher, () => GpuProfileService.MainScreen("amd"));
        IntelProfileService.Shared.Start(Launcher);
        // Properties › Anti-cheat: a Steam game the player starts from Steam's entry without its anti-cheat.
        AntiCheatCheck.ChoseWithoutAntiCheat = game => Settings.SteamWithoutAntiCheat(game.Id);
        // Its shader recorder goes in only for a session Exo starts, through Exo Shaders' engine (DlssSwapService.SessionMods.cs).
        DlssSwapService.RecorderSwitch = (id, on, ct) => ShadersHost.RecorderSetAsync(id, on, ct);
        // Frame generation Exo put into a game: checked as each game starts and ends, and kept on the mod's newest release.
        DlssSwapService.WatchFrameGen(Launcher, Settings, Library);
        // Open Exo when Windows starts: the Run value follows the switch (an update or a moved install puts its path right).
        var startWithWindows = Settings.Current.StartWithWindows;
        _ = Task.Run(() => StartWithWindows.Apply(startWithWindows));
        // Share my games: which game you opened from Exo, for your Discord presence when you turned it on.
        DiscordClient.DiscordSession.WatchGames(Launcher, Settings, Library);
        // Share when I'm getting ready: a game installing, building its shaders, or ready, for friends who turned it on.
        DiscordClient.DiscordSession.WatchPrep(Launcher, ShaderPrepare, Settings, Library);
        // Setups: Exo's plan and the apps in the way closed while a competitive game runs, the way back kept on disk.
        Setups.Start(Launcher, _lifetime.Token);
        _ = Task.Run(async () =>
        {
            // Saved downloads resume once the library can answer for them: a
            // resume checks the game against the current library first.
            try
            {
                for (var waited = 0; waited < 60 && Library.PeekCachedLibrary().Count == 0; waited++)
                    await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
                await Task.Delay(2000, _lifetime.Token).ConfigureAwait(false);
                Launcher.ResumeAfterRestart();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn("Resume after restart failed: " + ex.Message); }
        }, _lifetime.Token);
        _ = Task.Run(async () =>
        {
            // Each migration runs on its own; one failing must not skip the rest.
            try
            {
                await Achievements.InitializeAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { AppLog.Debug("Deferred achievements init failed: " + ex.GetType().Name); }
            if (_lifetime.IsCancellationRequested) return;
            try { Settings.HandStartupToExo(); }
            catch (Exception ex) { AppLog.Warn("Startup hand-off failed: " + ex.GetType().Name); }
            if (!_lifetime.IsCancellationRequested && Settings.Shared.StartupMigrationVersion < CurrentStartupMigrationVersion)
            {
                await RunOneTimeStartupMigrationsAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }, _lifetime.Token);
        _ = Task.Run(() =>
        {
            try
            {
                if (_lifetime.IsCancellationRequested) return;
                Library.StartWatchers();
                if (_lifetime.IsCancellationRequested) return;
                HiddenStores.Start();
                AppLog.Info("Deferred store observers ready.");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Deferred store observers failed: " + ex.GetType().Name);
            }
        }, _lifetime.Token);
    }

    /// <summary>
    /// Today's backup of everything the player chose (PlayerData), once setup is done, when LauncherBackup.Due says so for
    /// this moment (at start when today has none; when the last game running closes; at exit when the last is not fresh).
    /// One at a time.
    /// </summary>
    private void BackUpToday(LauncherBackup.Moment moment)
    {
        if (!Settings.Current.OnboardingComplete || HostMode.IsDisposable) return;
        lock (LauncherBackup.Gate)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var written = LauncherBackup.WrittenToday(PathHelper.AppDataDir, LauncherBackup.DefaultFolder, now);
                if (!LauncherBackup.Due(moment, written is not null, Launcher.AnyGameRunning, now - (written ?? DateTimeOffset.MinValue))) return;
                var before = LauncherBackup.LastResult(PathHelper.AppDataDir);
                var outcome = LauncherBackup.BackUpDaily(PathHelper.AppDataDir, LauncherBackup.DefaultFolder, now,
                    typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0", replace: true, LauncherBackup.ThisPc(PathHelper.AppDataDir));
                if (!outcome.Ok) AppLog.Warn("Backup: " + outcome.Message);
                // A stop in the middle of a session is said as it happens, not at the next start.
                LauncherBackup.AnnounceStop(before, LauncherBackup.LastResult(PathHelper.AppDataDir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Backup: " + ex.GetType().Name); }
        }
    }
    public void Shutdown()
    {
        try { _lifetime.Cancel(); } catch { }
        LeanSignal.Changed -= Dependencies.OnGameStateChanged;
        Updater.Exiting();
        PlaytimeService.FlushActiveSessions();
        // Compare held as Exo closes: the EQ comes back, as it would when let go.
        if (ExoEq.Comparing) ExoEq.Compare(false);
        // At exit, with the sessions just flushed: today's backup holds the whole run, inside the 8 s the installer allows.
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var backup = Task.Run(() => BackUpToday(LauncherBackup.Moment.Exit));
        try { if (!backup.Wait(LauncherBackup.BackupMayWait(budget.Elapsed))) AppLog.Warn("Backup: the backup at exit was still being written as Exo closed."); }
        catch (AggregateException ex) { AppLog.Warn("Backup: at exit: " + ex.InnerException?.GetType().Name); }
        NvidiaTrayIcon.Stop();
        DriverKeepWatch.Stop();
        StartupGuard.Shared?.Dispose();
        Shaders.Dispose();
        if (_store.IsValueCreated) _store.Value.Dispose();
        if (_hid.IsValueCreated) _hid.Value.Dispose();
        _hdrHost?.Stop();
        _shadersHost?.Dispose();
        _headlessCapture?.Dispose();
        _musicEngine?.Close();
        _mousePanel?.Close();
        _downloads?.Dispose();
        _junk?.Dispose();
        _compressor?.Dispose();
        Achievements.Dispose();
        HiddenStores.Dispose();
        GogAuth.Dispose();
        EpicAuth.Dispose();
        EpicPlaytime.CachedMinutesUpdated -= OnEpicPlaytimeUpdated;
        EpicAdapter.OwnedLibraryCacheUpdated -= OnEpicOwnedLibraryUpdated;
        SteamClientReady.ClientReady -= OnSteamClientReady;
        SteamSignIn.SteamWebsiteToken.Arrived -= OnSteamWebsiteToken;
        GogOwnedLibrary.CacheUpdated -= OnGogOwnedLibraryUpdated;
        GogOwnedLibrary.Dispose();
        if (_library.IsValueCreated)
        {
            Library.DisposeWatchers();
            LibraryDiskCache.FlushPending(TimeSpan.FromSeconds(2));
        }
        // Last: app.exit is queued and one send gets up to two seconds, never more.
        Helpers.Telemetry.Exit(clean: true);
    }

    private const int CurrentStartupMigrationVersion = 1;

    private async Task RunOneTimeStartupMigrationsAsync(CancellationToken ct)
    {
        var completedCleanly = true;
        try { CompetitiveSync.RestoreLeftovers(); }
        catch (Exception ex) { completedCleanly = false; AppLog.Warn("G-SYNC competitive sync restore failed: " + ex.GetType().Name); }
        if (File.Exists(CompetitiveSync.StatePath)) completedCleanly = false;
        try { GpuProfileService.RetireGsync(); }
        catch (Exception ex) { completedCleanly = false; AppLog.Warn("GPU G-SYNC retire failed: " + ex.GetType().Name); }
        try { LegacyProfileDataCleanup.Run(); }
        catch (Exception ex) { completedCleanly = false; AppLog.Debug("Legacy data cleanup failed: " + ex.GetType().Name); }
        try
        {
            MouseBridgeRetirement.Run(Settings);
            var afterBridge = Settings.Shared;
            if (afterBridge.MouseBridge is not null || afterBridge.MouseBridgeAppAutostart is not null || afterBridge.MouseBridgeAppPath is not null)
                completedCleanly = false;
        }
        catch (Exception ex) { completedCleanly = false; AppLog.Warn("OpenMouse bridge retirement failed: " + ex.GetType().Name); }
        try
        {
            await ExoScheduledTaskCleanup.RemoveLeftoversAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { completedCleanly = false; AppLog.Debug("Deferred state migration failed: " + ex.GetType().Name); }

        if (completedCleanly && !ct.IsCancellationRequested)
            Settings.RecordStartupMigrationVersion(CurrentStartupMigrationVersion);
    }

    private void OnGogOwnedLibraryUpdated() => RefreshOwnedStore(Models.StoreKind.Gog);

    private void RefreshOwnedStore(Models.StoreKind store)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Library.RefreshStoreAsync(store, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { AppLog.Debug($"{store} owned-library cache refresh failed: {ex.Message}"); }
        });
    }

    private void OnEpicPlaytimeUpdated()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Playtime is a derived overlay. Repaint it without re-running
                // any store scan or blocking the startup library response.
                await Library.RefreshDerivedStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Epic playtime derived refresh failed: " + ex.Message);
            }
        });
    }

    private void OnEpicOwnedLibraryUpdated() => RefreshOwnedStore(Models.StoreKind.Epic);

    /// <summary>The Steam website sign-in's token arrived: the Steam library reads its family with it.</summary>
    private void OnSteamWebsiteToken() => RefreshOwnedStore(Models.StoreKind.Steam);

    /// <summary>A Steam client that was still signing in now answers ownership.</summary>
    private void OnSteamClientReady()
    {
        foreach (var steam in Adapters.OfType<SteamAdapter>())
            steam.InvalidateClientOwnership();
        RefreshOwnedStore(Models.StoreKind.Steam);
    }

    private static readonly Lazy<string> s_appVersion = new(ReadAppVersion);

    /// <summary>The running build's version, read once per process.</summary>
    internal static string RunningVersion => s_appVersion.Value;

    public string AppVersion => s_appVersion.Value;

    private static string ReadAppVersion()
    {
        try
        {
            // Prefer VERSION file next to exe / project
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "VERSION"),
                Path.Combine(AppContext.BaseDirectory, "..", "VERSION"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VERSION"),
            };
            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(c);
                    if (File.Exists(full))
                    {
                        var t = File.ReadAllText(full).Trim();
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
                catch { /* */ }
            }

            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    var product = info.ProductVersion?.Trim();
                    if (!string.IsNullOrWhiteSpace(product))
                        return product.Split('+', 2)[0].Split('-', 2)[0].Trim();
                }
            }
            catch { /* fall through */ }

            var v = typeof(AppServices).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "1.0.0"; }
    }

    public IStoreAdapter? FindAdapter(StoreKind store) =>
        Adapters.FirstOrDefault(a => a.Store == store);

    public IStoreAdapter? FindAdapterById(string id) =>
        Adapters.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
