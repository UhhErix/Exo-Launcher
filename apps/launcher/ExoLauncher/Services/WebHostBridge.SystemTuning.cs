using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.tuning: Best settings › System (<see cref="SystemTuning"/>). No action reads the items; apply sets every Best
/// settings item the player keeps on in one elevated step behind a restore point; on and off set or put back one item, by
/// its item. Each answer carries the items as they are after it, and restart: a change waits on a restart.
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
            "on" => item is null ? "Exo doesn't know that setting." : await Task.Run(() => SystemTuning.Shared.ApplyAsync([item]), ct).ConfigureAwait(false),
            "off" => item is null ? "Exo doesn't know that setting." : await Task.Run(() => SystemTuning.Shared.TurnOffAsync(item), ct).ConfigureAwait(false),
            _ => null,
        };
        var state = await Task.Run(SystemTuning.Shared.Read, ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            applied = state.Applied,
            restart = state.Restart,
            items = state.Items.Select(i => new { key = i.Key, label = i.Label, detail = i.Detail, on = i.On, canUndo = i.CanUndo, off = i.Off, optIn = i.OptIn, restart = i.Restart, needsRestart = i.NeedsRestart, blocked = i.Blocked }).ToList(),
        };
    }
}
