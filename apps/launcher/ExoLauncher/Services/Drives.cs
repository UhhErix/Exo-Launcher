using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Drives: each physical drive with its health, link, firmware, temperature and space, and a line only for what is
/// wrong. Every read is a standard user's: the storage driver asked through a handle opened with no access (the one
/// <see cref="PcAbout"/> opens), Windows' device properties and registry, and a DirectStorage (BypassIO) query on a game
/// file opened for reading. Three things Exo can set, each with Windows' permission: write caching on,
/// write-cache flushing back on, and Windows' weekly optimize (retrim) able to run again. Exo never flashes firmware,
/// switches the storage driver, changes a sector format, sends a drive a command of its own,
/// trims, tests or benchmarks it. This file is the pure part: the parsers for what the driver answers and the decisions
/// over them (DrivesTests); Drives.Native.cs reads this PC, DriveSpace.cs what fills it, DriveHeat.cs a game's session.
/// </summary>
internal static partial class Drives
{
    internal enum Bus { Nvme, Sata, Usb, Raid, Other }

    /// <summary>The PCIe link as negotiated against what the drive can do: generation (1 to 6) and lanes.</summary>
    internal sealed record Link(int Gen, int Width, int MaxGen, int MaxWidth);

    /// <summary>The drive's temperature now and its own warning and critical points (°C; null: the drive doesn't say).</summary>
    internal sealed record Temperature(int NowC, int? WarnC, int? CriticalC);

    /// <summary>
    /// Write caching on, and Windows' "turn off write-cache buffer flushing" on a drive whose cache isn't power-protected.
    /// Off: Windows reports caching disabled on a drive that has a cache and doesn't refuse the change.
    /// </summary>
    internal sealed record WriteCache(bool On, bool FlushOff, bool Off = false);

    /// <summary>
    /// What an NVMe drive says of itself that its maker's tool shows as hidden settings, all read without a vendor driver:
    /// its power state now (0 is full power) and how many it has, whether it supports and runs autonomous power states
    /// (APST), the host memory (HMB) it asks for (a drive without its own DRAM asks; 0: it has DRAM or doesn't use it) and
    /// whether Windows gave it, its volatile write cache, and its sector size with whether a 4 KB format exists.
    /// </summary>
    internal sealed record NvmeFacts(
        int? PowerState = null, int PowerStates = 0, bool Apst = false, bool? ApstOn = null, long HmbBytes = 0, bool? HmbOn = null,
        bool VolatileCache = false, int? SectorBytes = null, bool FourK = false);

    /// <summary>Identify Controller's fields Exo reads: firmware slots, temperature limits (°C), power states, APST, HMB preferred size, volatile write cache.</summary>
    internal sealed record Identity(int Slots, int? WarnC, int? CriticalC, int PowerStates = 0, bool Apst = false, long HmbBytes = 0, bool VolatileCache = false);

    /// <summary>
    /// What the drive reports about its wear and faults. NVMe from its SMART/Health log; SATA from its SMART attributes.
    /// Throttle counts are the NVMe thermal management transitions and seconds (both levels added).
    /// </summary>
    internal sealed record Health(
        int? LifeUsed = null, int? Spare = null, int? SpareThreshold = null, double? WrittenTb = null, long? Hours = null,
        long? PowerCycles = null, long? UnsafeShutdowns = null, long? MediaErrors = null, int CriticalWarning = 0,
        long? ThrottleEvents = null, long? ThrottleSeconds = null, bool? Failing = null, long? BadSectors = null);

    internal sealed record Volume(string Letter, long Total, long Free);

    /// <summary>
    /// One physical drive. <paramref name="Driver"/>: the storage controller's driver service ("stornvme"); FlushSetting:
    /// Windows' CacheIsPowerProtected for it (1: flushing turned off), which outlives the cache property until a restart.
    /// </summary>
    internal sealed record Disk(
        int Number, string? Model, Bus Bus, string Kind, string? Firmware, int? FirmwareSlots, Link? Link, string? Driver,
        Health? Health, Temperature? Temperature, WriteCache? Cache, IReadOnlyList<Volume> Volumes, string? InstanceId, int? FlushSetting = null,
        NvmeFacts? Nvme = null, int? CacheSetting = null);

    /// <summary>What keeps a file off DirectStorage's fast path: the driver that said no and its reason, as Windows words them.</summary>
    internal sealed record Blocked(string Driver, string Reason);

    /// <summary>A game on a drive: its size, its shader caches, and for one that loads through DirectStorage what keeps it off the fast path.</summary>
    internal sealed record Game(string Id, string Title, string Letter, long Bytes, long CacheBytes = 0, bool DirectStorage = false, Blocked? Blocked = null, int BlockedFiles = 0);

    internal sealed record Verdict(string Key, string Text, bool Bad);

    // ---- Parsers ----

    /// <summary>The NVMe SMART/Health log (log page 02h, 512 bytes). Null when cut short.</summary>
    internal static Health? ParseSmartLog(ReadOnlySpan<byte> log)
    {
        if (log.Length < 232) return null;
        // 128-bit counters: the high half is 0 on any drive that exists.
        static long Count(ReadOnlySpan<byte> s, int at) => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(s[at..]), long.MaxValue);
        static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
        return new Health(
            LifeUsed: log[5], Spare: log[3], SpareThreshold: log[4],
            WrittenTb: Count(log, 48) * 512_000.0 / 1e12, // data units of 1,000 sectors of 512 bytes
            Hours: Count(log, 128), PowerCycles: Count(log, 112), UnsafeShutdowns: Count(log, 144), MediaErrors: Count(log, 160),
            CriticalWarning: log[0], ThrottleEvents: U32(log, 216) + (long)U32(log, 220), ThrottleSeconds: U32(log, 224) + (long)U32(log, 228));
    }

    /// <summary>
    /// Identify Controller (4,096 bytes): the firmware slots (FRMW), the warning and critical temperatures (WCTEMP, CCTEMP:
    /// Kelvin there, °C here), the power states (NPSS, zero-based), APST support (APSTA), the host memory buffer it
    /// prefers (HMPRE, in 4 KiB units) and a volatile write cache (VWC).
    /// </summary>
    internal static Identity? ParseIdentify(ReadOnlySpan<byte> id)
    {
        if (id.Length < 526) return null;
        static int? Celsius(ushort kelvin) => kelvin > 273 ? kelvin - 273 : null;
        return new Identity((id[260] >> 1) & 7, Celsius(BinaryPrimitives.ReadUInt16LittleEndian(id[266..])), Celsius(BinaryPrimitives.ReadUInt16LittleEndian(id[268..])),
            id[263] + 1, (id[265] & 1) != 0, BinaryPrimitives.ReadUInt32LittleEndian(id[272..]) * 4096L, (id[525] & 1) != 0);
    }

    /// <summary>
    /// Identify Namespace (4,096 bytes): the sector size in use (FLBAS picks one of the LBA formats; LBADS is its size as a
    /// power of two) and whether a 4 KB format without metadata exists. Changing formats erases the drive: Exo only says.
    /// </summary>
    internal static (int SectorBytes, bool FourK)? ParseNamespace(ReadOnlySpan<byte> ns)
    {
        if (ns.Length < 192) return null;
        int formats = Math.Min(ns[25] + 1, 16), current = ns[26] & 0xF;
        if (current >= formats) return null;
        static uint Format(ReadOnlySpan<byte> ns, int i) => BinaryPrimitives.ReadUInt32LittleEndian(ns[(128 + 4 * i)..]);
        var shift = (int)(Format(ns, current) >> 16) & 0xFF;
        if (shift is < 9 or > 16) return null;
        var fourK = false;
        for (var i = 0; i < formats; i++) fourK |= ((Format(ns, i) >> 16) & 0xFF) == 12 && (Format(ns, i) & 0xFFFF) == 0;
        return (1 << shift, fourK);
    }

    /// <summary>STORAGE_TEMPERATURE_DATA_DESCRIPTOR: the first sensor (the drive's composite) against the drive's own limits.</summary>
    internal static Temperature? ParseTemperature(ReadOnlySpan<byte> d)
    {
        if (d.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(d[12..]) == 0) return null;
        var now = BinaryPrimitives.ReadInt16LittleEndian(d[26..]);
        if (now is <= -40 or >= 150) return null;
        static int? Limit(short c) => c is > 0 and < 150 ? c : null;
        return new Temperature(now, Limit(BinaryPrimitives.ReadInt16LittleEndian(d[10..])), Limit(BinaryPrimitives.ReadInt16LittleEndian(d[8..])));
    }

    /// <summary>
    /// STORAGE_WRITE_CACHE_PROPERTY: caching on (WriteCacheEnabled 2), and flushing turned off by the user
    /// (UserDefinedPowerProtection) on a cache that isn't non-volatile (NVCacheEnabled: a drive with power-loss protection).
    /// Off: caching disabled (1) on a drive with a cache (WriteCacheType not None) that doesn't say it can't change
    /// (WriteCacheChangeable not 1; StorNVMe answers 0, unknown).
    /// </summary>
    internal static WriteCache? ParseWriteCache(ReadOnlySpan<byte> d)
    {
        if (d.Length < 27) return null;
        var enabled = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        var on = enabled == 2;
        var off = enabled == 1 && BinaryPrimitives.ReadInt32LittleEndian(d[8..]) != 1 && BinaryPrimitives.ReadInt32LittleEndian(d[16..]) != 1;
        return new WriteCache(on, on && d[25] != 0 && d[26] == 0, off);
    }

    /// <summary>The power state in a Get Features Power Management (02h) answer: bits 4:0 of its first dword.</summary>
    internal static int PowerStateOf(uint dword) => (int)(dword & 0x1F);

    /// <summary>FS_BPIO_OUTPUT of a query: null when the whole stack can bypass; else the driver that said no and why.</summary>
    internal static Blocked? ParseBypass(ReadOnlySpan<byte> output)
    {
        if (output.Length < 352 || BinaryPrimitives.ReadUInt32LittleEndian(output[24..]) == 0) return null;
        static string Text(ReadOnlySpan<byte> output, int lengthAt, int at, int max)
        {
            var chars = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(output[lengthAt..]), max);
            return Encoding.Unicode.GetString(output.Slice(at, chars * 2)).TrimEnd('\0').Trim();
        }
        return new Blocked(Text(output, 28, 30, 32), Text(output, 94, 96, 128));
    }

    /// <summary>
    /// A SATA drive's SMART attributes (the 512 bytes STORAGE_PREDICT_FAILURE returns: a revision word, then 30 entries of
    /// 12 bytes: id, flags, value, worst, a 6-byte raw count). Life used from the wear attributes' normalized value
    /// (231 SSD life left, 233 media wearout, 177 Samsung's wear leveling), bad sectors from reallocated (5), pending (197)
    /// and uncorrectable (198), unsafe shutdowns from 174 (an SSD's unexpected power loss) or 192 (a hard drive's emergency retracts).
    /// </summary>
    internal static (Health Health, int? TemperatureC)? ParseAtaSmart(ReadOnlySpan<byte> data, bool failing)
    {
        if (data.Length < 364) return null;
        var raw = new Dictionary<byte, long>();
        var value = new Dictionary<byte, int>();
        for (var at = 2; at + 12 <= 362; at += 12)
        {
            var id = data[at];
            if (id == 0) continue;
            value[id] = data[at + 3];
            raw[id] = (long)(BinaryPrimitives.ReadUInt64LittleEndian(data[(at + 5)..]) & 0xFFFF_FFFF_FFFF);
        }
        if (raw.Count == 0) return null;
        long? Raw(byte id) => raw.TryGetValue(id, out var v) ? v : null;
        int? used = null;
        foreach (byte id in (byte[])[231, 233, 177])
            if (value.TryGetValue(id, out var left) && left is > 0 and <= 100) { used = 100 - left; break; }
        long? bad = Raw(5) is null && Raw(197) is null && Raw(198) is null ? null : (Raw(5) ?? 0) + (Raw(197) ?? 0) + (Raw(198) ?? 0);
        int? temperature = Raw(194) is { } t && (t & 0xFF) is > 0 and < 100 ? (int)(t & 0xFF) : null;
        return (new Health(LifeUsed: used, Hours: Raw(9) is { } h ? h & 0xFFFF_FFFF : null, PowerCycles: Raw(12), UnsafeShutdowns: Raw(174) ?? Raw(192),
            Failing: failing, BadSectors: bad), temperature);
    }

    /// <summary>A STORAGE_DEVICE_DESCRIPTOR's product revision (at 20): the firmware version, on every bus.</summary>
    internal static string? ParseRevision(byte[]? d)
    {
        if (d is null || d.Length < 24) return null;
        var offset = BitConverter.ToInt32(d, 20);
        if (offset <= 0 || offset >= d.Length) return null;
        var end = Array.IndexOf(d, (byte)0, offset);
        var text = Encoding.ASCII.GetString(d, offset, (end < 0 ? d.Length : end) - offset).Trim();
        return text.Length > 0 ? text : null;
    }

    internal static Bus BusOf(int bus) => bus switch { 0x11 => Bus.Nvme, 0x0B or 0x03 => Bus.Sata, 0x07 => Bus.Usb, 0x08 => Bus.Raid, _ => Bus.Other };

    // ---- Verdicts ----

    /// <summary>"PCIe 4.0", from the link generation.</summary>
    internal static string Pcie(int gen) => $"PCIe {gen}.0";

    /// <summary>
    /// The lines for what is wrong with one drive, worst first; nothing for what is fine. <paramref name="slowSeen"/>: a game's
    /// session never saw the link at its full speed (at rest a link may drop its speed to save power, so a slower one alone
    /// says nothing; fewer lanes always does).
    /// </summary>
    internal static IReadOnlyList<Verdict> Verdicts(Disk d, IReadOnlyList<Game> games, bool slowSeen)
    {
        var list = new List<Verdict>();
        if (d.Health is { } h)
        {
            if ((h.CriticalWarning & 0x08) != 0) list.Add(new("readonly", "It has gone read-only. Copy your files off it now.", true));
            else if ((h.CriticalWarning & 0x04) != 0 || h.Failing == true) list.Add(new("failing", "It is failing. Copy your files off it now.", true));
            else if ((h.CriticalWarning & 0x01) != 0 || h.Spare < h.SpareThreshold) list.Add(new("spare", "It may fail soon. Copy your files off it.", true));
            if (h.MediaErrors > 0) list.Add(new("media", $"{Count(h.MediaErrors.Value, "read error", "read errors")}. Copy your files off it.", true));
            if (h.BadSectors > 0) list.Add(new("sectors", $"{Count(h.BadSectors.Value, "bad sector", "bad sectors")}. Copy your files off it.", true));
            if (h.LifeUsed >= 90) list.Add(new("worn", h.LifeUsed >= 100 ? "Worn out. Replace it." : "Nearly worn out. Plan to replace it.", h.LifeUsed >= 100));
        }
        if (d.Temperature is { WarnC: { } warn } t && t.NowC >= warn)
            list.Add(new("hot", "Running hot. Give it airflow or a heatsink.", true));
        if (d.Link is { } link)
        {
            if (link.Width < link.MaxWidth)
                list.Add(new("lanes", "Its M.2 slot is slower than the drive.", true));
            else if (link.Gen < link.MaxGen && slowSeen)
                list.Add(new("speed", "Its M.2 slot or a BIOS setting holds it back.", false));
        }
        if (KnownBad(d.Model, d.Firmware) is { } bad) list.Add(new("firmware", bad, true));
        else if (NewerFirmware(d.Model, d.Firmware) is not null && Vendor(d.Model) is { } maker)
            list.Add(new("firmware", $"{maker.Name} has newer firmware.", false));
        if (CacheFixable(d))
            list.Add(new("cache", "Write caching is off.", false));
        if (d.Kind is "NVMe SSD" or "SSD" && d.Volumes.Where(v => v.Total > 0).MinBy(v => (double)v.Free / v.Total) is { } full
            && full.Free < full.Total * LowFreeFor(d))
            list.Add(new("full", $"{full.Letter} is nearly full, which slows it down.", false));
        if (d.Cache is { FlushOff: true })
            list.Add(new("flush", "A power cut can damage files: write-cache flushing is off.", false));
        if (d.Bus is Bus.Nvme or Bus.Raid && DriverName(d.Driver) is { } driver)
            list.Add(new("driver", $"It runs on {driver}, not Windows' own NVMe driver.", false));
        if (d.Kind == "Hard drive" && games.Count > 0)
            list.Add(new("hdd", games.Count == 1
                ? $"{games[0].Title} loads slowly from this hard drive."
                : $"{Count(games.Count, "game", "games")} load slowly from this hard drive.", false));
        foreach (var game in games.Where(g => g is { DirectStorage: true, Blocked: not null }))
            list.Add(new("bypass:" + game.Id, $"{game.Title} can't load through DirectStorage's fast path: {Words(game.Blocked!)}.", false));
        return list;
    }

    /// <summary>
    /// Why a file is off the fast path, in plain words: the reasons Windows gives (its own words, English on every Windows)
    /// and the filter drivers known to block it (EasyAntiCheat's EOSSys, Box Drive's cbfsconnect: reported in the wild).
    /// </summary>
    internal static string Words(Blocked b)
    {
        if (b.Reason.Contains("sparse", StringComparison.OrdinalIgnoreCase)) return "its files are sparse";
        if (b.Driver.Contains("wof", StringComparison.OrdinalIgnoreCase)) return "Windows compressed its files (CompactOS or a tool like CompactGUI)";
        if (b.Reason.Contains("compress", StringComparison.OrdinalIgnoreCase)) return "Windows compressed its files";
        if (b.Reason.Contains("encrypt", StringComparison.OrdinalIgnoreCase)) return "its files are encrypted";
        if (b.Driver.Contains("eossys", StringComparison.OrdinalIgnoreCase)) return "EasyAntiCheat's driver doesn't support it";
        if (b.Driver.Contains("cbfsconnect", StringComparison.OrdinalIgnoreCase)) return "Box Drive's driver doesn't support it";
        var driver = b.Driver.Length > 0 ? b.Driver : "a driver";
        return b.Reason.Length > 0 ? $"{driver} says “{b.Reason.TrimEnd('.')}”" : $"{driver} doesn't support it";
    }

    /// <summary>
    /// The storage driver in plain words when it isn't Windows' own (StorNVMe, or its native NVMe driver nvmedisk):
    /// Samsung's NVMe driver, Intel RST or VMD, AMD RAID, else the service's own name. Null for Windows' drivers.
    /// </summary>
    internal static string? DriverName(string? service) => service?.Trim().ToLowerInvariant() switch
    {
        null or "" or "stornvme" or "nvmedisk" or "storahci" or "disk" or "uaspstor" or "usbstor" => null,
        "secnvme" => "Samsung's NVMe driver",
        var s when s.StartsWith("iastor", StringComparison.Ordinal) => "Intel RST",
        "rcraid" or "rcbottom" => "AMD RAID",
        var s => $"the {s} driver",
    };

    /// <summary>The storage driver as the drive row shows it, Windows' own included (Exo never switches it); null when unread.</summary>
    internal static string? DriverLabel(string? service) => service?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "stornvme" => "Microsoft NVMe (StorNVMe)",
        "nvmedisk" => "Microsoft native NVMe",
        "storahci" => "Microsoft AHCI",
        "uaspstor" => "Microsoft USB (UASP)",
        "usbstor" => "Microsoft USB storage",
        var s => DriverName(s) ?? s,
    };

    /// <summary>
    /// Full power, what Samsung Magician's Full Power Mode and WD Dashboard's Gaming Mode set inside the drive: under
    /// Windows' StorNVMe the power plan decides instead. StorNVMe moves an idle drive only into a state whose latency fits
    /// the plan's NVMe latency tolerances, after its idle timeouts (Microsoft, power management for NVMe), so both
    /// tolerances at 0, or both timeouts at 0, keep it in PS0. On AC; null when the plan doesn't say.
    /// </summary>
    internal static bool? FullPower(uint? primaryTolerance, uint? secondaryTolerance, uint? primaryIdle, uint? secondaryIdle) =>
        primaryTolerance is null && secondaryTolerance is null && primaryIdle is null && secondaryIdle is null ? null
        : primaryTolerance == 0 && secondaryTolerance == 0 || primaryIdle == 0 && secondaryIdle == 0;

    /// <summary>The plan's NVMe power: full power on AC (<see cref="FullPower"/>), the plan's name, whether it's Exo's, on battery now.</summary>
    internal sealed record PlanPower(bool Full, string? Plan, bool Exo, bool Battery);

    private static string Count(long n, string one, string many) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";

    // ---- Firmware ----

    /// <summary>
    /// QLC drives by model, from the maker's own naming or spec: Samsung 860/870 QVO ("4bit MLC" V-NAND), Intel 660p/665p
    /// (SSDPEKNW…8/9) and 670p (SSDPEKNU…Z), Solidigm P41 Plus (SSDPFKNU…Z): "QLC 3D NAND" on Intel ARK and Solidigm's pages;
    /// Crucial P1 (CT…P1SSD8), sold as Crucial's QLC drive. Not shipped, unverified from the maker: Crucial P3 / P3 Plus,
    /// Kingston NV2/NV3 and WD Blue SN5000 (their NAND varies by batch or isn't on the maker's page).
    /// </summary>
    internal static bool Qlc(string? model) => model is not null && QlcModel().IsMatch(model);

    /// <summary>
    /// The free share under which a drive slows down, by what it is. A QLC drive writes at SLC speed only while about a
    /// quarter is free (Intel on the 660p: SLC speed "until the drive is ~75% full"), so 25%; a drive without DRAM (an
    /// NVMe drive that asks for host memory) leans on the same shrinking cache and its mapping table in your RAM, so 15%
    /// (between the two: no maker publishes a number); a TLC drive with its own DRAM 10% (Samsung's 840 white paper sets
    /// aside 6.7-10%); anything else, a SATA SSD or a hard drive whose make Exo can't tell, <see cref="LowFree"/>.
    /// </summary>
    internal static double LowFreeFor(Disk d) =>
        Qlc(d.Model) ? 0.25
        : d.Nvme is { HmbBytes: > 0 } ? 0.15
        : d.Nvme is not null && d.Kind == "NVMe SSD" ? 0.10
        : LowFree;

    /// <summary>A plain name for what the drive is built from, where it's known: "QLC" or "no DRAM", else null.</summary>
    internal static string? Build(Disk d) => Qlc(d.Model) ? "QLC" : d.Nvme is { HmbBytes: > 0 } ? "no DRAM" : null;

    /// <summary>
    /// The newest firmware a maker lists, where its page says so publicly. Samsung's consumer tools page
    /// (semiconductor.samsung.com/consumer-storage/support/tools/) listed 8B2QJXD7 for the 990 PRO (1, 2 TB) on 2026-10-28.
    /// </summary>
    private static readonly (Regex Model, string Latest)[] LatestFirmware = [(NinetyNinetyPro(), "8B2QJXD7")];

    /// <summary>
    /// The line for firmware with a known fault on this Windows (<paramref name="build"/>: its build, this PC's when null),
    /// from the drivers' advisories (<see cref="DriverAdvisories"/>, kind ssd: Samsung's 990 PRO 0B2QJXD7 and 980 PRO
    /// 3B2QGXA7, WD's 2TB SN770, SN580 and SN5000 before their host memory buffer fix on 24H2 and later), naming the version
    /// to update to and the maker's tool that installs it (Exo never flashes), or null.
    /// </summary>
    internal static string? KnownBad(string? model, string? firmware, int? build = null)
    {
        if (model is null || firmware is null) return null;
        var now = firmware.Trim();
        return DriverAdvisories.Shared.Against("ssd:" + model.Trim(), now, new([], build ?? Environment.OSVersion.Version.Build)) is { } advisory
            ? $"Its firmware, {now}, has a known fault: {advisory.Why}. Update it to {advisory.To} or newer with {Vendor(model)?.Tool ?? "its maker's tool"}."
            : null;
    }

    /// <summary>
    /// The maker's newest firmware when this drive runs an older one of the same line (Samsung numbers a line by its first
    /// character: 4B2QJXD7 before 8B2QJXD7), else null. A version of another line, or one newer than the list, says nothing.
    /// </summary>
    internal static string? NewerFirmware(string? model, string? firmware)
    {
        if (model is null || firmware is not { Length: > 1 }) return null;
        foreach (var (pattern, latest) in LatestFirmware)
        {
            if (!pattern.IsMatch(model)) continue;
            var now = firmware.Trim().ToUpperInvariant();
            return now.Length == latest.Length && now[1..] == latest[1..] && char.IsAsciiDigit(now[0]) && now[0] < latest[0] ? latest : null;
        }
        return null;
    }

    /// <summary>The maker of a drive by its model, its firmware tool, and the official page that has it (opened in the browser; Exo never flashes).</summary>
    internal static (string Name, string Tool, string Url)? Vendor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var m = model.Trim();
        bool Starts(params string[] prefixes) => prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (Starts("Samsung")) return ("Samsung", "Samsung Magician", "https://semiconductor.samsung.com/consumer-storage/support/tools/");
        if (Starts("WD", "WDC", "SanDisk", "Western Digital")) return ("SanDisk", "SanDisk Dashboard", "https://www.sandisk.com/en-us/topics/upgrades/drive-monitoring-sandisk-dashboard");
        if (Starts("Crucial", "CT", "Micron")) return ("Crucial", "Storage Executive", "https://www.crucial.com/support/storage-executive");
        if (Starts("Kingston", "KINGSTON", "SNV", "SKC", "SFYR", "SA400", "SA2000")) return ("Kingston", "Kingston SSD Manager", "https://www.kingston.com/en/support/technical/ksm-firmware-update");
        if (Starts("Solidigm", "INTEL SSDPE", "SSDPE")) return ("Solidigm", "Solidigm Synergy", "https://www.solidigm.com/support-page/drivers-downloads/ka-01737.html");
        if (Starts("Corsair", "Force MP")) return ("Corsair", "Corsair SSD Toolbox", "https://help.corsair.com/hc/en-us/articles/15292042915213-SSD-How-to-Update-your-SSD-Firmware");
        if (Starts("Seagate", "ST")) return ("Seagate", "SeaTools", "https://www.seagate.com/support/downloads/seatools/");
        return null;
    }

    // 990 PRO and 990 PRO with Heatsink at 1 and 2 TB (the 4 TB runs its own firmware line).
    [GeneratedRegex(@"Samsung SSD 990 PRO\b(?!.*4TB)", RegexOptions.IgnoreCase)]
    private static partial Regex NinetyNinetyPro();

    [GeneratedRegex(@"Samsung SSD 8[67]0 QVO\b|\bSSDPEKNW\d{3}[GT][89]\b|\bSSDP[EF]KNU\d{3}[GT]Z\b|\bCT\d{3,4}P1SSD8\b", RegexOptions.IgnoreCase)]
    private static partial Regex QlcModel();

    // ---- Fixes ----

    /// <summary>
    /// A row Exo can fix with Windows' permission. Cache: write caching on for every drive inside the PC that has it off;
    /// flush: write-cache flushing back on for every drive that has it off (a restart finishes either); optimize: Windows'
    /// weekly optimize able to run (its task on, Automatic Maintenance on).
    /// Applied: Exo set it; Restart: set, waiting for a restart.
    /// </summary>
    internal sealed record Fix(string Key, string Label, string Detail, bool Applied = false, bool Restart = false);

    /// <summary>
    /// Windows' scheduled optimize: its task turned off, Automatic Maintenance (which runs it) turned off, or its service
    /// disabled, which optimizer tools also do: the task then reads as ready and Optimize Drives won't open.
    /// </summary>
    internal sealed record Optimize(bool TaskOff, bool MaintenanceOff, bool ServiceOff = false)
    {
        internal bool Off => TaskOff || MaintenanceOff || ServiceOff;
    }

    /// <summary>
    /// What Exo changed: each drive's CacheIsPowerProtected as it was, the task's, Automatic Maintenance's and the
    /// service's state, and each drive's UserWriteCacheSetting as it was (null: it wasn't set).
    /// </summary>
    internal sealed record Backup(List<FlushChange> Flush, OptimizeChange? Optimize, List<CacheChange>? Cache = null);

    internal sealed record FlushChange(string InstanceId, int Was);

    internal sealed record CacheChange(string InstanceId, int? Was);

    internal sealed record OptimizeChange(bool TaskWasOff, bool MaintenanceWasOff, bool ServiceWasOff = false);

    /// <summary>Drives whose flushing Exo would turn back on: flushing off, by Windows' own setting, on a drive Windows names.</summary>
    internal static IReadOnlyList<Disk> FlushPending(IReadOnlyList<Disk> disks) =>
        disks.Where(d => d.Cache is { FlushOff: true } && d.FlushSetting is not 0 && d.InstanceId is { } id && DiskId().IsMatch(id)).ToList();

    /// <summary>
    /// Write caching off on a drive inside the PC. A USB drive's is Windows' Quick removal, right for a drive that's
    /// unplugged without ejecting, so it's left alone.
    /// </summary>
    private static bool CacheFixable(Disk d) => d.Cache is { Off: true } && d.Bus is not (Bus.Usb or Bus.Raid);

    /// <summary>Drives whose write caching Exo would turn on: off, not yet set on by Windows' setting (UserWriteCacheSetting 1), on a drive Windows names.</summary>
    internal static IReadOnlyList<Disk> CachePending(IReadOnlyList<Disk> disks) =>
        disks.Where(d => CacheFixable(d) && d.CacheSetting is not 1 && d.InstanceId is { } id && DiskId().IsMatch(id)).ToList();

    /// <summary>The fix rows, from what was read and what Exo changed.</summary>
    internal static IReadOnlyList<Fix> Fixes(IReadOnlyList<Disk> disks, Optimize? optimize, Backup? backup)
    {
        var list = new List<Fix>();
        var cachePending = CachePending(disks);
        var cacheMine = backup?.Cache ?? [];
        if (cachePending.Count > 0)
            list.Add(new("cache", "Write caching", $"Off on {(cachePending.Count == 1 ? cachePending[0].Model ?? "your drive" : $"{cachePending.Count} drives")}"));
        else if (cacheMine.Count > 0 && disks.Any(d => cacheMine.Any(c => c.InstanceId == d.InstanceId)))
        {
            var waiting = disks.Any(d => d.Cache is { Off: true } && d.CacheSetting == 1 && cacheMine.Any(c => c.InstanceId == d.InstanceId));
            list.Add(new("cache", "Write caching", waiting ? "On after a restart" : "On", Applied: true, Restart: waiting));
        }
        var pending = FlushPending(disks);
        var mine = backup?.Flush ?? [];
        if (pending.Count > 0)
        {
            var named = pending.Count == 1 ? pending[0].Model ?? "your drive" : $"{pending.Count} drives";
            var shutdowns = pending.Sum(d => d.Health?.UnsafeShutdowns ?? 0);
            list.Add(new("flush", "Write-cache flushing", shutdowns > 0
                ? $"Off on {named} · shut down without warning {Count(shutdowns, "time", "times")}"
                : $"Off on {named}"));
        }
        else if (mine.Count > 0 && disks.Any(d => mine.Any(c => c.InstanceId == d.InstanceId)))
        {
            var waiting = disks.Any(d => d.Cache is { FlushOff: true } && d.FlushSetting == 0 && mine.Any(c => c.InstanceId == d.InstanceId));
            list.Add(new("flush", "Write-cache flushing", waiting ? "On after a restart" : "On", Applied: true, Restart: waiting));
        }
        if (optimize is { Off: true })
            list.Add(new("optimize", "Weekly optimize", optimize.ServiceOff
                ? "Off: Windows' optimize service is disabled"
                : optimize.MaintenanceOff
                    ? "Off: Windows' Automatic Maintenance is off"
                    : "Off: Windows' scheduled optimize is off"));
        else if (optimize is not null && backup?.Optimize is not null)
            list.Add(new("optimize", "Weekly optimize", "On", Applied: true));
        return list;
    }

    internal const string DefragTask = @"\Microsoft\Windows\Defrag\ScheduledDefrag";
    internal const string MaintenanceKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
    internal const string DefragService = "defragsvc";

    /// <summary>
    /// The elevated lines that turn flushing on for <paramref name="flush"/> drives (CacheIsPowerProtected 0), set write caching (<paramref name="cache"/>: UserWriteCacheSetting, the value behind Device Manager's
    /// "Enable write caching on the device"; null deletes it, as it was), and turn optimize on (the service back to Manual, Windows' own default, through sc.exe since the service manager only
    /// reads the registry at boot). Only device ids
    /// Windows names (checked by pattern, quoted) reach reg.exe; null when one isn't.
    /// </summary>
    internal static IReadOnlyList<string>? Commands(string reg, string schtasks, IEnumerable<(string Id, int Value)> flush, OptimizeChange? optimizeOn,
        IEnumerable<(string Id, int? Value)>? cache = null)
    {
        var lines = new List<string>();
        foreach (var (id, value) in flush)
        {
            if (!DiskId().IsMatch(id) || value is < 0 or > 1) return null;
            lines.Add($"\"{reg}\" add \"HKLM\\SYSTEM\\CurrentControlSet\\Enum\\{id}\\Device Parameters\\Disk\" /v CacheIsPowerProtected /t REG_DWORD /d {value} /f /reg:64");
        }
        foreach (var (id, value) in cache ?? [])
        {
            if (!DiskId().IsMatch(id) || value is < 0 or > 1) return null;
            var key = $"\"HKLM\\SYSTEM\\CurrentControlSet\\Enum\\{id}\\Device Parameters\\Disk\" /v UserWriteCacheSetting";
            lines.Add(value is { } v ? $"\"{reg}\" add {key} /t REG_DWORD /d {v} /f /reg:64" : $"\"{reg}\" delete {key} /f /reg:64");
        }
        if (optimizeOn is { } on)
        {
            if (on.TaskWasOff) lines.Add($"\"{schtasks}\" /Change /TN \"{DefragTask}\" /ENABLE");
            if (on.MaintenanceWasOff) lines.Add($"\"{reg}\" delete \"HKLM\\{MaintenanceKey}\" /v MaintenanceDisabled /f /reg:64");
            if (on.ServiceWasOff) lines.Add($"\"{Path.Combine(Path.GetDirectoryName(schtasks) ?? "", "sc.exe")}\" config {DefragService} start= demand");
        }
        return lines;
    }

    /// <summary>A disk's device instance as Windows names it ("SCSI\DISK&amp;VEN_NVME&amp;PROD_SAMSUNG_SSD_990\5&amp;33C33320&amp;0&amp;000000").</summary>
    [GeneratedRegex(@"^(?:SCSI|IDE|NVME|STORAGE|USBSTOR)\\[A-Z0-9_&.\-]{1,120}\\[A-Z0-9_&.\-]{1,80}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex DiskId();

    // ---- Install-time advice ----

    /// <summary>Under this share free, an SSD slows down (its fast cache shrinks) and store updates run out of room to patch.</summary>
    internal const double LowFree = 0.12;

    /// <summary>Whether putting <paramref name="size"/> bytes on a volume leaves it under <paramref name="low"/> free (or doesn't fit).</summary>
    internal static bool LeavesLow(long free, long total, long size, double low = LowFree) => total > 0 && size > 0 && free - size < total * low;

    /// <summary>
    /// The best other volume for an install: on an SSD (NVMe first), never a hard drive, USB drive or the volume itself,
    /// staying above its drive's own headroom (<see cref="LowFreeFor"/>) after it, the most room left first. Null when none.
    /// </summary>
    internal static (Volume Volume, Disk Disk)? BestFor(IReadOnlyList<Disk> disks, long size, string notLetter) =>
        disks.Where(d => d.Kind is "NVMe SSD" or "SSD")
            .SelectMany(d => d.Volumes.Select(v => (Volume: v, Disk: d)))
            .Where(p => !p.Volume.Letter.Equals(notLetter, StringComparison.OrdinalIgnoreCase) && !LeavesLow(p.Volume.Free, p.Volume.Total, size, LowFreeFor(p.Disk)))
            .OrderByDescending(p => p.Disk.Bus == Bus.Nvme).ThenByDescending(p => p.Volume.Free - size)
            .Select(p => ((Volume, Disk)?)p).FirstOrDefault();

    // ---- A game's session ----

    /// <summary>A game session's drive temperature worth saying: its peak, the drive's warning point, and how long the drive slowed itself.</summary>
    internal sealed record Heat(int PeakC, int? WarnC, long SlowedSeconds, DateTimeOffset At);

    /// <summary>
    /// Worth saying when the drive slowed itself (its thermal management counted time or a transition during the session)
    /// or came within 5 °C of its warning point; else null.
    /// </summary>
    internal static Heat? HeatWorthSaying(int peak, int? warn, long slowedSeconds, long slowedEvents, DateTimeOffset at) =>
        slowedSeconds > 0 || slowedEvents > 0 || warn is { } w && peak >= w - 5 ? new Heat(peak, warn, Math.Max(0, slowedSeconds), at) : null;
}
