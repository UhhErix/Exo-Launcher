using System.Diagnostics;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Macros;

namespace ExoLauncher.Tests;

public sealed class MacroEngineTests
{
    private static MacroProfile DesktopProfile() => new()
    {
        Id = "desktop",
        Name = "Desktop",
        IsDefault = true,
        Bindings =
        [
            new MacroBinding
            {
                Id = "desk-onboard",
                Name = "Desktop Push-to-Talk",
                Delivery = MacroDelivery.Onboard,
                Trigger = new MacroTrigger { Chord = [new MacroInputSpec { DeviceKind = "mouse", Code = "Mouse5" }] },
                Steps = [new MacroStep { Id = "d1", Kind = MacroStepKind.KeyDown, Code = "F13", DelayMs = 0 }],
            },
        ],
    };

    private static MacroProfile GameProfile(string id, string gameId, string delivery, string code = "F14") => new()
    {
        Id = id,
        Name = id,
        IsDefault = false,
        GameIds = [gameId],
        Bindings =
        [
            new MacroBinding
            {
                Id = id + "-bind",
                Name = id + " bind",
                Delivery = delivery,
                Trigger = new MacroTrigger { Chord = [new MacroInputSpec { DeviceKind = "keyboard", Code = "F1" }] },
                Steps = [new MacroStep { Id = "g1", Kind = MacroStepKind.KeyDown, Code = code, DelayMs = 0 }],
            },
        ],
    };

    private static MacroProfile AppProfile(string id, string exe) => new()
    {
        Id = id,
        Name = id,
        IsDefault = false,
        AppExecutables = [exe],
        Bindings = [],
    };

    [Fact]
    public void Scheduler_SubMillisecondAccuracy_AcrossShortDelays()
    {
        var scheduler = new MacroScheduler();
        var sink = new DryRunMacroSink();
        var delays = new[] { 1.0, 2.0, 4.0, 5.0, 8.0, 10.0, 12.0, 16.0 };
        var steps = new List<MacroStep>();
        var idx = 0;
        for (var round = 0; round < 3; round++)
        {
            foreach (var ms in delays)
            {
                steps.Add(new MacroStep { Id = $"s{idx++}", Kind = MacroStepKind.KeyDown, Code = "A", DelayMs = ms });
            }
        }

        var binding = new MacroBinding
        {
            Id = "timing-bench",
            Name = "Timing accuracy",
            Delivery = MacroDelivery.Software,
            Trigger = new MacroTrigger { Mode = MacroPlaybackMode.Once, JitterMs = 0 },
            Steps = steps,
        };

        var result = scheduler.Execute(binding, sink, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(steps.Count, sink.Events.Count);
        Assert.Equal(steps.Count, result.Latency.Samples);
        Assert.True(result.Latency.P50Us < 1000.0, $"Expected P50 < 1000us, got {result.Latency.P50Us}us");
        Assert.True(result.Latency.P95Us < 2000.0, $"Expected P95 < 2000us, got {result.Latency.P95Us}us");
    }

    [Fact]
    public void Scheduler_RandomizationOffByDefault_AndBoundedWhenEnabled()
    {
        var scheduler = new MacroScheduler();
        var baseSteps = new[]
        {
            new MacroStep { Id = "s1", Kind = MacroStepKind.Delay, DelayMs = 6.0 },
            new MacroStep { Id = "s2", Kind = MacroStepKind.KeyDown, Code = "E", DelayMs = 6.0 },
        };

        var noJitter = new MacroBinding
        {
            Id = "no-jitter",
            Trigger = new MacroTrigger { Mode = MacroPlaybackMode.Once, JitterMs = 0 },
            Steps = baseSteps,
        };
        var withJitter = new MacroBinding
        {
            Id = "with-jitter",
            Trigger = new MacroTrigger { Mode = MacroPlaybackMode.Once, JitterMs = 3 },
            Steps = baseSteps,
        };

        var resFixed = scheduler.Execute(noJitter, new DryRunMacroSink(), CancellationToken.None, seed: 42);
        var resJitter = scheduler.Execute(withJitter, new DryRunMacroSink(), CancellationToken.None, seed: 42);

        Assert.Equal(6.0, resFixed.Trace[0].TargetOffsetMs);
        Assert.Equal(12.0, resFixed.Trace[1].TargetOffsetMs);
        Assert.InRange(resJitter.Trace[0].TargetOffsetMs, 3.0, 9.0);
        Assert.InRange(resJitter.Trace[1].TargetOffsetMs, 6.0, 18.0);
        Assert.NotEqual(12.0, resJitter.Trace[1].TargetOffsetMs);
    }

    [Fact]
    public void Scheduler_RepeatMode_ExecutesExactRounds()
    {
        var scheduler = new MacroScheduler();
        var sink = new DryRunMacroSink();
        var binding = new MacroBinding
        {
            Id = "repeat-test",
            Trigger = new MacroTrigger { Mode = MacroPlaybackMode.Repeat, RepeatCount = 4, JitterMs = 0 },
            Steps =
            [
                new MacroStep { Id = "r1", Kind = MacroStepKind.KeyDown, Code = "Space", DelayMs = 1 },
                new MacroStep { Id = "r2", Kind = MacroStepKind.KeyUp, Code = "Space", DelayMs = 1 },
            ],
        };

        var res = scheduler.Execute(binding, sink, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal(8, sink.Events.Count);
        Assert.Equal(8, res.Trace.Count);
    }

    [Fact]
    public void ChordMatcher_RespectsDeviceIdLayerAndWindow()
    {
        var matcher = new MacroChordMatcher();
        var t0 = Stopwatch.GetTimestamp();
        var tInside = t0 + (long)(0.020 * Stopwatch.Frequency);
        var tOutside = t0 + (long)(0.120 * Stopwatch.Frequency);

        var mouseSpec = new MacroInputSpec { DeviceKind = "mouse", DeviceId = "vid_046d_pid_c094", Code = "Mouse4" };
        var keySpec = new MacroInputSpec { DeviceKind = "keyboard", DeviceId = null, Code = "E" };
        var trigger = new MacroTrigger
        {
            Chord = [mouseSpec, keySpec],
            ChordWindowMs = 45,
            LayerId = "shift",
        };

        matcher.KeyDown("vid_046d_pid_c094:mouse:Mouse4", t0);
        matcher.KeyDown("vid_31e3_pid_1310:keyboard:E", tInside);

        Assert.False(matcher.Matches(trigger, "base", tInside));
        Assert.True(matcher.Matches(trigger, "shift", tInside));

        matcher.KeyUp("vid_046d_pid_c094:mouse:Mouse4");
        matcher.KeyDown("other_mouse:mouse:Mouse4", t0);
        Assert.False(matcher.Matches(trigger, "shift", tInside));

        matcher.KeyUp("other_mouse:mouse:Mouse4");
        matcher.KeyUp("vid_31e3_pid_1310:keyboard:E");
        matcher.KeyDown("vid_046d_pid_c094:mouse:Mouse4", t0);
        matcher.KeyDown("vid_31e3_pid_1310:keyboard:E", tOutside);
        Assert.False(matcher.Matches(trigger, "shift", tOutside));
    }

    [Fact]
    public void ProfileSwitcher_RapidFlapping_DoesNotThrashOrDoubleSwitch()
    {
        var profiles = new List<MacroProfile>
        {
            DesktopProfile(),
            GameProfile("fps", "steam:730", MacroDelivery.Onboard, "F15"),
        };
        var switcher = new MacroProfileSwitcher(debounceMs: 350, exitGraceMs: 1500, hardwareCooldownMs: 2000);
        switcher.SetInitialActive("desktop", null, MacroProfileSwitcher.ComputeOnboardHash(profiles[0]));

        for (var i = 0; i < 20; i++)
        {
            switcher.ObserveGameState("steam:730", isRunning: i % 2 == 0, nowMs: i * 10, profiles);
            var mid = switcher.Poll(i * 10, profiles, _ => true);
            Assert.False(mid.Changed);
        }

        switcher.ObserveGameState("steam:730", isRunning: true, nowMs: 220, profiles);
        Assert.False(switcher.Poll(500, profiles, _ => true).Changed);

        var settled = switcher.Poll(580, profiles, _ => true);
        Assert.True(settled.Changed);
        Assert.Equal("fps", settled.ProfileId);
        Assert.True(settled.HardwareWriteAllowed);
        Assert.Equal(1, switcher.AppliedSwitches);
        Assert.Equal(1, switcher.HardwareWrites);
        Assert.True(switcher.SuppressedFlaps > 0);
    }

    [Fact]
    public void ProfileSwitcher_ExitGraceHold_SuppressesLauncherHandoffFlap()
    {
        var profiles = new List<MacroProfile>
        {
            DesktopProfile(),
            GameProfile("rpg", "steam:1091500", MacroDelivery.Onboard, "F16"),
        };
        var switcher = new MacroProfileSwitcher(debounceMs: 300, exitGraceMs: 1500, hardwareCooldownMs: 2000);
        switcher.SetInitialActive("desktop", null, MacroProfileSwitcher.ComputeOnboardHash(profiles[0]));

        switcher.ObserveGameState("steam:1091500", isRunning: true, nowMs: 0, profiles);
        var entered = switcher.Poll(350, profiles, _ => true);
        Assert.True(entered.Changed);
        Assert.Equal("rpg", entered.ProfileId);

        switcher.ObserveGameState("steam:1091500", isRunning: false, nowMs: 1000, profiles);
        Assert.False(switcher.Poll(1400, profiles, _ => true).Changed);
        Assert.Equal("rpg", switcher.ActiveProfileId);

        switcher.ObserveGameState("steam:1091500", isRunning: true, nowMs: 1800, profiles);
        Assert.False(switcher.Poll(2500, profiles, _ => true).Changed);
        Assert.Equal("rpg", switcher.ActiveProfileId);
        Assert.Equal(1, switcher.AppliedSwitches);

        switcher.ObserveGameState("steam:1091500", isRunning: false, nowMs: 3000, profiles);
        Assert.False(switcher.Poll(4400, profiles, _ => true).Changed);
        Assert.False(switcher.Poll(4600, profiles, _ => true).Changed);
        var backToDesktop = switcher.Poll(4900, profiles, _ => true);
        Assert.True(backToDesktop.Changed);
        Assert.Equal("desktop", backToDesktop.ProfileId);
    }

    [Fact]
    public void ProfileSwitcher_HardwareWriteCooldown_PreventsWriteLoop()
    {
        var profiles = new List<MacroProfile>
        {
            DesktopProfile(),
            GameProfile("g1", "steam:1", MacroDelivery.Onboard, "F14"),
            GameProfile("g2", "steam:2", MacroDelivery.Onboard, "F15"),
        };
        var switcher = new MacroProfileSwitcher(debounceMs: 100, exitGraceMs: 0, hardwareCooldownMs: 2000);
        switcher.SetInitialActive("desktop", null, MacroProfileSwitcher.ComputeOnboardHash(profiles[0]));

        switcher.ObserveGameState("steam:1", isRunning: true, nowMs: 0, profiles);
        var first = switcher.Poll(150, profiles, _ => true);
        Assert.True(first.Changed);
        Assert.True(first.HardwareWriteAllowed);

        switcher.ObserveGameState("steam:2", isRunning: true, nowMs: 500, profiles);
        var second = switcher.Poll(650, profiles, _ => true);
        Assert.True(second.Changed);
        Assert.False(second.HardwareWriteAllowed);
        Assert.Equal(1, switcher.HardwareWrites);
        Assert.Equal(1, switcher.SuppressedHardwareWrites);
    }

    [Fact]
    public void ProfileSwitcher_ForegroundAppDebounce_IgnoresQuickAltTab()
    {
        var profiles = new List<MacroProfile>
        {
            DesktopProfile(),
            AppProfile("editor", "code.exe"),
        };
        var switcher = new MacroProfileSwitcher(debounceMs: 350, exitGraceMs: 0, hardwareCooldownMs: 2000);
        switcher.SetInitialActive("desktop");

        switcher.ObserveForegroundApp("C:\\Program Files\\VSCode\\Code.exe", nowMs: 0, profiles);
        switcher.ObserveForegroundApp("explorer.exe", nowMs: 120, profiles);
        Assert.False(switcher.Poll(600, profiles, _ => true).Changed);
        Assert.Equal("desktop", switcher.ActiveProfileId);

        switcher.ObserveForegroundApp("Code.exe", nowMs: 1000, profiles);
        var switched = switcher.Poll(1400, profiles, _ => true);
        Assert.True(switched.Changed);
        Assert.Equal("editor", switched.ProfileId);
    }

    [Fact]
    public void AntiCheatPolicy_NeverAutoEnablesAndRequiresExactConfirmation()
    {
        var riotGame = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot, Installed = true };
        var eacGame = new GameEntry { Id = "steam:1172470", Title = "Apex Legends", Store = StoreKind.Steam, Installed = true };
        var cleanGame = new GameEntry { Id = "steam:413150", Title = "Stardew Valley", Store = StoreKind.Steam, Installed = true };

        var riotAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Riot Vanguard"], "store");
        var eacAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "files");
        var cleanAnswer = AntiCheatCheck.Answer.Clean;

        var riotPolicy = MacroPolicy.Evaluate(riotGame, riotAnswer, []);
        var eacPolicy = MacroPolicy.Evaluate(eacGame, eacAnswer, []);
        var cleanPolicy = MacroPolicy.Evaluate(cleanGame, cleanAnswer, []);

        Assert.True(riotPolicy.RequiresConfirmation);
        Assert.False(riotPolicy.AutoEnableAllowed);
        Assert.False(riotPolicy.Confirmed);
        Assert.Contains("risk a ban", riotPolicy.SoftwareWarning);
        Assert.False(MacroPolicy.CanRunSoftwareMacros(riotPolicy, manualActivation: false));
        Assert.False(MacroPolicy.CanRunSoftwareMacros(riotPolicy, manualActivation: true));

        Assert.True(eacPolicy.RequiresConfirmation);
        Assert.False(eacPolicy.AutoEnableAllowed);

        var consents = new List<MacroGameConsent>
        {
            new() { GameId = eacGame.Id, AntiCheatLabel = eacAnswer.Label!, ConfirmedAt = "2026-11-24T00:00:00Z" },
        };
        var eacConfirmed = MacroPolicy.Evaluate(eacGame, eacAnswer, consents);
        Assert.True(eacConfirmed.Confirmed);
        Assert.False(eacConfirmed.AutoEnableAllowed);
        Assert.False(MacroPolicy.CanRunSoftwareMacros(eacConfirmed, manualActivation: false));
        Assert.True(MacroPolicy.CanRunSoftwareMacros(eacConfirmed, manualActivation: true));

        var changedAcAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["BattlEye", "Easy Anti-Cheat"], "files");
        var eacStale = MacroPolicy.Evaluate(eacGame, changedAcAnswer, consents);
        Assert.False(eacStale.Confirmed);

        Assert.False(cleanPolicy.RequiresConfirmation);
        Assert.True(cleanPolicy.AutoEnableAllowed);
        Assert.True(MacroPolicy.CanRunSoftwareMacros(cleanPolicy, manualActivation: false));
    }

    [Fact]
    public void ProfileSwitcher_PausesSoftwareMacrosForUnconfirmedAntiCheatGame()
    {
        var profiles = new List<MacroProfile>
        {
            DesktopProfile(),
            GameProfile("val", "riot:valorant", MacroDelivery.Software),
        };
        var switcher = new MacroProfileSwitcher(debounceMs: 100, exitGraceMs: 0, hardwareCooldownMs: 2000);
        switcher.SetInitialActive("desktop");

        switcher.ObserveGameState("riot:valorant", isRunning: true, nowMs: 0, profiles);
        var decision = switcher.Poll(150, profiles, _ => false);

        Assert.True(decision.Changed);
        Assert.Equal("val", decision.ProfileId);
        Assert.False(decision.SoftwareAllowed);
        Assert.NotNull(decision.BlockedReason);
    }
}
