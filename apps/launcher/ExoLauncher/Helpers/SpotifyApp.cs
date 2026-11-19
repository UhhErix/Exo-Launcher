using System.Diagnostics;

namespace ExoLauncher.Helpers;

/// <summary>
/// Spotify's own desktop app (classic or Microsoft Store) answers for the same account as the web player Exo plays
/// through, and a running one takes the Connect device, so the player's play is refused. Exo closes it whenever Exo
/// starts Spotify's player: WM_CLOSE to its windows, then the exact processes that stayed after 3 s are ended (the app
/// may keep a tray process). Every step is logged.
/// </summary>
internal static class SpotifyApp
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

    internal static async Task CloseRunningAsync(CancellationToken cancel)
    {
        try
        {
            var before = Snapshot();
            if (before.Count == 0) return;
            AppLog.Info($"Spotify: the Spotify app is running ({before.Count} process(es)); closing it so Exo's player can load.");
            Adapters.ProcessHelper.TryCloseProcesses("Spotify");
            await Task.Delay(CloseGrace, cancel).ConfigureAwait(false);
            var stays = PidsToEnd(before, Snapshot());
            if (stays.Count == 0)
            {
                AppLog.Info("Spotify: the Spotify app closed on WM_CLOSE.");
                return;
            }
            foreach (var pid in stays)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (ProcessTermination.Kill(process))
                        AppLog.Info($"Spotify: ended pid {pid}; it stayed open 3 s after WM_CLOSE.");
                }
                catch (ArgumentException) { /* ended meanwhile */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("Spotify: could not close the Spotify app: " + ex.Message);
        }
    }

    /// <summary>The processes from the snapshot taken before WM_CLOSE that are still running; a process started after it is not ours to end.</summary>
    internal static IReadOnlyList<int> PidsToEnd(IReadOnlyList<int> beforeClose, IReadOnlyList<int> nowRunning)
    {
        var running = nowRunning.ToHashSet();
        return beforeClose.Where(running.Contains).Distinct().ToArray();
    }

    private static List<int> Snapshot()
    {
        var processes = Process.GetProcessesByName("Spotify");
        try
        {
            var ids = new List<int>();
            foreach (var p in processes)
            {
                try { if (!p.HasExited) ids.Add(p.Id); }
                catch { /* ended meanwhile */ }
            }
            return ids;
        }
        finally { foreach (var p in processes) p.Dispose(); }
    }
}
