using System.Net;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// CoverCacheClient — asks Exo's shared cover cache (cloud/covers) for a game's portrait before the Launcher's own
/// lookups. Ways this could fail silently:
/// - the request sends more than the title and the store id (anything else is a leak; the cache needs nothing more);
/// - a cache answer with a URL outside the artwork allowlist is downloaded (the cache is a server we don't fully trust);
/// - a broken or error response is read as "no art" and the Launcher stops looking, so the game keeps its icon;
/// - an unreachable cache is retried on every warm, hammering it and slowing the library (it backs off for two minutes);
/// - one odd title, a store the cache could not reach, or a firewall's 403 switches the cache off for everyone's next lookups;
/// - the Launcher gives up before the cache's own worst case, so a slow store looks like an outage.
/// </summary>
[Collection("CoverCacheStatics")]
public sealed class CoverCacheClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestCarriesOnlyTitleStoreAndId()
    {
        var uri = CoverCacheClient.BuildUri("Hades II", "steam", "1145360");
        Assert.Equal("https://covers.getexo.app/v1/cover?title=Hades%20II&store=steam&id=1145360", uri.AbsoluteUri);
    }

    [Fact]
    public void TitleOnlyRequestOmitsStoreAndId()
    {
        var uri = CoverCacheClient.BuildUri("osu!", store: null, id: null);
        Assert.Equal("https://covers.getexo.app/v1/cover?title=osu!", uri.AbsoluteUri.Replace("%21", "!"));
        Assert.DoesNotContain("store=", uri.Query);
        Assert.DoesNotContain("id=", uri.Query);
    }

    [Fact]
    public void AnApprovedArtUrlIsAHit()
    {
        var answer = CoverCacheClient.ParseBody("{\"ok\":true,\"url\":\"https://cdn.cloudflare.steamstatic.com/steam/apps/1145360/library_600x900.jpg\"}");
        Assert.Equal(CoverCacheClient.Outcome.Hit, answer.Outcome);
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/steam/apps/1145360/library_600x900.jpg", answer.Url);
    }

    [Fact]
    public void AUrlOutsideTheAllowlistIsNotAMiss()
    {
        // Another host, or another port on a good host: nothing is downloaded, and nothing is said about the game\"s art.
        foreach (var url in new[] { "https://images.example.com/cover.jpg", "https://cdn.cloudflare.steamstatic.com:8443/steam/apps/1/library_600x900.jpg" })
            Assert.Equal(CoverCacheClient.Outcome.Unknown, CoverCacheClient.ParseBody("{\"ok\":true,\"url\":\"" + url + "\"}").Outcome);
    }

    [Fact]
    public void AMissFromTheCacheIsAMissAndNothingMore()
    {
        // Never final: the PC goes on to its own searches whether or not a store could be asked.
        Assert.Equal(CoverCacheClient.Outcome.Miss, CoverCacheClient.ParseBody("{\"ok\":false,\"miss\":true}").Outcome);
        Assert.Equal(CoverCacheClient.Outcome.Miss, CoverCacheClient.ParseBody("{\"ok\":false,\"miss\":true,\"partial\":true}").Outcome);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("{\"ok\":true}")]
    [InlineData("")]
    public void AMalformedBodyIsUnreachableNotNoArt(string body)
    {
        Assert.Equal(CoverCacheClient.Outcome.Unreachable, CoverCacheClient.ParseBody(body).Outcome);
    }

    [Fact]
    public void AnOutageBacksOffBrieflyAndTheCacheMayAskForLonger()
    {
        var state = new CoverCacheClient.Backoff();
        Assert.False(state.Active(T0));
        state.Note(T0);
        Assert.True(state.Active(T0.AddMinutes(1)));
        Assert.False(state.Active(T0.AddMinutes(2)));

        var asked = new CoverCacheClient.Backoff();
        asked.Note(T0, TimeSpan.FromMinutes(5));
        Assert.True(asked.Active(T0.AddMinutes(4)));
        Assert.False(asked.Active(T0.AddMinutes(5)));

        var greedy = new CoverCacheClient.Backoff();
        greedy.Note(T0, TimeSpan.FromHours(6)); // never switched off for hours by a header
        Assert.False(greedy.Active(T0.AddMinutes(15)));
    }

    [Fact]
    public void TheTimeoutIsLongerThanTheWorkersOwnBudget()
    {
        // cloud/covers/src/limits.json is the one number: the Worker reads it as its total time, and this reads it as the Worker's.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloud", "covers", "src", "limits.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "cloud", "covers", "src", "limits.json")));
        var budget = TimeSpan.FromMilliseconds(doc.RootElement.GetProperty("totalBudgetMs").GetInt32());
        Assert.True(CoverCacheClient.RequestTimeout > budget);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"message\":\"Not found.\"}", "Unknown")]
    [InlineData("{\"ok\":false,\"miss\":true}", "Miss")]
    [InlineData("{\"ok\":false,\"miss\":true,\"partial\":true}", "Miss")]
    public void ABodyThatIsNotAMissIsNotOne(string body, string expected)
    {
        Assert.Equal(expected, CoverCacheClient.ParseBody(body).Outcome.ToString());
    }

    private static async Task<(CoverCacheClient.Answer Answer, int Calls, CoverCacheClient.Backoff Backoff)> Ask(
        HttpStatusCode status, string body = "", Action<HttpResponseMessage>? decorate = null)
    {
        var calls = 0;
        var backoff = new CoverCacheClient.Backoff();
        var http = ExoHttp.Create(TimeSpan.FromSeconds(5), new StubHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            decorate?.Invoke(response);
            return response;
        }));
        var game = new GameEntry { Id = "local:odd", Title = "!!!", Store = StoreKind.Local };
        var answer = await CoverCacheClient.LookupAsync(http, game, TestContext.Current.CancellationToken, T0, backoff);
        return (answer, calls, backoff);
    }

    [Fact]
    public async Task A400ForOneOddTitleLeavesTheCacheOnAndTheLocalSearchesRunning()
    {
        var (answer, _, backoff) = await Ask(HttpStatusCode.BadRequest, "{\"ok\":false,\"message\":\"Send a title or a store id.\"}");
        Assert.Equal(CoverCacheClient.Outcome.Unknown, answer.Outcome);
        Assert.False(backoff.Active(T0));
    }

    [Fact]
    public async Task TheCachesMissAndAPartialMissAreMissesThatLeaveTheCacheOn()
    {
        foreach (var body in new[] { "{\"ok\":false,\"miss\":true}", "{\"ok\":false,\"miss\":true,\"partial\":true}" })
        {
            var (answer, _, backoff) = await Ask(HttpStatusCode.NotFound, body);
            Assert.Equal(CoverCacheClient.Outcome.Miss, answer.Outcome);
            Assert.False(backoff.Active(T0));
        }

        var elsewhere = await Ask(HttpStatusCode.NotFound, "<html>no such page</html>"); // not the cache\"s JSON: not a miss
        Assert.Equal(CoverCacheClient.Outcome.Unknown, elsewhere.Answer.Outcome);
        Assert.False(elsewhere.Backoff.Active(T0));
    }

    [Fact]
    public async Task AFirewallsRefusalIsNeitherAMissNorAnOutage()
    {
        var (answer, _, backoff) = await Ask(HttpStatusCode.Forbidden, "blocked");
        Assert.Equal(CoverCacheClient.Outcome.Unknown, answer.Outcome);
        Assert.False(backoff.Active(T0));
    }

    [Fact]
    public async Task ARateLimitOrA503SwitchesTheCacheOffAndTheRetryAfterIsHonoured()
    {
        var limited = await Ask(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new(TimeSpan.FromMinutes(5)));
        Assert.Equal(CoverCacheClient.Outcome.Unreachable, limited.Answer.Outcome);
        Assert.True(limited.Backoff.Active(T0.AddMinutes(4)));
        Assert.False(limited.Backoff.Active(T0.AddMinutes(5)));

        var down = await Ask(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(CoverCacheClient.Outcome.Unreachable, down.Answer.Outcome);
        Assert.False(down.Backoff.Active(T0.AddMinutes(2)));
    }

    [Fact]
    public async Task ATimeoutIsAnOutage()
    {
        // A cache that never answers: the request is cut off by the timeout (a short one here), not by the caller.
        var backoff = new CoverCacheClient.Backoff();
        var http = ExoHttp.Create(TimeSpan.FromSeconds(30), new HangingHandler());
        var game = new GameEntry { Id = "local:x", Title = "Hades", Store = StoreKind.Local };
        var answer = await CoverCacheClient.LookupAsync(http, game, TestContext.Current.CancellationToken, T0, backoff, TimeSpan.FromMilliseconds(150));
        Assert.Equal(CoverCacheClient.Outcome.Unreachable, answer.Outcome);
        Assert.True(backoff.Active(T0.AddMinutes(1)));
    }

    [Fact]
    public async Task ANetworkErrorIsAnOutageToo()
    {
        var backoff = new CoverCacheClient.Backoff();
        var http = ExoHttp.Create(TimeSpan.FromSeconds(5), new StubHandler(_ => throw new HttpRequestException("no route")));
        var game = new GameEntry { Id = "local:x", Title = "Hades", Store = StoreKind.Local };
        var answer = await CoverCacheClient.LookupAsync(http, game, TestContext.Current.CancellationToken, T0, backoff);
        Assert.Equal(CoverCacheClient.Outcome.Unreachable, answer.Outcome);
        Assert.True(backoff.Active(T0.AddMinutes(1)));
    }

    [Fact]
    public async Task AGameWithoutATitleNeverAsksTheCache()
    {
        var calls = 0;
        var http = ExoHttp.Create(TimeSpan.FromSeconds(5), new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var answer = await CoverCacheClient.LookupAsync(http, new GameEntry { Id = "local:x", Title = "  ", Store = StoreKind.Local }, TestContext.Current.CancellationToken, T0);
        Assert.Equal(CoverCacheClient.Outcome.Miss, answer.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A500IsUnreachableAndStartsTheBackoff()
    {
        var calls = 0;
        var backoff = new CoverCacheClient.Backoff();
        var http = ExoHttp.Create(TimeSpan.FromSeconds(5), new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }));
        var game = new GameEntry { Id = "local:osu", Title = "osu!", Store = StoreKind.Local };

        var first = await CoverCacheClient.LookupAsync(http, game, TestContext.Current.CancellationToken, T0, backoff);
        var second = await CoverCacheClient.LookupAsync(http, game, TestContext.Current.CancellationToken, T0.AddMinutes(1), backoff);

        Assert.Equal(CoverCacheClient.Outcome.Unreachable, first.Outcome);
        Assert.Equal(CoverCacheClient.Outcome.Unreachable, second.Outcome);
        Assert.Equal(1, calls); // the second warm inside the backoff never reached the network
    }

    /// <summary>One warm of a game nobody has art for, with a cache that answers as given. Its own state: the shared backoff is cleared first and the title is its own.</summary>
    private static async Task<List<string>> HostsAskedByAWarm(string cacheReply)
    {
        CoverCacheClient.Shared.Reset();
        var unique = Guid.NewGuid().ToString("N");
        var hosts = new List<string>();
        var http = ExoHttp.Create(TimeSpan.FromSeconds(5), new StubHandler(request =>
        {
            lock (hosts) hosts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "covers.getexo.app"
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(cacheReply) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var game = new GameEntry { Id = "local:warm-" + unique, Title = "Zzyzx Quest " + unique[..8], Store = StoreKind.Local, Installed = true };
        await CoverArtService.WarmOneAsync(http, game, CoverArtService.ArtworkWarmIntent.UserRefetch, TestContext.Current.CancellationToken);
        CoverCacheClient.Shared.Reset();
        return hosts;
    }

    [Theory]
    [InlineData("{\"ok\":false,\"miss\":true}")]
    [InlineData("{\"ok\":false,\"miss\":true,\"partial\":true}")]
    public async Task AfterACacheMissThisPcStillSearchesSteamAndEpic(string reply)
    {
        // A miss from the cache is never final for the PC: its own searches run (CoverMissMemory stops the repeats).
        var hosts = await HostsAskedByAWarm(reply);
        Assert.Contains("covers.getexo.app", hosts);
        Assert.True(hosts.Contains("steamcommunity.com") || hosts.Contains("store.steampowered.com"), string.Join(", ", hosts));
        Assert.Contains(hosts, host => host.EndsWith("epicgames.com", StringComparison.Ordinal));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
