using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static class FrameGenService
{
    private static readonly TimeSpan FactsWait = TimeSpan.FromSeconds(4);

    internal sealed record RouteResult(bool Ok, FrameGenRouter.FrameGenDecision? Decision, string? Message = null);

    internal static async Task<FrameGenRouter.FrameGenDecision> ResolveAsync(
        GameEntry game,
        DlssSwapService dlss,
        SettingsService settings,
        CancellationToken ct)
    {
        var gpu = await Task.Run(ReadGpu, ct).ConfigureAwait(false);
        var fluidTask = FrameGenFacts.WithinAsync(game, FactsWait, ct);
        var scanTask = Task.Run(() => InspectGame(game, dlss, gpu, ct), ct);
        await Task.WhenAll(fluidTask, scanTask).ConfigureAwait(false);

        var fluid = await fluidTask.ConfigureAwait(false);
        var inspected = await scanTask.ConfigureAwait(false);
        var savedRoute = settings.FrameGenRouteFor(game.Id);
        var smoothOn = settings.SmoothMotionEnabled(game.Id);
        var exoFlowAvailable = ExoFlowService.IsAvailable();
        var exoFlowActive = exoFlowAvailable
            && string.Equals(savedRoute, FrameGenRouter.RouteExternalExoFlow, StringComparison.OrdinalIgnoreCase);
        var intelChoice = gpu.Vendor == "intel" ? IntelProfileService.Shared.FrameGen(game.Id).Choice : "game";
        bool? afmfOverride = string.Equals(savedRoute, FrameGenRouter.RouteDriverAfmf, StringComparison.OrdinalIgnoreCase)
            ? true
            : string.Equals(savedRoute, "off", StringComparison.OrdinalIgnoreCase)
                ? false
                : null;

        var input = new FrameGenRouter.FrameGenGameInput(
            GameId: game.Id,
            Title: game.Title,
            Installed: game.Installed,
            Readable: inspected.Readable,
            IsWutheringWaves: inspected.IsWuWa,
            IsRiot: AntiCheatCheck.IsRiot(game),
            AntiCheat: inspected.AntiCheat,
            AllowsPlacedMods: inspected.AllowsPlaced,
            ShipsDlssFg: inspected.ShipsDlss,
            ShipsFsrFg: inspected.ShipsFsr,
            ShipsXessFg: inspected.ShipsXess,
            ShipsXessFgDll: inspected.ShipsXessDll,
            ModStatus: inspected.ModStatus,
            FluidFacts: fluid,
            SmoothMotionEnabled: smoothOn,
            AfmfOverride: afmfOverride,
            IntelXessOverride: intelChoice,
            UserSelectedRoute: savedRoute,
            ExoFlowAvailable: exoFlowAvailable,
            ExoFlowEnabled: exoFlowActive);

        return FrameGenRouter.Decide(gpu, input);
    }

    internal static async Task<RouteResult> ApplyRouteAsync(
        GameEntry game,
        string? routeId,
        string? xessOverride,
        DlssSwapService dlss,
        SettingsService settings,
        CancellationToken ct,
        bool acceptRisk = false,
        string? shownAntiCheat = null)
    {
        var gpu = await Task.Run(ReadGpu, ct).ConfigureAwait(false);
        var current = await ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(xessOverride))
        {
            if (gpu.Vendor != "intel" || !current.Routes.Any(r => r.Id == FrameGenRouter.RouteDriverXessOverride))
                return new RouteResult(false, current, "Intel XeSS-FG override requires an Intel GPU and a game with libxess_fg.dll.");
            var saved = await Task.Run(() => IntelProfileService.Shared.SetFrameGen(game.Id, xessOverride), ct).ConfigureAwait(false);
            if (!saved) return new RouteResult(false, current, "Could not save Intel XeSS-FG override.");
            if (string.IsNullOrWhiteSpace(routeId))
                return new RouteResult(true, await ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false));
        }

        if (string.IsNullOrWhiteSpace(routeId))
            return new RouteResult(true, current);

        if (string.Equals(routeId, "off", StringComparison.OrdinalIgnoreCase))
        {
            var err = await TurnOffActiveRoutesAsync(game, dlss, settings, ct).ConfigureAwait(false);
            settings.SetFrameGenRoute(game.Id, "off");
            var next = await ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false);
            return new RouteResult(err is null, next, err);
        }

        var target = current.Routes.FirstOrDefault(r => string.Equals(r.Id, routeId, StringComparison.OrdinalIgnoreCase));
        if (target is null || !target.Selectable)
        {
            var isModRequest = string.Equals(routeId, FrameGenRouter.RouteModOptiScaler, StringComparison.OrdinalIgnoreCase)
                || string.Equals(routeId, FrameGenRouter.RouteModDlssg, StringComparison.OrdinalIgnoreCase)
                || string.Equals(routeId, FrameGenRouter.RouteModMfg, StringComparison.OrdinalIgnoreCase);
            if (isModRequest)
            {
                var currentAntiCheat = await Task.Run(() => dlss.AntiCheatNow(game, ct), ct).ConfigureAwait(false);
                if (FrameGenAntiCheat.IsDllDisallowed(game, currentAntiCheat))
                    return new RouteResult(false, current, FrameGenAntiCheat.OnlineBlockedMessage);
            }
            return new RouteResult(false, current, target?.Reason ?? "That frame generation route is not available for this game.");
        }

        string? applyError = null;
        if (target.Id == FrameGenRouter.RouteDriverSmoothMotion)
        {
            applyError = await ApplySmoothMotionAsync(game, true, settings, ct).ConfigureAwait(false);
            if (applyError is null)
                settings.SetFrameGenRoute(game.Id, FrameGenRouter.RouteDriverSmoothMotion);
        }
        else if (target.Id is FrameGenRouter.RouteModOptiScaler or FrameGenRouter.RouteModDlssg or FrameGenRouter.RouteModMfg)
        {
            var currentAntiCheat = await Task.Run(() => dlss.AntiCheatNow(game, ct), ct).ConfigureAwait(false);
            if (FrameGenAntiCheat.IsDllDisallowed(game, currentAntiCheat))
                return new RouteResult(false, current, FrameGenAntiCheat.OnlineBlockedMessage);

            string? acceptedAntiCheat = null;
            if (currentAntiCheat is not null && FrameGenAntiCheat.IsDllOptInWithConsent(game, currentAntiCheat))
            {
                var savedConsent = settings.GraphicsModConsent(game.Id);
                if (string.Equals(savedConsent, currentAntiCheat, StringComparison.OrdinalIgnoreCase))
                {
                    acceptedAntiCheat = currentAntiCheat;
                }
                else if (acceptRisk && (string.IsNullOrWhiteSpace(shownAntiCheat) || string.Equals(shownAntiCheat, currentAntiCheat, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.SetGraphicsModConsent(game.Id, currentAntiCheat);
                    AppLog.Info($"Frame generation consent: gameId={game.Id}; anti-cheat {currentAntiCheat}; user accepted the risk.");
                    acceptedAntiCheat = currentAntiCheat;
                }
                else
                {
                    return new RouteResult(false, current, FrameGenAntiCheat.BanRiskWarning(game, currentAntiCheat));
                }
            }
            settings.SetFrameGenKeptOff(game.Id, false);
            var (ok, message) = await dlss.FrameGenOnAsync(game, SystemSpecs.FrameGenGpu, ct, auto: false, acceptedAntiCheat: acceptedAntiCheat).ConfigureAwait(false);
            if (!ok) applyError = message;
            else
            {
                await ApplySmoothMotionAsync(game, false, settings, ct).ConfigureAwait(false);
                settings.SetFrameGenRoute(game.Id, target.Id);
            }
        }
        else if (target.Id == FrameGenRouter.RouteExternalExoFlow)
        {
            if (!ExoFlowService.IsAvailable())
            {
                applyError = "Exo Flow sidecar (exo-flow.exe) was not found.";
            }
            else
            {
                await Task.Run(() =>
                {
                    var pc = GameSettings.ThisPc();
                    if (GameSettings.For(game, pc) is { WindowMode: not null } profile)
                        GameSettings.ChooseWindowMode(profile, GameSettings.Borderless);
                }, ct).ConfigureAwait(false);
                await ApplySmoothMotionAsync(game, false, settings, ct).ConfigureAwait(false);
                settings.SetFrameGenRoute(game.Id, FrameGenRouter.RouteExternalExoFlow);
                ExoFlowService.EnsureRunning();
            }
        }
        else if (target.Id == FrameGenRouter.RouteDriverAfmf)
        {
            settings.SetFrameGenRoute(game.Id, FrameGenRouter.RouteDriverAfmf);
        }
        else if (target.Tier == FrameGenRouter.TierNative)
        {
            await ApplySmoothMotionAsync(game, false, settings, ct).ConfigureAwait(false);
            if (GameSettings.IsWutheringWavesEntry(game) || GameSettings.IsWutheringWaves(game))
                await Task.Run(() => DlssSwapService.ApplyWuWaNativeFrameGenConfig(game, gpu.Name, target.Id), ct).ConfigureAwait(false);
            settings.SetFrameGenRoute(game.Id, target.Id);
        }

        var refreshed = await ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false);
        return new RouteResult(applyError is null, refreshed, applyError);
    }

    private static async Task<string?> TurnOffActiveRoutesAsync(
        GameEntry game,
        DlssSwapService dlss,
        SettingsService settings,
        CancellationToken ct)
    {
        var smErr = await ApplySmoothMotionAsync(game, false, settings, ct).ConfigureAwait(false);
        await ExoFlowService.StopIfRunningAsync(ct).ConfigureAwait(false);
        if (GraphicsService.Vendor() == "intel")
            await Task.Run(() => IntelProfileService.Shared.SetFrameGen(game.Id, "game"), ct).ConfigureAwait(false);
        settings.SetFrameGenKeptOff(game.Id, true);
        await Task.Run(() => dlss.FrameGenOff(game), ct).ConfigureAwait(false);
        return smErr;
    }

    private static async Task<string?> ApplySmoothMotionAsync(
        GameEntry game,
        bool on,
        SettingsService settings,
        CancellationToken ct)
    {
        if (!on && !settings.SmoothMotionEnabled(game.Id)) return null;
        return await Task.Run<string?>(() =>
        {
            var gpuName = SystemSpecs.FrameGenGpu ?? "";
            var series = GpuProfile.NvidiaSeries(gpuName) ?? 0;
            if (on && (GraphicsService.Vendor() != "nvidia" || series < 40 || !GraphicsService.KnownIds().Contains(GraphicsCatalog.SmoothMotion)))
                return "NVIDIA Smooth Motion requires an RTX 40 or 50 GPU with a supported driver.";

            var exe = ResolveGameExe(game);
            if (exe is null)
            {
                if (!on)
                {
                    settings.SetSmoothMotion(game.Id, false);
                    return null;
                }
                return "Could not find this game's executable for its NVIDIA driver profile.";
            }

            if (NvidiaProfiles.GameProfilesUsable)
            {
                var fileInFolder = GameSettings.IsWutheringWavesEntry(game) ? "Client-Win64-ShippingBase.dll" : "";
                var program = new NvidiaProfiles.GameProgram(
                    exe,
                    game.Title,
                    fileInFolder,
                    (name, beside) => string.IsNullOrEmpty(fileInFolder)
                        || beside.Equals(fileInFolder, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(game.Title, StringComparison.OrdinalIgnoreCase));
                var writes = new Dictionary<uint, uint?> { [GraphicsCatalog.SmoothMotion] = on ? 1u : 0u };
                var result = NvidiaProfiles.ForGameLive(program, [GraphicsCatalog.SmoothMotion], writes);
                if (!result.Ok) return result.Message ?? "Could not update the NVIDIA driver profile.";
                if (on && result.Skipped?.Contains(GraphicsCatalog.SmoothMotion) == true)
                    return "This NVIDIA driver does not support Smooth Motion (0xB0D384C0).";
            }

            settings.SetSmoothMotion(game.Id, on);
            return null;
        }, ct).ConfigureAwait(false);
    }

    private sealed record InspectedGame(
        bool Readable,
        bool IsWuWa,
        AntiCheatCheck.Answer AntiCheat,
        bool AllowsPlaced,
        bool ShipsDlss,
        bool ShipsFsr,
        bool ShipsXess,
        bool ShipsXessDll,
        DlssSwapService.FrameGenStatus? ModStatus);

    private static InspectedGame InspectGame(GameEntry game, DlssSwapService dlss, FrameGenRouter.FrameGenGpu gpu, CancellationToken ct)
    {
        var isWuWa = GameSettings.IsWutheringWavesEntry(game) || GameSettings.IsWutheringWaves(game);
        if (!game.Installed)
        {
            return new InspectedGame(false, isWuWa, default, false, false, false, false, false, null);
        }

        var status = dlss.GetStatus(game, refresh: false, consented: null, ct);
        var files = status.Rows;
        var shipsDlss = files.Any(r => r.Dll.Mod is null && r.Dll.FileName.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase));
        var shipsFsr = files.Any(r => r.Dll.Mod is null && r.Dll.FileName.Equals("amd_fidelityfx_framegeneration_dx12.dll", StringComparison.OrdinalIgnoreCase));
        var shipsXessDll = files.Any(r => r.Dll.Mod is null && r.Dll.FileName.Equals("libxess_fg.dll", StringComparison.OrdinalIgnoreCase));
        var modStatus = dlss.FrameGenState(game, gpu.Name, ct);
        var readable = !(modStatus.State == "hidden" && modStatus.Reason == "unread") || isWuWa;
        var antiCheat = AntiCheatCheck.Cached(game);
        var allowsPlaced = AntiCheatCheck.Allows(game, antiCheat, AntiCheatCheck.Change.FrameGenMod);

        return new InspectedGame(
            Readable: readable,
            IsWuWa: isWuWa,
            AntiCheat: antiCheat,
            AllowsPlaced: allowsPlaced,
            ShipsDlss: shipsDlss,
            ShipsFsr: shipsFsr,
            ShipsXess: shipsXessDll,
            ShipsXessDll: shipsXessDll,
            ModStatus: modStatus);
    }

    private static FrameGenRouter.FrameGenGpu ReadGpu()
    {
        var vendor = GraphicsService.Vendor();
        var gpuName = SystemSpecs.FrameGenGpu;
        var smoothDriver = vendor == "nvidia" && GraphicsService.KnownIds().Contains(GraphicsCatalog.SmoothMotion);
        var (afmfSupported, amdGpu, amdSoftware) = vendor == "amd" ? RadeonProfileService.Shared.Afmf() : (false, null, false);
        var intelSupported = vendor == "intel" && IntelProfileService.Shared.FrameGen("__probe__").Supported;
        return FrameGenRouter.FrameGenGpu.From(
            vendor: vendor,
            gpuName: amdGpu ?? gpuName,
            smoothMotionDriverSupported: smoothDriver,
            afmfDriverSupported: afmfSupported,
            amdSoftwareRunning: amdSoftware,
            intelOverrideDriverSupported: intelSupported);
    }

    private static string? ResolveGameExe(GameEntry game)
    {
        if (GameSettings.WutheringWavesProgram(game) is { } wuwaExe) return wuwaExe;
        if (string.IsNullOrWhiteSpace(game.Path)) return null;
        if (File.Exists(game.Path) && game.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return game.Path;
        if (!Directory.Exists(game.Path)) return null;
        try
        {
            var exes = new List<(string Path, long Size)>();
            CollectExes(game.Path, exes, 0);
            return DlssSwapService.GameProgram(exes, []);
        }
        catch
        {
            return null;
        }
    }

    private static void CollectExes(string dir, List<(string Path, long Size)> found, int depth)
    {
        if (depth > 5) return;
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(dir).EnumerateFiles("*.exe"); }
        catch { return; }
        foreach (var file in files)
        {
            if (GpuPreference.IsGameProgram(file.Name))
                found.Add((file.FullName, file.Length));
        }
        IEnumerable<DirectoryInfo> dirs;
        try { dirs = new DirectoryInfo(dir).EnumerateDirectories(); }
        catch { return; }
        foreach (var child in dirs)
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CollectExes(child.FullName, found, depth + 1);
        }
    }
}
