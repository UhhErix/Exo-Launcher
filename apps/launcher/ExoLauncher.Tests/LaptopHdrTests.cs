using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LaptopHdrTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "exo-hdr-test-" + Guid.NewGuid().ToString("N"));

    public LaptopHdrTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) DeletionGate.DeleteDirectory(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, "", false)]
    [InlineData(0u, "", false)]
    [InlineData(1u, "", false)]
    [InlineData(2u, "", false)]
    [InlineData(3u, "", false)]
    [InlineData(4u, "dvi", false)]
    [InlineData(5u, "hdmi", false)]
    [InlineData(6u, "", true)]
    [InlineData(8u, "", false)]
    [InlineData(9u, "", false)]
    [InlineData(10u, "dp", false)]
    [InlineData(11u, "dp", true)]
    [InlineData(12u, "", false)]
    [InlineData(13u, "", false)]
    [InlineData(14u, "", false)]
    [InlineData(15u, "", false)]
    [InlineData(16u, "", false)]
    [InlineData(17u, "", false)]
    [InlineData(18u, "dp", false)]
    [InlineData(0x80000000u, "", true)]
    public void Classifies_connector_and_internal_panel_for_every_output_technology(uint technology, string expectedConnector, bool expectedInternal)
    {
        Assert.Equal(expectedConnector, WindowsHdr.Connector(technology));
        Assert.Equal(expectedInternal, WindowsHdr.IsInternalTechnology(technology));
    }

    [Theory]
    [InlineData(true, 1000, 1000, "ok", false)]
    [InlineData(true, 1000, 1100, "ok", false)]
    [InlineData(true, 1000, 900, "ok", false)]
    [InlineData(true, 1000, 1101, "mismatch", true)]
    [InlineData(true, 1260, 160, "mismatch", true)]
    [InlineData(true, 1260, 13440, "mismatch", true)]
    [InlineData(false, 1000, 1050, "ok", false)]
    [InlineData(false, 1000, 1400, "reported", false)]
    [InlineData(true, null, 1000, "unknown", false)]
    [InlineData(true, 1000, null, "unknown", false)]
    [InlineData(false, 0, 1000, "unknown", false)]
    public void Peak_health_check_enforces_ten_percent_threshold_and_internal_only_warning(
        bool isInternal, int? edid, int? dxgi, string expectedStatus, bool expectWarning)
    {
        var health = HdrDisplayHealth.Evaluate(isInternal, edid, dxgi);
        Assert.Equal(expectedStatus, health.Status);
        if (expectWarning)
        {
            Assert.NotNull(health.Warning);
            Assert.Contains(edid!.Value.ToString(), health.Warning);
            Assert.Contains(dxgi!.Value.ToString(), health.Warning);
        }
        else
        {
            Assert.Null(health.Warning);
        }
    }

    [Fact]
    public void Binary_search_converges_across_160_to_13440_nit_swing_and_restores_user_pair()
    {
        var store = new HdrCalibrationStore(Path.Combine(_tempDir, "cal-swing.json"));
        var backend = new SyntheticBackend(
            isInternal: true,
            edidPeakNits: 1260,
            initialBrightness: 65,
            initialSdrRaw: HdrBrightness.SliderToRaw(20),
            model: slider => (float)(160.0 * Math.Pow(13440.0 / 160.0, slider / 100.0)));

        var progressCount = 0;
        var result = HdrCalibrator.Run(backend, store, _ => progressCount++, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("ok", result.Status);
        Assert.NotNull(result.Entry);
        Assert.True(progressCount >= 5);
        Assert.InRange(result.Entry!.MeasuredPeakNits, 1200, 1320);
        Assert.Equal(65, result.Entry.GlobalBrightness);
        Assert.Equal(65, backend.GlobalBrightness);
        Assert.Equal(HdrBrightness.SliderToRaw(20), backend.SdrWhiteRaw);
        Assert.Equal(1260, store.ValidPeakFor(backend.DevicePath));
    }

    [Fact]
    public void Binary_search_recovers_from_non_monotonic_glitch()
    {
        var store = new HdrCalibrationStore(Path.Combine(_tempDir, "cal-glitch.json"));
        var backend = new SyntheticBackend(
            isInternal: true,
            edidPeakNits: 1260,
            initialBrightness: 50,
            initialSdrRaw: HdrBrightness.SliderToRaw(10),
            model: slider =>
            {
                if (slider == 50) return 9500f;
                return (float)(160.0 * Math.Pow(13440.0 / 160.0, slider / 100.0));
            });

        var result = HdrCalibrator.Run(backend, store, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.NotNull(result.Entry);
        Assert.InRange(result.Entry!.MeasuredPeakNits, 1200, 1320);
        Assert.NotEqual(HdrBrightness.SliderToRaw(50), result.Entry.SdrWhiteRaw);
        Assert.Equal(50, backend.GlobalBrightness);
        Assert.Equal(HdrBrightness.SliderToRaw(10), backend.SdrWhiteRaw);
    }

    [Fact]
    public void Binary_search_handles_stale_metadata_step_by_nudging_and_re_reading()
    {
        var store = new HdrCalibrationStore(Path.Combine(_tempDir, "cal-stale.json"));
        var backend = new SyntheticBackend(
            isInternal: true,
            edidPeakNits: 1260,
            initialBrightness: 70,
            initialSdrRaw: HdrBrightness.SliderToRaw(15),
            model: slider => (float)(160.0 * Math.Pow(13440.0 / 160.0, slider / 100.0)),
            staleOnReadNumber: 4);

        var result = HdrCalibrator.Run(backend, store, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.NotNull(result.Entry);
        Assert.InRange(result.Entry!.MeasuredPeakNits, 1200, 1320);
        Assert.True(backend.FreshDxgiQueries >= 6);
        Assert.Equal(70, backend.GlobalBrightness);
        Assert.Equal(HdrBrightness.SliderToRaw(15), backend.SdrWhiteRaw);
    }

    [Fact]
    public void Skips_calibration_when_edid_peak_is_unknown()
    {
        var backend = new SyntheticBackend(
            isInternal: true,
            edidPeakNits: null,
            initialBrightness: 60,
            initialSdrRaw: HdrBrightness.SliderToRaw(30),
            model: _ => 1000f);

        var result = HdrCalibrator.Run(backend, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("unknown_edid", result.Status);
        Assert.Equal(0, backend.SdrWriteCount);
        Assert.Equal(0, backend.BrightnessWriteCount);
    }

    [Fact]
    public void Reports_unsupported_api_honestly_with_manual_steps_and_never_fakes_success()
    {
        var store = new HdrCalibrationStore(Path.Combine(_tempDir, "cal-unsupported.json"));
        var backend = new SyntheticBackend(
            isInternal: true,
            edidPeakNits: 1000,
            initialBrightness: 60,
            initialSdrRaw: HdrBrightness.SliderToRaw(30),
            model: _ => 1000f,
            unsupportedSdrApi: true);

        var result = HdrCalibrator.Run(backend, store, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("unsupported_api", result.Status);
        Assert.NotNull(result.ManualSteps);
        Assert.NotEmpty(result.ManualSteps!);
        Assert.Null(store.Get(backend.DevicePath));
    }

    [Fact]
    public void Neutral_pair_saves_applies_warns_on_midgame_change_restores_on_exit_and_recovers_after_crash()
    {
        var journalPath = Path.Combine(_tempDir, "hdr-journal.json");
        var calPath = Path.Combine(_tempDir, "hdr-cal.json");
        var store = new HdrCalibrationStore(calPath);
        var screen = MakeScreen("DISPLAY#PANEL#1", @"\\.\DISPLAY1", isInternal: true, supported: true, enabled: true);

        store.Save(new HdrCalibrationEntry(
            screen.DevicePath, screen.Name, 1260, 1255, 80, HdrBrightness.SliderToRaw(47), 268.0, true, DateTime.UtcNow.ToString("O")));

        var fake = new FakeDisplayControl(screen, initialSdrRaw: HdrBrightness.SliderToRaw(10), initialBrightness: 45);
        var journal = new HdrJournal(journalPath);
        var controller = new HdrSessionController(fake, journal, store);

        string? raisedKind = null;
        controller.WarningRaised += (_, kind, _) => raisedKind = kind;

        Assert.True(controller.ApplyNeutralPairForGame(screen));
        Assert.Equal(80, fake.Brightness);
        Assert.Equal(HdrBrightness.SliderToRaw(47), fake.SdrRaw);
        Assert.Single(journal.Entries);

        Assert.False(controller.CheckMidGameBrightnessChange());
        fake.Brightness = 55;
        Assert.True(controller.CheckMidGameBrightnessChange());
        Assert.Equal("brightness_changed", raisedKind);
        Assert.NotNull(controller.BrightnessWarning);

        controller.RestoreAll();
        Assert.Equal(45, fake.Brightness);
        Assert.Equal(HdrBrightness.SliderToRaw(10), fake.SdrRaw);
        Assert.Empty(journal.Entries);

        Assert.True(controller.ApplyNeutralPairForGame(screen));
        Assert.Single(journal.Entries);

        var restartedJournal = new HdrJournal(journalPath);
        var restartedController = new HdrSessionController(fake, restartedJournal, store);
        Assert.Equal(1, restartedController.RecoverFromJournal());
        Assert.Equal(45, fake.Brightness);
        Assert.Equal(HdrBrightness.SliderToRaw(10), fake.SdrRaw);
        Assert.Empty(restartedJournal.Entries);
    }

    [Fact]
    public void Cursor_fix_gates_on_external_hdr_under_160_nits_and_restores_on_exit()
    {
        Assert.True(HdrDisplayHealth.IsCursorFixEligible(isInternal: false, hdrSupported: true, sdrWhiteNits: 120, alreadyEnabled: false));
        Assert.False(HdrDisplayHealth.IsCursorFixEligible(isInternal: false, hdrSupported: true, sdrWhiteNits: 160, alreadyEnabled: false));
        Assert.True(HdrDisplayHealth.IsCursorFixEligible(isInternal: false, hdrSupported: true, sdrWhiteNits: 220, alreadyEnabled: true));
        Assert.False(HdrDisplayHealth.IsCursorFixEligible(isInternal: true, hdrSupported: true, sdrWhiteNits: 80, alreadyEnabled: false));
        Assert.False(HdrDisplayHealth.IsCursorFixEligible(isInternal: false, hdrSupported: false, sdrWhiteNits: 80, alreadyEnabled: false));

        var journal = new HdrJournal(Path.Combine(_tempDir, "cursor-journal.json"));
        var store = new HdrCalibrationStore(Path.Combine(_tempDir, "cursor-cal.json"));
        var extScreen = MakeScreen("DISPLAY#EXT#1", @"\\.\DISPLAY2", isInternal: false, supported: true, enabled: true);
        var fake = new FakeDisplayControl(extScreen, initialSdrRaw: HdrBrightness.NitsToRaw(100), initialBrightness: null);
        var controller = new HdrSessionController(fake, journal, store);

        Assert.False(controller.ApplyCursorFixForGame(extScreen, optIn: false));
        Assert.True(controller.ApplyCursorFixForGame(extScreen, optIn: true));
        Assert.Equal(HdrBrightness.NitsToRaw(220), fake.SdrRaw);
        Assert.Single(journal.Entries);

        controller.RestoreAll();
        Assert.Equal(HdrBrightness.NitsToRaw(100), fake.SdrRaw);
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public void Laptop_checks_acm_autohdr_and_unreal_peak_feed_behave_honestly()
    {
        Assert.Equal("sdr", WindowsHdr.ActiveColorModeName(0));
        Assert.Equal("acm", WindowsHdr.ActiveColorModeName(1));
        Assert.Equal("hdr", WindowsHdr.ActiveColorModeName(2));
        Assert.Null(WindowsHdr.ActiveColorModeName(99));

        Assert.True(LaptopDisplayChecks.ParseAutoHdrEnable("GpuPreference=2;AutoHDREnable=2097;"));
        Assert.False(LaptopDisplayChecks.ParseAutoHdrEnable("AutoHDREnable=2096;"));
        Assert.False(LaptopDisplayChecks.ParseAutoHdrEnable("AutoHDREnable=0;"));
        Assert.True(LaptopDisplayChecks.ParseAutoHdrEnable("SwapEffectUpgradeEnable=1;AutoHDREnable=1;"));
        Assert.Null(LaptopDisplayChecks.ParseAutoHdrEnable("GpuPreference=2;"));

        Assert.Equal("off", LaptopDisplayChecks.ParseCabcOption(0));
        Assert.Equal("active", LaptopDisplayChecks.ParseCabcOption(1));
        Assert.Equal("battery_only", LaptopDisplayChecks.ParseCabcOption(2));
        Assert.Equal("unknown", LaptopDisplayChecks.ParseCabcOption(null));

        Assert.Equal("active", LaptopDisplayChecks.ParseIntelFeatureTestControl(0x9240));
        Assert.Equal("off", LaptopDisplayChecks.ParseIntelFeatureTestControl(0x9250));
        Assert.Equal("unknown", LaptopDisplayChecks.ParseIntelFeatureTestControl(null));

        var pluggedHdr = LaptopDisplayChecks.Evaluate(new LaptopDisplayChecks.RawSignals(
            IsLaptop: true, PluggedIn: true, HdrOn: true, ColorMode: "hdr", AutoHdrGlobal: true,
            BatterySaver: "active", Cabc: "active", IntelDpst: "unknown", AmdVariBright: "active"));
        Assert.True(pluggedHdr.Items.Single(i => i.Id == "batterySaver").Warn);
        Assert.True(pluggedHdr.Items.Single(i => i.Id == "cabc").Warn);
        Assert.True(pluggedHdr.Items.Single(i => i.Id == "amdVariBright").Warn);
        var dpst = pluggedHdr.Items.Single(i => i.Id == "intelDpst");
        Assert.Equal("unknown", dpst.Status);
        Assert.False(dpst.Warn);
        Assert.Contains("Can't detect", dpst.Detail);

        var onBattery = LaptopDisplayChecks.Evaluate(new LaptopDisplayChecks.RawSignals(
            IsLaptop: true, PluggedIn: false, HdrOn: true, ColorMode: "hdr", AutoHdrGlobal: null,
            BatterySaver: "active", Cabc: "battery_only", IntelDpst: "not_applicable", AmdVariBright: "not_applicable"));
        Assert.All(onBattery.Items, item => Assert.False(item.Warn));

        var screen = MakeScreen("DISPLAY#LAPTOP#1", @"\\.\DISPLAY1", isInternal: true, supported: true, enabled: true);
        var peak = GameHdr.PeakNits(screen, null, neutralCalibratedPeak: 1260);
        Assert.NotNull(peak);
        Assert.Equal((1260, "neutral-pair"), peak!.Value);
        var ueGroup = GameSettings.UnrealHdrGroup(peak.Value.Nits);
        Assert.Equal("1260", ueGroup.Changes.Single().Value);
    }

    private static WindowsHdr.Screen MakeScreen(string devicePath, string gdiName, bool isInternal, bool supported, bool enabled) =>
        new(default, 1, 0, gdiName, "Test Panel", devicePath, supported, enabled, true, 0, isInternal ? "dp" : "hdmi", 120, isInternal, isInternal ? 11u : 5u, enabled ? "hdr" : "sdr");

    private sealed class SyntheticBackend(
        bool isInternal,
        int? edidPeakNits,
        int initialBrightness,
        uint initialSdrRaw,
        Func<int, float> model,
        bool unsupportedSdrApi = false,
        int staleOnReadNumber = -1) : ICalibrationBackend
    {
        private float _lastReported;
        public string DevicePath => "DISPLAY#SYNTH#1";
        public string ScreenName => "Synthetic Panel";
        public bool IsInternal => isInternal;
        public int? EdidPeakNits => edidPeakNits;
        public int GlobalBrightness { get; private set; } = initialBrightness;
        public uint SdrWhiteRaw { get; private set; } = initialSdrRaw;
        public int SdrWriteCount { get; private set; }
        public int BrightnessWriteCount { get; private set; }
        public int FreshDxgiQueries { get; private set; }

        public int? ReadGlobalBrightness() => GlobalBrightness;

        public bool WriteGlobalBrightness(int level)
        {
            BrightnessWriteCount++;
            GlobalBrightness = level;
            return true;
        }

        public uint? ReadSdrWhiteRaw() => SdrWhiteRaw;

        public HdrBrightness.WriteSdrResult WriteSdrWhiteRaw(uint rawLevel)
        {
            SdrWriteCount++;
            if (unsupportedSdrApi) return new HdrBrightness.WriteSdrResult(false, 50, true);
            SdrWhiteRaw = rawLevel;
            return new HdrBrightness.WriteSdrResult(true, 0, false);
        }

        public float? ReadFreshDxgiMaxLuminance()
        {
            FreshDxgiQueries++;
            if (FreshDxgiQueries == staleOnReadNumber && _lastReported > 0)
                return _lastReported;
            var slider = HdrBrightness.RawToSlider(SdrWhiteRaw);
            _lastReported = model(slider);
            return _lastReported;
        }

        public void Settle(CancellationToken ct) => ct.ThrowIfCancellationRequested();
    }

    private sealed class FakeDisplayControl(WindowsHdr.Screen screen, uint initialSdrRaw, int? initialBrightness) : IHdrDisplayControl
    {
        public uint SdrRaw { get; set; } = initialSdrRaw;
        public int? Brightness { get; set; } = initialBrightness;

        public WindowsHdr.Screen? FindScreen(string devicePath) =>
            string.Equals(screen.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase) ? screen : null;

        public uint? ReadSdrWhiteRaw(WindowsHdr.Screen s) => SdrRaw;

        public HdrBrightness.WriteSdrResult WriteSdrWhiteRaw(WindowsHdr.Screen s, uint rawLevel)
        {
            SdrRaw = rawLevel;
            return new HdrBrightness.WriteSdrResult(true, 0, false);
        }

        public int? ReadPanelBrightness() => Brightness;

        public bool WritePanelBrightness(int level)
        {
            Brightness = level;
            return true;
        }
    }
}
