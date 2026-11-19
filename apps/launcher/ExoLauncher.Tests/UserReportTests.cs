using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Report a problem could leak or break: the attached log keeps a path, address, email, id or token; a page sends a
/// log the preview never showed and it leaves unscrubbed; the log goes with Attach logs off, or a contact nobody typed; an
/// empty message is sent; a field the service (cloud/reports/src/user-report.js) does not name is added; a contact carries
/// a line break; the service's answer is misread, so the player sees the wrong number or no word on a failure.
/// </summary>
public sealed class UserReportTests
{
    private const string UserName = "Erix";
    private const string Profile = @"C:\Users\Erix";
    private const string Machine = "DESKTOP-K3J9";
    private static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Log =
    [
        @"[2026-11-19T09:58:10.1230000Z] WARN Launch: Steam did not start Elden Ring (C:\Users\Erix\Games\Elden Ring\eldenring.exe) for Erix.",
        "[2026-11-19T09:58:11.0010000Z] ERROR Library: https://api.example.com/v1/library?token=abc123XYZ failed for 76561198012345678 on " + Machine + " at 10.0.0.12, mail pat@contoso.org",
        "",
    ];

    private static JsonObject Body(string message = "The library went blank", string? excerpt = "", string? contact = null, string? install = "5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13")
    {
        var json = UserReport.ToJson(message, excerpt, contact, "3.26.0", "10.0.26300.1", UserName, Profile, Machine, install);
        Assert.NotNull(json);
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public void ThePreviewIsScrubbedAsACrashReportIs()
    {
        var excerpt = UserReport.Excerpt(Log, Now, "3.26.0", "10.0.26300.1", UserName, Profile, Machine);
        Assert.Contains("Error (app): Report a problem", excerpt);
        Assert.Contains("Steam did not start", excerpt);
        foreach (var leak in new[] { "Erix", @"C:\", "api.example.com", "abc123XYZ", "76561198012345678", Machine, "10.0.0.12", "pat@contoso.org" })
            Assert.DoesNotContain(leak, excerpt);
    }

    [Fact]
    public void ALogThePageSendsIsScrubbedAgainBeforeItLeaves()
    {
        var body = Body(excerpt: @"[2026-11-19T09:58:10Z] WARN: read C:\Users\Erix\secret.txt and https://x.example/?t=1 for ada@example.com");
        var excerpt = body["excerpt"]!.GetValue<string>();
        foreach (var leak in new[] { @"C:\", "x.example", "ada@example.com", "Erix" }) Assert.DoesNotContain(leak, excerpt);
    }

    [Fact]
    public void OnlyTheServicesFieldsGoAndTheLogOnlyWhenAttached()
    {
        var full = Body(excerpt: "Error (app): Report a problem", contact: "ada on discord");
        Assert.Equal(["app", "version", "osBuild", "message", "excerpt", "contact", "installId"], full.Select(p => p.Key));
        Assert.Equal("launcher", full["app"]!.GetValue<string>());
        Assert.Equal("3.26.0", full["version"]!.GetValue<string>());
        Assert.Equal("10.0.26300.1", full["osBuild"]!.GetValue<string>());

        var bare = Body(excerpt: null, contact: "   ", install: null);
        Assert.Equal(["app", "version", "osBuild", "message"], bare.Select(p => p.Key));
    }

    [Fact]
    public void TheMessageAndContactAreTrimmedCappedAndOneLine()
    {
        Assert.Null(UserReport.ToJson("  \r\n ", "", null, "3.26.0", "10.0.26300.1", UserName, Profile, Machine, null));
        var body = Body(message: "  first\r\nsecond\u0007  " + new string('x', 5000), contact: "ada\r\n@example.com\u2028x");
        var message = body["message"]!.GetValue<string>();
        Assert.StartsWith("first\nsecond", message);
        Assert.DoesNotContain('\u0007', message);
        Assert.True(message.Length <= UserReport.MaxMessage);
        Assert.Equal("ada @example.com x", body["contact"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(202, """{"issue":123}""", true, 123, null)]
    [InlineData(202, """{"ref":"1a2b3c4d"}""", true, null, "1a2b3c4d")]
    [InlineData(202, """{"ref":"<script>"}""", true, null, null)]
    [InlineData(202, "not json", true, null, null)]
    [InlineData(429, "", false, null, null)]
    [InlineData(422, "", false, null, null)]
    [InlineData(503, "", false, null, null)]
    [InlineData(0, "", false, null, null)]
    public void TheAnswerIsReadForThePlayer(int status, string body, bool ok, int? issue, string? reference)
    {
        var result = UserReport.Read(status, body);
        Assert.Equal(ok, result.Ok);
        Assert.Equal(issue, result.Issue);
        Assert.Equal(reference, result.Ref);
        if (!ok) Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void ItPostsToTheReportService()
    {
        Assert.Equal("https://reports.getexo.app/v1/user-report", UserReport.Endpoint);
    }
}
