using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

internal sealed partial class MouseDiagnostics
{
    internal sealed record UsbPortLink(
        int PortNumber,
        int VendorId,
        int ProductId,
        int SpeedCode,
        bool SuperSpeedPlus,
        bool IsHub,
        bool HasIsochronous,
        int? IntervalUs);

    internal sealed record UsbDeviceNode(
        string Name,
        string? Kind,
        int PortNumber,
        int SpeedCode,
        string SpeedLabel,
        int? BIntervalUs,
        int? PollingHz,
        bool HighRate,
        string? HighRateReason,
        bool SharesHubWithHighRate,
        IReadOnlyList<string> SharedWith,
        bool IsMouse);

    internal sealed record UsbHubNode(
        string Name,
        bool IsRoot,
        int? PortNumber,
        int PortCount,
        bool? SelectiveSuspendEnabled,
        IReadOnlyList<UsbDeviceNode> Devices,
        IReadOnlyList<UsbHubNode> Hubs);

    internal sealed record UsbControllerNode(
        string Name,
        string VendorName,
        string PciLocation,
        string Placement,
        string InterruptMode,
        bool MsiSupported,
        int? FreeCpuPorts,
        UsbHubNode RootHub);

    internal sealed record UsbTopologyReport(
        IReadOnlyList<UsbControllerNode> Controllers,
        IReadOnlyList<string> Advice,
        bool MouseOnCpu,
        bool MouseOnHub,
        bool MouseSharesHighRateHub);

    internal sealed record UsbControllerMeta(
        string Id,
        string Name,
        string? Parent,
        bool? Local,
        string? PciLocation,
        InterruptMode.Mode Mode,
        bool MsiSupported,
        int? FreeCpuPorts);

    private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const uint IoctlGetNodeInformation = 0x22 << 16 | 258 << 2;
    private const uint IoctlGetConnectionEx = 0x22 << 16 | 274 << 2;
    private const uint IoctlGetConnectionExV2 = 0x22 << 16 | 279 << 2;
    private const uint UsbProtocolSuperSpeedPlus = 0x04;
    private const uint FlagOperatingAtSuperSpeedPlusOrHigher = 0x08;

    private static readonly Guid DevicePropsGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly DevPropKey BusNumberKey = new(DevicePropsGuid, 23);
    private static readonly DevPropKey AddressKey = new(DevicePropsGuid, 30);

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbVidPidRegex();

    internal static UsbPortLink? ParseConnectionEx(ReadOnlySpan<byte> info, ReadOnlySpan<byte> infoV2 = default)
    {
        if (info.Length < 35) return null;
        var status = BinaryPrimitives.ReadUInt32LittleEndian(info[31..35]);
        if (status != 1) return null;
        var portNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0..4]);
        var vendorId = BinaryPrimitives.ReadUInt16LittleEndian(info[12..14]);
        var productId = BinaryPrimitives.ReadUInt16LittleEndian(info[14..16]);
        int speed = info[23];
        var isHub = info[26] != 0;
        var pipes = BinaryPrimitives.ReadInt32LittleEndian(info[27..31]);
        int? fastestInterruptUs = null;
        var hasIso = false;
        for (var i = 0; i < pipes && 35 + (i + 1) * 11 <= info.Length; i++)
        {
            var pipe = info.Slice(35 + i * 11, 11);
            var isIn = (pipe[2] & 0x80) != 0;
            var transferType = pipe[3] & 0x03;
            if (transferType == 1)
            {
                hasIso = true;
            }
            else if (isIn && transferType == 3 && UsbPorts.IntervalMicros(speed, pipe[6]) is { } micros)
            {
                fastestInterruptUs = fastestInterruptUs is { } cur ? Math.Min(cur, micros) : micros;
            }
        }
        var superSpeedPlus = ParseConnectionExV2SuperSpeedPlus(infoV2);
        return new UsbPortLink(portNumber, vendorId, productId, speed, superSpeedPlus, isHub, hasIso, fastestInterruptUs);
    }

    internal static bool ParseConnectionExV2SuperSpeedPlus(ReadOnlySpan<byte> infoV2)
    {
        if (infoV2.Length < 16) return false;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(infoV2[12..16]);
        return (flags & FlagOperatingAtSuperSpeedPlusOrHigher) != 0;
    }

    internal static string SpeedName(int speed, bool superSpeedPlus)
    {
        if (superSpeedPlus) return "SuperSpeed+ (10 Gbps+)";
        return speed switch
        {
            0 => "Low (1.5 Mbps)",
            1 => "Full (12 Mbps)",
            2 => "High (480 Mbps)",
            3 => "SuperSpeed (5 Gbps)",
            _ => "Unknown",
        };
    }

    internal static string VendorName(int vendorId) => vendorId switch
    {
        0x1022 => "AMD",
        0x8086 => "Intel",
        0x1B21 => "ASMedia",
        0x1106 => "VIA",
        0x1912 => "Renesas",
        0x1B73 => "Fresco Logic",
        0x1B6F => "Etron",
        0x10DE => "NVIDIA",
        0x17CB => "Qualcomm",
        0x106B => "Apple",
        _ => $"PCI 0x{vendorId:X4}",
    };

    internal static (bool HighRate, string? Reason) ClassifyHighRate(string name, string? kind, string? deviceClass, UsbPortLink? link, int? measuredMouseHz)
    {
        var effectiveHz = link?.IntervalUs is > 0 ? 1_000_000 / link.IntervalUs.Value : 0;
        if (kind == "mouse" && measuredMouseHz is > 0) effectiveHz = Math.Max(effectiveHz, measuredMouseHz.Value);
        if (effectiveHz >= 4000)
        {
            var label = effectiveHz >= 7000 ? "8 kHz" : "4 kHz";
            return (true, kind == "mouse" ? $"{label} mouse" : $"{label} device");
        }
        var lower = name.ToLowerInvariant();
        if (string.Equals(deviceClass, "Camera", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceClass, "Image", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("webcam", StringComparison.Ordinal)
            || lower.Contains("camera", StringComparison.Ordinal)
            || lower.Contains("cam link", StringComparison.Ordinal)
            || lower.Contains("brio", StringComparison.Ordinal)
            || lower.Contains("capture", StringComparison.Ordinal))
        {
            return (true, "Webcam / video");
        }
        if (kind == "audio"
            || string.Equals(deviceClass, "MEDIA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceClass, "AudioEndpoint", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("headset", StringComparison.Ordinal)
            || lower.Contains("dac", StringComparison.Ordinal)
            || lower.Contains("microphone", StringComparison.Ordinal)
            || lower.Contains("audio", StringComparison.Ordinal))
        {
            return (true, "Audio / headset");
        }
        if (link?.HasIsochronous == true)
        {
            return (true, "Isochronous stream");
        }
        return (false, null);
    }

    internal static UsbTopologyReport BuildTopology(
        IReadOnlyList<Peripherals.DevNode> tree,
        IReadOnlyDictionary<string, UsbControllerMeta> pci,
        Func<string, (int PortCount, IReadOnlyList<UsbPortLink> Links)?> hubReader,
        Func<string, bool?> selectiveSuspendReader,
        IReadOnlyList<Peripherals.Peripheral> cards,
        int? measuredMouseHz = null)
    {
        var children = tree.Where(n => n.Parent is not null).ToLookup(n => n.Parent!, StringComparer.OrdinalIgnoreCase);
        var kinds = cards.Where(c => c.Source is not null)
            .GroupBy(c => c.Source!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var pciNodes = pci.ToDictionary(
            kv => kv.Key,
            kv => new UsbPorts.PciNode(kv.Value.Id, kv.Value.Parent, kv.Value.Name, kv.Value.Local),
            StringComparer.OrdinalIgnoreCase);

        var controllers = new List<UsbControllerNode>();
        foreach (var root in tree.Where(n => n.Id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) && n.Parent is not null))
        {
            if (!pci.TryGetValue(root.Parent!, out var meta)) continue;
            var pciNode = pciNodes[meta.Id];
            var ancestors = new List<UsbPorts.PciNode>();
            for (string? up = pciNode.Parent; up is not null && pciNodes.TryGetValue(up, out var anc) && ancestors.Count < 32; up = anc.Parent) ancestors.Add(anc);
            var where = pciNode.Local == false || ancestors.Any(a => a.Local == false || IsDockName(a.Name))
                ? UsbPorts.Where.Dock
                : UsbPorts.Classify(pciNode, ancestors).Where;
            var ids = UsbPorts.Ids(meta.Id);
            var vendor = ids is { } pair ? VendorName(pair.Vendor) : "Unknown";
            var location = meta.PciLocation ?? (ids is { } p ? $"VEN_{p.Vendor:X4}&DEV_{p.Device:X4}" : "PCI");
            var modeText = meta.Mode switch
            {
                InterruptMode.Mode.Msi => "msi",
                InterruptMode.Mode.Line => "line",
                _ => "unknown",
            };

            var rootHub = BuildHub(root, true, null);
            controllers.Add(new UsbControllerNode(
                meta.Name,
                vendor,
                location,
                where.ToString().ToLowerInvariant(),
                modeText,
                meta.MsiSupported,
                meta.FreeCpuPorts,
                rootHub));

            UsbHubNode BuildHub(Peripherals.DevNode hubNode, bool isRoot, int? hubPortNumber)
            {
                var info = hubReader(hubNode.Id);
                var ports = info?.Links.ToList() ?? [];
                var portCount = info?.PortCount ?? ports.Count;
                var rawDevices = new List<(string Name, string? Kind, string? Class, UsbPortLink? Link, int PortNumber, bool HighRate, string? HighRateReason, bool IsMouse)>();
                var childHubs = new List<UsbHubNode>();

                foreach (var child in children[hubNode.Id])
                {
                    if (!child.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                    var link = TakeLink(ports, child.Id);
                    var portNum = link?.PortNumber ?? 0;
                    if (UsbPorts.IsHub(child.Id, child.Service) || link?.IsHub == true)
                    {
                        childHubs.Add(BuildHub(child, false, portNum > 0 ? portNum : null));
                        continue;
                    }
                    var card = kinds.GetValueOrDefault(child.Id);
                    var displayName = card?.Name ?? child.Name ?? "USB device";
                    var childClass = child.Class ?? FirstChildClass(child.Id, children);
                    var isMouse = card?.Kind == "mouse";
                    var (highRate, reason) = ClassifyHighRate(displayName, card?.Kind, childClass, link, isMouse ? measuredMouseHz : null);
                    rawDevices.Add((displayName, card?.Kind, childClass, link, portNum, highRate, reason, isMouse));
                }

                var highRateNames = rawDevices.Where(d => d.HighRate).Select(d => d.Name).ToList();
                var devices = new List<UsbDeviceNode>(rawDevices.Count);
                foreach (var d in rawDevices.OrderBy(x => x.PortNumber == 0 ? 999 : x.PortNumber).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var shared = highRateNames.Where(n => !string.Equals(n, d.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var shares = shared.Count > 0;
                    var intervalUs = d.Link?.IntervalUs;
                    var pollingHz = intervalUs is > 0 ? 1_000_000 / intervalUs.Value : (int?)null;
                    devices.Add(new UsbDeviceNode(
                        d.Name,
                        d.Kind,
                        d.PortNumber,
                        d.Link?.SpeedCode ?? -1,
                        d.Link is { } l ? SpeedName(l.SpeedCode, l.SuperSpeedPlus) : "Unknown",
                        intervalUs,
                        pollingHz,
                        d.HighRate,
                        d.HighRateReason,
                        shares,
                        shared,
                        d.IsMouse));
                }

                return new UsbHubNode(
                    hubNode.Name ?? (isRoot ? "USB Root Hub" : "USB Hub"),
                    isRoot,
                    hubPortNumber,
                    portCount,
                    selectiveSuspendReader(hubNode.Id),
                    devices,
                    childHubs.OrderBy(h => h.PortNumber ?? 999).ToList());
            }
        }

        var ordered = controllers
            .OrderBy(c => c.Placement switch { "cpu" => 0, "chipset" => 1, "addin" => 2, _ => 3 })
            .ThenByDescending(c => CountDevices(c.RootHub))
            .ToList();

        var (advice, onCpu, onHub, sharesHighRate) = BuildAdvice(ordered, measuredMouseHz);
        return new UsbTopologyReport(ordered, advice, onCpu, onHub, sharesHighRate);
    }

    internal static (List<string> Advice, bool MouseOnCpu, bool MouseOnHub, bool MouseSharesHighRateHub) BuildAdvice(
        IReadOnlyList<UsbControllerNode> controllers,
        int? measuredMouseHz)
    {
        var advice = new List<string>();
        var hasCpuController = controllers.Any(c => c.Placement == "cpu");
        var mouseOnCpu = false;
        var mouseOnHub = false;
        var mouseSharesHighRate = false;

        foreach (var ctrl in controllers)
        {
            FindMouse(ctrl, ctrl.RootHub, false, null);
        }

        if (advice.Count == 0 && mouseOnCpu && !mouseOnHub && !mouseSharesHighRate)
        {
            advice.Add("Your mouse is directly on a CPU USB controller with no high-rate devices sharing its hub.");
        }

        return (advice, mouseOnCpu, mouseOnHub, mouseSharesHighRate);

        void FindMouse(UsbControllerNode ctrl, UsbHubNode hub, bool behindHub, string? hubName)
        {
            foreach (var dev in hub.Devices)
            {
                if (!dev.IsMouse) continue;
                if (ctrl.Placement == "cpu") mouseOnCpu = true;
                if (behindHub) mouseOnHub = true;
                if (dev.SharesHubWithHighRate) mouseSharesHighRate = true;

                var hz = Math.Max(dev.PollingHz ?? 0, measuredMouseHz ?? 0);
                var mouseLabel = hz >= 7000 ? "8 kHz mouse" : hz >= 3500 ? "4 kHz mouse" : "mouse";
                var neighbor = dev.SharedWith.FirstOrDefault();

                if ((behindHub || ctrl.Placement is "chipset" or "addin" or "dock") && hasCpuController && neighbor is not null)
                {
                    var where = behindHub ? $"the hub with {neighbor}" : $"the {ctrl.Placement} controller with {neighbor}";
                    advice.Add($"Move your {mouseLabel} to a port on the CPU's USB controller, not {where}.");
                }
                else if (behindHub && hasCpuController && ctrl.Placement != "cpu")
                {
                    advice.Add($"Move your {mouseLabel} to a port on the CPU's USB controller instead of through {hubName ?? "a USB hub"}.");
                }
                else if (behindHub && neighbor is not null)
                {
                    advice.Add($"Plug your {mouseLabel} directly into the PC, not the hub shared with {neighbor}.");
                }
                else if (behindHub)
                {
                    advice.Add($"Plug your {mouseLabel} directly into the PC instead of through {hubName ?? "a USB hub"}.");
                }
                else if (ctrl.Placement is "chipset" or "addin" or "dock" && hasCpuController)
                {
                    advice.Add($"Move your {mouseLabel} to a port on the CPU's USB controller instead of the {ctrl.Placement} controller.");
                }
                else if (neighbor is not null)
                {
                    advice.Add($"Move {neighbor} off the same USB hub as your {mouseLabel} to reduce bandwidth and interrupt contention.");
                }

                if (ctrl.InterruptMode == "line" && ctrl.MsiSupported)
                {
                    advice.Add($"Your {mouseLabel}'s USB controller ({ctrl.Name}) uses line-based interrupts even though it supports MSI mode.");
                }
            }
            foreach (var childHub in hub.Hubs)
            {
                FindMouse(ctrl, childHub, true, childHub.Name);
            }
        }
    }

    internal static UsbTopologyReport ReadTopology(IReadOnlyList<Peripherals.Peripheral> cards, int? measuredMouseHz = null)
    {
        var tree = Peripherals.Tree();
        var parents = tree.ToDictionary(n => n.Id, n => n.Parent, StringComparer.OrdinalIgnoreCase);
        var nativeDevices = new InterruptMode.NativeDevices();
        var pciList = nativeDevices.List();
        var (_, cpuPorts) = UsbPorts.Read(cards);
        var pci = new Dictionary<string, UsbControllerMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in pciList)
        {
            var msiSupported = ((d.Support ?? 0) & 0x6) != 0;
            pci[d.Id] = new UsbControllerMeta(
                d.Id,
                d.Name,
                parents.GetValueOrDefault(d.Id) ?? nativeDevices.Parent(d.Id),
                d.Local,
                ReadPciLocation(d.Id),
                d.Mode,
                msiSupported,
                cpuPorts?.Free);
        }
        return BuildTopology(tree, pci, ReadHubLinksNative, ReadHubSelectiveSuspend, cards, measuredMouseHz);
    }

    internal static bool? ReadHubSelectiveSuspend(string hubInstanceId)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{hubInstanceId}\Device Parameters");
            if (key is null) return null;
            var enhanced = key.GetValue("EnhancedPowerManagementEnabled") as int?;
            var idle = key.GetValue("AllowIdleIrpInD3") as int?;
            var selective = key.GetValue("SelectiveSuspendEnabled") as int?;
            if (enhanced is null && idle is null && selective is null) return true;
            return (enhanced ?? 1) != 0 || (idle ?? 1) != 0 || (selective ?? 1) != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> FindMouseHubInstances(string? mouseInstanceId, IReadOnlyList<Peripherals.DevNode> tree)
    {
        if (string.IsNullOrEmpty(mouseInstanceId)) return [];
        var byId = tree.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hubs = new List<string>();
        string? cur = mouseInstanceId;
        for (var hops = 0; !string.IsNullOrEmpty(cur) && hops < 32; hops++)
        {
            if (cur.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) break;
            if (cur.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
                || (byId.TryGetValue(cur, out var node) && UsbPorts.IsHub(cur, node.Service)))
            {
                if (!hubs.Contains(cur, StringComparer.OrdinalIgnoreCase)) hubs.Add(cur);
            }
            cur = byId.TryGetValue(cur, out var next) ? next.Parent : null;
        }
        return hubs;
    }

    private static (int PortCount, IReadOnlyList<UsbPortLink> Links)? ReadHubLinksNative(string hubInstance)
    {
        if (HubInterfacePath(hubInstance) is not { } path) return null;
        using var hub = CreateFile(path, GenericWrite, FileShareWrite, 0, OpenExisting, 0, 0);
        if (hub.IsInvalid) return null;
        var node = new byte[80];
        if (!DeviceIoControl(hub, IoctlGetNodeInformation, node, node.Length, node, node.Length, out _, 0)) return null;
        int portCount = node[6];
        var found = new List<UsbPortLink>();
        for (var number = 1; number <= portCount; number++)
        {
            var info = new byte[512];
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0, 4), number);
            if (!DeviceIoControl(hub, IoctlGetConnectionEx, info, info.Length, info, info.Length, out var returned, 0)) continue;
            var infoV2 = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(infoV2.AsSpan(0, 4), number);
            BinaryPrimitives.WriteInt32LittleEndian(infoV2.AsSpan(4, 4), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(infoV2.AsSpan(8, 4), 0x07 | UsbProtocolSuperSpeedPlus);
            var hasV2 = DeviceIoControl(hub, IoctlGetConnectionExV2, infoV2, infoV2.Length, infoV2, infoV2.Length, out var returnedV2, 0) && returnedV2 >= 16;
            if (ParseConnectionEx(info.AsSpan(0, returned), hasV2 ? infoV2.AsSpan(0, returnedV2) : default) is { } link)
            {
                found.Add(link);
            }
        }
        return (portCount, found);
    }

    private static string? ReadPciLocation(string pciInstanceId)
    {
        if (CM_Locate_DevNode(out var node, pciInstanceId, 0) != 0) return null;
        var bus = ReadDevPropUInt(node, BusNumberKey);
        var addr = ReadDevPropUInt(node, AddressKey);
        if (bus is null || addr is null) return null;
        var dev = (addr.Value >> 16) & 0xFFFF;
        var fn = addr.Value & 0xFFFF;
        return string.Format(CultureInfo.InvariantCulture, "PCI bus {0}, dev {1}, fn {2}", bus.Value, dev, fn);
    }

    private static uint? ReadDevPropUInt(uint node, DevPropKey key)
    {
        Span<byte> buf = stackalloc byte[8];
        var size = (uint)buf.Length;
        return CM_Get_DevNode_Property(node, ref key, out var type, ref MemoryMarshal.GetReference(buf), ref size, 0) == 0 && type == 0x7 && size >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(buf)
            : null;
    }

    private static string? HubInterfacePath(string instanceId)
    {
        var guid = HubInterface;
        if (CM_Get_Device_Interface_List_Size(out var length, ref guid, instanceId, 0) != 0 || length <= 1) return null;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_List(ref guid, instanceId, buffer, length, 0) != 0) return null;
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static UsbPortLink? TakeLink(List<UsbPortLink> ports, string id)
    {
        if (UsbVidPidRegex().Match(id) is not { Success: true } m) return null;
        var vid = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var pid = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var at = ports.FindIndex(p => p.VendorId == vid && p.ProductId == pid);
        if (at < 0) return null;
        var link = ports[at];
        ports.RemoveAt(at);
        return link;
    }

    private static string? FirstChildClass(string id, ILookup<string, Peripherals.DevNode> children)
    {
        foreach (var c in children[id])
        {
            if (!string.IsNullOrEmpty(c.Class) && !string.Equals(c.Class, "USB", StringComparison.OrdinalIgnoreCase)) return c.Class;
        }
        return null;
    }

    private static int CountDevices(UsbHubNode hub) => hub.Devices.Count + hub.Hubs.Sum(CountDevices);

    private static bool IsDockName(string? name) => name is not null &&
        (name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("USB4", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Dock", StringComparison.OrdinalIgnoreCase));

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, nint overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref DevPropKey key, out uint type, ref byte buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);
}
