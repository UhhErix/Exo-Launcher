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
        lossless = status.Lossless is { } ls ? new
        {
            installed = ls.Installed,
            storeUrl = ls.StoreUrl,
            mode = ls.Mode,
            multiplier = ls.Multiplier,
            hotkey = ls.Hotkey,
        } : null,
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
        var mode = ReadString(p, hasParams, "mode");
        int? multiplier = hasParams && p.TryGetProperty("multiplier", out var multEl) && multEl.TryGetInt32(out var mult) ? mult : null;
        var gpu = await Task.Run(() => SystemSpecs.FrameGenGpu, ct).ConfigureAwait(false);
        var (ok, message) = await Task.Run(() => _dlss.FrameGenSetAsync(game, gpu, enabled, mode, multiplier, ct), ct).ConfigureAwait(false);
        if (ok) _services.Settings.SetFrameGenKeptOff(game.Id, !enabled);
        var status = await Task.Run(() => _dlss.FrameGenState(game, gpu, ct), ct).ConfigureAwait(false);
        return FrameGenDto(status, ok, message);
    }
}
