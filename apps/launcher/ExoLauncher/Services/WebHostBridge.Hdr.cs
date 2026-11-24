using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private object HdrState()
    {
        var allScreens = WindowsHdr.All();
        var primaryGdi = WindowsHdr.PrimaryGdiName();
        var primary = primaryGdi is null ? null : allScreens.FirstOrDefault(s => string.Equals(s.GdiName, primaryGdi, StringComparison.OrdinalIgnoreCase));

        var displays = allScreens.Select(s => BuildDisplayHdrInfo(s, string.Equals(s.GdiName, primaryGdi, StringComparison.OrdinalIgnoreCase))).ToArray();

        if (primary is not { Supported: true } screen)
            return new
            {
                supported = false,
                @internal = primary?.IsInternal ?? false,
                colorMode = primary?.ColorMode,
                autoHdrGlobal = LaptopDisplayChecks.ReadAutoHdrGlobal(),
                displays,
            };

        var edidNits = WindowsHdr.PeakNits(screen);
        var dxgiNits = GameHdr.DxgiPeak(screen.GdiName);
        var health = HdrDisplayHealth.Evaluate(screen.IsInternal, edidNits, dxgiNits);
        var checks = LaptopDisplayChecks.Read(screen);
        var verdict = edidNits switch
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
            devicePath = screen.DevicePath,
            peakNits = edidNits,
            edidPeakNits = edidNits,
            dxgiPeakNits = dxgiNits,
            @internal = screen.IsInternal,
            colorMode = screen.ColorMode,
            autoHdrGlobal = checks.AutoHdrGlobal,
            calibrated = WindowsHdr.HasHdrProfile(screen, MainWindow.SdrGammaReplaced(_services.Settings, screen.DevicePath)),
            verdict,
            peakHealth = new
            {
                status = health.Status,
                edidNits = health.EdidNits,
                dxgiNits = health.DxgiNits,
                gapPercent = health.GapPercent,
                warning = health.Warning,
            },
            laptopChecks = new
            {
                isLaptop = checks.IsLaptop,
                pluggedIn = checks.PluggedIn,
                hdrOn = checks.HdrOn,
                items = checks.Items.Select(i => new
                {
                    id = i.Id,
                    label = i.Label,
                    status = i.Status,
                    warn = i.Warn,
                    detail = i.Detail,
                }).ToArray(),
            },
            displays,
        };
    }

    private static object BuildDisplayHdrInfo(WindowsHdr.Screen s, bool main)
    {
        var edidNits = WindowsHdr.PeakNits(s);
        var dxgiNits = s.Supported ? GameHdr.DxgiPeak(s.GdiName) : null;
        var health = HdrDisplayHealth.Evaluate(s.IsInternal, edidNits, dxgiNits);
        return new
        {
            name = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name,
            gdiName = s.GdiName,
            devicePath = s.DevicePath,
            main,
            supported = s.Supported,
            on = s.Enabled,
            @internal = s.IsInternal,
            connector = s.Connector,
            colorMode = s.ColorMode,
            edidPeakNits = edidNits,
            dxgiPeakNits = dxgiNits,
            peakHealth = new
            {
                status = health.Status,
                edidNits = health.EdidNits,
                dxgiNits = health.DxgiNits,
                gapPercent = health.GapPercent,
                warning = health.Warning,
            },
        };
    }

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
        var autoHdr = LaptopDisplayChecks.ReadAutoHdrForGame(game);
        return new
        {
            ok = true,
            hdr,
            reason,
            mode = _services.Settings.HdrGameMode(game.Id),
            autoHdr = new
            {
                global = autoHdr.Global,
                game = autoHdr.Game,
                effective = autoHdr.Effective,
            },
        };
    }
}
