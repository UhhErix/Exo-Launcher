using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.interrupts: System > This PC > Device interrupts (<see cref="InterruptMode"/>). No action reads the devices;
/// apply switches every device that can to MSI mode, undo puts every one back, revert one (by its token), reapply (Switch
/// back) sets again what a driver update put back. Each answer carries the devices as they are after it, and reset: a driver
/// update put Exo's values back and Windows would ask to set them again, so PC shows Switch back.
/// </summary>
public sealed partial class WebHostBridge
{
    private static InterruptMode PcInterrupts => InterruptMode.ThisPc;

    private static async Task<object> SystemInterruptsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var device = ReadString(p, hasParams, "device");
        var message = await Task.Run(() => action switch
        {
            "apply" => PcInterrupts.Apply(),
            "undo" => PcInterrupts.Undo(null),
            "revert" when !string.IsNullOrEmpty(device) => PcInterrupts.Undo(device),
            "reapply" => PcInterrupts.Reapply(),
            _ => null,
        }, ct).ConfigureAwait(false);
        var state = await Task.Run(PcInterrupts.Read, ct).ConfigureAwait(false);
        // When Windows won't ask, Exo sets it again by itself at a quiet moment (InterruptMode.FollowUpAsync).
        var reset = state.Lapsed && await Task.Run(Helpers.ElevatedCommands.WindowsAsks, ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            restart = state.Restart,
            result = state.Result,
            canUndo = state.CanUndo,
            reset,
            devices = state.Devices.Select(d => new
            {
                id = d.Token,
                name = d.Name,
                kind = d.Kind,
                mode = d.Mode.ToString().ToLowerInvariant(),
                plan = d.Plan.ToString().ToLowerInvariant(),
                reason = d.Reason,
                changed = d.Changed,
            }).ToList(),
        };
    }
}
