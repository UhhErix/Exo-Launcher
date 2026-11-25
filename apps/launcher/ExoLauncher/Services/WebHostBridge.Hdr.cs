using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private void OnHdrWarning(string devicePath, string kind, string message) =>
        PostEvent("hdr.warning", new { devicePath, kind, message });

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
        var sdrWhiteRaw = HdrBrightness.ReadSdrWhiteRaw(screen);
        var sdrWhiteNits = sdrWhiteRaw is { } raw ? Math.Round(HdrBrightness.RawToNits(raw), 1) : (double?)null;
        var health = HdrDisplayHealth.Evaluate(screen.IsInternal, edidNits, dxgiNits);
        var cal = HdrCalibrationStore.Shared.Get(screen.DevicePath);
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
            sdrWhiteNits,
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
            calibration = cal is null ? null : new
            {
                devicePath = cal.DevicePath,
                edidPeakNits = cal.EdidPeakNits,
                measuredPeakNits = cal.MeasuredPeakNits,
                globalBrightness = cal.GlobalBrightness,
                sdrWhiteNits = Math.Round(cal.SdrWhiteNits, 1),
                enabled = cal.Enabled,
                calibratedAtUtc = cal.CalibratedAtUtc,
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
            brightnessWarning = _services.HdrHost.BrightnessWarning,
            displays,
        };
    }

    private object BuildDisplayHdrInfo(WindowsHdr.Screen s, bool main)
    {
        var edidNits = WindowsHdr.PeakNits(s);
        var dxgiNits = s.Supported ? GameHdr.DxgiPeak(s.GdiName) : null;
        var sdrWhiteRaw = s.Supported ? HdrBrightness.ReadSdrWhiteRaw(s) : null;
        var sdrWhiteNits = sdrWhiteRaw is { } raw ? Math.Round(HdrBrightness.RawToNits(raw), 1) : (double?)null;
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
            sdrWhiteNits,
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

    private async Task<object> HdrCalibrateAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action") ?? "run";
        var requestedPath = ReadString(p, hasParams, "devicePath");
        var screens = WindowsHdr.All();
        var target = !string.IsNullOrWhiteSpace(requestedPath)
            ? screens.FirstOrDefault(s => string.Equals(s.DevicePath, requestedPath, StringComparison.OrdinalIgnoreCase))
            : WindowsHdr.Primary() ?? screens.FirstOrDefault(s => s.IsInternal);

        if (action == "undo")
        {
            var pathToUndo = requestedPath ?? target?.DevicePath ?? "";
            if (string.IsNullOrWhiteSpace(pathToUndo)) return new { ok = false, status = "no_screen", message = "No display selected." };
            _services.HdrHost.UndoCalibration(pathToUndo);
            return new { ok = true, status = "undone", message = "Neutral-pair calibration removed and display restored." };
        }

        if (action == "toggle")
        {
            var pathToToggle = requestedPath ?? target?.DevicePath ?? "";
            var enabled = ReadBool(p, hasParams, "enabled") == true;
            if (string.IsNullOrWhiteSpace(pathToToggle)) return new { ok = false, status = "no_screen", message = "No display selected." };
            var ok = _services.HdrHost.SetCalibrationEnabled(pathToToggle, enabled);
            return new { ok, status = ok ? (enabled ? "enabled" : "disabled") : "not_found", message = ok ? (enabled ? "Neutral-pair calibration enabled." : "Neutral-pair calibration paused.") : "No calibration saved for this display." };
        }

        if (target is null)
            return new { ok = false, status = "no_screen", message = "No active display found." };
        if (!target.Supported)
            return new { ok = false, status = "no_hdr", message = "This display does not support HDR." };
        if (!target.Enabled)
            return new { ok = false, status = "hdr_off", message = "Turn HDR on before running neutral-pair calibration." };

        var result = await Task.Run(() => HdrCalibrator.Run(
            new NativeCalibrationBackend(target),
            HdrCalibrationStore.Shared,
            progress => PostEvent("hdr.calibrationProgress", new
            {
                devicePath = progress.DevicePath,
                step = progress.Step,
                total = progress.Total,
                slider = progress.Slider,
                measuredNits = progress.MeasuredNits,
                targetNits = progress.TargetNits,
            }),
            ct), ct).ConfigureAwait(false);

        return new
        {
            ok = result.Ok,
            status = result.Status,
            message = result.Message,
            manualSteps = result.ManualSteps,
            entry = result.Entry is null ? null : new
            {
                devicePath = result.Entry.DevicePath,
                edidPeakNits = result.Entry.EdidPeakNits,
                measuredPeakNits = result.Entry.MeasuredPeakNits,
                globalBrightness = result.Entry.GlobalBrightness,
                sdrWhiteNits = Math.Round(result.Entry.SdrWhiteNits, 1),
                enabled = result.Entry.Enabled,
                calibratedAtUtc = result.Entry.CalibratedAtUtc,
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
        if (ReadBool(p, hasParams, "cursorFix") is { } cursorFixOpt)
        {
            _services.Settings.SetHdrCursorFix(game.Id, cursorFixOpt);
            _services.HdrHost.SyncSoon();
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
        var primary = WindowsHdr.Primary();
        var sdrWhiteRaw = primary is { Supported: true } ? HdrBrightness.ReadSdrWhiteRaw(primary) : null;
        var sdrWhiteNits = sdrWhiteRaw is { } raw ? Math.Round(HdrBrightness.RawToNits(raw), 1) : (double?)null;
        var cursorFix = _services.Settings.HdrCursorFix(game.Id);
        var cursorFixEligible = primary is not null &&
            HdrDisplayHealth.IsCursorFixEligible(primary.IsInternal, primary.Supported, sdrWhiteNits, cursorFix);
        var autoHdr = LaptopDisplayChecks.ReadAutoHdrForGame(game);
        return new
        {
            ok = true,
            hdr,
            reason,
            mode = _services.Settings.HdrGameMode(game.Id),
            cursorFix,
            cursorFixEligible,
            sdrWhiteNits,
            autoHdr = new
            {
                global = autoHdr.Global,
                game = autoHdr.Game,
                effective = autoHdr.Effective,
            },
        };
    }
}
