using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static class VersionWatch
{
    internal sealed record Found(string Id, string Name, string Version, string? Installed, string Page);

    private static readonly Lock Gate = new();
    private static Dictionary<string, string>? _told;
    private static string ToldPath => Path.Combine(PathHelper.AppDataDir, "version-watch.json");

    internal static async Task<List<Found>> FindAsync(AppSettings settings, CancellationToken ct)
    {
        var found = new List<Found>();
        if (await DriverAsync(settings, ct).ConfigureAwait(false) is { } driver) found.Add(driver);
        return found;
    }

    private static async Task<Found?> DriverAsync(AppSettings settings, CancellationToken ct)
    {
        var vendor = GraphicsService.Vendor() ?? GraphicsService.Vendors(GpuCapability.Adapters()).FirstOrDefault();
        var advisories = DriverAdvisories.Shared;
        if (vendor is "amd" or "intel")
        {
            if (GpuDriverUpdate.Card(vendor) is not { } card || await GpuDriverUpdate.LatestAsync(vendor, ct).ConfigureAwait(false) is not { } newest) return null;
            var have = GpuDriverUpdate.Installed(vendor, card);
            if (have is null || !GpuDriverUpdate.Newer(newest.Version, have) || advisories.Against(DriverAdvisories.Gpu(vendor, card), newest.Version) is not null)
                return null;
            var label = vendor == "amd"
                ? newest.Kind switch
                {
                    "hotfix" => "AMD Software: Adrenalin hotfix",
                    "optional" => "AMD Software: Adrenalin optional driver",
                    _ => "AMD Software: Adrenalin Edition",
                }
                : newest.Kind == "hotfix" ? "Intel hotfix graphics driver" : "Intel graphics driver";
            return new Found("driver." + vendor, label, newest.Version, have, "drivers");
        }
        if (vendor != "nvidia") return null;
        var latest = await NvidiaDriverService.LatestAsync(settings.NvidiaDriverBranch == "studio" ? "studio" : "game", ct).ConfigureAwait(false);
        var installed = NvidiaDriverService.Installed();
        if (latest is null || installed is null || NvidiaDriverService.Compare(latest.Version, installed) <= 0
            || advisories.Against(DriverAdvisories.Gpu("nvidia", NvidiaDriverService.Card()), latest.Version) is not null) return null;
        var name = latest.Kind switch
        {
            "hotfix" => "NVIDIA hotfix driver",
            "studio" => "NVIDIA Studio driver",
            "security" => "NVIDIA security update",
            "workstation" => "NVIDIA RTX Enterprise driver",
            _ => "NVIDIA Game Ready driver",
        };
        return new Found("driver.nvidia", name, latest.Version, installed, "drivers");
    }

    internal static bool FirstTime(Found found)
    {
        lock (Gate)
        {
            var told = Told();
            if (told.TryGetValue(found.Id, out var before) && before == found.Version) return false;
            told[found.Id] = found.Version;
            try
            {
                var temp = ToldPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(told));
                File.Move(temp, ToldPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Versions: what was told wasn't saved: " + ex.Message); }
            return true;
        }
    }

    private static Dictionary<string, string> Told()
    {
        if (_told is not null) return _told;
        try
        {
            if (File.Exists(ToldPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ToldPath)) is { } saved) return _told = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Debug("Versions: what was told is unreadable: " + ex.Message); }
        return _told = [];
    }
}
