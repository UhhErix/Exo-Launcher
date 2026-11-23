using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class FrameCapLabTests
{
    [Theory]
    [InlineData(60, 59)]
    [InlineData(75, 73)]
    [InlineData(120, 116)]
    [InlineData(144, 138)]
    [InlineData(165, 157)]
    [InlineData(240, 224)]
    [InlineData(360, 324)]
    public void VrrCapFormulaMatchesPublishedValues(double refreshHz, int expected)
    {
        Assert.Equal(expected, FrameCapPlanner.VrrCap(refreshHz));
    }

    [Fact]
    public void VrrCapClampsToVrrFloor()
    {
        Assert.Equal(48, FrameCapPlanner.VrrCap(48, 48));
        Assert.Equal(50, FrameCapPlanner.VrrCap(50, 50));
        var plan = FrameCapPlanner.Plan(48, vrrEnabled: true, vrrFloorHz: 48);
        Assert.True(plan.InVrrRange);
        Assert.Equal(48, plan.OutputCeilingFps);
        Assert.Equal(48, plan.MaxBaseCapFps);
    }

    [Theory]
    [InlineData(60, 2, 29)]
    [InlineData(75, 2, 36)]
    [InlineData(120, 2, 58)]
    [InlineData(144, 2, 69)]
    [InlineData(144, 3, 46)]
    [InlineData(144, 4, 34)]
    [InlineData(165, 2, 78)]
    [InlineData(165, 3, 52)]
    [InlineData(165, 4, 39)]
    [InlineData(240, 2, 112)]
    [InlineData(240, 3, 74)]
    [InlineData(240, 4, 56)]
    [InlineData(360, 2, 162)]
    [InlineData(360, 3, 108)]
    [InlineData(360, 4, 81)]
    public void FrameGenMultiplierKeepsOutputInsideVrr(double refreshHz, int mult, int expectedBase)
    {
        var plan = FrameCapPlanner.Plan(refreshHz, vrrEnabled: true, vrrFloorHz: 48, fgMultiplier: mult);
        Assert.Equal(expectedBase, plan.MaxBaseCapFps);
        Assert.True(plan.MaxBaseCapFps * mult <= plan.OutputCeilingFps);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    [InlineData(360)]
    public void VrrOffOffersRefreshLockedCapsAndUncappedLowLatency(double refreshHz)
    {
        var plan = FrameCapPlanner.Plan(
            refreshHz,
            vrrEnabled: false,
            vrrFloorHz: 48,
            fgMultiplier: 1,
            supportsLowLatency: true,
            lowLatencyLabel: "NVIDIA Reflex");

        Assert.False(plan.InVrrRange);
        Assert.NotEmpty(plan.RefreshLockedCaps);
        Assert.Equal((int)refreshHz, plan.RefreshLockedCaps[0]);
        Assert.NotNull(plan.UncappedOption);
        Assert.Contains("NVIDIA Reflex", plan.UncappedOption!.Label, StringComparison.Ordinal);
        Assert.Null(plan.UncappedOption.CapFps);
    }

    [Fact]
    public void FrameGenRouterMultiplierForReadsActiveRoutesAndOverrides()
    {
        var route2x = new FrameGenRouter.FrameGenRoute(
            FrameGenRouter.RouteDriverSmoothMotion, FrameGenRouter.TierDriver, FrameGenRouter.RouteDriverSmoothMotion,
            "Smooth Motion", "active", "Smooth Motion", true, "RTX 4070", "safe", "Safe", false, "Low", true, true);
        var dec2x = new FrameGenRouter.FrameGenDecision(
            route2x.Id, route2x.Id, "on", "Smooth Motion", true, false, null, null, [route2x]);
        Assert.Equal(2, FrameGenRouter.MultiplierFor(dec2x));

        var route4x = new FrameGenRouter.FrameGenRoute(
            FrameGenRouter.RouteDriverXessOverride, FrameGenRouter.TierDriver, FrameGenRouter.RouteDriverXessOverride,
            "XeSS override", "active", "XeSS 4x", true, "Arc B580", "safe", "Safe", false, "Low", true, true,
            ChoiceValue: "4x", Choices: ["game", "2x", "3x", "4x"]);
        var dec4x = new FrameGenRouter.FrameGenDecision(
            route4x.Id, route4x.Id, "on", "XeSS 4x", true, false, null, null, [route4x]);
        Assert.Equal(4, FrameGenRouter.MultiplierFor(dec4x));

        var decOff = dec2x with { State = "off", SelectedRouteId = "off" };
        Assert.Equal(1, FrameGenRouter.MultiplierFor(decOff));
    }

    [Fact]
    public void VendorRoutingHandlesNvidiaAmdIntelAndHybridLaptops()
    {
        var cleanAc = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, (string?)null, "clean");
        var eacAc = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, "Easy Anti-Cheat", "EAC");

        var nv = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["NVIDIA GeForce RTX 3070"], true, false, false, false, false, false,
            false, null, false, false, eacAc));
        Assert.Equal("nvidia", nv.ActiveVendor);
        Assert.Equal(FrameCapVendorRouter.BackendNvidiaDrs, nv.RecommendedBackend);
        Assert.True(nv.CanApply);
        Assert.False(nv.IsHybridLaptop);

        var amdChill = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["AMD Radeon RX 7800 XT"], false, true, true, false, false, false,
            false, null, false, false, eacAc));
        Assert.Equal("amd", amdChill.ActiveVendor);
        Assert.Equal(FrameCapVendorRouter.BackendAmdChill, amdChill.RecommendedBackend);
        Assert.True(amdChill.CanApply);

        var amdNoChill = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["AMD Radeon RX 7800 XT"], false, true, false, false, false, false,
            false, null, false, false, cleanAc));
        Assert.False(amdNoChill.CanApply);
        Assert.Equal(FrameCapVendorRouter.BackendNone, amdNoChill.RecommendedBackend);
        Assert.Contains("AMD Software", amdNoChill.Summary, StringComparison.OrdinalIgnoreCase);

        var intel = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["Intel(R) Arc(TM) B580 Graphics"], false, false, false, true, true, true,
            false, null, false, false, eacAc));
        Assert.Equal("intel", intel.ActiveVendor);
        Assert.Equal(FrameCapVendorRouter.BackendIntelIgcl, intel.RecommendedBackend);
        Assert.True(intel.Backends.Single(b => b.Id == FrameCapVendorRouter.BackendIntelIgcl).PerGameNative);

        var hybridIntelNv = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["Intel(R) UHD Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU"],
            true, false, false, true, true, false,
            false, null, false, false, eacAc));
        Assert.True(hybridIntelNv.IsHybridLaptop);
        Assert.Equal("nvidia", hybridIntelNv.ActiveVendor);
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", hybridIntelNv.ActiveAdapter);
        Assert.Equal(FrameCapVendorRouter.BackendNvidiaDrs, hybridIntelNv.RecommendedBackend);

        var hybridAmdIntel = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["AMD Radeon(TM) Graphics", "Intel(R) Arc(TM) A770 Graphics"],
            false, true, true, true, true, true,
            false, null, false, false, cleanAc));
        Assert.True(hybridAmdIntel.IsHybridLaptop);
        Assert.Equal("intel", hybridAmdIntel.ActiveVendor);
        Assert.Equal(FrameCapVendorRouter.BackendIntelIgcl, hybridAmdIntel.RecommendedBackend);

        var riotWithOwn = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            ["NVIDIA GeForce RTX 4080"], true, false, false, false, false, false,
            true, "GameUserSettings.ini · FrameRateLimit", true, IsRiot: true, eacAc));
        Assert.Equal(FrameCapVendorRouter.BackendNvidiaDrs, riotWithOwn.RecommendedBackend);
        Assert.False(riotWithOwn.Backends.Single(b => b.Id == FrameCapVendorRouter.BackendOwnInterface).Available);
    }

    [Fact]
    public void DipTierPickerComputesSmoothBalancedMaxAndMonotonicVariance()
    {
        var frames = new List<double>();
        for (var i = 0; i < 900; i++) frames.Add(7.5);
        for (var i = 0; i < 80; i++) frames.Add(11.1);
        for (var i = 0; i < 20; i++) frames.Add(15.4);

        var plan = FrameCapPlanner.Plan(144, vrrEnabled: true, vrrFloorHz: 48, fgMultiplier: 1, sessionFrameTimesMs: frames);
        Assert.True(plan.HasMeasuredFrames);
        var smooth = plan.Tiers.Single(t => t.Id == FrameCapPlanner.TierSmooth);
        var balanced = plan.Tiers.Single(t => t.Id == FrameCapPlanner.TierBalanced);
        var max = plan.Tiers.Single(t => t.Id == FrameCapPlanner.TierMax);

        Assert.Equal(64, smooth.CapFps);
        Assert.Equal(90, balanced.CapFps);
        Assert.Equal(138, max.CapFps);
        Assert.True(smooth.PredictedVarianceMs2 <= balanced.PredictedVarianceMs2);
        Assert.True(balanced.PredictedVarianceMs2 <= max.PredictedVarianceMs2);

        var suggestion = FrameCapPlanner.SuggestTier(plan);
        Assert.NotNull(suggestion.CapFps);
        Assert.False(string.IsNullOrWhiteSpace(suggestion.Reason));
    }

    [Fact]
    public void AbVerdictRespectsNoiseThresholdAndInterleavedSchedule()
    {
        Assert.Equal("A", CapLabAbTester.ArmForSecond(0));
        Assert.Equal("A", CapLabAbTester.ArmForSecond(4));
        Assert.Equal("B", CapLabAbTester.ArmForSecond(5));
        Assert.Equal("B", CapLabAbTester.ArmForSecond(9));
        Assert.Equal("A", CapLabAbTester.ArmForSecond(10));
        Assert.Equal("B", CapLabAbTester.ArmForSecond(15));

        var choppyA = Enumerable.Repeat(8.0, 180).Concat(Enumerable.Repeat(18.0, 20)).ToList();
        var smoothB = Enumerable.Repeat(10.0, 198).Concat(Enumerable.Repeat(10.5, 2)).ToList();

        var winB = CapLabAbTester.Evaluate(138, 100, choppyA, smoothB, noiseThresholdPercent: 3.0);
        Assert.Equal("b", winB.Winner);
        Assert.StartsWith("B is smoother by ", winB.Verdict, StringComparison.Ordinal);
        Assert.True(winB.DeltaPercent >= 3.0);

        var winA = CapLabAbTester.Evaluate(100, 138, smoothB, choppyA, noiseThresholdPercent: 3.0);
        Assert.Equal("a", winA.Winner);
        Assert.StartsWith("A is smoother by ", winA.Verdict, StringComparison.Ordinal);

        var nearlySameA = Enumerable.Repeat(10.0, 200).ToList();
        var nearlySameB = Enumerable.Repeat(10.05, 200).ToList();
        var tie = CapLabAbTester.Evaluate(100, 99, nearlySameA, nearlySameB, noiseThresholdPercent: 3.0);
        Assert.Equal("none", tie.Winner);
        Assert.Equal("No measurable difference", tie.Verdict);
    }

    [Fact]
    public void StutterAttributionIdentifiesEverySourceInPlainWords()
    {
        static List<double> SmoothSec(double ms = 8.0) => [.. Enumerable.Repeat(ms, 100)];
        static List<double> SpikeSec(double ms = 8.0, double spike = 38.0, int spikes = 4) =>
            [.. Enumerable.Repeat(ms, 96).Concat(Enumerable.Repeat(spike, spikes))];
        static SystemSecond QuietSys(long vramUsed = 4L << 30, long vramTotal = 8L << 30, long writes = 0, long faults = 0, double gameCores = 2.0, IReadOnlyList<AppLoad>? bg = null) =>
            new(35, 12, 70, 65, vramUsed, vramTotal, 8L << 30, 16L << 30, 2L << 30, 4L << 30, 1.0, 1.0, 100, 100, gameCores, writes, faults, bg ?? []);

        var shaderTrace = new List<CapLabTraceSecond>();
        for (var i = 0; i < 6; i++) shaderTrace.Add(new(SmoothSec(), QuietSys()));
        shaderTrace.Add(new(SpikeSec(), QuietSys(writes: 4L << 20, gameCores: 4.5)));
        var shaderReport = CapLabStutterAttribution.Analyze(shaderTrace);
        Assert.Equal(CapLabStutterAttribution.CauseShaderCompile, shaderReport.TopCause);
        Assert.Contains("Shader compile", shaderReport.Summary, StringComparison.OrdinalIgnoreCase);

        var loadTrace = new List<CapLabTraceSecond>();
        for (var i = 0; i < 6; i++) loadTrace.Add(new(SmoothSec(), QuietSys()));
        loadTrace.Add(new(SpikeSec(spike: 220.0, spikes: 2), QuietSys()));
        var loadReport = CapLabStutterAttribution.Analyze(loadTrace);
        Assert.Equal(CapLabStutterAttribution.CauseLoadingGap, loadReport.TopCause);

        var bgTrace = new List<CapLabTraceSecond>();
        for (var i = 0; i < 6; i++) bgTrace.Add(new(SmoothSec(), QuietSys(bg: [new AppLoad("chrome", 0.05)])));
        bgTrace.Add(new(SpikeSec(), QuietSys(bg: [new AppLoad("chrome", 2.2)])));
        var bgReport = CapLabStutterAttribution.Analyze(bgTrace);
        Assert.Equal(CapLabStutterAttribution.CauseBackgroundCpu, bgReport.TopCause);
        Assert.Contains("chrome", bgReport.Summary, StringComparison.OrdinalIgnoreCase);

        var diskTrace = new List<CapLabTraceSecond>();
        for (var i = 0; i < 6; i++) diskTrace.Add(new(SmoothSec(), QuietSys()));
        diskTrace.Add(new(SpikeSec(), QuietSys(faults: 320)));
        var diskReport = CapLabStutterAttribution.Analyze(diskTrace);
        Assert.Equal(CapLabStutterAttribution.CauseBackgroundDisk, diskReport.TopCause);

        var vramTrace = new List<CapLabTraceSecond>();
        for (var i = 0; i < 6; i++) vramTrace.Add(new(SmoothSec(), QuietSys(), ProcessVramBytes: 6L << 30, ProcessVramBudgetBytes: 8L << 30));
        vramTrace.Add(new(SpikeSec(), QuietSys(), ProcessVramBytes: 7900L << 20, ProcessVramBudgetBytes: 8L << 30));
        var vramReport = CapLabStutterAttribution.Analyze(vramTrace);
        Assert.Equal(CapLabStutterAttribution.CauseVramBudget, vramReport.TopCause);
        Assert.True(vramReport.PerProcessVramAvailable);

        var capTrace = new List<CapLabTraceSecond>();
        for (var i = 0; i < 6; i++) capTrace.Add(new(SmoothSec(7.0), QuietSys(), ActiveCapFps: 144, RefreshHz: 144, VrrEnabled: true));
        capTrace.Add(new(SpikeSec(ms: 7.0, spike: 19.0, spikes: 5), QuietSys(), ActiveCapFps: 144, RefreshHz: 144, VrrEnabled: true));
        var capReport = CapLabStutterAttribution.Analyze(capTrace);
        Assert.Equal(CapLabStutterAttribution.CauseFrameCap, capReport.TopCause);
    }

    [Fact]
    public void AbRunnerCancelsOnFocusLossAndRestoresPriorCap()
    {
        var svc = new FrameCapService();
        var appliedCaps = new List<int?>();
        var events = new List<FrameCapService.CapLabStatus>();
        svc.CapLabChanged += events.Add;

        var game = new GameEntry { Id = "steam:999999", Title = "Test Game", Installed = true, Store = StoreKind.Steam };
        try
        {
            var focused = true;
            var (ok, msg, _) = svc.StartAbRunForTest(
                game,
                138,
                100,
                priorCap: 138,
                applyCap: cap => appliedCaps.Add(cap),
                isInFront: () => focused);
            Assert.True(ok, msg);
            Assert.Equal([138], appliedCaps);

            svc.RecordSecond(game.Id, inFront: true, Enumerable.Repeat(7.3, 120).ToList(), null);
            focused = false;
            svc.RecordSecond(game.Id, inFront: false, [], null);

            Assert.Contains(events, e => e.Status == "cancelled" && e.Reason!.Contains("lost focus", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(138, appliedCaps[^1]);
        }
        finally
        {
            svc.ResetForTest();
        }
    }

    [Fact]
    public void AbRunnerInterleavesFourFiveSecondBlocksAndEmitsVerdict()
    {
        var svc = new FrameCapService();
        var appliedCaps = new List<int?>();
        var events = new List<FrameCapService.CapLabStatus>();
        svc.CapLabChanged += events.Add;

        var game = new GameEntry { Id = "steam:888888", Title = "Bench Game", Installed = true, Store = StoreKind.Steam };
        try
        {
            var (ok, msg, _) = svc.StartAbRunForTest(
                game,
                138,
                100,
                priorCap: 138,
                applyCap: cap => appliedCaps.Add(cap),
                isInFront: () => true);
            Assert.True(ok, msg);

            var choppyA = Enumerable.Repeat(8.0, 90).Concat(Enumerable.Repeat(18.0, 10)).ToList();
            var smoothB = Enumerable.Repeat(10.0, 99).Concat(Enumerable.Repeat(10.3, 1)).ToList();

            for (var sec = 0; sec < CapLabAbTester.TotalDurationSeconds; sec++)
            {
                var frames = CapLabAbTester.ArmForSecond(sec) == "A" ? choppyA : smoothB;
                svc.RecordSecond(game.Id, inFront: true, frames, null);
            }

            Assert.Equal([138, 100, 138, 100, 138], appliedCaps);
            var done = Assert.Single(events, e => e.Status == "completed");
            Assert.Equal("b", done.Winner);
            Assert.StartsWith("B is smoother by ", done.Verdict, StringComparison.Ordinal);
        }
        finally
        {
            svc.ResetForTest();
        }
    }

    [Fact]
    public void RadeonChillWritesOrderMinAndMaxSafelyAndOwnInterfaceRestoresCleanly()
    {
        var raise = RadeonProfileService.BuildChillWrites(currentMaxFps: 90, targetFps: 138);
        Assert.Equal(AmdAdlx.Keys.Chill, raise[0].Key);
        Assert.Equal(AmdAdlx.Keys.ChillMax, raise[1].Key);
        Assert.Equal(AmdAdlx.Keys.ChillMin, raise[2].Key);

        var lower = RadeonProfileService.BuildChillWrites(currentMaxFps: 144, targetFps: 90);
        Assert.Equal(AmdAdlx.Keys.ChillMin, lower[1].Key);
        Assert.Equal(AmdAdlx.Keys.ChillMax, lower[2].Key);

        var dir = Path.Combine(Path.GetTempPath(), "exo-owncap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ini = Path.Combine(dir, "GameUserSettings.ini");
            File.WriteAllText(ini, "[/Script/Engine.GameUserSettings]\nFullscreenMode=1\nFrameRateLimit=0.000000\n");
            var target = new FrameCapService.OwnCapTarget(ini, "FrameRateLimit", "ini_float", "GameUserSettings.ini · FrameRateLimit");
            Assert.True(FrameCapService.WriteOwnInterfaceCap(target, 138, out var prior, out var err), err);
            Assert.Equal("0.000000", prior);
            Assert.Contains("FrameRateLimit=138.000000", File.ReadAllText(ini), StringComparison.Ordinal);

            Assert.True(FrameCapService.RestoreOwnInterfaceCap(target, prior, out err), err);
            Assert.Contains("FrameRateLimit=0.000000", File.ReadAllText(ini), StringComparison.Ordinal);
        }
        finally
        {
            try { DeletionGate.DeleteDirectory(dir, recursive: true); } catch { }
        }
    }
}
