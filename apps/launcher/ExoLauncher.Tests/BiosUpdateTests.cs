using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Drivers list's BIOS could go wrong silently:
/// - a beta, or a BIOS ASUS lists but hasn't released (IsRelease "0"), is offered as the update;
/// - the newest is taken as the list's first file instead of its highest version;
/// - a laptop's "GA402RJ.319" in the registry doesn't match ASUS's "319", so it never reads as up to date;
/// - another maker's version ("P3.20", "1.A0", "F17a") reads as an ASUS number and is compared;
/// - a BIOS newer than the newest release (a beta someone flashed) is called out of date;
/// - a model ASUS doesn't know, or a list without released files, reads as some version instead of none;
/// - the maker's page is built from something other than the model, or isn't escaped.
/// The answers under Fixtures/BiosUpdate are ASUS's own (GetPDBIOS), saved October 29, 2026.
/// </summary>
public sealed class BiosUpdateTests
{
    private const string Board = "ROG STRIX B550-F GAMING WIFI II";

    private static string Answer(string name) => File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "BiosUpdate", name + ".json"));

    private static string List(params (string Version, string Date, string IsRelease, string Description)[] files) =>
        "{\"Result\":{\"Count\":1,\"Obj\":[{\"Name\":\"BIOS\",\"Count\":" + files.Length + ",\"Files\":["
        + string.Join(",", files.Select(f => $"{{\"Version\":\"{f.Version}\",\"Title\":\"\",\"Description\":\"{f.Description}\",\"ReleaseDate\":\"{f.Date}\",\"IsRelease\":\"{f.IsRelease}\"}}"))
        + "]}]},\"Status\":\"SUCCESS\",\"Message\":\"\"}";

    // ---- ASUS's list ----

    [Fact]
    public void BoardListNamesItsNewestRelease() =>
        Assert.Equal(new BiosUpdate.Newest("3645", "2026-09-14", BiosUpdate.AsusPage(Board)),
            BiosUpdate.ParseAsus(Answer("asus-rog-strix-b550-f-gaming-wifi-ii"), Board));

    [Fact]
    public void LaptopListNamesItsNewestFromTheBiosGroup() =>
        Assert.Equal(new BiosUpdate.Newest("319", "2023-08-11", BiosUpdate.AsusPage("GA402RJ")),
            BiosUpdate.ParseAsus(Answer("asus-ga402rj"), "GA402RJ"));

    [Fact]
    public void UnreleasedAndBetaFilesAreNeverTheUpdate() =>
        Assert.Equal("3645", BiosUpdate.ParseAsus(List(
            ("3701", "2026/10/20", "0", "Improve system compatibility"),
            ("3650", "2026/10/02", "1", "Beta version: update AGESA"),
            ("3645", "2026/09/14", "1", "Improve system compatibility")), Board)?.Version);

    [Fact]
    public void HighestVersionWinsWhateverTheOrder() =>
        Assert.Equal("3645", BiosUpdate.ParseAsus(List(
            ("3621", "2025/04/01", "1", ""),
            ("3645", "2026/09/14", "1", ""),
            ("3644", "2026/08/27", "1", "")), Board)?.Version);

    [Fact]
    public void UnknownModelIsNone() =>
        Assert.Null(BiosUpdate.ParseAsus("{\"Result\":null,\"Status\":\"FAIL\",\"Message\":\"FAIL\"}", "NOT A BOARD"));

    [Fact]
    public void ListWithoutAReleaseIsNone() =>
        Assert.Null(BiosUpdate.ParseAsus(List(("3634", "2025/09/24", "0", "")), Board));

    [Fact]
    public void PageIsTheModelsEscaped() =>
        Assert.Equal("https://www.asus.com/supportonly/ROG%20STRIX%20B550-F%20GAMING%20WIFI%20II/helpdesk_bios/", BiosUpdate.AsusPage(Board));

    // ---- Versions ----

    [Theory]
    [InlineData("3636", 3636)]
    [InlineData("3645", 3645)]
    [InlineData("0805", 805)]
    [InlineData("GA402RJ.319", 319)]
    [InlineData("UX425EA.311", 311)]
    [InlineData("319", 319)]
    [InlineData(" 3636 ", 3636)]
    public void AsusVersionsReadAsNumbers(string version, int number) => Assert.Equal(number, BiosUpdate.AsusNumber(version));

    [Theory]
    [InlineData("P3.20")]   // ASRock
    [InlineData("1.A0")]    // MSI
    [InlineData("A.H0")]    // MSI
    [InlineData("F17a")]    // Gigabyte
    [InlineData("1.21.0")]  // Dell
    [InlineData("ALASKA - 1072009")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherVersionsAreNotAsusNumbers(string? version) => Assert.Null(BiosUpdate.AsusNumber(version));

    // ---- This PC against the newest ----

    private static readonly BiosUpdate.Newest Latest = new("3645", "2026-09-14", BiosUpdate.AsusPage(Board));

    [Fact]
    public void OlderBiosIsOutOfDate() =>
        Assert.Equal(new BiosUpdate.Bios("ASUS " + Board, "3636", "2026-01-04", "3645", "2026-09-14", false, Latest.Page),
            BiosUpdate.Compare("ASUS " + Board, "3636", "2026-01-04", Latest));

    [Theory]
    [InlineData("3645")]
    [InlineData("3650")] // a beta newer than the newest release
    public void SameOrNewerIsUpToDate(string version) => Assert.True(BiosUpdate.Compare(Board, version, null, Latest).UpToDate);

    [Fact]
    public void LaptopRegistryVersionMatchesTheList() =>
        Assert.True(BiosUpdate.Compare("ASUS GA402RJ", "GA402RJ.319", "2023-08-11", new BiosUpdate.Newest("319", "2023-08-11", BiosUpdate.AsusPage("GA402RJ"))).UpToDate);

    [Fact]
    public void WithoutTheMakersListOnlyWhatRunsShows() =>
        Assert.Equal(new BiosUpdate.Bios("MSI MAG B550 TOMAHAWK (MS-7C91)", "A.H0", "2024-01-12", null, null, null, null),
            BiosUpdate.Compare("MSI MAG B550 TOMAHAWK (MS-7C91)", "A.H0", "2024-01-12", null));

    [Fact]
    public void VersionAsusDoesNotNumberHasNoVerdict() =>
        Assert.Null(BiosUpdate.Compare(Board, "P3.20", null, Latest).UpToDate);
}
