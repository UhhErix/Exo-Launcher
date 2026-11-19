using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The waiting-changes file must never cost a play session or a corrupt file its data. Ways this could fail: a write error
/// (a folder where the file should be) throws into the caller, so a play session's end does not save its minutes; a corrupt
/// file is overwritten, losing what it held; the byte cap drops a log line (a play session) to make room for settings.
/// Fixture game and ids match no real library entry or account.
/// </summary>
public sealed class AccountSyncPendingSafetyTests : IDisposable
{
    private const string Game = "steam:999999961";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-pending-safety-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public AccountSyncPendingSafetyTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
        PlaytimeService.ForgetLoaded();
        RiotLedger.ForgetLoaded();
    }

    public void Dispose()
    {
        AccountSyncHooks.Attach(null);
        PlaytimeService.ForgetLoaded();
        RiotLedger.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class Empty : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    private AccountSyncHost SignedOutHost() =>
        new(_dir, new ExoAccountClient(new Empty()), () => null, _ => { });

    private string Pending => Path.Combine(_dir, AccountSyncHost.PendingFile);

    private int MinutesOnDisk(string game)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "playtime.json")))!;
        return root["minutes"]?[game]?.GetValue<int>() ?? 0;
    }

    [Fact]
    public void ACorruptWaitingFileIsKeptAsideNotOverwritten()
    {
        File.WriteAllText(Pending, "{not a list");
        var host = SignedOutHost();

        host.Write("playtime", "session-9", "{\"minutes\":5}");

        var aside = Directory.GetFiles(_dir, AccountSyncHost.PendingFile + ".unreadable-*");
        var kept = Assert.Single(aside);
        Assert.Equal("{not a list", File.ReadAllText(kept));
        Assert.Contains("playtime", File.ReadAllText(Pending));
    }

    [Fact]
    public void AWriteThatCannotBeKeptIsLoggedAndNeverThrows()
    {
        // A folder where the file should be: nothing can be written there.
        Directory.CreateDirectory(Pending);
        var host = SignedOutHost();

        var error = Record.Exception(() => host.Write("playtime", "session-10", "{\"minutes\":5}"));

        Assert.Null(error);
        Assert.True(Directory.Exists(Pending));
    }

    [Fact]
    public void APlaySessionEndsAndKeepsItsMinutesEvenWhenItsWaitingWriteFails()
    {
        var host = SignedOutHost();
        AccountSyncHooks.Attach(host);
        Directory.CreateDirectory(Pending);
        var now = DateTimeOffset.UtcNow;
        PlaytimeService.AdoptSession(Game, now.AddMinutes(-30));
        PlaytimeService.AdoptSession("riot:valorant", now.AddMinutes(-30));

        var error = Record.Exception(() => PlaytimeService.EndSession(Game, now));
        var riotError = Record.Exception(() => PlaytimeService.EndSession("riot:valorant", now));

        Assert.Null(error);
        Assert.Null(riotError);
        Assert.Equal(30, MinutesOnDisk(Game));
        Assert.Equal(30, RiotLedger.Totals(RiotLedger.Unassigned, "riot:valorant"));
    }
}
