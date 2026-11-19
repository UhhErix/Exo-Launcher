using ExoLauncher.Services;
using static ExoLauncher.Services.MouseCore;
using DevNode = ExoLauncher.Services.Peripherals.DevNode;

namespace ExoLauncher.Tests;

/// <summary>
/// The wireless doctor, on made-up reports and a made-up PC. The ways it could go wrong silently:
/// - "No dropouts" for a receiver that leaves slots empty, or a hand stopping counted as a dropout;
/// - dropouts at random read as a steady beat, or a real one missed;
/// - a wired mouse examined; another device's receiver taken for this mouse's;
/// - Wi-Fi on 5 GHz named as sharing the band, or the mouse counted as its own Bluetooth neighbor;
/// - anything nearby said to be the cause, or listed with no dropouts to explain.
/// </summary>
public sealed class MouseWirelessTests
{
    private const long F = 10_000_000;
    private const long Slot = F / 1000;

    private static List<Report> From(IEnumerable<long> gaps)
    {
        var at = F;
        var list = new List<Report>();
        foreach (var gap in gaps)
        {
            at += gap;
            list.Add(new Report(at, 8, 0));
        }
        return list;
    }

    [Fact]
    public void A_clean_run_has_no_dropouts()
    {
        var d = ReadDropouts(From(Enumerable.Repeat(Slot, 5000)), F);
        Assert.Equal(Pattern.None, d.Pattern);
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void Short_gaps_are_not_dropouts_but_empty_slots_are_still_said()
    {
        // One slot in 20 skipped: 2 ms gaps, under four slots, and 4.8% of slots empty.
        var d = ReadDropouts(From(Enumerable.Range(0, 5000).Select(i => i % 20 == 0 ? 2 * Slot : Slot)), F);
        Assert.Equal(0, d.Count);
        Assert.Equal(Pattern.Empty, d.Pattern);
        Assert.True(d.Empty > 4);
    }

    [Fact]
    public void A_hand_stopping_is_no_dropout()
    {
        var gaps = Enumerable.Repeat(Slot, 2000).Append(F).Concat(Enumerable.Repeat(Slot, 2000));
        Assert.Equal(Pattern.None, ReadDropouts(From(gaps), F).Pattern);
    }

    [Fact]
    public void Even_dropouts_are_a_beat_and_says_how_often()
    {
        var gaps = Enumerable.Range(0, 9000).Select(i => i % 1000 == 999 ? 8 * Slot : Slot);
        var d = ReadDropouts(From(gaps), F);
        Assert.Equal(Pattern.Beat, d.Pattern);
        Assert.Equal(9, d.Count);
        Assert.Equal(1.0, d.Every);
    }

    [Fact]
    public void Dropouts_at_random_are_no_beat()
    {
        var rng = new Random(11);
        var gaps = Enumerable.Range(0, 9000).Select(_ => rng.Next(250) == 0 ? 8 * Slot : Slot);
        var d = ReadDropouts(From(gaps), F);
        Assert.True(d.Count > 6);
        Assert.Equal(Pattern.Spread, d.Pattern);
    }

    [Fact]
    public void Most_in_one_second_is_a_stretch()
    {
        var gaps = Enumerable.Range(0, 9000).Select(i => i is > 4000 and < 4400 && i % 40 == 0 ? 8 * Slot : Slot);
        Assert.Equal(Pattern.Stretch, ReadDropouts(From(gaps), F).Pattern);
    }

    [Fact]
    public void With_no_dropouts_nothing_nearby_is_listed()
    {
        var words = Words(new Dropouts(0, 0, Pattern.None), null, new Air(6, 2, "Samsung T7", false));
        Assert.Equal("No dropouts in this test.", Assert.Single(words));
    }

    [Fact]
    public void With_dropouts_it_names_what_is_nearby_and_says_it_reads_gaps_not_the_radio()
    {
        var words = Words(new Dropouts(12, 1.8, Pattern.Spread), 30, new Air(6, 2, "Samsung T7", false));
        Assert.Contains("12 dropouts", words[0]);
        Assert.Contains("30 before", words[0]);
        Assert.Contains(words, w => w.Contains("channel 6"));
        Assert.Contains(words, w => w.Contains("2 Bluetooth"));
        Assert.Contains(words, w => w.Contains("Samsung T7"));
        Assert.Contains(words, w => w.Contains("not the radio"));
        Assert.DoesNotContain(words, w => w.Contains("caus", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(36)]
    [InlineData(149)]
    public void Wifi_on_5_ghz_is_not_named(int channel) =>
        Assert.DoesNotContain(Words(new Dropouts(3, 1, Pattern.Spread), null, new Air(channel, 0, null, false)), w => w.Contains("Wi-Fi is on"));

    [Fact]
    public void A_bluetooth_mouse_is_told_to_compare_with_bluetooth_and_offered_no_receiver_advice()
    {
        var words = Words(new Dropouts(3, 1, Pattern.Spread), null, new Air(null, 0, null, true));
        Assert.Contains(words, w => w.Contains("other Bluetooth"));
        Assert.DoesNotContain(words, w => w.Contains("receiver"));
    }

    private const string Receiver = @"USB\VID_361D&PID_0300\R";
    private const string Hid = @"HID\VID_361D&PID_0300&MI_01\H";
    private const string Wired = @"USB\VID_1532&PID_00B7\W";

    private static Peripherals.Peripheral Mouse(string source, string connection) =>
        new(source, "mouse", "Mouse", null, null, connection, null, null) { Source = source };

    [Fact]
    public void A_wired_mouse_gets_no_doctor()
    {
        var tree = new List<DevNode> { new(Wired, null), new(Hid, Wired) };
        Assert.Null(ReadAir(Hid, tree, [Mouse(Wired, "usb")], () => 6, _ => []));
    }

    [Fact]
    public void It_finds_the_receiver_up_the_tree_and_counts_only_others_on_bluetooth()
    {
        var tree = new List<DevNode>
        {
            new(Receiver, null), new(Hid, Receiver),
            new(@"BTHENUM\DEV_1\A", null, Container: "{a}", Connected: true),
            new(@"BTHENUM\DEV_1\B", null, Container: "{a}", Connected: true),
            new(@"BTHLE\DEV_2\C", null, Container: "{b}", Connected: false),
        };
        var map = new List<UsbPorts.MapController>
        {
            new("xHCI", "cpu", "msi", [new("Mouse", "mouse", 2, 0, 125), new("Samsung T7", null, 3, 0, null)]),
        };
        var air = ReadAir(Hid, tree, [Mouse(Receiver, "wireless")], () => 6, _ => map);
        Assert.NotNull(air);
        Assert.Equal(6, air.Channel);
        Assert.Equal(1, air.BluetoothDevices);
        Assert.Equal("Samsung T7", air.Neighbor);
        Assert.False(air.Bluetooth);
    }

    [Fact]
    public void Two_tests_compare_and_a_wired_one_says_nothing()
    {
        var folder = Directory.CreateTempSubdirectory("exo-mousewireless-").FullName;
        try
        {
            var core = new MouseCore(new NoDevices(), new NoWriter(), Path.Combine(folder, "b.json"), () => DateTimeOffset.UtcNow.AddHours(-1), () => DateTimeOffset.UtcNow);
            var late = From(Enumerable.Range(0, 5000).Select(i => i % 500 == 250 ? 8 * Slot : Slot));
            var sample = new Sample(late, "HID\\X", F, 0, [], new Topology([], [], 8));
            var first = core.Examine(sample, _ => new Air(null, 0, null, false));
            Assert.Null(first!.Before);
            Assert.Equal(first.Dropouts, core.Examine(sample, _ => new Air(null, 0, null, false))!.Before);
            Assert.Null(core.Examine(sample, _ => null));
            Assert.Null(core.LastDoctor);
        }
        finally
        {
            DeletionGate.DeleteDirectory(folder, recursive: true);
        }
    }

    private sealed class NoDevices : InterruptMode.IDevices
    {
        public IReadOnlyList<InterruptMode.Device> List() => [];
        public IReadOnlyList<string> BootDisks() => [];
        public string? Parent(string id) => null;
    }

    private sealed class NoWriter : IWriter
    {
        public string? Write(string id, int? policy, byte[]? mask) => null;
    }
}
