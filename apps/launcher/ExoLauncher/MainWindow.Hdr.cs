using ExoLauncher.Helpers;
using ExoLauncher.Services;
namespace ExoLauncher;
/// <summary>
/// System > Graphics > HDR > HDR for HDR games: while a game that outputs real HDR runs (Exo's verdict, <see cref="GameHdr"/>:
/// its own settings, what Exo learned, then PCGamingWiki; or the game's page says Always), however it was started, HDR is on for
/// the screen that game is on: the screen its window is on, else the screen it used last time, else the main screen. Before its
/// window opens the switch is made, and Windows is waited for to report HDR (<see cref="HdrPlan"/>). A switch is never made under a
/// fullscreen game. When the last such game ends, HDR goes back off after a hold of a few seconds (a restart or a launcher
/// reopening the game keeps it on), and the screen's gamma ramp is put back. A game Exo starts that is already known to want HDR
/// gets it just before its store starts it (<see cref="OnHdrLaunching"/>), since some games look for HDR only at their first start.
/// Games without HDR stay SDR. Only HDR Exo itself switched on is ever switched off, and each screen goes back to what it was.
/// While any screen is in HDR (Exo's doing or the user's), SDR on it is drawn with gamma 2.2 (<see cref="SdrInHdr"/>); when HDR
/// goes off the profile the screen had before comes back. The screen Exo switched on, its gamma ramp and each game's screen are
/// kept in settings before the switch: HDR and the ramp still go back after a crash, an update or a power-off.
/// A game that ran while a screen was in HDR and whose own HDR setting reads on once it closed is learned as an HDR game for good
/// (<see cref="GameHdr.AfterRun"/>).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan HdrRecheck = TimeSpan.FromSeconds(5);
    private System.Threading.Timer? _hdrWatch;
    private WindowsHdr.Screen? _hdrSwitchedOn;
    private ushort[]? _hdrRamp;
    // When the last game that wants HDR ended (TickCount64); 0 while one runs or none was switched on.
    private long _hdrReleasedAt;
    private int _hdrSyncing, _hdrAsked, _sdrGammaTries = 6;
    private bool _hdrAdopted;
    // Games Exo is starting that want HDR, by when they were handed to their store: until the process shows up (or this
    // long passes) they count as running, so HDR is already on when it starts. Some games look for HDR only at first start.
    private const long HdrLaunchGraceMs = 120_000;
    private readonly Dictionary<string, long> _hdrLaunching = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sdrGammaWhyLogged = [];
    private readonly HashSet<string> _hdrWhyLogged = [];
    private readonly Dictionary<string, bool> _hdrRuns = new(StringComparer.OrdinalIgnoreCase);
    private SubclassProc? _displayChangeProc;
    // A game that wants HDR now: its window (null before it opens) and the screen Exo remembers it on.
    private sealed record HdrWant(string Id, WindowsHdr.GameWindow? Window, string? Remembered);
    // No polling while nothing runs: a game starting or closing (the bridge's run-state
    // poll reports games started outside Exo too) and a display change wake the check;
    // while a library game runs with HDR for games on, it looks again every 5 s.
    private void StartHdrForGames()
    {
        _hdrWatch = new System.Threading.Timer(_ => SyncHdr(), null, HdrRecheck, Timeout.InfiniteTimeSpan);
        App.Services.Launcher.GameRunStateChanged += OnHdrRunState;
        App.Services.Launcher.GameLaunching += OnHdrLaunching;
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _displayChangeProc = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (message == WmDisplayChange) { SyncHdrSoon(); NvidiaTrayIcon.DisplayChanged(); }
            else if (message == taskbarCreated && taskbarCreated != 0) NvidiaTrayIcon.Soon();
            else if (message == WmNcDestroy) RemoveWindowSubclass(hwnd, _displayChangeProc!, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        try { SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _displayChangeProc, 0x48445220, 0); }
        catch (Exception ex) { AppLog.Debug("HDR: display change hook failed: " + ex.GetType().Name); }
    }
    private void OnHdrRunState(string gameId, GameRunState state) => SyncHdrSoon();
    // Just before the store starts a game that wants HDR (on the launch's own thread, so this finishes first): HDR goes
    // on now, not once the process is up, since a game that checks for HDR only at start would otherwise never offer it.
    // Unknown "auto" games are still caught once they run; only what Exo can tell at once counts here (the game's settings,
    // what it learned, the wiki's kept answer: no program is read).
    private void OnHdrLaunching(Models.GameEntry game)
    {
        var settings = App.Services.Settings;
        if (!settings.Shared.HdrForGames) return;
        var mode = settings.HdrGameMode(game.Id);
        if (!WantsHdr(mode, mode == "auto" ? GameHdr.Supports(GameHdr.Judge(game, readFiles: false)) : null)) return;
        lock (_hdrLaunching) _hdrLaunching[game.Id] = Environment.TickCount64;
        SyncHdr();
    }
    /// <summary>The games Exo started that are not running yet and still inside the grace; one that shows up, or outstays it, is dropped.</summary>
    internal static List<string> PendingHdrLaunches(Dictionary<string, long> launching, IEnumerable<string> running, long now, long graceMs)
    {
        lock (launching)
        {
            var seen = running.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, at) in launching.ToList())
                if (seen.Contains(id) || now - at > graceMs) launching.Remove(id);
            return [.. launching.Keys];
        }
    }
    /// <summary>Runs the HDR check on the pool now: a game started or closed, a setting or the screen changed.</summary>
    internal void SyncHdrSoon()
    {
        // A screen just switched to HDR can still be settling: the profile is tried again for about 30 s.
        Volatile.Write(ref _sdrGammaTries, 6);
        try { _hdrWatch?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }
    private void SyncHdr()
    {
        Interlocked.Exchange(ref _hdrAsked, 1);
        if (Interlocked.Exchange(ref _hdrSyncing, 1) == 1) return;
        var again = false;
        try
        {
            while (Interlocked.Exchange(ref _hdrAsked, 0) == 1) again = SyncHdrOnce();
        }
        finally { Interlocked.Exchange(ref _hdrSyncing, 0); }
        if (Volatile.Read(ref _hdrAsked) == 1) { SyncHdr(); return; }
        try { _hdrWatch?.Change(again ? HdrRecheck : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }
    /// <summary>One look; true while a game runs or a restore is held, so it looks again.</summary>
    private bool SyncHdrOnce()
    {
        try
        {
            AdoptHdr();
            var gammaPending = !SyncSdrGamma() && Interlocked.Decrement(ref _sdrGammaTries) >= 0;
            var running = RunningLibraryGames().Keys;
            var screens = WindowsHdr.All();
            LearnHdr(running, screens.Any(screen => screen.Enabled));
            if (!App.Services.Settings.Shared.HdrForGames) { RestoreHdr(); return _hdrSwitchedOn is not null || gammaPending; }
            var pending = PendingHdrLaunches(_hdrLaunching, running, Environment.TickCount64, HdrLaunchGraceMs);
            var wanted = running.Concat(pending).Distinct(StringComparer.OrdinalIgnoreCase).Where(WantsHdrNow)
                .Select(id => WantOf(id)).ToList();
            var now = Environment.TickCount64;
            if (wanted.Count > 0) _hdrReleasedAt = 0;
            else if (_hdrSwitchedOn is not null)
            {
                // Held: a restart or a launcher reopening the game within the hold keeps HDR on, with no flash.
                if (_hdrReleasedAt == 0) _hdrReleasedAt = now;
                if (HdrPlan.RestoreDue(_hdrReleasedAt, now)) { _hdrReleasedAt = 0; RestoreHdr(); }
            }
            if (wanted.Count > 0)
            {
                RememberGameScreens(wanted, screens);
                if (PickTarget(wanted, screens) is { } target) SwitchForGames(target, wanted);
            }
            return running.Count > 0 || pending.Count > 0 || _hdrSwitchedOn is not null || gammaPending;
        }
        catch (Exception ex) { AppLog.Debug("HDR for games: " + ex.GetType().Name); return _hdrSwitchedOn is not null; }
    }
    private bool WantsHdrNow(string id)
    {
        var mode = App.Services.Settings.HdrGameMode(id);
        if (mode != "auto") return WantsHdr(mode, null);
        // Never the program's read while it runs (done once it has closed, LearnHdr).
        var verdict = App.Services.Library.Find(id) is { } game ? GameHdr.Judge(game, readFiles: false) : new GameHdr.Verdict("unknown", null, null);
        // The game says nothing: ask the wiki now; the next look uses the answer.
        if (verdict.From is null && GameWiki.Cached(id) is null) LookUpHdr(id);
        return WantsHdr(mode, GameHdr.Supports(verdict));
    }
    private static HdrWant WantOf(string id)
    {
        var window = App.Services.Library.Find(id) is { } game ? WindowsHdr.WindowOf(App.Services.Launcher.GameProcesses(game)) : null;
        var remembered = App.Services.Settings.Shared.HdrGameScreens.GetValueOrDefault(id);
        return new HdrWant(id, window, remembered);
    }
    private static WindowsHdr.Screen? ScreenOfWindow(WindowsHdr.GameWindow? window, IEnumerable<WindowsHdr.Screen> screens) =>
        window is null ? null : screens.FirstOrDefault(screen => string.Equals(screen.GdiName, window.GdiName, StringComparison.OrdinalIgnoreCase));
    /// <summary>The screen for the games wanting HDR: a game's window's screen first, then the screen a game used last time, then the main screen.</summary>
    private static WindowsHdr.Screen? PickTarget(IReadOnlyList<HdrWant> wanted, IReadOnlyList<WindowsHdr.Screen> screens)
    {
        var preferred = new List<string?>();
        var screensNow = screens as IReadOnlyList<WindowsHdr.Screen> ?? screens.ToList();
        preferred.AddRange(wanted.Select(want => ScreenOfWindow(want.Window, screensNow)?.DevicePath));
        preferred.AddRange(wanted.Select(want => want.Remembered));
        preferred.Add(WindowsHdr.Primary()?.DevicePath);
        var capable = screensNow.Where(screen => screen.Supported).Select(screen => screen.DevicePath).ToList();
        var path = HdrPlan.PickScreen(preferred, capable);
        return screensNow.FirstOrDefault(screen => string.Equals(screen.DevicePath, path, StringComparison.OrdinalIgnoreCase));
    }
    private void RememberGameScreens(IEnumerable<HdrWant> wanted, IEnumerable<WindowsHdr.Screen> screens)
    {
        foreach (var want in wanted)
            if (ScreenOfWindow(want.Window, screens) is { } screen && !string.Equals(want.Remembered, screen.DevicePath, StringComparison.OrdinalIgnoreCase))
                RememberGameScreen(want.Id, screen.DevicePath);
    }
    /// <summary>
    /// Switches HDR on the target screen when it is not on, or moves Exo's HDR from the screen it was on to the target (the game
    /// moved). The screen the owner had in HDR is never touched, and nothing switches under a fullscreen game.
    /// </summary>
    private void SwitchForGames(WindowsHdr.Screen target, IReadOnlyList<HdrWant> wanted)
    {
        var held = _hdrSwitchedOn;
        if (held is null && target.Enabled) return;
        if (held is not null && string.Equals(held.DevicePath, target.DevicePath, StringComparison.OrdinalIgnoreCase)) return;
        var window = wanted.Select(want => want.Window).FirstOrDefault(win => win is not null && string.Equals(win.GdiName, target.GdiName, StringComparison.OrdinalIgnoreCase));
        if (!HdrPlan.MaySwitchOn(window is not null, window?.CoversScreen == true))
        {
            if (_hdrWhyLogged.Add("fullscreen " + target.DevicePath))
                AppLog.Info($"HDR: a fullscreen game is up on {target.GdiName}; HDR for it waits for its next start.");
            return;
        }
        if (held is not null)
        {
            RestoreHdr();
            if (_hdrSwitchedOn is not null) return;
        }
        if (target.Enabled) return;
        SwitchOn(target);
    }
    private void SwitchOn(WindowsHdr.Screen target)
    {
        var ramp = WindowsHdr.ReadRamp(target.GdiName);
        // Written before the switch: a crash after it leaves Exo's record to undo it, never HDR on without one.
        if (!RememberHdrSwitchedOn(target.DevicePath, HdrPlan.RampText(ramp))) return;
        if (!WindowsHdr.Set(target, true))
        {
            RememberHdrSwitchedOn("", "");
            AppLog.Warn($"HDR: Windows did not turn HDR on for {target.GdiName}.");
            return;
        }
        _hdrSwitchedOn = target;
        _hdrRamp = ramp;
        var reached = WaitForHdr(target.DevicePath, true);
        AppLog.Info($"HDR on for a game on {target.GdiName}{(reached ? "" : ", Windows has not reported it yet")}.");
        SyncSdrGamma();
    }
    /// <summary>Waits for Windows to report the screen in the state asked for, up to <see cref="HdrPlan.SwitchWaitMs"/>.</summary>
    private static bool WaitForHdr(string devicePath, bool wantOn)
    {
        var until = Environment.TickCount64 + HdrPlan.SwitchWaitMs;
        while (true)
        {
            var reported = WindowsHdr.All().FirstOrDefault(screen => string.Equals(screen.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))?.Enabled;
            if (HdrPlan.Reached(wantOn, reported)) return true;
            if (Environment.TickCount64 >= until) return false;
            Thread.Sleep(300);
        }
    }
    // Once a run: a screen Exo switched HDR on for before it last closed, still in HDR, is Exo's to switch back off.
    // A screen already off (a restart, a reboot) gets back the gamma ramp it had, and nothing else is undone.
    private void AdoptHdr()
    {
        if (_hdrAdopted) return;
        _hdrAdopted = true;
        var settings = App.Services.Settings.Shared;
        var path = settings.HdrSwitchedOn;
        if (path.Length == 0 || _hdrSwitchedOn is not null) return;
        var ramp = HdrPlan.RampFrom(settings.HdrSwitchedRamp);
        var screen = WindowsHdr.All().FirstOrDefault(s => string.Equals(s.DevicePath, path, StringComparison.OrdinalIgnoreCase));
        if (screen is { Enabled: true })
        {
            _hdrSwitchedOn = screen;
            _hdrRamp = ramp;
            AppLog.Info("HDR: still on from the last run's game; Exo switches it back off once no HDR game runs.");
            return;
        }
        if (screen is not null && ramp is not null) WindowsHdr.WriteRamp(screen.GdiName, ramp);
        RememberHdrSwitchedOn("", "");
    }
    private static bool RememberHdrSwitchedOn(string devicePath, string ramp)
    {
        try
        {
            App.Services.Settings.UpdateProfile(settings =>
            {
                settings.HdrSwitchedOn = devicePath;
                settings.HdrSwitchedRamp = ramp;
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("HDR: " + ex.Message);
            return false;
        }
    }
    private static void RememberGameScreen(string gameId, string devicePath)
    {
        try { App.Services.Settings.UpdateProfile(settings => settings.HdrGameScreens[gameId] = devicePath); }
        catch (Exception ex) { AppLog.Debug("HDR: the game's screen was not saved: " + ex.Message); }
    }
    /// <summary>
    /// Per running game, whether a screen was in HDR at any look while it ran. Once it has closed (and written its
    /// settings), one that saw HDR may be learned as an HDR game, and its verdict is judged again with its files read.
    /// </summary>
    private void LearnHdr(ICollection<string> running, bool screenInHdr)
    {
        List<(string Id, bool SawHdr)> ended;
        lock (_hdrRuns)
        {
            foreach (var id in running) _hdrRuns[id] = screenInHdr || _hdrRuns.GetValueOrDefault(id);
            ended = [.. _hdrRuns.Where(run => !running.Contains(run.Key)).Select(run => (run.Key, run.Value))];
            foreach (var (id, _) in ended) _hdrRuns.Remove(id);
        }
        foreach (var (id, sawHdr) in ended)
            if (App.Services.Library.Find(id) is { } game)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _lifetimeCts.Token).ConfigureAwait(false);
                        if (sawHdr) GameHdr.AfterRun(game);
                        GameHdr.Judge(game);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { AppLog.Debug($"HDR: after {game.Title} closed: {ex.GetType().Name}"); }
                });
    }
    /// <summary>On for a game set to Always, or on Auto when the game outputs HDR itself.</summary>
    internal static bool WantsHdr(string mode, bool? supports) => mode == "on" || (mode == "auto" && supports == true);
    // A game started before its page was ever opened: ask the wiki now; the next check uses the answer.
    private void LookUpHdr(string gameId)
    {
        if (App.Services.Library.PeekCachedLibrary().FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase)) is not { } game)
            return;
        _ = Task.Run(() => GameWiki.LookupAsync(game, _lifetimeCts.Token));
    }
    /// <summary>
    /// The advanced-color profile a screen had before Exo's SDR gamma 2.2 one (null: none), kept in settings: Exo's
    /// profile outlives a restart while the screen stays in HDR, and the calibration must still come back after.
    /// </summary>
    internal static string? SdrGammaReplaced(SettingsService settings, string devicePath) =>
        settings.Shared.SdrGammaBefore.TryGetValue(devicePath, out var name) && name.Length > 0 ? name : null;
    private static void RememberSdrGammaReplaced(string devicePath, string? previous)
    {
        try { App.Services.Settings.UpdateProfile(settings => settings.SdrGammaBefore[devicePath] = previous ?? ""); }
        catch (Exception ex) { AppLog.Warn("HDR: the profile SDR gamma replaced was not saved: " + ex.Message); }
    }
    private static string? ForgetSdrGammaReplaced(string devicePath)
    {
        var settings = App.Services.Settings;
        if (!settings.Shared.SdrGammaBefore.ContainsKey(devicePath)) return null;
        var before = SdrGammaReplaced(settings, devicePath);
        try { settings.UpdateProfile(s => s.SdrGammaBefore.Remove(devicePath)); }
        catch (Exception ex) { AppLog.Warn("HDR: " + ex.Message); }
        return before;
    }
    /// <summary>Exo's SDR gamma 2.2 profile on every screen in HDR (when the setting is on), off every other; false while a
    /// screen in HDR is still without it.</summary>
    private bool SyncSdrGamma()
    {
        const bool on = true;
        var done = true;
        foreach (var screen in WindowsHdr.All())
        {
            if (on && screen.Enabled)
            {
                if (SdrInHdr.Apply(screen, RememberSdrGammaReplaced) is not { } why) continue;
                done = false;
                // Once per screen and reason, where a friend's log shows it.
                lock (_sdrGammaWhyLogged)
                    if (_sdrGammaWhyLogged.Add(screen.DevicePath + why)) AppLog.Warn($"HDR: SDR gamma 2.2 not on {screen.GdiName} ({screen.Name}): {why}");
            }
            else SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        }
        return done;
    }
    private bool _hdrOffRefusedLogged;
    /// <summary>HDR off on the screen Exo turned it on for, and its gamma ramp back. Windows can refuse while the screen
    /// reconfigures: then Exo still owns it and the next look tries again.</summary>
    private void RestoreHdr()
    {
        if (_hdrSwitchedOn is not { } held) return;
        var screen = WindowsHdr.All().FirstOrDefault(s => string.Equals(s.DevicePath, held.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            // Not connected now: nothing on it to switch off.
            ForgetSdrGammaReplaced(held.DevicePath);
            _hdrSwitchedOn = null;
            _hdrRamp = null;
            RememberHdrSwitchedOn("", "");
            return;
        }
        SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        if (!WindowsHdr.Set(screen, false))
        {
            if (!_hdrOffRefusedLogged) AppLog.Warn("HDR: Windows did not turn HDR back off after the game; Exo tries again.");
            _hdrOffRefusedLogged = true;
            return;
        }
        var ramp = _hdrRamp;
        _hdrSwitchedOn = null;
        _hdrRamp = null;
        _hdrOffRefusedLogged = false;
        RememberHdrSwitchedOn("", "");
        if (ramp is not null) WindowsHdr.WriteRamp(screen.GdiName, ramp);
        AppLog.Info($"HDR back off on {screen.GdiName} after the game.");
    }
    private void StopHdrForGames()
    {
        try { App.Services.Launcher.GameRunStateChanged -= OnHdrRunState; } catch { }
        try { App.Services.Launcher.GameLaunching -= OnHdrLaunching; } catch { }
        // A look still running on the pool could switch HDR on again after the restore below.
        try
        {
            using var done = new ManualResetEvent(false);
            if (_hdrWatch?.Dispose(done) == true) done.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch { }
        try { RestoreHdr(); } catch { }
    }
    private const uint WmDisplayChange = 0x007E, WmNcDestroy = 0x0082;
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint refData);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint refData);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
