using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;

namespace ExoLauncher.Services;

internal sealed partial class RuneHostSession
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int SwRestore = 9;
    private static readonly object ActiveUiLock = new();
    private static Process? _activeUiProcess;

    internal static void RequestActivate()
    {
        Process? ui;
        lock (ActiveUiLock) ui = _activeUiProcess;
        if (ui is null) return;
        try
        {
            if (ui.HasExited) return;
            ui.Refresh();
            var hwnd = ui.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
        catch { }
    }

    internal sealed record Options(string PipeName, bool SpawnUi, string? UiExecutableOverride);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,120}$")]
    private static partial Regex ValidPipeName();

    internal static ProcessStartInfo BuildUiStartInfo(string exe, string pipeName)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = PathHelper.AppDataDir,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        return psi;
    }

    internal static bool ReadUseClassicWebUiPreference()
    {
        try
        {
            var path = PathHelper.SettingsPath;
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("useClassicWebUi", out var prop)
                && prop.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    internal static Options? TryParse(
        string[]? args,
        Func<string, string?>? readEnv = null,
        Func<string, bool>? fileExists = null,
        Action<string>? onWarning = null,
        Func<bool>? readClassicPref = null)
    {
        readEnv ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;
        readClassicPref ??= ReadUseClassicWebUiPreference;
        try
        {
            string? explicitPipe = null;
            var hasExplicitPipeFlag = false;
            string? uiMode = null;
            string? uiExe = null;
            var smokeTest = false;

            if (args is not null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (string.IsNullOrEmpty(arg))
                        continue;

                    if (arg.Equals("--smoke-test", StringComparison.Ordinal))
                    {
                        smokeTest = true;
                    }
                    else if (arg.Equals("--host-pipe", StringComparison.OrdinalIgnoreCase))
                    {
                        hasExplicitPipeFlag = true;
                        explicitPipe = i + 1 < args.Length ? args[++i] : string.Empty;
                    }
                    else if (arg.StartsWith("--host-pipe=", StringComparison.OrdinalIgnoreCase))
                    {
                        hasExplicitPipeFlag = true;
                        explicitPipe = arg["--host-pipe=".Length..];
                    }
                    else if (arg.Equals("--ui", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        uiMode = args[++i];
                    }
                    else if (arg.StartsWith("--ui=", StringComparison.OrdinalIgnoreCase))
                    {
                        uiMode = arg["--ui=".Length..];
                    }
                    else if (arg.Equals("--rune-ui-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        uiExe = args[++i];
                    }
                    else if (arg.StartsWith("--rune-ui-path=", StringComparison.OrdinalIgnoreCase))
                    {
                        uiExe = arg["--rune-ui-path=".Length..];
                    }
                }
            }

            if (hasExplicitPipeFlag)
            {
                if (!IsSafePipeName(explicitPipe))
                {
                    SafeWarn(onWarning, "RuneHostSession: ignoring invalid --host-pipe argument; falling back.");
                    return null;
                }
                return new Options(explicitPipe!, SpawnUi: false, UiExecutableOverride: null);
            }

            if (smokeTest)
                return null;

            uiMode ??= SafeReadEnv(readEnv, "EXO_UI", onWarning);
            if (string.Equals(uiMode, "web", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uiMode, "webview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uiMode, "webview2", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            uiExe ??= SafeReadEnv(readEnv, "EXO_RUNE_UI_PATH", onWarning);
            var explicitRune = string.Equals(uiMode, "rune", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uiMode, "native", StringComparison.OrdinalIgnoreCase);
            if (!explicitRune)
            {
                if (SafeReadClassicPref(readClassicPref, onWarning))
                    return null;
                var bundledUi = Path.Combine(AppContext.BaseDirectory, "exo-ui.exe");
                var hasOverride = !string.IsNullOrWhiteSpace(uiExe) && SafeFileExists(fileExists, uiExe, onWarning);
                if (!hasOverride && !SafeFileExists(fileExists, bundledUi, onWarning))
                    return null;
            }

            return new Options($"exo-launcher-rpc-{Environment.ProcessId}", SpawnUi: true, UiExecutableOverride: uiExe);
        }
        catch (Exception ex)
        {
            SafeWarn(onWarning, $"RuneHostSession: failed to parse launch options ({ex.GetType().Name}); falling back.");
            return null;
        }
    }

    private static string? SafeReadEnv(Func<string, string?> readEnv, string name, Action<string>? onWarning)
    {
        try
        {
            return readEnv(name);
        }
        catch (Exception ex)
        {
            SafeWarn(onWarning, $"RuneHostSession: failed to read {name} ({ex.GetType().Name}); ignoring.");
            return null;
        }
    }

    private static bool SafeFileExists(Func<string, bool> fileExists, string path, Action<string>? onWarning)
    {
        try
        {
            return fileExists(path);
        }
        catch (Exception ex)
        {
            SafeWarn(onWarning, $"RuneHostSession: failed to probe UI executable path ({ex.GetType().Name}); ignoring.");
            return false;
        }
    }

    private static bool SafeReadClassicPref(Func<bool> readClassicPref, Action<string>? onWarning)
    {
        try
        {
            return readClassicPref();
        }
        catch (Exception ex)
        {
            SafeWarn(onWarning, $"RuneHostSession: failed to read classic UI preference ({ex.GetType().Name}); ignoring.");
            return false;
        }
    }

    private static void SafeWarn(Action<string>? onWarning, string message)
    {
        if (onWarning is null) return;
        try { onWarning(message); } catch { }
    }

    internal static bool IsSafePipeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ValidPipeName().IsMatch(name);

    internal static string? ResolveUiExecutable(string? overridePath, string appBaseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var beside = Path.Combine(appBaseDirectory, "exo-ui.exe");
        if (File.Exists(beside)) return beside;

#if DEBUG
        var dir = new DirectoryInfo(appBaseDirectory);
        for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var release = Path.Combine(dir.FullName, "native-ui", "target", "release", "exo-ui.exe");
            if (File.Exists(release)) return release;
            var debug = Path.Combine(dir.FullName, "native-ui", "target", "debug", "exo-ui.exe");
            if (File.Exists(debug)) return debug;
        }
#endif
        return null;
    }

    private static void StartGameServices()
    {
        GameSettings.StartKeeping();
        DriveHeat.Start();
        _ = Task.Run(ExoLinks.Register);
        _ = Task.Run(GpuPreference.WindowedGamesOptimized);
        Achievements.AchievementWatch.Start();
        FpsCounter.Start();
        Achievements.AchievementSource.Start();
    }

    internal static int Run(Options options)
    {
        SystemSpecs.WarmInBackground();
        WebViewRuntimeLocator.ClearStaleBrowserFolderPin();
        var services = App.Services;
        services.Initialize();
        if (!PackagedSmoke.Enabled && !HostMode.IsDisposable && !ForegroundWatcher.StartedQuiet &&
            services.Updater.SettleKeptUpdate(services.AppVersion))
        {
            var started = false;
            try { started = services.Updater.ApplyKeptAtStartAsync(services.AppVersion).GetAwaiter().GetResult(); }
            catch (Exception ex) { AppLog.Warn("Update at start failed: " + ex.GetType().Name); }
            if (started)
            {
                UpdateHandOff.Save(UpdateHandOff.FilePath, Program.LaunchArgs, DateTimeOffset.UtcNow);
                services.Shutdown();
                return 0;
            }
        }
        KeepAwake.SettingOn = () => true;

        var dispatcherController = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = dispatcherController.DispatcherQueue;
        using (var syncReady = new ManualResetEventSlim(false))
        {
            if (queue.TryEnqueue(() => UiGuard.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                syncReady.Set();
            })))
            {
                syncReady.Wait(TimeSpan.FromSeconds(2));
            }
        }
        using var transport = new NamedPipeHostTransport(options.PipeName);
        var bridge = new WebHostBridge(services, queue);
        using var stop = new ManualResetEventSlim(false);
        string? shutdownReason = null;
        void TriggerStop(string reason)
        {
            Interlocked.CompareExchange(ref shutdownReason, reason, null);
            stop.Set();
        }
        bridge.RequestHostShutdown = () => TriggerStop("host shutdown requested");
        bridge.Attach(transport);
        transport.Start();
        services.HdrHost.Start();
        bridge.StartBackgroundMonitors();
        Action<int> onDiscordBadge = count => bridge.PostFriendsBadge(count);
        DiscordClient.DiscordSession.BadgeChanged += onDiscordBadge;
        _ = Task.Run(DiscordClient.DiscordSession.Start);
        AppLog.Info($"RuneHostSession: listening on {transport.FullPipePath} (spawnUi={options.SpawnUi})");

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            TriggerStop("console cancel");
        };
        Console.CancelKeyPress += onCancel;

        Process? uiProcess = null;
        var stderrLines = new Queue<string>(40);
        var stderrLock = new object();
        var jobHandle = IntPtr.Zero;
        try
        {
            if (options.SpawnUi)
            {
                var exe = ResolveUiExecutable(options.UiExecutableOverride, AppContext.BaseDirectory);
                if (exe is null)
                {
                    AppLog.Error("RuneHostSession: exo-ui.exe not found.");
                    return 2;
                }
                var psi = BuildUiStartInfo(exe, options.PipeName);
                uiProcess = Process.Start(psi);
                if (uiProcess is null)
                {
                    AppLog.Error("RuneHostSession: failed to start exo-ui.exe.");
                    return 3;
                }
                uiProcess.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (stderrLock)
                    {
                        if (stderrLines.Count >= 40) stderrLines.Dequeue();
                        stderrLines.Enqueue(e.Data);
                    }
                };
                uiProcess.BeginErrorReadLine();
                jobHandle = BindChildToKillJob(uiProcess);
                lock (ActiveUiLock) _activeUiProcess = uiProcess;
                uiProcess.EnableRaisingEvents = true;
                uiProcess.Exited += (_, _) => TriggerStop("ui process exited");
                if (uiProcess.HasExited) TriggerStop("ui already exited");
                services.StartDeferredServices();
                StartGameServices();
            }

            stop.Wait();
            return 0;
        }
        finally
        {
            lock (ActiveUiLock)
            {
                if (ReferenceEquals(_activeUiProcess, uiProcess)) _activeUiProcess = null;
            }
            Console.CancelKeyPress -= onCancel;
            DiscordClient.DiscordSession.BadgeChanged -= onDiscordBadge;
            try { bridge.Detach(); } catch { }
            try { transport.Detach(); } catch { }
            try { services.MusicEngine.Close(); } catch { }
            try { services.MousePanel.Close(); } catch { }
            if (uiProcess is not null)
            {
                try
                {
                    if (!uiProcess.HasExited) uiProcess.WaitForExit(1500);
                    if (uiProcess.HasExited) uiProcess.WaitForExit(2000);
                }
                catch { }
            }
            string[] capturedStderr;
            lock (stderrLock) capturedStderr = [.. stderrLines];
            var reason = Volatile.Read(ref shutdownReason) ?? "stop";
            var uiExited = false;
            int? uiExitCode = null;
            if (uiProcess is not null)
            {
                try
                {
                    uiExited = uiProcess.HasExited;
                    if (uiExited) uiExitCode = uiProcess.ExitCode;
                }
                catch { }
                uiProcess.Dispose();
            }
            var stderrSummary = capturedStderr.Length > 0
                ? $" stderr=[{string.Join(" | ", capturedStderr)}]"
                : string.Empty;
            if (uiExitCode is int code && code != 0)
            {
                AppLog.WriteImmediate("ERROR", $"RuneHostSession: stopping ({reason}); exo-ui exited with code {code}.{stderrSummary}");
            }
            else
            {
                AppLog.WriteImmediate("INFO", $"RuneHostSession: stopping ({reason}); uiExited={uiExited}{(uiExitCode is int c ? $", uiExitCode={c}" : string.Empty)}.{stderrSummary}");
            }
            if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
            try { services.Shutdown(); } catch { }
            try { KeepAwake.Shared.ReleaseAll(); } catch { }
            try { dispatcherController.ShutdownQueueAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static IntPtr BindChildToKillJob(Process process)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, (uint)size)
            || !AssignProcessToJobObject(job, process.Handle))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        uint jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
