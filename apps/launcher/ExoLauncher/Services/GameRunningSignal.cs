using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Local\ExoLauncher.GameRunning: a manual-reset event, set while any library game runs (one Exo started, or one found
/// running however it was started) and reset when none does, and when Exo starts or closes. An Exo starting during a
/// game reads it to stay out of the way (<see cref="Helpers.ForegroundWatcher"/>), and so do Exo Cinema and tools/lab.
/// It exists only while Exo Launcher runs or a reader holds a handle: once it is gone, opening it fails, which reads as no
/// game; one a reader kept alive is reset when Exo opens it again (<see cref="Open"/>). Updated by the run-state poll,
/// which also holds Wi-Fi in its game mode while a game runs (<see cref="WifiGameMode"/>). A game session Exo
/// still has open, with no game process left, keeps it set only until the session closes: the poll checks again every
/// 15 s while that lasts (<see cref="RunStatePlan"/>) and once more when the session ends.
/// </summary>
internal static class GameRunningSignal
{
    internal const string Name = @"Local\ExoLauncher.GameRunning";

    private static readonly Lazy<EventWaitHandle?> Handle = new(() => Open(Name), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Creates the event, or opens one that outlived an Exo that crashed while a game ran (a reader still held a
    /// handle), and resets it: the constructor's initial state applies only to a new event, so an opened one would
    /// keep "a game runs" until the next game ended.
    /// </summary>
    internal static EventWaitHandle? Open(string name)
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var created);
            if (!created && handle.WaitOne(0)) AppLog.Info("Game running signal: found set from an earlier Exo; cleared.");
            handle.Reset();
            return handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Warn("Game running signal: not created: " + ex.Message);
            return null;
        }
    }

    /// <summary>Exo is closing: the event is reset now, so a reader holding a handle past Exo's exit reads no game.</summary>
    internal static void Clear()
    {
        if (Interlocked.Exchange(ref _state, 0) == 1) AppLog.Info("Game running signal: cleared as Exo closes.");
        if (Handle.IsValueCreated && Handle.Value is { } handle) handle.Reset();
    }

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