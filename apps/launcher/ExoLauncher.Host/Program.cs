using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

public static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint ErrorModeNoDialogs = 0x0001 | 0x0002 | 0x8000;

    public const string AppUserModelId = "ImAvgErix.ExoLauncher";

    internal static string[] LaunchArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupTiming.Begin();
        SetErrorMode(ErrorModeNoDialogs);
        if (!args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase) && !WindowsRequirement.Supports(Environment.OSVersion.Version.Build))
        {
            WindowsRequirement.Refuse();
            return;
        }
        try
        {
            NativeProcessSecurity.HardenDllSearch();
            ThreadPool.GetMinThreads(out var workers, out var io);
            ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 16));

            if (args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
            {
                ExoUninstall.Run();
                return;
            }
            if (args.Contains(AutoTuneWorker.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = AutoTuneWorker.Run(args);
                return;
            }
            if (args.Contains(LatencyTrace.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = LatencyTrace.Run(args);
                return;
            }
            if (args.Contains(MouseDiagTrace.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = MouseDiagTrace.Run(args);
                return;
            }
            if (args.Contains(WindowsFeatures.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = WindowsFeatures.Run(args);
                return;
            }
            if (args.Contains(StandbyPurger.Flag, StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = StandbyPurger.RunElevatedCli();
                return;
            }
            if (args.Contains("--fetch-components", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = ExoComponents.RunFromSetup(args);
                return;
            }

            var fromSetup = Environment.GetEnvironmentVariable(UpdateHandOff.FromSetupVariable);
            Environment.SetEnvironmentVariable(UpdateHandOff.FromSetupVariable, null);
            Environment.SetEnvironmentVariable("EXO_SILENT_INSTALL", null);
            try { Environment.CurrentDirectory = PathHelper.AppDataDir; } catch { }

            if (RuneHostSession.TryParse(args) is { SpawnUi: false } headlessRune)
            {
                Environment.ExitCode = RuneHostSession.Run(headlessRune);
                return;
            }

            WebViewRuntimeLocator.ClearStaleBrowserFolderPin();
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(5));
            PackagedSmoke.Configure(args);
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            using var instance = Win32SingleInstance.Acquire(PackagedSmoke.InstanceKey);
            var route = UpdateHandOff.RouteOf(!instance.IsPrimary, () => !HostMode.IsDisposable && UpdateHandOff.HoldsBack(
                UpdateHandOff.RunningSetupImages(), UpdateHandOff.EngineRunning(), AppUpdateService.DefaultUpdatesDirectory,
                Environment.GetEnvironmentVariable(UpdateStartupHealth.NonceVariable), fromSetup));

            if (route == UpdateHandOff.Route.ToRunningExo)
            {
                instance.TryForwardActivation(args);
                return;
            }
            if (route == UpdateHandOff.Route.WaitForUpdate)
            {
                UpdateHandOff.Save(UpdateHandOff.FilePath, args, DateTimeOffset.UtcNow);
                AppLog.Info("Startup: Exo is updating; this launch waits for the new version.");
                return;
            }

            args = UpdateHandOff.Take(UpdateHandOff.FilePath, args, DateTimeOffset.UtcNow);
            LaunchArgs = args;
            if (NoteTextFiles.NoteArgument(args) is { } note) WebHostBridge.QueueNoteFromWindows(note);
            if (ExoLinks.Find(args) is { } place) WebHostBridge.QueueOpen(place);

            var runeOptions = RuneHostSession.TryParse(args)
                ?? new RuneHostSession.Options($"exo-launcher-rpc-{Environment.ProcessId}", SpawnUi: true, UiExecutableOverride: Environment.GetEnvironmentVariable("EXO_RUNE_UI_PATH"));

            instance.Activated += forwarded => UiGuard.Run(() =>
            {
                if (NoteTextFiles.NoteArgument(forwarded) is { } opened) WebHostBridge.QueueNoteFromWindows(opened);
                if (ExoLinks.Find(forwarded) is { } linkPlace) WebHostBridge.QueueOpen(linkPlace);
                RuneHostSession.RequestActivate();
            });

            Environment.ExitCode = RuneHostSession.Run(runeOptions);
        }
        catch (Exception ex)
        {
            PendingReports.Record(ex);
            try { AppLog.Crash("Fatal headless host startup exception", ex); } catch { }
            throw;
        }
    }
}
