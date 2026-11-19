using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// A change made while no account is signed in (a play session, a Riot ledger line, a cover choice) waits on this PC and joins
/// the account queue at sign-in, once per item id. Ways this could fail: the change is dropped because no account was signed in
/// (the offline play never reaches the account); two changes to one item enter the queue twice; the waiting file is kept after
/// it has been queued, so it is queued again on every start. Fixture ids match no real play or account.
/// </summary>
public sealed class AccountSyncPendingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-sync-pending-" + Guid.NewGuid().ToString("N"));

    public AccountSyncPendingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class Empty : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task AChangeMadeSignedOutIsQueuedOnceWhenSignedIn()
    {
        AccountSession? current = null;
        var host = new AccountSyncHost(_dir, new ExoAccountClient(new Empty()), () => current, _ => { });

        host.Write("playtime", "session-1", "{\"minutes\":30}");
        host.Write("playtime", "session-1", "{\"minutes\":30}");

        Assert.True(File.Exists(Path.Combine(_dir, AccountSyncHost.PendingFile)));
        current = new AccountSession("token-value", "acc-1", "person@example.com");
        await host.SyncAsync(CancellationToken.None);

        var saved = AccountSyncStore.Load(_dir, "acc-1");
        Assert.Contains("playtime/session-1", saved.Dirty);
        Assert.Single(saved.Items.Keys, key => key == "playtime/session-1");
        Assert.False(File.Exists(Path.Combine(_dir, AccountSyncHost.PendingFile)));
    }
}
