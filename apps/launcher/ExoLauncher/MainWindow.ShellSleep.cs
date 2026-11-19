using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;

namespace ExoLauncher;

/// <summary>
/// While Exo is hidden or minimized to the tray, the shell's page is suspended after a 30 s grace: its renderer stops
/// running script, timers and frames, and its memory can page out. Voice, music, a capture or recording and a download
/// or install are host work, but a Discord call, music, a recording or a download skip the suspend (<see cref="ShellSleepRules"/>).
/// Showing Exo wakes it at once.
/// </summary>
public sealed partial class MainWindow
{
    // Long enough that a quick alt-tab never pays a suspend and a wake.
    private static readonly TimeSpan ShellSleepDelay = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _shellSleep;
    private bool _shellAsleep;

    private bool ShellMaySleep(bool hiddenOrMinimized) => ShellSleepRules.MaySleep(SleepInputs(hiddenOrMinimized));

    private ShellSleepInputs SleepInputs(bool hiddenOrMinimized) => new(
        HiddenOrMinimized: hiddenOrMinimized,
        Closing: _closingWindow,
        VoiceConnected: Services.DiscordClient.DiscordSession.Current?.InCall == true,
        MusicPlaying: Volatile.Read(ref _musicWebAudio) || _mediaSessions?.Current is { Status: "playing" },
        CaptureRecording: _captureBusy,
        DownloadRunning: App.Services.Launcher.IsBusy || App.Services.Launcher.HasQueuedJobs);

    private string? _lastSleepBlocker = "shown";
    private int _sleepReevaluateQueued;

    private void ApplyShellSleep(bool hiddenOrMinimized)
    {
        var inputs = SleepInputs(hiddenOrMinimized);
        var blocker = ShellSleepRules.Blocker(inputs);
        if (blocker != _lastSleepBlocker)
        {
            if (blocker is "voice call" or "music" or "capture" or "download" || _lastSleepBlocker is "voice call" or "music" or "capture" or "download")
                AppLog.Info($"Shell: {(blocker is null ? "nothing keeps it awake" : "kept awake by " + blocker)}.");
            _lastSleepBlocker = blocker;
        }
        switch (ShellSleepRules.Next(inputs, _shellAsleep || _shellSleep is not null))
        {
            case ShellSleepRules.Step.Wake:
                WakeShell();
                break;
            case ShellSleepRules.Step.StartTimer:
                var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                _shellSleep = cancel;
                _ = SleepShellAsync(cancel);
                break;
        }
    }

    /// <summary>
    /// A call, music, a recording or a download started or ended: the sleep decision runs again now, not at the next
    /// window change. Safe from any thread; one run is queued at a time.
    /// </summary>
    private void ReevaluateShellSleep()
    {
        if (_closingWindow || Interlocked.Exchange(ref _sleepReevaluateQueued, 1) == 1) return;
        DispatcherQueue.TryEnqueue(() => UiGuard.Run(() =>
        {
            Volatile.Write(ref _sleepReevaluateQueued, 0);
            if (!_closingWindow) ApplyShellSleep(WindowIsHiddenOrMinimized());
        }));
    }

    private void AttachShellSleepTriggers()
    {
        Services.DiscordClient.DiscordSession.CallStateChanged += ReevaluateShellSleep;
        App.Services.Launcher.DownloadQueueChanged += ReevaluateShellSleep;
    }

    private async Task SleepShellAsync(CancellationTokenSource cancel)
    {
        var hidPage = false;
        try
        {
            await Task.Delay(ShellSleepDelay, cancel.Token);
            if (!ShellMaySleep(WindowIsHiddenOrMinimized()) || WebHost.CoreWebView2 is not { } core || core.IsDocumentPlayingAudio) return;
            // Suspend is only valid on a hidden controller, so the view collapses first; the controller hears it on a
            // later layout turn, so a suspend in this one is refused (the music view's pattern).
            WebHost.Visibility = Visibility.Collapsed;
            hidPage = true;
            var suspended = false;
            string? failure = null;
            // The controller hears the collapse on a later turn — up to ~200ms while the window is up — and
            // TrySuspendAsync is refused until then, so it is retried across that window.
            var deadline = Environment.TickCount64 + 1600;
            while (!suspended && !cancel.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                await NextLowPriorityTurnAsync();
                try { suspended = await core.TrySuspendAsync(); failure = null; }
                catch (Exception error) { failure = $"{error.GetType().Name} 0x{error.HResult:X8}"; }
                if (!suspended) await Task.Delay(120);
            }
            if (!suspended || cancel.IsCancellationRequested || !ShellMaySleep(WindowIsHiddenOrMinimized()))
            {
                WebHost.Visibility = Visibility.Visible;
                if (core.IsSuspended) core.Resume();
                if (!suspended) AppLog.Info($"Shell: stayed awake while hidden ({failure ?? "declined"}).");
                return;
            }
            _shellAsleep = true;
            AppLog.Info("Shell: asleep while Exo is hidden.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Info($"Shell: sleep failed ({error.GetType().Name} 0x{error.HResult:X8})."); }
        finally
        {
            // A failed or canceled suspend must not leave the controller collapsed. Showing the window can cancel
            // before _shellAsleep is set, and a WebView failure can skip the normal restore path above.
            if (hidPage && !_shellAsleep)
            {
                try
                {
                    WebHost.Visibility = Visibility.Visible;
                    if (WebHost.CoreWebView2 is { IsSuspended: true } core) core.Resume();
                }
                catch (Exception error) { AppLog.Info($"Shell: restore failed ({error.GetType().Name} 0x{error.HResult:X8})."); }
            }
            if (ReferenceEquals(_shellSleep, cancel)) _shellSleep = null;
            cancel.Dispose();
        }
    }

    private void WakeShell()
    {
        var pending = _shellSleep is not null;
        _shellSleep?.Cancel();
        if (!_shellAsleep && !pending) return;
        _shellAsleep = false;
        var started = Stopwatch.GetTimestamp();
        try
        {
            WebHost.Visibility = Visibility.Visible;
            if (WebHost.CoreWebView2 is { IsSuspended: true } core) core.Resume();
        }
        catch { }
        AppLog.Info($"Shell: awake in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
    }
}
