using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Helpers.FrameGenRouter;

namespace ExoLauncher.Tests;

public sealed class FrameGenRouterTests
{
    private static readonly AntiCheatCheck.Answer NoAntiCheat = new(AntiCheatCheck.Kind.None, (string?)null, "clean");
    private static readonly AntiCheatCheck.Answer AceAntiCheat = new(AntiCheatCheck.Kind.Found, "Anti-Cheat Expert", "WuWa");
    private static readonly AntiCheatCheck.Answer EacAntiCheat = new(AntiCheatCheck.Kind.Found, "Easy Anti-Cheat", "EAC");
    private static readonly AntiCheatCheck.Answer VanguardAntiCheat = new(AntiCheatCheck.Kind.Found, "Riot Vanguard", "Riot");

    private static FrameGenGpu Rtx3070(bool smoothDriver = false) =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 3070", smoothMotionDriverSupported: smoothDriver);

    private static FrameGenGpu Rtx4070(bool smoothDriver = true) =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 4070", smoothMotionDriverSupported: smoothDriver);

    private static FrameGenGpu Rtx5080(bool smoothDriver = true) =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce RTX 5080", smoothMotionDriverSupported: smoothDriver);

    private static FrameGenGpu Gtx1660() =>
        FrameGenGpu.From("nvidia", "NVIDIA GeForce GTX 1660 Super");

    private static FrameGenGpu Rx6700(bool amdSoftware = true) =>
        FrameGenGpu.From("amd", "AMD Radeon RX 6700 XT", afmfDriverSupported: true, amdSoftwareRunning: amdSoftware);

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
        bool lsInstalled = true,
        bool smoothOn = false,
        bool dx12Mode = true,
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
            ModStatus: new DlssSwapService.FrameGenStatus("note", AntiCheat: "Anti-Cheat Expert", Note: "This game uses Anti-Cheat Expert, so Exo puts nothing into it."),
            FluidFacts: SinglePlayerFacts("Wuthering Waves"),
            SmoothMotionEnabled: smoothOn,
            IntelXessOverride: intelOverride,
            LosslessScalingInstalled: lsInstalled,
            Dx12Mode: dx12Mode);

    [Fact]
    public void WuWaOnRtx4070PrefersNativeDlssThenSmoothMotionThenLosslessAndBlocksMods()
    {
        var decision = Decide(Rtx4070(), WuWaInput(lsInstalled: true));

        Assert.NotEqual("hidden", decision.State);
        Assert.Equal(RouteNativeDlss, decision.BestRouteId);
        Assert.Contains("DLSS", decision.Word, StringComparison.Ordinal);
        Assert.DoesNotContain(decision.Routes, r => r.Tier == TierMod);
        var ids = decision.Routes.Select(r => r.Id).ToList();
        var dlssIdx = ids.IndexOf(RouteNativeDlss);
        var smIdx = ids.IndexOf(RouteDriverSmoothMotion);
        var lsIdx = ids.IndexOf(RouteExternalLossless);
        Assert.True(dlssIdx >= 0 && smIdx > dlssIdx && lsIdx > smIdx);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRtx3070UsesNativeFsrThenLosslessAndNeverOffersSmoothMotionOrMods()
    {
        var decision = Decide(Rtx3070(smoothDriver: true), WuWaInput(lsInstalled: true));

        Assert.Equal(RouteNativeFsr, decision.BestRouteId);
        Assert.DoesNotContain(decision.Routes, r => r.Id == RouteNativeDlss);
        Assert.DoesNotContain(decision.Routes, r => r.Id == RouteDriverSmoothMotion);
        Assert.DoesNotContain(decision.Routes, r => r.Tier == TierMod);
        Assert.Contains(decision.Routes, r => r.Id == RouteExternalLossless);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRtx3070Dx11FallsBackToLosslessScalingWhenInstalled()
    {
        var decision = Decide(Rtx3070(), WuWaInput(lsInstalled: true, dx12Mode: false) with
        {
            ShipsDlssFg = false,
            ShipsFsrFg = false,
            ShipsXessFg = false,
            ShipsXessFgDll = false,
        });

        Assert.Equal(RouteExternalLossless, decision.BestRouteId);
        Assert.DoesNotContain(decision.Routes, r => r.Tier == TierMod);
        Assert.DoesNotContain(decision.Routes, r => r.Id == RouteDriverSmoothMotion);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRx6700PrefersNativeFsrThenAfmfThenLosslessAndBlocksMods()
    {
        var decision = Decide(Rx6700(), WuWaInput(lsInstalled: true));

        Assert.Equal(RouteNativeFsr, decision.BestRouteId);
        Assert.DoesNotContain(decision.Routes, r => r.Tier == TierMod);
        var ids = decision.Routes.Select(r => r.Id).ToList();
        var fsrIdx = ids.IndexOf(RouteNativeFsr);
        var afmfIdx = ids.IndexOf(RouteDriverAfmf);
        var lsIdx = ids.IndexOf(RouteExternalLossless);
        Assert.True(fsrIdx >= 0 && afmfIdx > fsrIdx && lsIdx > afmfIdx);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnArcPrefersNativeXessWithIntelOverrideThenLosslessAndBlocksMods()
    {
        var decision = Decide(ArcB580(), WuWaInput(lsInstalled: true, intelOverride: "2x"));

        Assert.Equal(RouteNativeXess, decision.BestRouteId);
        Assert.DoesNotContain(decision.Routes, r => r.Tier == TierMod);
        var xessOverride = Assert.Single(decision.Routes, r => r.Id == RouteDriverXessOverride);
        Assert.Equal("2x", xessOverride.ChoiceValue);
        Assert.Equal(["game", "2x", "3x", "4x"], xessOverride.Choices);
        Assert.Contains(decision.Routes, r => r.Id == RouteExternalLossless);
        AssertAllRouteMetadataPresent(decision);
    }

    [Fact]
    public void WuWaOnRx5700AndGtx1660ExcludesFsrFgAndUsesLosslessOrExplainsWhyUnavailable()
    {
        var rx5700WithLs = Decide(Rx5700(), WuWaInput(lsInstalled: true));
        Assert.Equal(RouteExternalLossless, rx5700WithLs.BestRouteId);

        var gtxNoLs = Decide(Gtx1660(), WuWaInput(lsInstalled: false));
        Assert.Equal(RouteUnavailable, gtxNoLs.BestRouteId);
        Assert.Equal("unavailable", gtxNoLs.State);
        Assert.Contains("Anti-Cheat Expert", gtxNoLs.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("Lossless Scaling", gtxNoLs.HowToEnable ?? "", StringComparison.Ordinal);
        AssertAllRouteMetadataPresent(gtxNoLs);
    }

    [Fact]
    public void TierBOptiScalerModOfferedOnlyWhenAntiCheatAllowsPlacedFiles()
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
            FluidFacts: SinglePlayerFacts("Cyberpunk 2077"),
            LosslessScalingInstalled: true);

        var cleanDecision = Decide(Rtx3070(), cleanGame);
        Assert.Equal(RouteModOptiScaler, cleanDecision.BestRouteId);
        var modRoute = Assert.Single(cleanDecision.Routes, r => r.Id == RouteModOptiScaler);
        Assert.True(modRoute.AutoEnable);
        Assert.Equal("safe", modRoute.AntiCheatVerdict);

        var acGame = cleanGame with
        {
            AntiCheat = EacAntiCheat,
            AllowsPlacedMods = false,
            LosslessScalingInstalled = false,
        };
        var blockedDecision = Decide(Rtx3070(), acGame);
        Assert.DoesNotContain(blockedDecision.Routes, r => r.Tier == TierMod);
        Assert.Equal(RouteUnavailable, blockedDecision.BestRouteId);
        Assert.Contains("Easy Anti-Cheat", blockedDecision.Reason ?? "", StringComparison.Ordinal);
        AssertAllRouteMetadataPresent(blockedDecision);
    }

    [Fact]
    public void TierCDriverSmoothMotionGatedToRtx40PlusAndDriverSupport()
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
            ModStatus: new DlssSwapService.FrameGenStatus("hidden"),
            FluidFacts: SinglePlayerFacts("ELDEN RING", over60: false),
            LosslessScalingInstalled: false);

        var rtx4070Supported = Decide(Rtx4070(smoothDriver: true), eldenRing);
        Assert.Equal(RouteDriverSmoothMotion, rtx4070Supported.BestRouteId);
        Assert.True(rtx4070Supported.Routes[0].AutoEnable);

        var rtx5080Supported = Decide(Rtx5080(smoothDriver: true), eldenRing);
        Assert.Equal(RouteDriverSmoothMotion, rtx5080Supported.BestRouteId);

        var rtx4070OldDriver = Decide(Rtx4070(smoothDriver: false), eldenRing);
        Assert.Equal(RouteUnavailable, rtx4070OldDriver.BestRouteId);
        Assert.Contains("581.08", rtx4070OldDriver.HowToEnable ?? "", StringComparison.Ordinal);

        var rtx3070Never = Decide(Rtx3070(smoothDriver: true), eldenRing);
        Assert.DoesNotContain(rtx3070Never.Routes, r => r.Id == RouteDriverSmoothMotion);
        Assert.Equal(RouteUnavailable, rtx3070Never.BestRouteId);
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
            ModStatus: new DlssSwapService.FrameGenStatus("hidden"),
            FluidFacts: SinglePlayerFacts("ELDEN RING", over60: false),
            LosslessScalingInstalled: false);

        var amdOn = Decide(Rx6700(amdSoftware: true), eldenRing);
        Assert.Equal(RouteDriverAfmf, amdOn.BestRouteId);
        Assert.True(amdOn.Routes[0].AutoEnable);
        Assert.True(amdOn.Good);

        var amdNoSoftware = Decide(Rx6700(amdSoftware: false), eldenRing);
        Assert.Equal(RouteDriverAfmf, amdNoSoftware.BestRouteId);
        Assert.True(amdNoSoftware.Bad);
        Assert.Contains("AMD Software", amdNoSoftware.Word, StringComparison.Ordinal);

        var arcNoXessDll = Decide(ArcB580(), eldenRing);
        Assert.DoesNotContain(arcNoXessDll.Routes, r => r.Id == RouteDriverXessOverride);
        Assert.Equal(RouteUnavailable, arcNoXessDll.BestRouteId);
        Assert.Contains("libxess_fg.dll", arcNoXessDll.HowToEnable ?? "", StringComparison.Ordinal);

        var arcWithXessDll = Decide(ArcB580(), eldenRing with { ShipsXessFg = true, ShipsXessFgDll = true });
        Assert.Contains(arcWithXessDll.Routes, r => r.Id == RouteDriverXessOverride);
    }

    [Fact]
    public void TierEUnavailableExplainsCompetitiveRiotAndLegacyDirectX()
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
            FluidFacts: new FluidMotionFrames.Game("VALORANT"),
            LosslessScalingInstalled: true);

        var riotDecision = Decide(Rtx4070(), riotGame);
        Assert.Equal(RouteUnavailable, riotDecision.BestRouteId);
        Assert.Contains("Riot", riotDecision.Word, StringComparison.Ordinal);
        Assert.NotEmpty(riotDecision.HowToEnable ?? "");

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
            ModStatus: new DlssSwapService.FrameGenStatus("hidden"),
            FluidFacts: SinglePlayerFacts("Half-Life 2", apis: ["dx9"]),
            LosslessScalingInstalled: false);

        var dx9Decision = Decide(Rtx4070(), dx9Game);
        Assert.Equal(RouteUnavailable, dx9Decision.BestRouteId);
        Assert.Contains("DX9", dx9Decision.Word, StringComparison.Ordinal);
        Assert.Contains("Lossless Scaling", dx9Decision.HowToEnable ?? "", StringComparison.Ordinal);
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
