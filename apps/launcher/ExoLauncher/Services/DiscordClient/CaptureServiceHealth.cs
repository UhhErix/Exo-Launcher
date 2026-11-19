using System.Security;
using Microsoft.Win32;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Read-only Windows Capture Service registry health check (no capture APIs inside Exo itself).
/// Surfaces disabled CaptureService state so Exo can explain screen compatibility mode calmly.
/// </summary>
internal static class CaptureServiceHealth
{
    private static volatile bool _observedDisabled;

    internal static bool IsDisabledDword(object? value) => value is int n && n == 4;

    internal static void MarkServiceDisabled() => _observedDisabled = true;

    internal static bool IsCaptureServiceDisabled()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return false;
            using (var main = services.OpenSubKey("CaptureService"))
            {
                if (main is not null && IsDisabledDword(main.GetValue("Start"))) return true;
            }
            foreach (var name in services.GetSubKeyNames())
            {
                if (!name.StartsWith("CaptureService_", StringComparison.OrdinalIgnoreCase)) continue;
                using var instance = services.OpenSubKey(name);
                if (instance is not null && IsDisabledDword(instance.GetValue("Start"))) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    internal static (string CaptureMode, bool ServiceDisabled) CurrentStatus()
    {
        var disabled = _observedDisabled || IsCaptureServiceDisabled();
        return (disabled ? "dxgi" : "wgc", disabled);
    }
}
