using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// A graphics driver install Exo runs (<see cref="NvidiaDriverService"/>, <see cref="GpuDriverUpdate"/>), for the page and
/// Exo's own background work. gpu.driverInstall (<see cref="Changed"/>) carries each step: waiting (a game, a call or an Exo
/// Shaders compile holds it), installing, reloading (the new driver loads), restoring (<see cref="DriverRestore"/> puts back
/// what the install reset), then done or failed.
/// </summary>
internal static class DriverInstallSignal
{
    /// <param name="Phase">waiting, installing, reloading, restoring, done or failed.</param>
    /// <param name="Restored">Done: what Exo put back, each a few words ("RTX Video").</param>
    /// <param name="NotRestored">Done: what it couldn't, each a sentence that says what to do.</param>
    internal sealed record Step(string Phase, string? Version, string? Message, IReadOnlyList<string>? Restored = null, IReadOnlyList<string>? NotRestored = null);

    private static int _installing;

    /// <summary>Each step, for the page's driver screen.</summary>
    internal static event Action<Step>? Changed;

    /// <summary>The installer, or the restoring after it, runs now.</summary>
    internal static bool Installing => Volatile.Read(ref _installing) == 1;

    internal static void Report(Step step)
    {
        if (step.Phase is "installing" or "reloading" or "restoring") Volatile.Write(ref _installing, 1);
        else if (step.Phase is "done" or "failed") Volatile.Write(ref _installing, 0);
        AppLog.Info($"Driver install: {step.Phase}{(step.Message is { } message ? ": " + message : "")}.");
        try { Changed?.Invoke(step); }
        catch (Exception ex) { AppLog.Debug("Driver install step not posted: " + ex.Message); }
    }
}
