using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Local\ExoLauncher.GameRunning: a manual-reset event, set while any library game runs (one Exo started, or one found
/// running however it was started) and reset when none does. An Exo starting during a game reads it to stay out of the
/// way (<see cref="Helpers.ForegroundWatcher"/>), and so does tools/lab, which won't run during a game. It exists only
/// while Exo Launcher runs: once Launcher closes, opening it fails, which reads as no game, so it can't go stale. Updated by the run-state poll,
/// which also holds Wi-Fi in its game mode while a game runs (<see cref="WifiGameMode"/>).
/// </summary>
internal static class GameRunningSignal
{
    internal const string Name = @"Local\ExoLauncher.GameRunning";

    private static readonly Lazy<EventWaitHandle?> Handle = new(() =>
    {
        try { return new EventWaitHandle(false, EventResetMode.ManualReset, Name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Warn("Game running signal: not created: " + ex.Message);
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static int _state = -1;

    /// <summary>What the run-state poll last saw (no game until its first pass): a free read for background work that
    /// stops or slows while a game runs, instead of a process scan of its own.</summary>
    internal static bool Running => Volatile.Read(ref _state) == 1;

    internal static void Update(bool running) => Update(running, WifiGameMode.Hold, LeanSignal.Update, Signal);

    /// <summary>
    /// The state is set first: LeanSignal.Changed's listeners (an update waiting for the game) read <see cref="Running"/>, and
    /// read the old value when it was set after them.
    /// </summary>
    internal static void Update(bool running, Action<bool> wifi, Action<bool> lean, Action<bool> signal)
    {
        var next = running ? 1 : 0;
        var prior = Interlocked.Exchange(ref _state, next);
        wifi(running);
        lean(running);
        if (prior != next) signal(running);
    }

    private static void Signal(bool running)
    {
        if (Handle.Value is not { } handle) return;
        if (running) handle.Set();
        else handle.Reset();
        AppLog.Info($"Game running signal: {(running ? "set" : "cleared")}.");
    }
}