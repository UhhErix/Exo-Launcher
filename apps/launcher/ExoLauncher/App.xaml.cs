using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using UiGuard = ExoLauncher.Helpers.UiGuard;

namespace ExoLauncher;

public partial class App : Application
{
    public static AppServices Services { get; } = new();
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        Services.Initialize();
        Helpers.KeepAwake.SettingOn = () => true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Helpers.KeepAwake.Shared.ReleaseAll();
        UnhandledException += (_, e) =>
        {
            Helpers.PendingReports.Record(e.Exception);
            try
            {
                Helpers.AppLog.Crash("Unhandled UI exception", e.Exception);
            }
            catch { /* best-effort */ }
            // An unhandled UI exception may leave native window state inconsistent.
            // Record it, then let WinUI terminate cleanly instead of claiming every
            // unknown failure was recovered.
        };
        // A faulted task nobody awaited: logged with its type and stack instead of vanishing.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Helpers.PendingReports.Record(e.Exception.GetBaseException());
            try { Helpers.AppLog.Warn("Unobserved task exception: " + e.Exception.GetBaseException()); }
            catch { /* best-effort */ }
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Helpers.PendingReports.Record(e.ExceptionObject as Exception);
            try
            {
                Helpers.AppLog.Crash("Unhandled domain exception", e.ExceptionObject as Exception);
            }
            catch { /* best-effort */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (Helpers.ChromePreview.Wanted)
        {
            Helpers.ChromePreview.Show();
            return;
        }
#endif
        // An update kept for after a game goes in now, while a plain Updating Exo window says so; with a game running (or
        // anything wrong with it) Exo opens as usual. Not when Exo started quiet during a game, never for a test copy.
        if (!Helpers.PackagedSmoke.Enabled && !Helpers.HostMode.IsDisposable && !Helpers.ForegroundWatcher.StartedQuiet &&
            // A kept update for this very version (the update took), or one whose last try rolled back, goes first, silently.
            Services.Updater.SettleKeptUpdate(Services.AppVersion))
        {
            var updating = UpdatingWindow();
            updating.Activate();
            var queue = updating.DispatcherQueue;
            _ = Task.Run(async () =>
            {
                var started = false;
                try { started = await Services.Updater.ApplyKeptAtStartAsync(Services.AppVersion).ConfigureAwait(false); }
                catch (Exception ex) { Helpers.AppLog.Warn("Update at start failed: " + ex.GetType().Name); }
                queue.TryEnqueue(() => UiGuard.Run(() =>
                {
                    if (started)
                    {
                        // What this launch was for (a note, a link) goes to the new build, which Setup opens.
                        UpdateHandOff.Save(UpdateHandOff.FilePath, Program.LaunchArgs, DateTimeOffset.UtcNow);
                        Services.Shutdown();
                        Exit();
                        return;
                    }
                    OpenMain();
                    updating.Close();
                }));
            });
            return;
        }
        ExoLauncher.Services.AutoTuneService.CheckAtStart();
        OpenMain();
    }

    /// <summary>Exo's one plain line while the kept update is checked and its installer starts (Setup's window follows).</summary>
    private static Window UpdatingWindow()
    {
        var window = new Window { Title = "Updating Exo" };
        window.Content = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
            Children =
            {
                new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = "Updating Exo",
                    FontSize = 20,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 240));
        return window;
    }

    private void OpenMain()
    {
        // The launch's one update check, under way before the page asks for it (update on open).
        if (!Helpers.PackagedSmoke.Enabled && !Helpers.HostMode.IsDisposable) _ = Services.Updater.OpenCheck(Services.AppVersion);
        _window = new MainWindow();
        MainAppWindow = _window;
        // EXO_NO_ACTIVATE=1 (a restart done while someone plays a game): the window opens without taking the focus.
        if (Helpers.ForegroundWatcher.StartedQuiet) _window.ShowQuietly();
        else
        {
            Helpers.ForegroundWatcher.Asked("Exo started");
            _window.Activate();
        }
        // Reports a frozen UI (a "Hang" report); started once the window exists, and it never throws.
        Helpers.HangWatchdog.Start(_window);
#if DEBUG
        Helpers.ReportSmokeHooks.AfterLaunch(_window.DispatcherQueue);
#endif
        Program.NotifyWindowReady();
        if (Helpers.PackagedSmoke.Enabled) return;
        // App telemetry: starts the queue and its timers.
        Helpers.Telemetry.Start();
        Helpers.Telemetry.Track("app.start", ("startup_ms", (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds));
        // Crashes recorded by an earlier run are sent now, 15 s after the window, so they never slow the first frame.
        Helpers.PendingReports.SendOnLaunch(TimeSpan.FromSeconds(15));
        // Clean leftover Steam CEF surfaces from older hide/restore (blank taskbar spam).
        // Process and window enumeration stays off the UI thread and after first frame.
        _ = Task.Run(() =>
        {
            try { Adapters.StoreWindowHider.CollapseOrphanSurfaces(); } catch { /* */ }
            ExoLauncher.Services.InstalledAppsEntry.Refresh(ExoLauncher.Services.AppServices.RunningVersion);
        });
        // New DLSS, FSR and XeSS releases, looked for as Exo opens (DlssSwapService.VendorWatch.cs).
        ExoLauncher.Services.DlssSwapService.WatchUpscalerReleases();
    }

    public static MainWindow? MainAppWindow { get; set; }
}
