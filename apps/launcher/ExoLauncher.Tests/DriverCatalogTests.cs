using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Drivers list could go wrong, each silent in the app:
/// - Windows Update's graphics driver shown beside NVIDIA's, or replacing it (an OEM-modified build, or one a game advises against);
/// - Windows Update's AMD System devices offered on top of the AMD chipset package Exo installs;
/// - a maker's row dropped for Windows Update's older offer for the same adapter, or Windows Update's newer one hidden behind a link-only row;
/// - a driver older than the one installed offered (Windows Update ranks by date and match, not version);
/// - a device on Windows' own placeholder driver (10.0.26100.1) never offered its real driver, because the placeholder outnumbers it;
/// - a hotfix offered, or a version with a known problem on this PC (DriverAdvisories) offered, or what runs with one left
///   without its way out (the maker's page, even when the maker's list wasn't read);
/// - a vendor's app shipped as a driver counted as an update and run by Update all, or a data-collecting one offered at all;
/// - going back fetching anything but NVIDIA's package of the advised version (the notebook one on a laptop);
/// - an update's version not read from either title format, so nothing compares;
/// - mid-batch, a driver the script hasn't reached yet (or an answer caught half written) read as failed.
/// </summary>
public sealed class DriverCatalogTests
{
    private static WindowsUpdateDrivers.Offer Offer(string id, string cls, string maker, string version, string? date = "2026-06-16",
        string? device = null, string? installed = null, string? installedDate = null, string? inf = null, string? title = null, bool hidden = false) =>
        new(id, title ?? $"{maker} - {cls} - {version}", cls, maker, "Model", version, date, hidden, device, "Device " + id, installed, installedDate, inf);

    private static DriverItem Row(string id, string kind, string? version, string? latest, DriverHow how) =>
        new(id, kind, "Adapter", "Intel", version, null, latest, null, how, null, false);

    [Fact]
    public void GraphicsDriversComeFromTheCardsMakerNotWindowsUpdate()
    {
        var list = DriverCatalog.Merge([], [Offer("a", "Display", "NVIDIA", "32.0.15.9999", device: "PCI\\GPU", installed: "32.0.15.6094", inf: "oem40.inf")], ["NVIDIA"], null);
        Assert.Empty(list);
        // A card Exo has no source for still gets Windows Update's driver.
        Assert.Single(DriverCatalog.Merge([], [Offer("a", "Display", "Matrox", "5.0", device: "PCI\\M", installed: "4.0", inf: "oem3.inf")], ["NVIDIA"], null));
    }

    [Fact]
    public void TheChipsetPackageWinsOverWindowsUpdatesSystemDevices()
    {
        Assert.Empty(DriverCatalog.Merge([], [Offer("a", "System", "Advanced Micro Devices, Inc.", "5.0", device: "PCI\\PSP", installed: "4.0", inf: "oem9.inf")], [], "AMD"));
        Assert.Single(DriverCatalog.Merge([], [Offer("a", "System", "Intel Corporation", "5.0", device: "PCI\\X", installed: "4.0", inf: "oem9.inf")], [], "AMD"));
    }

    [Fact]
    public void AMakersRowWinsUnlessWindowsUpdateIsNewerAndTheRowOnlyLinks()
    {
        var install = Row("network:PCI\\NIC", "network", "1.0", "2.0", DriverHow.Install);
        var keep = DriverCatalog.Merge([install], [Offer("a", "Net", "Intel", "3.0", device: "PCI\\NIC", installed: "1.0", inf: "oem5.inf")], [], null);
        Assert.Equal("network", Assert.Single(keep).Kind);

        var link = Row("network:PCI\\NIC", "network", "1.0", "2.0", DriverHow.Page);
        Assert.Equal("windows", Assert.Single(DriverCatalog.Merge([link], [Offer("a", "Net", "Intel", "3.0", device: "PCI\\NIC", installed: "1.0", inf: "oem5.inf")], [], null)).Kind);
        Assert.Equal("network", Assert.Single(DriverCatalog.Merge([link], [Offer("a", "Net", "Intel", "1.5", device: "PCI\\NIC", installed: "1.0", inf: "oem5.inf")], [], null)).Kind);
    }

    [Theory]
    [InlineData("31.0.101.5000", "2026-01-01", "oem4.inf", "30.0.101.1000", "2026-06-01", false)] // older number, newer date
    [InlineData("31.0.101.5000", "2026-01-01", "oem4.inf", "31.0.101.5000", "2026-06-01", false)] // the same
    [InlineData("31.0.101.5000", "2026-06-01", "oem4.inf", "31.0.101.6000", "2026-01-01", false)] // newer number, earlier date
    [InlineData("31.0.101.5000", "2026-01-01", "oem4.inf", "31.0.101.6000", "2026-06-01", true)]
    [InlineData("10.0.26100.1", "2006-06-20", "c_swcomponent.inf", "1.0.4.7057", "2026-06-16", true)] // Windows' placeholder
    [InlineData(null, null, null, "1.0", null, true)] // no driver yet
    [InlineData("1.0", null, "oem1.inf", null, null, false)] // no readable offer
    public void OnlyNewerThanWhatRuns(string? installed, string? installedDate, string? inf, string? offered, string? offeredDate, bool newer) =>
        Assert.Equal(newer, DriverCatalog.Newer(installed, installedDate, inf, offered, offeredDate));

    [Fact]
    public void HiddenHotfixAndOlderOffersAreLeftOut()
    {
        Assert.Empty(DriverCatalog.Merge([], [Offer("a", "Media", "Realtek", "6.0", hidden: true)], [], null));
        Assert.Empty(DriverCatalog.Merge([], [Offer("a", "Media", "Realtek", "6.0", title: "Realtek - Media - 6.0 Hotfix")], [], null));
        Assert.Empty(DriverCatalog.Merge([], [Offer("a", "Media", "Realtek", "6.0", device: "HDAUDIO\\X", installed: "6.1", inf: "oem7.inf")], [], null));
    }

    [Fact]
    public void TwoOffersForOneDeviceShowOnceTheNewest()
    {
        var list = DriverCatalog.Merge([], [Offer("a", "Media", "Realtek", "6.0", device: "HDAUDIO\\X"), Offer("b", "Media", "Realtek", "6.2", device: "HDAUDIO\\X")], [], null);
        Assert.Equal("6.2", Assert.Single(list).Latest);
    }

    private static readonly DriverAdvisories.Advisory BiosFtpm = DriverAdvisories.BuiltIn.Advisories.Single(a => a.Id == "asus-b550f-wifi-ii-ftpm-2026");

    /// <summary>The built-in table on a PC with no games and Windows 11 26300.</summary>
    private static DriverAdvisories.Advisory? Against(string subject, string? version) =>
        version is null ? null : DriverAdvisories.Find(DriverAdvisories.BuiltIn.Advisories, subject, version, new([], 26300));

    [Fact]
    public void AnOfferedVersionWithAKnownProblemIsLeftOutAndSaysWhy()
    {
        var nic = Row("network:PCI\\VEN_8086&DEV_125C", "network", "2.1.0.1", "2.1.3.10", DriverHow.Install) with { Device = "PCI\\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\\6&1 I226-V" };
        var item = Assert.Single(DriverCatalog.Apply([nic], Against));
        // 2.1.3.10 is before Intel's fix, so it isn't offered; what runs (older still) has the same problem.
        Assert.Equal((DriverHow.None, "intel-i226v-link-drops", "intel-i226v-link-drops"), (item.How, item.Skipped?.Id, item.Advised?.Id));
        Assert.True(item.NeedsYou);
        Assert.Equal("2.1.3.10 skipped: the link drops at random", DriverAdvisories.Skipped(item.Latest!, item.Skipped!));
    }

    [Fact]
    public void ThisPcsBiosNeedsYouForItsFtpmFixesAndOpensAsus()
    {
        // ASUS ROG STRIX B550-F GAMING WIFI II on 3636; ASUS lists 3645.
        var bios = new DriverItem("bios:board", "bios", "BIOS", "ASUS ROG STRIX B550-F GAMING WIFI II", "3636", "2026-01-04", "3645", "2026-09-14",
            DriverHow.Page, "https://www.asus.com/supportonly/ROG%20STRIX%20B550-F%20GAMING%20WIFI%20II/helpdesk_bios/", false) { Device = "ASUS ROG STRIX B550-F GAMING WIFI II" };
        var item = Assert.Single(DriverCatalog.Apply([bios], Against));
        Assert.Equal(BiosFtpm, item.Advised);
        Assert.Null(item.Skipped);
        Assert.Equal((DriverAdvisories.Advice.Update, "3644"), (item.Advised!.Do, item.Advised.To));
        Assert.Equal(DriverHow.Page, item.How);
        // Even when ASUS's list isn't read, the advice opens ASUS's page.
        var unread = Assert.Single(DriverCatalog.Apply([bios with { Latest = null, How = DriverHow.None }], Against));
        Assert.Equal(DriverHow.Page, unread.How);
        // On 3645 there's nothing to say.
        Assert.Null(Assert.Single(DriverCatalog.Apply([bios with { Version = "3645", How = DriverHow.None }], Against)).Advised);
    }

    [Fact]
    public void AnSsdWithAFaultyFirmwareNeedsYouThroughTheAdvisories()
    {
        (string, string, string) samsung = ("Samsung", "Samsung Magician", "https://semiconductor.samsung.com/consumer-storage/support/tools/");
        var faulty = DriverCatalog.Ssd(1, "Samsung SSD 990 PRO 2TB", "0B2QJXD7", null, samsung)!;
        Assert.False(faulty.NeedsYou);
        var applied = Assert.Single(DriverCatalog.Apply([faulty], Against));
        Assert.True(applied.NeedsYou);
        Assert.Equal((DriverHow.Page, "1B2QJXD7"), (applied.How, applied.Advised?.To));
    }

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe", false,
        "https://us.download.nvidia.com/Windows/617.42/617.42-desktop-win10-win11-64bit-international-dch-whql.exe")]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe", true,
        "https://us.download.nvidia.com/Windows/617.42/617.42-notebook-win10-win11-64bit-international-dch-whql.exe")]
    [InlineData(null, true, "https://us.download.nvidia.com/Windows/617.42/617.42-notebook-win10-win11-64bit-international-dch-whql.exe")]
    public void GoingBackDownloadsTheAdvisedVersionFromNvidia(string? newest, bool laptop, string url)
    {
        var template = newest is null ? null : new NvidiaDriverService.Latest("621.01", newest, 900_000_000, null, "game");
        var back = NvidiaDriverService.At("617.42", template, laptop);
        Assert.Equal(url, back.Url);
        Assert.Equal("617.42", back.Version);
        Assert.Equal(0, back.Size);
    }

    [Fact]
    public void NvidiasHdmiAudioGoesInOnlyWhileAScreenOnTheCardPlaysSound()
    {
        const string nvidia = @"{1}.HDAUDIO\FUNC_01&VEN_10DE&DEV_009E&SUBSYS_104387BC&REV_1001\5&EE802AC&0&0001";
        const string realtek = @"{1}.HDAUDIO\FUNC_01&VEN_10EC&DEV_1168&SUBSYS_104387C5&REV_1001\5&590AA42&0&0001";
        // This PC: the VG248 on the RTX 3070 is active (1); its other NVIDIA endpoints are not present (4) or unplugged (0x20000004).
        Assert.True(NvidiaDriverService.DisplayAudio([(4, nvidia), (553648132, nvidia), (1, nvidia), (1, realtek)]));
        Assert.False(NvidiaDriverService.DisplayAudio([(4, nvidia), (8, nvidia), (553648132, nvidia), (1, realtek), (268435457, null)]));
        Assert.True(NvidiaDriverService.DisplayAudio([(0x10000001, nvidia)])); // high bits are flags, not the state
    }

    [Fact]
    public void AnSsdNeedsYouOnlyWithNewerFirmwareAndOpensItsMakersTool()
    {
        (string, string, string) samsung = ("Samsung", "Samsung Magician", "https://semiconductor.samsung.com/consumer-storage/support/tools/");
        Assert.False(DriverCatalog.Ssd(0, "Samsung SSD 990 PRO 2TB", "8B2QJXD7", null, samsung)!.NeedsYou);
        var newer = DriverCatalog.Ssd(0, "Samsung SSD 990 PRO 2TB", "4B2QJXD7", "8B2QJXD7", samsung)!;
        Assert.Equal((DriverHow.Page, "8B2QJXD7", "Samsung Magician"), (newer.How, newer.Latest, newer.Source));
        Assert.Equal("https://semiconductor.samsung.com/consumer-storage/support/tools/", newer.Page);
        Assert.False(DriverCatalog.Ssd(2, "Unknown drive", "1.0", "2.0", null)!.NeedsYou);
        Assert.Null(DriverCatalog.Ssd(3, null, "1.0", null, null));
    }

    [Fact]
    public void AVendorsAppIsItsOwnRowOutsideUpdateAllAndBloatIsLeftOut()
    {
        var app = Offer("a", "SoftwareComponent", "Realtek", "1.0.0.5", device: "SWC\\VEN_10EC&AID_0001", title: "Realtek - SoftwareComponent - 1.0.0.5");
        var item = Assert.Single(DriverCatalog.Merge([], [app], [], null));
        Assert.True(item.App);
        Assert.Equal(DriverHow.Install, item.How);
        Assert.False(item.NeedsYou); // not counted, and Update all leaves it for its own Install
        Assert.Empty(DriverCatalog.Merge([], [Offer("b", "SoftwareComponent", "A-Volute", "3.1.0.0", device: "SWC\\AV", title: "A-Volute - SoftwareComponent - 3.1.0.0")], [], null));
        Assert.Empty(DriverCatalog.Merge([], [Offer("c", "SoftwareComponent", "HP Inc.", "1.0", device: "SWC\\HP", title: "HP Inc. - SoftwareComponent - 1.0") with { Model = "HP Analytics service" }], [], null));
        // A device's own driver is never an app.
        Assert.False(Assert.Single(DriverCatalog.Merge([], [Offer("d", "MEDIA", "Realtek", "6.0", device: "HDAUDIO\\X")], [], null)).App);
    }

    [Fact]
    public void DuplicateSoftwareComponentOffersCollapseToSingleRow()
    {
        var first = Offer("guid-1", "SoftwareComponent", "Dell Inc.", "5.5.0.0", device: "SWC\\VEN_1028&PID_0001", title: "Dell Inc. - SoftwareComponent - 5.5.0.0") with { Model = "Alienware Command Center Win32 Component" };
        var second = Offer("guid-2", "SoftwareComponent", "Dell Inc.", "5.5.1.0", device: "SWC\\VEN_1028&PID_0002", title: "Dell Inc. - SoftwareComponent - 5.5.1.0") with { Model = "Alienware Command Center Win32 Component" };
        var third = Offer("guid-3", "SoftwareComponent", "Dell Inc.", "5.4.0.0", device: null, title: "Dell Inc. - SoftwareComponent - 5.4.0.0") with { Model = "Alienware Command Center  Win32 Component" };
        var other = Offer("guid-4", "SoftwareComponent", "Realtek", "1.0.0.5", device: "SWC\\VEN_10EC&AID_0001", title: "Realtek - SoftwareComponent - 1.0.0.5") with { Model = "Realtek Audio Console" };

        var merged = DriverCatalog.Merge([], [first, second, third, other], [], null);
        Assert.Equal(2, merged.Count);
        var alienware = Assert.Single(merged, i => i.Name.Contains("Alienware", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("5.5.1.0", alienware.Latest);
    }

    [Theory]
    [InlineData("Microsoft Corporation AudioProcessingObject Driver Update (1.0.4.7057)", "1.0.4.7057")]
    [InlineData("Intel - System - 10.1.1.44", "10.1.1.44")]
    [InlineData("Realtek Semiconductor Corp. - Extension - 10.0.26100.21310", "10.0.26100.21310")]
    [InlineData("Some driver without a version", null)]
    public void TheVersionIsReadFromEitherTitle(string title, string? version) =>
        Assert.Equal(version, WindowsUpdateDrivers.TitleVersion(title));

    [Fact]
    public void TheInstallAnswerSaysRestartAndRollBack()
    {
        var a = new DriverItem("windows:740c4b6c-bcfa-4f97-ae49-7bcc6933e696", "windows", "A", null, "1", null, "2", null, DriverHow.Install, null, false);
        var b = a with { Id = "windows:11111111-2222-3333-4444-555555555555" };
        var results = WindowsUpdateDrivers.Results([a, b],
            """{"Reboot":true,"Results":[{"Id":"740c4b6c-bcfa-4f97-ae49-7bcc6933e696","Ok":true},{"Id":"11111111-2222-3333-4444-555555555555","Ok":false,"RolledBack":true}]}""");
        Assert.True(results[0].Result.Ok);
        Assert.True(results[0].Result.Restart);
        Assert.False(results[1].Result.Ok);
        Assert.Contains("earlier driver is back", results[1].Result.Message);
        Assert.All(WindowsUpdateDrivers.Results([a], null), r => Assert.False(r.Result.Ok));
    }

    [Fact]
    public void MidBatchOnlyTheDriversDoneSoFarLand()
    {
        var a = new DriverItem("windows:740c4b6c-bcfa-4f97-ae49-7bcc6933e696", "windows", "A", null, "1", null, "2", null, DriverHow.Install, null, false);
        var b = a with { Id = "windows:11111111-2222-3333-4444-555555555555" };
        var landed = Assert.Single(WindowsUpdateDrivers.Results([a, b],
            """{"Reboot":false,"Results":[{"Id":"740c4b6c-bcfa-4f97-ae49-7bcc6933e696","Ok":true}]}""", landedOnly: true));
        Assert.Equal(a.Id, landed.Item.Id);
        Assert.True(landed.Result.Ok);
        Assert.Empty(WindowsUpdateDrivers.Results([a, b], """{"Reboot":false,"Resu""", landedOnly: true));
        Assert.Empty(WindowsUpdateDrivers.Results([a, b], null, landedOnly: true));
    }
}
