using Microsoft.UI.Dispatching;

namespace ExoLauncher.Helpers;

/// <summary>
/// Raw UI-thread callbacks (DispatcherQueue posts, DispatcherQueueTimer ticks, WebView2 events) run through here. A throw
/// is recorded as a pending crash report, then rethrown, so the process ends as it did. A raw DispatcherQueue callback
/// that throws fails fast in WinAppSDK and never reaches Application.UnhandledException, so the record is not left to it.
/// XAML events are not wrapped: they already reach that handler.
/// </summary>
internal static class UiGuard
{
    /// <summary>Where a throw is recorded first. The app keeps <see cref="PendingReports.Record"/>; tests swap it.</summary>
    internal static Action<Exception> Record { get; set; } = ex => CrashBreadcrumb.Record("ui-callback", ex);

    internal static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { SafeRecord(ex); }
    }

    internal static T Run<T>(Func<T> function)
    {
        try { return function(); }
        catch (Exception ex) { SafeRecord(ex); return default!; }
    }

    internal static async Task RunAsync(Func<Task> function, string? context = null)
    {
        try { await function(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SafeRecord(ex, context); }
    }

    /// <summary>Posts <paramref name="action"/> to this queue through <see cref="Run"/>. Same result as TryEnqueue.</summary>
    internal static bool Enqueue(this DispatcherQueue queue, Action action) =>
        queue.TryEnqueue(() => Run(action));

    internal static bool Enqueue(this DispatcherQueue queue, DispatcherQueuePriority priority, Action action) =>
        queue.TryEnqueue(priority, () => Run(action));

    /// <summary>
    /// Posts asynchronous UI work to <paramref name="queue"/> without an <c>async void</c> lambda: exceptions record
    /// into <see cref="PendingReports"/> and log cleanly instead of crashing the dispatcher with an unhandled async void fault.
    /// </summary>
    internal static bool EnqueueAsync(this DispatcherQueue queue, Func<Task> action, string? context = null) =>
        queue.TryEnqueue(() => _ = RunFireAndForgetAsync(action, context));

    private static async Task RunFireAndForgetAsync(Func<Task> action, string? context)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SafeRecord(ex, context);
        }
    }

    private static void SafeRecord(Exception ex, string? context = null)
    {
        try
        {
            if (context is not null)
                AppLog.Warn(context + " failed: " + ex.GetType().Name + ": " + ex.Message);
            Record(ex);
        }
        catch { }
    }
}
