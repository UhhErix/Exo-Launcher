using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Ui;

/// <summary>
/// Same scheme gate as the leftover React <c>shell.openUrl</c> path.
/// Native Settings / Discover / keys must not Process.Start an arbitrary URI.
/// </summary>
internal static class ExoUrlLaunch
{
    internal const string MicrosoftStoreHomeUri = "ms-windows-store://home";
    internal const string MicrosoftStoreAppsFolderArg = @"shell:AppsFolder\Microsoft.WindowsStore_8wekyb3d8bbwe!App";

    internal sealed record UrlLaunchOutcome(bool Ok, string? Message = null);

    internal interface IUrlLaunchOps
    {
        void StartShell(string fileName, string? arguments = null);
        bool IsMicrosoftStoreInstalled();
    }

    internal static readonly IUrlLaunchOps LiveOps = new WindowsUrlLaunchOps();

    private sealed class WindowsUrlLaunchOps : IUrlLaunchOps
    {
        public void StartShell(string fileName, string? arguments = null)
        {
            var psi = arguments is null
                ? new ProcessStartInfo(fileName) { UseShellExecute = true }
                : new ProcessStartInfo(fileName, arguments) { UseShellExecute = true };
            using var _ = Process.Start(psi);
        }

        public bool IsMicrosoftStoreInstalled() => ExoUrlLaunch.IsMicrosoftStoreInstalled();
    }

    public static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase)) return IsPlainSteam(uri);
        // PC › Needs you opens Windows Update; no other Settings page is reachable from a link.
        if (uri.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase))
            return uri.OriginalString.Equals("ms-settings:windowsupdate", StringComparison.OrdinalIgnoreCase);
        if (!uri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Host.Equals("home", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath is "" or "/"
                && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment)
                && string.IsNullOrEmpty(uri.UserInfo)
                && uri.IsDefaultPort;
        return uri.Host is "pdp" or "search";
    }

    /// <summary>Normalizes <c>ms-windows-store://home</c> without a trailing slash so Windows ShellExecute activates Store home reliably.</summary>
    internal static string LaunchUri(Uri uri) =>
        uri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("home", StringComparison.OrdinalIgnoreCase)
            ? MicrosoftStoreHomeUri
            : uri.AbsoluteUri;

    internal static bool IsMicrosoftStoreInstalled()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return packages?.GetSubKeyNames()
                .Any(name => name.StartsWith("Microsoft.WindowsStore_", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch { return false; }
    }

    internal static UrlLaunchOutcome OpenStoreUri(Uri uri, IUrlLaunchOps? ops = null)
    {
        ops ??= LiveOps;
        var target = LaunchUri(uri);
        try
        {
            ops.StartShell(target);
            return new(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not open Microsoft Store ({target}): {ex.GetType().Name}: {ex.Message}");
            if (!ops.IsMicrosoftStoreInstalled())
                return new(false, "Microsoft Store is not installed on this PC.");
            try
            {
                ops.StartShell("explorer.exe", MicrosoftStoreAppsFolderArg);
                return new(true);
            }
            catch (Exception fallbackEx)
            {
                AppLog.Warn($"Microsoft Store shell:AppsFolder fallback failed: {fallbackEx.GetType().Name}: {fallbackEx.Message}");
                return new(false, "Windows couldn’t open Microsoft Store.");
            }
        }
    }

    // Steam's harmless forms only: its store, client pages and https pages inside it, and install or run by app id alone.
    // steam://run/<id>//<args> passes launch arguments to the game, and console commands go to the client: never from a link.
    private static bool IsPlainSteam(Uri uri)
    {
        var rest = uri.AbsolutePath.Trim('/');
        return uri.Host.ToLowerInvariant() switch
        {
            "store" or "nav" or "open" => true,
            "openurl" => uri.OriginalString.Length > "steam://openurl/".Length
                && Uri.TryCreate(uri.OriginalString["steam://openurl/".Length..], UriKind.Absolute, out var inner) && inner.Scheme == Uri.UriSchemeHttps,
            "install" or "rungameid" => rest.Length is > 0 and <= 20 && rest.All(char.IsAsciiDigit) && uri.Query.Length == 0,
            _ => false,
        };
    }

    public static bool IsEpicLogin(Uri uri) =>
        Httpish(uri) && (HostEnds(uri.Host, "epicgames.com") || HostEnds(uri.Host, "unrealengine.com"));

    public static bool IsGogLogin(Uri uri) =>
        Httpish(uri) && HostEnds(uri.Host, "gog.com");

    private static bool Httpish(Uri uri) =>
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);

    private static bool HostEnds(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    public static bool TryStart(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
        {
            AppLog.Warn($"Url launch rejected: {(Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? $"{parsed.Scheme}://{parsed.Host}" : "invalid URI")}");
            return false;
        }
        if (uri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase))
            return OpenStoreUri(uri).Ok;
        try
        {
            LiveOps.StartShell(LaunchUri(uri));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
