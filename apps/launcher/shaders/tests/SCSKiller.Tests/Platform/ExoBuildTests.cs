using System.Net;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// The Exo build's local-only rules (App/ExoBuild.cs): the hosts it never contacts and the request gate
// (anti-cheat decisions are upstream's: GameFiles) in RouteFailover. The gate is tested with localOnly on, whatever the build's constant.
public class ExoBuildTests
{
    sealed class CountingHandler : HttpMessageHandler
    {
        public readonly List<Uri> Sent = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Theory]
    [InlineData("https://api.scskiller.com/healthz")]
    [InlineData("https://api.scskiller.io/")]
    [InlineData("https://dl.scskiller.io/packages.json")]
    [InlineData("https://scskiller.com/")]
    [InlineData("https://www.patreon.com/oauth2/authorize")]
    public void SCSKiller_and_Patreon_hosts_are_blocked(string url) => Assert.True(ExoBuild.BlockedHost(new Uri(url)));

    [Theory]
    [InlineData("https://github.com/WorkingRobot/OodleUE/releases/download/x/gcc-x64-release.zip")]
    [InlineData("https://api.nuget.org/v3-flatcontainer/x.nupkg")]
    [InlineData("https://scskiller.com.evil.example/")]
    public void Other_hosts_are_not_blocked(string url) => Assert.False(ExoBuild.BlockedHost(new Uri(url)));

    [Fact]
    public async Task A_local_only_client_sends_nothing_to_SCSKiller_or_Patreon()
    {
        var inner = new CountingHandler();
        var client = new HttpMessageInvoker(new RouteFailover(inner, localOnly: true));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.scskiller.com/healthz"), default));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.scskiller.io/community"), default));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://www.patreon.com/api/oauth2/v2/identity"), default));
        Assert.Empty(inner.Sent);

        using var ok = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://github.com/x/releases/download/a.zip"), default);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Single(inner.Sent);   // other hosts (the codec downloads) pass through
    }

    [Fact]
    public async Task Without_local_only_the_routes_are_tried_as_before()
    {
        var inner = new CountingHandler();
        var client = new HttpMessageInvoker(new RouteFailover(inner, localOnly: false));
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.scskiller.com/healthz"), default);
        Assert.Single(inner.Sent);
    }
}
