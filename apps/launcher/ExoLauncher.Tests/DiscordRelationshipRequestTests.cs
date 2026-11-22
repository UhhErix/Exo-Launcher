using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

[Description("""
    Discord relationship request shape, context headers, status logging, and user-facing error lines, which fail silently
    until a live user clicks Accept, Add Friend, Block, or Remove:
    - X-Context-Properties omitted or wrong location JSON/base64, causing Discord's anti-abuse checks to answer 400;
    - Accept or stranger-confirm body missing confirm_stranger_request or sending the wrong boolean;
    - Add-friend or block body missing discriminator: null or type: 2, or Remove/Ignore sending an unexpected body;
    - X-Super-Properties missing on per-request headers or lacking current web client fields;
    - Status error log dropping Discord's numeric error code (or leaking error messages, captcha keys, or user snowflakes);
    - Captcha or account verification 400/403 answers falling through to raw jargon instead of telling the user what to do.
    """)]
public sealed class DiscordRelationshipRequestTests
{
    private const string SampleUserId = "123456789012345678";

    private static JsonElement DecodeBase64Json(string headerValue)
    {
        var bytes = Convert.FromBase64String(headerValue);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static JsonElement ReadJsonBody(HttpRequestMessage request)
    {
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
        var raw = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static string RequireHeader(HttpRequestMessage request, string name)
    {
        Assert.True(request.Headers.TryGetValues(name, out var values), $"Missing header {name}");
        return Assert.Single(values);
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void AcceptRequestSendsFriendsContextAndUnconfirmedFlag()
    {
        using var request = DiscordRest.BuildRequest(DiscordRest.AcceptFriendRequest(SampleUserId, confirmStranger: false));

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships/{SampleUserId}", request.RequestUri?.AbsoluteUri);

        var contextHeader = RequireHeader(request, "X-Context-Properties");
        Assert.Equal("eyJsb2NhdGlvbiI6IkZyaWVuZHMifQ==", contextHeader);
        Assert.Equal("Friends", DecodeBase64Json(contextHeader).GetProperty("location").GetString());

        var body = ReadJsonBody(request);
        Assert.True(body.TryGetProperty("confirm_stranger_request", out var flag));
        Assert.Equal(JsonValueKind.False, flag.ValueKind);
    }

    [Fact]
    public void ConfirmStrangerRequestSendsFriendsContextAndConfirmedFlag()
    {
        using var request = DiscordRest.BuildRequest(DiscordRest.AcceptFriendRequest(SampleUserId, confirmStranger: true));

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships/{SampleUserId}", request.RequestUri?.AbsoluteUri);

        var contextHeader = RequireHeader(request, "X-Context-Properties");
        Assert.Equal("eyJsb2NhdGlvbiI6IkZyaWVuZHMifQ==", contextHeader);
        Assert.Equal("Friends", DecodeBase64Json(contextHeader).GetProperty("location").GetString());

        var body = ReadJsonBody(request);
        Assert.True(body.TryGetProperty("confirm_stranger_request", out var flag));
        Assert.Equal(JsonValueKind.True, flag.ValueKind);
    }

    [Fact]
    public void AddFriendRequestSendsAddFriendContextAndNullDiscriminator()
    {
        using var request = DiscordRest.BuildRequest(DiscordRest.AddFriendRequest("someone"));

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships", request.RequestUri?.AbsoluteUri);

        var contextHeader = RequireHeader(request, "X-Context-Properties");
        Assert.Equal("eyJsb2NhdGlvbiI6IkFkZCBGcmllbmQifQ==", contextHeader);
        Assert.Equal("Add Friend", DecodeBase64Json(contextHeader).GetProperty("location").GetString());

        var body = ReadJsonBody(request);
        Assert.Equal("someone", body.GetProperty("username").GetString());
        Assert.True(body.TryGetProperty("discriminator", out var disc));
        Assert.Equal(JsonValueKind.Null, disc.ValueKind);
    }

    [Fact]
    public void BlockRequestSendsContextMenuLocationAndTypeTwo()
    {
        using var request = DiscordRest.BuildRequest(DiscordRest.BlockRequest(SampleUserId));

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships/{SampleUserId}", request.RequestUri?.AbsoluteUri);

        var contextHeader = RequireHeader(request, "X-Context-Properties");
        Assert.Equal("eyJsb2NhdGlvbiI6IkNvbnRleHQgTWVudSJ9", contextHeader);
        Assert.Equal("Context Menu", DecodeBase64Json(contextHeader).GetProperty("location").GetString());

        var body = ReadJsonBody(request);
        Assert.Equal(2, body.GetProperty("type").GetInt32());
    }

    [Fact]
    public void RemoveAndIgnoreRequestsSendExpectedContextWithoutBody()
    {
        using var remove = DiscordRest.BuildRequest(DiscordRest.RemoveRelationshipRequest(SampleUserId));
        Assert.Equal(HttpMethod.Delete, remove.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships/{SampleUserId}", remove.RequestUri?.AbsoluteUri);
        Assert.Equal("Friends", DecodeBase64Json(RequireHeader(remove, "X-Context-Properties")).GetProperty("location").GetString());
        Assert.Null(remove.Content);

        using var ignore = DiscordRest.BuildRequest(DiscordRest.IgnoreRequest(SampleUserId, ignore: true));
        Assert.Equal(HttpMethod.Put, ignore.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships/{SampleUserId}/ignore", ignore.RequestUri?.AbsoluteUri);
        Assert.Equal("Context Menu", DecodeBase64Json(RequireHeader(ignore, "X-Context-Properties")).GetProperty("location").GetString());
        Assert.Null(ignore.Content);

        using var unignore = DiscordRest.BuildRequest(DiscordRest.IgnoreRequest(SampleUserId, ignore: false));
        Assert.Equal(HttpMethod.Delete, unignore.Method);
        Assert.Equal($"{DiscordRest.Api}users/@me/relationships/{SampleUserId}/ignore", unignore.RequestUri?.AbsoluteUri);
        Assert.Equal("Context Menu", DecodeBase64Json(RequireHeader(unignore, "X-Context-Properties")).GetProperty("location").GetString());
        Assert.Null(unignore.Content);
    }

    [Fact]
    public void EveryBuiltRequestCarriesCurrentSuperPropertiesHeader()
    {
        using var request = DiscordRest.BuildRequest(DiscordRest.AcceptFriendRequest(SampleUserId, confirmStranger: false));
        var props = DecodeBase64Json(RequireHeader(request, "X-Super-Properties"));

        Assert.Equal("Windows", props.GetProperty("os").GetString());
        Assert.Equal("Chrome", props.GetProperty("browser").GetString());
        Assert.Equal("stable", props.GetProperty("release_channel").GetString());
        Assert.Equal(DiscordRest.Build, props.GetProperty("client_build_number").GetInt32());
        Assert.Equal("", props.GetProperty("device_model_name").GetString());
        Assert.Equal("focused", props.GetProperty("client_app_state").GetString());
        Assert.True(Guid.TryParse(props.GetProperty("client_launch_id").GetString(), out _));
        Assert.True(Guid.TryParse(props.GetProperty("client_heartbeat_session_id").GetString(), out _));
    }

    [Fact]
    public void StatusLogIncludesNumericErrorCodeWithoutLeakingMessageOrIds()
    {
        var line = DiscordRest.FormatStatusLog(
            HttpMethod.Put,
            $"users/@me/relationships/{SampleUserId}",
            HttpStatusCode.BadRequest,
            Json("""{"code": 50035, "message": "Invalid Form Body for secret_user"}"""));

        Assert.Equal("Discord client: PUT users/@me/relationships/:id answered 400 (code 50035).", line);
        Assert.DoesNotContain(SampleUserId, line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_user", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid Form Body", line, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusLogMarksCaptchaAndHandlesMissingCodeCleanly()
    {
        var captchaLine = DiscordRest.FormatStatusLog(
            HttpMethod.Put,
            $"users/@me/relationships/{SampleUserId}",
            HttpStatusCode.BadRequest,
            Json("""{"captcha_key": ["captcha-required"], "captcha_sitekey": "site-secret"}"""));

        Assert.Equal("Discord client: PUT users/@me/relationships/:id answered 400 (captcha).", captchaLine);
        Assert.DoesNotContain("site-secret", captchaLine, StringComparison.Ordinal);
        Assert.DoesNotContain("captcha-required", captchaLine, StringComparison.Ordinal);

        var plainLine = DiscordRest.FormatStatusLog(
            HttpMethod.Put,
            $"users/@me/relationships/{SampleUserId}",
            HttpStatusCode.BadRequest,
            null);

        Assert.Equal("Discord client: PUT users/@me/relationships/:id answered 400.", plainLine);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"captcha_key": ["captcha-required"]}""", "check you're human")]
    [InlineData(HttpStatusCode.Forbidden, """{"code": 40002, "message": "You need to verify your account"}""", "verify your email")]
    [InlineData(HttpStatusCode.BadRequest, """{"code": 70007, "message": "Phone verification required"}""", "verify your phone")]
    [InlineData(HttpStatusCode.Forbidden, """{"code": 40068, "message": "Quarantined"}""", "limited friend requests")]
    [InlineData(HttpStatusCode.Forbidden, """{"code": 340007, "message": "Limited"}""", "limited friend requests")]
    [InlineData(HttpStatusCode.Forbidden, """{"code": 80000, "message": "Incoming disabled"}""", "isn't accepting friend requests")]
    [InlineData(HttpStatusCode.BadRequest, """{"code": 80004, "message": "No users with DiscordTag exist"}""", "No Discord account matches")]
    [InlineData(HttpStatusCode.BadRequest, """{"code": 80007, "message": "Already friends"}""", "already friends")]
    public void ErrorLineExplainsCaptchaAndRelationshipVerificationPlainly(HttpStatusCode status, string body, string expectedSnippet)
    {
        var line = DiscordRest.ErrorLine(status, Json(body));
        Assert.Contains(expectedSnippet, line, StringComparison.OrdinalIgnoreCase);
    }
}
