using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Settings › General › Open Exo when Windows starts (owner, 2026-11-19), off until the player turns it on. On: one value
/// under the current user's Run key names Exo's installed program; off: that value goes. Nothing else starts with Windows
/// and nothing is elevated (HKCU). The value is put right again at each start while the switch is on, so an update or a
/// moved install never leaves a stale path, and taken out if the switch is off and the value was left behind.
/// </summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Exo Launcher";

    /// <summary>The Run value for Exo's program, quoted: the installed copy, or this process's own where there is none.</summary>
    internal static string CommandFor(string exePath) => $"\"{exePath}\" --startup";

    /// <summary>Makes the Run value agree with the switch. False when the registry refused (logged).</summary>
    internal static bool Apply(bool on)
    {
        if (HostMode.IsDisposable) return true;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;
            var wanted = CommandFor(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ExoLauncher.exe"));
            var now = key.GetValue(ValueName) as string;
            if (on && !string.Equals(now, wanted, StringComparison.OrdinalIgnoreCase)) key.SetValue(ValueName, wanted, RegistryValueKind.String);
            else if (!on && now is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Warn("Open Exo when Windows starts: the Run key could not be changed: " + ex.Message);
            return false;
        }
    }
}

public sealed partial class WebHostBridge
{
    /// <summary>Settings: startWithWindows; saved, then the Run value follows it.</summary>
    private void ApplyStartWithWindowsSetting(JsonElement p)
    {
        if (!p.TryGetProperty("startWithWindows", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var on = value.GetBoolean();
        _services.Settings.SetStartWithWindows(on);
        StartWithWindows.Apply(on);
    }
}
