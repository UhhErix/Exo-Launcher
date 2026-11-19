using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The driver's "NVIDIA Settings" notification-area icon (NVDisplay.Container's session plugin) is removed while Exo
/// runs, since System is where Exo sets the GPU. Nothing polls: it is looked for as Exo starts, when Explorer restarts
/// (TaskbarCreated, which makes the container add it back) and when the container itself restarts (a driver install or
/// Windows Update reloads the display driver: <see cref="DisplayChanged"/>), a few times each since the container may add
/// it late (a sign-in starts both at once). It can't be given back on close (the container runs as SYSTEM, so Exo's
/// TaskbarCreated never reaches it): it returns at the next sign-in or Explorer restart. Removing NVIDIA App and Control
/// Panel in Clean up turns it off for good (NVIDIA's own NvTray StartOnLogin, <see cref="ReplacedApps"/>).
/// </summary>
internal static class NvidiaTrayIcon
{
    private const string Container = "NVDisplay.Container";
    private static readonly TimeSpan[] Looks = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)];
    // A driver reload changes the display a few times and starts the container a moment later.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private static Timer? _watch, _restart;
    private static int _look;
    private static bool _logged;
    // The container's processes in this session at the last look: one that wasn't there before is a restart.
    private static int[] _seen = [];

    /// <summary>Looks now and a few times over the next two minutes; called as Exo starts, after Explorer restarts and after a driver install.</summary>
    internal static void Soon()
    {
        Volatile.Write(ref _look, 0);
        var watch = _watch ??= new Timer(_ => Look());
        try { watch.Change(Looks[0], Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The display changed (Exo's window got WM_DISPLAYCHANGE): once it settles, a container in this session that wasn't
    /// there at the last look means the driver reloaded, so the icon is looked for again and a driver installed outside Exo
    /// is followed up (<see cref="ReplacedApps.AfterDriverChangeAsync"/>). One process list per change; none while a game
    /// runs (a game's mode changes), and the first change after it catches up.
    /// </summary>
    internal static void DisplayChanged()
    {
        if (App.Services.Launcher.AnyGameRunning || GameRunningSignal.Running) return;
        var restart = _restart ??= new Timer(_ => Restarted());
        try { restart.Change(Settle, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    internal static void Stop()
    {
        Interlocked.Exchange(ref _watch, null)?.Dispose();
        Interlocked.Exchange(ref _restart, null)?.Dispose();
    }

    private static void Restarted()
    {
        try
        {
            var pids = Pids();
            var before = Interlocked.Exchange(ref _seen, pids);
            if (pids.Length == 0 || pids.SequenceEqual(before)) return;
            AppLog.Info("NVIDIA's display container restarted: looking for its tray icon again.");
            Soon();
            // The driver reloaded under Exo's page too: it reloads once if its GPU process went.
            MainWindow.ReloadAfterDriverChange();
            _ = ReplacedApps.AfterDriverChangeAsync("after the driver changed", CancellationToken.None);
        }
        catch (Exception ex) { AppLog.Debug("NVIDIA tray icon: " + ex.GetType().Name); }
    }

    private static void Look()
    {
        var removed = Remove();
        var next = Interlocked.Increment(ref _look);
        if (removed || next >= Looks.Length) return;
        try { _watch?.Change(Looks[next] - Looks[next - 1], Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private static bool Remove()
    {
        try
        {
            var pids = Pids();
            Volatile.Write(ref _seen, pids);
            if (pids.Length == 0) return false;
            // Each driver version's container is its own path (its DriverStore folder): an update leaves the old one's entry too.
            var icons = IconExecutables().SelectMany(path => TrayIcons.Read(path)).Distinct().ToList();
            if (icons.Count == 0 || TrayIconRemover.Remove(icons, pids) == 0) return false;
            if (!_logged) { _logged = true; AppLog.Info("NVIDIA tray icon removed while Exo runs."); }
            return true;
        }
        catch (Exception ex) { AppLog.Debug("NVIDIA tray icon: " + ex.GetType().Name); return false; }
    }

    /// <summary>The container's processes in this session, in order.</summary>
    private static int[] Pids()
    {
        using var self = Process.GetCurrentProcess();
        var session = self.SessionId;
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName(Container))
            using (process) if (process.SessionId == session) pids.Add(process.Id);
        pids.Sort();
        return [.. pids];
    }

    /// <summary>The paths Windows recorded the icon under (a known-folder GUID prefix, not a file path).</summary>
    private static List<string> IconExecutables()
    {
        var paths = new List<string>();
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (key is null) return paths;
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub?.GetValue("ExecutablePath") is string path && path.EndsWith(@"\" + Container + ".exe", StringComparison.OrdinalIgnoreCase))
                paths.Add(path);
        }
        return paths;
    }
}
