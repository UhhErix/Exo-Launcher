using System.Text.Json.Serialization;

namespace ExoLauncher.Services;

/// <summary>
/// The USB map, for the Devices room: every USB controller and what hangs from it, each device with its link speed, how many hubs it passes through and,
/// for what you play with, the polling interval it asks for (the fastest it may send, not what it does send). Read only, and only what the
/// hubs themselves report. Which sockets are which is something Windows can't say, so the map names none. A controller's place (the processor's, the chipset's) is read from
/// PCI ids and stays an estimate.
/// The ways it could go wrong, covered by UsbMapTests: a hub or a composite's own interfaces listed as devices; a bInterval read in the wrong unit (milliseconds at
/// USB 1 speeds, 125 µs steps doubling at USB 2 and 3); an output endpoint or a bulk one read as how a device polls; two of one model mixed up; a device on no USB controller listed.
/// </summary>
internal static partial class UsbPorts
{
    /// <summary>One port of a hub as it reports it. <paramref name="Speed"/>: USB_DEVICE_SPEED, 0 low to 3 super. <paramref name="Interval"/>: its fastest interrupt IN endpoint's interval in µs; null with none.</summary>
    internal sealed record Link(int Vendor, int Product, int Speed, int? Interval);

    /// <summary>A device on the map. Hubs: on its way to the controller. Interval: µs, only for what you play with.</summary>
    internal sealed record MapDevice(string Name, string? Kind, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Speed, int Hubs, int? Interval, string? Hub = null);

    /// <summary>A USB controller on the map. Interrupts: msi or line; null when unknown. Where: cpu, chipset, addin, dock or unknown.</summary>
    internal sealed record MapController(string Name, string Where, string? Interrupts, IReadOnlyList<MapDevice> Devices);

    /// <summary>A pipe's interval in µs from its bInterval: milliseconds at low and full speed, 125 µs × 2^(bInterval-1) at high speed and up (USB 2.0 § 9.6.6). Null when it says nothing usable.</summary>
    internal static int? IntervalMicros(int speed, int bInterval)
    {
        if (bInterval <= 0) return null;
        if (speed <= 1) return bInterval * 1000;
        return bInterval > 16 ? null : 125 << (bInterval - 1);
    }

    /// <summary>
    /// A port's USB_NODE_CONNECTION_INFORMATION_EX (packed): the device descriptor from byte 4 (idVendor at 12, idProduct at 14), speed at 23, open pipes at 27, status at 31,
    /// then the pipes from 35, 11 bytes each: an endpoint descriptor (address at 2, attributes at 3, bInterval at 6) and its schedule offset.
    /// </summary>
    internal static Link? ParseLink(ReadOnlySpan<byte> info)
    {
        if (info.Length < 35 || BitConverter.ToUInt32(info[31..35]) != 1) return null;
        int speed = info[23];
        var pipes = BitConverter.ToInt32(info[27..31]);
        int? fastest = null;
        for (var i = 0; i < pipes && 35 + (i + 1) * 11 <= info.Length; i++)
        {
            var pipe = info.Slice(35 + i * 11, 11);
            var input = (pipe[2] & 0x80) != 0;
            var interrupt = (pipe[3] & 0x3) == 3;
            if (!input || !interrupt || IntervalMicros(speed, pipe[6]) is not { } micros) continue;
            fastest = fastest is { } so ? Math.Min(so, micros) : micros;
        }
        return new Link(BitConverter.ToUInt16(info[12..14]), BitConverter.ToUInt16(info[14..16]), speed, fastest);
    }

    /// <summary>
    /// The map. <paramref name="links"/> asks a hub for its ports; a device takes the first unclaimed port of its hub with its vendor and product, so two of one model each get one.
    /// </summary>
    internal static List<MapController> Map(IReadOnlyList<Peripherals.DevNode> tree, IReadOnlyDictionary<string, PciNode> pci, IReadOnlyDictionary<string, InterruptMode.Mode> modes,
        Func<string, IReadOnlyList<Link>?> links, IReadOnlyList<Peripherals.Peripheral> cards)
    {
        var children = tree.Where(n => n.Parent is not null).ToLookup(n => n.Parent!, StringComparer.OrdinalIgnoreCase);
        var kinds = cards.Where(c => c.Source is not null).GroupBy(c => c.Source!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var map = new List<MapController>();
        foreach (var root in tree.Where(n => n.Id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) && n.Parent is not null))
        {
            if (!pci.TryGetValue(root.Parent!, out var xhci)) continue;
            var ancestors = new List<PciNode>();
            for (string? up = xhci.Parent; up is not null && pci.TryGetValue(up, out var node) && ancestors.Count < 32; up = node.Parent) ancestors.Add(node);
            var where = xhci.Local == false || ancestors.Any(a => a.Local == false || Dock(a.Name)) ? Where.Dock : Classify(xhci, ancestors).Where;
            var devices = new List<MapDevice>();
            Walk(root, 0, null);
            var mode = modes.TryGetValue(xhci.Id, out var m) ? m : InterruptMode.Mode.None;
            map.Add(new MapController(xhci.Name ?? "USB controller", where.ToString().ToLowerInvariant(),
                mode switch { InterruptMode.Mode.Msi => "msi", InterruptMode.Mode.Line => "line", _ => null },
                devices.OrderBy(d => d.Kind is null ? 1 : 0).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList()));

            void Walk(Peripherals.DevNode hub, int hubs, string? hubName)
            {
                var ports = links(hub.Id)?.ToList() ?? [];
                foreach (var child in children[hub.Id])
                {
                    if (!child.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                    var link = Take(ports, child.Id);
                    if (IsHub(child.Id, child.Service))
                    {
                        Walk(child, hubs + 1, child.Name);
                        continue;
                    }
                    var card = kinds.GetValueOrDefault(child.Id);
                    var plays = card?.Kind is "mouse" or "keyboard" or "controller";
                    devices.Add(new MapDevice(card?.Name ?? child.Name ?? "USB device", card?.Kind, link?.Speed, hubs, plays ? link?.Interval : null, hubName));
                }
            }
        }
        return map.OrderBy(c => c.Where switch { "cpu" => 0, "chipset" => 1, "addin" => 2, _ => 3 }).ThenByDescending(c => c.Devices.Count).ToList();
    }

    /// <summary>The hub's port for a device, by the VID and PID in its instance id; it's taken, so a second of one model gets the next.</summary>
    private static Link? Take(List<Link> ports, string id)
    {
        if (DeviceIds().Match(id) is not { Success: true } ids) return null;
        var vendor = int.Parse(ids.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
        var product = int.Parse(ids.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
        var at = ports.FindIndex(p => p.Vendor == vendor && p.Product == product);
        if (at < 0) return null;
        var link = ports[at];
        ports.RemoveAt(at);
        return link;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DeviceIds();

    /// <summary>A hub's ports through its own query; null when it doesn't answer.</summary>
    internal static IReadOnlyList<Link>? Links(string hubInstance)
    {
        if (InterfacePath(hubInstance) is not { } path) return null;
        using var hub = Hid.HidInterop.CreateFile(path, Hid.HidInterop.GenericWrite, Hid.HidInterop.FileShareWrite, 0, Hid.HidInterop.OpenExisting, 0, 0);
        if (hub.IsInvalid) return null;
        var node = new byte[80];
        if (!DeviceIoControl(hub, GetNodeInformation, node, node.Length, node, node.Length, out _, 0)) return null;
        var found = new List<Link>();
        for (var number = 1; number <= node[6]; number++)
        {
            var info = new byte[512];
            BitConverter.TryWriteBytes(info.AsSpan(0, 4), number);
            if (DeviceIoControl(hub, GetConnectionEx, info, info.Length, info, info.Length, out var returned, 0) && ParseLink(info.AsSpan(0, returned)) is { } link) found.Add(link);
        }
        return found;
    }

    /// <summary>This PC's map: the device tree, its PCI devices and its hubs.</summary>
    internal static List<MapController> ReadMap(IReadOnlyList<Peripherals.Peripheral> cards)
    {
        var tree = Peripherals.Tree();
        var parents = tree.ToDictionary(n => n.Id, n => n.Parent, StringComparer.OrdinalIgnoreCase);
        var devices = new InterruptMode.NativeDevices();
        var list = devices.List();
        var pci = list.ToDictionary(d => d.Id, d => new PciNode(d.Id, parents.GetValueOrDefault(d.Id) ?? devices.Parent(d.Id), d.Name, d.Local), StringComparer.OrdinalIgnoreCase);
        return Map(tree, pci, list.ToDictionary(d => d.Id, d => d.Mode, StringComparer.OrdinalIgnoreCase), Links, cards);
    }
}
