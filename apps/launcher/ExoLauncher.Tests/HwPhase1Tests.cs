using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class HwPhase1Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-hw-phase1-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, AutopilotJournal.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) DeletionGate.DeleteDirectory(_dir, recursive: true);
    }

    [Fact]
    public void GpuProcessMonitorParsesPdhInstancesAndLuids()
    {
        Assert.True(GpuProcessMonitor.TryParseLuid("0x00000000", "0x00010A4F", out var luid));
        Assert.Equal("0x00000000_0x00010A4F", GpuProcessMonitor.FormatLuid(luid));

        var eng = GpuProcessMonitor.ParseEngineInstance("pid_4120_luid_0x00000000_0x00010A4F_phys_0_eng_0_engtype_3D");
        Assert.NotNull(eng);
        Assert.Equal((4120, luid, "3D"), eng.Value);

        var dec = GpuProcessMonitor.ParseEngineInstance("pid_4120_luid_0x00000000_0x00010A4F_phys_0_eng_2_engtype_VideoDecode");
        Assert.Equal("Video Decode", dec!.Value.EngineType);

        var emptyType = GpuProcessMonitor.ParseEngineInstance("pid_500_luid_0x00000000_0x00010A4F_phys_0_eng_1_engtype_");
        Assert.Equal("Other", emptyType!.Value.EngineType);

        var mem = GpuProcessMonitor.ParseMemoryInstance("pid_4120_luid_0x00000000_0x00010A4F_phys_0");
        Assert.Equal((4120, luid), mem!.Value);

        Assert.Null(GpuProcessMonitor.ParseEngineInstance("pid_0_luid_0x00000000_0x00010A4F_phys_0_eng_0_engtype_3D"));
        Assert.Null(GpuProcessMonitor.ParseEngineInstance("_Total"));
        Assert.Null(GpuProcessMonitor.ParseMemoryInstance("invalid"));
    }

    [Fact]
    public void GpuProcessMonitorAggregatesEnginesVramAdaptersAndSummarizesAnswer()
    {
        GpuProcessMonitor.TryParseLuid("0x00000000", "0x00010A4F", out var dgpuLuid);
        GpuProcessMonitor.TryParseLuid("0x00000000", "0x00020B10", out var igpuLuid);

        var adapters = new GpuAdapterInfo[]
        {
            new(dgpuLuid, GpuProcessMonitor.FormatLuid(dgpuLuid), "NVIDIA GeForce RTX 3070", 0x10DE, 8L << 30, 8L << 30, false),
            new(igpuLuid, GpuProcessMonitor.FormatLuid(igpuLuid), "AMD Radeon Graphics", 0x1002, 512L << 20, 8L << 30, false),
        };
        var names = new Dictionary<int, string>
        {
            [4120] = "chrome",
            [5200] = "ollama",
            [1024] = "dwm",
        };
        var engines = new Dictionary<string, double>
        {
            ["pid_4120_luid_0x00000000_0x00010A4F_phys_0_eng_0_engtype_3D"] = 12.4,
            ["pid_4120_luid_0x00000000_0x00010A4F_phys_0_eng_1_engtype_3D"] = 5.6,
            ["pid_4120_luid_0x00000000_0x00010A4F_phys_0_eng_2_engtype_VideoDecode"] = 4.2,
            ["pid_1024_luid_0x00000000_0x00020B10_phys_0_eng_0_engtype_3D"] = 2.5,
        };
        var local = new Dictionary<string, double>
        {
            ["pid_4120_luid_0x00000000_0x00010A4F_phys_0"] = 400.0 * 1024 * 1024,
            ["pid_5200_luid_0x00000000_0x00010A4F_phys_0"] = 4.2 * 1024 * 1024 * 1024,
            ["pid_1024_luid_0x00000000_0x00020B10_phys_0"] = 180.0 * 1024 * 1024,
        };
        var nonLocal = new Dictionary<string, double>
        {
            ["pid_4120_luid_0x00000000_0x00010A4F_phys_0"] = 96.0 * 1024 * 1024,
        };

        var snap = GpuProcessMonitor.Aggregate(engines, local, nonLocal, adapters, names, maxProcesses: 10, selfPid: 9999);

        Assert.Equal(3, snap.Processes.Count);
        var chrome = snap.Processes[0];
        Assert.Equal("chrome", chrome.Name);
        Assert.Equal("NVIDIA GeForce RTX 3070", chrome.AdapterName);
        Assert.Equal(18.0, chrome.TotalPercent);
        Assert.Equal(2, chrome.Engines.Count);
        Assert.Equal(("3D", 18.0), (chrome.Engines[0].Type, chrome.Engines[0].Percent));
        Assert.Equal(("Video Decode", 4.2), (chrome.Engines[1].Type, chrome.Engines[1].Percent));
        Assert.Equal(96L << 20, chrome.NonLocalBytes);

        var dwm = snap.Processes.Single(p => p.Pid == 1024);
        Assert.Equal("AMD Radeon Graphics", dwm.AdapterName);
        Assert.NotNull(dwm.ProtectedReason);

        Assert.Equal("chrome", snap.Answer.TopGpu?.Name);
        Assert.Equal("ollama", snap.Answer.TopVram?.Name);
        Assert.Contains("chrome is using 18% GPU (3D)", snap.Answer.Summary);
        Assert.Contains("ollama holds the most VRAM", snap.Answer.Summary);
        Assert.DoesNotContain(snap.Answer.Hogs, h => h.Pid == 1024);
        Assert.Contains(snap.Answer.Hogs, h => h.Pid == 4120);
        Assert.Contains(snap.Answer.Hogs, h => h.Pid == 5200);

        var idle = GpuProcessMonitor.Aggregate(null, null, null, adapters, names);
        Assert.Empty(idle.Processes);
        Assert.Equal("Nothing is actively using your GPU right now.", idle.Answer.Summary);
    }

    [Fact]
    public void AutopilotGpuHogRulesAndEngineDemoteBackgroundGpuHogsOnly()
    {
        Assert.True(AutopilotRules.GpuHog(10.0, 0));
        Assert.True(AutopilotRules.GpuHog(3.0, 256L << 20));
        Assert.False(AutopilotRules.GpuHog(2.9, 512L << 20));
        Assert.False(AutopilotRules.GpuHog(9.9, 64L << 20));

        var pc = new FakeGpuAutopilotSystem();
        pc.Add(9000, "ExoLauncher.exe");
        pc.Add(100, "Game.exe");
        pc.Add(200, "chrome.exe");
        pc.Add(201, "wallpaper32.exe");
        pc.Add(202, "notepad.exe");
        pc.Add(203, "obs64.exe");

        pc.Gpu[100] = new GpuProcessLoad(85.0, 5L << 30, 0);
        pc.Gpu[200] = new GpuProcessLoad(15.0, 128L << 20, 0);
        pc.Gpu[201] = new GpuProcessLoad(4.0, 400L << 20, 0);
        pc.Gpu[202] = new GpuProcessLoad(0.5, 32L << 20, 0);
        pc.Gpu[203] = new GpuProcessLoad(22.0, 600L << 20, 0);

        var options = new AutopilotSettings { On = true };
        using var timer = new DummyDisposable();
        var engine = new AutopilotEngine(pc, new AutopilotJournal(JournalPath), () => options, () => PowerProfiles.CpuClass.AmdSingleCcd, _ => timer, _ => { }, self: 9000);

        engine.Follow(new AutopilotGame("steam:1", "The Game", [100], true, []));

        Assert.Equal(AutopilotRules.EcoOn, pc.Values[(Knob.Eco, 200)]);
        Assert.Equal(AutopilotRules.BelowNormal, pc.Values[(Knob.Priority, 200)]);
        Assert.Equal(AutopilotRules.EcoOn, pc.Values[(Knob.Eco, 201)]);
        Assert.Equal(AutopilotRules.EcoDefault, pc.Values[(Knob.Eco, 202)]);
        Assert.Equal(AutopilotRules.EcoDefault, pc.Values[(Knob.Eco, 203)]);

        engine.Follow(null);
        Assert.Equal(AutopilotRules.EcoDefault, pc.Values[(Knob.Eco, 200)]);
        Assert.Equal(AutopilotRules.EcoDefault, pc.Values[(Knob.Eco, 201)]);
    }

    [Fact]
    public void IntelVminShiftMicrocodeDetectsVulnerableDesktopRaptorLakeOnly()
    {
        var rev125 = new byte[8];
        BitConverter.GetBytes(0x00000125u).CopyTo(rev125, 4);
        Assert.Equal(0x125u, PcHealth.ParseMicrocodeRevision(rev125));
        Assert.Null(PcHealth.ParseMicrocodeRevision(new byte[8]));

        Assert.Equal((6, 0xB7), PcHealth.ParseIntelFamilyModel("Intel64 Family 6 Model 183 Stepping 1"));

        var old14700k = PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) i7-14700K", "Intel64 Family 6 Model 183 Stepping 1", 0x125, laptop: false);
        Assert.Equal((0x125u, true), old14700k);

        var partial12b = PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) i9-13900K", "Intel64 Family 6 Model 183 Stepping 1", 0x12B, laptop: false);
        Assert.Equal((0x12Bu, false), partial12b);

        var c0i5 = PcHealth.IntelVminShiftMicrocode("13th Gen Intel(R) Core(TM) i5-13400F", "Intel64 Family 6 Model 191 Stepping 2", 0x32, laptop: false);
        Assert.NotNull(c0i5);

        Assert.Null(PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) i7-14700K", "Intel64 Family 6 Model 183 Stepping 1", 0x12F, laptop: false));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) i7-14700K", "Intel64 Family 6 Model 183 Stepping 1", 0x132, laptop: false));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("12th Gen Intel(R) Core(TM) i7-12700K", "Intel64 Family 6 Model 151 Stepping 2", 0x35, laptop: false));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("12th Gen Intel(R) Core(TM) i5-12400", "Intel64 Family 6 Model 191 Stepping 5", 0x28, laptop: false));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) Ultra 7 265K", "Intel64 Family 6 Model 198 Stepping 2", 0x114, laptop: false));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) i9-14900HX", "Intel64 Family 6 Model 183 Stepping 1", 0x125, laptop: true));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("Intel(R) Core(TM) i5-14500T", "Intel64 Family 6 Model 191 Stepping 2", 0x32, laptop: false));
        Assert.Null(PcHealth.IntelVminShiftMicrocode("AMD Ryzen 5 5600X 6-Core Processor", "AMD64 Family 25 Model 33 Stepping 0", 0x100, laptop: false));

        var reading = new PcHealth.Reading([], null, "Intel(R) Core(TM) i7-14700K", null, null, null,
            BoardMaker: "Micro-Star International Co., Ltd.", BoardProduct: "PRO Z790-A WIFI",
            CpuIdentifier: "Intel64 Family 6 Model 183 Stepping 1", MicrocodeRevision: 0x125);
        var item = Assert.Single(PcHealth.Decide(reading), i => i.Key == "cpumicrocode");
        Assert.Contains("0x125", item.Detail);
        Assert.Contains("0x12F", item.Detail);
        Assert.Contains("msi.com", item.Url);
    }

    [Fact]
    public void RyzenBranchPredictionAdvisoryFlagsOnlyMissingBuildsOnRyzen()
    {
        Assert.True(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 7 7800X3D 8-Core Processor", 19045, 4894));
        Assert.True(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 9 9950X 16-Core Processor", 22631, 3958));
        Assert.True(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 5 5600X 6-Core Processor", 22000, 2538));
        Assert.False(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 7 7800X3D 8-Core Processor", 22631, 4112));
        Assert.False(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 7 7800X3D 8-Core Processor", 26100, 1742));
        Assert.False(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 9 9950X 16-Core Processor", 26200, 100));
        Assert.False(PcHealth.RyzenBranchPredictionMissing("Intel(R) Core(TM) i7-14700K", 19045, 4894));
        Assert.False(PcHealth.RyzenBranchPredictionMissing("AMD Ryzen 5 5600X 6-Core Processor", 0, null));

        var reading = new PcHealth.Reading([], null, "AMD Ryzen 7 7800X3D 8-Core Processor", null, null, null, OsBuild: 22631, OsUbr: 3880);
        var item = Assert.Single(PcHealth.Decide(reading), i => i.Key == "ryzenbranch");
        Assert.Contains("branch-prediction", item.Detail);
    }

    [Fact]
    public void SingleChannelRamDetectionHandlesEveryVendorAndSkipsSolderedLpddr()
    {
        var oneOfFour = new PcHealth.MemorySlot[]
        {
            new(false, 0x09, 0x1A, "DIMM_A1", "BANK 0"),
            new(true, 0x09, 0x1A, "DIMM_A2", "BANK 1"),
            new(false, 0x09, 0x1A, "DIMM_B1", "BANK 2"),
            new(false, 0x09, 0x1A, "DIMM_B2", "BANK 3"),
        };
        var single = PcHealth.SingleChannelRam(oneOfFour);
        Assert.NotNull(single);
        Assert.False(single.SameChannelMisplaced);
        Assert.Equal(4, single.TotalSlots);

        var sameChannel = new PcHealth.MemorySlot[]
        {
            new(true, 0x09, 0x22, "DIMM_A1", "P0 CHANNEL A"),
            new(true, 0x09, 0x22, "DIMM_A2", "P0 CHANNEL A"),
            new(false, 0x09, 0x22, "DIMM_B1", "P0 CHANNEL B"),
            new(false, 0x09, 0x22, "DIMM_B2", "P0 CHANNEL B"),
        };
        var misplaced = PcHealth.SingleChannelRam(sameChannel);
        Assert.NotNull(misplaced);
        Assert.True(misplaced.SameChannelMisplaced);

        var dualChannel = new PcHealth.MemorySlot[]
        {
            new(false, 0x09, 0x22, "ChannelA-DIMM0", "BANK 0"),
            new(true, 0x09, 0x22, "ChannelA-DIMM1", "BANK 1"),
            new(false, 0x09, 0x22, "ChannelB-DIMM0", "BANK 2"),
            new(true, 0x09, 0x22, "ChannelB-DIMM1", "BANK 3"),
        };
        Assert.Null(PcHealth.SingleChannelRam(dualChannel));

        var twoOfTwo = new PcHealth.MemorySlot[]
        {
            new(true, 0x0D, 0x1A, "SODIMM 1", "BANK 0"),
            new(true, 0x0D, 0x1A, "SODIMM 2", "BANK 1"),
        };
        Assert.Null(PcHealth.SingleChannelRam(twoOfTwo));

        var solderedLpddr = new PcHealth.MemorySlot[]
        {
            new(true, 0x0B, 0x23, "ChannelA", "BANK 0"),
            new(false, 0x0B, 0x23, "ChannelB", "BANK 1"),
        };
        Assert.Null(PcHealth.SingleChannelRam(solderedLpddr));

        var ambiguousBanks = new PcHealth.MemorySlot[]
        {
            new(true, 0x09, 0x1A, "DIMM 1", "BANK 0"),
            new(true, 0x09, 0x1A, "DIMM 2", "BANK 1"),
            new(false, 0x09, 0x1A, "DIMM 3", "BANK 2"),
            new(false, 0x09, 0x1A, "DIMM 4", "BANK 3"),
        };
        Assert.Null(PcHealth.SingleChannelRam(ambiguousBanks));

        var smbios = BuildSmbiosType17(
            (0, 0x09, 0x1A, "DIMM_A1", "BANK 0"),
            (8192, 0x09, 0x1A, "DIMM_A2", "BANK 1"),
            (0, 0x09, 0x1A, "DIMM_B1", "BANK 2"),
            (0, 0x09, 0x1A, "DIMM_B2", "BANK 3"));
        var parsed = PcHealth.ParseMemorySlots(smbios);
        Assert.Equal(4, parsed.Count);
        Assert.NotNull(PcHealth.SingleChannelRam(parsed));

        var decided = PcHealth.Decide(new PcHealth.Reading([], null, null, null, null, null, MemorySlots: parsed));
        Assert.Contains(decided, i => i.Key == "ramchannel");
    }

    [Fact]
    public void BiosHintsAndSupportPagesCoverAllMajorBoardVendors()
    {
        Assert.NotNull(PcHealth.BiosPath("Biostar Group", "xmp"));
        Assert.NotNull(PcHealth.BiosPath("NZXT", "rebar"));
        Assert.NotNull(PcHealth.BiosPath("EVGA", "xmp"));
        Assert.NotNull(PcHealth.BiosPath("Supermicro", "rebar"));
        Assert.Null(PcHealth.BiosPath("Dell Inc.", "xmp"));

        Assert.Contains("asus.com", BiosUpdate.SupportPage("ASUSTeK COMPUTER INC.", "ROG STRIX B550-F"));
        Assert.Contains("msi.com", BiosUpdate.SupportPage("Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK"));
        Assert.Contains("gigabyte.com", BiosUpdate.SupportPage("Gigabyte Technology Co., Ltd.", "B650 AORUS ELITE"));
        Assert.Contains("asrock.com", BiosUpdate.SupportPage("ASRock", "B650M Pro RS"));
        Assert.Contains("biostar.com.tw", BiosUpdate.SupportPage("Biostar", "B650EGTQ"));
        Assert.Contains("nzxt.com", BiosUpdate.SupportPage("NZXT", "N7 B650E"));
        Assert.Contains("dell.com", BiosUpdate.SupportPage("Dell Inc.", "Alienware Aurora R16"));
        Assert.Null(BiosUpdate.SupportPage("UnknownBoardCorp", "Model1"));
    }

    [Fact]
    public void SsdTrimCheckFixAndVendorKeyedFirmwareAdvisoriesWorkAcrossAllVendors()
    {
        var ssd = new Drives.Disk(0, "Samsung SSD 990 PRO 2TB", Drives.Bus.Nvme, "NVMe SSD", "8B2QJXD7", 3, null, "stornvme", null, null, null, [new("C:", 2_000_000_000_000, 1_000_000_000_000)], @"SCSI\Disk&Ven_NVMe&Prod_Samsung\1");
        var hdd = new Drives.Disk(1, "WDC WD20EZRZ", Drives.Bus.Sata, "Hard drive", "80.00A80", null, null, "storahci", null, null, null, [new("D:", 2_000_000_000_000, 1_000_000_000_000)], null);

        var trimOffFix = Assert.Single(Drives.Fixes([ssd], new(false, false), null, new Drives.TrimState(NtfsOff: true)));
        Assert.Equal("trim", trimOffFix.Key);
        Assert.False(trimOffFix.Applied);

        Assert.Empty(Drives.Fixes([hdd], new(false, false), null, new Drives.TrimState(NtfsOff: true)));

        var trimApplied = Assert.Single(Drives.Fixes([ssd], new(false, false), new([], null, null, new Drives.TrimChange(NtfsWasOff: true)), new Drives.TrimState(NtfsOff: false)));
        Assert.Equal("trim", trimApplied.Key);
        Assert.True(trimApplied.Applied);

        var cmds = Drives.Commands(@"C:\Windows\System32\reg.exe", @"C:\Windows\System32\schtasks.exe", [], null, null, new Drives.TrimChange(NtfsWasOff: true, RefsWasOff: true))!;
        Assert.Equal(2, cmds.Count);
        Assert.Contains("fsutil.exe\" behavior set DisableDeleteNotify 0", cmds[0]);
        Assert.Contains("fsutil.exe\" behavior set DisableDeleteNotify ReFS 0", cmds[1]);

        Assert.Equal(-1, Math.Sign(DriverAdvisories.Firmware("P8CR001", "P8CR003")!.Value));
        Assert.Null(DriverAdvisories.Firmware("P7CR401", "P8CR003"));
        Assert.Equal(-1, Math.Sign(DriverAdvisories.Firmware("41060A20", "51061A20")!.Value));
        Assert.Equal(-1, Math.Sign(DriverAdvisories.Firmware("001C", "002C")!.Value));
        Assert.Equal(-1, Math.Sign(DriverAdvisories.Firmware("EIFK31.6", "EIFK31.7")!.Value));
        Assert.Null(DriverAdvisories.Firmware("EIFK51.6", "EIFK31.7"));

        Assert.NotNull(Drives.KnownBad("Crucial CT2000T500SSD8", "P8CR001"));
        Assert.Null(Drives.KnownBad("Crucial CT2000T500SSD8", "P8CR003"));
        Assert.NotNull(Drives.KnownBad("SK hynix Platinum P41 SHPP41-2000GM", "41060A20"));
        Assert.Null(Drives.KnownBad("SK hynix Platinum P41 SHPP41-2000GM", "51061A20"));
        Assert.NotNull(Drives.KnownBad("SOLIDIGM SSDPFKKW020X7", "001C"));
        Assert.Null(Drives.KnownBad("SOLIDIGM SSDPFKKW020X7", "002C"));
        Assert.NotNull(Drives.KnownBad("KINGSTON SKC3000D2048G", "EIFK31.6"));
        Assert.Null(Drives.KnownBad("KINGSTON SKC3000D2048G", "EIFK31.7"));
        Assert.NotNull(Drives.KnownBad("KINGSTON SFYRD2000G", "EIFK31.6"));

        Assert.Equal("8B2QJXD7", Drives.NewerFirmware("Samsung SSD 990 PRO 2TB", "4B2QJXD7"));
        Assert.Equal("5B2QGXA7", Drives.NewerFirmware("Samsung SSD 980 PRO 1TB", "3B2QGXA7"));
        Assert.Equal("624361WD", Drives.NewerFirmware("WD_BLACK SN850X 2000GB", "624311WD"));
        Assert.Equal("P8CR003", Drives.NewerFirmware("CT2000T500SSD8", "P8CR001"));
        Assert.Equal("51061A20", Drives.NewerFirmware("SHPP41-2000GM", "41060A20"));
        Assert.Equal("EIFK31.7", Drives.NewerFirmware("SKC3000D2048G", "EIFK31.6"));
        Assert.Equal("002C", Drives.NewerFirmware("SOLIDIGM SSDPFKKW020X7", "001C"));
        Assert.Equal("SK hynix", Drives.Vendor("SHPP41-2000GM")!.Value.Name);
        Assert.Equal("Solidigm", Drives.Vendor("SSDPFKKW020X7")!.Value.Name);
    }

    [Fact]
    public void ExoAiCoordinatorIncludesGpuAnswerInSnapshot()
    {
        var answer = new GpuUsageAnswer("chrome is using 18% GPU (3D) and 640 MB VRAM.", null, null, []);
        using var coordinator = new ExoAiCoordinator(
            new ExoAiFans(() => new UnavailableFanBackend()),
            () => false,
            () => [],
            () => [],
            () => new ExoAiAutopilotSnapshot(false, null, null, 0, false, false),
            () => DateTimeOffset.UtcNow,
            () => answer);

        var snap = coordinator.Snapshot();
        Assert.Equal(answer, snap.GpuAnswer);
    }

    private static byte[] BuildSmbiosType17(params (ushort SizeMb, byte FormFactor, byte MemoryType, string DeviceLocator, string BankLocator)[] entries)
    {
        var body = new List<byte>();
        foreach (var e in entries)
        {
            var s = new byte[0x24];
            s[0] = 17;
            s[1] = 0x24;
            BitConverter.GetBytes(e.SizeMb).CopyTo(s, 0x0C);
            s[0x0E] = e.FormFactor;
            s[0x10] = 1;
            s[0x11] = 2;
            s[0x12] = e.MemoryType;
            body.AddRange(s);
            body.AddRange(Encoding.ASCII.GetBytes(e.DeviceLocator));
            body.Add(0);
            body.AddRange(Encoding.ASCII.GetBytes(e.BankLocator));
            body.Add(0);
            body.Add(0);
        }
        body.AddRange([127, 4, 0, 0, 0, 0]);
        return [0, 3, 3, 0, .. BitConverter.GetBytes((uint)body.Count), .. body];
    }

    private sealed class DummyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class UnavailableFanBackend : IFanBackend
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "none";
        public int? ReadTempC() => null;
        public int ReadFanCount() => 0;
        public int? ReadFanPercent(int index) => null;
        public FanApplyResult SetFanPercent(int index, int percent) => new(false, false, false, "none");
        public void RestoreAuto(int index) { }
        public void Dispose() { }
    }

    private sealed class FakeGpuAutopilotSystem : IAutopilotSystem
    {
        internal readonly List<ProcessRow> Rows = [];
        internal readonly Dictionary<(Knob, int), int> Values = [];
        internal readonly Dictionary<int, GpuProcessLoad> Gpu = [];
        internal int FrontPid = 100;

        internal void Add(int pid, string name)
        {
            Rows.Add(new ProcessRow(pid, 1, name, 4, 10, 1, 1000 + pid, 0, 0, 0, 0, 0, 8, 0));
            Values[(Knob.Priority, pid)] = AutopilotRules.Normal;
            Values[(Knob.Eco, pid)] = AutopilotRules.EcoDefault;
            Values[(Knob.Io, pid)] = AutopilotRules.IoNormal;
            Values[(Knob.Memory, pid)] = AutopilotRules.MemoryNormal;
        }

        public IReadOnlyList<ProcessRow> Processes() => [.. Rows];
        public int Front() => FrontPid;
        public IReadOnlyDictionary<int, GpuProcessLoad> GpuProcesses() => Gpu;
        public int? Read(Knob knob, int pid, long created) => Values.TryGetValue((knob, pid), out var v) ? v : null;

        public bool Write(Knob knob, int pid, long created, int value)
        {
            Values[(knob, pid)] = value;
            return true;
        }

        public uint? PowerRead(Guid scheme, Guid subgroup, Guid setting) => null;
        public bool PowerWrite(Guid scheme, Guid subgroup, Guid setting, uint value) => false;
        public Guid? ActiveScheme() => null;
        public bool OnBattery() => false;
    }
}
