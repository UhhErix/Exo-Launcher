using System.ComponentModel;
using System.Diagnostics;
using ExoLauncher.Helpers;
using Windows.Management.Deployment;

namespace ExoLauncher.Services;

/// <summary>
/// gpu.controlPanel: the graphics card maker's own control panel, for what Exo doesn't set. For the card Exo sets
/// (<see cref="GraphicsService.Vendor"/>, else the first card Windows lists): NVIDIA Control Panel (its Store app, else the
/// nvcplui.exe a pre-DCH driver installs), AMD Software: Adrenalin Edition (RadeonSoftware.exe), or Intel Graphics Software,
/// Arc Control before it, or Intel Graphics Command Center (its Store app). A program is looked for where its installer
/// puts it, then by its Start menu shortcut. Always opened as the user: a program is started without the shell, so one
/// that asks for administrator rights fails instead of Windows asking. One that isn't installed is named.
/// </summary>
internal static class GpuControlPanel
{
    /// <summary>One place a panel can be: a Store app's package family, or a program (its usual path, its exe's name).</summary>
    internal sealed record Place(string Name, string? Family = null, string? Program = null);

    /// <summary>Where each maker's panel can be, newest first; the first is the one named when none is there.</summary>
    internal static IReadOnlyList<Place> For(string? vendor)
    {
        var files = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return vendor switch
        {
            "nvidia" =>
            [
                new("NVIDIA Control Panel", Family: "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj"),
                new("NVIDIA Control Panel", Program: Path.Combine(files, @"NVIDIA Corporation\Control Panel Client\nvcplui.exe")),
            ],
            "amd" => [new("AMD Software", Program: Path.Combine(files, @"AMD\CNext\CNext\RadeonSoftware.exe"))],
            "intel" =>
            [
                new("Intel Graphics Software", Program: Path.Combine(files, @"Intel\Intel Graphics Software\IntelGraphicsSoftware.exe")),
                new("Arc Control", Program: Path.Combine(files, @"Intel\Intel Arc Control\ArcControl.exe")),
                new("Intel Graphics Command Center", Family: "AppUp.IntelGraphicsExperience_8j3eq9eme6ctt"),
            ],
            _ => [],
        };
    }

    internal static object Open()
    {
        var vendor = GraphicsService.Vendor() ?? GraphicsService.Vendors(GpuCapability.Adapters()).FirstOrDefault();
        var places = For(vendor);
        if (places.Count == 0) return new { ok = false, message = "Exo can't find a graphics card." };
        IReadOnlyList<string>? menu = null;
        foreach (var place in places)
        {
            if (place.Family is { } family)
            {
                if (AppId(family) is not { } app) continue;
                return Started(place.Name, () => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = @"shell:AppsFolder\" + app, UseShellExecute = true }));
            }
            var program = place.Program!;
            if (!File.Exists(program))
            {
                menu ??= StartMenuTargets();
                program = menu.FirstOrDefault(target => Path.GetFileName(target).Equals(Path.GetFileName(program), StringComparison.OrdinalIgnoreCase) && File.Exists(target)) ?? "";
            }
            if (program.Length == 0) continue;
            return Started(place.Name, () => Process.Start(new ProcessStartInfo(program) { WorkingDirectory = Path.GetDirectoryName(program), UseShellExecute = false }));
        }
        // NVIDIA's is a Store app (and Exo's Clean up may have removed it as a replaced app): its Store page, to get it back
        // with one press there. AMD's and Intel's come with their drivers, so they are only named.
        if (vendor == "nvidia")
        {
            AppLog.Info("Control panel: NVIDIA Control Panel isn't installed; opening its Store page.");
            return Started("NVIDIA Control Panel's Store page", () => Process.Start(new ProcessStartInfo(NvidiaStorePage) { UseShellExecute = true }));
        }
        AppLog.Info($"Control panel: {places[0].Name} isn't installed.");
        return new { ok = false, message = $"{places[0].Name} isn't installed." };
    }

    /// <summary>NVIDIA Control Panel in the Microsoft Store (apps.microsoft.com/detail/9NF8H0H7WMLT).</summary>
    internal const string NvidiaStorePage = "ms-windows-store://pdp/?ProductId=9NF8H0H7WMLT";

    private static object Started(string name, Func<Process?> start)
    {
        try
        {
            using var process = start();
            AppLog.Info($"Control panel: opened {name}.");
            return new { ok = true };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)   // ERROR_ELEVATION_REQUIRED: never asked for here
        {
            AppLog.Info($"Control panel: {name} asks for administrator rights; not opened.");
            return new { ok = false, message = $"{name} only opens as administrator." };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Control panel: {name} didn't open: {ex.Message}");
            return new { ok = false, message = $"{name} didn't open." };
        }
    }

    /// <summary>A Store app's id for this user (its first entry in Start), or null when it isn't installed.</summary>
    private static string? AppId(string family)
    {
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, family))
                if (package.GetAppListEntries().FirstOrDefault()?.AppUserModelId is { Length: > 0 } id) return id;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Debug($"Control panel: {family} unreadable ({ex.GetType().Name}).");
        }
        return null;
    }

    /// <summary>What the Start menu's shortcuts (all users' and this user's) open.</summary>
    private static List<string> StartMenuTargets()
    {
        var targets = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var link in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(3000))
                    if (LeftoverScan.ShortcutTarget(link) is { } target && Path.IsPathFullyQualified(target)) targets.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return targets;
    }
}
