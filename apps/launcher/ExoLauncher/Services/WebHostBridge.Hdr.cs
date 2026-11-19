using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// System > Graphics > HDR: whether the main screen can show HDR, how bright it really gets
    /// (its EDID), whether Windows holds an HDR calibration for it, and a plain verdict.
    /// </summary>
    private object HdrState()
    {
        if (WindowsHdr.Primary() is not { Supported: true } screen)
            return new { supported = false };
        var nits = WindowsHdr.PeakNits(screen);
        var verdict = nits switch
        {
            null => "unknown",
            >= 600 => "great",
            >= 400 => "good",
            _ => "weak",
        };
        return new
        {
            supported = true,
            on = screen.Enabled,
            name = string.IsNullOrWhiteSpace(screen.Name) ? null : screen.Name,
            peakNits = nits,
            calibrated = WindowsHdr.HasHdrProfile(screen, MainWindow.SdrGammaReplaced(_services.Settings, screen.DevicePath)),
            verdict,
        };
    }

    /// <summary>
    /// Game sheet > HDR: Exo's verdict on the game (<see cref="GameHdr"/>; the wiki asked only when the game itself says
    /// nothing), as "true", "false", "hackable" (the wiki: only with a mod) or "unknown", and its Auto/On/Off choice (set
    /// when "mode" is passed).
    /// </summary>
    private async Task<object> HdrGameAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        if (ReadString(p, hasParams, "mode") is { } mode)
        {
            if (mode is not ("auto" or "on" or "off")) return new { ok = false, message = "Unknown HDR choice." };
            _services.Settings.SetHdrGameMode(game.Id, mode);
        }
        var verdict = await Task.Run(() => GameHdr.Judge(game, readFiles: false), ct).ConfigureAwait(false);
        if (verdict.From is null or "wiki" or "files")
        {
            await GameWiki.LookupAsync(game, ct).ConfigureAwait(false);
            verdict = await Task.Run(() => GameHdr.Judge(game), ct).ConfigureAwait(false);
        }
        var hdr = verdict.Hdr switch
        {
            "yes" => "true",
            "no" => verdict.From == "wiki" && GameWiki.Cached(game.Id) == "hackable" ? "hackable" : "false",
            _ => "unknown",
        };
        var reason = HdrPlan.Reason(verdict.Hdr, verdict.From, GameWiki.Cached(game.Id) == "hackable");
        return new { ok = true, hdr, reason, mode = _services.Settings.HdrGameMode(game.Id) };
    }
}
