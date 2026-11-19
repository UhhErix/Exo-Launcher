using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

/// <summary>
/// Exo's own page never stays white or dead. A graphics driver install resets the display adapter, and WebView2's GPU
/// process goes with it (0xC0000409 on this PC's NVIDIA install); its page or browser process can too. Each failure is
/// logged with its kind and exit code and recovered without a restart (<see cref="WebRecovery.For"/>): the GPU process
/// WebView2 starts again by itself, so after a moment the page is asked for two frames and reloaded when none comes; a
/// page process that exited or hangs is reloaded; an exited browser process takes its environment with it, so the
/// WebView2 is made again on a new one. Every recovery is checked the same way once the page is back, and one that still
/// draws nothing is followed by a new WebView2. The page restores its rooms from the host. At most three recoveries a
/// minute (<see cref="RecoveryBudget"/>): past that the driver keeps failing, and Exo says it needs a restart, with Restart.
/// After a driver install Exo ran, the page reloads once before its settings go back (<see cref="ReloadAfterDriverInstallAsync"/>),
/// and after a driver change Exo notices by itself, when the GPU process went since the page loaded (<see cref="ReloadAfterDriverChange"/>).
/// WinUI's own composition needs nothing: WinUI makes its graphics device again after a device-removed and redraws its tree;
/// an app acts only for surfaces of its own (SwapChainPanel, CompositionGraphicsDevice.RenderingDeviceReplaced, Win2D), and
/// Exo has none: its composition is clips on visuals (EmbeddedClientFrame), which hold no device resources.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The time WebView2 takes to start its GPU process again before the page is asked for frames.</summary>
    private static readonly TimeSpan GpuSettle = TimeSpan.FromSeconds(2);
    /// <summary>How long two frames may take: a busy page still draws within it.</summary>
    private static readonly TimeSpan PaintWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PageBackWait = TimeSpan.FromSeconds(15);
    private readonly RecoveryBudget _webRecoveries = new(3, TimeSpan.FromMinutes(1));
    private CoreWebView2Environment? _shellEnvironment;
    private Task _shellBrowserGone = Task.CompletedTask;
    // The page's GPU process exited since the page last loaded.
    private bool _gpuLostSinceLoad;
    private bool _paintCheckRunning, _paintCheckWanted, _verifyAfterReveal, _recreatingWeb;
    private TaskCompletionSource? _pageBack;

    private void OnWebProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = e.ProcessFailedKind;
        var action = WebRecovery.For(kind);
        var what = $"kind={kind} reason={e.Reason} exit={e.ExitCode}";
        var line = "webview-process-failed " + what;
        // Out of memory is usually Windows' commit limit, reached by whatever else runs: say how full it was and who held it.
        if (e.Reason == CoreWebView2ProcessFailedReason.OutOfMemory) line += " " + SystemCommitLine();
        if (action is WebRecovery.Action.Reload or WebRecovery.Action.Recreate) AppLog.Error(line);
        else AppLog.Warn(line);
        // A page or browser process that exited is a crash: one report with the kind and exit code only (never the page address).
        if (kind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
            CrashReport.Submit("WebView2Crash", $"{kind} exit {e.ExitCode}", "webview");
        // Not inside WebView2's own event: a reload or a Close from here re-enters it.
        DispatcherQueue.Enqueue(() =>
        {
            if (_closingWindow || _recreatingWeb) return;
            switch (action)
            {
                case WebRecovery.Action.CheckPaint:
                    _gpuLostSinceLoad = true;
                    // Mid-install the adapter resets again and again; the install's own end reloads the page.
                    if (!DriverInstallSignal.Installing) _ = CheckPaintAsync(what);
                    break;
                case WebRecovery.Action.Reload:
                    RecoverWeb(what, recreate: false);
                    break;
                case WebRecovery.Action.Recreate:
                    RecoverWeb(what, recreate: true, browserGone: true);
                    break;
            }
        });
    }

    /// <summary>One recovery when the minute allows another, else Exo says it needs a restart.</summary>
    private void RecoverWeb(string why, bool recreate, bool browserGone = false)
    {
        if (_closingWindow || _recreatingWeb) return;
        if (!_webRecoveries.TryTake(Environment.TickCount64))
        {
            AppLog.Error($"webview-recovery stopped ({why}): 3 in the last minute; asking for a restart.");
            ShowWebFallback(crashed: true);
            return;
        }
        AppLog.Warn($"webview-recovery {(recreate ? "recreate" : "reload")} ({why}).");
        _verifyAfterReveal = true;
        if (recreate)
        {
            _ = RecreateWebAsync(browserGone);
            return;
        }
        try
        {
            ShowBootPanel("Recovering the Exo window…");
            (WebHost.CoreWebView2 ?? throw new InvalidOperationException("no CoreWebView2")).Reload();
        }
        catch (Exception ex)
        {
            AppLog.Error("webview-reload-failed", ex);
            _ = RecreateWebAsync(browserGone: false);
        }
    }

    /// <summary>
    /// Asks the page for two frames and recovers when none comes in <see cref="PaintWait"/>: after a GPU process exit (a
    /// reload), or once a recovered page is back (a new WebView2). It waits <see cref="GpuSettle"/> first, for WebView2 to
    /// start another GPU process or for a page just shown to know it is. A hidden or minimized window draws nothing: it is
    /// asked again once it shows (<see cref="CheckPaintWhenShown"/>).
    /// </summary>
    private async Task CheckPaintAsync(string why, bool afterRecovery = false)
    {
        _paintCheckWanted = true;
        if (_paintCheckRunning) return;
        _paintCheckRunning = true;
        try
        {
            await Task.Delay(GpuSettle);
            while (_paintCheckWanted && !_closingWindow && !_recreatingWeb)
            {
                if (WindowIsHiddenOrMinimized() || WebHost.CoreWebView2 is not { } core) return;
                _paintCheckWanted = false;
                string? reply = null;
                try { reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", WebRecovery.PaintProbe).AsTask().WaitAsync(PaintWait); }
                catch (Exception ex) { AppLog.Debug("webview-paint-check: " + ex.GetType().Name); }
                if (_closingWindow || _recreatingWeb || !ReferenceEquals(WebHost.CoreWebView2, core)) return;
                var paint = WebRecovery.ReadPaint(reply);
                AppLog.Info($"webview-paint-check ({why}): {paint}.");
                if (paint == WebRecovery.Paint.Hidden) { _paintCheckWanted = true; return; }
                if (paint == WebRecovery.Paint.Stuck)
                {
                    RecoverWeb($"{why}; no frame in {PaintWait.TotalSeconds:0} s", recreate: afterRecovery);
                    return;
                }
            }
        }
        finally { _paintCheckRunning = false; }
    }

    /// <summary>A check the hidden window couldn't make, made once it shows. Any thread.</summary>
    private void CheckPaintWhenShown()
    {
        if (!_paintCheckWanted || _paintCheckRunning) return;
        DispatcherQueue.Enqueue(() => { if (!WindowIsHiddenOrMinimized()) _ = CheckPaintAsync("shown again"); });
    }

    /// <summary>The page loaded and showed again: a recovery's own check, and whoever waits on it.</summary>
    private void OnPageBack()
    {
        Interlocked.Exchange(ref _pageBack, null)?.TrySetResult();
        if (!_verifyAfterReveal) return;
        _verifyAfterReveal = false;
        _ = CheckPaintAsync("after the recovery", afterRecovery: true);
    }

    /// <summary>
    /// A new WebView2 in the old one's place, its page loaded again. <paramref name="browserGone"/>: the browser process
    /// exited, so its environment is dropped (every surface's next one starts a new browser, music's Retry included) and its
    /// processes are given up to 5 s to release the user data folder. Otherwise the environment stays (the music player
    /// keeps its browser) and only the page's controller and its composition are new.
    /// </summary>
    private async Task RecreateWebAsync(bool browserGone)
    {
        if (_recreatingWeb || _closingWindow) return;
        _recreatingWeb = true;
        try
        {
            ShowBootPanel("Recovering the Exo window…");
            if (browserGone)
            {
                WebViewEnvironmentFactory.Reset(_shellEnvironment);
                _shellEnvironment = null;
                _processWatchAttached = false;
                try { await _shellBrowserGone.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { AppLog.Warn("webview-recovery: the old browser's processes outlived 5 s."); }
            }
            if (_closingWindow) return;
            var old = WebHost;
            var fresh = new WebView2
            {
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = old.DefaultBackgroundColor,
            };
            var index = ContentHost.Children.IndexOf(old);
            try { old.Close(); } catch (Exception ex) { AppLog.Debug("webview-recovery: closing the old view: " + ex.GetType().Name); }
            if (index >= 0) ContentHost.Children[index] = fresh;
            else ContentHost.Children.Insert(0, fresh);
            WebHost = fresh;
            _webReady = false;
            _ensureWebTask = null;
            await EnsureWebAsync();
            AppLog.Info($"webview-recovery: a new WebView2 {(_webReady ? "is loading the page" : "didn't start")}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("webview-recreate-failed", ex);
            ShowWebFallback(crashed: true);
        }
        finally { _recreatingWeb = false; }
    }

    /// <summary>
    /// After a graphics driver install ran (NVIDIA's, AMD's or Intel's), before its settings go back: the page reloads once,
    /// whatever it shows, since the install reset the display adapter under it; returns once the page shows again (at most
    /// 15 s). The driver screen comes back from what the page kept. Any thread.
    /// </summary>
    internal static Task ReloadAfterDriverInstallAsync() => OnWindowAsync(window => window.ReloadForDriverAsync("after the driver install", onlyIfGpuLost: false));

    /// <summary>A driver change Exo noticed by itself (NVIDIA's display container restarted): the page reloads once if its GPU process went since it loaded. Any thread.</summary>
    internal static void ReloadAfterDriverChange() =>
        _ = OnWindowAsync(window => window.ReloadForDriverAsync("after the driver changed", onlyIfGpuLost: true));

    /// <summary>Runs on the window's thread; an install waiting on it goes on after 20 s whatever happened (a window closing never runs it).</summary>
    private static Task OnWindowAsync(Func<MainWindow, Task> work)
    {
        if (App.MainAppWindow is not { } window) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.Enqueue(async () =>
            {
                try { await work(window); }
                catch (Exception ex) { AppLog.Warn("webview-reload after a driver: " + ex.Message); }
                finally { done.TrySetResult(); }
            }))
            return Task.CompletedTask;
        return Task.WhenAny(done.Task, Task.Delay(PageBackWait + TimeSpan.FromSeconds(5)));
    }

    private async Task ReloadForDriverAsync(string why, bool onlyIfGpuLost)
    {
        if (_closingWindow || _recreatingWeb || !_webReady || WebHost.CoreWebView2 is not { } core) return;
        if (onlyIfGpuLost && !_gpuLostSinceLoad) return;
        if (DriverInstallSignal.Installing && onlyIfGpuLost) return;
        AppLog.Info($"webview-reload {why}{(_gpuLostSinceLoad ? "; its GPU process exited meanwhile" : "")}.");
        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pageBack = back;
        _verifyAfterReveal = true;
        try { core.Reload(); }
        catch (Exception ex)
        {
            _pageBack = null;
            RecoverWeb($"reload {why} failed: {ex.GetType().Name}", recreate: true);
            return;
        }
        try { await back.Task.WaitAsync(PageBackWait); }
        catch (TimeoutException) { AppLog.Warn($"webview-reload {why}: the page wasn't back in {PageBackWait.TotalSeconds:0} s."); }
    }
}
