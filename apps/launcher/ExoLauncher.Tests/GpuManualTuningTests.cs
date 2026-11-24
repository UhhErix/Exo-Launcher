using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class GpuManualTuningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-gpu-manual-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataPath;
    private readonly TuneStore _tuneStore;

    public GpuManualTuningTests()
    {
        Directory.CreateDirectory(_root);
        _dataPath = Path.Combine(_root, GpuManualTuningService.StoreFileName);
        _tuneStore = new TuneStore(Path.Combine(_root, "tune"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, true);
    }

    private sealed class FakeBackend : IGpuTuningBackend
    {
        public string BackendId { get; set; }
        public string Vendor { get; set; }
        public GpuTuningCapabilities Caps { get; set; }
        public GpuManualProfile Defaults { get; set; } = GpuManualProfile.Auto;
        public GpuManualProfile Current { get; set; } = GpuManualProfile.Auto;
        public Func<GpuManualProfile, GpuApplyResult>? OnApply { get; set; }
        public Func<GpuApplyResult>? OnReset { get; set; }
        public int ApplyCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public int WaiverCalls { get; private set; }
        public GpuManualProfile? LastApplied { get; private set; }

        public FakeBackend(GpuTuningCapabilities caps, GpuManualProfile? defaults = null)
        {
            Caps = caps;
            BackendId = caps.Backend;
            Vendor = caps.Vendor;
            Defaults = defaults ?? GpuManualProfile.Auto;
            Current = Defaults;
        }

        public GpuTuningCapabilities Probe() => Caps;
        public GpuManualProfile ReadCurrent() => Current;
        public GpuManualProfile ReadFactoryDefaults() => Defaults;

        public GpuApplyResult Apply(GpuManualProfile profile)
        {
            ApplyCalls++;
            LastApplied = profile;
            if (OnApply is not null)
            {
                var custom = OnApply(profile);
                if (custom.Ok) Current = profile;
                return custom;
            }
            Current = profile;
            return new(true, false, null, BackendId);
        }

        public GpuApplyResult ResetToFactory()
        {
            ResetCalls++;
            if (OnReset is not null)
            {
                var custom = OnReset();
                if (custom.Ok) Current = Defaults;
                return custom;
            }
            Current = Defaults;
            return new(true, false, null, BackendId);
        }

        public bool AcceptWaiver()
        {
            WaiverCalls++;
            Caps = Caps with { WaiverAccepted = true };
            return true;
        }
    }

    private sealed class FakeComputeRunner(Func<int, ulong> passFunc) : IGpuComputePassRunner
    {
        private int _index;
        public ulong RunPass() => passFunc(++_index);
        public void Dispose() { }
    }

    private sealed class FakeSensorSampler(Func<int, GpuSensors?> sampleFunc, Func<int>? errorCount = null) : IGpuSensorSampler
    {
        private int _reads;
        public GpuSensors? Sample() => sampleFunc(++_reads);
        public int HardwareErrorCount() => errorCount?.Invoke() ?? 0;
    }

    private static GpuTuningCapabilities NvidiaFixture(string gpuName, string generation, string backend = "nvapi", int fanCount = 2)
    {
        var vf = new[]
        {
            new GpuVfPoint(0, 750, 1500, 0),
            new GpuVfPoint(1, 825, 1710, 0),
            new GpuVfPoint(2, 900, 1890, 0),
            new GpuVfPoint(3, 975, 2025, 0),
            new GpuVfPoint(4, 1050, 2100, 0),
        };
        var isNvApi = backend == "nvapi";
        return new GpuTuningCapabilities
        {
            Vendor = "nvidia",
            Generation = generation,
            DeviceId = $"nvidia:{gpuName}",
            DeviceName = gpuName,
            Backend = backend,
            PersistsInDriver = false,
            NeedsResidentForFanCurve = fanCount > 0,
            SupportsCurveEditor = isNvApi,
            SupportsUndervoltSearch = true,
            CoreClockOffsetMhz = new(-500, 500, 15, 0, true),
            MemoryClockOffsetMhz = new(-1000, 1500, 25, 0, true),
            PowerLimitPercent = isNvApi ? new(50, 115, 1, 100, true) : GpuTuningRange.Unsupported,
            PowerLimitWatts = isNvApi ? GpuTuningRange.Unsupported : new(120, 260, 5, 220, true),
            TempLimitC = isNvApi ? new(65, 90, 1, 83, true) : GpuTuningRange.Unsupported,
            ClockLockMhz = new(600, 2400, 15, 2100, true),
            VfCurvePoints = isNvApi ? vf : [],
            FanCount = fanCount,
            SupportsSoftwareFanCurve = fanCount > 0,
            FanMaxPoints = fanCount > 0 ? 5 : 0,
            FanSpeedPercent = fanCount > 0 ? new(30, 100, 1, 50, true) : GpuTuningRange.Unsupported,
            FanTempC = fanCount > 0 ? new(30, 95, 1, 60, true) : GpuTuningRange.Unsupported,
        };
    }

    private static GpuTuningCapabilities AmdFixture(string gpuName, string generation)
    {
        var rdna3Plus = generation is "RDNA3" or "RDNA4";
        return new GpuTuningCapabilities
        {
            Vendor = "amd",
            Generation = generation,
            DeviceId = $"amd:{gpuName}",
            DeviceName = gpuName,
            Backend = "adlx",
            PersistsInDriver = true,
            NeedsResidentForFanCurve = false,
            SupportsCurveEditor = rdna3Plus,
            SupportsUndervoltSearch = true,
            CoreMinClockMhz = new(500, 2900, 10, 500, true),
            CoreMaxClockMhz = new(500, 3200, 10, 2500, true),
            VoltageMv = rdna3Plus ? GpuTuningRange.Unsupported : new(800, 1200, 10, 1150, true),
            VoltageOffsetMv = rdna3Plus ? new(-150, 50, 5, 0, true) : GpuTuningRange.Unsupported,
            MemoryMaxClockMhz = new(1750, 2750, 10, 2500, true),
            MemoryTimingLevels = ["Default", "Fast Timing"],
            PowerLimitPercent = new(-10, 15, 1, 0, true),
            VfCurvePoints = rdna3Plus ? [new(0, 750, 1400, 0), new(1, 900, 2100, 0), new(2, 1050, 2600, 0)] : [],
            FanCount = 1,
            SupportsHardwareFanCurve = true,
            SupportsZeroRpm = true,
            FanMaxPoints = 5,
            FanSpeedPercent = new(15, 100, 1, 50, true),
            FanTempC = new(30, 100, 1, 60, true),
        };
    }

    private static GpuTuningCapabilities IntelFixture(string gpuName, string generation, bool waiverAccepted = false)
    {
        var battlemage = generation == "Battlemage";
        return new GpuTuningCapabilities
        {
            Vendor = "intel",
            Generation = generation,
            DeviceId = $"intel:{gpuName}",
            DeviceName = gpuName,
            Backend = "igcl",
            RequiresWaiver = true,
            WaiverAccepted = waiverAccepted,
            WaiverText = GpuTuningCapabilities.IntelWaiverBody,
            PersistsInDriver = false,
            NeedsResidentForFanCurve = false,
            SupportsCurveEditor = false,
            SupportsUndervoltSearch = battlemage,
            CoreClockOffsetMhz = new(-200, 300, 5, 0, true),
            VoltageOffsetMv = battlemage ? new(-100, 100, 5, 0, true) : new(0, 100, 5, 0, true),
            PowerLimitWatts = new(100, 228, 5, 190, true),
            TempLimitC = new(65, 95, 1, 90, true),
            MemoryMaxClockMhz = battlemage ? new(18000, 20000, 100, 19000, true) : GpuTuningRange.Unsupported,
            FanCount = 2,
            SupportsHardwareFanCurve = true,
            FanMaxPoints = 5,
            FanSpeedPercent = new(0, 100, 1, 50, true),
            FanTempC = new(30, 100, 1, 60, true),
        };
    }

    private static GpuTuningCapabilities LaptopLockedFixture(string vendor, string gpuName) => new()
    {
        Vendor = vendor,
        Generation = GpuTuningPolicy.DetectGeneration(vendor, gpuName),
        DeviceId = $"{vendor}:{gpuName}",
        DeviceName = gpuName,
        Backend = vendor == "nvidia" ? "nvapi" : vendor == "amd" ? "adlx" : "igcl",
        IsLaptop = true,
        LockedByMaker = true,
        LockReason = GpuTuningCapabilities.LockedMessage,
    };

    [Theory]
    [InlineData("nvidia", "NVIDIA GeForce RTX 2080 Ti", "Turing")]
    [InlineData("nvidia", "NVIDIA GeForce RTX 3070", "Ampere")]
    [InlineData("nvidia", "NVIDIA GeForce RTX 4080 SUPER", "Ada")]
    [InlineData("nvidia", "NVIDIA GeForce RTX 5080", "Blackwell")]
    [InlineData("amd", "AMD Radeon RX 5700 XT", "RDNA1")]
    [InlineData("amd", "AMD Radeon RX 6800 XT", "RDNA2")]
    [InlineData("amd", "AMD Radeon RX 7900 XTX", "RDNA3")]
    [InlineData("amd", "AMD Radeon RX 9070 XT", "RDNA4")]
    [InlineData("intel", "Intel Arc A770", "Alchemist")]
    [InlineData("intel", "Intel Arc B580", "Battlemage")]
    public void GenerationDetectionCoversAllVendorsAndGenerations(string vendor, string name, string expected)
    {
        Assert.Equal(expected, GpuTuningPolicy.DetectGeneration(vendor, name));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2080", "Turing")]
    [InlineData("NVIDIA GeForce RTX 3070", "Ampere")]
    [InlineData("NVIDIA GeForce RTX 4080", "Ada")]
    [InlineData("NVIDIA GeForce RTX 5090", "Blackwell")]
    public void RangeGatingClampsNvidiaAcrossGenerations(string gpuName, string generation)
    {
        var caps = NvidiaFixture(gpuName, generation);
        var req = new GpuManualProfile
        {
            Mode = "manual",
            CoreClockOffsetMhz = 999,
            MemoryClockOffsetMhz = 3000,
            PowerLimitPercent = 150,
            TempLimitC = 105,
            VoltageOffsetMv = -50,
            MemoryTimingLevel = "Fast Timing",
            ZeroRpm = true,
        };

        var result = GpuTuningPolicy.ValidateAndClamp(caps, req);
        Assert.True(result.Allowed);
        Assert.NotNull(result.Clamped);
        Assert.Equal(495, result.Clamped.CoreClockOffsetMhz);
        Assert.Equal(1500, result.Clamped.MemoryClockOffsetMhz);
        Assert.Equal(115, result.Clamped.PowerLimitPercent);
        Assert.Equal(90, result.Clamped.TempLimitC);
        Assert.Null(result.Clamped.VoltageOffsetMv);
        Assert.Null(result.Clamped.MemoryTimingLevel);
        Assert.Null(result.Clamped.ZeroRpm);
    }

    [Theory]
    [InlineData("AMD Radeon RX 5700 XT", "RDNA1", false)]
    [InlineData("AMD Radeon RX 6800 XT", "RDNA2", false)]
    [InlineData("AMD Radeon RX 7900 XTX", "RDNA3", true)]
    [InlineData("AMD Radeon RX 9070 XT", "RDNA4", true)]
    public void RangeGatingAdaptsAmdAcrossRdna1ThroughRdna4(string gpuName, string generation, bool expectsCurveOffset)
    {
        var caps = AmdFixture(gpuName, generation);
        Assert.Equal(expectsCurveOffset, caps.SupportsCurveEditor);
        Assert.Equal(expectsCurveOffset, caps.VoltageOffsetMv.Supported);
        Assert.Equal(!expectsCurveOffset, caps.VoltageMv.Supported);

        var req = new GpuManualProfile
        {
            Mode = "manual",
            CoreMinClockMhz = 2800,
            CoreMaxClockMhz = 2400,
            VoltageMv = 700,
            VoltageOffsetMv = -220,
            MemoryMaxClockMhz = 3500,
            MemoryTimingLevel = "Fast Timing",
            PowerLimitPercent = 25,
            ZeroRpm = true,
            FanCurve = [new(95, 100), new(35, 25), new(55, 45), new(70, 65), new(82, 85), new(100, 100)],
        };

        var result = GpuTuningPolicy.ValidateAndClamp(caps, req);
        Assert.True(result.Allowed);
        Assert.NotNull(result.Clamped);
        Assert.Equal(2400, result.Clamped.CoreMinClockMhz);
        Assert.Equal(2400, result.Clamped.CoreMaxClockMhz);
        Assert.Equal(2750, result.Clamped.MemoryMaxClockMhz);
        Assert.Equal("Fast Timing", result.Clamped.MemoryTimingLevel);
        Assert.Equal(15, result.Clamped.PowerLimitPercent);
        Assert.True(result.Clamped.ZeroRpm);
        Assert.Equal(5, result.Clamped.FanCurve?.Count);
        if (expectsCurveOffset)
        {
            Assert.Equal(-150, result.Clamped.VoltageOffsetMv);
            Assert.Null(result.Clamped.VoltageMv);
        }
        else
        {
            Assert.Equal(800, result.Clamped.VoltageMv);
            Assert.Null(result.Clamped.VoltageOffsetMv);
        }
    }

    [Fact]
    public void RangeGatingEnforcesIntelAlchemistVsBattlemageCapabilities()
    {
        var alchemist = IntelFixture("Intel Arc A770", "Alchemist", waiverAccepted: true);
        var battlemage = IntelFixture("Intel Arc B580", "Battlemage", waiverAccepted: true);

        Assert.False(alchemist.SupportsUndervoltSearch);
        Assert.True(battlemage.SupportsUndervoltSearch);

        var req = new GpuManualProfile
        {
            Mode = "manual",
            CoreClockOffsetMhz = 400,
            VoltageOffsetMv = -75,
            PowerLimitWatts = 250,
            MemoryMaxClockMhz = 19500,
        };

        var resA = GpuTuningPolicy.ValidateAndClamp(alchemist, req);
        Assert.True(resA.Allowed);
        Assert.Equal(300, resA.Clamped!.CoreClockOffsetMhz);
        Assert.Equal(0, resA.Clamped.VoltageOffsetMv);
        Assert.Equal(225, resA.Clamped.PowerLimitWatts);
        Assert.Null(resA.Clamped.MemoryMaxClockMhz);

        var resB = GpuTuningPolicy.ValidateAndClamp(battlemage, req);
        Assert.True(resB.Allowed);
        Assert.Equal(-75, resB.Clamped!.VoltageOffsetMv);
        Assert.Equal(19500, resB.Clamped.MemoryMaxClockMhz);
    }

    [Theory]
    [InlineData("nvidia", "NVIDIA GeForce RTX 4070 Laptop GPU")]
    [InlineData("amd", "AMD Radeon RX 7700S")]
    [InlineData("intel", "Intel Arc A370M")]
    public void LaptopLockedCardsShowLockedMessageAndRefuseApply(string vendor, string gpuName)
    {
        var locked = LaptopLockedFixture(vendor, gpuName);
        var backend = new FakeBackend(locked);
        using var service = new GpuManualTuningService(backend, _tuneStore, _dataPath, () => null);

        var state = service.ReadState();
        Assert.True(state.Capabilities.LockedByMaker);
        Assert.Equal(GpuTuningCapabilities.LockedMessage, state.StatusLine);

        var applied = service.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 50 });
        Assert.False(applied.Ok);
        Assert.Equal(GpuTuningCapabilities.LockedMessage, applied.Message);
        Assert.Equal(0, backend.ApplyCalls);
    }

    [Fact]
    public void PrivateNvApiIdsMatchResearchDocument()
    {
        Assert.Equal(0x21537AD4u, NvApiInterfaceIds.GetVfpCurve);
        Assert.Equal(0x23F1B133u, NvApiInterfaceIds.GetClockBoostTable);
        Assert.Equal(0x0733E009u, NvApiInterfaceIds.SetClockBoostTable);
        Assert.Equal(0xE440B867u, NvApiInterfaceIds.GetClockBoostLock);
        Assert.Equal(0x39442CFBu, NvApiInterfaceIds.SetClockBoostLock);
        Assert.Equal(0x64B43A6Au, NvApiInterfaceIds.GetClockBoostRanges);
        Assert.Equal(0x507B4B59u, NvApiInterfaceIds.GetClockBoostMask);
        Assert.Equal(0x6FF81213u, NvApiInterfaceIds.GetPstates20);
        Assert.Equal(0x0F4DAE6Bu, NvApiInterfaceIds.SetPstates20);
        Assert.Equal(0x34206D86u, NvApiInterfaceIds.ClientPowerPoliciesGetInfo);
        Assert.Equal(0x70916171u, NvApiInterfaceIds.ClientPowerPoliciesGetStatus);
        Assert.Equal(0xAD95F5EDu, NvApiInterfaceIds.ClientPowerPoliciesSetStatus);
        Assert.Equal(0x0D258BB5u, NvApiInterfaceIds.ClientThermalPoliciesGetInfo);
        Assert.Equal(0xE9C425A1u, NvApiInterfaceIds.ClientThermalPoliciesGetLimit);
        Assert.Equal(0x34C0B13Du, NvApiInterfaceIds.ClientThermalPoliciesSetLimit);
        Assert.Equal(0xFB85B01Eu, NvApiInterfaceIds.ClientFanCoolersGetInfo);
        Assert.Equal(0x814B209Fu, NvApiInterfaceIds.ClientFanCoolersGetControl);
        Assert.Equal(0xA58971A5u, NvApiInterfaceIds.ClientFanCoolersSetControl);
        Assert.Equal(0xDA141340u, NvApiInterfaceIds.GetCoolerSettings);
        Assert.Equal(0x891FA0AEu, NvApiInterfaceIds.SetCoolerLevels);
    }

    [Fact]
    public void UnknownNvApiInterfaceIdFallsBackCleanlyToNvmlWithoutMixing()
    {
        var nvapi = new NvidiaNvApiManualTuningBackend(() => false, _ => 0);
        var nvml = new FakeBackend(NvidiaFixture("NVIDIA GeForce RTX 3070", "Ampere", backend: "nvml", fanCount: 2));
        var fallback = new FallbackGpuTuningBackend(nvapi, nvml);

        var caps = fallback.Probe();
        Assert.Equal("nvml", caps.Backend);
        Assert.True(fallback.UsingFallback);
        Assert.NotNull(caps.FallbackReason);

        var applied = fallback.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 120, ClockLockMhz = 1950 });
        Assert.True(applied.Ok);
        Assert.Equal("nvml", applied.BackendUsed);
        Assert.Equal(1, nvml.ApplyCalls);
    }

    [Fact]
    public void RuntimeNvApiFailureSwitchesToNvmlAndNeverCallsNvApiAgain()
    {
        var primary = new FakeBackend(NvidiaFixture("NVIDIA GeForce RTX 4080", "Ada", backend: "nvapi"))
        {
            OnApply = _ => new(false, true, "NVAPI struct mismatch", "nvapi"),
        };
        var nvml = new FakeBackend(NvidiaFixture("NVIDIA GeForce RTX 4080", "Ada", backend: "nvml"));
        var combined = new FallbackGpuTuningBackend(primary, nvml);

        Assert.Equal("nvapi", combined.Probe().Backend);
        var first = combined.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 90 });
        Assert.True(first.Ok);
        Assert.Equal("nvml", first.BackendUsed);
        Assert.Equal(1, primary.ApplyCalls);
        Assert.Equal(1, nvml.ApplyCalls);

        var second = combined.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 105 });
        Assert.True(second.Ok);
        Assert.Equal(1, primary.ApplyCalls);
        Assert.Equal(2, nvml.ApplyCalls);
        Assert.Equal("nvml", combined.Probe().Backend);
    }

    [Fact]
    public void IntelConsentGateBlocksUntilAcceptedPerDevice()
    {
        var arcA770 = new FakeBackend(IntelFixture("Intel Arc A770", "Alchemist", waiverAccepted: false));
        using (var service = new GpuManualTuningService(arcA770, _tuneStore, _dataPath, () => null))
        {
            var denied = service.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 75 });
            Assert.False(denied.Ok);
            Assert.Contains("waiver", denied.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, arcA770.ApplyCalls);

            var waived = service.AcceptWaiver("intel:Intel Arc A770");
            Assert.True(waived.Ok);
            Assert.True(waived.Capabilities.WaiverAccepted);
            Assert.Equal(1, arcA770.WaiverCalls);

            var allowed = service.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 75 });
            Assert.True(allowed.Ok);
            Assert.Equal(1, arcA770.ApplyCalls);
        }

        var arcB580 = new FakeBackend(IntelFixture("Intel Arc B580", "Battlemage", waiverAccepted: false));
        using (var secondService = new GpuManualTuningService(arcB580, _tuneStore, _dataPath, () => null))
        {
            var deniedOtherDevice = secondService.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 50 });
            Assert.False(deniedOtherDevice.Ok);
            Assert.Equal(0, arcB580.ApplyCalls);
        }
    }

    [Fact]
    public void ProtectedGameQueuesApplyAndResetUntilExitAndRefusesTests()
    {
        var valorant = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot };
        var protectedAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, [DlssSwapService.RiotVanguard], "service");
        var eacGame = new GameEntry { Id = "steam:1938090", Title = "Apex Legends", Store = StoreKind.Steam };
        var eacAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "files");
        Assert.Equal(AntiCheatCheck.Category.ExternalDriver, AntiCheatCheck.CategoryOf(AntiCheatCheck.Change.GpuDriverTuning));
        Assert.True(AntiCheatCheck.Allows(eacGame, eacAnswer, AntiCheatCheck.Change.GpuDriverTuning));
        Assert.False(AntiCheatCheck.Allows(valorant, protectedAnswer, AntiCheatCheck.Change.GpuDriverTuning));
        Assert.Equal("VALORANT", AntiCheatCheck.ProtectedGameRunningName([(valorant, protectedAnswer)]));

        var indie = new GameEntry { Id = "steam:1", Title = "Hades", Store = StoreKind.Steam };
        var noneAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, [], "");
        Assert.Null(AntiCheatCheck.ProtectedGameRunningName([(indie, noneAnswer)]));

        string? runningProtected = "VALORANT";
        var backend = new FakeBackend(NvidiaFixture("NVIDIA GeForce RTX 3070", "Ampere"));
        using var service = new GpuManualTuningService(backend, _tuneStore, _dataPath, () => runningProtected);
        var events = new List<GpuManualTuningState>();
        service.StateChanged += s => events.Add(s);

        var queued = service.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 120 });
        Assert.True(queued.Ok);
        Assert.True(queued.QueuedPending);
        Assert.Contains("VALORANT", queued.QueuedReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.ApplyCalls);

        var testBlocked = service.StartStabilityTest("60s");
        Assert.False(testBlocked.Ok);
        Assert.Contains("VALORANT", testBlocked.Message!, StringComparison.OrdinalIgnoreCase);

        var uvBlocked = service.StartUndervoltSearch();
        Assert.False(uvBlocked.Ok);

        runningProtected = null;
        service.OnGameStateChanged();
        Assert.Equal(1, backend.ApplyCalls);
        Assert.Equal(120, backend.LastApplied?.CoreClockOffsetMhz);
        Assert.False(service.ReadState().QueuedPending);
        Assert.True(events.Count >= 2);
    }

    [Fact]
    public void StabilityTestRollsBackOnChecksumArtifactTdrClockDropAndOverheat()
    {
        var rollbackCount = 0;
        var opts = new GpuStabilityOptions(TimeSpan.FromMilliseconds(80), MaxTempC: 85, MaxClockDropRatio: 0.18, ClockDropConsecutiveSamples: 3, SampleInterval: TimeSpan.FromMilliseconds(1));

        var artifact = GpuStabilityTest.Run(
            () => new FakeComputeRunner(i => i < 4 ? 0xABCDEFUL : 0xDEADUL),
            new FakeSensorSampler(_ => new GpuSensors(68, null, 200, 1950, 1024, 8192, 1600, 99)),
            opts,
            () => rollbackCount++,
            null,
            () => false);
        Assert.False(artifact.Passed);
        Assert.False(artifact.ChecksumOk);
        Assert.True(artifact.RolledBack);
        Assert.Contains("checksum", artifact.FailureReason!, StringComparison.OrdinalIgnoreCase);

        var tdr = GpuStabilityTest.Run(
            () => new FakeComputeRunner(i => i == 2 ? throw new GpuLoad.Lost("The graphics driver reset.") : 0x1234UL),
            new FakeSensorSampler(_ => new GpuSensors(65, null, 200, 1950, 1024, 8192, 1600, 99)),
            opts,
            () => rollbackCount++,
            null,
            () => false);
        Assert.False(tdr.Passed);
        Assert.True(tdr.RolledBack);
        Assert.Contains("reset", tdr.FailureReason!, StringComparison.OrdinalIgnoreCase);

        var clockDrop = GpuStabilityTest.Run(
            () => new FakeComputeRunner(_ => 0x5555UL),
            new FakeSensorSampler(n => new GpuSensors(70, null, 200, n <= 2 ? 2000 : 1400, 1024, 8192, 1600, 99)),
            opts with { Duration = TimeSpan.FromSeconds(2) },
            () => rollbackCount++,
            null,
            () => false);
        Assert.False(clockDrop.Passed);
        Assert.True(clockDrop.RolledBack);
        Assert.Contains("clock drop", clockDrop.FailureReason!, StringComparison.OrdinalIgnoreCase);

        var hot = GpuStabilityTest.Run(
            () => new FakeComputeRunner(_ => 0x5555UL),
            new FakeSensorSampler(_ => new GpuSensors(88, null, 220, 1950, 1024, 8192, 2200, 99)),
            opts with { Duration = TimeSpan.FromSeconds(2) },
            () => rollbackCount++,
            null,
            () => false);
        Assert.False(hot.Passed);
        Assert.True(hot.RolledBack);
        Assert.Contains("temperature", hot.FailureReason!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(4, rollbackCount);
    }

    [Fact]
    public void CrashGuardPausesAfterCrashWithinWindowAndPreservesSavedProfileForReapply()
    {
        var appliedAt = new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
        var events = new List<TuneCrashEvent>
        {
            new("Display", 4101, appliedAt.AddMinutes(3)),
        };
        var backend = new FakeBackend(IntelFixture("Intel Arc B580", "Battlemage", waiverAccepted: true));
        using var service = new GpuManualTuningService(
            backend,
            _tuneStore,
            _dataPath,
            () => null,
            now: () => appliedAt,
            recentCrashEvents: () => events);

        Assert.True(service.Apply(new GpuManualProfile { Mode = "manual", CoreClockOffsetMhz = 100 }).Ok);
        var paused = service.EvaluateLifecycleTrigger("logon");
        Assert.True(paused.PausedAfterCrash);
        Assert.Equal(TunePersistence.PausedNote, paused.PauseReason);
        Assert.True(paused.CanReapply);
        Assert.Equal(100, paused.Saved.CoreClockOffsetMhz);
        Assert.Equal("auto", paused.Mode);
        Assert.Equal(1, backend.ResetCalls);

        events.Clear();
        var reapplied = service.ReapplySaved();
        Assert.True(reapplied.Ok);
        Assert.False(reapplied.PausedAfterCrash);
        Assert.Equal("manual", reapplied.Mode);
        Assert.Equal(2, backend.ApplyCalls);
    }

    [Fact]
    public void UndervoltSearchStopsAtFirstInstabilityAndBacksOffTwoSteps()
    {
        var nvidiaCaps = NvidiaFixture("NVIDIA GeForce RTX 3070", "Ampere");
        var stock = GpuManualProfile.Auto;
        var applied = new List<GpuManualProfile>();
        var resets = 0;

        var res = GpuUndervoltSearch.Run(
            nvidiaCaps,
            stock,
            p => { applied.Add(p); return new(true, false, null, "nvapi"); },
            () => { resets++; return new(true, false, null, "nvapi"); },
            step => step.StepIndex >= 5
                ? new(false, false, true, false, 10, 5, 72, 2000, 1950, 2000, "Artifact detected")
                : new(true, false, false, true, 50, 15, 68, 2000, 1980, 2010, null),
            null,
            () => false,
            stockPeakClockMhz: 1950);

        Assert.True(res.Completed);
        Assert.True(res.AppliedSafeProfile);
        Assert.Equal(5, res.FailedAtStepIndex);
        Assert.Equal(3, res.AppliedStepIndex);
        Assert.Equal(45, res.FinalProfile.CoreClockOffsetMhz);
        Assert.Equal(1, resets);

        var amdCaps = AmdFixture("AMD Radeon RX 7900 XTX", "RDNA3");
        var amdRes = GpuUndervoltSearch.Run(
            amdCaps,
            stock,
            _ => new(true, false, null, "adlx"),
            () => new(true, false, null, "adlx"),
            step => step.StepIndex >= 4
                ? new(false, false, true, true, 12, 4, 78, 2500, 2400, 2500, "Driver reset")
                : new(true, false, false, true, 40, 15, 74, 2500, 2480, 2520, null),
            null,
            () => false);

        Assert.True(amdRes.Completed);
        Assert.True(amdRes.AppliedSafeProfile);
        Assert.Equal(4, amdRes.FailedAtStepIndex);
        Assert.Equal(2, amdRes.AppliedStepIndex);
        Assert.Equal(-20, amdRes.FinalProfile.VoltageOffsetMv);

        var earlyFail = GpuUndervoltSearch.Run(
            amdCaps,
            stock,
            _ => new(true, false, null, "adlx"),
            () => new(true, false, null, "adlx"),
            step => step.StepIndex >= 2
                ? new(false, false, true, false, 5, 2, 75, 2500, 2400, 2500, "Checksum mismatch")
                : new(true, false, false, true, 20, 15, 70, 2500, 2500, 2500, null),
            null,
            () => false);

        Assert.True(earlyFail.Completed);
        Assert.False(earlyFail.AppliedSafeProfile);
        Assert.Equal(0, earlyFail.AppliedStepIndex);
        Assert.Equal("auto", earlyFail.FinalProfile.Mode);
    }

    [Fact]
    public void FanCurveInterpolationHysteresisAndSafetyClampHold()
    {
        var curve = new GpuFanPoint[] { new(30, 30), new(50, 50), new(70, 75), new(80, 88), new(85, 100) };
        Assert.Equal(40, GpuManualTuningService.EvaluateFanCurve(curve, 40, null, null));
        Assert.Equal(50, GpuManualTuningService.EvaluateFanCurve(curve, 48, lastSpeed: 50, lastTempC: 50));
        Assert.Equal(46, GpuManualTuningService.EvaluateFanCurve(curve, 46, lastSpeed: 50, lastTempC: 50));
        Assert.Equal(85, GpuManualTuningService.EvaluateFanCurve([new(30, 20), new(90, 40)], 80, null, null));
        Assert.Equal(100, GpuManualTuningService.EvaluateFanCurve([new(30, 20), new(90, 40)], 85, null, null));
    }
}
