using System.IO.Compression;
using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the network driver provider could go wrong silently:
/// - a chip is matched to the wrong maker (a Killer E3100G is Realtek silicon and an E3100 Intel's I225, both on Killer's driver;
///   Killer Wi-Fi is on Intel's package), so Exo puts Intel's generic driver over Killer's;
/// - Intel's package version (31.3) is compared with the INF's DriverVer (2.1.5.12), so every Intel adapter reads behind;
/// - the INF that lists another chip, or only another board's SUBSYS, is taken as the adapter's;
/// - versions compare as text (2.1.10 before 2.1.9), or Intel's legacy 22.x Wi-Fi line and Realtek's NetAdapterCx line
///   are compared with a package they aren't on, so a row claims a newer driver that isn't;
/// - the zip's directory or a deflated INF is misread, or a UTF-16 INF reads as garbage;
/// - Intel's page is misread: another file's SHA-512, another version;
/// - the elevated pnputil lines take a name or path that cmd would read as a second command;
/// - an install starts while a game, a call, a game download or Exo's own update runs (the link drops for seconds), or
///   installs a version the advisories learned of after the list was read (the check is asked again right before Windows' prompt).
/// </summary>
public sealed class NetworkDriversTests : IDisposable
{
    // This PC's adapter, as Windows lists it.
    private const string I226V = @"PCI\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\6&102e3adf&0&0048020A";
    private const string Zip = "https://downloadmirror.intel.com/931387/Wired_driver_31.3_x64.zip";
    private const string Sha = "3621b7dce617cb57b1e243bccf1defa5cacc8c9f1b8857c9ca1e71591eabe8bd64259cdadfdfe6041b7eca10c8324657a86592c4f758bb5d0af8c114bee24cd4";

    // e2fn.inf from Intel's 31.3 package (PRO2500\Winx64\W11), its Version section and a few model lines.
    private const string E2fn = """
        [Version]
        Signature   = "$Windows NT$"
        Class       = Net
        ClassGUID   = {4d36e972-e325-11ce-bfc1-08002be10318}
        Provider    = %Intel%
        PnpLockDown = 1
        CatalogFile = e2fn.cat
        DriverVer   = 03/24/2026,2.1.5.12

        [Intel.NTamd64.10.0.1..22000]
        %E15F3_3NC.DeviceDesc%          = I225.10.0.1..22000,         PCI\VEN_8086&DEV_15F3&REV_03
        %E125BNC.DeviceDesc%            = I226.10.0.1..22000,         PCI\VEN_8086&DEV_125B
        %E125CNC.DeviceDesc%            = I226.10.0.1..22000,         PCI\VEN_8086&DEV_125C
        %E125B_Retail.DeviceDesc%       = I226.10.0.1..22000,         PCI\VEN_8086&DEV_125B&SUBSYS_00018086
        """;

    private const string IntelPage = """
        <meta name="DownloadVersion" content="31.3"/>
        <label>Date</label>
                                <span class="dc-page-banner-actions-detail__value">10/16/2026</span>
        <a href="https://downloadmirror.intel.com/931387/Wired_driver_31.3_x64.zip">Download</a>
        <li class="dc-page-available-downloads-hero-details-list__detail">SHA512: 3621b7dce617cb57b1e243bccf1defa5cacc8c9f1b8857c9ca1e71591eabe8bd64259cdadfdfe6041b7eca10c8324657a86592c4f758bb5d0af8c114bee24cd4</li>
        <a href="https://downloadmirror.intel.com/931387/readme.txt">readme</a>
        <li>SHA512: 0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000f</li>
        """;

    private const string RealtekPage = """
        <tr><td style="text-align:center"><a href="/Download/ToDownload?type=direct&amp;downloadid=4357"></a></td>
        <td>Win10/Win11 Auto Installation Program (NDIS) - Not Support Power Saving</td>
        <td>10.80.20</td><td>2026/10/05</td><td>5 MB</td></tr>
        <tr><td style="text-align:center"><a href="/Download/ToDownload?type=direct&amp;downloadid=3613"></a></td>
        <td>Win10/Win11 Auto Installation Program (NDIS)</td>
        <td>10.80.50</td>
        <td>2026/10/05</td><td>5 MB</td></tr>
        <tr><td>Win11 Auto Installation Program (NetAdapterCx)</td><td>11.031.50</td></tr>
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("exo-netdriver-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    [Theory]
    [InlineData(I226V, false, "IntelWired")]
    [InlineData(@"PCI\VEN_8086&DEV_15F3&SUBSYS_86721043&REV_03\4&1", false, "IntelWired")]
    [InlineData(@"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\4&1", true, "IntelWifi")]
    [InlineData(@"PCI\VEN_8086&DEV_2725&SUBSYS_16718086&REV_1A\4&1", true, "IntelWifi")] // Killer AX1675: Intel's package
    [InlineData(@"PCI\VEN_10EC&DEV_8125&SUBSYS_86771043&REV_05\4&1", false, "Realtek")]
    [InlineData(@"PCI\VEN_10EC&DEV_8126&SUBSYS_7E061462&REV_01\4&1", false, "Realtek")]
    [InlineData(@"PCI\VEN_10EC&DEV_3000&SUBSYS_7D251462&REV_06\4&1", false, "Killer")] // E3100G
    [InlineData(@"PCI\VEN_10EC&DEV_5000&SUBSYS_7E061462&REV_01\4&1", false, "Killer")] // E5000
    [InlineData(@"PCI\VEN_1D6A&DEV_07B1&SUBSYS_87411043&REV_02\4&1", false, "Marvell")]
    [InlineData(@"PCI\VEN_14C3&DEV_0616&SUBSYS_E0CD1A3B&REV_00\4&1", true, "MediaTek")]
    [InlineData(@"PCI\VEN_17CB&DEV_1107&SUBSYS_E0E91A3B&REV_01\4&1", true, "Qualcomm")]
    [InlineData(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00011A56&REV_03\4&1", false, "Killer")] // E3100: Intel I225, Killer's driver
    [InlineData(@"USB\VID_0BDA&PID_8156\000001", false, "None")]
    public void Identifies_the_maker_by_vendor(string device, bool wifi, string expected) =>
        Assert.Equal(expected, NetworkDrivers.Identify(device, wifi).ToString());

    [Fact]
    public void Reads_the_infs_driverver_not_the_package_version()
    {
        Assert.Equal(("2.1.5.12", "2026-03-24"), NetworkDrivers.DriverVer(E2fn));
        Assert.Null(NetworkDrivers.DriverVer("[Version]\nCatalogFile = e2fn.cat"));
    }

    [Fact]
    public void Matches_the_inf_that_lists_the_adapter()
    {
        var ids = NetworkDrivers.PciIds(E2fn);
        Assert.Contains(@"PCI\VEN_8086&DEV_125C", ids);
        var infs = new[]
        {
            new NetworkDrivers.PackageInf(@"PRO1000\Winx64\W11\e1d.inf", "12.19.2.65", "2026-01-12", [@"PCI\VEN_8086&DEV_15B8"]),
            new NetworkDrivers.PackageInf(@"PRO2500\Winx64\W11\e2fn.inf", "2.1.5.12", "2026-03-24", ids),
        };
        Assert.Equal("2.1.5.12", NetworkDrivers.Match(infs, I226V)?.Version);
        Assert.Equal("2.1.5.12", NetworkDrivers.Match(infs, @"PCI\VEN_8086&DEV_15F3&SUBSYS_86721043&REV_03\4&1")?.Version);
        // An I225-V B2 isn't on this INF's REV_03 line; an Intel chip on no INF matches none.
        Assert.Null(NetworkDrivers.Match(infs, @"PCI\VEN_8086&DEV_15F3&SUBSYS_86721043&REV_02\4&1"));
        Assert.Null(NetworkDrivers.Match(infs, @"PCI\VEN_8086&DEV_1539&SUBSYS_86721043&REV_03\4&1"));
    }

    [Fact]
    public void A_subsys_line_covers_only_that_board()
    {
        Assert.False(NetworkDrivers.Covers(@"PCI\VEN_8086&DEV_125B&SUBSYS_00018086", @"PCI\VEN_8086&DEV_125B&SUBSYS_88671043&REV_06\6&1"));
        Assert.True(NetworkDrivers.Covers(@"PCI\VEN_8086&DEV_125B&SUBSYS_00018086", @"PCI\VEN_8086&DEV_125B&SUBSYS_00018086&REV_04\6&1"));
        Assert.True(NetworkDrivers.Covers(@"PCI\VEN_8086&DEV_125C", I226V));
    }

    [Theory]
    [InlineData("2.1.5.12", "2.1.4.3", true)]
    [InlineData("2.1.5.12", "2.1.5.12", false)]
    [InlineData("2.1.10.1", "2.1.9.7", true)]
    [InlineData("12.19.2.65", "12.19.2.9", true)]
    [InlineData("2.1.4.3", "2.1.5.12", false)]
    public void Intel_wired_versions_compare_by_number(string latest, string installed, bool newer) =>
        Assert.Equal(newer, VersionNumber.Newer(latest, installed));

    [Fact]
    public void Reads_intels_wired_page()
    {
        var package = NetworkDrivers.IntelWiredPackage(IntelPage);
        Assert.Equal(new NetworkDrivers.IntelPackage("31.3", Zip, Sha, "2026-10-16"), package);
    }

    [Fact]
    public void Reads_intels_wifi_page_and_compares_three_parts()
    {
        const string page = """<meta name="DownloadVersion" content="24.80.0"/><a href="https://downloadmirror.intel.com/929786/WiFi-24.80.0-Driver64-Win10-Win11.exe">""";
        Assert.Equal("24.80.0", NetworkDrivers.IntelWifiVersion(page));
        Assert.Null(NetworkDrivers.IntelWifiVersion(page.Replace("24.80.0\"", "24.70.0\"", StringComparison.Ordinal)));
        Assert.True(NetworkDrivers.WifiBehind("24.70.0.3", "24.80.0"));
        Assert.False(NetworkDrivers.WifiBehind("24.80.0.4", "24.80.0"));
        Assert.Null(NetworkDrivers.WifiBehind("22.200.2.1", "24.80.0")); // Intel's legacy line
    }

    [Fact]
    public void Reads_realteks_packages_and_compares_each_line()
    {
        var packages = NetworkDrivers.RealtekVersions(RealtekPage);
        Assert.Equal(new NetworkDrivers.RealtekPackages("10.80.50", "11.031.50"), packages);
        Assert.Equal(("10.80.50", true), NetworkDrivers.RealtekCompare("10.68.815.2023", packages!));
        Assert.Equal(("10.80.50", false), NetworkDrivers.RealtekCompare("10.80.1003.2026", packages!));
        // NetAdapterCx: the chip's number first, then the package's (Intel lists 1125.31.50.603 for 11.031.50).
        Assert.Equal(("1125.31.50", true), NetworkDrivers.RealtekCompare("1125.21.903.2024", packages!));
        Assert.Equal(("1126.31.50", false), NetworkDrivers.RealtekCompare("1126.31.50.603", packages!));
        Assert.Null(NetworkDrivers.RealtekCompare("7.140.1201.2020", packages!)); // another line: no claim
    }

    [Fact]
    public void Reads_each_killer_models_driver_from_intels_page()
    {
        const string page = """
            <li><span style=""><strong>2.1.5.7</strong> (Windows 11*) </span>for Intel® Killer™ Ethernet E3100</span></li>
            <li><span style="	font-size: 12.0pt;"><strong>1126.31.50.603</strong> (Windows 11*) for Intel® Killer™ Ethernet E5000</span></li>
            <li><span style="	font-size: 12.0pt;"><strong>1125.31.50.603 </strong> (Windows 11*) for Intel® Killer™ Ethernet E3000, E3100G</span></li>
            """;
        var versions = NetworkDrivers.KillerVersions(page)!;
        Assert.Equal(("2.1.5.7", "1126.31.50.603", "1125.31.50.603", "1125.31.50.603"), (versions["E3100"], versions["E5000"], versions["E3000"], versions["E3100G"]));
        Assert.Equal("E3000", NetworkDrivers.KillerModel(@"PCI\VEN_10EC&DEV_3000&SUBSYS_7D251462&REV_06\4&1"));
        Assert.Equal("E5000", NetworkDrivers.KillerModel(@"PCI\VEN_10EC&DEV_5000&SUBSYS_7E061462&REV_01\4&1"));
        Assert.Equal("E3100", NetworkDrivers.KillerModel(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00011A56&REV_03\4&1"));
        Assert.Null(NetworkDrivers.KillerModel(I226V));
    }

    [Theory]
    [InlineData("game")]
    [InlineData("call")]
    [InlineData("download")]
    [InlineData("exo")]
    public async Task Never_installs_while_the_link_drop_would_hurt(string busy)
    {
        var (zip, _) = PackageZip(E2fn);
        var host = new FakeHost(zip, new NetworkDrivers.Adapter(I226V, "Intel(R) Ethernet Controller I226-V", false, "Intel", "2.1.4.3", "2025-06-01", "oem5.inf")) { Busy = busy };
        var drivers = new NetworkDrivers(host, Path.Combine(_folder, "nd"));
        var item = Assert.Single(await drivers.ReadAsync(CancellationToken.None));
        Assert.NotNull(drivers.Busy());
        var result = await drivers.InstallAsync(item, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Empty(host.Downloads);
    }
    [Fact]
    public void Reads_a_utf16_inf_out_of_the_zip_directory()
    {
        var (zip, _) = PackageZip(E2fn);
        var tail = zip[^Math.Min(zip.Length, 65536)..];
        var entry = Assert.Single(NetworkDrivers.ZipDirectory(tail, zip.Length), e => e.Name.EndsWith(".inf", StringComparison.Ordinal));
        Assert.Equal(@"PRO2500\Winx64\W11\e2fn.inf", entry.Name);
        var text = NetworkDrivers.InfText(NetworkDrivers.Inflate(zip[(int)entry.Offset..], entry));
        Assert.Equal(("2.1.5.12", "2026-03-24"), NetworkDrivers.DriverVer(text));
    }

    [Theory]
    [InlineData("2.1.5.12", "None")]
    [InlineData("2.1.4.3", "Install")]
    public async Task This_pcs_i226v_against_intels_package(string installed, string how)
    {
        var (zip, _) = PackageZip(E2fn);
        var host = new FakeHost(zip, new NetworkDrivers.Adapter(I226V, "Intel(R) Ethernet Controller I226-V", false, "Intel", installed, "2026-03-24", "oem5.inf"));
        var item = Assert.Single(await new NetworkDrivers(host, Path.Combine(_folder, "nd")).ReadAsync(CancellationToken.None));
        Assert.Equal(("Intel", "2.1.5.12", "2026-03-24", how), (item.Maker, item.Latest, item.LatestDate, item.How.ToString()));
        Assert.Equal("network:" + I226V, item.Id);
        Assert.DoesNotContain(host.Urls, u => u.Contains("125C", StringComparison.OrdinalIgnoreCase)); // requests name no device
    }

    [Fact]
    public void Pnputil_lines_take_only_plain_names_and_paths()
    {
        const string pnputil = @"C:\Windows\System32\pnputil.exe";
        Assert.Equal(
            [$"\"{pnputil}\" /export-driver oem5.inf \"C:\\Exo\\kept\\oem5\"", $"\"{pnputil}\" /add-driver \"C:\\Exo\\31.3\\e2fn.inf\" /install"],
            NetworkDrivers.InstallCommands(pnputil, "oem5.inf", @"C:\Exo\kept\oem5", @"C:\Exo\31.3\e2fn.inf"));
        Assert.Null(NetworkDrivers.InstallCommands(pnputil, "oem5.inf & calc", @"C:\Exo\kept", @"C:\Exo\e2fn.inf"));
        Assert.Null(NetworkDrivers.InstallCommands(pnputil, "oem5.inf", @"C:\Exo\kept"" & calc", @"C:\Exo\e2fn.inf"));
        // Windows' own driver (net*.inf) stays in Windows: nothing to export, and roll back only removes Exo's.
        Assert.Equal([$"\"{pnputil}\" /add-driver \"C:\\Exo\\31.3\\e2fn.inf\" /install"], NetworkDrivers.InstallCommands(pnputil, "net1ic64.inf", @"C:\Exo\kept\net1ic64", @"C:\Exo\31.3\e2fn.inf"));
        Assert.Null(NetworkDrivers.RollBackCommands(pnputil, @"C:\Exo\kept\e2fn.inf", "e2fn.inf"));
        Assert.Equal(3, NetworkDrivers.RollBackCommands(pnputil, @"C:\Exo\kept\oem5\e2fn.inf", "oem12.inf")?.Count);
        Assert.Equal([$"\"{pnputil}\" /delete-driver oem12.inf /uninstall", $"\"{pnputil}\" /scan-devices"], NetworkDrivers.RollBackCommands(pnputil, null, "oem12.inf"));
    }

    [Fact]
    public async Task Installs_with_one_prompt_keeps_the_old_driver_and_rolls_back()
    {
        var (zip, _) = PackageZip(E2fn);
        var host = new FakeHost(zip, new NetworkDrivers.Adapter(I226V, "Intel(R) Ethernet Controller I226-V", false, "Intel", "2.1.4.3", "2025-06-01", "oem5.inf"))
        {
            Page = IntelPage.Replace(Sha, Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(zip)).ToLowerInvariant(), StringComparison.Ordinal),
        };
        var folder = Path.Combine(_folder, "nd");
        var drivers = new NetworkDrivers(host, folder);
        var item = Assert.Single(await drivers.ReadAsync(CancellationToken.None));

        var result = await drivers.InstallAsync(item, null, CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        var prompt = Assert.Single(host.Prompts);
        Assert.Collection(prompt,
            line => Assert.Contains($"/export-driver oem5.inf \"{Path.Combine(folder, "kept", "oem5")}\"", line, StringComparison.Ordinal),
            line => Assert.EndsWith($"{Path.Combine(folder, "31.3", "e2fn.inf")}\" /install", line, StringComparison.Ordinal));
        Assert.Equal(new[] { "kept" }, Directory.GetDirectories(folder).Select(Path.GetFileName)); // the zip and what was unpacked are gone
        Assert.Empty(Directory.GetFiles(folder, "*.zip*"));
        var updated = Assert.Single(await drivers.ReadAsync(CancellationToken.None));
        Assert.Equal(("2.1.5.12", "None", true), (updated.Version, updated.How.ToString(), updated.CanRollBack));

        var back = await drivers.RollBackAsync(updated, CancellationToken.None);
        Assert.True(back.Ok, back.Message);
        Assert.Collection(host.Prompts[1],
            line => Assert.EndsWith($"{Path.Combine(folder, "kept", "oem5", "e2fn.inf")}\"", line, StringComparison.Ordinal),
            line => Assert.EndsWith("/delete-driver oem12.inf /uninstall", line, StringComparison.Ordinal),
            line => Assert.EndsWith("/scan-devices", line, StringComparison.Ordinal));
        Assert.False(Assert.Single(await drivers.ReadAsync(CancellationToken.None)).CanRollBack);
    }

    [Fact]
    public async Task A_download_that_isnt_intels_package_installs_nothing()
    {
        var (zip, _) = PackageZip(E2fn);
        var host = new FakeHost(zip, new NetworkDrivers.Adapter(I226V, "Intel(R) Ethernet Controller I226-V", false, "Intel", "2.1.4.3", "2025-06-01", "oem5.inf"));
        var drivers = new NetworkDrivers(host, Path.Combine(_folder, "nd"));
        var result = await drivers.InstallAsync(Assert.Single(await drivers.ReadAsync(CancellationToken.None)), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Empty(host.Prompts);
    }

    [Fact]
    public async Task Checks_the_advisories_again_right_before_installing()
    {
        var (zip, _) = PackageZip(E2fn);
        // Learned of after the list was read: Intel's 2.1.5.12 held back for this chip.
        DriverAdvisories.Advisory held = new("test", @"^network:PCI\\VEN_8086&DEV_125C\b.* I226-V$", "2.1.5.12", "2.1.5.13", "a test problem",
            DriverAdvisories.Advice.Skip, null, "https://example.com", "2026-10-29");
        var host = new FakeHost(zip, new NetworkDrivers.Adapter(I226V, "Intel(R) Ethernet Controller I226-V", false, "Intel", "2.1.4.3", "2025-06-01", "oem5.inf"))
        {
            Page = IntelPage.Replace(Sha, Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(zip)).ToLowerInvariant(), StringComparison.Ordinal),
            Advisories = [held],
        };
        var drivers = new NetworkDrivers(host, Path.Combine(_folder, "nd"));
        var item = Assert.Single(await drivers.ReadAsync(CancellationToken.None));
        Assert.Equal("network:" + I226V + " Intel(R) Ethernet Controller I226-V", item.Subject); // the subject the list's check uses too

        var result = await drivers.InstallAsync(item, null, CancellationToken.None);
        Assert.Equal((false, "2.1.5.12 skipped: a test problem."), (result.Ok, result.Message));
        Assert.Empty(host.Prompts); // no Windows prompt, nothing changed
        Assert.Equal(item.Subject, Assert.Single(host.Asked));
    }

    /// <summary>A zip shaped as Intel's: a readme, then the W11 INF (UTF-16 with its BOM, as Intel's are) deflated.</summary>
    private static (byte[] Zip, string Name) PackageZip(string inf)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var readme = new StreamWriter(archive.CreateEntry(@"PRO2500\Winx64\W11\readme.txt").Open())) readme.Write("readme");
            using var stream = archive.CreateEntry(@"PRO2500\Winx64\W11\e2fn.inf", CompressionLevel.Optimal).Open();
            stream.Write([0xFF, 0xFE]);
            stream.Write(Encoding.Unicode.GetBytes(inf));
            stream.Dispose();
            using var catalog = archive.CreateEntry(@"PRO2500\Winx64\W11\e2fn.cat").Open();
            catalog.Write([0x30, 0x82]);
        }
        return (memory.ToArray(), @"PRO2500\Winx64\W11\e2fn.inf");
    }

    private sealed class FakeHost(byte[] zip, params NetworkDrivers.Adapter[] adapters) : NetworkDrivers.IHost
    {
        internal readonly List<string> Urls = [];
        internal readonly List<IReadOnlyList<string>> Prompts = [];
        internal string Page { get; init; } = IntelPage;
        private readonly NetworkDrivers.Adapter[] _current = [.. adapters];

        public IReadOnlyList<NetworkDrivers.Adapter> Adapters() => _current;

        public Task<string> GetTextAsync(string url, CancellationToken ct)
        {
            Urls.Add(url);
            return Task.FromResult(url == NetworkDrivers.IntelWiredPage ? Page : "");
        }

        public Task<(byte[] Bytes, long Total)> GetRangeAsync(string url, long from, int length, CancellationToken ct)
        {
            Urls.Add(url);
            var start = from < 0 ? Math.Max(0, zip.Length + from) : from;
            var end = from < 0 ? zip.Length : Math.Min(zip.Length, from + length);
            return Task.FromResult((zip[(int)start..(int)end], (long)zip.Length));
        }

        internal readonly List<string> Downloads = [];
        internal string? Busy { get; init; }

        public Task DownloadAsync(string url, string path, IProgress<int>? progress, CancellationToken ct)
        {
            Downloads.Add(url);
            File.WriteAllBytes(path, zip);
            return Task.CompletedTask;
        }

        public bool Signed(string catalog) => File.Exists(catalog);
        public bool GameRunning() => Busy == "game";
        public bool InCall() => Busy == "call";
        public bool OtherInstall() => false;
        public bool Downloading() => Busy == "download";
        public bool ExoUpdating() => Busy == "exo";
        /// <summary>What pnputil would do: export writes the kept INF, an install moves the adapter to oem12.inf, a delete puts the old one back.</summary>
        public string? Elevated(IReadOnlyList<string> commands)
        {
            Prompts.Add(commands);
            var old = adapters[0];
            foreach (var line in commands)
            {
                if (line.Contains("/export-driver", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(line.Split('"')[^2], "e2fn.inf"), E2fn);
                else if (line.Contains("/install", StringComparison.Ordinal)) _current[0] = old with { Version = "2.1.5.12", Date = "2026-03-24", Inf = "oem12.inf" };
                else if (line.Contains("/delete-driver", StringComparison.Ordinal)) _current[0] = old;
            }
            return null;
        }

        /// <summary>The advisories this fake answers from (none by default), with the subjects it was asked about.</summary>
        internal IReadOnlyList<DriverAdvisories.Advisory> Advisories { get; init; } = [];
        internal readonly List<string> Asked = [];

        public string? Advised(string subject, string version)
        {
            Asked.Add(subject);
            return DriverAdvisories.Find(Advisories, subject, version, new([], 26300)) is { } a ? DriverAdvisories.Skipped(version, a) + "." : null;
        }
    }
}
