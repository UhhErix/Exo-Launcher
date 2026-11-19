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
    internal static Action<Exception> Record { get; set; } = PendingReports.Record;

    internal static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { Record(ex); throw; }
    }

    internal static T Run<T>(Func<T> function)
    {
        try { return function(); }
        catch (Exception ex) { Record(ex); throw; }
    }

    internal static async Task RunAsync(Func<Task> function)
    {
        try { await function(); }
        catch (Exception ex) { Record(ex); throw; }
    }

    /// <summary>Posts <paramref name="action"/> to this queue through <see cref="Run"/>. Same result as TryEnqueue.</summary>
    internal static bool Enqueue(this DispatcherQueue queue, Action action) =>
        queue.TryEnqueue(() => Run(action));

    internal static bool Enqueue(this DispatcherQueue queue, DispatcherQueuePriority priority, Action action) =>
        queue.TryEnqueue(priority, () => Run(action));
}
