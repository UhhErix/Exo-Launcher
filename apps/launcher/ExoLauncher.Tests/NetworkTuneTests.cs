using ExoLauncher.Services;
using static ExoLauncher.Services.NetworkTune;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the network row's Optimize could go wrong silently:
/// - a value is written that the driver doesn't list for that keyword, or a keyword the adapter doesn't have, or an
///   Ethernet setting on Wi-Fi; a setting already at Exo's value (or unset with the driver's default there) is written again;
/// - the backup is written after the change, a declined prompt leaves a backup for a change that never happened, or a
///   second press overwrites the user's original value with Exo's;
/// - Undo writes Exo's value back, or writes a value where there was none instead of deleting it;
/// - a keyword, value or device id cmd would read specially (a quote, % or ^) reaches reg.exe or pnputil;
/// - a 100 Mbps link is blamed on the cable when the user forced that speed, or a gigabit link on a 2.5 Gbps adapter
///   (a gigabit router port is normal) is called a fault; a localized speed name isn't read.
/// </summary>
public sealed class NetworkTuneTests : IDisposable
{
    private const string Intel = @"PCI\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\6&102E3ADF&0&0048020A";

    private readonly string _folder = Directory.CreateTempSubdirectory("exo-net-").FullName;
    private string BackupPath => Path.Combine(_folder, "exo-network-tune.json");

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    private static Param P(string? value, string? @default, params string[] allowed) =>
        new(value, @default, allowed.ToDictionary(a => a, a => a, StringComparer.OrdinalIgnoreCase));

    private static readonly Param Speeds = new("0", "0", new Dictionary<string, string>
    {
        ["0"] = "Auto Negotiation", ["4"] = "100 Mbps Full Duplex", ["6"] = "1.0 Gbps Full Duplex", ["2500"] = "2.5 Gbps Full Duplex",
    });

    private static Adapter Ethernet(Dictionary<string, Param> settings, long link = 2_500_000_000, string key = "0000") =>
        new(key, "Ethernet", "Intel(R) Ethernet Controller I226-V", Intel, false, link, settings);

    private sealed class FakeAdapters(List<Adapter> list) : IAdapters
    {
        public List<Adapter> List { get; } = list;
        public bool Laptop { get; set; }
        IReadOnlyList<Adapter> IAdapters.List() => List;
        public IReadOnlyDictionary<string, Param>? Params(string key) => List.FirstOrDefault(a => a.Key == key)?.Params;
        public bool Portable() => Laptop;
    }

    /// <summary>Writes into the fake adapters' settings as the registry would, or refuses as a declined prompt does.</summary>
    private sealed class FakeWriter(FakeAdapters adapters) : IWriter
    {
        public string? Refuse { get; set; }
        public List<(string Key, string Keyword, string? Value)> Written { get; } = [];

        public string? Write(IReadOnlyList<(Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> writes)
        {
            if (Refuse is not null) return Refuse;
            foreach (var (adapter, values) in writes)
            {
                var index = adapters.List.FindIndex(a => a.Key == adapter.Key);
                var settings = new Dictionary<string, Param>(adapters.List[index].Params, StringComparer.OrdinalIgnoreCase);
                foreach (var (keyword, value) in values)
                {
                    Written.Add((adapter.Key, keyword, value));
                    settings[keyword] = settings[keyword] with { Value = value };
                }
                adapters.List[index] = adapters.List[index] with { Params = settings };
            }
            return null;
        }
    }

    private (NetworkTune Tune, FakeAdapters Adapters, FakeWriter Writer) Make(params Adapter[] list)
    {
        var adapters = new FakeAdapters([.. list]);
        var writer = new FakeWriter(adapters);
        return (new NetworkTune(adapters, writer, BackupPath, () => DateTimeOffset.UnixEpoch), adapters, writer);
    }

    [Fact]
    public void Decide_SetsOnlyWhatTheDriverListsAndIsNotAlreadySet()
    {
        var adapter = Ethernet(new(StringComparer.OrdinalIgnoreCase)
        {
            ["*EEE"] = P("1", "1", "0", "1"),
            ["EEE"] = P(null, "0", "0", "1"),                    // unset, the driver's default already off
            ["DMACoalescing"] = P("1", "1", "1", "2"),           // 0 isn't one of its values
            ["*InterruptModeration"] = P("1", "1", "0", "1"),    // no evidence it helps a game: never touched
            ["*SpeedDuplex"] = Speeds,
        });
        Assert.Equal(["*EEE"], Decide(adapter, portable: false).Select(r => r.Keyword));
    }

    [Fact]
    public void Decide_PutsShrunkBuffersBackToTheDriversOwnSize()
    {
        var none = new Dictionary<string, string>();
        var adapter = Ethernet(new(StringComparer.OrdinalIgnoreCase)
        {
            ["*ReceiveBuffers"] = new("256", "1024", none),   // an optimizer shrank it
            ["*TransmitBuffers"] = new("2048", "1024", none), // bigger than the default: the user's, left alone
        });
        var rule = Assert.Single(Decide(adapter, portable: false));
        Assert.Equal(("*ReceiveBuffers", "1024"), (rule.Keyword, rule.To));
    }

    [Fact]
    public void Decide_UnsetValueFallsBackToTheDriversDefault()
    {
        var adapter = Ethernet(new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = P(null, "1", "0", "1") });
        Assert.Equal(["*EEE"], Decide(adapter, portable: false).Select(r => r.Keyword));
    }

    [Fact]
    public void Decide_LeavesWiFiAloneForEthernetRules()
    {
        var wifi = new Adapter("0001", "Wi-Fi", "Intel Wi-Fi 6E AX210", Intel, true, 1_200_000_000,
            new Dictionary<string, Param>(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = P("1", "1", "0", "1") });
        Assert.Empty(Decide(wifi, portable: false));
    }

    [Fact]
    public void Apply_BacksUpFirstAndKeepsTheUsersValueAcrossPresses()
    {
        var (tune, adapters, writer) = Make(Ethernet(new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = P("1", "1", "0", "1"), ["*SpeedDuplex"] = Speeds }));
        Assert.Null(tune.Apply());
        Assert.Equal([("0000", "*EEE", (string?)"0")], writer.Written);
        Assert.True(tune.Read().Applied);

        // A driver update puts its default back; the second press must still undo to the user's own 1, not Exo's 0.
        adapters.List[0] = adapters.List[0] with { Params = new Dictionary<string, Param>(adapters.List[0].Params, StringComparer.OrdinalIgnoreCase) { ["*EEE"] = P(null, "1", "0", "1") } };
        Assert.Null(tune.Apply());
        Assert.Null(tune.Undo());
        Assert.Equal(("0000", "*EEE", (string?)"1"), writer.Written[^1]);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void Undo_DeletesAValueThatWasUnset()
    {
        var (tune, _, writer) = Make(Ethernet(new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = P(null, "1", "0", "1") }));
        Assert.Null(tune.Apply());
        Assert.Null(tune.Undo());
        Assert.Equal(("0000", "*EEE", (string?)null), writer.Written[^1]);
    }

    [Fact]
    public void Apply_DeclinedPromptLeavesNoBackup()
    {
        var (tune, _, writer) = Make(Ethernet(new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = P("1", "1", "0", "1") }));
        writer.Refuse = "Windows' permission was declined, so nothing changed.";
        Assert.Equal(writer.Refuse, tune.Apply());
        Assert.False(File.Exists(BackupPath));
        Assert.False(tune.Read().Applied);
    }

    [Theory]
    [InlineData("*EEE\" & calc", "0")]
    [InlineData("*EEE", "0 & calc")]
    [InlineData("%PATH%", "0")]
    [InlineData("*EEE", "^1")]
    public void Commands_RefuseAnythingCmdWouldRead(string keyword, string value) =>
        Assert.Null(NetworkTune.Commands("reg.exe", "pnputil.exe", Ethernet(new()), [(keyword, value)]));

    [Fact]
    public void Commands_RefuseAnOddDeviceId() =>
        Assert.Null(NetworkTune.Commands("reg.exe", "pnputil.exe", Ethernet(new()) with { DeviceId = "PCI\\VEN_8086\"&calc\\1" }, [("*EEE", "0")]));

    [Fact]
    public void Commands_WriteStringsDeleteUnsetValuesAndRestartTheAdapter()
    {
        var lines = NetworkTune.Commands("reg.exe", "pnputil.exe", Ethernet(new()), [("*EEE", "0"), ("EEE", null)])!;
        Assert.Equal(3, lines.Count);
        Assert.Contains("add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}\\0000\" /v \"*EEE\" /t REG_SZ /d 0", lines[0]);
        Assert.Contains("delete", lines[1]);
        Assert.Equal($"\"pnputil.exe\" /restart-device \"{Intel}\"", lines[2]);
    }

    [Fact]
    public void SlowLink_OnlyAHundredMegabitLinkLeftToNegotiate()
    {
        var auto = new Dictionary<string, Param>(StringComparer.OrdinalIgnoreCase) { ["*SpeedDuplex"] = Speeds };
        Assert.Equal(2_500_000_000, SlowLink([Ethernet(auto, link: 100_000_000)])?.CanBps);
        Assert.Null(SlowLink([Ethernet(auto, link: 1_000_000_000)])); // a gigabit router port is normal
        var forced = new Dictionary<string, Param>(StringComparer.OrdinalIgnoreCase) { ["*SpeedDuplex"] = Speeds with { Value = "4" } };
        Assert.Null(SlowLink([Ethernet(forced, link: 100_000_000)]));
    }

    [Fact]
    public void TopSpeed_ReadsLocalizedNames()
    {
        var german = new Dictionary<string, Param>(StringComparer.OrdinalIgnoreCase)
        {
            ["*SpeedDuplex"] = new("0", "0", new Dictionary<string, string> { ["0"] = "Automatische Aushandlung", ["6"] = "1,0 Gbit/s Vollduplex" }),
        };
        Assert.Equal(1_000_000_000, TopSpeed(Ethernet(german)));
        Assert.Equal("2.5 Gbps", Speed(2_500_000_000));
        Assert.Equal("100 Mbps", Speed(100_000_000));
    }

    [Fact]
    public void Names_JoinsDistinctNames() =>
        Assert.Equal("energy-efficient Ethernet on and network buffers shrunk",
            Names([new("*EEE", "0", "energy-efficient Ethernet on", false), new("EEE", "0", "energy-efficient Ethernet on", false), new("*ReceiveBuffers", "1024", "network buffers shrunk", false)]));

    [Fact]
    public void Decide_SetsPnpCapabilitiesBitmaskOnDesktopsAndSkipsLaptops()
    {
        var unset = Ethernet(new(StringComparer.OrdinalIgnoreCase) { [PnpCapabilitiesKey] = new(null, "0", new Dictionary<string, string>()) });
        var zero = Ethernet(new(StringComparer.OrdinalIgnoreCase) { [PnpCapabilitiesKey] = new("0", "0", new Dictionary<string, string>()) });
        var bits4 = Ethernet(new(StringComparer.OrdinalIgnoreCase) { [PnpCapabilitiesKey] = new("4", "0", new Dictionary<string, string>()) });
        var already = Ethernet(new(StringComparer.OrdinalIgnoreCase) { [PnpCapabilitiesKey] = new("24", "0", new Dictionary<string, string>()) });

        Assert.Equal("24", Assert.Single(Decide(unset, portable: false)).To);
        Assert.Equal("24", Assert.Single(Decide(zero, portable: false)).To);
        Assert.Equal("28", Assert.Single(Decide(bits4, portable: false)).To);
        Assert.Empty(Decide(already, portable: false));
        Assert.Empty(Decide(zero, portable: true));
    }

    [Fact]
    public void Commands_UsesRegDwordForPnpCapabilitiesAndRestoresOrDeletesOnUndo()
    {
        var adapter = Ethernet(new(StringComparer.OrdinalIgnoreCase)
        {
            ["*EEE"] = P("1", "1", "0", "1"),
            [PnpCapabilitiesKey] = new(null, "0", new Dictionary<string, string>()),
        });
        var lines = NetworkTune.Commands("reg.exe", "pnputil.exe", adapter, [("*EEE", "0"), (PnpCapabilitiesKey, "24"), (PnpCapabilitiesKey, null)])!;
        Assert.Contains("/v \"*EEE\" /t REG_SZ /d 0", lines[0]);
        Assert.Contains($"/v \"{PnpCapabilitiesKey}\" /t REG_DWORD /d 24", lines[1]);
        Assert.Contains($"delete \"HKLM\\{ClassKey}\\0000\" /v \"{PnpCapabilitiesKey}\" /f /reg:64", lines[2]);

        var (tune, _, writer) = Make(Ethernet(new(StringComparer.OrdinalIgnoreCase) { [PnpCapabilitiesKey] = new(null, "0", new Dictionary<string, string>()) }));
        Assert.Null(tune.Apply());
        Assert.Contains(("0000", PnpCapabilitiesKey, "24"), writer.Written);
        Assert.Null(tune.Undo());
        Assert.Equal(("0000", PnpCapabilitiesKey, (string?)null), writer.Written[^1]);
    }

    [Fact]
    public void PickRssPlan_SelectsDedicatedUpperPhysicalCoresAndPrefersSecondaryCcd()
    {
        var quad = new MouseCore.Topology([new([0], 1), new([1], 1), new([2], 1), new([3], 1)], [new(0xFUL, 16)], 4);
        Assert.Null(PickRssPlan(quad));

        var hex = new MouseCore.Topology(
            [new([0, 1], 1), new([2, 3], 1), new([4, 5], 1), new([6, 7], 1), new([8, 9], 1), new([10, 11], 1)],
            [new(0xFFFUL, 32)],
            12);
        var hexPlan = PickRssPlan(hex)!;
        Assert.Equal((8, 10, 2, (1UL << 8) | (1UL << 10)), (hexPlan.BaseCpu, hexPlan.MaxCpu, hexPlan.MaxProcessors, hexPlan.AffinityMask));

        var avoidMouse = PickRssPlan(hex, 1UL << 10)!;
        Assert.Equal((6, 8), (avoidMouse.BaseCpu, avoidMouse.MaxCpu));

        var dualCcd = new MouseCore.Topology(
            [new([0, 1], 1), new([2, 3], 1), new([4, 5], 1), new([6, 7], 1), new([8, 9], 1), new([10, 11], 1), new([12, 13], 1), new([14, 15], 1)],
            [new(0x00FFUL, 96), new(0xFF00UL, 32)],
            16);
        var dualPlan = PickRssPlan(dualCcd)!;
        Assert.Equal((12, 14), (dualPlan.BaseCpu, dualPlan.MaxCpu));
    }

    [Fact]
    public void DecideAndCommands_IsolateEthernetRssAndPciAffinitySafely()
    {
        var plan = new RssPlan(8, 10, 2, (1UL << 8) | (1UL << 10));
        var adapter = Ethernet(new(StringComparer.OrdinalIgnoreCase)
        {
            [RssBaseProcKey] = new("0", "0", new Dictionary<string, string>()),
            [RssMaxProcKey] = new("11", "11", new Dictionary<string, string>()),
            [MaxRssProcessorsKey] = new("4", "4", new Dictionary<string, string> { ["2"] = "2", ["4"] = "4" }),
            [NicPolicyKey] = new(null, null, new Dictionary<string, string>()),
            [NicMaskKey] = new(null, null, new Dictionary<string, string>()),
        });

        var desktopRules = Decide(adapter, portable: false, plan);
        Assert.Contains(desktopRules, r => r.Keyword == RssBaseProcKey && r.To == "8");
        Assert.Contains(desktopRules, r => r.Keyword == RssMaxProcKey && r.To == "10");
        Assert.Contains(desktopRules, r => r.Keyword == MaxRssProcessorsKey && r.To == "2");
        Assert.Contains(desktopRules, r => r.Keyword == NicPolicyKey && r.To == "4");
        Assert.Contains(desktopRules, r => r.Keyword == NicMaskKey && r.To == plan.MaskHex);

        Assert.Empty(Decide(adapter, portable: true, plan));

        var custom = Ethernet(new(StringComparer.OrdinalIgnoreCase)
        {
            [NicPolicyKey] = new("4", null, new Dictionary<string, string>()),
            [NicMaskKey] = new("0000000000000004", null, new Dictionary<string, string>()),
        });
        Assert.Empty(Decide(custom, portable: false, plan));

        var lines = NetworkTune.Commands("reg.exe", "pnputil.exe", adapter, [(NicPolicyKey, "4"), (NicMaskKey, plan.MaskHex), (NicMaskKey, null)])!;
        Assert.Contains(@"\Device Parameters\Interrupt Management\Affinity Policy"" /v ""DevicePolicy"" /t REG_DWORD /d 4", lines[0]);
        Assert.Contains($@"\Device Parameters\Interrupt Management\Affinity Policy"" /v ""AssignmentSetOverride"" /t REG_BINARY /d {plan.MaskHex}", lines[1]);
        Assert.Contains($@"delete ""HKLM\SYSTEM\CurrentControlSet\Enum\{adapter.DeviceId}\Device Parameters\Interrupt Management\Affinity Policy"" /v ""AssignmentSetOverride"" /f /reg:64", lines[2]);
        Assert.Null(NetworkTune.Commands("reg.exe", "pnputil.exe", adapter, [(NicMaskKey, "NOT_HEX")]));
    }
}
