using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private static object FrameGenDto(DlssSwapService.FrameGenStatus status, bool ok = true, string? message = null) => new
    {
        ok,
        state = status.State,
        path = status.Path,
        package = status.Package,
        version = status.Version,
        session = status.Session,
        mod = status.Mod,
        antiCheat = status.AntiCheat,
        note = status.Note,
        reason = status.Reason,
        canToggle = status.CanToggle,
        message,
    };

    private async Task<object> DlssFrameGenAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, state = "hidden" };
        var gpu = await Task.Run(() => SystemSpecs.FrameGenGpu, ct).ConfigureAwait(false);
        var status = await Task.Run(() => _dlss.FrameGenState(game, gpu, ct), ct).ConfigureAwait(false);
        return FrameGenDto(status);
    }

    private async Task<object> DlssFrameGenSetAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, state = "hidden", message = "Game not found." };
        var enabled = ReadBool(p, hasParams, "enabled") == true;
        var acceptRisk = ReadBool(p, hasParams, "acceptRisk") == true;
        var shownAntiCheat = ReadString(p, hasParams, "antiCheat");
        var gpu = await Task.Run(() => SystemSpecs.FrameGenGpu, ct).ConfigureAwait(false);
        var (ok, message) = await Task.Run(() => _dlss.FrameGenSetAsync(game, gpu, enabled, acceptRisk, shownAntiCheat, ct), ct).ConfigureAwait(false);
        if (ok) _services.Settings.SetFrameGenKeptOff(game.Id, !enabled);
        var status = await Task.Run(() => _dlss.FrameGenState(game, gpu, ct), ct).ConfigureAwait(false);
        return FrameGenDto(status, ok, message);
    }

    private async Task<object> GameFrameGenAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        var route = ReadString(p, hasParams, "route");
        var xessOverride = ReadString(p, hasParams, "xessOverride");
        var acceptRisk = ReadBool(p, hasParams, "acceptRisk") == true;
        var shownAntiCheat = ReadString(p, hasParams, "antiCheat");
        if (route is not null || xessOverride is not null)
        {
            var applied = await FrameGenService.ApplyRouteAsync(game, route, xessOverride, _dlss, _services.Settings, ct, acceptRisk, shownAntiCheat).ConfigureAwait(false);
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
