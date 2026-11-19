using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// Game sheet > Frame generation (DlssSwapService.FrameGen.cs): what the line shows. Exo puts it in and takes it out
    /// by itself, from Best settings' switch (DlssSwapService.FrameGenAuto.cs).
    /// </summary>
    private async Task<object> DlssFrameGenAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, state = "hidden" };
        var gpu = await Task.Run(() => SystemSpecs.FrameGenGpu, ct).ConfigureAwait(false);
        var status = await Task.Run(() => _dlss.FrameGenState(game, gpu, ct), ct).ConfigureAwait(false);
        return new { ok = true, state = status.State, path = status.Path, version = status.Version, session = status.Session, mod = status.Mod, antiCheat = status.AntiCheat, note = status.Note, reason = status.Reason };
    }
}
