using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// A play session a crash left open is counted when playtime.json loads. It must reach the account too, as a session that ended
/// normally does: otherwise another PC never sees those minutes. Ways this could fail: the recovered session is credited on
/// this PC only (no sync entry is queued); it is queued but not marked counted, so the account's copy comes back and is
/// credited a second time. Fixture game and times match no real library entry.
/// </summary>
public sealed class PlaytimeRecoveredSyncTests : IDisposable
{
    private const string Crashed = "steam:999999942";
    private const string Other = "steam:999999943";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-recovered-sync-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public PlaytimeRecoveredSyncTests()
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
    public void ACrashRecoveredSessionIsQueuedForTheAccount()
    {
        var now = DateTimeOffset.UtcNow;
        // Left open by a crash: started two hours ago, last seen an hour ago (60 minutes).
        var stamp = $"{{\"minutes\":{{}},\"activeSessions\":{{\"{Crashed}\":{{\"startUtc\":\"{now.AddHours(-2):O}\",\"checkpointUtc\":\"{now.AddHours(-1):O}\"}}}}}}";
        File.WriteAllText(Path.Combine(_data, "playtime.json"), stamp);
        var accountDir = Path.Combine(Path.GetTempPath(), "exo-recovered-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(accountDir);
        var session = new AccountSession("token-value", "acc-1", "person@example.com");
        var host = new AccountSyncHost(accountDir,
            new ExoAccountClient(new Answer(HttpStatusCode.Unauthorized, "{\"error\":\"signed-out\"}")),
            () => session, _ => { });
        AccountSyncHooks.Attach(host);

        // Any playtime call loads the file, which counts the recovered session.
        PlaytimeService.AdoptSession(Other, now);

        var dirty = AccountSyncStore.Load(accountDir, "acc-1").Dirty;
        Assert.Contains(dirty, key => key.StartsWith("playtime/", StringComparison.Ordinal));
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
