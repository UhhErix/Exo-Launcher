using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace ExoLauncher;

public static class Program
{
    private static int _restoreRequested;

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint ErrorModeNoDialogs = 0x0001 | 0x0002 | 0x8000;

    public const string AppUserModelId = "ImAvgErix.ExoLauncher";

    /// <summary>The arguments this launch runs with (its own, or what a launch during an update kept for it).</summary>
    internal static string[] LaunchArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupTiming.Begin();
        SetErrorMode(ErrorModeNoDialogs);
        // Windows 11 only. Uninstall cleanup still runs on an older Windows, so an old copy can be removed.
        if (!args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase) && !WindowsRequirement.Supports(Environment.OSVersion.Version.Build))
        {
            WindowsRequirement.Refuse();
            return;
        }
        try
        {
            Helpers.NativeProcessSecurity.HardenDllSearch();
            // A page opening asks for a dozen driver, disk and network reads at once, and several block their thread on a
            // lock or a driver. Past the processor count the pool adds one thread every half second, which is how a
            // 60 ms read came back in 300-500 ms. Threads are made only when work waits for one, so this costs nothing idle.
            ThreadPool.GetMinThreads(out var workers, out var io);
            ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 16));
            // Exo's uninstaller, before it deletes Exo: what Exo changed goes back, Windows' records of it go (no window).
            if (args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
            {
                Services.ExoUninstall.Run();
                return;
            }
            // Auto tune's test, elevated by the press that started it (no window); Exo reads what it writes.
            if (args.Contains(Services.AutoTuneWorker.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Services.AutoTuneWorker.Run(args);
                return;
            }
            // Best settings › System's latency check, elevated by its press (no window): ten seconds of tracing, one answer file.
            if (args.Contains(Helpers.LatencyTrace.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Helpers.LatencyTrace.Run(args);
                return;
            }
            if (args.Contains(Helpers.MouseDiagTrace.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Helpers.MouseDiagTrace.Run(args);
                return;
            }
            if (args.Contains(Services.WindowsFeatures.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Services.WindowsFeatures.Run(args);
                return;
            }
            if (args.Contains(Services.StandbyPurger.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Services.StandbyPurger.RunElevatedCli();
                return;
            }
            // Exo's setup, once Exo is installed: Exo's parts fetched and verified, with progress for its window (no window here).
            if (args.Contains("--fetch-components", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Services.ExoComponents.RunFromSetup(args);
                return;
            }
#if DEBUG
            // A developer's look at the service windows' chrome (ChromePreview): no hand-off to a running Exo, no main window.
            if (Helpers.ChromePreview.Wanted)
            {
                ComWrappersSupport.InitializeComWrappers();
                XamlCheckProcessRequirements();
                Application.Start(_ =>
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                    new App();
                });
                return;
            }
#endif
            // Exo's own marks on the starts its installer makes; read once, never passed on to a store or a game.
            var fromSetup = Environment.GetEnvironmentVariable(Services.UpdateHandOff.FromSetupVariable);
            Environment.SetEnvironmentVariable(Services.UpdateHandOff.FromSetupVariable, null);
            Environment.SetEnvironmentVariable("EXO_SILENT_INSTALL", null);
            // Every program Exo starts (a store's launcher, a game) inherits this folder.
            // Inside the app folder it would hold it open after Exo exits, and the next
            // update could not swap the folder; Exo's data folder is never replaced.
            try { Environment.CurrentDirectory = Helpers.PathHelper.AppDataDir; } catch { }
            if (Services.RuneHostSession.TryParse(args, onWarning: AppLog.Warn) is { SpawnUi: false } headlessRune)
            {
                ComWrappersSupport.InitializeComWrappers();
                Environment.ExitCode = Services.RuneHostSession.Run(headlessRune);
                return;
            }
            Helpers.WebViewRuntimeLocator.ClearStaleBrowserFolderPin();
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(5));
            PackagedSmoke.Configure(args);

            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            // Custom-main WinUI apps must initialize WinRT wrappers before the
            // first Windows App SDK API, including AppInstance.
            ComWrappersSupport.InitializeComWrappers();
            var currentInstance = AppInstance.GetCurrent();
            var mainInstance = AppInstance.FindOrRegisterForKey(PackagedSmoke.InstanceKey);
            // A running Exo gets this launch, update or not; with none, a launch during one of Exo's own updates (Setup's window
            // shows it and opens the new Exo; the install engine's own mark counts too, in case that window was closed) does not
            // start the build being replaced, and what it was started for waits for the new one (UpdateHandOff).
            var route = Services.UpdateHandOff.RouteOf(!mainInstance.IsCurrent, () => !HostMode.IsDisposable && Services.UpdateHandOff.HoldsBack(
                Services.UpdateHandOff.RunningSetupImages(), Services.UpdateHandOff.EngineRunning(), Services.AppUpdateService.DefaultUpdatesDirectory,
                Environment.GetEnvironmentVariable(UpdateStartupHealth.NonceVariable), fromSetup));
            if (route == Services.UpdateHandOff.Route.ToRunningExo)
            {
                mainInstance
                    .RedirectActivationToAsync(currentInstance.GetActivatedEventArgs())
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return;
            }
            if (route == Services.UpdateHandOff.Route.WaitForUpdate)
            {
                Services.UpdateHandOff.Save(Services.UpdateHandOff.FilePath, args, DateTimeOffset.UtcNow);
                AppLog.Info("Startup: Exo is updating; this launch waits for the new version.");
                return;
            }
            // What a launch during an update kept for this one (a text file, an exolauncher:// link): taken as if passed now.
            args = Services.UpdateHandOff.Take(Services.UpdateHandOff.FilePath, args, DateTimeOffset.UtcNow);
            LaunchArgs = args;
            // A text file Windows opens with Exo (Notes' --note "%1"): in Notes once the page asks, Exo starting with it.
            if (Services.NoteTextFiles.NoteArgument(args) is { } note) Services.WebHostBridge.QueueNoteFromWindows(note);
            // An exolauncher:// link Exo started with: that place, once the page asks.
            if (Services.ExoLinks.Find(args) is { } place) Services.WebHostBridge.QueueOpen(place);
            if (Services.RuneHostSession.TryParse(args, onWarning: AppLog.Warn) is { } runeOptions)
            {
                mainInstance.Activated += (_, _) => UiGuard.Run(Services.RuneHostSession.RequestActivate);
                Environment.ExitCode = Services.RuneHostSession.Run(runeOptions);
                return;
            }
            mainInstance.Activated += (_, activated) => UiGuard.Run(() =>
            {
                // Exo already open: the file opens in Notes now.
                if (activated.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                    && Services.NoteTextFiles.NoteArgument(Services.NoteTextFiles.Split(launch.Arguments)) is { } opened)
                    _ = Task.Run(() => App.MainAppWindow?.OpenNoteFromWindows(opened));
                // Exo already open: an exolauncher:// link opens its place now.
                if (activated.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs link
                    && Services.ExoLinks.Find(Services.NoteTextFiles.Split(link.Arguments)) is { } place)
                    App.MainAppWindow?.OpenLink(place);
                Interlocked.Exchange(ref _restoreRequested, 1);
                TryDeliverRestore();
            });

            StartupTiming.MarkWinUiStart();
            XamlCheckProcessRequirements();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            AppLog.Info("Shutdown: WinUI application loop completed.");
        }
        catch (Exception ex)
        {
            Helpers.PendingReports.Record(ex);
            try
            {
                AppLog.Crash("Fatal startup exception", ex);
            }
            catch { /* best-effort */ }
            throw;
        }
    }

    internal static void NotifyWindowReady()
    {
        StartupTiming.LogWindowReady();
        TryDeliverRestore();
    }

    private static void TryDeliverRestore()
    {
        var window = App.MainAppWindow;
        if (window is null || Volatile.Read(ref _restoreRequested) == 0) return;
        _ = window.DispatcherQueue.Enqueue(static () =>
        {
            var current = App.MainAppWindow;
            if (current is not null && Interlocked.Exchange(ref _restoreRequested, 0) == 1)
                current.RestoreAndActivate("Exo was opened again");
        });
    }
}
