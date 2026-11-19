using System.Net;
using ExoLauncher.Helpers;
using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Tests;

/// <summary>
/// The owned and family calls carry the account's access token in the query string. A failed call must not leave that
/// token in any text that can reach a log, a crash report or telemetry: the exception a failed call throws has no URL in
/// it, and the shared scrubber (AppLog.Redact, which the crash report and the app log both use) removes the token if one
/// does get into a line.
/// </summary>
public sealed class SteamTokenLogScrubTests
{
    private const string Token = "SECRET" + "BODYab" + "c123XY" + "Z";

    /// <summary>A transport that fails the way .NET does: its message names the request URL, token included.</summary>
    private sealed class FailingTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection to " + request.RequestUri + " failed"));
    }

    private static SteamWebApi Api() => new(ExoHttp.Create(TimeSpan.FromSeconds(5), new FailingTransport()), "https://api.test");

    [Fact]
    public async Task AFailedOwnedGamesCallDoesNotCarryTheTokenInItsException()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Api().OwnedGamesAsync("eyJhbG" + "ciOiJS" + "UzI1Ni" + "J9." + Token + ".sig", 76561197960265729, TestContext.Current.CancellationToken));

        var text = error.ToString();
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("access_token", text);
        Assert.DoesNotContain("GetOwnedGames", text);
    }

    [Fact]
    public async Task AFailedFamilyCallDoesNotCarryTheTokenInItsException()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Api().FamilyGroupAsync("eyJhbG" + "ciOiJS" + "UzI1Ni" + "J9." + Token + ".sig", 76561197960265729, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public void AFailedRequestLoggedWithItsUrlLosesTheTokenInTheLogLine()
    {
        var logged = "Steam request failed: GET https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?access_token=eyJhbG" + "ciOiJS" + "UzI1Ni" + "J9." + Token + ".sig&steamid=1&format=json";

        var line = AppLog.Redact(logged);

        Assert.DoesNotContain(Token, line);
        Assert.DoesNotContain("eyJ", line);
        Assert.Contains("[redacted]", line);
    }

    [Fact]
    public void TheSteamLoginCookieIsRedactedInLogLines()
    {
        var line = AppLog.Redact("Cookie sent: steamLoginSecure=76561197960265729%7C%7CeyJhbGciOi." + Token + "; sessionid=abc");

        Assert.DoesNotContain(Token, line);
        Assert.DoesNotContain("76561197960265729%7C", line);
    }

    [Fact]
    public void TokenFieldsInJsonAreRedacted()
    {
        var line = AppLog.Redact("""{"refresh_token":"eyJhbGciOi.""" + Token + """.sig","steamLoginSecure":"x"}""");

        Assert.DoesNotContain(Token, line);
    }
}
