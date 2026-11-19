using System.Collections.Concurrent;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Apps › Windows apps: Store and preinstalled apps across this PC's accounts (<see cref="StorePackages"/>).
/// apps.packages lists them (what's never offered isn't, only counted); apps.packagesPlan says, before anything goes, what
/// removing the chosen ones for you or for every account does and whether it can be undone, and keeps that plan here under a
/// token; apps.packagesRemove carries out only that plan (the page names nothing that becomes a command): for you through
/// PackageManager with no prompt, for every account in one Windows prompt. apps.packageAddBack registers one removed for you
/// again from the files still on this PC, else opens its Microsoft Store page.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly ConcurrentDictionary<string, (List<StorePackage> Packages, bool Everyone, DateTime At)> PackagePlans = new();

    private static async Task<object> AppsPackagesAsync()
    {
        var (packages, hidden) = await Task.Run(StorePackages.Read).ConfigureAwait(true);
        return new
        {
            ok = true,
            hidden,
            items = packages.Select(package => new
            {
                id = package.Family,
                name = package.Display,
                publisher = package.Publisher,
                forMe = package.ForMe,
                others = package.Others,
                provisioned = package.Provisioned is not null,
                bloat = AppsBloat.ForPackage(package.Name),
                addBack = !package.ForMe && package.Staged,
            }),
        };
    }

    private static async Task<object> AppsPackagesPlanAsync(JsonElement p, bool hasParams)
    {
        var ids = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Take(100).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var everyone = hasParams && p.TryGetProperty("everyone", out var all) && all.ValueKind == JsonValueKind.True;
        var (packages, _) = await Task.Run(StorePackages.Read).ConfigureAwait(true);
        var chosen = packages.Where(package => ids.Contains(package.Family) && StorePackages.Line(package, everyone) is not null).ToList();
        if (chosen.Count == 0)
            return new { ok = false, token = (string?)null, admin = false, lines = Array.Empty<PackageLine>(), message = (string?)(everyone ? "None of those can be removed here." : "None of those are installed for you: choose Every account.") };
        foreach (var old in PackagePlans.Where(pair => DateTime.UtcNow - pair.Value.At > TimeSpan.FromMinutes(30)).Select(pair => pair.Key).ToList()) PackagePlans.TryRemove(old, out _);
        var token = Guid.NewGuid().ToString("N");
        PackagePlans[token] = (chosen, everyone, DateTime.UtcNow);
        return new
        {
            ok = true, token, admin = everyone,
            lines = chosen.Select(package => StorePackages.Line(package, everyone)!).Select(line => new { id = line.Id, name = line.Name, does = line.Does, back = line.Back, undoable = line.Undoable }),
            message = (string?)null,
        };
    }

    private async Task<object> AppsPackagesRemoveAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (!PackagePlans.TryRemove(ReadString(p, hasParams, "token") ?? "", out var plan)) return new { ok = false, removed = 0, left = 0, message = (string?)"Look at what goes again first." };
        if (!await AppsGate.WaitAsync(0, ct).ConfigureAwait(false)) return new { ok = false, removed = 0, left = 0, message = (string?)"Another app is being removed." };
        try
        {
            var declined = false;
            if (plan.Everyone)
            {
                var steps = plan.Packages.Select(StorePackages.EveryoneStep).OfType<string>().ToList();
                if (steps.Count > 0) declined = !await ReplacedApps.RemoveAsAdminAsync(steps, ct).ConfigureAwait(true);
            }
            else
            {
                var packages = new WindowsAppPackages();
                foreach (var package in plan.Packages.Where(package => package.Mine is not null)) await packages.RemoveAsync(package.Mine!, ct).ConfigureAwait(true);
            }
            var (now, _) = await Task.Run(StorePackages.Read, ct).ConfigureAwait(true);
            var left = plan.Packages.Count(package => now.FirstOrDefault(found => found.Family.Equals(package.Family, StringComparison.OrdinalIgnoreCase)) is { } still &&
                (plan.Everyone ? still.ForMe || still.Others > 0 || still.Provisioned is not null : still.ForMe));
            var removed = plan.Packages.Count - left;
            AppLog.Info($"Store apps: removed {removed} of {plan.Packages.Count} {(plan.Everyone ? "for every account" : "for this user")}{(declined ? ", Windows' permission declined" : "")}.");
            return new
            {
                ok = left == 0, removed, left,
                message = (string?)(left == 0 ? null : declined ? "Windows' permission was declined: nothing was removed." : $"{left} {(left == 1 ? "app" : "apps")} stayed: Windows didn't remove {(left == 1 ? "it" : "them")}."),
            };
        }
        finally
        {
            AppsChangedHere();
            AppsGate.Release();
        }
    }

    private static async Task<object> AppsPackageAddBackAsync(string id, CancellationToken ct)
    {
        var (packages, _) = await Task.Run(StorePackages.Read, ct).ConfigureAwait(true);
        if (packages.FirstOrDefault(package => package.Family.Equals(id, StringComparison.OrdinalIgnoreCase)) is { ForMe: false, Staged: true } package &&
            await StorePackages.AddBackAsync(package.Family, ct).ConfigureAwait(true))
            return new { ok = true, store = false, message = (string?)null };
        // Not on this PC to register again: its Store page, where it can be installed again.
        if (StorePackages.StoreLink(id) is not { } link) return new { ok = false, store = false, message = (string?)"Windows didn't add it back." };
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
            return new { ok = true, store = true, message = (string?)"Opened in the Microsoft Store." };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Debug("Store apps: the Store didn't open: " + ex.GetType().Name);
            return new { ok = false, store = false, message = (string?)"Windows didn't add it back, and the Microsoft Store didn't open." };
        }
    }
}
