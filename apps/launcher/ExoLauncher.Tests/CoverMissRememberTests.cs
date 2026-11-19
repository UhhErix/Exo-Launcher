using System.Net;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// A game is remembered as having no cover (for a day) only when a store answered with no match. A warm whose request
/// failed on the network (offline, timed out, a server error) says nothing about the game, so it is not remembered.
/// Ways this could fail: an unreachable store is remembered as "no cover" and the game is not searched for a day; a
/// store's 404 is treated as a failure and the game is searched on every pass; the flag leaks from one warm into the next.
/// </summary>
public sealed class CoverMissRememberTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }

    private static (HttpClient Client, CoverNetworkWatch.WarmFlag Flag) Warm(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer)
    {
        var flag = new CoverNetworkWatch.WarmFlag();
        CoverNetworkWatch.Current.Value = flag;
        return (ExoHttp.Create(TimeSpan.FromSeconds(5), new CoverNetworkWatch(new ScriptedHandler(answer))), flag);
    }

    [Fact]
    public async Task AStoreThatIsOfflineFailsTheWarm()
    {
        var (client, flag) = Warm(_ => throw new HttpRequestException("offline"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://store.example/search", TestContext.Current.CancellationToken));

        Assert.True(flag.Failed);
    }

    [Fact]
    public async Task AStoreThatTimesOutFailsTheWarm()
    {
        var (client, flag) = Warm(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://store.example/search", TestContext.Current.CancellationToken));

        Assert.True(flag.Failed);
    }

    [Fact]
    public async Task AStoreServerErrorFailsTheWarm()
    {
        var (client, flag) = Warm(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        using var response = await client.GetAsync("https://store.example/search", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(flag.Failed);
    }

    [Fact]
    public async Task AStoreThatAnswersNoMatchIsNotAFailure()
    {
        var (client, flag) = Warm(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        using var response = await client.GetAsync("https://store.example/poster", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(flag.Failed);
    }

    [Fact]
    public async Task AnAnsweredWarmIsNotFlaggedWithoutAFailure()
    {
        var (client, flag) = Warm(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await client.GetAsync("https://store.example/search", TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(flag.Failed);
    }
}
