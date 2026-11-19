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
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
            if (run is null) return false;
            ApplyTo(run, approved, on, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ExoLauncher.exe"));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Warn("Open Exo when Windows starts: the Run key could not be changed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Windows' own record of whether each Run value may start (what Task Manager's Startup apps switch writes).</summary>
    internal const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// On: the Run value names <paramref name="exePath"/>, and an off left by Task Manager is turned back on, so the switch
    /// really brings startup back. Off: both go.
    /// </summary>
    internal static void ApplyTo(RegistryKey run, RegistryKey? approved, bool on, string exePath)
    {
        var wanted = CommandFor(exePath);
        var now = run.GetValue(ValueName) as string;
        if (on)
        {
            if (!string.Equals(now, wanted, StringComparison.OrdinalIgnoreCase)) run.SetValue(ValueName, wanted, RegistryValueKind.String);
            if (approved?.GetValue(ValueName) is byte[] record && !StartupApps.IsEnabled(record))
                approved.SetValue(ValueName, StartupApps.Approved(enabled: true, DateTime.UtcNow), RegistryValueKind.Binary);
        }
        else
        {
            if (now is not null) run.DeleteValue(ValueName, throwOnMissingValue: false);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
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
        if (StartWithWindows.Apply(on)) return;
        // Windows refused: the switch goes back to what Windows still does, and the player is told why.
        _services.Settings.SetStartWithWindows(!on);
        throw new InvalidOperationException(on
            ? "Windows didn't let Exo add itself to startup. Try again, or turn Exo on in Task Manager › Startup apps."
            : "Windows didn't let Exo take itself out of startup. Try again, or turn Exo off in Task Manager › Startup apps.");
    }
}
