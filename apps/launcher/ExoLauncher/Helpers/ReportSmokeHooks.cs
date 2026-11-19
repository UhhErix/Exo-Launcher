#if DEBUG
using Microsoft.UI.Dispatching;

namespace ExoLauncher.Helpers;

/// <summary>
/// Test-only hooks, compiled into DEBUG builds only (absent from release). EXO_REPORT_SMOKE=hang blocks the UI thread
/// for seven seconds once, shortly after startup, so the hang watchdog must send a "SmokeTestHang" report.
/// EXO_REPORT_SMOKE=crash throws a <see cref="SmokeTestCrashException"/> on the UI thread shortly after startup, so the
/// raw DispatcherQueue callback (posted through UiGuard.Enqueue) records a pending report (errorType
/// SmokeTestCrashException) that the next start sends. Without UiGuard that throw fails fast and records nothing.
/// Nothing outside this file's #if references them except the two lines that call them (HangWatchdog.Start,
/// App.OnLaunched), which are also DEBUG-only.
/// </summary>
internal sealed class SmokeTestCrashException : Exception
{
    public SmokeTestCrashException(string message) : base(message) { }
}

internal static class ReportSmokeHooks
{
    private const string EnvName = "EXO_REPORT_SMOKE";
    private const string HangKind = "SmokeTestHang";
    private static DispatcherQueueTimer? _hangTimer;

    private static string Mode() => (Environment.GetEnvironmentVariable(EnvName) ?? "").Trim().ToLowerInvariant();

    /// <summary>The kind the watchdog reports under instead of "Hang", or null when no hang smoke is asked for.</summary>
    internal static string? HangKindOverride() => Mode() == "hang" ? HangKind : null;

    /// <summary>True when EXO_REPORT_SMOKE is set to any non-empty value: a disposable host may then send reports (the live smoke).</summary>
    internal static bool AllowSends => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvName));

    /// <summary>Starts the smoke for <paramref name="queue"/>'s thread (the UI thread). Never throws.</summary>
    internal static void AfterLaunch(DispatcherQueue? queue)
    {
        try
        {
            var mode = Mode();
            if (mode == "hang" && queue is not null)
            {
                _hangTimer = queue.CreateTimer();
                _hangTimer.Interval = TimeSpan.FromSeconds(3);
                _hangTimer.IsRepeating = false;
                _hangTimer.Tick += (_, _) => UiGuard.Run(() =>
                {
                    _hangTimer?.Stop();
                    AppLog.Warn("Smoke: blocking the UI thread for 7 s.");
                    Thread.Sleep(TimeSpan.FromSeconds(7));
                });
                _hangTimer.Start();
            }
            else if (mode == "crash" && queue is not null)
            {
                // A raw DispatcherQueue callback posted through UiGuard.Enqueue: the throw is recorded, then rethrown.
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => queue.Enqueue(() =>
                {
                    AppLog.Info("Smoke: throwing the smoke crash on the UI thread now.");
                    throw new SmokeTestCrashException("Smoke test crash");
                }));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Report smoke not started (" + ex.GetType().Name + ").");
        }
    }
}
#endif
