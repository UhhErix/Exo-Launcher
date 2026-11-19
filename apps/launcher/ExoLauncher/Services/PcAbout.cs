using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Overview's About card: what this PC is, from Windows' own records, without WMI or administrator rights; the one
/// network read is Microsoft's Windows release information page, for whether Windows is on the newest version. The model (or, on a PC built from parts, the name Windows gives it), the processor's cores and the memory's
/// type, size and slots from SMBIOS (the table <see cref="PcHealth"/> reads), the motherboard, BIOS and Windows from the
/// registry, Secure Boot, the TPM and Memory integrity, and the connection in use. What can't change while Exo runs is
/// read once; the connection each time. A part that can't be read is null, never a guess. The drives are PC › Drives'
/// (<see cref="Drives"/>), which asks each disk through the no-access handle and descriptor parsing here.
/// </summary>
internal static partial class PcAbout
{
    /// <summary>The fitted memory: total, type (DDR4, DDR5…), the speed it runs at and the kit's rating (MT/s), slots used and slots.</summary>
    internal sealed record Memory(int Gb, string? Type, int? Speed, int? Rated, int Used, int Slots);

    internal sealed record About(
        string? Name, string? Processor, int? Cores, int Threads,
        string? Graphics, double? VideoMemoryGb, string DirectX, bool? ResizableBar,
        int? MemoryGb, string? MemoryType, int? MemorySpeed, int? MemoryRated, int? MemorySlotsUsed, int? MemorySlots,
        string? Motherboard, string? Bios, string? BiosDate, string? Windows, string? WindowsVersion, string? Build,
        bool? SecureBoot, string? Tpm, bool? MemoryIntegrity,
        string? Network = null, double? NetworkMbps = null, WindowsStatus? WindowsRelease = null, string? NetworkAdapter = null);

    /// <summary>
    /// Windows on this PC against the newest feature version Microsoft lists for everyone: "Windows 11", "26H2",
    /// "26300.9711", newest "26H2" at build 26300. Only the feature version (the major build) counts, never a monthly
    /// update. Newest, NewestBuild and UpToDate are null while Microsoft's page can't be read. Insider: Windows Insider
    /// names a channel, or the build is above every one Microsoft lists.
    /// </summary>
    internal sealed record WindowsStatus(string Name, string? Version, string Build, string? Newest, int? NewestBuild, bool? UpToDate, bool Insider);

    /// <summary>The newest feature version for existing PCs on Microsoft's page and its major build, and the highest build any row lists.</summary>
    internal sealed record NewestWindows(string Version, int Build, int Highest);

    /// <summary>This PC's Windows from the registry: the product name, its feature version, major build and update revision, and whether Windows Insider names a channel.</summary>
    private sealed record ThisWindows(string? Product, string? Version, int Build, int? Revision, bool Insider)
    {
        internal string FullBuild => Revision is { } revision ? $"{Build}.{revision}" : Build.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly Lazy<About> Once = new(ReadOnce, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ThisWindows> Installed = new(ReadWindows, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The motherboard's maker and product and its BIOS's version and release date (yyyy-MM-dd), as the registry has them.</summary>
    internal sealed record Firmware(string? Maker, string? Product, string? Version, string? Date);

    private static readonly Lazy<Firmware> Board = new(ReadFirmware, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This PC's board and BIOS, read once (<see cref="BiosUpdate"/> looks up the maker's newest for it).</summary>
    internal static Firmware Bios => Board.Value;

    internal static About Read()
    {
        var network = Connection();
        return Once.Value with { Network = network?.Kind, NetworkMbps = network?.Mbps, NetworkAdapter = network?.Adapter };
    }

    /// <summary>The first look at Microsoft's page waits this long; past it the card shows without the newest version, and the look goes on for the next showing.</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(5);

    private static readonly LatestCache<NewestWindows> Releases = new("Microsoft's Windows release information", TimeSpan.FromDays(1), "windows-release.json");
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(30));

    /// <summary><see cref="Read"/> plus Windows against Microsoft's newest release (its page read at most once a day, kept on disk; an older answer shows while it is read again).</summary>
    internal static async Task<About> ReadAsync(CancellationToken ct)
    {
        var about = await Task.Run(Read, ct).ConfigureAwait(false);
        var windows = Installed.Value;
        var page = ReleasePage(windows.Build);
        NewestWindows? newest = null;
        try { newest = await Releases.GetAsync(page, token => NewestAsync(page, token), ct).WaitAsync(FirstLook, ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* still reading: kept for the next showing */ }
        return about with { WindowsRelease = Status(windows.Build, windows.FullBuild, windows.Version, windows.Insider, newest) };
    }

    /// <summary>Microsoft's release information page for this PC's Windows: Windows 11's, or Windows 10's below build 22000.</summary>
    internal static string ReleasePage(int build) => build >= 22000
        ? "https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information"
        : "https://learn.microsoft.com/en-us/windows/release-health/release-information";

    private static async Task<NewestWindows?> NewestAsync(string page, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, page);
        request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0 (+https://getexo.app)");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        Releases.SlowDown(response);
        response.EnsureSuccessStatusCode();
        // A page that lists no version is a failure, not an answer: the last one read stays, and it is read again soon.
        return ParseReleases(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            ?? throw new FormatException("its current versions table lists no General Availability version");
    }

    /// <summary>
    /// Windows as the card shows it: its name ("Windows 11" from build 22000), feature version and full build, against
    /// <paramref name="newest"/>. Up to date when the major build is at least the newest's, whatever monthly update either
    /// has; unknown (null) without an answer from Microsoft. Insider when Windows Insider names a channel or the build is
    /// above every build Microsoft lists.
    /// </summary>
    internal static WindowsStatus Status(int build, string fullBuild, string? version, bool insiderChannel, NewestWindows? newest) => new(
        build >= 22000 ? "Windows 11" : "Windows 10", version, fullBuild, newest?.Version, newest?.Build,
        newest is null ? null : build >= newest.Build, insiderChannel || build > newest?.Highest);

    /// <summary>
    /// The newest feature version on Microsoft's release information page. Its current-versions tables (columns Version,
    /// Servicing option, Availability date, Latest build; the release history's tables below them are not read) give
    /// every General Availability Channel row; the newest is the one that came out last. The page says Windows 11's
    /// feature updates come in the second half of the year, and a first-half version (26H1) is only for new devices, so
    /// when any second-half version is listed only those count. Highest is the top major build of every row, LTSC and
    /// new-device versions included. Null when no table or row reads.
    /// </summary>
    internal static NewestWindows? ParseReleases(string html)
    {
        var history = html.IndexOf("release-history\"", Math.Max(0, html.IndexOf("<table", StringComparison.OrdinalIgnoreCase)), StringComparison.Ordinal);
        var rows = new List<(string Version, bool General, DateOnly Available, int Build)>();
        foreach (Match table in Table().Matches(history > 0 ? html[..history] : html))
        {
            var heads = HeadCell().Matches(table.Value).Select(m => CellText(m.Groups[1].Value)).ToList();
            int version = heads.IndexOf("Version"), servicing = heads.IndexOf("Servicing option"), available = heads.IndexOf("Availability date"), latest = heads.IndexOf("Latest build");
            if (version < 0 || servicing < 0 || latest < 0) continue;
            foreach (Match row in TableRow().Matches(table.Value))
            {
                var cells = DataCell().Matches(row.Groups[1].Value).Select(m => CellText(m.Groups[1].Value)).ToList();
                if (cells.Count <= Math.Max(Math.Max(version, servicing), Math.Max(available, latest)) || cells[version].Length == 0
                    || !int.TryParse(cells[latest].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var build)) continue;
                var date = available >= 0 && DateOnly.TryParseExact(cells[available], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : DateOnly.MinValue;
                rows.Add((cells[version], cells[servicing].Contains("General Availability", StringComparison.OrdinalIgnoreCase), date, build));
            }
        }
        var general = rows.Where(r => r.General).ToList();
        if (general.Any(r => r.Version.EndsWith("H2", StringComparison.OrdinalIgnoreCase))) general.RemoveAll(r => !r.Version.EndsWith("H2", StringComparison.OrdinalIgnoreCase));
        if (general.Count == 0) return null;
        var newest = general.OrderByDescending(r => r.Available).ThenByDescending(r => r.Build).First();
        return new NewestWindows(newest.Version, newest.Build, rows.Max(r => r.Build));
    }

    /// <summary>A table cell's text: footnote marks (<c>&lt;sup&gt;</c>) and tags out, entities decoded, spaces collapsed.</summary>
    private static string CellText(string html) =>
        Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(Footnote().Replace(html, ""), "")), " ").Trim();

    /// <summary>The Windows build and update revision as one string ("26300.9711", as Settings › About shows them); null when the revision can't be read.</summary>
    internal static string? BuildAndRevision() => Installed.Value is { Revision: not null } windows ? windows.FullBuild : null;

    private static ThisWindows ReadWindows()
    {
        string? product = null, version = null;
        var build = Environment.OSVersion.Version.Build;
        int? revision = null;
        var insider = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (int.TryParse(Text(key, "CurrentBuildNumber"), out var n)) build = n;
            product = Text(key, "ProductName");
            version = Text(key, "DisplayVersion");
            revision = key?.GetValue("UBR") is int ubr ? ubr : null;
            using var flight = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
            insider = Text(flight, "BranchName") is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left out */ }
        return new ThisWindows(product, version, build, revision, insider);
    }

    private static About ReadOnce()
    {
        var specs = SystemSpecs.Read();
        var raw = PcHealth.SmbiosRaw();
        var memory = ParseMemory(raw);
        var firmware = Board.Value;
        string? model = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            model = Model(Text(key, "SystemManufacturer"), Text(key, "SystemProductName"), Text(key, "SystemVersion"), firmware.Product);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left out */ }
        var windows = Installed.Value;
        bool? secureBoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            secureBoot = key?.GetValue("UEFISecureBootEnabled") is int on ? on == 1 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left out */ }
        return new About(
            model ?? HostName(), specs.Cpu, ParseCores(raw), Environment.ProcessorCount,
            specs.Gpu, specs.VramGb, SystemSpecs.Direct3D12(0xc200) ? "12 Ultimate" : specs.DirectX == 12 ? "12" : "11", ResizableBar(PcHealth.MainGpu()),
            memory?.Gb ?? (specs.RamGb > 0 ? specs.RamGb : null), memory?.Type, memory?.Speed, memory?.Rated, memory?.Used, memory?.Slots,
            Named(firmware.Maker, firmware.Product), firmware.Version, firmware.Date, WindowsName(windows.Product, windows.Build), windows.Version, windows.FullBuild, secureBoot, Tpm(), ControllerLab.MemoryIntegrityOn());
    }

    private static Firmware ReadFirmware()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return new Firmware(Text(key, "BaseBoardManufacturer"), Text(key, "BaseBoardProduct"), Text(key, "BIOSVersion"), BiosDate(Text(key, "BIOSReleaseDate")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new Firmware(null, null, null, null); }
    }

    private static string? Text(RegistryKey? key, string name) => (key?.GetValue(name) as string)?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>The PC's own name, as Windows' About page shows it (case kept, unlike the NetBIOS name).</summary>
    private static string? HostName()
    {
        try { return Dns.GetHostName() is { Length: > 0 } name ? name : null; }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    // ---- SMBIOS ----

    /// <summary>
    /// The memory SMBIOS lists (type 17): every slot, and of the fitted ones the total, the type, the speed the slowest
    /// runs at and the kit's rating (the larger of the firmware's and the part number's, as <see cref="PcHealth.SlowRam"/>).
    /// Null when the table lists no fitted module.
    /// </summary>
    internal static Memory? ParseMemory(ReadOnlySpan<byte> raw)
    {
        int slots = 0, mb = 0;
        var types = new List<string?>();
        PcHealth.Walk(raw, (type, s, _) =>
        {
            if (type != 17 || s.Length < 0x13) return;
            slots++;
            // Size: 0 an empty slot, 0xFFFF unknown, 0x7FFF says to read the extended size (MB) at 0x1C; bit 15 counts in KB.
            var size = PcHealth.U16(s, 0x0C);
            if (size is 0 or 0xFFFF) return;
            mb += size == 0x7FFF ? (int)(PcHealth.U32(s, 0x1C) & 0x7FFFFFFF) : (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;
            types.Add(MemoryType(s[0x12]));
        });
        if (types.Count == 0) return null;
        var dimms = PcHealth.ParseDimms(raw).Where(d => d.Configured > 0).ToList();
        int? speed = dimms.Count > 0 ? dimms.Min(d => d.Configured) : null;
        int? rated = dimms.Count > 0 ? dimms.Min(d => Math.Max(d.Speed, PcHealth.RatedFromPart(d.Part) ?? 0)) : null;
        return new Memory((int)Math.Round(mb / 1024.0), types.Distinct().Count() == 1 ? types[0] : null, speed, rated > speed ? rated : speed, types.Count, slots);
    }

    /// <summary>SMBIOS's memory type code as people name it.</summary>
    internal static string? MemoryType(byte code) => code switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3",
        0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => null,
    };

    /// <summary>The processor's cores (SMBIOS type 4, populated sockets added up; 0xFF says to read the 3.0 word at 0x2A), or null.</summary>
    internal static int? ParseCores(ReadOnlySpan<byte> raw)
    {
        var cores = 0;
        PcHealth.Walk(raw, (type, s, _) =>
        {
            if (type != 4 || s.Length < 0x24 || (s[0x18] & 0x40) == 0) return;
            cores += s[0x23] == 0xFF ? PcHealth.U16(s, 0x2A) : s[0x23];
        });
        return cores > 0 ? cores : null;
    }

    // ---- Names ----

    // What board makers' firmware leaves in the system fields of a PC built from parts.
    private static readonly string[] Placeholders = ["System Product Name", "System Version", "System manufacturer", "To be filled by O.E.M.",
        "Default string", "Not Applicable", "None", "O.E.M.", "OEM", "Type1ProductConfigId", "All Series", "Standard PC", "Not Specified", "Undefined"];

    internal static bool Real([NotNullWhen(true)] string? value) =>
        value?.Trim() is { Length: > 0 } text && text.Any(char.IsLetterOrDigit) && !Placeholders.Any(p => text.Equals(p, StringComparison.OrdinalIgnoreCase));

    private static readonly (string Prefix, string Name)[] Makers =
    [
        ("ASUS", "ASUS"), ("Micro-Star", "MSI"), ("MSI", "MSI"), ("Gigabyte", "Gigabyte"), ("ASRock", "ASRock"), ("Biostar", "Biostar"),
        ("Dell", "Dell"), ("Alienware", "Alienware"), ("Lenovo", "Lenovo"), ("Hewlett", "HP"), ("HP", "HP"), ("Acer", "Acer"),
        ("Razer", "Razer"), ("Microsoft", "Microsoft"), ("Samsung", "Samsung"), ("Framework", "Framework"), ("Valve", "Valve"),
        ("Intel", "Intel"), ("NZXT", "NZXT"), ("EVGA", "EVGA"), ("Supermicro", "Supermicro"), ("LG Electronics", "LG"),
    ];

    /// <summary>A maker as people say it ("ASUSTeK COMPUTER INC." is ASUS, "Micro-Star International Co., Ltd." MSI), or null.</summary>
    internal static string? Maker(string? maker)
    {
        if (!Real(maker)) return null;
        var text = maker.Trim();
        foreach (var (prefix, name) in Makers)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;
        return CorporateTail().Replace(text, "").Trim() is { Length: > 0 } plain ? plain : text;
    }

    /// <summary>A maker and a product as one name, the maker once ("ASUS ROG STRIX B550-F GAMING WIFI II"), or null without a product.</summary>
    internal static string? Named(string? maker, string? product)
    {
        if (!Real(product)) return null;
        var name = product.Trim();
        return Maker(maker) is { } brand && !name.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? $"{brand} {name}" : name;
    }

    /// <summary>
    /// The PC's model as its maker names it ("ASUS ROG Zephyrus G14", "Lenovo Legion 5 15ACH6H"), or null on a PC built
    /// from parts: placeholders, or a product that is only the motherboard's (MSI's "MS-7C91", Gigabyte's board name).
    /// Lenovo keeps the machine type in the product ("82JQ") and the name in the version.
    /// </summary>
    internal static string? Model(string? maker, string? product, string? version, string? board)
    {
        var name = Maker(maker) == "Lenovo" && Real(version) ? version : product;
        if (!Real(name)) return null;
        if (Real(board) && board.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return Named(maker, name);
    }

    /// <summary>Windows' name: the registry still says "Windows 10" on Windows 11 (build 22000 and newer).</summary>
    internal static string? WindowsName(string? product, int build) =>
        string.IsNullOrWhiteSpace(product) ? null
        : build >= 22000 ? product.Trim().Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : product.Trim();

    /// <summary>The BIOS's release date ("01/04/2026" in the registry) as yyyy-MM-dd, or null.</summary>
    internal static string? BiosDate(string? text) =>
        DateTime.TryParseExact(text, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>Resizable BAR: on when the CPU sees all of the card's memory, off on a card that has it and doesn't; null on an integrated GPU, an older card or a Windows that can't say.</summary>
    internal static bool? ResizableBar(PcHealth.GpuBar? gpu) =>
        gpu is not { Uma: false } ? null : gpu.UploadHeap == true ? true : PcHealth.ReBarOff(gpu) ? false : null;

    // ---- Drives ----

    /// <summary>The number of the physical disk a volume ("C:") starts on (\\.\PhysicalDriveN), or null; PC › Drives reads the same disk.</summary>
    internal static int? DiskNumber(string letter)
    {
        using var volume = Open(@"\\.\" + letter.TrimEnd('\\'));
        var extents = new byte[8 + 24 * 4]; // VOLUME_DISK_EXTENTS: a count, then DISK_EXTENTs of 24 bytes
        return !volume.IsInvalid && DeviceIoControl(volume, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, null, 0, extents, extents.Length, out _, IntPtr.Zero)
            && BitConverter.ToInt32(extents, 0) >= 1 ? (int)BitConverter.ToUInt32(extents, 8) : null;
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR: a spinning disk pays to seek; null when the driver doesn't say.</summary>
    internal static bool? Seeks(SafeFileHandle disk) => Query(disk, 7 /* StorageDeviceSeekPenaltyProperty */, 12) is { } penalty ? penalty[8] != 0 : null;

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY's standard query of one property, or null when the driver doesn't answer it.</summary>
    internal static byte[]? Query(SafeFileHandle disk, int property, int size)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY: the property, PropertyStandardQuery (0), a spare byte
        BitConverter.GetBytes(property).CopyTo(query, 0);
        var answer = new byte[size];
        return DeviceIoControl(disk, 0x002D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) && returned >= 12 ? answer : null;
    }

    /// <summary>
    /// A STORAGE_DEVICE_DESCRIPTOR's model (its vendor before its product, unless the vendor is a bus's word or already in
    /// the product) and bus type (at 28).
    /// </summary>
    internal static (string? Model, int Bus) ParseDevice(byte[]? d)
    {
        if (d is null || d.Length < 36) return (null, 0);
        string? Field(int at)
        {
            var offset = BitConverter.ToInt32(d, at);
            if (offset <= 0 || offset >= d.Length) return null;
            var end = Array.IndexOf(d, (byte)0, offset);
            var text = Encoding.ASCII.GetString(d, offset, (end < 0 ? d.Length : end) - offset).Trim();
            return text.Length > 0 ? text : null;
        }
        var (vendor, product) = (Field(12), Field(16));
        var model = vendor is null or "ATA" or "NVMe" or "SCSI" || product?.Contains(vendor, StringComparison.OrdinalIgnoreCase) == true ? product : product is null ? null : $"{vendor} {product}";
        return (model, BitConverter.ToInt32(d, 28));
    }

    /// <summary>A disk's kind from its bus (STORAGE_BUS_TYPE) and, on SATA and the rest, whether it pays to seek.</summary>
    internal static string Kind(int bus, bool? seeks) => bus switch
    {
        0x11 => "NVMe SSD",
        0x07 => "USB drive",
        0x0C or 0x0D => "Memory card",
        0x08 => "RAID",
        0x0E or 0x0F => "Virtual disk",
        0x10 => "Storage Spaces",
        _ => seeks switch { true => "Hard drive", false => "SSD", null => "Drive" },
    };

    // ---- Security and connection ----

    /// <summary>The TPM's version ("2.0", "1.2"), "None" when the PC has none, null when Windows can't say.</summary>
    private static string? Tpm()
    {
        try
        {
            var result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
            if (result == 0x8028400F) return "None"; // TBS_E_TPM_NOT_FOUND
            return result != 0 ? null : info.TpmVersion switch { 1 => "1.2", 2 => "2.0", _ => null };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>The connection Windows routes through: an Ethernet or Wi-Fi adapter that's up with a gateway (Ethernet first, no virtual switches), its kind, link speed and the adapter's name ("Intel Ethernet Controller I226-V").</summary>
    private static (string Kind, double Mbps, string Adapter)? Connection()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Speed > 0
                    && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211
                    && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(n => ((string, double, string)?)(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet", n.Speed / 1_000_000.0,
                    n.Description.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Trim()))
                .FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    // ", Inc.", " Co., Ltd.", " Corporation", " Technology Co., Ltd." and the like.
    [GeneratedRegex(@"[,.]?\s+(?:Co\.?,?\s*)?(?:Ltd|Inc|Corporation|Corp|LLC|GmbH|Technology|Computer|International)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex CorporateTail();

    [GeneratedRegex(@"<table\b.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Table();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadCell();

    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DataCell();

    [GeneratedRegex(@"<sup\b[^>]*>.*?</sup>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Footnote();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>No access asked: these IOCTLs only describe the device.</summary>
    internal static SafeFileHandle Open(string path) => CreateFile(path, 0, 0x3 /* share read, write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
