using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// <see cref="SystemTuning"/> on this PC: HKLM read without administrator rights, services' start types from their keys,
/// the performance counter's rate, Memory Integrity from Windows' code integrity options, and the one elevated step
/// (<see cref="ReplacedApps.RemoveAsAdminAsync"/>: one prompt however many lines).
/// </summary>
internal sealed partial class SystemTuning
{
    internal static SystemTuning Shared { get; } = new(new NativeSystem(), Path.Combine(PathHelper.AppDataDir, "exo-system-tuning.json"), () => DateTimeOffset.UtcNow);

    /// <summary><paramref name="root"/> and <paramref name="prefix"/> place the keys it reads: HKLM itself, or a test's own key.</summary>
    internal sealed partial class NativeSystem(RegistryKey? root = null, string prefix = "") : ISystem
    {
        private readonly RegistryKey _root = root ?? Registry.LocalMachine;

        private RegistryKey? Open(string key) => _root.OpenSubKey(prefix.Length == 0 ? key : $@"{prefix}\{key}");

        public long Frequency() => Stopwatch.Frequency;

        public int? Dword(string key, string name)
        {
            try
            {
                using var k = Open(key);
                return k?.GetValue(name) is int value ? value : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        public string[]? Strings(string key, string name)
        {
            try
            {
                using var k = Open(key);
                return k?.GetValue(name) switch { string[] list => list, string one => [one], _ => null };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        public bool Exists(string key)
        {
            try { using var k = Open(key); return k is not null; }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }

        public (int Start, bool Delayed)? Service(string name)
        {
            try
            {
                using var k = Open($@"SYSTEM\CurrentControlSet\Services\{name}");
                if (k?.GetValue("Start") is not int start) return null;
                return (start, k.GetValue("DelayedAutostart") is int delayed && delayed != 0);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        // SystemCodeIntegrityInformation (103): CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED is 0x400.
        private const int CodeIntegrityInformation = 103;
        private const uint HvciEnabled = 0x400;

        [StructLayout(LayoutKind.Sequential)]
        private struct CodeIntegrity { public uint Length; public uint Options; }

        [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, ref CodeIntegrity info, int length, out int returned);

        public bool? IntegrityRunning()
        {
            var info = new CodeIntegrity { Length = (uint)Marshal.SizeOf<CodeIntegrity>() };
            return NtQuerySystemInformation(CodeIntegrityInformation, ref info, (int)info.Length, out _) == 0 ? (info.Options & HvciEnabled) != 0 : null;
        }

        public DateTimeOffset Boot() => InterruptMode.BootTime();

        public Task<bool> Admin(IReadOnlyList<string> lines) => ReplacedApps.RemoveAsAdminAsync(lines, CancellationToken.None);

        public string Answer() => Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.answer");

        public string? ReadAnswer(string answer, bool keep = false)
        {
            try
            {
                if (!File.Exists(answer)) return null;
                var text = File.ReadAllText(answer);
                if (!keep) DeletionGate.DeleteFile(answer);
                return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("System best settings: an answer couldn't be read: " + ex.GetType().Name);
                return null;
            }
        }

        public bool PointStanding => RestorePoint.StandingNow;

        public void PointMade(RestorePoint.Outcome outcome) => RestorePoint.Noted(outcome, DateTime.UtcNow);

        public async Task<LatencyTrace.Answer?> Latency()
        {
            if (HostMode.IsDisposable || Environment.ProcessPath is not { } exo) return null;
            var answer = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.latency.json");
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exo, $"{LatencyTrace.Flag} \"{answer}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                if (process is null) return null;
                using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                return File.Exists(answer) ? System.Text.Json.JsonSerializer.Deserialize<LatencyTrace.Answer>(await File.ReadAllTextAsync(answer).ConfigureAwait(false)) : new(null, "The check didn't finish.");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException or System.Text.Json.JsonException)
            {
                AppLog.Warn("Latency check: " + ex.Message);
                return new(null, "The check didn't finish.");
            }
            finally
            {
                if (File.Exists(answer)) DeletionGate.DeleteFile(answer);
            }
        }

        public bool SupportsUserHardwareToggles => true;

        public IReadOnlyList<string> NvidiaDisplayClassKeys() => DisplayClassKeys("NVIDIA", null, "VEN_10DE");

        public IReadOnlyList<string> AmdDisplayClassKeys() => DisplayClassKeys("AMD", "Advanced Micro Devices", "VEN_1002");

        private IReadOnlyList<string> DisplayClassKeys(string providerA, string? providerB, string vendorToken)
        {
            var found = new List<string>();
            try
            {
                using var root = Open(DisplayClassRoot);
                if (root is null) return found;
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
                    try
                    {
                        using var adapter = root.OpenSubKey(sub);
                        var provider = adapter?.GetValue("ProviderName") as string;
                        var matchId = adapter?.GetValue("MatchingDeviceId") as string;
                        if ((provider is not null && (provider.Contains(providerA, StringComparison.OrdinalIgnoreCase)
                                || (providerB is not null && provider.Contains(providerB, StringComparison.OrdinalIgnoreCase))))
                            || (matchId is not null && matchId.Contains(vendorToken, StringComparison.OrdinalIgnoreCase)))
                            found.Add($@"{DisplayClassRoot}\{sub}");
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            return found;
        }

        public string? StringValue(string key, string name) => HklmValue<string>(key, name);

        private T? HklmValue<T>(string key, string name) where T : class
        {
            try
            {
                using var k = Open(key);
                return k?.GetValue(name) as T;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        private static T? ReadUserKey<T>(string key, string name, Func<object?, T?> convert)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                return convert(k?.GetValue(name));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return default; }
        }

        private static bool WriteUserKey(string key, string name, object? value, RegistryValueKind kind)
        {
            if (HostMode.IsDisposable) return false;
            try
            {
                if (value is null)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
                    k?.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    using var k = Registry.CurrentUser.CreateSubKey(key);
                    k.SetValue(name, value, kind);
                }
                return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }

        public int? UserDword(string key, string name) => ReadUserKey<int?>(key, name, static v => v is int i ? i : null);

        public string? UserString(string key, string name) => ReadUserKey(key, name, static v => v as string);

        public bool SetUserDword(string key, string name, int? value) => WriteUserKey(key, name, value, RegistryValueKind.DWord);

        public bool SetUserString(string key, string name, string? value) => WriteUserKey(key, name, value, RegistryValueKind.String);

        public string? UserDirectXSettings()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(DirectXUserPrefs);
                return k?.GetValue(DirectXGlobalSettings) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        public bool SetUserDirectXSettings(string? value)
        {
            if (HostMode.IsDisposable) return false;
            try
            {
                if (value is null)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(DirectXUserPrefs, writable: true);
                    k?.DeleteValue(DirectXGlobalSettings, throwOnMissingValue: false);
                }
                else
                {
                    using var k = Registry.CurrentUser.CreateSubKey(DirectXUserPrefs);
                    k.SetValue(DirectXGlobalSettings, value, RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }

        private const uint SpiGetMouse = 0x0003;
        private const uint SpiSetMouse = 0x0004;
        private const uint SpiFUpdateIniFileAndSendChange = 0x0003;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, [In, Out] int[] pvParam, uint fWinIni);

        public int[]? MouseParams()
        {
            var values = new int[3];
            return SystemParametersInfo(SpiGetMouse, 0, values, 0) ? values : null;
        }

        public bool SetMouseParams(int[] values)
        {
            if (HostMode.IsDisposable || values.Length != 3) return false;
            return SystemParametersInfo(SpiSetMouse, 0, values, SpiFUpdateIniFileAndSendChange);
        }

        public IReadOnlyList<string> HvciRequiringGames() => FindHvciRequiringGames(App.Services.Library.PeekCachedLibrary());

        public bool SupportsScheduledTasks => true;

        public bool? ScheduledTaskEnabled(string taskPath)
        {
            var found = ScheduledTasks([taskPath]);
            return found.TryGetValue(taskPath, out var enabled) ? enabled : null;
        }

        public IReadOnlyDictionary<string, bool> ScheduledTasks(IEnumerable<string> taskPaths)
        {
            var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            object? service = null;
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type is null) return results;
                service = Activator.CreateInstance(type);
                if (service is null) return results;
                _ = type.InvokeMember("Connect", System.Reflection.BindingFlags.InvokeMethod, null, service, null, System.Globalization.CultureInfo.InvariantCulture);

                var folders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var fullPath in taskPaths)
                {
                    var slash = fullPath.LastIndexOf('\\');
                    if (slash < 0 || slash >= fullPath.Length - 1) continue;
                    var folderPath = slash == 0 ? @"\" : fullPath[..slash];
                    var taskName = fullPath[(slash + 1)..];
                    if (!folders.TryGetValue(folderPath, out var folder))
                    {
                        try
                        {
                            folder = service.GetType().InvokeMember("GetFolder", System.Reflection.BindingFlags.InvokeMethod, null, service, [folderPath], System.Globalization.CultureInfo.InvariantCulture);
                            if (folder is not null) folders[folderPath] = folder;
                        }
                        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or COMException)
                        {
                            continue;
                        }
                    }
                    if (folder is null) continue;
                    object? task = null;
                    try
                    {
                        task = folder.GetType().InvokeMember("GetTask", System.Reflection.BindingFlags.InvokeMethod, null, folder, [taskName], System.Globalization.CultureInfo.InvariantCulture);
                        if (task is not null)
                        {
                            var raw = task.GetType().InvokeMember("Enabled", System.Reflection.BindingFlags.GetProperty, null, task, null, System.Globalization.CultureInfo.InvariantCulture);
                            if (raw is bool enabled) results[fullPath] = enabled;
                        }
                    }
                    catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or COMException)
                    {
                    }
                    finally
                    {
                        if (task is not null && Marshal.IsComObject(task)) Marshal.FinalReleaseComObject(task);
                    }
                }
                foreach (var folder in folders.Values)
                    if (Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
            }
            catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
            finally
            {
                if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
            }
            return results;
        }

        public bool SupportsWinFeatures => true;

        public WindowsFeatures.FeatureSnapshot? WinFeature(uint featureId) => WindowsFeatures.Query(featureId);

        public string? SelfExePath() => Environment.ProcessPath;

        public bool SupportsOsOverhead => true;

        public bool IsLaptop() => PcHealth.ThisPcIsLaptop();

        public bool SupportsDeepPlaybook => true;

        public IReadOnlyList<string> UsbRootHubKeys()
        {
            var found = new List<string>();
            try
            {
                using var usb = Open(UsbEnumRoot);
                if (usb is null) return found;
                foreach (var hub in usb.GetSubKeyNames())
                {
                    if (!hub.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var hubKey = usb.OpenSubKey(hub);
                        if (hubKey is null) continue;
                        foreach (var instance in hubKey.GetSubKeyNames())
                        {
                            var rel = $@"{UsbEnumRoot}\{hub}\{instance}\Device Parameters";
                            if (!UsbRootHubKeyPattern().IsMatch(rel)) continue;
                            using var dp = Open(rel);
                            if (dp is not null) found.Add(rel);
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            return found;
        }

        public long TotalMemoryKb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024L;
    }
}
