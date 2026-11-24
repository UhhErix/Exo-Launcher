using System.Globalization;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static class FrameGenRouter
{
    internal const string TierNative = "native";
    internal const string TierMod = "mod";
    internal const string TierDriver = "driver";
    internal const string TierUnavailable = "unavailable";

    internal const string RouteNativeDlss = "native_dlss";
    internal const string RouteNativeFsr = "native_fsr";
    internal const string RouteNativeXess = "native_xess";
    internal const string RouteModOptiScaler = "mod_optiscaler";
    internal const string RouteModDlssg = "mod_dlssg";
    internal const string RouteModMfg = "mod_mfg";
    internal const string RouteModOther = "mod_other";
    internal const string RouteDriverSmoothMotion = "driver_smooth_motion";
    internal const string RouteDriverAfmf = "driver_afmf";
    internal const string RouteDriverXessOverride = "driver_xess_override";
    internal const string RouteUnavailable = "unavailable";

    internal sealed record FrameGenGpu(
        string Vendor,
        string Name,
        int Series,
        bool SupportsDlssFg,
        bool SupportsMultiFrame,
        bool SupportsSmoothMotionHardware,
        bool SupportsSmoothMotionDriver,
        bool SupportsAfmfHardware,
        bool SupportsAfmfDriver,
        bool AmdSoftwareRunning,
        bool SupportsIntelOverrideDriver,
        bool SupportsFsrFg,
        bool SupportsFsrFgWuWa,
        bool SupportsXessFg,
        DlssSwapService.FrameGenCard? Card)
    {
        internal static FrameGenGpu From(
            string? vendor,
            string? gpuName,
            bool smoothMotionDriverSupported = false,
            bool afmfDriverSupported = false,
            bool amdSoftwareRunning = false,
            bool intelOverrideDriverSupported = false)
        {
            var rawName = gpuName?.Trim() ?? "";
            var upper = rawName.ToUpperInvariant();
            var resolvedVendor = NormalizeVendor(vendor, upper);
            var card = DlssSwapService.FrameGenCardFor(rawName);

            if (resolvedVendor == "nvidia")
            {
                var series = GpuProfile.NvidiaSeries(rawName) ?? card?.Series ?? 0;
                var isRtx40Plus = series >= 40;
                var isRtx20Plus = series >= 20;
                var isDx12FsrCapable = isRtx20Plus || series is 16 or 10;
                return new FrameGenGpu(
                    Vendor: "nvidia",
                    Name: rawName.Length > 0 ? rawName : "NVIDIA GPU",
                    Series: series,
                    SupportsDlssFg: isRtx40Plus,
                    SupportsMultiFrame: series >= 50,
                    SupportsSmoothMotionHardware: isRtx40Plus,
                    SupportsSmoothMotionDriver: isRtx40Plus && smoothMotionDriverSupported,
                    SupportsAfmfHardware: false,
                    SupportsAfmfDriver: false,
                    AmdSoftwareRunning: false,
                    SupportsIntelOverrideDriver: false,
                    SupportsFsrFg: isDx12FsrCapable,
                    SupportsFsrFgWuWa: isRtx20Plus,
                    SupportsXessFg: isRtx20Plus,
                    Card: card);
            }

            if (resolvedVendor == "amd")
            {
                var rxSeries = 0;
                if (FluidMotionFrames.Discrete().Match(upper) is { Success: true } rx)
                    rxSeries = int.Parse(rx.Groups[1].Value, CultureInfo.InvariantCulture);
                var afmfHw = rawName.Length > 0 && FluidMotionFrames.Runs(rawName);
                var isRx5000 = rxSeries is >= 5000 and < 6000;
                var supportsFsr = card is not null || afmfHw;
                var supportsFsrWuWa = afmfHw && !isRx5000;
                return new FrameGenGpu(
                    Vendor: "amd",
                    Name: rawName.Length > 0 ? rawName : "AMD Radeon GPU",
                    Series: rxSeries,
                    SupportsDlssFg: false,
                    SupportsMultiFrame: false,
                    SupportsSmoothMotionHardware: false,
                    SupportsSmoothMotionDriver: false,
                    SupportsAfmfHardware: afmfHw,
                    SupportsAfmfDriver: afmfHw && afmfDriverSupported,
                    AmdSoftwareRunning: amdSoftwareRunning,
                    SupportsIntelOverrideDriver: false,
                    SupportsFsrFg: supportsFsr,
                    SupportsFsrFgWuWa: supportsFsrWuWa,
                    SupportsXessFg: afmfHw,
                    Card: card);
            }

            if (resolvedVendor == "intel")
            {
                var isArc = card is { Vendor: "intel" } || upper.Contains("ARC", StringComparison.Ordinal);
                return new FrameGenGpu(
                    Vendor: "intel",
                    Name: rawName.Length > 0 ? rawName : "Intel GPU",
                    Series: 0,
                    SupportsDlssFg: false,
                    SupportsMultiFrame: false,
                    SupportsSmoothMotionHardware: false,
                    SupportsSmoothMotionDriver: false,
                    SupportsAfmfHardware: false,
                    SupportsAfmfDriver: false,
                    AmdSoftwareRunning: false,
                    SupportsIntelOverrideDriver: isArc && intelOverrideDriverSupported,
                    SupportsFsrFg: isArc,
                    SupportsFsrFgWuWa: isArc,
                    SupportsXessFg: isArc,
                    Card: card);
            }

            return new FrameGenGpu(
                Vendor: "unknown",
                Name: rawName.Length > 0 ? rawName : "This GPU",
                Series: 0,
                SupportsDlssFg: false,
                SupportsMultiFrame: false,
                SupportsSmoothMotionHardware: false,
                SupportsSmoothMotionDriver: false,
                SupportsAfmfHardware: false,
                SupportsAfmfDriver: false,
                AmdSoftwareRunning: false,
                SupportsIntelOverrideDriver: false,
                SupportsFsrFg: false,
                SupportsFsrFgWuWa: false,
                SupportsXessFg: false,
                Card: card);
        }

        private static string NormalizeVendor(string? vendor, string upperName)
        {
            if (string.Equals(vendor, "nvidia", StringComparison.OrdinalIgnoreCase)) return "nvidia";
            if (string.Equals(vendor, "amd", StringComparison.OrdinalIgnoreCase)) return "amd";
            if (string.Equals(vendor, "intel", StringComparison.OrdinalIgnoreCase)) return "intel";
            if (upperName.Contains("NVIDIA", StringComparison.Ordinal) || upperName.Contains("GEFORCE", StringComparison.Ordinal)
                || upperName.Contains("RTX", StringComparison.Ordinal) || upperName.Contains("GTX", StringComparison.Ordinal))
                return "nvidia";
            if (upperName.Contains("RADEON", StringComparison.Ordinal) || upperName.Contains("AMD", StringComparison.Ordinal))
                return "amd";
            if (upperName.Contains("INTEL", StringComparison.Ordinal) || upperName.Contains("ARC", StringComparison.Ordinal))
                return "intel";
            return "unknown";
        }
    }

    internal sealed record FrameGenGameInput(
        string GameId,
        string Title,
        bool Installed,
        bool Readable,
        bool IsWutheringWaves,
        bool IsRiot,
        AntiCheatCheck.Answer AntiCheat,
        bool AllowsPlacedMods,
        bool ShipsDlssFg,
        bool ShipsFsrFg,
        bool ShipsXessFg,
        bool ShipsXessFgDll,
        DlssSwapService.FrameGenStatus? ModStatus,
        FluidMotionFrames.Game FluidFacts,
        bool SmoothMotionEnabled = false,
        bool? AfmfOverride = null,
        string IntelXessOverride = "game",
        string? UserSelectedRoute = null,
        bool Dx12Mode = true);

    internal sealed record FrameGenRoute(
        string Id,
        string Tier,
        string Kind,
        string Label,
        string Status,
        string Summary,
        bool GpuSupported,
        string GpuNote,
        string AntiCheatVerdict,
        string AntiCheatNote,
        bool AutoEnable,
        string LatencyNote,
        bool Selectable,
        bool Active,
        string? Reason = null,
        string? HowToEnable = null,
        string? ChoiceValue = null,
        IReadOnlyList<string>? Choices = null);

    internal sealed record FrameGenDecision(
        string BestRouteId,
        string SelectedRouteId,
        string State,
        string Word,
        bool Good,
        bool Bad,
        string? Reason,
        string? HowToEnable,
        IReadOnlyList<FrameGenRoute> Routes,
        DlssSwapService.FrameGenStatus? LegacyMod = null);

    internal static FrameGenDecision Decide(FrameGenGpu gpu, FrameGenGameInput input)
    {
        if (!input.Installed)
        {
            var reason = "Install the game to check frame generation";
            var how = "Install this game, then open its page to see available frame generation routes.";
            var route = MakeUnavailable(gpu, input, reason, how, "unknown");
            return new FrameGenDecision(
                RouteUnavailable, RouteUnavailable, "unavailable", reason, false, false, reason, how, [route], input.ModStatus);
        }

        if (!input.Readable && !input.IsWutheringWaves)
        {
            var reason = "Could not check this game's files";
            var how = "Make sure the install folder is accessible and not locked by WindowsApps permissions.";
            var route = MakeUnavailable(gpu, input, reason, how, "unreadable");
            return new FrameGenDecision(
                RouteUnavailable, RouteUnavailable, "unread", "Could not check", false, true, reason, how, [route], input.ModStatus);
        }

        var routes = new List<FrameGenRoute>();
        var isCompetitive = FluidMotionFrames.IsCompetitive(input.FluidFacts);
        var apiUnsupported = HasUnsupportedLegacyApi(input.FluidFacts);
        var mod = input.ModStatus;
        var entry = new Models.GameEntry
        {
            Id = input.GameId,
            Title = input.Title,
            Store = input.IsRiot ? Models.StoreKind.Riot : Models.StoreKind.Steam,
        };
        var antiCheatLabel = input.IsRiot
            ? DlssSwapService.RiotVanguard
            : input.AntiCheat.Label ?? mod?.AntiCheat;
        var blocksMods = !input.AllowsPlacedMods || FrameGenAntiCheat.BlocksDllMods(entry, antiCheatLabel);
        var isOfflineAcMode = !blocksMods && (
            FrameGenAntiCheat.IsOfficialOfflineModeActive(entry, input.AntiCheat.Evidence) ||
            FrameGenAntiCheat.RuleFor(entry)?.Verdict == FrameGenAntiCheatVerdict.OfflineOfficialOnly);

        if (mod is { State: "on" or "broken" or "mod" } || (mod is { State: "note", Note: { Length: > 0 } noteText } && noteText.StartsWith("Coming out:", StringComparison.Ordinal)))
        {
            routes.Add(BuildActiveOrBrokenModRoute(gpu, input, mod, isOfflineAcMode, entry));
        }

        var shipsDlss = input.ShipsDlssFg || (input.IsWutheringWaves && input.Dx12Mode) || WikiHasTech(input.FluidFacts.Wiki, "dlss");
        var shipsFsr = input.ShipsFsrFg || (input.IsWutheringWaves && input.Dx12Mode) || WikiHasTech(input.FluidFacts.Wiki, "fsr");
        var shipsXess = input.ShipsXessFg || (input.IsWutheringWaves && input.Dx12Mode) || WikiHasTech(input.FluidFacts.Wiki, "xess");
        var shipsXessDll = input.ShipsXessFgDll || (input.IsWutheringWaves && input.Dx12Mode);

        AddNativeRoutes(routes, gpu, input, shipsDlss, shipsFsr, shipsXess);

        var hasUsableNative = routes.Any(r => r.Tier == TierNative);
        var hasActiveMod = routes.Any(r => r.Tier == TierMod);

        if (!hasActiveMod && !hasUsableNative && !blocksMods && !input.IsWutheringWaves && mod is { State: "off" })
        {
            routes.Add(BuildAvailableModRoute(gpu, input, mod, entry, isOfflineAcMode, allowAuto: !isOfflineAcMode));
            hasActiveMod = true;
        }

        AddDriverRoutes(routes, gpu, input, isCompetitive, apiUnsupported, hasUsableNative, shipsDlss, shipsXessDll);

        if (!hasActiveMod && !blocksMods && !hasUsableNative)
        {
            routes.Add(BuildAvailableModRoute(gpu, input, mod, entry, isOfflineAcMode, allowAuto: !isOfflineAcMode));
        }

        if (routes.Count == 0)
        {
            var (reason, how) = BuildUnavailableReason(gpu, input, entry, antiCheatLabel, blocksMods, isCompetitive, apiUnsupported, shipsDlss, shipsFsr, shipsXess);
            var verdict = blocksMods || input.AntiCheat.AssumeProtected ? "blocked" : "safe";
            var unavailable = MakeUnavailable(gpu, input, reason, how, verdict);
            return new FrameGenDecision(
                RouteUnavailable, RouteUnavailable, "unavailable", reason, false, false, reason, how, [unavailable], mod);
        }

        var best = routes[0];
        var selected = ResolveSelectedRoute(routes, input, best);
        var (state, word, good, bad) = SummarizeSelection(selected, routes, input, mod);
        return new FrameGenDecision(
            BestRouteId: best.Id,
            SelectedRouteId: selected.Id,
            State: state,
            Word: word,
            Good: good,
            Bad: bad,
            Reason: selected.Reason,
            HowToEnable: selected.HowToEnable,
            Routes: routes,
            LegacyMod: mod);
    }

    private static void AddNativeRoutes(
        List<FrameGenRoute> routes,
        FrameGenGpu gpu,
        FrameGenGameInput input,
        bool shipsDlss,
        bool shipsFsr,
        bool shipsXess)
    {
        var fsrSupported = input.IsWutheringWaves ? gpu.SupportsFsrFgWuWa : gpu.SupportsFsrFg;
        var dx12Hint = input.IsWutheringWaves ? " Requires DirectX 12 in Wuthering Waves." : "";
        var latency = "Lowest latency: uses game motion vectors and in-game Reflex, Anti-Lag, or XeLL." + dx12Hint;
        var acNote = "Safe: built into the game with no files placed or hooked.";

        void TryAddDlss()
        {
            if (!shipsDlss || !gpu.SupportsDlssFg) return;
            var label = gpu.SupportsMultiFrame ? "Built into the game (DLSS MFG)" : "Built into the game (DLSS)";
            var summary = gpu.SupportsMultiFrame ? "Built into the game · DLSS MFG" : "Built into the game · DLSS";
            routes.Add(new FrameGenRoute(
                Id: RouteNativeDlss,
                Tier: TierNative,
                Kind: RouteNativeDlss,
                Label: label,
                Status: "active",
                Summary: summary,
                GpuSupported: true,
                GpuNote: $"{gpu.Name} supports NVIDIA DLSS Frame Generation.",
                AntiCheatVerdict: "safe",
                AntiCheatNote: acNote,
                AutoEnable: false,
                LatencyNote: latency,
                Selectable: true,
                Active: !input.SmoothMotionEnabled));
        }

        void TryAddFsr()
        {
            if (!shipsFsr || !fsrSupported) return;
            routes.Add(new FrameGenRoute(
                Id: RouteNativeFsr,
                Tier: TierNative,
                Kind: RouteNativeFsr,
                Label: "Built into the game (FSR 3)",
                Status: "active",
                Summary: "Built into the game · FSR 3",
                GpuSupported: true,
                GpuNote: $"{gpu.Name} supports AMD FSR 3 Frame Generation.",
                AntiCheatVerdict: "safe",
                AntiCheatNote: acNote,
                AutoEnable: false,
                LatencyNote: latency,
                Selectable: true,
                Active: !input.SmoothMotionEnabled));
        }

        void TryAddXess()
        {
            if (!shipsXess || !gpu.SupportsXessFg) return;
            routes.Add(new FrameGenRoute(
                Id: RouteNativeXess,
                Tier: TierNative,
                Kind: RouteNativeXess,
                Label: "Built into the game (XeSS)",
                Status: "active",
                Summary: "Built into the game · XeSS",
                GpuSupported: true,
                GpuNote: $"{gpu.Name} supports Intel XeSS Frame Generation.",
                AntiCheatVerdict: "safe",
                AntiCheatNote: acNote,
                AutoEnable: false,
                LatencyNote: latency,
                Selectable: true,
                Active: !input.SmoothMotionEnabled));
        }

        if (gpu.Vendor == "nvidia")
        {
            TryAddDlss();
            TryAddFsr();
            TryAddXess();
        }
        else if (gpu.Vendor == "intel")
        {
            TryAddXess();
            TryAddFsr();
        }
        else
        {
            TryAddFsr();
            TryAddXess();
        }
    }

    private static FrameGenRoute BuildActiveOrBrokenModRoute(FrameGenGpu gpu, FrameGenGameInput input, DlssSwapService.FrameGenStatus mod, bool isOfflineAcMode, Models.GameEntry entry)
    {
        if (mod.State == "mod")
        {
            var modName = mod.Mod ?? "A mod";
            return new FrameGenRoute(
                Id: RouteModOther,
                Tier: TierMod,
                Kind: RouteModOther,
                Label: modName,
                Status: "active",
                Summary: modName,
                GpuSupported: true,
                GpuNote: $"{gpu.Name} runs the existing third-party frame generation mod.",
                AntiCheatVerdict: input.AllowsPlacedMods ? (isOfflineAcMode ? "offline" : "safe") : "blocked",
                AntiCheatNote: "Existing third-party mod in the game folder; Exo leaves it untouched.",
                AutoEnable: false,
                LatencyNote: "Uses in-game motion vectors; latency depends on the installed mod.",
                Selectable: false,
                Active: true);
        }

        var (id, defaultLabel) = mod.Path switch
        {
            "dlss" => (RouteModDlssg, "DLSSG SM86"),
            "mfg" => (RouteModMfg, "MFG Unlocker"),
            "optifg" => (RouteModOptiScaler, "OptiScaler FG"),
            _ => (RouteModOptiScaler, "OptiScaler"),
        };
        var label = mod.Version ?? mod.Package ?? defaultLabel;
        var broken = mod.State == "broken";
        var summary = broken
            ? mod.Reason switch
            {
                "replaced" => $"{mod.Mod ?? "Another mod"} replaced Exo's loader",
                "changed" => "Exo's files were changed",
                "gone" => "This game no longer has DLSS Frame Generation",
                _ => "Exo's files were removed",
            }
            : mod.Note ?? label;
        var acVerdict = !input.AllowsPlacedMods ? "blocked" : isOfflineAcMode ? "offline" : "safe";
        var acNote = !input.AllowsPlacedMods
            ? "Anti-cheat detected; Exo will remove placed files."
            : isOfflineAcMode
                ? FrameGenAntiCheat.OfflineModeNote(entry)
                : "Safe: no anti-cheat detected in this game.";
        return new FrameGenRoute(
            Id: id,
            Tier: TierMod,
            Kind: id,
            Label: label,
            Status: broken ? "broken" : "active",
            Summary: summary,
            GpuSupported: true,
            GpuNote: $"{gpu.Name} supports {label}.",
            AntiCheatVerdict: acVerdict,
            AntiCheatNote: acNote,
            AutoEnable: !broken && input.AllowsPlacedMods && !isOfflineAcMode,
            LatencyNote: "Low latency: hooks in-game motion vectors and pacing.",
            Selectable: input.AllowsPlacedMods && !broken,
            Active: !broken,
            Reason: broken ? summary : null);
    }

    private static FrameGenRoute BuildAvailableModRoute(FrameGenGpu gpu, FrameGenGameInput input, DlssSwapService.FrameGenStatus? mod, Models.GameEntry entry, bool isOfflineAcMode, bool allowAuto = true)
    {
        var path = mod?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = gpu.Vendor == "nvidia" && gpu.Series is 20 or 30 && input.ShipsDlssFg
                ? "dlss"
                : input.ShipsDlssFg
                    ? "fsr"
                    : "optifg";
        }
        var (id, defaultLabel) = path switch
        {
            "dlss" => (RouteModDlssg, "DLSSG SM86"),
            "mfg" => (RouteModMfg, "MFG Unlocker"),
            "optifg" => (RouteModOptiScaler, "OptiScaler FG"),
            _ => (RouteModOptiScaler, "OptiScaler"),
        };
        var label = mod?.Package ?? defaultLabel;
        var offlineNote = isOfflineAcMode ? FrameGenAntiCheat.OfflineModeNote(entry) : null;
        return new FrameGenRoute(
            Id: id,
            Tier: TierMod,
            Kind: id,
            Label: label,
            Status: "off",
            Summary: $"Off · {label} ready",
            GpuSupported: true,
            GpuNote: $"{gpu.Name} supports {label}.",
            AntiCheatVerdict: isOfflineAcMode ? "offline" : "safe",
            AntiCheatNote: offlineNote ?? "Safe: clean game with no anti-cheat (Placed files allowed).",
            AutoEnable: allowAuto && !isOfflineAcMode,
            LatencyNote: "Low latency: hooks in-game motion vectors and Reflex/FSR pacing.",
            Selectable: true,
            Active: false,
            HowToEnable: offlineNote);
    }

    private static void AddDriverRoutes(
        List<FrameGenRoute> routes,
        FrameGenGpu gpu,
        FrameGenGameInput input,
        bool isCompetitive,
        bool apiUnsupported,
        bool hasUsableNative,
        bool shipsDlss,
        bool shipsXessDll)
    {
        if (gpu.Vendor == "nvidia" && gpu.SupportsSmoothMotionHardware && gpu.SupportsSmoothMotionDriver && !apiUnsupported && !isCompetitive)
        {
            var is60Locked = input.FluidFacts.Wiki?.Over60 == false;
            var autoEnable = !hasUsableNative && is60Locked;
            var active = input.SmoothMotionEnabled;
            var conflictNote = shipsDlss && gpu.SupportsDlssFg
                ? " Turn off in-game DLSS Frame Generation when Smooth Motion is on."
                : "";
            routes.Add(new FrameGenRoute(
                Id: RouteDriverSmoothMotion,
                Tier: TierDriver,
                Kind: RouteDriverSmoothMotion,
                Label: "NVIDIA Smooth Motion",
                Status: active ? "active" : "available",
                Summary: active ? "NVIDIA Smooth Motion · On" : "NVIDIA Smooth Motion · Off",
                GpuSupported: true,
                GpuNote: $"{gpu.Name} supports NVIDIA Smooth Motion (DRS 0xB0D384C0).",
                AntiCheatVerdict: "safe",
                AntiCheatNote: "Safe: signed NVIDIA driver profile with zero game files modified.",
                AutoEnable: autoEnable,
                LatencyNote: "Adds ~1 frame of driver latency with no game motion vectors. Off for competitive games." + conflictNote,
                Selectable: true,
                Active: active));
        }

        if (gpu.Vendor == "amd" && gpu.SupportsAfmfHardware && gpu.SupportsAfmfDriver && !apiUnsupported && !isCompetitive)
        {
            var afmfDecision = FluidMotionFrames.Decide(gpu.Name, input.FluidFacts);
            var active = input.AfmfOverride ?? afmfDecision.On;
            var badSoftware = active && !gpu.AmdSoftwareRunning;
            var summary = !active
                ? "Fluid Motion Frames · Off"
                : badSoftware
                    ? "AMD Software isn't running"
                    : "Fluid Motion Frames · On";
            routes.Add(new FrameGenRoute(
                Id: RouteDriverAfmf,
                Tier: TierDriver,
                Kind: RouteDriverAfmf,
                Label: "AMD Fluid Motion Frames",
                Status: badSoftware ? "broken" : active ? "active" : "available",
                Summary: summary,
                GpuSupported: true,
                GpuNote: $"{gpu.Name} supports AMD Fluid Motion Frames 2 / 2.1.",
                AntiCheatVerdict: "safe",
                AntiCheatNote: "Safe: AMD Adrenalin driver feature with zero game files modified.",
                AutoEnable: !hasUsableNative && afmfDecision.On,
                LatencyNote: "Adds ~5–13 ms driver latency and pauses in fast flicks. Requires AMD Software running and in-game V-Sync off.",
                Selectable: true,
                Active: active,
                Reason: badSoftware ? "AMD Software isn't running" : null,
                HowToEnable: badSoftware ? "Open AMD Software: Adrenalin Edition so Fluid Motion Frames can activate." : null));
        }

        if (gpu.Vendor == "intel" && gpu.SupportsIntelOverrideDriver && shipsXessDll)
        {
            var choice = string.IsNullOrWhiteSpace(input.IntelXessOverride) ? "game" : input.IntelXessOverride;
            var active = !string.Equals(choice, "game", StringComparison.OrdinalIgnoreCase);
            routes.Add(new FrameGenRoute(
                Id: RouteDriverXessOverride,
                Tier: TierDriver,
                Kind: RouteDriverXessOverride,
                Label: "Intel XeSS-FG override",
                Status: active ? "active" : "available",
                Summary: active ? $"Intel XeSS override · {choice}" : "Intel XeSS override · Game's choice",
                GpuSupported: true,
                GpuNote: $"{gpu.Name} supports Intel XeSS multi-frame generation override (2x/3x/4x).",
                AntiCheatVerdict: "safe",
                AntiCheatNote: "Safe: Intel Graphics driver override with zero game files modified.",
                AutoEnable: false,
                LatencyNote: "Uses in-game XeSS-FG + XeLL motion vectors; only works in games shipping libxess_fg.dll.",
                Selectable: true,
                Active: active,
                ChoiceValue: choice,
                Choices: ["game", "2x", "3x", "4x"]));
        }
    }

    private static FrameGenRoute ResolveSelectedRoute(
        List<FrameGenRoute> routes,
        FrameGenGameInput input,
        FrameGenRoute best)
    {
        if (!string.IsNullOrWhiteSpace(input.UserSelectedRoute))
        {
            var explicitRoute = routes.FirstOrDefault(r => string.Equals(r.Id, input.UserSelectedRoute, StringComparison.OrdinalIgnoreCase));
            if (explicitRoute is not null) return explicitRoute;
        }
        if (input.SmoothMotionEnabled && routes.FirstOrDefault(r => r.Id == RouteDriverSmoothMotion) is { } sm)
            return sm;
        if (routes.FirstOrDefault(r => r.Id == RouteDriverXessOverride && r.Active) is { } xess)
            return xess;
        if (input.AfmfOverride == true && routes.FirstOrDefault(r => r.Id == RouteDriverAfmf) is { } afmf)
            return afmf;
        return best;
    }

    private static (string State, string Word, bool Good, bool Bad) SummarizeSelection(
        FrameGenRoute selected,
        List<FrameGenRoute> routes,
        FrameGenGameInput input,
        DlssSwapService.FrameGenStatus? mod)
    {
        if (selected.Status == "broken")
            return ("broken", selected.Summary, false, true);

        if (selected.Tier == TierNative)
        {
            if (routes.FirstOrDefault(r => r.Id == RouteDriverXessOverride && r.Active) is { } xess)
                return ("native", $"{selected.Summary} ({xess.ChoiceValue})", true, false);
            return ("native", selected.Summary, false, false);
        }

        if (selected.Tier == TierMod)
        {
            if (selected.Kind == RouteModOther) return ("mod", selected.Summary, false, false);
            return selected.Active
                ? ("on", selected.Summary, true, false)
                : ("off", selected.Summary, false, false);
        }

        if (selected.Tier == TierDriver)
            return ("driver", selected.Summary, selected.Active, false);

        return ("unavailable", selected.Summary, false, false);
    }

    private static (string Reason, string HowToEnable) BuildUnavailableReason(
        FrameGenGpu gpu,
        FrameGenGameInput input,
        Models.GameEntry entry,
        string? antiCheatLabel,
        bool blocksMods,
        bool isCompetitive,
        bool apiUnsupported,
        bool shipsDlss,
        bool shipsFsr,
        bool shipsXess)
    {
        if (blocksMods)
        {
            if (input.IsWutheringWaves && !input.Dx12Mode)
            {
                return (
                    FrameGenAntiCheat.OnlineBlockedMessage,
                    "Switch Wuthering Waves to DirectX 12 for built-in FSR 3 / XeSS frame generation. Anti-Cheat Expert blocks DLL mods.");
            }
            var verdict = FrameGenAntiCheat.Evaluate(entry, antiCheatLabel);
            if (verdict == FrameGenAntiCheatVerdict.OfflineOfficialOnly)
            {
                return (
                    FrameGenAntiCheat.OnlineBlockedMessage,
                    "Choose this game's official offline (without anti-cheat) launch mode to use frame generation mods.");
            }
            var acName = FrameGenAntiCheat.ReasonFor(entry, antiCheatLabel);
            return (
                FrameGenAntiCheat.OnlineBlockedMessage,
                $"{acName} blocks DLL mods; only built-in or signed driver frame generation can run safely here.");
        }

        if (isCompetitive)
        {
            return (
                "Off · Competitive game (adds input lag)",
                "Keep frame generation off in competitive games to avoid extra input latency.");
        }

        if (apiUnsupported)
        {
            var apis = input.FluidFacts.Wiki?.Apis ?? [];
            var apiText = apis.Count > 0 ? string.Join("/", apis).ToUpperInvariant() : "legacy DirectX";
            return (
                $"Not supported · {apiText}",
                "Frame generation requires DirectX 11, DirectX 12, Vulkan, or OpenGL.");
        }

        if (input.IsWutheringWaves && !input.Dx12Mode)
        {
            return (
                "Off · DirectX 12 required for built-in frame generation",
                "Switch Wuthering Waves to DirectX 12 for built-in frame generation.");
        }

        if (gpu.Vendor == "nvidia" && gpu.SupportsSmoothMotionHardware && !gpu.SupportsSmoothMotionDriver)
        {
            return (
                "Not available · Update NVIDIA driver for Smooth Motion",
                "Update to NVIDIA driver 581.08+ (RTX 40) or 572.16+ (RTX 50) to enable Smooth Motion.");
        }

        if (gpu.Vendor == "nvidia" && !gpu.SupportsSmoothMotionHardware)
        {
            var reason = shipsDlss
                ? $"Not supported on {gpu.Name} without a mod"
                : "Not built into this game";
            return (
                reason,
                $"NVIDIA Smooth Motion requires RTX 40/50, and this game has no supported mod route on {gpu.Name}.");
        }

        if (gpu.Vendor == "intel" && !shipsXess)
        {
            return (
                "Not built into this game",
                "Intel's XeSS-FG override only works in games with libxess_fg.dll.");
        }

        if (gpu.Vendor == "amd" && !gpu.SupportsAfmfHardware)
        {
            return (
                $"Not supported on {gpu.Name}",
                "AMD Fluid Motion Frames requires RX 6000+ or Radeon 700M/800M+.");
        }

        return (
            "Not supported in this game",
            "No built-in, mod, or driver frame generation route applies to this game.");
    }

    private static FrameGenRoute MakeUnavailable(
        FrameGenGpu gpu,
        FrameGenGameInput input,
        string reason,
        string howToEnable,
        string acVerdict)
    {
        var acNote = acVerdict switch
        {
            "blocked" => $"{input.AntiCheat.Name ?? "Anti-cheat"} blocks placed mods; only built-in or signed driver routes are safe.",
            "unreadable" => "Could not inspect the game's files or anti-cheat status.",
            _ => "No anti-cheat block.",
        };
        return new FrameGenRoute(
            Id: RouteUnavailable,
            Tier: TierUnavailable,
            Kind: RouteUnavailable,
            Label: "Not available",
            Status: "unavailable",
            Summary: reason,
            GpuSupported: false,
            GpuNote: gpu.Name,
            AntiCheatVerdict: acVerdict,
            AntiCheatNote: acNote,
            AutoEnable: false,
            LatencyNote: "Frame generation is off (no extra latency added).",
            Selectable: false,
            Active: false,
            Reason: reason,
            HowToEnable: howToEnable);
    }

    private static bool HasUnsupportedLegacyApi(FluidMotionFrames.Game facts) =>
        facts.Wiki?.Apis is { Count: > 0 } apis
        && !apis.Any(api => api is "dx11" or "dx12" or "vulkan" or "opengl");

    internal static int MultiplierFor(FrameGenDecision? decision)
    {
        if (decision is null) return 1;
        if (decision.State is "unavailable" or "unread" or "off") return 1;
        if (string.Equals(decision.SelectedRouteId, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision.SelectedRouteId, RouteUnavailable, StringComparison.OrdinalIgnoreCase))
            return 1;
        var route = decision.Routes.FirstOrDefault(r => string.Equals(r.Id, decision.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
        if (route is null || !route.Active) return 1;
        if (!string.IsNullOrWhiteSpace(route.ChoiceValue))
        {
            if (string.Equals(route.ChoiceValue, "4x", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(route.ChoiceValue, "3x", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(route.ChoiceValue, "2x", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(route.ChoiceValue, "game", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        return 2;
    }

    private static bool WikiHasTech(GameWiki.Facts? wiki, string needle)
    {
        if (wiki?.FrameGen != "true") return false;
        var tech = wiki.FrameGenTech;
        if (tech is not { Count: > 0 }) return false;
        return tech.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
