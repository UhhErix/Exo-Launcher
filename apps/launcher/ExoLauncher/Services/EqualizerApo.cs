using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>
/// Equalizer APO (Jonas Thedering, GPL-2.0, sourceforge.net/projects/equalizerapo), the audio engine Exo EQ writes its
/// file for. It is a separate program: Exo never ships or changes it. It finds it, reads which outputs Windows runs it on,
/// opens its Device Selector, and on the player's press downloads its official installer from SourceForge, checks it
/// against the size and hashes published for it, and runs it with one permission prompt. Ways it could go wrong: a file
/// that is not the published installer runs as administrator (it must match the size, SourceForge's MD5 and the SHA-256
/// Chocolatey's reviewed package states, two sources); the download follows a redirect off SourceForge; the player says
/// no to Windows' prompt and Exo reports a failure instead; an output where APO was just turned on is shown as playing
/// before the restart APO needs; Windows' "audio enhancements" switched off for the output hides APO without a word.
/// </summary>
internal static class EqualizerApo
{
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";
    private const string Description = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"; // PKEY_Device_DeviceDesc
    private const string Adapter = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";    // PKEY_DeviceInterface_FriendlyName
    private const string NoEffects = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";  // PKEY_AudioEndpoint_Disable_SysFx

    internal const string UpdateCheckerTask = @"\EqualizerAPOUpdateChecker";
    internal const string UpdateCheckerRegKey = @"Software\EqualizerAPO\Update Checker";
    internal const string HideFailureMessageValue = "hideFailureMessage";

    /// <summary>The installer Exo fetches: Equalizer APO 1.4.2 for x64, as SourceForge lists it.</summary>
    internal static readonly Pin Installer = new(
        "1.4.2",
        "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe",
        11_980_366,
        // SourceForge's file list (its RSS of /1.4.2) and Chocolatey's reviewed equalizerapo 1.4.2 package.
        Md5: "410aab9749ae4673b950bc29a4eb226f",
        Sha256: "7403be7427bbe1936a40dded082829b6e217fc4f5990fee5cba501f0ae055afa");

    internal sealed record Pin(string Version, string Address, long Size, string Md5, string Sha256);

    /// <summary>One output Windows has, as APO sees it.</summary>
    /// <param name="Apo">APO is installed on it (its Device Selector put its effect in the output's FxProperties).</param>
    /// <param name="Restart">APO went on it since Windows started: it plays after a restart.</param>
    /// <param name="EffectsOff">Windows' "audio enhancements" are off for it, which keeps APO out.</param>
    internal sealed record Output(string Guid, string Name, bool Active, bool Apo, bool Restart, bool EffectsOff);

    /// <summary>APO's install folder, or null when it is not installed.</summary>
    internal static string? InstallFolder()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            return key?.GetValue("InstallPath") is string { Length: > 0 } folder && File.Exists(Path.Combine(folder, "EqualizerAPO.dll")) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>APO's config folder (config.txt and Exo's exo.txt), or null when it is not installed.</summary>
    internal static string? ConfigFolder()
    {
        if (InstallFolder() is not { } install) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            var folder = key?.GetValue("ConfigPath") as string is { Length: > 0 } configured ? configured : Path.Combine(install, "config");
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    internal static string? Version()
    {
        if (InstallFolder() is not { } folder) return null;
        try { return FileVersionInfo.GetVersionInfo(Path.Combine(folder, "EqualizerAPO.dll")).ProductVersion?.Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"{0.0.0.00000000}.{9709e4ab-…}" to its endpoint id, the part Equalizer APO's Device lines name.</summary>
    internal static string? GuidOf(string? endpointId) =>
        endpointId is null ? null : endpointId[(endpointId.LastIndexOf('.') + 1)..] is { Length: 38 } guid && ExoEqConfig.IsGuid(guid) ? guid.ToLowerInvariant() : null;

    /// <summary>The output Windows plays on now (its endpoint id), or null.</summary>
    internal static string? Playing() => Peripherals.DefaultEndpoints().FirstOrDefault(e => !e.Capture) is { } output ? GuidOf(output.Id) : null;

    /// <summary>One output, read from Windows' list of them; null when Windows has none by that id.</summary>
    internal static Output? Read(string guid)
    {
        try
        {
            using var endpoint = Registry.LocalMachine.OpenSubKey(RenderKey + guid);
            if (endpoint is null) return null;
            using var properties = endpoint.OpenSubKey("Properties");
            using var fx = endpoint.OpenSubKey("FxProperties");
            var name = properties?.GetValue(Description) as string ?? "Output";
            if (properties?.GetValue(Adapter) is string { Length: > 0 } adapter) name = $"{name} ({adapter})";
            var apo = fx is not null && fx.GetValueNames().Any(value => VoiceTune.NamesEqualizerApo(fx.GetValue(value)));
            var effectsOff = (properties?.GetValue(NoEffects) ?? fx?.GetValue(NoEffects)) is int off && off != 0;
            var keyId = guid.ToLowerInvariant();
            var fxWritten = fx is null ? null : Written(fx);
            var started = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var audioStarted = AudioServiceStartedUtc();
            var selectorUtc = Volatile.Read(ref _selectorTriggeredTicks) is > 0 and var t ? new DateTime(t, DateTimeKind.Utc) : (DateTime?)null;
            var settled = _settledApoOutputs.ContainsKey(keyId) && !(selectorUtc is { } sel && fxWritten is { } w && w >= sel);
            var restart = NeedsRestart(apo, fxWritten, started, audioStarted, settled);
            if (apo && !restart) _settledApoOutputs[keyId] = 0;
            return new Output(keyId, name, endpoint.GetValue("DeviceState") is int state && state == 1, apo, restart, effectsOff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _settledApoOutputs = new(StringComparer.OrdinalIgnoreCase);
    private static long _selectorTriggeredTicks;

    /// <summary>
    /// True only when APO was just added and neither Windows nor <c>Audiosrv</c> has restarted since the FxProperties write.
    /// Guards against Windows Fast Startup stale <c>TickCount64</c>, <c>DeviceSelector</c>'s own audio-service restart option,
    /// initial audio-service startup writes (+15 s grace), and post-boot format/spatial writes on already-active APO outputs.
    /// </summary>
    internal static bool NeedsRestart(bool apo, DateTime? fxWrittenUtc, DateTime bootStartedUtc, DateTime? audioServiceStartedUtc, bool alreadySettled = false)
    {
        if (!apo || fxWrittenUtc is not { } written || alreadySettled) return false;
        var reference = audioServiceStartedUtc is { } svc && svc > bootStartedUtc
            ? svc + TimeSpan.FromSeconds(15)
            : bootStartedUtc;
        return written > reference;
    }

    private static DateTime? AudioServiceStartedUtc()
    {
        var scm = OpenSCManagerW(null, null, 0x0001);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenServiceW(scm, "Audiosrv", 0x0004);
            if (svc == IntPtr.Zero) return null;
            try
            {
                if (!QueryServiceStatusEx(svc, 0, out var ssp, Marshal.SizeOf<ServiceStatusProcess>(), out _) || ssp.ProcessId == 0)
                    return null;
                NtQuerySystemInformation(5, IntPtr.Zero, 0, out var len);
                if (len <= 0) len = 256 * 1024;
                var size = len + 65536;
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (NtQuerySystemInformation(5, buf, size, out _) != 0) return null;
                    var cur = buf;
                    while (true)
                    {
                        var next = (uint)Marshal.ReadInt32(cur, 0);
                        var pid = (uint)Marshal.ReadIntPtr(cur, 0x50).ToInt64();
                        if (pid == ssp.ProcessId)
                        {
                            var ft = Marshal.ReadInt64(cur, 0x20);
                            return ft > 0 ? DateTime.FromFileTimeUtc(ft) : null;
                        }
                        if (next == 0) break;
                        cur = IntPtr.Add(cur, (int)next);
                    }
                    return null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll")] private static extern bool QueryServiceStatusEx(IntPtr svc, int infoLevel, out ServiceStatusProcess status, int bufSize, out int bytesNeeded);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

    /// <summary>Whether any output has APO on it.</summary>
    internal static int OutputsWithApo()
    {
        try
        {
            using var render = Registry.LocalMachine.OpenSubKey(RenderKey.TrimEnd('\\'));
            return render?.GetSubKeyNames().Count(guid => Read(guid) is { Apo: true, Active: true }) ?? 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return 0; }
    }

    private static DateTime? Written(RegistryKey key)
    {
        var rc = RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var time);
        return rc == 0 ? DateTime.FromFileTimeUtc(time) : null;
    }

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr className, IntPtr classLength, IntPtr reserved, IntPtr subKeys,
        IntPtr maxSubKey, IntPtr maxClass, IntPtr values, IntPtr maxValueName, IntPtr maxValue, IntPtr security, out long lastWrite);

    /// <summary>APO's Device Selector, where the player turns it on for each output (it asks Windows for permission itself).</summary>
    internal static string? OpenDeviceSelector()
    {
        if (InstallFolder() is not { } folder) return "Equalizer APO isn't installed.";
        var selector = Path.Combine(folder, "DeviceSelector.exe");
        if (!File.Exists(selector)) selector = Path.Combine(folder, "Configurator.exe");
        if (!File.Exists(selector)) return "Equalizer APO's Device Selector is missing. Install Equalizer APO again.";
        try
        {
            Volatile.Write(ref _selectorTriggeredTicks, DateTime.UtcNow.Ticks);
            using var _ = Process.Start(new ProcessStartInfo(selector) { UseShellExecute = true, WorkingDirectory = folder });
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Windows didn't get permission, so the Device Selector didn't open."; }
        catch (Win32Exception ex) { AppLog.Warn("Exo EQ: the Device Selector didn't open: " + ex.NativeErrorCode); return "The Device Selector didn't open. Try again."; }
    }

    /// <summary>Windows' Sound settings (where "audio enhancements" are turned back on for an output).</summary>
    internal static string? OpenSoundSettings()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception) { return "Windows' Sound settings didn't open."; }
    }

    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(3));

    /// <summary>Whether a file is the installer Exo pins: its size, MD5 and SHA-256.</summary>
    internal static bool Matches(Stream file, Pin pin)
    {
        if (file.Length != pin.Size) return false;
        file.Position = 0;
        var sha = Convert.ToHexStringLower(SHA256.HashData(file));
        file.Position = 0;
#pragma warning disable CA5351 // MD5 is the hash SourceForge publishes; SHA-256 is checked too.
        var md5 = Convert.ToHexStringLower(MD5.HashData(file));
#pragma warning restore CA5351
        return sha == pin.Sha256 && md5 == pin.Md5;
    }

    /// <summary>Whether a download that ended up at <paramref name="address"/> came from SourceForge.</summary>
    internal static bool FromSourceForge(Uri? address) =>
        address is { Scheme: "https" } uri && (uri.Host.Equals("sourceforge.net", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".sourceforge.net", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads the pinned installer, checks it, and runs it silently (/S) as administrator: one prompt from Windows. Its
    /// own Device Selector opens when it is done. Null when it installed, else what to tell the player.
    /// </summary>
    internal static async Task<string?> InstallAsync(CancellationToken ct = default)
    {
        if (HostMode.IsDisposable || InstallFolder() is not null) return null;
        var folder = Path.Combine(Path.GetTempPath(), "ExoLauncher-apo-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "EqualizerAPO-x64-" + Installer.Version + ".exe");
        try
        {
            Directory.CreateDirectory(folder);
            using (var response = await Http.GetAsync(Installer.Address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode || !FromSourceForge(response.RequestMessage?.RequestUri))
                    return "SourceForge didn't send Equalizer APO. Try again later.";
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var out_ = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                for (int read; (read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0;)
                {
                    total += read;
                    if (total > Installer.Size) return "The download wasn't Equalizer APO's installer, so Exo didn't run it.";
                    await out_.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await out_.FlushAsync(ct).ConfigureAwait(false);
                if (!Matches(out_, Installer))
                {
                    AppLog.Warn($"Exo EQ: the Equalizer APO download didn't match its pin ({total} bytes).");
                    return "The download wasn't Equalizer APO's installer, so Exo didn't run it.";
                }
            }
            Process? setup;
            try { setup = Process.Start(new ProcessStartInfo(file, "/S") { UseShellExecute = true, Verb = "runas", WorkingDirectory = folder }); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Windows didn't get permission, so Equalizer APO wasn't installed."; }
            if (setup is null) return "Equalizer APO's installer didn't start.";
            using (setup)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromMinutes(10));
                await setup.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                if (setup.ExitCode != 0) { AppLog.Warn("Exo EQ: Equalizer APO's installer ended with " + setup.ExitCode); return "Equalizer APO's installer stopped. Try again."; }
            }
            AppLog.Info("Exo EQ: Equalizer APO " + Installer.Version + " installed.");
            Volatile.Write(ref _selectorTriggeredTicks, DateTime.UtcNow.Ticks);
            if (InstallFolder() is null) return "Equalizer APO's installer finished, but Equalizer APO isn't there. Try again.";
            SilenceUpdateChecker(forceTaskDisable: true);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or Win32Exception)
        {
            AppLog.Warn("Exo EQ: installing Equalizer APO failed: " + ex.GetType().Name);
            return "Equalizer APO couldn't be downloaded. Try again.";
        }
        finally
        {
            try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Silences Equalizer APO's broken Qt6 UpdateChecker.exe popup ("Error while reading response of update check:
    /// illegal value") by setting its HKCU hideFailureMessage flag and disabling the \EqualizerAPOUpdateChecker
    /// scheduled task without requesting UAC. Safe to call both immediately after install and once at startup for
    /// existing Equalizer APO installs.
    /// </summary>
    internal static bool SilenceUpdateChecker(bool forceTaskDisable = false)
    {
        if (HostMode.IsDisposable) return false;
        return SilenceUpdateCheckerCore(
            forceTaskDisable,
            ReadHideFailureFlag,
            WriteHideFailureFlag,
            DisableUpdateCheckerTask);
    }

    internal static bool SilenceUpdateCheckerCore(
        bool forceTaskDisable,
        Func<string?> readFlag,
        Action<string> writeFlag,
        Func<bool> disableTask)
    {
        if (HostMode.IsDisposable) return false;
        var alreadyHidden = string.Equals(readFlag(), "true", StringComparison.OrdinalIgnoreCase);
        if (alreadyHidden && !forceTaskDisable) return false;
        var changed = false;
        if (!alreadyHidden)
        {
            try { writeFlag("true"); changed = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                AppLog.Warn("Exo EQ: couldn't silence Equalizer APO update checker flag: " + ex.GetType().Name);
            }
        }
        if (disableTask()) changed = true;
        return changed;
    }

    private static string? ReadHideFailureFlag()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UpdateCheckerRegKey);
            return key?.GetValue(HideFailureMessageValue)?.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static void WriteHideFailureFlag(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UpdateCheckerRegKey);
        key?.SetValue(HideFailureMessageValue, value, RegistryValueKind.String);
    }

    private static bool DisableUpdateCheckerTask()
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("/change");
            info.ArgumentList.Add("/tn");
            info.ArgumentList.Add(UpdateCheckerTask);
            info.ArgumentList.Add("/disable");
            var result = ProcessOutput.RunCaptureBytes(info, TimeSpan.FromSeconds(5));
            if (result.Completed && result.ExitCode == 0)
            {
                AppLog.Info("Exo EQ: disabled Equalizer APO UpdateChecker scheduled task.");
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            AppLog.Debug("Exo EQ: couldn't disable Equalizer APO UpdateChecker task: " + ex.GetType().Name);
            return false;
        }
    }
}
