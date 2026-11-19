using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Lean while playing: Exo goes as light as it can while a game runs or streaming software is open, and comes back
/// when both are gone. Built in, with no setting: nothing on screen changes, only what Exo costs the game and the
/// encoder. Updated by the 5 s run-state poll, with one process snapshot.
/// </summary>
internal static class LeanSignal
{
    /// <summary>Broadcast and desktop-capture apps, by executable name without ".exe" (a trailing * is a prefix). Game
    /// clip recorders (Medal, Outplayed, ShadowPlay, Exo Capture) record only while a game runs, which is lean already.</summary>
    internal static readonly string[] StreamingApps =
    [
        "obs64", "obs32", "streamlabs*", "xsplit.core", "prismlivestudio", "meldstudio", "twitchstudio", "vmix", "vmix64",
    ];

    private static int _state;

    /// <summary>A game runs or streaming software is open: Exo yields to them.</summary>
    internal static bool Active => Volatile.Read(ref _state) != 0;

    /// <summary>Streaming software is open: personal details stay off Exo's screens, as Discord's Streamer Mode does.</summary>
    internal static bool StreamingOpen => (Volatile.Read(ref _state) & 2) != 0;

    /// <summary>Raised on the poll's thread when <see cref="Active"/> or <see cref="StreamingOpen"/> changes.</summary>
    internal static event Action? Changed;

    internal static void Update(bool gameRunning)
    {
        var streaming = IsStreamingApp(BackgroundTrim.ProcessTable().Select(row => row.Name));
        var next = (gameRunning ? 1 : 0) | (streaming ? 2 : 0);
        var prior = Interlocked.Exchange(ref _state, next);
        if (prior == next) return;
        AppLog.Info($"Lean while playing: {(next == 0 ? "off" : gameRunning && streaming ? "game and streaming" : gameRunning ? "game" : "streaming")}.");
        try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Warn("Lean while playing: a listener failed: " + ex.GetType().Name); }
    }

    internal static bool IsStreamingApp(IEnumerable<string> executableNames)
    {
        foreach (var file in executableNames)
        {
            var name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
            foreach (var app in StreamingApps)
                if (app.EndsWith('*') ? name.StartsWith(app[..^1], StringComparison.OrdinalIgnoreCase)
                    : name.Equals(app, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
