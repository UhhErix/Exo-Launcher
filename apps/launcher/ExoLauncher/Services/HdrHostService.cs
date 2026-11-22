using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal sealed class HdrHostService
{
    private static readonly TimeSpan HdrRecheck = TimeSpan.FromSeconds(5);
    private const long HdrLaunchGraceMs = 120_000;

    private readonly AppServices _services;
    private readonly CancellationToken _lifetime;
    private Timer? _watch;
    private WindowsHdr.Screen? _switchedOn;
    private ushort[]? _ramp;
    private long _releasedAt;
    private int _syncing, _asked, _sdrGammaTries = 6, _started;
    private bool _adopted, _offRefusedLogged;
    private readonly Dictionary<string, long> _launching = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sdrGammaWhyLogged = [];
    private readonly HashSet<string> _whyLogged = [];
    private readonly Dictionary<string, bool> _runs = new(StringComparer.OrdinalIgnoreCase);

    private sealed record HdrWant(string Id, WindowsHdr.GameWindow? Window, string? Remembered);

    internal HdrHostService(AppServices services, CancellationToken lifetime)
    {
        _services = services;
        _lifetime = lifetime;
    }

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _watch = new Timer(_ => Sync(), null, HdrRecheck, Timeout.InfiniteTimeSpan);
        _services.Launcher.GameRunStateChanged += OnRunState;
        _services.Launcher.GameLaunching += OnLaunching;
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        try { _services.Launcher.GameRunStateChanged -= OnRunState; } catch { }
        try { _services.Launcher.GameLaunching -= OnLaunching; } catch { }
        try
        {
            using var done = new ManualResetEvent(false);
            if (_watch?.Dispose(done) == true) done.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch { }
        _watch = null;
        try { Restore(); } catch { }
    }

    internal void SyncSoon()
    {
        Volatile.Write(ref _sdrGammaTries, 6);
        try { _watch?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    internal static bool WantsHdr(string mode, bool? supports) => mode == "on" || (mode == "auto" && supports == true);

    internal static List<string> PendingLaunches(Dictionary<string, long> launching, IEnumerable<string> running, long now, long graceMs)
    {
        lock (launching)
        {
            var seen = running.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, at) in launching.ToList())
                if (seen.Contains(id) || now - at > graceMs) launching.Remove(id);
            return [.. launching.Keys];
        }
    }

    internal static string? SdrGammaReplaced(SettingsService settings, string devicePath) =>
        settings.Shared.SdrGammaBefore.TryGetValue(devicePath, out var name) && name.Length > 0 ? name : null;

    private void OnRunState(string gameId, GameRunState state) => SyncSoon();

    private void OnLaunching(GameEntry game)
    {
        var settings = _services.Settings;
        if (!settings.Shared.HdrForGames) return;
        var mode = settings.HdrGameMode(game.Id);
        if (!WantsHdr(mode, mode == "auto" ? GameHdr.Supports(GameHdr.Judge(game, readFiles: false)) : null)) return;
        lock (_launching) _launching[game.Id] = Environment.TickCount64;
        Sync();
    }

    private void Sync()
    {
        Interlocked.Exchange(ref _asked, 1);
        if (Interlocked.Exchange(ref _syncing, 1) == 1) return;
        var again = false;
        try
        {
            while (Interlocked.Exchange(ref _asked, 0) == 1) again = SyncOnce();
        }
        finally { Interlocked.Exchange(ref _syncing, 0); }
        if (Volatile.Read(ref _asked) == 1) { Sync(); return; }
        try { _watch?.Change(again ? HdrRecheck : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private bool SyncOnce()
    {
        try
        {
            Adopt();
            var gammaPending = !SyncSdrGamma() && Interlocked.Decrement(ref _sdrGammaTries) >= 0;
            var running = MainWindow.RunningLibraryGames().Keys;
            var screens = WindowsHdr.All();
            Learn(running, screens.Any(screen => screen.Enabled));
            if (!_services.Settings.Shared.HdrForGames) { Restore(); return _switchedOn is not null || gammaPending; }
            var pending = PendingLaunches(_launching, running, Environment.TickCount64, HdrLaunchGraceMs);
            var wanted = running.Concat(pending).Distinct(StringComparer.OrdinalIgnoreCase).Where(WantsHdrNow)
                .Select(WantOf).ToList();
            var now = Environment.TickCount64;
            if (wanted.Count > 0) _releasedAt = 0;
            else if (_switchedOn is not null)
            {
                if (_releasedAt == 0) _releasedAt = now;
                if (HdrPlan.RestoreDue(_releasedAt, now)) { _releasedAt = 0; Restore(); }
            }
            if (wanted.Count > 0)
            {
                RememberGameScreens(wanted, screens);
                if (PickTarget(wanted, screens) is { } target) SwitchForGames(target, wanted);
            }
            return running.Count > 0 || pending.Count > 0 || _switchedOn is not null || gammaPending;
        }
        catch (Exception ex) { AppLog.Debug("HDR for games: " + ex.GetType().Name); return _switchedOn is not null; }
    }

    private bool WantsHdrNow(string id)
    {
        var mode = _services.Settings.HdrGameMode(id);
        if (mode != "auto") return WantsHdr(mode, null);
        var verdict = _services.Library.Find(id) is { } game ? GameHdr.Judge(game, readFiles: false) : new GameHdr.Verdict("unknown", null, null);
        if (verdict.From is null && GameWiki.Cached(id) is null) LookUpHdr(id);
        return WantsHdr(mode, GameHdr.Supports(verdict));
    }

    private HdrWant WantOf(string id)
    {
        var window = _services.Library.Find(id) is { } game ? WindowsHdr.WindowOf(_services.Launcher.GameProcesses(game)) : null;
        var remembered = _services.Settings.Shared.HdrGameScreens.GetValueOrDefault(id);
        return new HdrWant(id, window, remembered);
    }

    private static WindowsHdr.Screen? ScreenOfWindow(WindowsHdr.GameWindow? window, IEnumerable<WindowsHdr.Screen> screens) =>
        window is null ? null : screens.FirstOrDefault(screen => string.Equals(screen.GdiName, window.GdiName, StringComparison.OrdinalIgnoreCase));

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

    private void SwitchForGames(WindowsHdr.Screen target, IReadOnlyList<HdrWant> wanted)
    {
        var held = _switchedOn;
        if (held is null && target.Enabled) return;
        if (held is not null && string.Equals(held.DevicePath, target.DevicePath, StringComparison.OrdinalIgnoreCase)) return;
        var window = wanted.Select(want => want.Window).FirstOrDefault(win => win is not null && string.Equals(win.GdiName, target.GdiName, StringComparison.OrdinalIgnoreCase));
        if (!HdrPlan.MaySwitchOn(window is not null, window?.CoversScreen == true))
        {
            if (_whyLogged.Add("fullscreen " + target.DevicePath))
                AppLog.Info($"HDR: a fullscreen game is up on {target.GdiName}; HDR for it waits for its next start.");
            return;
        }
        if (held is not null)
        {
            Restore();
            if (_switchedOn is not null) return;
        }
        if (target.Enabled) return;
        SwitchOn(target);
    }

    private void SwitchOn(WindowsHdr.Screen target)
    {
        var ramp = WindowsHdr.ReadRamp(target.GdiName);
        if (!RememberSwitchedOn(target.DevicePath, HdrPlan.RampText(ramp))) return;
        if (!WindowsHdr.Set(target, true))
        {
            RememberSwitchedOn("", "");
            AppLog.Warn($"HDR: Windows did not turn HDR on for {target.GdiName}.");
            return;
        }
        _switchedOn = target;
        _ramp = ramp;
        var reached = WaitForHdr(target.DevicePath, true);
        AppLog.Info($"HDR on for a game on {target.GdiName}{(reached ? "" : ", Windows has not reported it yet")}.");
        SyncSdrGamma();
    }

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

    private void Adopt()
    {
        if (_adopted) return;
        _adopted = true;
        var settings = _services.Settings.Shared;
        var path = settings.HdrSwitchedOn;
        if (path.Length == 0 || _switchedOn is not null) return;
        var ramp = HdrPlan.RampFrom(settings.HdrSwitchedRamp);
        var screen = WindowsHdr.All().FirstOrDefault(s => string.Equals(s.DevicePath, path, StringComparison.OrdinalIgnoreCase));
        if (screen is { Enabled: true })
        {
            _switchedOn = screen;
            _ramp = ramp;
            AppLog.Info("HDR: still on from the last run's game; Exo switches it back off once no HDR game runs.");
            return;
        }
        if (screen is not null && ramp is not null) WindowsHdr.WriteRamp(screen.GdiName, ramp);
        RememberSwitchedOn("", "");
    }

    private bool RememberSwitchedOn(string devicePath, string ramp)
    {
        try
        {
            _services.Settings.UpdateProfile(settings =>
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

    private void RememberGameScreen(string gameId, string devicePath)
    {
        try { _services.Settings.UpdateProfile(settings => settings.HdrGameScreens[gameId] = devicePath); }
        catch (Exception ex) { AppLog.Debug("HDR: the game's screen was not saved: " + ex.Message); }
    }

    private void Learn(ICollection<string> running, bool screenInHdr)
    {
        List<(string Id, bool SawHdr)> ended;
        lock (_runs)
        {
            foreach (var id in running) _runs[id] = screenInHdr || _runs.GetValueOrDefault(id);
            ended = [.. _runs.Where(run => !running.Contains(run.Key)).Select(run => (run.Key, run.Value))];
            foreach (var (id, _) in ended) _runs.Remove(id);
        }
        foreach (var (id, sawHdr) in ended)
            if (_services.Library.Find(id) is { } game)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _lifetime).ConfigureAwait(false);
                        if (sawHdr) GameHdr.AfterRun(game);
                        GameHdr.Judge(game);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { AppLog.Debug($"HDR: after {game.Title} closed: {ex.GetType().Name}"); }
                });
    }

    private void LookUpHdr(string gameId)
    {
        if (_services.Library.PeekCachedLibrary().FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase)) is not { } game)
            return;
        _ = Task.Run(() => GameWiki.LookupAsync(game, _lifetime));
    }

    private void RememberSdrGammaReplaced(string devicePath, string? previous)
    {
        try { _services.Settings.UpdateProfile(settings => settings.SdrGammaBefore[devicePath] = previous ?? ""); }
        catch (Exception ex) { AppLog.Warn("HDR: the profile SDR gamma replaced was not saved: " + ex.Message); }
    }

    private string? ForgetSdrGammaReplaced(string devicePath)
    {
        var settings = _services.Settings;
        if (!settings.Shared.SdrGammaBefore.ContainsKey(devicePath)) return null;
        var before = SdrGammaReplaced(settings, devicePath);
        try { settings.UpdateProfile(s => s.SdrGammaBefore.Remove(devicePath)); }
        catch (Exception ex) { AppLog.Warn("HDR: " + ex.Message); }
        return before;
    }

    private bool SyncSdrGamma()
    {
        var done = true;
        foreach (var screen in WindowsHdr.All())
        {
            if (screen.Enabled)
            {
                if (SdrInHdr.Apply(screen, RememberSdrGammaReplaced) is not { } why) continue;
                done = false;
                lock (_sdrGammaWhyLogged)
                    if (_sdrGammaWhyLogged.Add(screen.DevicePath + why)) AppLog.Warn($"HDR: SDR gamma 2.2 not on {screen.GdiName} ({screen.Name}): {why}");
            }
            else SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        }
        return done;
    }

    private void Restore()
    {
        if (_switchedOn is not { } held) return;
        var screen = WindowsHdr.All().FirstOrDefault(s => string.Equals(s.DevicePath, held.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            ForgetSdrGammaReplaced(held.DevicePath);
            _switchedOn = null;
            _ramp = null;
            RememberSwitchedOn("", "");
            return;
        }
        SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        if (!WindowsHdr.Set(screen, false))
        {
            if (!_offRefusedLogged) AppLog.Warn("HDR: Windows did not turn HDR back off after the game; Exo tries again.");
            _offRefusedLogged = true;
            return;
        }
        var ramp = _ramp;
        _switchedOn = null;
        _ramp = null;
        _offRefusedLogged = false;
        RememberSwitchedOn("", "");
        if (ramp is not null) WindowsHdr.WriteRamp(screen.GdiName, ramp);
        AppLog.Info($"HDR back off on {screen.GdiName} after the game.");
    }
}
