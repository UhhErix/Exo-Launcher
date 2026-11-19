using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// A pulled page is applied item by item, and a refused pull waits. Ways this could fail: a malformed Xbox seed (a missing name)
/// throws out of the page, so every item after it, here the settings, is never applied; a 429 with Retry-After is retried at once,
/// hammering the service and keeping a signed-out or refused PC busy. Fixture values match no real account or game.
/// </summary>
public sealed class AccountSyncApplyTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-sync-apply-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public AccountSyncApplyTests()
    {
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);
        PlaytimeService.ForgetLoaded();
    }

    public void Dispose()
    {
        AccountSyncHooks.Attach(null);
        PlaytimeService.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_data, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void ABadSeedDoesNotStopTheSettingsAfterIt()
    {
        var settings = new SettingsService();
        settings.Load();
        // An Xbox hours seed with no name: it cannot be applied.
        var badSeed = new AccountSyncHost.PulledItem("playtime-seed", "seed-1", "{\"title\":\"1234\",\"minutes\":60}", IsNew: false);
        var goodSettings = new AccountSyncHost.PulledItem("settings", AccountSyncMap.SettingsId, "{\"SortMode\":\"played\"}", IsNew: false);

        AccountSyncApply.Pulled(badSeed, settings);
        AccountSyncApply.Pulled(goodSettings, settings);

        Assert.Equal("played", settings.Current.SortMode);
    }

    [Fact]
    public async Task ARateLimitedPullWaitsForItsRetryAfter()
    {
        var accountDir = Path.Combine(_data, "account");
        Directory.CreateDirectory(accountDir);
        var session = new AccountSession("token-value", "acc-1", "person@example.com");
        var answer = new CountingAnswer();
        var host = new AccountSyncHost(accountDir, new ExoAccountClient(answer), () => session, _ => { });

        await host.SyncAsync(CancellationToken.None);
        await host.SyncAsync(CancellationToken.None);

        Assert.Equal(1, answer.Pulls);
    }

    private sealed class CountingAnswer : HttpMessageHandler
    {
        public int Pulls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Pulls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"slow-down\"}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return Task.FromResult(response);
        }
    }
}
