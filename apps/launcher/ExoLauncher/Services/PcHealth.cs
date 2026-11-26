using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// System > This PC: setup that quietly costs games speed. Exo reads it and shows a row only for what is wrong:
/// RAM below its rated speed (EXPO/XMP off), Resizable BAR off, a power plan that isn't Exo's (the one row Exo can fix,
/// with Undo: <see cref="PowerPlan"/>; Windows' own plans switch to it by themselves, a custom one is offered it), an AMD
/// or Intel chipset driver behind its maker's newest (<see cref="ChipsetUpdate"/>), and network adapter settings that add
/// delay, DNS and TCP (Best settings' Network group, <see cref="NetworkBest"/>) or a cable holding Ethernet back. Game Mode has no row: Exo keeps it on
/// itself (<see cref="WindowsGameMode"/>). Each check is a pure decision over a reading (<see cref="Decide"/>); a reading
/// that fails drops its row instead of guessing.
/// </summary>
internal static partial class PcHealth
{
    /// <summary>
    /// One problem and its hint. <paramref name="Applied"/>: Exo's plan on, with Undo; the chipset update waiting for a
    /// restart. <paramref name="Busy"/>: Exo is working on it (the chipset install). <paramref name="Cpu"/>: the
    /// processor Exo's plan is tuned for, for its button. <paramref name="Current"/>: nothing to do, said so (the chipset
    /// driver on its maker's newest, so an update that just finished doesn't make its row vanish). <paramref name="Held"/>:
    /// nothing to do because the maker's newest has a known problem here; shown in Drivers with no button.
    /// </summary>
    internal sealed record Item(string Key, string Label, string Detail, string? Url = null, bool Applied = false, bool CanUndo = false, bool Busy = false, string? Cpu = null, bool Current = false, bool Held = false, bool Off = false);

    /// <summary>A fitted memory module as SMBIOS (type 17) describes it. Speeds in MT/s; 0 when the firmware leaves them out.</summary>
    internal sealed record Dimm(int Speed, int Configured, string? Part, byte FormFactor);

    /// <summary>The main graphics card: whether it shares the system's memory (an integrated GPU) and whether Direct3D 12 reports GPU upload heaps (null: this Windows or driver can't say).</summary>
    internal sealed record GpuBar(string Name, bool Uma, bool? UploadHeap);

    /// <summary>A PC with two graphics chips: the one Windows picks to save power, and the one it picks for performance.</summary>
    internal sealed record HybridGpu(string PowerSaving, string HighPerformance);

    /// <summary>A discrete graphics card's active and maximum PCIe link widths (e.g. running at x8 on an x16 slot).</summary>
    internal sealed record PcieLink(string AdapterName, uint CurrentWidth, uint MaxWidth);

    internal sealed record MemorySlot(bool Populated, byte FormFactor, byte MemoryType, string? DeviceLocator, string? BankLocator);

    internal sealed record SingleChannelIssue(int PopulatedCount, int TotalSlots, bool SameChannelMisplaced, IReadOnlyList<string> PopulatedLocators);

    internal sealed record Reading(
        IReadOnlyList<Dimm> Dimms,
        GpuBar? Gpu,
        string? Cpu,
        PowerPlan.State? Power,
        string? ChipsetInstalled,
        string? ChipsetLatest,
        ChipsetUpdate.Status? ChipsetUpdate = null,
        bool ChipsetAuto = false,
        bool HagsOff = false,
        HybridGpu? Hybrid = null,
        bool FastGpuForGames = false,
        NetworkBest.State? Network = null,
        DriverAdvisories.Advisory? ChipsetSkipped = null,
        DriverAdvisories.Advisory? ChipsetAdvised = null,
        bool? CpuHybridProbe = null,
        string? BoardMaker = null,
        PcieLink? GpuPcie = null,
        bool Laptop = false,
        bool PrimaryOnIntegrated = false,
        IReadOnlyList<MemorySlot>? MemorySlots = null,
        string? BoardProduct = null,
        string? CpuIdentifier = null,
        uint? MicrocodeRevision = null,
        int OsBuild = 0,
        int? OsUbr = null);

    private const byte DimmFormFactor = 0x09; // SMBIOS: a desktop DIMM (SODIMMs are laptops and mini PCs, which seldom offer EXPO/XMP)
    private const byte SodimmFormFactor = 0x0D;
    internal const uint IntelVminFixMicrocode = 0x12F;
    internal const uint IntelVminRootFixMicrocode = 0x12B;
    internal const int Win11_24H2_Build = 26100;
    internal const int Win11_BranchBackportMinUbr = 4112;

    // The speeds memory runs at with EXPO/XMP off (JEDEC's DDR4 and DDR5 bins). A kit at any other speed was set by hand.
    private static readonly int[] Jedec = [1333, 1600, 1866, 2133, 2400, 2666, 2933, 3200, 4000, 4400, 4800, 5200, 5600, 6000, 6400];

    /// <summary>Vendor-specific BIOS menu path for EXPO/XMP or Resizable BAR, or null when the board maker is unknown.</summary>
    internal static string? BiosPath(string? boardMaker, string topic) =>
        (PcAbout.Maker(boardMaker), topic.Trim().ToLowerInvariant()) switch
        {
            ("ASUS", "xmp") => "Ai Tweaker → Ai Overclock Tuner",
            ("ASUS", "rebar") => "Advanced → PCI Subsystem Settings → Re-Size BAR Support",
            ("MSI", "xmp") => "OC → A-XMP / EXPO",
            ("MSI", "rebar") => "Settings → Advanced → PCIe Subsystem Settings",
            ("Gigabyte", "xmp") => "Tweaker → Extreme Memory Profile (X.M.P.)",
            ("Gigabyte", "rebar") => "Settings → IO Ports → Above 4G Decoding & Re-Size BAR",
            ("ASRock", "xmp") => "OC Tweaker → DRAM Configuration",
            ("ASRock", "rebar") => "Advanced → PCI Configuration → Above 4G Decoding & C.A.M.",
            ("Biostar", "xmp") => "Tweaker → Memory Clock Mode / XMP",
            ("Biostar", "rebar") => "Advanced → PCI Subsystem Settings → Re-Size BAR Support",
            ("NZXT", "xmp") => "OC Tweaker → DRAM Configuration",
            ("NZXT", "rebar") => "Advanced → PCI Configuration → Above 4G Decoding & C.A.M.",
            ("EVGA", "xmp") => "Memory → Memory Profile (XMP)",
            ("EVGA", "rebar") => "Advanced → PCIe Configuration → Resizable BAR",
            ("Supermicro", "xmp") => "Advanced → Memory Configuration",
            ("Supermicro", "rebar") => "Advanced → PCIe/PCI/PnP Configuration → Above 4G Decoding",
            ("Dell" or "Alienware", "xmp") => "Advanced → Performance Options → XMP Memory",
            ("Dell" or "Alienware", "rebar") => "Advanced → PCIe Configuration → Resizable BAR",
            ("HP", "xmp") => "Advanced → Memory Profile (XMP)",
            ("Lenovo", "xmp") => "Configuration → Memory Profile (XMP)",
            _ => null,
        };

    internal static IReadOnlyList<Item> Decide(Reading r)
    {
        var items = new List<Item>();
        var x3d = ClassOf(r.Cpu, r.CpuHybridProbe) == PowerProfiles.CpuClass.AmdDualCcdX3d;
        var parked = x3d || ClassOf(r.Cpu, r.CpuHybridProbe) == PowerProfiles.CpuClass.AmdDualCcdZen5;
        if (IntelVminShiftMicrocode(r.Cpu, r.CpuIdentifier, r.MicrocodeRevision, r.Laptop) is { } mc)
        {
            var url = BiosUpdate.SupportPage(r.BoardMaker, r.BoardProduct);
            items.Add(new("cpumicrocode", "CPU microcode",
                $"Your Intel 13th/14th Gen CPU is running microcode 0x{mc.Revision:X} (below Intel's 0x12F Vmin-shift fix). Update your motherboard BIOS to prevent instability and permanent CPU degradation.",
                Url: url));
        }
        if (RyzenBranchPredictionMissing(r.Cpu, r.OsBuild, r.OsUbr))
        {
            var detail = r.OsBuild < 22000
                ? "Windows 10 lacks AMD Ryzen branch-prediction optimizations. Upgrading to Windows 11 24H2 or newer can boost Ryzen gaming FPS by up to 10–11%."
                : "This Windows 11 build lacks AMD Ryzen branch-prediction optimizations (24H2 or 23H2 KB5041587). Updating Windows can boost Ryzen gaming FPS by up to 10–11%.";
            items.Add(new("ryzenbranch", "Windows branch prediction", detail));
        }
        if (SlowRam(r.Dimms) is { } ram)
        {
            var hint = BiosPath(r.BoardMaker, "xmp") is { } path ? $" ({path})" : "";
            items.Add(new("ram", "Memory speed", $"Your RAM runs at {ram.Runs} MT/s; it's rated for {ram.Rated}. Turn on EXPO/XMP in the BIOS{hint}."));
        }
        if (SingleChannelRam(r.MemorySlots) is { } sc)
        {
            var loc = sc.PopulatedLocators.Count > 0 ? $" ({string.Join(", ", sc.PopulatedLocators)})" : "";
            var detail = sc.SameChannelMisplaced
                ? $"Your {sc.PopulatedCount} memory sticks{loc} are in the same channel, so RAM runs in single-channel mode. Move one stick to the other channel (typically slots A2 and B2) for dual-channel performance."
                : $"Only 1 memory stick is installed across {sc.TotalSlots} slots, so RAM runs in single-channel mode. Adding a matching second stick in the other channel improves CPU-bound gaming FPS.";
            items.Add(new("ramchannel", "Memory channels", detail));
        }
        if (ReBarOff(r.Gpu))
        {
            var hint = BiosPath(r.BoardMaker, "rebar") is { } path ? $" ({path})" : "";
            items.Add(new("rebar", "Resizable BAR", $"Off. Turn on Resizable BAR in the BIOS{hint}."));
        }
        if (r.Power is { } power)
        {
            var cpu = ShortCpu(r.Cpu) ?? "processor";
            if (power.Exo)
            {
                // The processor stays named, so Best settings reads "Exo's power plan for Ryzen 5 5600X" before and after.
                if (power.CanUndo) items.Add(new("power", "Power plan", "Exo's plan", Applied: true, CanUndo: true, Cpu: ShortCpu(r.Cpu)));
            }
            else if (HeldBack(power, parked, x3d) is { } why)
                items.Add(new("power", "Power plan", why, Cpu: cpu));
            // Windows' own plans switch to Exo's by themselves; after Undo, only a plan that holds the processor back speaks.
            // The plan in use, by name: the row's value.
            else if (!power.Stock)
                items.Add(new("power", "Power plan", string.IsNullOrWhiteSpace(power.Name) ? "Another plan" : power.Name.Trim(), Cpu: cpu));
        }
        if (ChipsetVendor(r.Cpu) is { } vendor && Chipset(r, vendor) is { } chipset) items.Add(chipset);
        // Frame generation is built for it: DLSS's needs it on (RTX 40 and 50), AMD recommends it for FSR's (RX 7000 and newer), and
        // XeSS's needs it on Intel Arc.
        // Only a card that gains from it and a switch that was turned off by hand or policy speak; a card or Windows that leaves it unset does not.
        if (r.HagsOff && r.Gpu is { Uma: false } gpu && (FrameGenCard().IsMatch(gpu.Name) || ArcCard().IsMatch(gpu.Name)))
            items.Add(new("gpusched", "GPU scheduling", "Off. Turn it on in Windows' Graphics settings, then restart."));
        // Two graphics chips: Windows chooses per game and can choose the built-in one. The switch is Exo's own (GpuPreference), with Undo.
        if (r.Hybrid is { } two)
            items.Add(r.FastGpuForGames
                ? new("gpupref", "Graphics for games", $"Your games run on the {two.HighPerformance}, not the {two.PowerSaving}.", Applied: true, CanUndo: true)
                : new("gpupref", "Graphics for games", $"Windows may run games on the {two.PowerSaving}, not the {two.HighPerformance}."));
        if (!r.Laptop && r.GpuPcie is { CurrentWidth: > 0, MaxWidth: >= 8 } pcie && pcie.CurrentWidth < pcie.MaxWidth)
            items.Add(new("pcie", "Graphics card slot", $"Running at PCIe x{pcie.CurrentWidth} instead of x{pcie.MaxWidth}. Move the card to the top PCIe x16 slot or reseat it."));
        if (!r.Laptop && r.Hybrid is { } hybrid && r.PrimaryOnIntegrated)
            items.Add(new("gpucable", "Display cable", $"Your main monitor is plugged into the motherboard ({hybrid.PowerSaving}) instead of your {hybrid.HighPerformance}. Move the cable to the graphics card for full performance."));
        if (r.Network is { } net) items.AddRange(Network(net));
        return items;
    }

    /// <summary>
    /// The network rows: Best settings' Network group ("network": set once every item the player keeps on is), each of
    /// its items ("net-adapter", "net-dns", "net-tcp": on, with something to put back, or turned off by the player), and a
    /// link slower than its adapter (a cable or port fault, no button).
    /// </summary>
    private static IEnumerable<Item> Network(NetworkBest.State net)
    {
        if (net.Items.Count > 0)
        {
            var left = net.Items.Where(i => !i.On && !i.Off).Select(i => i.Label).ToList();
            yield return new("network", "Network", left.Count == 0 ? "Exo's network settings for games" : string.Join(", ", left), Applied: net.Applied, CanUndo: net.Items.Any(i => i.CanUndo));
            foreach (var item in net.Items)
                yield return new("net-" + item.Key, item.Label, item.Detail, Applied: item.On, CanUndo: item.CanUndo, Off: item.Off);
        }
        if (net.Tune.SlowLink is { } slow)
            yield return new("netlink", "Ethernet", $"{NetworkTune.Speed(slow.Adapter.LinkBps)} of {NetworkTune.Speed(slow.CanBps)}. Try another cable or router port.");
    }

    /// <summary>
    /// The chipset row (AMD's or Intel's, <paramref name="vendor"/>): Restart to finish after an update that needs one, the
    /// version going in while it installs, Up to date on the maker's newest, else when the maker has a newer one what is
    /// installed and what's next (Exo installs it while you're away when it can do so without asking; Update installs it
    /// now). After a failed install, why, with the maker's page. A newest with a known problem here isn't offered ("8.01.20.513
    /// skipped: …"); an installed one with one says so in place of "installed" (<see cref="DriverAdvisories"/>).
    /// </summary>
    private static Item? Chipset(Reading r, string vendor)
    {
        var maker = GpuDriverUpdate.Maker(vendor);
        var label = $"{maker} chipset driver";
        switch (r.ChipsetUpdate)
        {
            case { Phase: Services.ChipsetUpdate.Phase.Restart }:
                return new("chipset", label, $"Restart to finish the {maker} chipset update", Applied: true);
            case { Phase: Services.ChipsetUpdate.Phase.Installing } installing:
                return new("chipset", label, $"Updating to {installing.Version}. {(vendor == "amd" ? "USB and sound may drop" : "Devices may reset")} for a moment", Busy: true);
        }
        var page = vendor == "amd" ? Services.ChipsetUpdate.AmdPage : Services.ChipsetUpdate.IntelPage;
        var runs = r.ChipsetAdvised is { } bad ? $"{r.ChipsetInstalled} installed: {bad.Why}" : $"{r.ChipsetInstalled} installed";
        if (r is { ChipsetSkipped: { } skip, ChipsetInstalled: not null, ChipsetLatest: { } skipped })
            return new("chipset", label, $"{runs} · {DriverAdvisories.Skipped(skipped, skip)}", Url: r.ChipsetAdvised is null ? null : page, Current: r.ChipsetAdvised is null, Held: r.ChipsetAdvised is null);
        if (!ChipsetBehind(r.ChipsetInstalled, r.ChipsetLatest))
            return r is { ChipsetInstalled: { } installed, ChipsetLatest: not null }
                ? r.ChipsetAdvised is null ? new("chipset", label, $"{installed} · newest", Current: true) : new("chipset", label, runs, Url: page)
                : null;
        var now = $"{runs} · {r.ChipsetLatest}";
        if (r.ChipsetUpdate is { Phase: Services.ChipsetUpdate.Phase.Failed } failed)
            return new("chipset", label, $"{now} available. {failed.Reason}", Url: vendor == "amd" ? Services.ChipsetUpdate.AmdPage : Services.ChipsetUpdate.IntelPage);
        return new("chipset", label, r.ChipsetAuto ? $"{now} installs while you're away" : $"{now} available");
    }

    /// <summary>Whose chipset driver Exo keeps up to date on this processor: amd on a Ryzen, intel on an Intel processor, else null.</summary>
    internal static string? ChipsetVendor(string? cpu) =>
        cpu is null ? null
        : cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? "amd"
        : cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel"
        : null;

    /// <summary>
    /// Why the active plan costs the processor speed, or null when it doesn't. Held back: Power saver, a maximum
    /// processor state under 100, boost disabled, or Balanced with Best power efficiency on AC. On a dual-CCD X3D or
    /// Zen 5's 9900X/9950X (<paramref name="parked"/>), also core parking off (minimum cores 100): AMD's driver parks the
    /// second CCD so a game stays on one CCD (the cache CCD on X3D), and a plan that keeps every core awake stops it.
    /// Anything else (Balanced, High performance, a third-party plan tuned for speed) isn't holding the processor back.
    /// </summary>
    internal static string? HeldBack(PowerPlan.State power, bool parked, bool x3d)
    {
        var plan = string.IsNullOrWhiteSpace(power.Name) ? "Your power plan" : Quoted(power.Name);
        if (power.Active == PowerPlan.PowerSaver) return "Your PC uses Power saver, which slows your processor down.";
        if (power.Cpu?.MaxState is < 100 and var max) return $"{plan} caps your processor at {max}%.";
        if (power.Cpu?.BoostMode == 0) return $"{plan} turns processor boost off.";
        if (power.Active == PowerPlan.Balanced && power.Overlay == PowerPlan.BestEfficiency && !power.Battery)
            return "Your PC uses Balanced with Best power efficiency, which slows your processor down.";
        if (parked && power.Cpu?.MinCores >= 100)
            return x3d
                ? $"{plan} keeps every core awake, so Windows can't keep games on your processor's V-Cache cores."
                : $"{plan} keeps every core awake, so Windows can't keep a game on one half of your processor.";
        return null;
    }

    private static string Quoted(string? name) => string.IsNullOrWhiteSpace(name) ? "another plan" : $"“{name.Trim()}”";

    /// <summary>
    /// Which of Exo's plan profiles fits this processor. AMD by name: dual-CCD X3D, Zen 5's 9900X/9950X (parked in Game
    /// Mode like X3D), dual-CCD (Ryzen 9 x900/x950 and Threadripper, the 12- and 16-core HX laptops, Ryzen AI Max
    /// 390/395), any other Ryzen single-CCD. Intel: hybrid when CPUID says so
    /// (<paramref name="hybrid"/>; 12th gen and newer with E-cores, Core Ultra), else up to 11th gen and E-core-less
    /// models. When CPUID can't say, the name decides. Anything else is unknown. Intel's own numbers never read as AMD's
    /// (the i9-9900X, i9-9900 and i9-7900X share a 9900X's, a 5900's and a 7900X's).
    /// </summary>
    internal static PowerProfiles.CpuClass ClassOf(string? name, bool? hybrid)
    {
        if (string.IsNullOrWhiteSpace(name)) return PowerProfiles.CpuClass.Unknown;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return hybrid ?? HybridIntel().IsMatch(name) ? PowerProfiles.CpuClass.IntelHybrid : PowerProfiles.CpuClass.IntelNonHybrid;
        if (DualCcdX3d().IsMatch(name)) return PowerProfiles.CpuClass.AmdDualCcdX3d;
        if (DualCcdZen5().IsMatch(name)) return PowerProfiles.CpuClass.AmdDualCcdZen5;
        if (DualCcd().IsMatch(name)) return PowerProfiles.CpuClass.AmdDualCcd;
        return name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? PowerProfiles.CpuClass.AmdSingleCcd : PowerProfiles.CpuClass.Unknown;
    }

    /// <summary>The processor's name as people say it: "Ryzen 5 5600X", "Core i7-14700K", "Core Ultra 7 265K".</summary>
    internal static string? ShortCpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var plain = CpuNoise().Replace(CpuTail().Replace(name, ""), " ");
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length == 0 ? null : plain;
    }

    /// <summary>SMBIOS chassis types of a portable PC: portable, laptop, notebook, handheld, sub notebook, tablet, convertible, detachable.</summary>
    private static readonly byte[] PortableChassis = [8, 9, 10, 11, 14, 30, 31, 32];

    /// <summary>A laptop (or tablet): a system battery, or a portable chassis in SMBIOS.</summary>
    internal static bool Laptop(bool battery, byte? chassis) => battery || chassis is { } type && PortableChassis.Contains(type);

    /// <summary>
    /// The slowest module's speed and the kit's rating when the kit runs at a JEDEC default below what it's rated for:
    /// EXPO/XMP is off. The rating is the larger of what the firmware reports and what the part number says
    /// (many boards report the JEDEC speed as the module's own). Desktop DIMMs only.
    /// </summary>
    internal static (int Runs, int Rated)? SlowRam(IReadOnlyList<Dimm> dimms)
    {
        var fitted = dimms.Where(d => d.Configured > 0 && d.FormFactor == DimmFormFactor).ToList();
        if (fitted.Count == 0 || fitted.Count != dimms.Count(d => d.Configured > 0)) return null;
        var runs = fitted.Min(d => d.Configured);
        var rated = fitted.Min(d => Math.Max(d.Speed, RatedFromPart(d.Part) ?? 0));
        return rated - runs >= 100 && Jedec.Any(speed => Math.Abs(speed - runs) <= 2) ? (runs, rated) : null;
    }

    /// <summary>The EXPO/XMP speed an enthusiast kit's part number spells out (Corsair, G.Skill, Kingston, Crucial, Patriot, ADATA, TeamGroup), else null.</summary>
    internal static int? RatedFromPart(string? part)
    {
        if (string.IsNullOrWhiteSpace(part) || PartRating().Match(part.Trim()) is not { Success: true } m) return null;
        var mt = m.Groups["mt"].Success ? int.Parse(m.Groups["mt"].Value)
            : m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) * 100
            : int.Parse(m.Groups["t"].Value) * 10;
        return mt is >= 1600 and <= 12000 ? mt : null;
    }

    /// <summary>
    /// Resizable BAR is off when a card that supports it (GeForce RTX 30 and newer, Radeon RX 6000 and newer, Intel Arc)
    /// has its own memory and Direct3D 12 says the CPU can't see all of it (no GPU upload heaps). An integrated GPU, an
    /// older card or a Windows that can't say shows nothing.
    /// </summary>
    internal static bool ReBarOff(GpuBar? gpu) => gpu is { Uma: false, UploadHeap: false } && ReBarCard().IsMatch(gpu.Name);

    internal static bool ChipsetBehind(string? installed, string? latest) => VersionNumber.Behind(installed, latest);

    /// <summary>The newest chipset package AMD's page links to (its installer is named after its version), else null.</summary>
    internal static string? LatestChipset(string html) =>
        ChipsetLink().Matches(html).Select(m => m.Groups[1].Value).Where(v => VersionNumber.Parse(v) is not null).Max(VersionNumber.Order);

    /// <summary>
    /// Hardware-accelerated GPU scheduling is off only when HwSchMode says 1 (Settings or policy wrote it); 2 is on, and a
    /// missing value leaves it to Windows and the driver, which turn it on for cards that support it.
    /// </summary>
    internal static bool HagsOff(object? value) => value is int i && i == 1;

    internal delegate void Visit(int type, ReadOnlySpan<byte> structure, List<string> strings);

    /// <summary>
    /// Each structure in SMBIOS's raw table (GetSystemFirmwareTable's 'RSMB' block: an 8-byte header, then the
    /// structures) with its strings. A cut-off or malformed table ends the walk where it breaks.
    /// </summary>
    internal static void Walk(ReadOnlySpan<byte> raw, Visit visit)
    {
        if (raw.Length < 8) return;
        var table = raw[8..];
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        if (declared < table.Length) table = table[..(int)declared];
        var at = 0;
        while (at + 4 <= table.Length)
        {
            int type = table[at], length = table[at + 1];
            if (length < 4 || at + length > table.Length) break;
            // The strings follow the formatted part, each ending in 0 and the set in a second 0 (two 0s when there are none).
            var strings = new List<string>();
            var end = at + length;
            while (end < table.Length)
            {
                var zero = table[end..].IndexOf((byte)0);
                if (zero < 0) { end = table.Length; break; }
                if (zero == 0) { end += strings.Count == 0 ? 2 : 1; break; }
                strings.Add(Encoding.ASCII.GetString(table.Slice(end, zero)));
                end += zero + 1;
            }
            visit(type, table.Slice(at, length), strings);
            if (type == 127) break; // end of table
            at = end;
        }
    }

    /// <summary>The memory modules in SMBIOS (type 17). Empty slots are left out.</summary>
    internal static List<Dimm> ParseDimms(ReadOnlySpan<byte> raw)
    {
        var dimms = new List<Dimm>();
        Walk(raw, (type, s, strings) =>
        {
            if (type != 17 || s.Length < 0x1B || U16(s, 0x0C) == 0) return;
            var speed = U16(s, 0x15);
            if (speed == 0xFFFF) speed = (int)U32(s, 0x54);
            var configured = U16(s, 0x20);
            if (configured == 0xFFFF) configured = (int)U32(s, 0x58);
            var part = s[0x1A] is > 0 and var index && index <= strings.Count ? strings[index - 1].Trim() : null;
            dimms.Add(new Dimm(speed, configured, part, s[0x0E]));
        });
        return dimms;
    }

    internal static List<MemorySlot> ParseMemorySlots(ReadOnlySpan<byte> raw)
    {
        var slots = new List<MemorySlot>();
        Walk(raw, (type, s, strings) =>
        {
            if (type != 17 || s.Length < 0x13) return;
            var size = U16(s, 0x0C);
            var populated = size is not (0 or 0xFFFF);
            var device = s[0x10] is > 0 and var di && di <= strings.Count ? strings[di - 1].Trim() : null;
            var bank = s[0x11] is > 0 and var bi && bi <= strings.Count ? strings[bi - 1].Trim() : null;
            slots.Add(new MemorySlot(populated, s[0x0E], s[0x12], device, bank));
        });
        return slots;
    }

    internal static SingleChannelIssue? SingleChannelRam(IReadOnlyList<MemorySlot>? slots)
    {
        if (slots is null || slots.Count < 2) return null;
        var populated = slots.Where(s => s.Populated).ToList();
        if (populated.Count == 0) return null;
        foreach (var slot in populated)
        {
            if (slot.FormFactor is not (DimmFormFactor or SodimmFormFactor)) return null;
            if (slot.MemoryType is 0x1B or 0x1C or 0x1D or 0x1E or 0x23) return null;
        }
        var locators = populated
            .Select(s => string.IsNullOrWhiteSpace(s.DeviceLocator) ? s.BankLocator : s.DeviceLocator)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim())
            .ToList();
        if (populated.Count == 1)
            return new SingleChannelIssue(1, slots.Count, SameChannelMisplaced: false, locators);
        if (slots.Count < 4) return null;

        var boardChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (ChannelOf(slot.DeviceLocator, slot.BankLocator) is { } ch) boardChannels.Add(ch);
        }
        if (boardChannels.Count < 2) return null;

        var populatedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in populated)
        {
            if (ChannelOf(slot.DeviceLocator, slot.BankLocator) is not { } ch) return null;
            populatedChannels.Add(ch);
        }
        return populatedChannels.Count == 1
            ? new SingleChannelIssue(populated.Count, slots.Count, SameChannelMisplaced: true, locators)
            : null;
    }

    internal static string? ChannelOf(string? deviceLocator, string? bankLocator)
    {
        return ExtractChannel(deviceLocator) ?? ExtractChannel(bankLocator);
    }

    private static string? ExtractChannel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (ChannelWordPattern().Match(s) is { Success: true } m1) return m1.Groups[1].Value.ToUpperInvariant();
        if (SlotLetterPattern().Match(s) is { Success: true } m2) return m2.Groups[1].Value.ToUpperInvariant();
        if (BareSlotLetterPattern().Match(s) is { Success: true } m3) return m3.Groups[1].Value.ToUpperInvariant();
        return null;
    }

    internal static uint? ParseMicrocodeRevision(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8)
        {
            var high = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
            return high > 0 ? high : null;
        }
        if (bytes.Length == 4)
        {
            var rev = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return rev > 0 ? rev : null;
        }
        return null;
    }

    internal static (int Family, int Model)? ParseIntelFamilyModel(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        var m = IntelIdentifierPattern().Match(identifier);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, out var fam) && int.TryParse(m.Groups[2].Value, out var mod)
            ? (fam, mod)
            : null;
    }

    internal static (uint Revision, bool Critical)? IntelVminShiftMicrocode(string? cpu, string? cpuIdentifier, uint? microcodeRevision, bool laptop)
    {
        if (laptop || microcodeRevision is not { } rev || rev == 0 || rev >= IntelVminFixMicrocode) return null;
        var isIntel = cpu?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true
            || cpuIdentifier?.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) == true;
        if (!isIntel) return null;
        if (cpu is not null && (cpu.Contains("Ultra", StringComparison.OrdinalIgnoreCase) || IntelMobileOrLowPower().IsMatch(cpu)))
            return null;

        var fm = ParseIntelFamilyModel(cpuIdentifier);
        var affectedSilicon = fm switch
        {
            (6, 0xB7) => cpu is null || !Intel12thGen().IsMatch(cpu),
            (6, 0xBF) => cpu is not null && Intel1314Desktop().IsMatch(cpu),
            null => cpu is not null && Intel1314Desktop().IsMatch(cpu),
            _ => false,
        };
        return affectedSilicon ? (rev, rev < IntelVminRootFixMicrocode) : null;
    }

    internal static bool RyzenBranchPredictionMissing(string? cpu, int osBuild, int? osUbr)
    {
        if (string.IsNullOrWhiteSpace(cpu) || !cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)) return false;
        if (osBuild <= 0 || osBuild >= Win11_24H2_Build) return false;
        if (osBuild is 22631 or 22621 && osUbr >= Win11_BranchBackportMinUbr) return false;
        return true;
    }

    /// <summary>The system enclosure's chassis type (SMBIOS type 3, lock bit dropped), else null.</summary>
    internal static byte? ChassisType(ReadOnlySpan<byte> raw)
    {
        byte? chassis = null;
        Walk(raw, (type, s, _) => { if (type == 3 && s.Length > 5 && chassis is null) chassis = (byte)(s[5] & 0x7F); });
        return chassis;
    }

    internal static int U16(ReadOnlySpan<byte> s, int offset) => s.Length >= offset + 2 ? BinaryPrimitives.ReadUInt16LittleEndian(s[offset..]) : 0;
    internal static uint U32(ReadOnlySpan<byte> s, int offset) => s.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]) : 0;

    // ---- Reading this PC ----

    private static readonly Lazy<byte[]> Smbios = new(SmbiosTable, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyList<Dimm>> Memory = new(() => ParseDimms(Smbios.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyList<MemorySlot>> MemorySlotTable = new(() => ParseMemorySlots(Smbios.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HybridGpu?> Hybrid = new(ReadHybrid, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<GpuBar?> Gpu = new(() => WithSmartAccessMemory(ReadGpu()), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<PcieLink?> GpuPcie = new(() => ReadGpuPcie(Gpu.Value?.Name), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The SMBIOS table, read once; PC › Overview reads the processor and memory from it too (<see cref="PcAbout"/>).</summary>
    internal static byte[] SmbiosRaw() => Smbios.Value;

    /// <summary>The main graphics card as the Resizable BAR check reads it, once.</summary>
    internal static GpuBar? MainGpu() => Gpu.Value;

    /// <summary>
    /// On a Radeon, AMD's own answer (ADLX's Smart Access Memory, AMD's Resizable BAR) wins over Direct3D 12's upload
    /// heaps, which a driver may leave out with Resizable BAR on; when ADLX can't say, Direct3D 12's answer stays.
    /// </summary>
    private static GpuBar? WithSmartAccessMemory(GpuBar? gpu) =>
        gpu is { Uma: false } && AmdAdlx.Present && RadeonName().IsMatch(gpu.Name) && AmdAdlx.SmartAccessMemory() is { } sam
            ? gpu with { UploadHeap = sam } : gpu;
    private static int _logged;

    /// <summary>Exo's plan for this PC: its processor's class (CPUID and name) and whether it's a laptop.</summary>
    internal static PowerProfiles.Profile ThisPcProfile() =>
        PowerProfiles.For(ClassOf(CpuName(), CpuHybrid()), ThisPcIsLaptop());

    /// <summary>A PC with a battery or a portable chassis (laptop, handheld, tablet).</summary>
    internal static bool ThisPcIsLaptop() => Laptop(NativePower.HasBattery(), ChassisType(Smbios.Value));

    internal static async Task<Reading> ReadAsync(PowerPlan power, bool chipsetAuto, bool fastGpuForGames, CancellationToken ct)
    {
        var cpu = CpuName();
        var chipset = Services.ChipsetUpdate.For(cpu);
        var installed = chipset?.Installed();
        string? latest = null;
        if (chipset is not null && installed is not null)
        {
            try { latest = (await chipset.LatestAsync().WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))?.Version; }
            catch (TimeoutException) { /* no row this time; the answer is kept when it lands */ }
        }
        WindowsGameMode.KeepOn();
        object? scheduling = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            scheduling = key?.GetValue("HwSchMode");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left unknown: no row */ }
        var update = installed is null ? null : chipset!.Current(latest, Services.ChipsetUpdate.BootedAt());
        var subject = chipset is null ? null : DriverAdvisories.Chipset(chipset.Vendor, cpu);
        var skipped = subject is null || latest is null || VersionNumber.Same(latest, installed) ? null : DriverAdvisories.Shared.Against(subject, latest);
        var advised = subject is null ? null : DriverAdvisories.Shared.Against(subject, installed);
        var laptop = ThisPcIsLaptop();
        var primaryOnIntegrated = !laptop && Hybrid.Value is not null && ReadPrimaryOnIntegrated();
        var (cpuIdentifier, microcode) = ReadCpuSilicon();
        var (osBuild, osUbr) = PcAbout.OsBuildAndUbr();
        var reading = new Reading(Memory.Value, Gpu.Value, cpu, power.Read(), installed, latest, update, chipsetAuto && installed is not null && !chipset!.WindowsAsks(), HagsOff(scheduling), Hybrid.Value, fastGpuForGames, NetworkBest.Shared.Read(), skipped, advised, CpuHybrid(), BoardMaker: PcAbout.Maker(PcAbout.Bios.Maker), GpuPcie: GpuPcie.Value, Laptop: laptop, PrimaryOnIntegrated: primaryOnIntegrated, MemorySlots: MemorySlotTable.Value, BoardProduct: PcAbout.Bios.Product, CpuIdentifier: cpuIdentifier, MicrocodeRevision: microcode, OsBuild: osBuild, OsUbr: osUbr);
        if (Interlocked.Exchange(ref _logged, 1) == 0) AppLog.Info("PC health: " + Describe(reading));
        return reading;
    }

    /// <summary>One app.log line of what was read, so a row that did or didn't show can be explained.</summary>
    internal static string Describe(Reading r)
    {
        var ram = r.Dimms.Count == 0 ? "RAM not reported"
            : $"RAM {string.Join(", ", r.Dimms.Select(d => $"{d.Configured}/{d.Speed} MT/s{(RatedFromPart(d.Part) is { } p ? $" (part {p})" : "")}"))}";
        var bar = r.Gpu is null ? "GPU not read" : $"{r.Gpu.Name}: {(r.Gpu.Uma ? "shared memory" : r.Gpu.UploadHeap switch { true => "Resizable BAR on", false => "Resizable BAR off", null => "Resizable BAR unknown" })}";
        var plan = r.Power is null ? "plan not read"
            : $"plan {r.Power.Active} overlay {r.Power.Overlay}{(r.Power.Exo ? " (Exo's)" : "")} class {PowerProfiles.Keys[ClassOf(r.Cpu, r.CpuHybridProbe)]} max {r.Power.Cpu?.MaxState?.ToString() ?? "-"}% boost {r.Power.Cpu?.BoostMode?.ToString() ?? "-"} min cores {r.Power.Cpu?.MinCores?.ToString() ?? "-"}%";
        return $"{ram}; {bar}{(r.GpuPcie is { } pcie ? $" (PCIe x{pcie.CurrentWidth}/x{pcie.MaxWidth})" : "")}; {plan}; chipset {r.ChipsetInstalled ?? "-"} (newest {r.ChipsetLatest ?? "-"}{(r.ChipsetUpdate is { } u ? $", update {u.Phase} {u.Version}" : "")}); GPU scheduling {(r.HagsOff ? "off" : "on or left to Windows")}; {(r.Hybrid is { } two ? $"two graphics chips ({two.PowerSaving} and {two.HighPerformance}{(r.PrimaryOnIntegrated ? ", primary on integrated" : "")}), games on {(r.FastGpuForGames ? "the fast one by Exo" : "Windows' pick")}" : "one graphics chip")}; network {(r.Network is { } n ? $"{string.Join(", ", n.Items.Select(i => $"{i.Key} {(i.Off ? "off" : i.On ? "on" : "to set")}"))}{(n.Tune.SlowLink is { } s ? $", slow link {NetworkTune.Speed(s.Adapter.LinkBps)}" : "")}" : "not read")}.";
    }

    private static readonly Lazy<string?> Processor = new(ReadCpuName, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The processor's name as Windows has it ("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"), or null. Read once: Settings asks on the UI thread.</summary>
    internal static string? CpuName() => Processor.Value;

    private static string? ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static (string? Identifier, uint? Microcode) ReadCpuSilicon()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var id = (key?.GetValue("Identifier") as string)?.Trim();
            var rev = key?.GetValue("Update Revision") is byte[] bytes ? ParseMicrocodeRevision(bytes) : null;
            return (id, rev);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return (null, null); }
    }

    [GeneratedRegex(@"amd_chipset_software_(\d+\.\d+\.\d+\.\d+)\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetLink();

    [GeneratedRegex(@"\b(?:7950|7900|9950|9900)X3D|\b(?:7945|9955)HX3D", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcdX3d();

    // Zen 5's desktop 9900X and 9950X: AMD's chipset driver parks their second CCD in Game Mode, as on X3D (AnandTech, Gamers Nexus).
    [GeneratedRegex(@"\b99[05]0X\b", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcdZen5();

    // Ryzen 9 3900/3950, 5900/5950, 7900/7950, 9900/9950 (X, XT or neither), every Threadripper, the 12- and 16-core HX laptops, and Ryzen AI Max 390/395.
    [GeneratedRegex(@"\b(?:39|59|79|99)[05]0(?:XT?)?\b|Threadripper|\b(?:7845|7945|9850|9955)HX\b|\bAI\s+MAX\+?\s+39[05]\b", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcd();

    // Intel with E-cores, when CPUID can't say: Core Ultra, and 12th to 14th gen i5/i7/i9 except the E-core-less i5-12400/12500 family.
    [GeneratedRegex(@"Core\(?T?M?\)?\s*Ultra|\bi[79]-1[2-4]\d{3}|\bi5-1(?:2[6-9]|[34]\d)\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex HybridIntel();

    // "(R)", "(TM)", "11th Gen", "AMD", "Intel".
    [GeneratedRegex(@"\((?:R|TM)\)|\b\d+(?:st|nd|rd|th)\s+Gen\b|\bAMD\b|\bIntel\b", RegexOptions.IgnoreCase)]
    private static partial Regex CpuNoise();

    // " 6-Core Processor", " with Radeon Graphics", " w/ Radeon 780M Graphics", " CPU @ 3.60GHz", " Processor".
    [GeneratedRegex(@"\s+(?:\d+-Core\s+Processor|(?:with|w/)\s+Radeon.*|CPU\b.*|@.*|Processor\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CpuTail();

    [GeneratedRegex(@"\bRTX\s*[3-9]0[5-9]0|\bRX\s*[6-9]\d{3}|\bArc\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReBarCard();

    // Hardware-accelerated GPU scheduling: GeForce RTX 30, 40 and 50 (3050 to 5090; not workstation RTX 4000 Ada) and Radeon RX 7000 and newer (including laptop 7600S).
    [GeneratedRegex(@"\bRTX\s*[3-5]0[5-9]0\b|\bRX\s*[79]\d{3}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex FrameGenCard();

    [GeneratedRegex(@"Arc", RegexOptions.IgnoreCase)]
    private static partial Regex ArcCard();

    [GeneratedRegex(@"\b(?:AMD|Radeon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonName();

    // Corsair CMK16GX4M2D3600C18, G.Skill F5-6000J3038F16G, Kingston KF3600C17 / KF436C16 / HX436C17, Crucial BL8G36C16 / CP16G60C36,
    // Patriot PVS416G360C8, ADATA AX4U360038G18, TeamGroup TF3D416G3600HC18 (mt: MT/s, h: hundreds, t: tens).
    [GeneratedRegex(@"^(?:CM\w*?X[45]M\d[A-Z]?(?<mt>\d{4})C\d|F[45]-(?<mt>\d{4})[A-Z]\d|KF(?<mt>\d{4})C|(?:KF|HX)[45](?<h>\d{2})C|(?:BL|CP)\w*?G(?<h>\d{2})C|PV\w*?G(?<t>\d{3})C|AX[45]U(?<mt>\d{4})|\w*?(?<mt>\d{4})HC\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PartRating();

    [GeneratedRegex(@"\bChannel\s*[-_]?\s*([A-H]|\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelWordPattern();

    [GeneratedRegex(@"(?:DIMM|SLOT|RAM|MEM|CH)[-_ ]*([A-H])[0-4]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlotLetterPattern();

    [GeneratedRegex(@"^([A-H])[1-4]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareSlotLetterPattern();

    [GeneratedRegex(@"Intel\w*\s+Family\s+(\d+)\s+Model\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelIdentifierPattern();

    [GeneratedRegex(@"\bi[579]-1[34]\d{3}(?:K|KF|KS|F)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Intel1314Desktop();

    [GeneratedRegex(@"\bi[3579]-12\d{3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Intel12thGen();

    [GeneratedRegex(@"\bi[3579]-1[34]\d{3}(?:T|TE|H|HX|HK|U|P|G\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelMobileOrLowPower();
}
