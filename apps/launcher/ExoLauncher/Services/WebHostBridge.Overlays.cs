using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private static async Task<object> SystemOverlaysAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var id = ReadString(p, hasParams, "id");
        var message = action switch
        {
            "off" => id is null ? "Exo doesn't know that overlay." : await Task.Run(() => OverlayAdvisor.Shared.Disable(id), ct).ConfigureAwait(false),
            "restore" => id is null ? "Exo doesn't know that overlay." : await Task.Run(() => OverlayAdvisor.Shared.Restore(id), ct).ConfigureAwait(false),
            _ => null,
        };
        var state = await Task.Run(OverlayAdvisor.Shared.Read, ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            items = state.Items.Select(i => new
            {
                id = i.Id,
                name = i.Name,
                status = i.Status,
                enabled = i.Enabled,
                cost = i.Cost,
                detail = i.Detail,
                canToggle = i.CanToggle,
                canUndo = i.CanUndo,
                manualSteps = i.ManualSteps,
            }).ToList(),
        };
    }
}
