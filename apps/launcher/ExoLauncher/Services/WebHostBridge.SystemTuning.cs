using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.tuning: Best settings › System (<see cref="SystemTuning"/>). No action reads the items; apply sets every Best
/// settings item the player keeps on in one elevated step behind a restore point; on and off set or put back one item, by
/// its item; latency runs the latency check (Windows asks; ten seconds). Each answer carries the items as they are after it, and restart: a change waits on a restart.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> SystemTuningAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var item = ReadString(p, hasParams, "item");
        var message = action switch
        {
            "apply" => await Task.Run(() => SystemTuning.Shared.ApplyAsync(), ct).ConfigureAwait(false),
            "playbook" => await Task.Run(SystemTuning.Shared.ApplyAutoAsync, ct).ConfigureAwait(false),
            "migrate" => await Task.Run(SystemTuning.Shared.MigrateAsync, ct).ConfigureAwait(false),
            "on" => item is null ? "Exo doesn't know that setting." : await Task.Run(() => SystemTuning.Shared.ApplyAsync([item]), ct).ConfigureAwait(false),
            "latency" => await Task.Run(SystemTuning.Shared.LatencyAsync, ct).ConfigureAwait(false),
            "off" => item is null ? "Exo doesn't know that setting." : await Task.Run(() => SystemTuning.Shared.TurnOffAsync(item), ct).ConfigureAwait(false),
            _ => null,
        };
        var state = await Task.Run(SystemTuning.Shared.Read, ct).ConfigureAwait(false);
        var (before, after, said) = SystemTuning.Shared.Latency();
        return new
        {
            ok = message is null,
            message,
            applied = state.Applied,
            restart = state.Restart,
            latency = after is null ? null : new { before = LatencyOf(before), after = LatencyOf(after), said },
            migration = state.Migration is { } mig ? new { pending = mig.Pending, keys = mig.Keys, summary = mig.Summary, reverted = mig.Reverted, restart = mig.Restart } : null,
            items = state.Items.Select(i => new { key = i.Key, label = i.Label, detail = i.Detail, on = i.On, canUndo = i.CanUndo, off = i.Off, optIn = i.OptIn, restart = i.Restart, needsRestart = i.NeedsRestart, blocked = i.Blocked }).ToList(),
        };
    }

    /// <summary>A latency check for the page: the longest DPC and ISR, in whole µs, and their drivers' file names.</summary>
    private static object? LatencyOf(Helpers.LatencyTrace.Result? r) => r is null ? null : new
    {
        at = r.At,
        dpcUs = Math.Round(r.DpcMaxUs),
        dpcDriver = r.DpcDriver,
        isrUs = Math.Round(r.IsrMaxUs),
        isrDriver = r.IsrDriver,
    };
}
