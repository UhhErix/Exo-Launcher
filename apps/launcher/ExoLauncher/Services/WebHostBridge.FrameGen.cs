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

    private async Task<object> GameFrameGenAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        var route = ReadString(p, hasParams, "route");
        var xessOverride = ReadString(p, hasParams, "xessOverride");
        if (route is not null || xessOverride is not null)
        {
            var applied = await FrameGenService.ApplyRouteAsync(game, route, xessOverride, _dlss, _services.Settings, ct).ConfigureAwait(false);
            return FormatFrameGenDecision(applied.Ok, applied.Decision, applied.Message);
        }
        var decision = await FrameGenService.ResolveAsync(game, _dlss, _services.Settings, ct).ConfigureAwait(false);
        return FormatFrameGenDecision(true, decision, null);
    }

    private static object FormatFrameGenDecision(bool ok, FrameGenRouter.FrameGenDecision? d, string? message)
    {
        if (d is null) return new { ok = false, message = message ?? "Could not check frame generation." };
        return new
        {
            ok,
            message,
            bestRouteId = d.BestRouteId,
            selectedRouteId = d.SelectedRouteId,
            state = d.State,
            word = d.Word,
            good = d.Good,
            bad = d.Bad,
            reason = d.Reason,
            howToEnable = d.HowToEnable,
            routes = d.Routes.Select(r => new
            {
                id = r.Id,
                tier = r.Tier,
                kind = r.Kind,
                label = r.Label,
                status = r.Status,
                summary = r.Summary,
                gpuSupported = r.GpuSupported,
                gpuNote = r.GpuNote,
                antiCheatVerdict = r.AntiCheatVerdict,
                antiCheatNote = r.AntiCheatNote,
                autoEnable = r.AutoEnable,
                latencyNote = r.LatencyNote,
                selectable = r.Selectable,
                active = r.Active,
                reason = r.Reason,
                howToEnable = r.HowToEnable,
                choiceValue = r.ChoiceValue,
                choices = r.Choices,
            }).ToList(),
        };
    }
}
