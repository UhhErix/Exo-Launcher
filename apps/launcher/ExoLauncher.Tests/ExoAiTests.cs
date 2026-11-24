using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class ExoAiTests
{
    private sealed class FakeFanBackend : IFanBackend
    {
        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public int TempC { get; set; } = 60;
        public int FanCount { get; set; } = 2;
        public int CurrentPct { get; set; } = 40;
        public Func<int, int, FanApplyResult>? OnSet;
        public List<(int Index, int Percent)> Applied { get; } = [];
        public List<int> Restored { get; } = [];
        public bool Disposed { get; private set; }

        public int? ReadTempC() => TempC;
        public int ReadFanCount() => FanCount;
        public int? ReadFanPercent(int index) => CurrentPct;

        public FanApplyResult SetFanPercent(int index, int percent)
        {
            if (OnSet is not null)
            {
                var res = OnSet(index, percent);
                if (!res.Ok) return res;
            }
            Applied.Add((index, percent));
            CurrentPct = percent;
            return new(true, false, false, null);
        }

        public void RestoreAuto(int index) => Restored.Add(index);

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void FanCurvesEvaluatePresetsHysteresisAndSafetyClamps()
    {
        Assert.Equal(48, ExoAiFans.EvaluateCurve("auto", 55));
        Assert.Equal(100, ExoAiFans.EvaluateCurve("auto", 85));
        Assert.Equal(90, ExoAiFans.EvaluateCurve("auto", 78, lastTargetPct: 90, lastTempC: 80));
        Assert.Equal("auto", ExoAiSettings.NormalizePreset("unknown-preset"));
        Assert.Equal("performance", ExoAiSettings.NormalizePreset(" Performance "));

        Assert.Equal(42, ExoAiFans.EvaluateCurve("quiet", 60));
        Assert.Equal(72, ExoAiFans.EvaluateCurve("balanced", 70));
        Assert.Equal(95, ExoAiFans.EvaluateCurve("performance", 75));

        // 3 °C downward hysteresis holds prior speed when temp drops only 2 °C.
        Assert.Equal(90, ExoAiFans.EvaluateCurve("balanced", 78, lastTargetPct: 90, lastTempC: 80));
        // Dropping >= 3 °C releases hysteresis.
        Assert.True(ExoAiFans.EvaluateCurve("balanced", 77, lastTargetPct: 90, lastTempC: 80) < 90);

        // Thermal safety floor overrides curve and hysteresis at >=80 °C and >=85 °C.
        Assert.True(ExoAiFans.EvaluateCurve("quiet", 80)! >= 85);
        Assert.Equal(100, ExoAiFans.EvaluateCurve("quiet", 85));
    }

    [Fact]
    public void FansHandleUnavailableGpuRefusalAutoAndShutdownRestore()
    {
        var unavailable = new FakeFanBackend { IsAvailable = false, UnavailableReason = "No NVIDIA GPU" };
        using (var fans = new ExoAiFans(() => unavailable))
        {
            var status = fans.SetPreset("balanced");
            Assert.False(status.Supported);
            Assert.Equal("No NVIDIA GPU", status.Note);
        }
        Assert.True(unavailable.Disposed);

        // Partial refusal on second fan restores already-touched first fan.
        var refusal = new FakeFanBackend
        {
            FanCount = 2,
            TempC = 65,
            OnSet = (idx, _) => idx == 1 ? new(false, false, true, "Needs admin") : new(true, false, false, null),
        };
        using (var fans = new ExoAiFans(() => refusal))
        {
            var status = fans.SetPreset("balanced");
            Assert.False(status.Supported);
            Assert.Equal("Needs admin", status.Note);
            Assert.Contains(0, refusal.Restored);
        }

        var autoRefusal = new FakeFanBackend
        {
            FanCount = 2,
            TempC = 65,
            OnSet = (idx, _) => idx == 1 ? new(false, false, true, "Needs admin") : new(true, false, false, null),
        };
        using (var fans = new ExoAiFans(() => autoRefusal))
        {
            var status = fans.SetPreset("auto");
            Assert.False(status.Supported);
            Assert.Null(status.Note);
            Assert.Null(status.TargetPercent);
            Assert.Contains(0, autoRefusal.Restored);
        }

        // Auto actively drives adaptive fan targets and Shutdown restores every touched fan.
        var working = new FakeFanBackend { FanCount = 2, TempC = 70 };
        using (var fans = new ExoAiFans(() => working))
        {
            var status = fans.SetPreset("balanced");
            Assert.True(status.Supported);
            Assert.Equal(72, status.TargetPercent);
            Assert.Equal([(0, 72), (1, 72)], working.Applied);

            working.Applied.Clear();
            var autoStatus = fans.SetPreset("auto");
            Assert.Equal("auto", autoStatus.Preset);
            Assert.True(autoStatus.Supported);
            Assert.Equal(72, autoStatus.TargetPercent);
            Assert.Equal([(0, 72), (1, 72)], working.Applied);

            working.Restored.Clear();
            fans.Shutdown();
            Assert.Equal([0, 1], working.Restored.OrderBy(i => i).ToArray());
            Assert.True(working.Disposed);
        }
    }

    [Fact]
    public void CoordinatorRunsSafeBackgroundOptimizationOnlyWhenIdle()
    {
        var gaming = false;
        var applyCalls = 0;
        var fanBackend = new FakeFanBackend { TempC = 55 };
        var settings = new AppSettings
        {
            ExoAi = new ExoAiSettings { AutoApplyBestSettings = true, BackgroundDriftCheck = true, FanPreset = "balanced" },
        };
        var timestamp = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

        using var coordinator = new ExoAiCoordinator(
            new ExoAiFans(() => fanBackend),
            () => gaming,
            () => { applyCalls++; return ["Exo power plan active"]; },
            () => ["Memory speed"],
            () => new ExoAiAutopilotSnapshot(gaming, gaming ? "steam:1" : null, gaming ? "Game" : null, 1, gaming, false),
            () => timestamp);

        coordinator.Start(() => settings);

        // Idle drift check applies safe settings silently and records attention items.
        coordinator.RunDriftCheck();
        Assert.Equal(1, applyCalls);
        var snap = coordinator.Snapshot();
        Assert.True(snap.AutoApplyBestSettings);
        Assert.True(snap.BackgroundDriftCheck);
        Assert.Equal("balanced", snap.Fans.Preset);
        Assert.Equal(50, snap.Fans.TargetPercent);
        Assert.Equal(["Exo power plan active"], snap.AutoApplied);
        Assert.Equal(["Memory speed"], snap.NeedsAttention);
        Assert.NotNull(snap.LastCheckUtc);
        Assert.True(snap.Fans.Supported);

        // While a game is running, scheduled drift check skips work.
        gaming = true;
        coordinator.RunDriftCheck();
        Assert.Equal(1, applyCalls);

        gaming = false;
        settings.ExoAi.AutoApplyBestSettings = false;
        coordinator.NotifySettings(settings.ExoAi);
        Assert.Equal(1, applyCalls);
        var disabledApplySnap = coordinator.Snapshot();
        Assert.False(disabledApplySnap.AutoApplyBestSettings);
        Assert.True(disabledApplySnap.BackgroundDriftCheck);
        Assert.Empty(disabledApplySnap.AutoApplied);

        coordinator.RunDriftCheck();
        Assert.Equal(1, applyCalls);

        settings.ExoAi.BackgroundDriftCheck = false;
        var disabledBothSnap = coordinator.Snapshot();
        Assert.False(disabledBothSnap.AutoApplyBestSettings);
        Assert.False(disabledBothSnap.BackgroundDriftCheck);

        timestamp = timestamp.AddMinutes(5);
        var after = coordinator.RunNow();
        Assert.Equal(2, applyCalls);
        Assert.False(after.AutoApplyBestSettings);
        Assert.False(after.BackgroundDriftCheck);
        Assert.Equal(["Exo power plan active"], after.AutoApplied);
        Assert.Equal(["Memory speed"], after.NeedsAttention);

        coordinator.Shutdown();
        Assert.True(fanBackend.Disposed);
    }

    [Fact]
    public void CoordinatorAppliesUserFanPresetAndRespectsDriftCheckSetting()
    {
        var applyCalls = 0;
        var attentionCalls = 0;
        var fanBackend = new FakeFanBackend { TempC = 55 };
        var settings = new AppSettings
        {
            ExoAi = new ExoAiSettings { AutoApplyBestSettings = true, BackgroundDriftCheck = false, FanPreset = "performance" },
        };
        var timestamp = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

        using var coordinator = new ExoAiCoordinator(
            new ExoAiFans(() => fanBackend),
            () => false,
            () => { applyCalls++; return ["Exo power plan active"]; },
            () => { attentionCalls++; return ["Memory speed"]; },
            () => new ExoAiAutopilotSnapshot(false, null, null, 0, false, false),
            () => timestamp);

        coordinator.Start(() => settings);
        var initialSnap = coordinator.Snapshot();
        Assert.Equal("performance", initialSnap.Fans.Preset);
        Assert.Equal(ExoAiFans.EvaluateCurve("performance", 55), initialSnap.Fans.TargetPercent);
        Assert.True(initialSnap.AutoApplyBestSettings);
        Assert.False(initialSnap.BackgroundDriftCheck);

        coordinator.RunDriftCheck();
        Assert.Equal(0, applyCalls);
        Assert.Equal(0, attentionCalls);

        settings.ExoAi.FanPreset = "quiet";
        coordinator.NotifySettings(settings.ExoAi);
        var quietSnap = coordinator.Snapshot();
        Assert.Equal("quiet", quietSnap.Fans.Preset);
        Assert.Equal(ExoAiFans.EvaluateCurve("quiet", 55), quietSnap.Fans.TargetPercent);

        settings.ExoAi.FanPreset = "auto";
        coordinator.NotifySettings(settings.ExoAi);
        var autoSnap = coordinator.Snapshot();
        Assert.Equal("auto", autoSnap.Fans.Preset);
        Assert.Equal(ExoAiFans.EvaluateCurve("auto", 55), autoSnap.Fans.TargetPercent);
    }
}
