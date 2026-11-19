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
}
