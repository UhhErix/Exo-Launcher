using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal sealed record NativeApoHealthSnapshot(
    bool HelperAvailable,
    bool SystemEqEnabled,
    bool DrmDisclosureAccepted,
    string? SigningMode,
    bool ProtectedAudioDgDisabled,
    bool EndpointRegistered,
    bool AudiodgRejected,
    bool EffectsOff);

internal sealed record NativeApoHealthStatus(
    bool NativeAvailable,
    bool SystemEqEnabled,
    bool DrmDisclosureAccepted,
    bool ProtectedAudioDgDisabled,
    bool EndpointRegistered,
    bool AudiodgRejected,
    bool NeedsReenable,
    string? ReenableReason);

internal static class ExoNativeApo
{
    internal const string AudioPolicyKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
    internal const string DisableProtectedAudioDgValue = "DisableProtectedAudioDG";
    internal const string ExoApoEfxClsid = "{7E3A8C14-4F2B-4A91-9D5E-2B8C6F1A4E03}";
    internal const string RenderEndpointsKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";
    internal const string CompositeFxEndpointValue = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},15";
    internal const string LegacyFxEndpointValue = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},7";
    internal const string DisableSysFxValue = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    internal const string DrmDisclosureCopy =
        "Some protected audio playback (DRM streams) may not play while system-wide EQ is on. You can turn it off any time.";

    internal static Func<string?, bool> AudiodgRejectionProbe { get; set; } = _ => false;

    internal static string LedgerPath => Path.Combine(PathHelper.AppDataDir, "exo-apo-ledger.json");
    internal static string DllPath => Path.Combine(AppContext.BaseDirectory, "exo_apo.dll");
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, "exo-apo-helper.exe");

    internal static bool IsPackaged() => File.Exists(DllPath) && File.Exists(HelperPath);

    internal static NativeApoHealthStatus EvaluateHealth(NativeApoHealthSnapshot s)
    {
        if (!s.SystemEqEnabled)
        {
            return new NativeApoHealthStatus(
                NativeAvailable: s.HelperAvailable,
                SystemEqEnabled: false,
                DrmDisclosureAccepted: s.DrmDisclosureAccepted,
                ProtectedAudioDgDisabled: s.ProtectedAudioDgDisabled,
                EndpointRegistered: false,
                AudiodgRejected: false,
                NeedsReenable: false,
                ReenableReason: null);
        }

        var requiresUnsignedPolicy = !string.Equals(s.SigningMode, "signed", StringComparison.OrdinalIgnoreCase);
        string? reason = null;
        if (requiresUnsignedPolicy && !s.ProtectedAudioDgDisabled)
            reason = "protected-audiodg-reset";
        else if (s.AudiodgRejected)
            reason = "audiodg-rejected";
        else if (!s.EndpointRegistered)
            reason = "endpoint-reset";
        else if (s.EffectsOff)
            reason = "effects-off";

        return new NativeApoHealthStatus(
            NativeAvailable: s.HelperAvailable,
            SystemEqEnabled: true,
            DrmDisclosureAccepted: s.DrmDisclosureAccepted,
            ProtectedAudioDgDisabled: s.ProtectedAudioDgDisabled,
            EndpointRegistered: s.EndpointRegistered,
            AudiodgRejected: s.AudiodgRejected,
            NeedsReenable: reason is not null,
            ReenableReason: reason);
    }

    internal static NativeApoHealthStatus InspectHealth(string? activeEndpointGuid)
    {
        var (enabled, disclosed, signingMode, ledgerEndpoints) = ReadLedgerState(LedgerPath);
        var protectedDgDisabled = ReadDisableProtectedAudioDg() == 1;
        var targetGuid = activeEndpointGuid ?? ledgerEndpoints.FirstOrDefault();
        var (endpointRegistered, effectsOff) = targetGuid is null
            ? (false, false)
            : ReadEndpointRegistration(targetGuid);
        var audiodgRejected = enabled && AudiodgRejectionProbe(targetGuid);

        return EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: IsPackaged(),
            SystemEqEnabled: enabled,
            DrmDisclosureAccepted: disclosed,
            SigningMode: signingMode,
            ProtectedAudioDgDisabled: protectedDgDisabled,
            EndpointRegistered: endpointRegistered,
            AudiodgRejected: audiodgRejected,
            EffectsOff: effectsOff));
    }

    internal static string BuildEnableArguments(
        string endpointGuid,
        string dllPath,
        string ledgerPath,
        bool drmDisclosureAccepted,
        bool signedMode = false)
    {
        var modeArg = signedMode
            ? "--signed"
            : (drmDisclosureAccepted ? "--drm-disclosure-accepted" : string.Empty);
        return $"register --endpoint \"{endpointGuid}\" --dll \"{dllPath}\" --ledger \"{ledgerPath}\" --explicit-user-consent {modeArg}".Trim();
    }

    internal static string BuildRollbackArguments(string ledgerPath) =>
        $"rollback --ledger \"{ledgerPath}\"";

    internal static async Task<string?> EnableSystemEqAsync(
        string? endpointGuid,
        bool drmDisclosureAccepted,
        bool signedMode = false,
        CancellationToken ct = default)
    {
        if (!signedMode && !drmDisclosureAccepted)
            return "Confirm the protected audio disclosure before enabling system-wide EQ.";
        if (string.IsNullOrWhiteSpace(endpointGuid))
            return "Exo can't tell which output Windows plays on. Plug it in, then try again.";
        if (HostMode.IsDisposable)
            return null;
        if (!IsPackaged())
            return "Exo's audio engine files are missing from this install.";

        var args = BuildEnableArguments(endpointGuid, DllPath, LedgerPath, drmDisclosureAccepted, signedMode);
        return await RunElevatedHelperAsync(args, ct).ConfigureAwait(false);
    }

    internal static Task<string?> ReenableSystemEqAsync(string? endpointGuid, CancellationToken ct = default)
    {
        var (_, disclosed, signingMode, ledgerEndpoints) = ReadLedgerState(LedgerPath);
        var target = endpointGuid ?? ledgerEndpoints.FirstOrDefault();
        var signed = string.Equals(signingMode, "signed", StringComparison.OrdinalIgnoreCase);
        return EnableSystemEqAsync(target, drmDisclosureAccepted: disclosed || !signed, signedMode: signed, ct: ct);
    }

    internal static async Task<string?> DisableSystemEqAsync(CancellationToken ct = default)
    {
        if (HostMode.IsDisposable)
            return null;
        if (!File.Exists(HelperPath))
            return "Exo's audio helper is missing from this install.";

        return await RunElevatedHelperAsync(BuildRollbackArguments(LedgerPath), ct).ConfigureAwait(false);
    }

    private static async Task<string?> RunElevatedHelperAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(HelperPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return "Exo's audio helper didn't start.";
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(45));
            await proc.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return proc.ExitCode == 0 ? null : $"System EQ update stopped (code {proc.ExitCode}). Try again.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows didn't get permission, so system-wide EQ wasn't changed.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or OperationCanceledException)
        {
            AppLog.Warn("Exo Native APO helper failed: " + ex.GetType().Name);
            return "System-wide EQ helper couldn't finish. Try again.";
        }
    }

    private static int? ReadDisableProtectedAudioDg()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AudioPolicyKeyPath);
            return key?.GetValue(DisableProtectedAudioDgValue) is int v ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static (bool Registered, bool EffectsOff) ReadEndpointRegistration(string guid)
    {
        try
        {
            using var endpoint = Registry.LocalMachine.OpenSubKey(RenderEndpointsKeyPath + guid);
            if (endpoint is null) return (false, false);
            using var props = endpoint.OpenSubKey("Properties");
            using var fx = endpoint.OpenSubKey("FxProperties");
            var effectsOff = (props?.GetValue(DisableSysFxValue) ?? fx?.GetValue(DisableSysFxValue)) is int off && off != 0;
            if (fx is null) return (false, effectsOff);
            var registered = false;
            if (fx.GetValue(CompositeFxEndpointValue) is string[] list)
                registered = list.Any(s => string.Equals(s, ExoApoEfxClsid, StringComparison.OrdinalIgnoreCase));
            else if (fx.GetValue(LegacyFxEndpointValue) is string single)
                registered = string.Equals(single, ExoApoEfxClsid, StringComparison.OrdinalIgnoreCase);
            return (registered, effectsOff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, false);
        }
    }

    internal static (bool Enabled, bool Disclosed, string? SigningMode, List<string> Endpoints) ReadLedgerState(string path)
    {
        if (!File.Exists(path)) return (false, false, null, []);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var enabled = root.TryGetProperty("systemEqEnabled", out var en) && en.ValueKind == JsonValueKind.True;
            var disclosed = root.TryGetProperty("drmDisclosureAccepted", out var ds) && ds.ValueKind == JsonValueKind.True;
            var mode = root.TryGetProperty("signingMode", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : null;
            var endpoints = new List<string>();
            if (root.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in eps.EnumerateObject())
                    endpoints.Add(prop.Name);
            }
            return (enabled, disclosed, mode, endpoints);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, false, null, []);
        }
    }
}
