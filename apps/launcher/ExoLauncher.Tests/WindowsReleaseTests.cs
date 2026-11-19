using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the About card's "is Windows on the newest version" could go wrong silently:
/// - the newest is read from the wrong row: a Long-Term Servicing row, a release history row, an older version the table
///   lists first, or a version made only for new devices (26H1, not offered to existing PCs) while a yearly one is out;
/// - a footnote mark joins the version's name ("24H2" and its "1");
/// - a monthly update decides it: a PC on the newest feature version with an older update is called out of date, or one
///   on an older version with a higher update number is called up to date;
/// - a released build is called Insider (26H1's 28000 is above 26H2's 26300), or one above every release is not;
/// - a page without the table, or without a General Availability row, reads as some version (a guess) instead of unknown;
/// - Windows 10 is checked against Windows 11's page.
/// Pages under Fixtures/WindowsRelease are Microsoft's own, saved October 28, 2026 (current versions and the start of the
/// release history).
/// </summary>
public sealed class WindowsReleaseTests
{
    private static string Page(string name) => File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "WindowsRelease", name + ".html"));

    private static string Table(params string[] rows) =>
        "<table><tbody><tr><th>Version</th><th>Servicing option</th><th>Availability date</th><th>Latest build</th></tr>"
        + string.Concat(rows.Select(row => "<tr>" + string.Concat(row.Split('|').Select(cell => $"<td nowrap=\"\">{cell}</td>")) + "</tr>"))
        + "</tbody></table>";

    private static readonly PcAbout.NewestWindows Newest26H2 = new("26H2", 26300, 28000);

    // ---- Microsoft's page ----

    [Fact]
    public void Windows11PageNames26H2NotTheNewDevicesVersion() =>
        Assert.Equal(Newest26H2, PcAbout.ParseReleases(Page("windows11-release-information")));

    [Fact]
    public void Windows10PageNames22H2() =>
        Assert.Equal(new PcAbout.NewestWindows("22H2", 19045, 19045), PcAbout.ParseReleases(Page("release-information")));

    [Fact]
    public void GeneralAvailabilityRowWinsOverALaterLongTermRow() => Assert.Equal(
        new PcAbout.NewestWindows("25H2", 26200, 26200),
        PcAbout.ParseReleases(Table(
            "24H2<sup>1</sup>|Long-Term Servicing Channel (LTSC)|2026-11-01|26100.9725",
            "25H2|General Availability Channel|2025-09-30|26200.9818")));

    [Fact]
    public void NewestIsTheLastToComeOutNotTheFirstListed() => Assert.Equal(
        new PcAbout.NewestWindows("25H2", 26200, 26200),
        PcAbout.ParseReleases(Table(
            "23H2|General Availability Channel|2023-10-31|22631.7654",
            "25H2|General Availability Channel|2025-09-30|26200.9818",
            "24H2|General Availability Channel|2024-10-01|26100.9725")));

    [Fact]
    public void FootnoteMarkIsNotPartOfTheName() =>
        Assert.Equal("24H2", PcAbout.ParseReleases(Table("24H2<sup>1</sup>|General Availability Channel|2024-10-01|26100.9725"))?.Version);

    [Fact]
    public void NewDevicesVersionCountsOnlyWhenNoYearlyOneIsListed()
    {
        Assert.Equal(new PcAbout.NewestWindows("26H1", 28000, 28000), PcAbout.ParseReleases(Table("26H1|General Availability Channel|2026-02-10|28000.3365")));
        Assert.Equal(new PcAbout.NewestWindows("25H2", 26200, 28000), PcAbout.ParseReleases(Table(
            "26H1|General Availability Channel|2026-02-10|28000.3365",
            "25H2|General Availability Channel|2025-09-30|26200.9818")));
    }

    [Fact]
    public void ReleaseHistoryIsNotRead()
    {
        var history = "<h2 id=\"windows-11-release-history\">Windows 11 release history</h2>"
            + Table("27H2|General Availability Channel|2027-09-28|27900.100");
        Assert.Equal(new PcAbout.NewestWindows("25H2", 26200, 26200),
            PcAbout.ParseReleases(Table("25H2|General Availability Channel|2025-09-30|26200.9818") + history));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body><p>Windows 11 release information is moving.</p></body></html>")]
    [InlineData("<table><tr><th>Version</th><th>Servicing option</th><th>Build</th></tr><tr><td>26H2</td><td>General Availability Channel</td><td>26300.9818</td></tr></table>")]
    public void UnreadablePageIsUnknown(string html) => Assert.Null(PcAbout.ParseReleases(html));

    [Fact]
    public void OnlyLongTermRowsIsUnknown() =>
        Assert.Null(PcAbout.ParseReleases(Table("24H2|Long-Term Servicing Channel (LTSC)|2024-10-01|26100.9725")));

    [Fact]
    public void RowWithoutABuildIsSkipped() => Assert.Equal(
        new PcAbout.NewestWindows("25H2", 26200, 26200),
        PcAbout.ParseReleases(Table("26H2|General Availability Channel|2026-09-29|To be announced", "25H2|General Availability Channel|2025-09-30|26200.9818")));

    [Fact]
    public void PageFollowsTheWindowsOnThisPc()
    {
        Assert.EndsWith("/windows11-release-information", PcAbout.ReleasePage(26300));
        Assert.EndsWith("/release-information", PcAbout.ReleasePage(19045));
        Assert.DoesNotContain("windows11", PcAbout.ReleasePage(19045));
    }

    // ---- This PC against it ----

    [Fact]
    public void SameFeatureVersionIsUpToDateWhateverItsMonthlyUpdate() => Assert.Equal(
        new PcAbout.WindowsStatus("Windows 11", "26H2", "26300.9711", "26H2", 26300, true, false),
        PcAbout.Status(26300, "26300.9711", "26H2", false, Newest26H2));

    [Fact]
    public void OlderFeatureVersionIsNotUpToDateWhateverItsMonthlyUpdate() =>
        Assert.False(PcAbout.Status(26200, "26200.9818", "25H2", false, Newest26H2).UpToDate);

    [Fact]
    public void NewDevicesVersionIsUpToDateAndReleased()
    {
        var status = PcAbout.Status(28000, "28000.3365", "26H1", false, Newest26H2);
        Assert.True(status.UpToDate);
        Assert.False(status.Insider);
    }

    [Fact]
    public void BuildAboveEveryReleaseIsInsider()
    {
        var status = PcAbout.Status(29510, "29510.1000", "26H2", false, Newest26H2);
        Assert.True(status.Insider);
        Assert.True(status.UpToDate);
    }

    [Fact]
    public void InsiderChannelIsInsiderOnAReleasedBuild() =>
        Assert.True(PcAbout.Status(26300, "26300.9711", "26H2", true, Newest26H2).Insider);

    [Fact]
    public void WithoutMicrosoftsAnswerNothingIsGuessed() => Assert.Equal(
        new PcAbout.WindowsStatus("Windows 11", "26H2", "26300.9711", null, null, null, false),
        PcAbout.Status(26300, "26300.9711", "26H2", false, null));

    [Fact]
    public void Windows10IsNamedSo() =>
        Assert.Equal("Windows 10", PcAbout.Status(19045, "19045.7811", "22H2", false, new("22H2", 19045, 19045)).Name);
}
