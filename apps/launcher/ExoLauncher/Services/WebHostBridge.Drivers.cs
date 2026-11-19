using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// system.drivers: PC › Drivers (<see cref="DriverCatalog"/>). No action reads the list (fresh: asks Windows Update again);
/// update-all starts installing every row Exo can install, and install (with id) only that row (a vendor's app, or the
/// update past a known problem); either answers at once, each row's start and end then arriving as drivers.step. Each
/// answer carries the rows, why nothing can install now, and whether a restart finishes what installed. A row's skipped
/// names an offered version with a known problem on this PC and why; advised, what runs has one, why, and the way out.
/// </summary>
public sealed partial class WebHostBridge
{
    private IReadOnlyList<DriverItem> _drivers = [];

    private async Task<object> SystemDriversAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var catalog = DriverCatalog.Shared;
        var action = ReadString(p, hasParams, "action");
        if (ReadBool(p, hasParams, "fresh") == true) await WindowsUpdateDrivers.Shared.OffersAsync(ct, fresh: true).ConfigureAwait(false);
        string? message = null;
        var installing = action is "update-all" or "install";
        if (installing)
        {
            var id = ReadString(p, hasParams, "id");
            var items = action == "install" ? _drivers.Where(i => i.Id == id && i.How == DriverHow.Install).ToList() : _drivers;
            message = catalog.Busy() ?? (action == "install" && items.Count == 0 ? "Exo can't install this one." : null);
            if (message is null && items.Any(i => i.How == DriverHow.Install))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await catalog.UpdateAllAsync(items, (item, result) => PostEvent("drivers.step", new
                        {
                            id = item.Id,
                            working = result is null ? catalog.Owner(item)?.Working(item) ?? "Updating" : null,
                            ok = result?.Ok,
                            message = result?.Message,
                            restart = result?.Restart == true,
                        }), CancellationToken.None, pressed: action == "install").ConfigureAwait(false);
                    }
                    catch (Exception ex) { AppLog.Warn("Drivers: update all stopped: " + ex.Message); }
                    finally { PostEvent("drivers.step", new { id = "", working = (string?)null, ok = (bool?)null, message = (string?)null, restart = catalog.Restart }); }
                });
            }
        }
        else if (action is not null) message = "Exo doesn't know that change.";
        if (!installing) _drivers = await catalog.ReadAsync(ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            busy = catalog.Busy(),
            running = catalog.Running || installing && message is null,
            restart = catalog.Restart,
            items = _drivers.Select(i => new
            {
                id = i.Id,
                kind = i.Kind,
                name = i.Name,
                maker = i.Maker,
                version = i.Version,
                date = i.Date,
                latest = i.Latest,
                latestDate = i.LatestDate,
                how = i.How switch { DriverHow.Install => "install", DriverHow.Page => "page", _ => "none" },
                page = i.Page,
                source = i.Source,
                app = i.App,
                skipped = i.Skipped is { } s ? new { version = i.Latest, why = s.Why } : null,
                advised = i.Advised is { } a ? new
                {
                    version = i.Version,
                    why = a.Why,
                    action = a.Do switch { DriverAdvisories.Advice.Back => "back", DriverAdvisories.Advice.Update => "update", _ => "skip" },
                    to = a.To,
                } : null,
            }).ToList(),
        };
    }
}
