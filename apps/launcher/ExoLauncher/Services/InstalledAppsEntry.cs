using Microsoft.Win32;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Windows' Installed apps entry for Exo (HKCU Uninstall\ExoLauncher): its version, publisher and links, kept to the
/// running Exo. Setup writes them on every install and update (tools/ExoLauncher.nsi, the same values); a copy put in
/// place another way (a build swapped in by hand) left the entry on an older version and an old publisher, so Exo puts
/// these few values right as it starts. Only the entry Setup made for this copy is touched, never created.
/// </summary>
internal static class InstalledAppsEntry
{
    /// <summary>The publisher every Exo app names: the family, as getexo.app does.</summary>
    internal const string Publisher = "Exo";
    internal const string Web = "https://getexo.app/launcher";
    /// <summary>Exo's help page on getexo.app: where people go for help (Setup writes the same link).</summary>
    internal const string Help = "https://getexo.app/help";
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ExoLauncher";

    internal static void Refresh(string version)
    {
        if (HostMode.IsDisposable) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue("UninstallString") is not string) return;
            // A build run from anywhere but the installed folder leaves the installed Exo's entry alone.
            if (key.GetValue("InstallLocation") is not string where || !SameFolder(where, AppContext.BaseDirectory)) return;
            Set(key, "DisplayVersion", VersionLabel.Of(version));
            Set(key, "Publisher", Publisher);
            Set(key, "URLInfoAbout", Web);
            Set(key, "URLUpdateInfo", Web);
            Set(key, "HelpLink", Help);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    internal static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void Set(RegistryKey key, string name, string value)
    {
        if (!string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal)) key.SetValue(name, value);
    }
}
