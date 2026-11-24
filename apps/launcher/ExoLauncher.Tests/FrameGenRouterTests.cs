using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Helpers.FrameGenRouter;

namespace ExoLauncher.Tests;

public sealed class FrameGenRouterTests
{
    private static readonly AntiCheatCheck.Answer NoAntiCheat = new(AntiCheatCheck.Kind.None, (string?)null, "clean");
    private static readonly AntiCheatCheck.Answer OfflineWithoutAc = new(AntiCheatCheck.Kind.None, (string?)null, AntiCheatCheck.WithoutAntiCheatEvidence);
    private static readonly AntiCheatCheck.Answer AceAntiCheat = new(AntiCheatCheck.Kind.Found, "Anti-Cheat Expert", "WuWa");
    private static readonly AntiCheatCheck.Answer EacAntiCheat = new(AntiCheatCheck.Kind.Found, "Easy Anti-Cheat", "EAC");
    private static readonly AntiCheatCheck.Answer VanguardAntiCheat = new(AntiCheatCheck.Kind.Found, "Riot Vanguard", "Riot");
    private static readonly AntiCheatCheck.Answer UnknownAntiCheat = new(AntiCheatCheck.Kind.Unknown, (string?)null, AntiCheatCheck.Unreadable);

    private static FrameGenGpu Rtx5080(bool smoothDriver = true) =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 5080", smoothMotionDriverSupported: smoothDriver);

    private static FrameGenGpu Rtx4070(bool smoothDriver = true) =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 4070", smoothMotionDriverSupported: smoothDriver);

    private static FrameGenGpu Rtx3070(bool smoothDriver = false) =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 3070", smoothMotionDriverSupported: smoothDriver);

    private static FrameGenGpu Gtx1660() =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce GTX 1660 Ti");

    private static FrameGenGpu Rx6700(bool afmfDriver = true, bool amdSoftware = true) =>
        FrameGenGpu.From("amd", "AMD Radeon RX 6700 XT", afmfDriverSupported: afmfDriver, amdSoftwareRunning: amdSoftware);

    private static FrameGenGpu Rx5700() =>
        FrameGenGpu.From("amd", "AMD Radeon RX 5700 XT", afmfDriverSupported: false, amdSoftwareRunning: true);

    private static FrameGenGpu ArcB580(bool intelDriver = true) =>
        FrameGenGpu.From("intel", "Intel(R) Arc(TM) B580 Graphics", intelOverrideDriverSupported: intelDriver);

    private static FluidMotionFrames.Game SinglePlayerFacts(string title, bool over60 = true, IReadOnlyList<string>? apis = null) =>
        new(title)
        {
            Tags = [4182],
            Wiki = new GameWiki.Facts(
                FrameGen: "",
                FrameGenTech: [],
                Apis: apis ?? ["dx12"],
                Over60: over60,
                Modes: ["singleplayer"],
                Genres: ["action rpg"]),
        };

    private static FrameGenGameInput WuWaInput(
        bool dx12Mode = true,
        bool smoothOn = false,
        string intelOverride = "game") =>
        new(
            GameId: "steam:3513350",
            Title: "Wuthering Waves",
            Installed: true,
            Readable: true,
            IsWutheringWaves: true,
            IsRiot: false,
            AntiCheat: AceAntiCheat,
            AllowsPlacedMods: false,
            ShipsDlssFg: true,
            ShipsFsrFg: true,
            ShipsXessFg: true,
            ShipsXessFgDll: true,
            ModStatus: new DlssSwapService.FrameGenStatus("native", Path: "dlss", Package: "DLSSG SM86", AntiCheat: "Anti-Cheat Expert", CanToggle: true, RiskWarning: AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert")),
            FluidFacts: SinglePlayerFacts("Wuthering Waves"),
            SmoothMotionEnabled: smoothOn,
            IntelXessOverride: intelOverride,
            Dx12Mode: dx12Mode);

    [Fact]
    public void WuWaOnRtx4070PrefersNativeConfigThenSmoothMotionWithOptInModFallback()
    {
        var decision = Decide(Rtx4070(), WuWaInput());

        Assert.NotEqual("hidden", decision.State);
        Assert.Equal(RouteNativeDlss, decision.BestRouteId);
        Assert.True(decision.Routes[0].AutoEnable);
        var ids = decision.Routes.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf(RouteDriverSmoothMotion) > ids.IndexOf(RouteNativeDlss));
        var mod = Assert.Single(decision.Routes, r => r.Tier == TierMod);
        Assert.True(mod.Selectable);
        Assert.False(mod.AutoEnable);
        Assert.True(mod.RequiresRiskConfirm);
        Assert.Equal(AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert"), mod.RiskWarning);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRtx3070PrefersNativeFsrConfigByDefaultWithOptInDlssgSm86BehindBanRiskConfirm()
    {
        var decision = Decide(Rtx3070(smoothDriver: true), WuWaInput());

        Assert.Equal(RouteNativeFsr, decision.BestRouteId);
        Assert.True(decision.Routes[0].AutoEnable);
        Assert.Contains(decision.Routes, r => r.Id == RouteNativeXess);
        Assert.DoesNotContain(decision.Routes, r => r.Id == RouteNativeDlss);
        Assert.DoesNotContain(decision.Routes, r => r.Id == RouteDriverSmoothMotion);
        var mod = Assert.Single(decision.Routes, r => r.Id == RouteModDlssg);
        Assert.True(mod.Selectable);
        Assert.False(mod.AutoEnable);
        Assert.True(mod.RequiresRiskConfirm);
        Assert.Equal(AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert"), mod.RiskWarning);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRtx3070Dx11OffersOptInModBehindBanRiskConfirmation()
    {
        var decision = Decide(Rtx3070(), WuWaInput(dx12Mode: false) with
        {
            ShipsDlssFg = false,
            ShipsFsrFg = false,
            ShipsXessFg = false,
            ShipsXessFgDll = false,
            ModStatus = new DlssSwapService.FrameGenStatus("off", Path: "optifg", Package: "OptiScaler FG", AntiCheat: "Anti-Cheat Expert", CanToggle: true, RiskWarning: AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert")),
        });

        Assert.Equal(RouteModOptiScaler, decision.BestRouteId);
        var mod = Assert.Single(decision.Routes, r => r.Tier == TierMod);
        Assert.True(mod.Selectable);
        Assert.False(mod.AutoEnable);
        Assert.True(mod.RequiresRiskConfirm);
        Assert.Equal(AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert"), mod.RiskWarning);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRx6700PrefersNativeFsrThenAfmfWithOptInModFallback()
    {
        var decision = Decide(Rx6700(), WuWaInput());

        Assert.Equal(RouteNativeFsr, decision.BestRouteId);
        Assert.True(decision.Routes[0].AutoEnable);
        var ids = decision.Routes.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf(RouteDriverAfmf) > ids.IndexOf(RouteNativeFsr));
        Assert.Contains(decision.Routes, r => r.Tier == TierMod && r.RequiresRiskConfirm && !r.AutoEnable);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnArcPrefersNativeXessWithIntelOverrideAndOptInModFallback()
    {
        var decision = Decide(ArcB580(), WuWaInput(intelOverride: "2x"));

        Assert.Equal(RouteNativeXess, decision.BestRouteId);
        var xessOverride = Assert.Single(decision.Routes, r => r.Id == RouteDriverXessOverride);
        Assert.Equal("2x", xessOverride.ChoiceValue);
        Assert.Equal(["game", "2x", "3x", "4x"], xessOverride.Choices);
        Assert.Contains(decision.Routes, r => r.Tier == TierMod && r.RequiresRiskConfirm && !r.AutoEnable);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRx5700AndGtx1660OffersOptInModBehindBanRiskConfirm()
    {
        var rx5700 = Decide(Rx5700(), WuWaInput());
        Assert.Contains(rx5700.Routes, r => r.Tier == TierMod && r.RequiresRiskConfirm && !r.AutoEnable);

        var gtx = Decide(Gtx1660(), WuWaInput());
        var mod = Assert.Single(gtx.Routes, r => r.Tier == TierMod);
        Assert.True(mod.RequiresRiskConfirm);
        Assert.False(mod.AutoEnable);
        Assert.Equal(AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert"), mod.RiskWarning);
        AssertAllRouteMetadataPresent(gtx);
    }

    [Fact]
    public void TierBOptiScalerAutoEnablesOnlyOnCleanGamesAndDisallowsUnlistedAntiCheatGames()
    {
        var cleanGame = new FrameGenGameInput(
            GameId: "steam:1091500",
            Title: "Cyberpunk 2077",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: NoAntiCheat,
            AllowsPlacedMods: true,
            ShipsDlssFg: true,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("off", Path: "fsr"),
            FluidFacts: SinglePlayerFacts("Cyberpunk 2077"));

        var cleanDecision = Decide(Rtx3070(), cleanGame);
        Assert.Equal(RouteModOptiScaler, cleanDecision.BestRouteId);
        var modRoute = Assert.Single(cleanDecision.Routes, r => r.Id == RouteModOptiScaler);
        Assert.True(modRoute.AutoEnable);
        Assert.Equal("safe", modRoute.AntiCheatVerdict);

        var acGame = cleanGame with
        {
            AntiCheat = EacAntiCheat,
            AllowsPlacedMods = false,
            ModStatus = new DlssSwapService.FrameGenStatus("note", AntiCheat: "Easy Anti-Cheat", Note: FrameGenAntiCheat.OnlineBlockedMessage),
        };
        var acDecision = Decide(Rtx3070(), acGame);
        Assert.Equal(RouteUnavailable, acDecision.BestRouteId);
        Assert.Equal(FrameGenAntiCheat.OnlineBlockedMessage, acDecision.Word);
        Assert.DoesNotContain(acDecision.Routes, r => r.Tier == TierMod);
        AssertAllRouteMetadataPresent(acDecision);
    }

    [Fact]
    public void TierCDriverSmoothMotionGatedToRtx40PlusAndOfflineAcRequiresOfficialOfflineModeOnRtx30()
    {
        var eldenRing = new FrameGenGameInput(
            GameId: "steam:1245620",
            Title: "ELDEN RING",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: EacAntiCheat,
            AllowsPlacedMods: false,
            ShipsDlssFg: false,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("note", AntiCheat: "Easy Anti-Cheat", Note: FrameGenAntiCheat.OnlineBlockedMessage),
            FluidFacts: SinglePlayerFacts("ELDEN RING", over60: false));

        var rtx4070Supported = Decide(Rtx4070(smoothDriver: true), eldenRing);
        Assert.Equal(RouteDriverSmoothMotion, rtx4070Supported.BestRouteId);
        Assert.True(rtx4070Supported.Routes[0].AutoEnable);
        Assert.DoesNotContain(rtx4070Supported.Routes, r => r.Tier == TierMod);

        var rtx5080Supported = Decide(Rtx5080(smoothDriver: true), eldenRing);
        Assert.Equal(RouteDriverSmoothMotion, rtx5080Supported.BestRouteId);
        Assert.DoesNotContain(rtx5080Supported.Routes, r => r.Tier == TierMod);

        var rtx3070ActiveEac = Decide(Rtx3070(smoothDriver: true), eldenRing);
        Assert.DoesNotContain(rtx3070ActiveEac.Routes, r => r.Id == RouteDriverSmoothMotion);
        Assert.DoesNotContain(rtx3070ActiveEac.Routes, r => r.Tier == TierMod);
        Assert.Equal(RouteUnavailable, rtx3070ActiveEac.BestRouteId);
        Assert.Contains("official offline", rtx3070ActiveEac.HowToEnable ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TierCDriverAfmfAndIntelXessOverrideGating()
    {
        var eldenRing = new FrameGenGameInput(
            GameId: "steam:1245620",
            Title: "ELDEN RING",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: EacAntiCheat,
            AllowsPlacedMods: false,
            ShipsDlssFg: false,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("note", AntiCheat: "Easy Anti-Cheat", Note: FrameGenAntiCheat.OnlineBlockedMessage),
            FluidFacts: SinglePlayerFacts("ELDEN RING", over60: false));

        var amdOn = Decide(Rx6700(amdSoftware: true), eldenRing);
        Assert.Equal(RouteDriverAfmf, amdOn.BestRouteId);
        Assert.True(amdOn.Routes[0].AutoEnable);
        Assert.True(amdOn.Good);
        Assert.DoesNotContain(amdOn.Routes, r => r.Tier == TierMod);

        var amdNoSoftware = Decide(Rx6700(amdSoftware: false), eldenRing);
        Assert.Equal(RouteDriverAfmf, amdNoSoftware.BestRouteId);
        Assert.True(amdNoSoftware.Bad);
        Assert.Contains("AMD Software", amdNoSoftware.Word, StringComparison.Ordinal);

        var arcNoXessDll = Decide(ArcB580(), eldenRing);
        Assert.DoesNotContain(arcNoXessDll.Routes, r => r.Id == RouteDriverXessOverride);
        Assert.DoesNotContain(arcNoXessDll.Routes, r => r.Tier == TierMod);
        Assert.Equal(RouteUnavailable, arcNoXessDll.BestRouteId);

        var arcWithXessDll = Decide(ArcB580(), eldenRing with { ShipsXessFg = true, ShipsXessFgDll = true });
        Assert.Contains(arcWithXessDll.Routes, r => r.Id == RouteDriverXessOverride);
        Assert.DoesNotContain(arcWithXessDll.Routes, r => r.Tier == TierMod);
    }

    [Fact]
    public void ValorantAndUnknownAntiCheatShowModsWouldGetYouBannedWhileOfflineAntiCheatWorksInOfficialOfflineMode()
    {
        var riotGame = new FrameGenGameInput(
            GameId: "riot:valorant",
            Title: "VALORANT",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: true,
            AntiCheat: VanguardAntiCheat,
            AllowsPlacedMods: false,
            ShipsDlssFg: false,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: null,
            FluidFacts: new FluidMotionFrames.Game("VALORANT"));

        var riotDecision = Decide(Rtx4070(), riotGame);
        Assert.Equal(RouteUnavailable, riotDecision.BestRouteId);
        Assert.Equal(FrameGenAntiCheat.OnlineBlockedMessage, riotDecision.Word);
        Assert.DoesNotContain(riotDecision.Routes, r => r.Tier == TierMod);

        var unknownAcGame = new FrameGenGameInput(
            GameId: "steam:9999992",
            Title: "Mystery Online Shooter",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: UnknownAntiCheat,
            AllowsPlacedMods: false,
            ShipsDlssFg: true,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: null,
            FluidFacts: SinglePlayerFacts("Mystery Online Shooter"));

        var unknownDecision = Decide(Rtx3070(), unknownAcGame);
        Assert.Equal(RouteUnavailable, unknownDecision.BestRouteId);
        Assert.Equal(FrameGenAntiCheat.OnlineBlockedMessage, unknownDecision.Word);
        Assert.DoesNotContain(unknownDecision.Routes, r => r.Tier == TierMod);

        var nbaOffline = new FrameGenGameInput(
            GameId: "steam:3472040",
            Title: "NBA 2K26",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: OfflineWithoutAc,
            AllowsPlacedMods: true,
            ShipsDlssFg: false,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("off", Path: "optifg", Package: "OptiScaler FG", CanToggle: true),
            FluidFacts: SinglePlayerFacts("NBA 2K26"));

        var offlineDecision = Decide(Rtx3070(), nbaOffline);
        Assert.Equal(RouteModOptiScaler, offlineDecision.BestRouteId);
        var offlineRoute = Assert.Single(offlineDecision.Routes, r => r.Id == RouteModOptiScaler);
        Assert.True(offlineRoute.Selectable);
        Assert.False(offlineRoute.AutoEnable);
        Assert.False(offlineRoute.RequiresRiskConfirm);
        Assert.Equal("offline", offlineRoute.AntiCheatVerdict);
        Assert.Equal(AntiCheatClass.OfflineOneTimeNote("NBA 2K26"), offlineRoute.AntiCheatNote);

        var dx9Game = new FrameGenGameInput(
            GameId: "steam:220",
            Title: "Half-Life 2",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: NoAntiCheat,
            AllowsPlacedMods: true,
            ShipsDlssFg: false,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("off", Path: "optifg", Package: "OptiScaler FG", CanToggle: true),
            FluidFacts: SinglePlayerFacts("Half-Life 2", apis: ["dx9"]));

        var dx9Decision = Decide(Rtx4070(), dx9Game);
        Assert.Equal(RouteModOptiScaler, dx9Decision.BestRouteId);
        Assert.All(dx9Decision.Routes, r => Assert.DoesNotContain("Lossless Scaling", r.HowToEnable ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public void ExoFlowUsesSharedAntiCheatClassForAllFourClassesAndNeverSuggestsLossless()
    {
        var unknownAc = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, (string?)null, "unknown");
        var baseInput = new FrameGenGameInput(
            GameId: "steam:1245620",
            Title: "ELDEN RING",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: EacAntiCheat,
            AllowsPlacedMods: false,
            ShipsDlssFg: false,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("hidden"),
            FluidFacts: SinglePlayerFacts("ELDEN RING", over60: false),
            ExoFlowAvailable: true);

        var offlineAc = Decide(Rtx3070(), baseInput);
        AssertAllRouteMetadataPresent(offlineAc);
        Assert.Equal(RouteExternalExoFlow, offlineAc.BestRouteId);
        Assert.Equal("safe", offlineAc.Routes[0].AntiCheatVerdict);
        Assert.True(offlineAc.Routes[0].Selectable);
        Assert.DoesNotContain(offlineAc.Routes, r => r.Id == "external_lossless_scaling");

        var officialOfflineAc = Decide(Rtx3070(), baseInput with
        {
            GameId = "steam:3472040",
            Title = "NBA 2K26",
            AntiCheat = OfflineWithoutAc,
            FluidFacts = SinglePlayerFacts("NBA 2K26", over60: false),
        });
        Assert.Contains(officialOfflineAc.Routes, r => r.Id == RouteExternalExoFlow);

        var activeOffline = Decide(Rtx3070(), baseInput with { ExoFlowEnabled = true });
        Assert.Equal(RouteExternalExoFlow, activeOffline.SelectedRouteId);
        Assert.Equal("external", activeOffline.State);
        Assert.True(activeOffline.Good);
        Assert.Equal("Exo Flow · On", activeOffline.Word);

        var onlinePve = Decide(Rtx3070(), baseInput with
        {
            GameId = "steam:2074920",
            Title = "The First Descendant",
            AntiCheat = EacAntiCheat,
            FluidFacts = SinglePlayerFacts("The First Descendant", over60: false),
        });
        AssertAllRouteMetadataPresent(onlinePve);
        Assert.Contains(onlinePve.Routes, r => r.Id == RouteExternalExoFlow);
        var pveRoute = Assert.Single(onlinePve.Routes, r => r.Id == RouteExternalExoFlow);
        Assert.Equal("safe", pveRoute.AntiCheatVerdict);

        var noAntiCheat = Decide(Rtx3070(), baseInput with
        {
            GameId = "steam:1091500",
            Title = "Cyberpunk 2077",
            AntiCheat = NoAntiCheat,
            AllowsPlacedMods = true,
            FluidFacts = SinglePlayerFacts("Cyberpunk 2077", over60: false),
        });
        AssertAllRouteMetadataPresent(noAntiCheat);
        Assert.Contains(noAntiCheat.Routes, r => r.Id == RouteExternalExoFlow);

        var competitivePvpEac = Decide(Rtx3070(), baseInput with
        {
            GameId = "steam:1172470",
            Title = "Apex Legends",
            AntiCheat = EacAntiCheat,
            FluidFacts = SinglePlayerFacts("Apex Legends", over60: false),
        });
        Assert.Equal(RouteUnavailable, competitivePvpEac.BestRouteId);
        Assert.DoesNotContain(competitivePvpEac.Routes, r => r.Id == RouteExternalExoFlow);

        var unlistedEacFailsClosed = Decide(Rtx3070(), baseInput with
        {
            GameId = "steam:9999991",
            Title = "Mystery EAC Shooter",
            AntiCheat = EacAntiCheat,
            FluidFacts = SinglePlayerFacts("Mystery EAC Shooter", over60: false),
        });
        Assert.Equal(RouteUnavailable, unlistedEacFailsClosed.BestRouteId);
        Assert.DoesNotContain(unlistedEacFailsClosed.Routes, r => r.Id == RouteExternalExoFlow);

        var unknownAntiCheat = Decide(Rtx3070(), baseInput with
        {
            GameId = "steam:9999992",
            Title = "Uninspected Online Game",
            AntiCheat = unknownAc,
            FluidFacts = SinglePlayerFacts("Uninspected Online Game", over60: false),
        });
        Assert.Equal(RouteUnavailable, unknownAntiCheat.BestRouteId);
        Assert.DoesNotContain(unknownAntiCheat.Routes, r => r.Id == RouteExternalExoFlow);

        var riotGame = Decide(Rtx3070(), baseInput with
        {
            GameId = "riot:valorant",
            Title = "VALORANT",
            IsRiot = true,
            AntiCheat = VanguardAntiCheat,
        });
        Assert.Equal(RouteUnavailable, riotGame.BestRouteId);
        Assert.DoesNotContain(riotGame.Routes, r => r.Id == RouteExternalExoFlow);
    }

    [Fact]
    public void ExoFgBridgeReplacesDlssgSm86OnRtx20And30UnderSharedAntiCheatClass()
    {
        var rtx2080 = FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 2080 Ti");
        var unknownAc = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, (string?)null, "unknown");
        var baseDlssgGame = new FrameGenGameInput(
            GameId: "steam:814380",
            Title: "Sekiro: Shadows Die Twice",
            Installed: true,
            Readable: true,
            IsWutheringWaves: false,
            IsRiot: false,
            AntiCheat: NoAntiCheat,
            AllowsPlacedMods: true,
            ShipsDlssFg: true,
            ShipsFsrFg: false,
            ShipsXessFg: false,
            ShipsXessFgDll: false,
            ModStatus: new DlssSwapService.FrameGenStatus("off", Path: "dlss"),
            FluidFacts: SinglePlayerFacts("Sekiro: Shadows Die Twice"),
            ExoFgBridgeAvailable: true);

        var rtx3070Clean = Decide(Rtx3070(), baseDlssgGame);
        AssertAllRouteMetadataPresent(rtx3070Clean);
        Assert.Equal(RouteModExoFgBridge, rtx3070Clean.BestRouteId);
        Assert.DoesNotContain(rtx3070Clean.Routes, r => r.Id == RouteModDlssg);
        Assert.False(rtx3070Clean.Routes[0].RequiresRiskConfirm);

        var rtx2080Clean = Decide(rtx2080, baseDlssgGame);
        Assert.Equal(RouteModExoFgBridge, rtx2080Clean.BestRouteId);

        var offlineMode = Decide(Rtx3070(), baseDlssgGame with
        {
            GameId = "steam:3472040",
            Title = "NBA 2K26",
            AntiCheat = OfflineWithoutAc,
            FluidFacts = SinglePlayerFacts("NBA 2K26"),
        });
        Assert.Equal(RouteModExoFgBridge, offlineMode.BestRouteId);
        Assert.Equal("offline", offlineMode.Routes[0].AntiCheatVerdict);
        Assert.Equal(AntiCheatClass.OfflineOneTimeNote("NBA 2K26"), offlineMode.Routes[0].AntiCheatNote);

        var pveOptIn = Decide(Rtx3070(), baseDlssgGame with
        {
            GameId = "steam:2074920",
            Title = "The First Descendant",
            AntiCheat = EacAntiCheat,
            AllowsPlacedMods = false,
            FluidFacts = SinglePlayerFacts("The First Descendant"),
        });
        var pveRoute = Assert.Single(pveOptIn.Routes, r => r.Id == RouteModExoFgBridge);
        Assert.True(pveRoute.RequiresRiskConfirm);
        Assert.Equal("confirm", pveRoute.AntiCheatVerdict);

        var pvpBlocked = Decide(Rtx3070(), baseDlssgGame with
        {
            GameId = "steam:1172470",
            Title = "Apex Legends",
            AntiCheat = EacAntiCheat,
            AllowsPlacedMods = false,
            FluidFacts = SinglePlayerFacts("Apex Legends"),
        });
        Assert.DoesNotContain(pveOptIn.Routes, r => r.Id == RouteModDlssg);
        Assert.DoesNotContain(pvpBlocked.Routes, r => r.Id == RouteModExoFgBridge);

        var unknownBlocked = Decide(Rtx3070(), baseDlssgGame with
        {
            GameId = "steam:9999998",
            Title = "Mystery Online Title",
            AntiCheat = unknownAc,
            AllowsPlacedMods = false,
            FluidFacts = SinglePlayerFacts("Mystery Online Title"),
        });
        Assert.DoesNotContain(unknownBlocked.Routes, r => r.Id == RouteModExoFgBridge);
    }

    private static void AssertAllRouteMetadataPresent(FrameGenDecision decision)
    {
        Assert.NotEmpty(decision.Word);
        Assert.NotEqual("hidden", decision.State);
        Assert.NotEmpty(decision.Routes);
        foreach (var route in decision.Routes)
        {
            Assert.False(string.IsNullOrWhiteSpace(route.GpuNote), route.Id + " missing GpuNote");
            Assert.False(string.IsNullOrWhiteSpace(route.AntiCheatVerdict), route.Id + " missing AntiCheatVerdict");
            Assert.False(string.IsNullOrWhiteSpace(route.AntiCheatNote), route.Id + " missing AntiCheatNote");
            Assert.False(string.IsNullOrWhiteSpace(route.LatencyNote), route.Id + " missing LatencyNote");
        }
    }
}
