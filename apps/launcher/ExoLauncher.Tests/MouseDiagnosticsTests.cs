using System.Buffers.Binary;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Services.InterruptMode;

namespace ExoLauncher.Tests;

public sealed class MouseDiagnosticsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "exo-mouse-diag-tests-" + Guid.NewGuid().ToString("N"));
    private const long Freq = 10_000_000;

    public MouseDiagnosticsTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) DeletionGate.DeleteDirectory(_tempDir, recursive: true); }
        catch { }
    }

    private static List<MouseCore.Report> MakeStream(int hz, int count, Func<int, double>? extraSlotFactor = null)
    {
        var list = new List<MouseCore.Report>(count);
        var slot = (double)Freq / hz;
        double t = Freq;
        for (var i = 0; i < count; i++)
        {
            var factor = extraSlotFactor?.Invoke(i) ?? 1.0;
            if (i > 0) t += slot * factor;
            list.Add(new MouseCore.Report((long)Math.Round(t), 4, 3));
        }
        return list;
    }

    [Theory]
    [InlineData(125)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(4000)]
    [InlineData(8000)]
    public void DetectsPollingRateAndSteadyPercentilesAcrossStandardRates(int hz)
    {
        var reports = MakeStream(hz, 600);
        var jitter = MouseDiagnostics.AnalyzeJitter(reports, Freq, 144);

        Assert.Equal(hz, jitter.PollingRateHz);
        Assert.Equal(Math.Round(1000.0 / hz, 3), jitter.NominalMs);
        Assert.InRange(jitter.MedianMs, jitter.NominalMs * 0.95, jitter.NominalMs * 1.05);
        Assert.InRange(jitter.P99Ms, jitter.NominalMs * 0.95, jitter.NominalMs * 1.05);
        Assert.InRange(jitter.P999Ms, jitter.NominalMs * 0.95, jitter.NominalMs * 1.05);
        Assert.Equal(0, jitter.LateCount);
        Assert.Equal(0, jitter.DroppedCount);
        Assert.Equal(0, jitter.BunchedCount);
        Assert.Equal(100, jitter.StabilityScore);
        Assert.Equal("steady", jitter.Cause);
        Assert.False(MouseDiagnostics.HasProblem(jitter));
        Assert.Equal(7, jitter.Histogram.Count);
        Assert.Equal(jitter.CheckedGaps, jitter.Histogram.Sum(b => b.Count));
    }

    [Fact]
    public void DetectsInjectedGapsDropsAndBunchingAt8000Hz()
    {
        var reports = new List<MouseCore.Report>(2000);
        var slot = (double)Freq / 8000;
        double t = Freq;
        for (var i = 0; i < 2000; i++)
        {
            double mult = 1.0;
            if (i is 200 or 400 or 600 or 800 or 1000) mult = 5.0;
            else if (i is >= 1200 and < 1300) mult = i % 2 == 0 ? 0.2 : 1.8;
            if (i > 0) t += slot * mult;
            reports.Add(new MouseCore.Report((long)Math.Round(t), 5, 4));
        }

        var jitter = MouseDiagnostics.AnalyzeJitter(reports, Freq, 240);

        Assert.Equal(8000, jitter.PollingRateHz);
        Assert.True(jitter.LateCount >= 5);
        Assert.True(jitter.DroppedCount >= 5);
        Assert.True(jitter.BunchedCount >= 40);
        Assert.True(jitter.P99Ms > jitter.NominalMs * 1.5);
        Assert.True(jitter.P999Ms >= jitter.NominalMs * 4.0);
        Assert.True(jitter.StabilityScore < 90);
        Assert.True(MouseDiagnostics.HasProblem(jitter));
        Assert.Equal("usb_uneven", jitter.Cause);
    }

    [Fact]
    public void SeparatesPcBusyFromUnevenUsbUsingEtwAndPdh()
    {
        var reports = MakeStream(1000, 1200, i => i % 100 == 50 ? 2.6 : 1.0);
        var etwBusy = new MouseDiagTrace.Result(
            DateTimeOffset.UtcNow, 10, 80000, 40000, 120000, 500000, 180, 920, 0, 0,
            [new MouseDiagTrace.CoreStats(0, 75000, 110000, 180, 20000, 320000, 920, true)],
            [new MouseDiagTrace.DriverStats("nvlddmkm.sys", 5000, 250000, 920, 100, 2000, 40, 0, [0])]);

        var busyReport = MouseDiagnostics.AnalyzeJitter(reports, Freq, 144, new MouseCore.Work(0, [1.2, 0.05], [1000, 50]), etwBusy);
        var quietReport = MouseDiagnostics.AnalyzeJitter(reports, Freq, 144, new MouseCore.Work(2, [0.05, 0.05, 0.05], [100, 50, 1000]), null);

        Assert.Equal("both", busyReport.Cause);
        Assert.Contains("nvlddmkm.sys", busyReport.CauseDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("usb_uneven", quietReport.Cause);
    }

    [Fact]
    public void ParsesBatchedRawInputBufferWith8ByteAlignment()
    {
        var buf = new byte[256];
        void WriteEntry(int offset, uint type, uint size, long device, ushort flags, int dx, int dy)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), type);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), size);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset + 8), device);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset + 24), flags);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 36), dx);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 40), dy);
        }

        WriteEntry(0, 0, 48, 0x1001, 0, 7, -3);
        WriteEntry(48, 0, 50, 0x1002, 1, 99, 99);
        WriteEntry(104, 0, 48, 0x1001, 0, -2, 6);

        var received = new List<(nint Dev, int Dx, int Dy)>();
        MouseDiagnostics.ParseRawInputBuffer(buf, 3, 12345, (dev, dx, dy, _) => received.Add((dev, dx, dy)));

        Assert.Equal(2, received.Count);
        Assert.Equal(((nint)0x1001, 7, -3), received[0]);
        Assert.Equal(((nint)0x1001, -2, 6), received[1]);
    }

    private static byte[] MakeConnectionExFixture(int port, ushort vid, ushort pid, byte speed, bool isHub, params (byte EndpointAddr, byte Attrs, byte BInterval)[] pipes)
    {
        var buf = new byte[35 + pipes.Length * 11];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), port);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), vid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), pid);
        buf[23] = speed;
        buf[26] = (byte)(isHub ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(27, 4), pipes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(31, 4), 1);
        for (var i = 0; i < pipes.Length; i++)
        {
            var off = 35 + i * 11;
            buf[off + 2] = pipes[i].EndpointAddr;
            buf[off + 3] = pipes[i].Attrs;
            buf[off + 6] = pipes[i].BInterval;
        }
        return buf;
    }

    private static byte[] MakeConnectionExV2Fixture(int port, bool superSpeedPlus)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), port);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 0x07);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), superSpeedPlus ? 0x08u : 0x02u);
        return buf;
    }

    [Fact]
    public void BuildsUsbTopologyTreeFromIoctlFixturesAndProducesSharedHubAdvice()
    {
        var mouseIoctl = MakeConnectionExFixture(1, 0x361D, 0x0200, speed: 2, isHub: false, (0x81, 0x03, 1));
        var webcamIoctl = MakeConnectionExFixture(2, 0x046D, 0x085E, speed: 3, isHub: false, (0x82, 0x01, 1));
        var webcamV2 = MakeConnectionExV2Fixture(2, superSpeedPlus: true);
        var hubIoctl = MakeConnectionExFixture(3, 0x05E3, 0x0610, speed: 3, isHub: true);

        var mouseLink = MouseDiagnostics.ParseConnectionEx(mouseIoctl);
        var webcamLink = MouseDiagnostics.ParseConnectionEx(webcamIoctl, webcamV2);
        var hubLink = MouseDiagnostics.ParseConnectionEx(hubIoctl);

        Assert.NotNull(mouseLink);
        Assert.Equal(125, mouseLink.IntervalUs);
        Assert.NotNull(webcamLink);
        Assert.True(webcamLink.SuperSpeedPlus);
        Assert.True(webcamLink.HasIsochronous);
        Assert.NotNull(hubLink);
        Assert.True(hubLink.IsHub);

        var cpuCtrlId = @"PCI\VEN_1022&DEV_149C&SUBSYS_00000000&REV_00\4&111&0&0341";
        var chipsetSwitchId = @"PCI\VEN_1022&DEV_43E9&SUBSYS_00000000&REV_00\4&222&0&0012";
        var chipsetCtrlId = @"PCI\VEN_1022&DEV_43EE&SUBSYS_00000000&REV_00\5&333&0&0000";
        var cpuRoot = @"USB\ROOT_HUB30\4&CPU";
        var chipRoot = @"USB\ROOT_HUB30\5&CHIP";
        var extHub = @"USB\VID_05E3&PID_0610\6&HUB";
        var mouseDev = @"USB\VID_361D&PID_0200\7&MOUSE";
        var webcamDev = @"USB\VID_046D&PID_085E\7&CAM";

        var tree = new List<Peripherals.DevNode>
        {
            new(cpuRoot, cpuCtrlId, "USB", "USBHUB3", "USB Root Hub (CPU)"),
            new(chipRoot, chipsetCtrlId, "USB", "USBHUB3", "USB Root Hub (Chipset)"),
            new(extHub, chipRoot, "USB", "USBHUB3", "Generic USB Hub"),
            new(mouseDev, extHub, "USB", "usbccgp", "8K Gaming Mouse"),
            new(webcamDev, extHub, "Camera", "usbvideo", "Logitech Brio Webcam"),
        };

        var pci = new Dictionary<string, MouseDiagnostics.UsbControllerMeta>(StringComparer.OrdinalIgnoreCase)
        {
            [cpuCtrlId] = new(cpuCtrlId, "AMD CPU USB 3.10 Host Controller", null, true, "PCI bus 11, dev 0, fn 3", Mode.Msi, true, 2),
            [chipsetSwitchId] = new(chipsetSwitchId, "AMD 500 Series Chipset Switch", null, true, "PCI bus 1, dev 0, fn 0", Mode.Msi, true, null),
            [chipsetCtrlId] = new(chipsetCtrlId, "AMD B550 Chipset USB Controller", chipsetSwitchId, true, "PCI bus 2, dev 0, fn 0", Mode.Line, true, null),
        };

        var cards = new List<Peripherals.Peripheral>
        {
            new("m1", "mouse", "8K Gaming Mouse", "Maker", "8K", "wireless", null, null) { Source = mouseDev },
        };

        var topology = MouseDiagnostics.BuildTopology(
            tree,
            pci,
            hubId => hubId == chipRoot ? (4, [hubLink]) : hubId == extHub ? (4, [mouseLink, webcamLink]) : (4, []),
            hubId => hubId == extHub,
            cards,
            8000);

        Assert.Equal(2, topology.Controllers.Count);
        Assert.Equal("cpu", topology.Controllers[0].Placement);
        Assert.Equal("chipset", topology.Controllers[1].Placement);
        Assert.Equal("line", topology.Controllers[1].InterruptMode);
        Assert.False(topology.MouseOnCpu);
        Assert.True(topology.MouseOnHub);
        Assert.True(topology.MouseSharesHighRateHub);

        var nestedHub = Assert.Single(topology.Controllers[1].RootHub.Hubs);
        var mouseNode = Assert.Single(nestedHub.Devices, d => d.IsMouse);
        Assert.Equal(8000, mouseNode.PollingHz);
        Assert.True(mouseNode.SharesHubWithHighRate);
        Assert.Contains("Logitech Brio Webcam", mouseNode.SharedWith);
        Assert.Contains(topology.Advice, a => a.Contains("8 kHz mouse", StringComparison.OrdinalIgnoreCase) && a.Contains("CPU", StringComparison.OrdinalIgnoreCase) && a.Contains("Logitech Brio Webcam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(topology.Advice, a => a.Contains("MSI mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AggregatesEtwIsrAndDpcEventsByCoreAndDriver()
    {
        var agg = new MouseDiagTrace.Aggregator();
        var modules = new List<MouseDiagTrace.DriverModule>
        {
            new("usbxhci.sys", 0xFFFFF800_10000000UL),
            new("nvlddmkm.sys", 0xFFFFF800_20000000UL),
            new("ndis.sys", 0xFFFFF800_30000000UL),
        };

        byte[] Payload(long initial, ulong routine)
        {
            var data = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0, 8), initial);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8, 8), routine);
            return data;
        }

        agg.AddEvent(67, 2, 1000 + 250, Payload(1000, 0xFFFFF800_10000120UL), Freq);
        agg.AddEvent(68, 2, 2000 + 900, Payload(2000, 0xFFFFF800_10000400UL), Freq);
        agg.AddEvent(68, 0, 3000 + 6000, Payload(3000, 0xFFFFF800_20000800UL), Freq);
        agg.AddEvent(68, 0, 10000 + 4000, Payload(10000, 0xFFFFF800_20000800UL), Freq);
        agg.AddEvent(69, 4, 15000 + 1500, Payload(15000, 0xFFFFF800_30000200UL), Freq);
        agg.AddEvent(50, 4, 17000 + 100, Payload(17000, 0xFFFFF800_30000050UL), Freq);

        var result = agg.Summarize(modules, Freq, 10.0, DateTimeOffset.UtcNow);

        Assert.Equal(2, result.TotalIsrs);
        Assert.Equal(4, result.TotalDpcs);
        Assert.Equal(2, result.MouseControllerCore);
        Assert.Equal(3, result.Cores.Count);
        Assert.True(result.Cores.Single(c => c.Core == 2).IsMouseCore);
        Assert.Equal("nvlddmkm.sys", result.TopDrivers[0].Driver);
        Assert.Equal(1000.0, result.TopDrivers[0].DpcTotalUs);
        Assert.Equal(600.0, result.TopDrivers[0].DpcMaxUs);
        Assert.Equal("ndis.sys", result.TopDrivers[1].Driver);
        Assert.Equal("usbxhci.sys", result.TopDrivers[2].Driver);
        Assert.Equal(2, result.TopDrivers[2].TopCore);
    }

    private sealed class FakeDevices(List<Device> list) : IDevices
    {
        public IReadOnlyList<Device> List() => list;
        public IReadOnlyList<string> BootDisks() => [@"SCSI\DISK&VEN_NVME\1"];
        public string? Parent(string id) => null;
    }

    private sealed class FakeFixApplier(List<Device> devices) : MouseDiagnostics.IFixApplier
    {
        public readonly List<InterruptMode.RegValue> InterruptWrites = [];
        public readonly List<(string HubId, string ValueName, int? Value)> HubWrites = [];
        public uint? AcUsbSuspend = 1;

        public string? WriteInterruptValues(IReadOnlyList<InterruptMode.RegValue> values)
        {
            InterruptWrites.AddRange(values);
            foreach (var v in values)
            {
                var idx = devices.FindIndex(d => string.Equals(d.Id, v.Id, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) continue;
                var cur = devices[idx];
                if (v.Name == MsiName) devices[idx] = cur with { MsiValue = v.Value as int? };
                else if (v.Name == MouseCore.PolicyName) devices[idx] = cur with { Policy = v.Value as int? };
                else if (v.Name == MouseCore.OverrideName) devices[idx] = cur with { Override = v.Value as byte[] };
            }
            return null;
        }

        public string? WriteHubPowerValues(IReadOnlyList<(string HubId, string ValueName, int? Value)> values)
        {
            HubWrites.AddRange(values);
            return null;
        }

        public uint? ReadAcUsbSuspend() => AcUsbSuspend;

        public bool WriteAcUsbSuspend(uint value)
        {
            AcUsbSuspend = value;
            return true;
        }
    }

    [Fact]
    public void GatesFixesStrictlyOnMeasuredProblemsAndSupportsApplyRetestAndUndo()
    {
        var ctrlId = @"PCI\VEN_1022&DEV_149C&SUBSYS_00000000&REV_00\4&111&0&0341";
        var hubId = @"USB\ROOT_HUB30\4&111";
        var pciList = new List<Device>
        {
            new(ctrlId, "AMD USB 3.10 eXtensible Host Controller", "USBXHCI", 0, 0x0C, 0x03, 0x30, 0x6, 8, 0x00FF0001, Mode.Line, 0, null, null, null, false, 1UL, true),
        };
        var topology = new MouseCore.Topology(
            [new([0, 1], 1), new([2, 3], 1), new([4, 5], 1)],
            [new(0b111111UL, 32_000_000)],
            6);

        var boot = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        var now = boot.AddHours(1);
        var backupPath = Path.Combine(_tempDir, "mouse-diag-fixes.json");
        var applier = new FakeFixApplier(pciList);
        var manager = new MouseDiagnostics.FixManager(new FakeDevices(pciList), applier, backupPath, () => boot, () => now);

        var steadyJitter = MouseDiagnostics.AnalyzeJitter(MakeStream(1000, 500), Freq, 144, new MouseCore.Work(0, [0.1, 0, 0, 0, 0, 0], [100, 0, 0, 0, 0, 0]));
        var steadyCtx = new MouseDiagnostics.FixContext(
            steadyJitter, pciList[0], topology, new MouseCore.Work(0, [0.1, 0, 0, 0, 0, 0], [100, 0, 0, 0, 0, 0]),
            pciList, new HashSet<string>(), [hubId], new Dictionary<string, MouseDiagnostics.HubPowerSaved> { [hubId] = new(hubId, 1, 1, 1) }, 1);
        Assert.Empty(manager.Evaluate(steadyCtx));

        var badReports = MakeStream(1000, 1000, i => i % 40 == 20 ? 3.5 : 1.0);
        var badWork = new MouseCore.Work(0, [1.5, 0.2, 0.05, 0.01, 0.02, 0.01], [5000, 100, 100, 50, 80, 50]);
        var badJitter = MouseDiagnostics.AnalyzeJitter(badReports, Freq, 144, badWork);
        Assert.True(MouseDiagnostics.HasProblem(badJitter));

        MouseDiagnostics.FixContext MakeBadCtx() => new(
            badJitter, pciList[0], topology, badWork,
            pciList, new HashSet<string>(), [hubId],
            new Dictionary<string, MouseDiagnostics.HubPowerSaved> { [hubId] = new(hubId, 1, null, 1) },
            applier.AcUsbSuspend);

        var offered = manager.Evaluate(MakeBadCtx());
        Assert.Equal(3, offered.Count);
        Assert.All(offered, c =>
        {
            Assert.Contains(c.Id, new[] { MouseDiagnostics.FixAffinity, MouseDiagnostics.FixMsi, MouseDiagnostics.FixUsbSuspend });
            Assert.Equal(MouseDiagnostics.HonestyNote, c.HonestyNote);
            Assert.True(c.CanApply);
        });

        Assert.Null(manager.Apply(MouseDiagnostics.FixAffinity, MakeBadCtx()));
        Assert.Equal(4, pciList[0].Policy);
        Assert.NotNull(pciList[0].Override);

        Assert.Null(manager.Apply(MouseDiagnostics.FixMsi, MakeBadCtx()));
        Assert.Equal(1, pciList[0].MsiValue);

        Assert.Null(manager.Apply(MouseDiagnostics.FixUsbSuspend, MakeBadCtx()));
        Assert.Equal(0u, applier.AcUsbSuspend);

        boot = now.AddMinutes(5);
        now = boot.AddMinutes(2);
        manager.RecordRetest(steadyJitter, ctrlId);

        var afterRetest = manager.Evaluate(MakeBadCtx());
        Assert.All(afterRetest, c =>
        {
            Assert.Equal("applied", c.Status);
            Assert.True(c.CanUndo);
            Assert.True(c.Improved);
            Assert.NotNull(c.Before);
            Assert.NotNull(c.After);
        });

        Assert.Null(manager.Undo(MouseDiagnostics.FixAffinity));
        Assert.Null(pciList[0].Policy);
        Assert.Null(pciList[0].Override);

        Assert.Null(manager.Undo(MouseDiagnostics.FixMsi));
        Assert.Equal(0, pciList[0].MsiValue);

        Assert.Null(manager.Undo(MouseDiagnostics.FixUsbSuspend));
        Assert.Equal(1u, applier.AcUsbSuspend);
    }
}
