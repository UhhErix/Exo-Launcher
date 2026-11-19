using System.Net;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the one-time tracker.gg seed can come out wrong:
/// - a lifetime total is read from a playlist sum, or a split by mode counts a playlist twice;
/// - a private profile reads as an empty seed instead of private;
/// - a League answer is taken for Valorant (or the reverse) when both are fetched.
/// The profiles are fixtures in the shape tracker.gg's profile JSON has (segments with a type, metadata and stats). The stat
/// field names are the ones this code reads; they are not checked against a live profile from this session.
/// </summary>
public sealed class TrackerGgSeedTests
{
    private const string ValorantProfile = """
        {"data":{"segments":[
          {"type":"overview","metadata":{"name":"Overview"},"stats":{
            "timePlayed":{"value":6000,"displayType":"TimeMinutes"},
            "matchesPlayed":{"value":200},"matchesWon":{"value":100},"kd":{"value":1.12},
            "rank":{"value":12,"displayValue":"Gold 2"},"peakRank":{"value":15,"displayValue":"Platinum 1"}}},
          {"type":"playlist","metadata":{"name":"Competitive"},"stats":{"timePlayed":{"value":4000,"displayType":"TimeMinutes"}}},
          {"type":"playlist","metadata":{"name":"Unrated"},"stats":{"timePlayed":{"value":2000,"displayType":"TimeMinutes"}}},
          {"type":"agent","metadata":{"name":"Jett"},"stats":{"matchesPlayed":{"value":90}}},
          {"type":"agent","metadata":{"name":"Sage"},"stats":{"matchesPlayed":{"value":40}}},
          {"type":"agent","metadata":{"name":"Omen"},"stats":{"matchesPlayed":{"value":70}}},
          {"type":"agent","metadata":{"name":"Raze"},"stats":{"matchesPlayed":{"value":10}}}
        ]}}
        """;

    private const string PrivateProfile = """{"errors":[{"code":"PrivatePlayerProfile","message":"This profile is private."}]}""";

    [Fact]
    public async Task ALifetimeProfileGivesTheSeedWithItsSplit()
    {
        var handler = new FixedProfiles(url => url.AbsolutePath.Contains("/valorant/") ? (HttpStatusCode.OK, ValorantProfile) : (HttpStatusCode.NotFound, ""));

        var result = await TrackerGgPlaytime.LoadAsync("PlayerC#EUW", handler, CancellationToken.None);

        Assert.True(result.Ok);
        var valorant = result.Readings!["riot:valorant"];
        Assert.Equal(6000, valorant.Minutes);
        Assert.True(valorant.Lifetime);
        Assert.Equal(4000, valorant.Modes!["competitive"]);
        Assert.Equal(2000, valorant.Modes["unrated"]);
        Assert.DoesNotContain("riot:league_of_legends", result.Readings!.Keys);
    }

    [Fact]
    public async Task APrivateProfileIsPrivateAndGivesNoSeed()
    {
        var handler = new FixedProfiles(_ => (HttpStatusCode.OK, PrivateProfile));

        var result = await TrackerGgPlaytime.LoadAsync("PlayerC#EUW", handler, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.ProfilePrivate);
        Assert.Null(result.Readings);
    }

    [Fact]
    public void ADuplicatePlaylistKeepsItsLargerReadingOnce()
    {
        var json = """
            {"data":{"segments":[
              {"type":"playlist","metadata":{"name":"ARAM"},"stats":{"timePlayed":{"value":30,"displayType":"TimeMinutes"}}},
              {"type":"playlist","metadata":{"name":"ARAM"},"stats":{"timePlayed":{"value":45,"displayType":"TimeMinutes"}}}
            ]}}
            """;

        var modes = TrackerGgPlaytime.ParseModes(json, "riot:league_of_legends");

        Assert.Equal(45, Assert.Single(modes!, pair => pair.Key == "aram").Value);
        Assert.Single(modes!);
    }

    [Fact]
    public void RankedAndCompetitiveAreCountedOnce()
    {
        // Valorant's "Ranked" and "Competitive" are one queue: 4000 min is counted once, not 8000.
        var json = """
            {"data":{"segments":[
              {"type":"playlist","metadata":{"name":"Competitive"},"stats":{"timePlayed":{"value":4000,"displayType":"TimeMinutes"}}},
              {"type":"playlist","metadata":{"name":"Ranked"},"stats":{"timePlayed":{"value":4000,"displayType":"TimeMinutes"}}}
            ]}}
            """;

        var modes = TrackerGgPlaytime.ParseModes(json, "riot:valorant");

        Assert.Equal(4000, Assert.Single(modes!, pair => pair.Key == "competitive").Value);
        Assert.Single(modes!);
        Assert.Equal(4000, TrackerGgPlaytime.ParseReading(json, "riot:valorant")!.Value.Minutes);
    }

    [Fact]
    public void AProfileWithoutPlaylistsGivesNoModes()
    {
        var overviewOnly = """{"data":{"segments":[{"type":"overview","stats":{"timePlayed":{"value":60,"displayType":"TimeMinutes"}}}]}}""";

        Assert.Null(TrackerGgPlaytime.ParseModes(overviewOnly, "riot:valorant"));
        Assert.Null(TrackerGgPlaytime.ParseModes(PrivateProfile, "riot:valorant"));
    }

    /// <summary>
    /// tracker.gg may drop or rename any stat, or send a profile that is not an object at the root. Each reads as absent and
    /// the parsers never throw, so the seed still imports what it can.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"data\":42}")]
    [InlineData("{\"data\":[1,\"x\",null]}")]
    [InlineData("{\"data\":{\"segments\":[{\"type\":\"overview\",\"stats\":\"none\"}]}}")]
    [InlineData("{\"data\":{\"segments\":[{\"type\":\"overview\",\"stats\":{\"matches\":{\"value\":10},\"rank\":\"Gold\"}}]}}")]
    [InlineData("{\"data\":{\"segments\":[{\"type\":\"champion\",\"metadata\":\"x\",\"stats\":null}]}}")]
    public void AMissingOrRenamedStatNeverThrows(string json)
    {
        Assert.Null(Record.Exception(() => Assert.Null(TrackerGgPlaytime.ParseModes(json, "riot:valorant"))));
        Assert.Null(Record.Exception(() => Assert.Null(TrackerGgPlaytime.ParseReading(json))));
        Assert.Null(Record.Exception(() => Assert.False(TrackerGgPlaytime.IsPrivateProfile(json))));
    }

    /// <summary>
    /// Ways a blocked or oversized tracker.gg answer can go wrong: it is read as hours or as a private profile; a second
    /// client is tried to get past the block; or a huge body fills memory before it is refused.
    /// </summary>
    [Fact]
    public async Task ABlockedProfileSaysSoAndLeavesTheSeedEmpty()
    {
        var handler = new FixedProfiles(_ => (HttpStatusCode.Forbidden, "<html>blocked</html>"));

        var result = await TrackerGgPlaytime.LoadAsync("PlayerC#EUW", handler, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.ProfilePrivate);
        Assert.Equal(TrackerGgPlaytime.BlockedMessage, result.Message);
        Assert.Empty(result.Minutes);
    }

    /// <summary>What api.tracker.gg answered an app in November 2026: Cloudflare's managed challenge, HTTP 403, as HTML.</summary>
    private const string ChallengePage =
        "<!DOCTYPE html><html lang=\"en-US\"><head><title>Just a moment...</title></head><body>" +
        "<script>(function(){window._cf_chl_opt = {cType: 'managed',cZone: 'api.tracker.gg'};}());</script></body></html>";

    [Fact]
    public async Task ABrowserCheckSaysTrackerGgBlocksAppsAndAsksOnlyOnce()
    {
        var asked = 0;
        var handler = new CapturingHandler(_ => { asked++; return (HttpStatusCode.Forbidden, ChallengePage); });

        var result = await TrackerGgPlaytime.LoadAsync("PlayerC#EUW", handler, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.ProfilePrivate);
        Assert.Equal(TrackerGgPlaytime.BlockedMessage, result.Message);
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData(403, ChallengePage, "Blocked")]
    [InlineData(200, ChallengePage, "Blocked")]
    [InlineData(403, "<title>You've Been Blocked - Tracker Network</title>", "Blocked")]
    [InlineData(429, "", "Blocked")]
    [InlineData(403, PrivateProfile, "Json")]
    [InlineData(200, "{\"data\":{}}", "Json")]
    [InlineData(500, "<html>oops</html>", "Unanswered")]
    [InlineData(200, null, "Unanswered")]
    public void AnAnswerIsAProfileABlockOrNothing(int status, string? body, string kind) =>
        Assert.Equal(kind, TrackerGgPlaytime.Classify(status, body).Kind.ToString());

    [Fact]
    public async Task AnOversizedProfileIsNoAnswer()
    {
        var huge = "{\"data\":\"" + new string('x', 16 * 1024 * 1024 + 1) + "\"}";
        var handler = new FixedProfiles(_ => (HttpStatusCode.OK, huge));

        var result = await TrackerGgPlaytime.LoadAsync("PlayerC#EUW", handler, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(TrackerGgPlaytime.UnansweredMessage, result.Message);
        Assert.Empty(result.Minutes);
    }

    /// <summary>
    /// The request says who asks and nothing more: a browser's user agent or spoofed Origin and Referer would make Exo
    /// pass as tracker.gg's own site, which it must not do to get past a block.
    /// </summary>
    [Fact]
    public async Task TheRequestIdentifiesExoAndPretendsToBeNothing()
    {
        HttpRequestMessage? seen = null;
        var handler = new CapturingHandler(request => { seen = request; return (HttpStatusCode.Forbidden, ""); });

        await TrackerGgPlaytime.LoadAsync("PlayerC#EUW", handler, CancellationToken.None);

        Assert.NotNull(seen);
        var userAgent = Assert.Single(seen!.Headers.UserAgent).Product!.Name;
        Assert.Equal("ExoLauncher", userAgent);
        Assert.DoesNotContain(seen.Headers, header => header.Key is "Origin" or "Referer" or "Accept-Language");
        Assert.DoesNotContain(seen.Headers.UserAgent, agent => agent.ToString().Contains("Chrome", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FixedProfiles(Func<Uri, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
