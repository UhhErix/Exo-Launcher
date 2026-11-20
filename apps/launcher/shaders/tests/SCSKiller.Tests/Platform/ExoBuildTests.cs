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

    [Fact]
    public void Bundled_ExoShaders_engine_allows_live_data_only_in_Exo_builds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var prevDir = Environment.GetEnvironmentVariable("SCSKILLER_DATA_DIR");
        var prevAllow = Environment.GetEnvironmentVariable("SCSKILLER_ALLOW_LIVE_DATA");
        var root = Path.Combine(Path.GetTempPath(), "exo-build-test-" + Guid.NewGuid().ToString("N"));
        var engineDir = Path.Combine(root, "engine");
        var cliDir = Path.Combine(engineDir, "cli");
        Directory.CreateDirectory(cliDir);
        try
        {
            Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", null);
            Environment.SetEnvironmentVariable("SCSKILLER_ALLOW_LIVE_DATA", null);

            Assert.True(AppStore.RefusesLiveData(engineDir, exoBuild: true));
            File.WriteAllText(Path.Combine(engineDir, "ExoShaders.Service.exe"), "");

            Assert.False(AppStore.RefusesLiveData(engineDir, exoBuild: true));
            Assert.False(AppStore.RefusesLiveData(cliDir, exoBuild: true));
            Assert.True(AppStore.RefusesLiveData(engineDir, exoBuild: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", prevDir);
            Environment.SetEnvironmentVariable("SCSKILLER_ALLOW_LIVE_DATA", prevAllow);
            Directory.Delete(root, recursive: true);
        }
    }
}
