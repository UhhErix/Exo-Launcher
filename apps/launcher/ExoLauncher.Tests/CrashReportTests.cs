using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways an opt-in crash report could leak or break: a path, a URL with its token, a host or machine name, an address, an
/// email, an id or a token left in the excerpt; a report the service refuses (its patterns are the same ones, in
/// cloud/reports/src/validate.js); a field the service does not name; an excerpt past its cap; a version or Windows build
/// outside its shape. Each sample is a realistic log, through AppLog.ReportFor as the Launcher builds it.
/// </summary>
public sealed class CrashReportTests
{
    private const string UserName = "Erix";
    private const string Profile = @"C:\Users\Erix";
    private const string Machine = "DESKTOP-K3J9";
    private static readonly DateTime Now = new(2026, 11, 5, 19, 50, 0, DateTimeKind.Utc);

    // Realistic lines: a game launch that failed, a request with a token in its URL, a crash with a stack, and the
    // Discord room's id and a share path. Nothing here is scrubbed; CrashReport must do that.
    private static readonly string[] Log =
    [
        @"[2026-11-05T19:42:10.1230000Z] WARN Launch: Steam did not start Elden Ring (C:\Users\Erix\Games\Elden Ring\eldenring.exe) for Erix.",
        "[2026-11-05T19:42:10.4560000Z] ERROR Library: Could not read https://api.example.com/v1/library?token=abc123XYZ for steam id 76561198012345678 | SteamGameException: refused.",
        @"   at ExoLauncher.Services.Library.Launch(String appId) in C:\Users\Erix\Code\exo\Library.cs:line 88",
        "[2026-11-05T19:42:11.0010000Z] CRASH Unhandled: System.AccessViolationException mailed to pat@contoso.org from " + Machine + " at 10.0.0.12",
        @"[2026-11-05T19:42:11.5000000Z] WARN Discord: user 123456789012345678 read \\DESKTOP-K3J9\share\x.txt and /home/erix/.config/exo.json",
        "[2026-11-05T19:42:11.7000000Z] INFO Settings: signed in with eyJhbG" + "ciOiJI" + "UzI1Ni" + "J9.eyJ" + "zdWIiO" + "iIxMjM" + "0NTY3O" + "DkwIn0" + ".abcde" + "fghijk" + "lmnopq" + "rstuvw" + "x",
        "[2026-11-05T19:42:12.0000000Z] WARN Mouse: fetched http://127.0.0.1:9364/achievements and mouse.exo-launcher.local",
        "[2026-11-05T19:42:12.2000000Z] ERROR Store: id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 failed",
        "",
    ];

    private static string Report(string error = "Could not start Elden Ring: SteamGameException", string area = "library") =>
        AppLog.ReportFor(error, area, Log, Now, "3.24.1", "10.0.26300.0", UserName, Profile);

    /// <summary>What the service refuses, copied from cloud/reports/src/validate.js (PERSONAL).</summary>
    private static readonly Regex[] ServicePatterns =
    [
        new(@"[A-Za-z]:\\"),
        new(@"\\\\[^\s\\]+\\"),
        new(@"(^|[^A-Za-z0-9])/(home|Users)/"),
        new(@"[a-z][a-z0-9+.-]{1,15}://", RegexOptions.IgnoreCase),
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}"),
        new(@"\d{15,}"),
        new(@"[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}"),
    ];

    [Fact]
    public void TheExcerptKeepsTheErrorAndTheLogButNotWhoOrWhere()
    {
        var payload = CrashReport.Build("Could not start Elden Ring: SteamGameException", Report(), "3.24.1", "10.0.26300.0", UserName, Profile, Machine);

        Assert.Contains("Could not start Elden Ring", payload.Excerpt);
        Assert.Contains("Steam did not start Elden Ring", payload.Excerpt);
        Assert.Contains("<path>", payload.Excerpt);
        Assert.Contains("<url>", payload.Excerpt);
        Assert.Contains("<host>", payload.Excerpt);
        Assert.Contains("<address>", payload.Excerpt);
        Assert.Contains("<id>", payload.Excerpt);
        foreach (var leak in new[] { UserName, "Erix", @"C:\", "://", "@", "76561", "123456789012", "eyJ", Machine, "DESKTOP", "10.0.0.12", "127.0.0.1", "api.example.com", "exo-launcher.local", "3f2504e0", "share", "/home/", "token=", "contoso" })
            Assert.DoesNotContain(leak, payload.Excerpt);
    }

    [Fact]
    public void TheExcerptPassesTheServicesOwnChecks()
    {
        var payload = CrashReport.Build("Could not start Elden Ring: SteamGameException", Report(), "3.24.1", "10.0.26300.0", UserName, Profile, Machine);
        foreach (var line in payload.Excerpt.Split('\n'))
            foreach (var pattern in ServicePatterns) Assert.DoesNotMatch(pattern, line);
    }

    [Theory]
    [InlineData(@"Opened C:\Users\Erix\AppData\Local\Temp\x.exe")]
    [InlineData(@"Read D:\Games\Riot\VALORANT\x.log for the team")]
    [InlineData(@"Read %USERPROFILE%\Games\x.exe")]
    [InlineData(@"Read <user>\AppData\Local\x")]
    [InlineData(@"Read \\DESKTOP-K3J9\share\x.txt")]
    [InlineData("Fetched https://api.example.com/v1?token=abc")]
    [InlineData("Fetched http://127.0.0.1:9364/achievements")]
    [InlineData("Signed in as erix@example.com")]
    [InlineData("Steam id 76561198012345678 owns this")]
    [InlineData("Discord id 123456789012345678 joined")]
    [InlineData("signed in with eyJhbG" + "ciOiJI" + "UzI1Ni" + "J9.eyJ" + "zdWIiO" + "iIxMjM" + "0NTY3O" + "DkwIn0" + ".abcde" + "fghijk" + "lmnopq" + "rstuvw" + "x")]
    [InlineData("Read /home/erix/.config/exo.json and /Users/erix/x")]
    public void EachKindOfPersonalDataIsTakenOut(string text)
    {
        var scrubbed = CrashReport.Scrub(text, UserName, Profile, Machine);
        foreach (var pattern in ServicePatterns) Assert.False(pattern.IsMatch(scrubbed), $"'{scrubbed}' still matches {pattern}");
        Assert.DoesNotContain("Erix", scrubbed);
    }

    [Theory]
    [InlineData(@"Launch C:\Users\Erix\Games\Elden Ring\eldenring.exe (Steam) ok", "Elden", "Ring", "Games")]
    [InlineData(@"Read \\DESKTOP-K3J9\My Share\Old Files\x.txt (done)", "Share", "Files", "Old")]
    [InlineData(@"Read /home/erix/My Documents/exo.json (done)", "Documents", "exo.json", "My")]
    public void APathWithSpacesIsTakenOutWhole(string text, string first, string second, string third)
    {
        // A path's folder names can hold spaces; each part after a space must go too, not only the first word.
        var scrubbed = CrashReport.Scrub(text, UserName, Profile, Machine);
        Assert.Contains("<path>", scrubbed);
        foreach (var word in new[] { first, second, third }) Assert.DoesNotContain(word, scrubbed);
    }

    [Fact]
    public void TheMachineNameAndTheUserNameAreTakenOutWhereverTheyAre()
    {
        var scrubbed = CrashReport.Scrub("on DESKTOP-K3J9 for Erix (desktop-k3j9)", UserName, Profile, Machine);
        Assert.Equal("on <host> for <user> (<host>)", scrubbed);
    }

    [Fact]
    public void AnErrorNamedInTheTextBecomesTheErrorType()
    {
        var payload = CrashReport.Build("Riot Client closed: VanguardAccessDeniedException", Report("Riot Client closed: VanguardAccessDeniedException", "library"), "3.24.1", "10.0.26300.0", UserName, Profile, Machine);
        Assert.Equal("VanguardAccessDeniedException", payload.ErrorType);
    }

    [Fact]
    public void TestRunWithoutCustomPosterNeverLeavesTheMachine()
    {
        Assert.False(CrashReport.MayLeave(disposable: false, testRun: true, customPoster: false, smokeAllowsSends: false, errorType: "UpdateFailure"));
        Assert.True(CrashReport.MayLeave(disposable: false, testRun: true, customPoster: true, smokeAllowsSends: false, errorType: "UpdateFailure"));
        Assert.True(CrashReport.MayLeave(disposable: false, testRun: false, customPoster: false, smokeAllowsSends: false, errorType: "UpdateFailure"));
    }

    [Fact]
    public void FitExcerptPreservesErrorLineAtTopAndNewestLogTail()
    {
        var head = "Exo Launcher 3.29.0 · Windows 10.0.26200.0 · 2026-11-23 07:27 UTC\nError (exo ai): Its installer didn't start.\n\nExo's log (200 entries):";
        var lines = Enumerable.Range(1, 200).Select(i => $"[2026-11-23T07:00:{i % 60:00}.0000000Z] WARN Entry {i}: " + new string('x', 80));
        var full = head + "\n" + string.Join('\n', lines);

        var fitted = CrashReport.FitExcerpt(full, 1200);
        Assert.True(fitted.Length <= 1200);
        Assert.StartsWith("Exo Launcher 3.29.0", fitted);
        Assert.Contains("Error (exo ai): Its installer didn't start.", fitted);
        Assert.Contains("WARN Entry 200:", fitted);
    }

    [Fact]
    public void WithoutANamedErrorTheTypeIsAppError()
    {
        // A log with no exception name in it (the sample log above has two, which would be picked first).
        var quiet = new[] { "[2026-11-05T19:42:10.1230000Z] WARN Runtimes: the release list was slow." };
        var report = AppLog.ReportFor("Something broke", "pc", quiet, Now, "3.24.1", "10.0.26300.0", UserName, Profile);
        var payload = CrashReport.Build("Something broke", report, "3.24.1", "10.0.26300.0", UserName, Profile, Machine);
        Assert.Equal("AppError", payload.ErrorType);
    }

    [Fact]
    public void HandledWarnLogLinesDoNotOverrideAppErrorClassification()
    {
        var warns = new[]
        {
            "[2026-11-05T19:42:10.1230000Z] WARN Steam achievements: the signed-in read was not used (SteamAuthException); the local cache answers.",
            "[2026-11-05T19:42:11.1230000Z] WARN Store check: HttpRequestException while refreshing.",
        };
        var report = AppLog.ReportFor("None of those are installed for you: choose Every account.", "library", warns, Now, "3.29.0", "10.0.26300.0", UserName, Profile);
        var payload = CrashReport.Build("None of those are installed for you: choose Every account.", report, "3.29.0", "10.0.26300.0", UserName, Profile, Machine);
        Assert.Equal("AppError", payload.ErrorType);

        var withError = new[]
        {
            "[2026-11-05T19:42:12.1230000Z] ERROR Unhandled: NullReferenceException in worker.",
        };
        var errReport = AppLog.ReportFor("Something broke", "library", withError, Now, "3.29.0", "10.0.26300.0", UserName, Profile);
        var errPayload = CrashReport.Build("Something broke", errReport, "3.29.0", "10.0.26300.0", UserName, Profile, Machine);
        Assert.Equal("NullReferenceException", errPayload.ErrorType);
    }

    [Fact]
    public void VersionAndWindowsBuildTakeTheirShape()
    {
        var payload = CrashReport.Build("x", Report("x", "pc"), "3.24.1-beta.2", "10.0.26300.0", UserName, Profile, Machine);
        Assert.Equal("3.24.1", payload.Version);
        Assert.Equal("10.0.26300.0", payload.OsBuild);
        Assert.Equal("10.0", CrashReport.Build("x", Report("x", "pc"), "3.24.1", "10.0", UserName, Profile, Machine).OsBuild);
        Assert.Equal("0.0.0", CrashReport.Build("x", Report("x", "pc"), "dev", "10.0.26300.0", UserName, Profile, Machine).Version);
    }

    [Fact]
    public void AnExcerptNeverGoesPastItsCap()
    {
        var long_ = Enumerable.Range(0, 2000).Select(i => $"[2026-11-05T19:42:10.0000000Z] WARN Library: entry {i} for the game").ToArray();
        var report = AppLog.ReportFor("Could not start: SteamGameException", "library", long_, Now, "3.24.1", "10.0.26300.0", UserName, Profile);
        var payload = CrashReport.Build("Could not start: SteamGameException", report, "3.24.1", "10.0.26300.0", UserName, Profile, Machine);
        Assert.True(payload.Excerpt.Length <= CrashReport.MaxExcerpt);
    }

    [Fact]
    public void ControlCharactersNeverFailAReport()
    {
        // The service refuses control characters (but tab and newline), so a bell or a carriage return must not reach it.
        const string error = "Could not start\u0007 Elden Ring: SteamGameException\r";
        var payload = CrashReport.Build(error, AppLog.ReportFor(error, "library", Log, Now, "3.24.1", "10.0.26300.0", UserName, Profile),
            "3.24.1", "10.0.26300.0", UserName, Profile, Machine);
        Assert.DoesNotMatch("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", payload.Excerpt);
        Assert.Contains("Could not start Elden Ring", payload.Excerpt);
    }

    [Fact]
    public void ToJsonHoldsExactlyTheFiveFields()
    {
        var payload = CrashReport.Build("Could not start Elden Ring: SteamGameException", Report(), "3.24.1", "10.0.26300.0", UserName, Profile, Machine);
        var body = JsonNode.Parse(CrashReport.ToJson(payload))!.AsObject();
        Assert.Equal(new[] { "app", "errorType", "excerpt", "osBuild", "version" }, body.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal("launcher", body["app"]!.GetValue<string>());
        Assert.Equal("3.24.1", body["version"]!.GetValue<string>());
        Assert.Equal("10.0.26300.0", body["osBuild"]!.GetValue<string>());
    }

    [Fact]
    public void TheEndpointIsTheReportsServiceOverHttps()
    {
        Assert.Equal("https://reports.getexo.app/v1/report", CrashReport.Endpoint);
    }

    [Fact]
    public void ScrubTimeoutFallsBackToSafeExcerptWithoutCrashing()
    {
        var payload = CrashReport.Build(
            "HostCallFailed",
            @"C:\Users\Erix\secret.txt on DESKTOP-K3J9 for Erix",
            "3.30.3",
            "10.0.26300.0",
            UserName,
            Profile,
            Machine,
            "HostCallFailed",
            (_, _, _, _) => throw new RegexMatchTimeoutException("input", "pattern", TimeSpan.FromSeconds(5)));

        Assert.Equal("HostCallFailed", payload.ErrorType);
        Assert.Equal("3.30.3", payload.Version);
        Assert.Contains("scrub fallback", payload.Excerpt);
        Assert.DoesNotContain(UserName, payload.Excerpt);
        Assert.DoesNotContain(Machine, payload.Excerpt);
        Assert.DoesNotContain(@"C:\", payload.Excerpt);
    }
}
