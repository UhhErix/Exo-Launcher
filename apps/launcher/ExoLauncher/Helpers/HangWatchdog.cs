using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ExoLauncher.Helpers;

/// <summary>
/// Reports a frozen UI as a "Hang" report (<see cref="CrashReport.Submit"/>). Two things are watched: Exo's UI thread
/// (a DispatcherQueue timer stamps it every 500 ms; a background thread checks the stamp every second) and the WebView2
/// page (<see cref="PageBeat"/>, one beat a second from ui/src/lib/heartbeat.ts, which arrives through the UI thread).
/// A stall is reported once, after more than five seconds, and re-arms when the stamp is fresh again.
/// <para>
/// A stall counts only while Exo is on screen to the person: a minimized window, a hidden window, or Exo behind another
/// app's window never counts. A fullscreen game is the case that matters most: it takes the front, so Exo is not
/// on screen to the person, and the UI thread of Exo keeps running on its own (a game does not stop it), so a game
/// in front is never a reason to call Exo stuck. Windows exposes no occlusion state short of capturing the screen,
/// which Exo never does, so "in front" (Exo's own process or a window it owns is the foreground) is the check used.
/// </para>
/// </summary>
public static class HangWatchdog
{
    internal static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UiBeatEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);

    private static long _uiBeat;
    private static long _pageBeat;
    // Since this stamp the page owes a first beat: set when the watchdog arms and when a new page is attached.
    private static long _pageOwedSince;
    private static volatile bool _pageVisible;
    private static volatile bool _pageAttached;
    private static DispatcherQueueTimer? _uiTimer;
    private static bool _started;
    private static nint _hwnd;
    // Set while Exo is on screen and in front: the watch thread sleeps on it, so a hidden or covered Exo wakes nothing.
    private static readonly ManualResetEventSlim Armed = new(false);
    internal static bool IsArmed => Armed.IsSet;
    internal static bool PageBeatSeen => Volatile.Read(ref _pageBeat) != 0;

    /// <summary>Starts the watchdog for <paramref name="window"/>. Call once on the UI thread after the window exists. Never throws.</summary>
    public static void Start(Window window)
    {
        try
        {
            if (_started) return;
            _started = true;
            var hwnd = WindowNative.GetWindowHandle(window);
            _hwnd = hwnd;
            _uiTimer = window.DispatcherQueue.CreateTimer();
            _uiTimer.Interval = UiBeatEvery;
            _uiTimer.Tick += (_, _) => UiGuard.Run(() => Volatile.Write(ref _uiBeat, Stopwatch.GetTimestamp()));
            var kind = "Hang";
#if DEBUG
            kind = ReportSmokeHooks.HangKindOverride() ?? kind;
#endif
            var thread = new Thread(() => Watch(hwnd, kind)) { IsBackground = true, Name = "Exo hang watchdog" };
            thread.Start();
            Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hang watchdog not started (" + ex.GetType().Name + ").");
        }
    }

    /// <summary>
    /// Arms the watchdog while Exo is on screen and in front, and stops it otherwise: no UI-thread timer, no watch
    /// thread wake-up and (through the page, heartbeat.ts) no beat while Exo is hidden or covered. Call on the UI
    /// thread whenever the window's visibility or front changes. Coming back starts every clock from zero, so the
    /// time spent hidden is never counted as a stall.
    /// </summary>
    internal static void Refresh()
    {
        if (_hwnd != 0) SetArmed(OnScreen(_hwnd));
    }

    internal static void SetArmed(bool on)
    {
        if (on == Armed.IsSet) return;
        if (on)
        {
            Volatile.Write(ref _uiBeat, Stopwatch.GetTimestamp());
            Volatile.Write(ref _pageBeat, 0);
            Volatile.Write(ref _pageOwedSince, Stopwatch.GetTimestamp());
            try { _uiTimer?.Start(); } catch (Exception ex) { AppLog.Warn("Hang watchdog timer: " + ex.GetType().Name); }
            Armed.Set();
        }
        else
        {
            Armed.Reset();
            try { _uiTimer?.Stop(); } catch (Exception ex) { AppLog.Warn("Hang watchdog timer: " + ex.GetType().Name); }
        }
    }

    /// <summary>The page's heartbeat, from the UI thread. <paramref name="visible"/>: the page is shown to the person.</summary>
    internal static void PageBeat(bool visible)
    {
        _pageVisible = visible;
        Volatile.Write(ref _pageBeat, Stopwatch.GetTimestamp());
    }

    /// <summary>A new page was attached: its beats start from now, so an earlier page's silence is never counted.</summary>
    internal static void PageAttached()
    {
        _pageAttached = true;
        Volatile.Write(ref _pageBeat, 0);
        Volatile.Write(ref _pageOwedSince, Stopwatch.GetTimestamp());
    }

    internal static void PageDetached()
    {
        _pageAttached = false;
        Volatile.Write(ref _pageBeat, 0);
    }

    /// <summary>The page watch's states: no judging, armed and waiting for the first beat, armed and beating, reported (once, until a beat comes back).</summary>
    internal enum PageWatchState { Disarmed, WaitingForFirstBeat, Beating, Reported }

    /// <summary>What one check knows about the page.</summary>
    internal readonly record struct PageWatchInput(bool Armed, bool UiAnswering, bool BeatSinceArm, bool LastBeatVisible, TimeSpan Silent, bool Reported, bool PageAttached = true);

    /// <summary>
    /// The page watch as one table. Rows, in order:
    /// not armed: Disarmed, nothing judged;
    /// armed, UI thread stuck: the page cannot beat through it, so no verdict (the UI report covers it), state kept;
    /// armed, no beat since arming: the page owes one, reported once when none came within the threshold (a page that hung while
    /// hidden is caught here), whatever the page last said about being visible;
    /// armed, beating, last beat said not visible: not judged (its timers are throttled), Beating;
    /// armed, beating, visible, silent past the threshold: reported once;
    /// armed, beating, visible, within the threshold: Beating (a report is re-armed).
    /// </summary>
    internal static (PageWatchState State, bool Report) PageCheck(PageWatchInput i)
    {
        if (!i.Armed || !i.PageAttached) return (PageWatchState.Disarmed, false);
        if (!i.UiAnswering)
            return (i.Reported ? PageWatchState.Reported : i.BeatSinceArm ? PageWatchState.Beating : PageWatchState.WaitingForFirstBeat, false);
        var late = i.Silent > Threshold;
        if (!i.BeatSinceArm)
            return late ? (PageWatchState.Reported, !i.Reported) : (PageWatchState.WaitingForFirstBeat, false);
        if (!i.LastBeatVisible || !late) return (PageWatchState.Beating, false);
        return (PageWatchState.Reported, !i.Reported);
    }

    /// <summary>The decision for one check: a stall longer than <see cref="Threshold"/>, while Exo is on screen, not yet reported.</summary>
    internal static bool ShouldReport(TimeSpan stalled, bool visible, bool reportedThisStall) =>
        visible && !reportedThisStall && stalled > Threshold;

    /// <summary>
    /// One check with the stall's state carried across checks. A stamp that is fresh again re-arms the stall; returns true
    /// when this check reports (and marks the stall as reported).
    /// </summary>
    internal static bool Evaluate(TimeSpan stalled, bool visible, ref bool reportedThisStall)
    {
        if (stalled <= Threshold)
        {
            reportedThisStall = false;
            return false;
        }
        if (!ShouldReport(stalled, visible, reportedThisStall)) return false;
        reportedThisStall = true;
        return true;
    }

    private static void Watch(nint hwnd, string kind)
    {
        var uiReported = false;
        var pageReported = false;
        var uiWasStalled = false;
        while (true)
        {
            try
            {
                Armed.Wait();
                Thread.Sleep(CheckEvery);
                // Disarmed during the wait: the stamps are stale by design, so this check counts for nothing.
                if (!Armed.IsSet) { uiReported = false; pageReported = false; uiWasStalled = false; continue; }
                var now = Stopwatch.GetTimestamp();
                var shown = OnScreen(hwnd);
                var uiStalled = Stopwatch.GetElapsedTime(Volatile.Read(ref _uiBeat), now);
                if (Evaluate(uiStalled, shown, ref uiReported))
                    CrashReport.Submit(kind, $"UI thread stopped responding for {(int)uiStalled.TotalSeconds} s", "ui");

                var uiAnswering = uiStalled <= Threshold;
                if (!uiAnswering) uiWasStalled = true;
                else if (uiWasStalled)
                {
                    uiWasStalled = false;
                    Volatile.Write(ref _pageBeat, 0);
                    Volatile.Write(ref _pageOwedSince, now);
                }

                // The page's beats come through the UI thread: while that thread is stuck they cannot arrive, so the
                // page is judged only while the UI thread answers (a stuck UI thread is reported above instead).
                var pageAt = Volatile.Read(ref _pageBeat);
                var since = pageAt != 0 ? pageAt : Volatile.Read(ref _pageOwedSince);
                var pageStalled = Stopwatch.GetElapsedTime(since, now);
                var (_, reportPage) = PageCheck(new PageWatchInput(shown, uiAnswering, pageAt != 0, _pageVisible, pageStalled, pageReported, _pageAttached));
                pageReported = reportPage || (pageReported && pageStalled > Threshold);
                if (reportPage)
                    CrashReport.Submit(kind, $"WebView page stopped responding for {(int)pageStalled.TotalSeconds} s", "page");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Hang watchdog check failed (" + ex.GetType().Name + ").");
            }
        }
    }

    /// <summary>Exo's window is on screen for the person: shown, not minimized, and Exo (or a window it owns) is in front.</summary>
    private static bool OnScreen(nint hwnd) =>
        Shown(IsWindowVisible(hwnd), IsIconic(hwnd), ForegroundWatcher.ExoInFront(hwnd), SmokeHangWithoutFront);

    /// <summary>
    /// The on-screen decision. A minimized or hidden window never counts. Otherwise Exo must be in front, except for the DEBUG hang
    /// smoke, which opens its window without activating it and so counts anyway.
    /// </summary>
    internal static bool Shown(bool visible, bool minimized, bool inFront, bool smokeHang) =>
        visible && !minimized && (inFront || smokeHang);

#if DEBUG
    /// <summary>The hang smoke (EXO_REPORT_SMOKE=hang) reports without the window being in front.</summary>
    private static bool SmokeHangWithoutFront => ReportSmokeHooks.HangKindOverride() is not null;
#else
    private const bool SmokeHangWithoutFront = false;
#endif

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
}
