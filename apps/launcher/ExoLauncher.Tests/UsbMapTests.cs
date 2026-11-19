using ExoLauncher.Services;
using static ExoLauncher.Services.UsbPorts;
using DevNode = ExoLauncher.Services.Peripherals.DevNode;

namespace ExoLauncher.Tests;

/// <summary>
/// The USB map. The ways it could go wrong silently:
/// - bInterval read in the wrong unit (ms at USB 1 speeds, doubling 125 µs steps at USB 2 and 3), so 8 kHz reads as 125 Hz;
/// - an output or a bulk endpoint taken for how a device polls;
/// - the packed layout read a byte off, or a port with nothing in it read as a device;
/// - a hub or a composite device's interfaces listed as devices, or hubs on the way not counted;
/// - two of one model mixed up; a controller with nothing on it dropped; a controller off its own PCI node listed.
/// </summary>
public sealed class UsbMapTests
{
    private const string Xhci = @"PCI\VEN_1022&DEV_15B6&SUBSYS_1&REV_00\X1";
    private const string Chipset = @"PCI\VEN_1022&DEV_43F7&SUBSYS_1&REV_00\X2";
    private const string Root = @"USB\ROOT_HUB30\R1";
    private const string Root2 = @"USB\ROOT_HUB30\R2";
    private const string Hub = @"USB\VID_05E3&PID_0610\H1";
    private const string Mouse = @"USB\VID_361D&PID_0300\M1";
    private const string Webcam = @"USB\VID_046D&PID_085C\W1";
    private const string Pad1 = @"USB\VID_054C&PID_0CE6\P1";
    private const string Pad2 = @"USB\VID_054C&PID_0CE6\P2";

    [Theory]
    [InlineData(0, 10, 10_000)] // low speed: milliseconds
    [InlineData(1, 1, 1_000)]   // full speed: 1 ms, 1000 Hz
    [InlineData(2, 1, 125)]     // high speed: 125 µs, 8000 Hz
    [InlineData(2, 4, 1_000)]
    [InlineData(3, 1, 125)]
    public void An_interval_is_read_in_its_speeds_unit(int speed, int bInterval, int micros) => Assert.Equal(micros, IntervalMicros(speed, bInterval));

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 17)]
    public void A_nonsense_interval_says_nothing(int speed, int bInterval) => Assert.Null(IntervalMicros(speed, bInterval));

    /// <summary>The packed structure, as a hub returns it.</summary>
    private static byte[] Info(int vendor, int product, int speed, uint status, params (byte Address, byte Attributes, byte Interval)[] pipes)
    {
        var info = new byte[35 + pipes.Length * 11];
        BitConverter.TryWriteBytes(info.AsSpan(12), (ushort)vendor);
        BitConverter.TryWriteBytes(info.AsSpan(14), (ushort)product);
        info[23] = (byte)speed;
        BitConverter.TryWriteBytes(info.AsSpan(27), pipes.Length);
        BitConverter.TryWriteBytes(info.AsSpan(31), status);
        for (var i = 0; i < pipes.Length; i++)
        {
            var at = 35 + i * 11;
            info[at] = 7;
            info[at + 1] = 5;
            info[at + 2] = pipes[i].Address;
            info[at + 3] = pipes[i].Attributes;
            info[at + 6] = pipes[i].Interval;
        }
        return info;
    }

    [Fact]
    public void The_packed_layout_gives_ids_speed_and_the_fastest_interrupt_in()
    {
        var link = ParseLink(Info(0x361D, 0x0300, 2, 1, (0x81, 3, 4), (0x82, 3, 1), (0x01, 3, 1), (0x83, 2, 1)));
        Assert.NotNull(link);
        Assert.Equal(0x361D, link.Vendor);
        Assert.Equal(0x0300, link.Product);
        Assert.Equal(2, link.Speed);
        Assert.Equal(125, link.Interval); // endpoint 0x82; an output (0x01) and a bulk one (attributes 2) don't count
    }

    [Fact]
    public void An_empty_port_is_no_device() => Assert.Null(ParseLink(Info(0, 0, 0, 0)));

    [Fact]
    public void A_device_with_no_interrupt_endpoint_has_no_interval() => Assert.Null(ParseLink(Info(1, 2, 3, 1, (0x81, 2, 0)))!.Interval);

    [Fact]
    public void A_truncated_answer_reads_only_whole_pipes()
    {
        var info = Info(1, 2, 1, 1, (0x81, 3, 1), (0x82, 3, 1));
        Assert.Equal(1000, ParseLink(info.AsSpan(0, 46))!.Interval);
        Assert.Null(ParseLink(info.AsSpan(0, 20)));
    }

    private static readonly Dictionary<string, PciNode> Pci = new(StringComparer.OrdinalIgnoreCase)
    {
        [Xhci] = new(Xhci, null, "AMD USB 3.20 Host Controller", true),
        [Chipset] = new(Chipset, null, "AMD Chipset USB", true),
    };

    private static readonly Dictionary<string, InterruptMode.Mode> Modes = new(StringComparer.OrdinalIgnoreCase) { [Xhci] = InterruptMode.Mode.Msi };

    private static IReadOnlyList<Peripherals.Peripheral> Cards(params (string Source, string Kind, string Name)[] cards) =>
        cards.Select(c => new Peripherals.Peripheral(c.Source, c.Kind, c.Name, null, null, "usb", null, null) { Source = c.Source }).ToList();

    private static readonly List<DevNode> Tree =
    [
        new(Root, Xhci), new(Root2, Chipset),
        new(Hub, Root, Service: "USBHUB3", Name: "Generic Hub"),
        new(Mouse, Hub, Name: "StarlightX Dongle"),
        new(Mouse + @"&MI_00\1", Mouse),
        new(Webcam, Root, Name: "Webcam"),
        new(Pad1, Root2, Name: "DualSense"),
        new(Pad2, Root2, Name: "DualSense"),
        new(@"PCI\VEN_1234&DEV_0000\Z", null),
    ];

    private static IReadOnlyList<Link>? Ports(string hub) => hub switch
    {
        Root => [new Link(0x05E3, 0x0610, 3, null), new Link(0x046D, 0x085C, 2, 4000)],
        Hub => [new Link(0x361D, 0x0300, 2, 125)],
        Root2 => [new Link(0x054C, 0x0CE6, 2, 4000), new Link(0x054C, 0x0CE6, 2, 1000)],
        _ => null,
    };

    [Fact]
    public void Controllers_hold_their_own_devices_and_hubs_only_add_to_the_count()
    {
        var map = Map(Tree, Pci, Modes, Ports, Cards((Mouse, "mouse", "StarlightX Dongle")));
        Assert.Equal(2, map.Count);
        var cpu = map[0];
        Assert.Equal("cpu", cpu.Where);
        Assert.Equal("msi", cpu.Interrupts);
        Assert.Equal(["StarlightX Dongle", "Webcam"], cpu.Devices.Select(d => d.Name));
        var mouse = cpu.Devices[0];
        Assert.Equal(1, mouse.Hubs);
        Assert.Equal(2, mouse.Speed);
        Assert.Equal(125, mouse.Interval);
        Assert.Equal("mouse", mouse.Kind);
        Assert.Equal("Generic Hub", mouse.Hub);
    }

    [Fact]
    public void Only_what_you_play_with_shows_an_interval()
    {
        var webcam = Map(Tree, Pci, Modes, Ports, []).First(c => c.Where == "cpu").Devices.First(d => d.Name == "Webcam");
        Assert.Null(webcam.Interval);
        Assert.Equal(2, webcam.Speed);
    }

    [Fact]
    public void Two_of_one_model_each_get_their_own_port()
    {
        var pads = Map(Tree, Pci, Modes, Ports, Cards((Pad1, "controller", "One"), (Pad2, "controller", "Two"))).First(c => c.Where == "chipset").Devices;
        Assert.Equal(2, pads.Count);
        Assert.Equal([1000, 4000], pads.Select(d => d.Interval!.Value).Order());
    }

    [Fact]
    public void A_hub_that_does_not_answer_leaves_its_devices_on_the_map_without_figures()
    {
        var map = Map(Tree, Pci, Modes, _ => null, []);
        var mouse = map.First(c => c.Where == "cpu").Devices.First(d => d.Name == "StarlightX Dongle");
        Assert.Null(mouse.Speed);
        Assert.Equal(1, mouse.Hubs);
    }

    [Fact]
    public void A_controller_with_nothing_on_it_stays_on_the_map()
    {
        var map = Map([new(Root, Xhci)], Pci, Modes, _ => [], []);
        Assert.Empty(Assert.Single(map).Devices);
    }
}
