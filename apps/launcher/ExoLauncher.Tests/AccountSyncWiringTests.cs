using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// What the Launcher syncs and how it reaches the service. What would go wrong silently:
/// - a setting that is neither synced nor device-only (a new property slips in unreviewed);
/// - a device-only value (a path, a GPU profile, a Discord setting) reaching the account;
/// - a PUT body the service would refuse (a key the fixture does not have);
/// - a PC that is not signed in writing to the queue, or a signed-out PC losing its queue on disk.
/// </summary>
public sealed class AccountSyncWiringTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-account-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { DeletionGate.DeleteDirectory(dir, recursive: true); } catch { }
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void EveryAppSettingsPropertyIsSyncedOrDeviceOnly()
    {
        Assert.Empty(AccountSyncMap.Unclassified());
    }

    [Fact]
    public void NoPropertyIsListedTwice()
    {
        var both = AccountSyncMap.SyncedSettings.Intersect(AccountSyncMap.DeviceSettings).ToArray();
        Assert.Empty(both);
    }

    [Fact]
    public void TheSettingsItemCarriesPreferencesAndNoDevicePaths()
    {
        var settings = new AppSettings { ReduceMotion = true, DefaultInstallRoot = @"D:\Games" };

        var value = AccountSyncMap.SettingsValue(settings);

        Assert.True(value["ReduceMotion"]!.GetValue<bool>());
        Assert.Null(value["DefaultInstallRoot"]);
    }

    [Fact]
    public void APulledSettingsValueLaysOnlySyncedPropertiesOver()
    {
        var source = new AppSettings { ReduceMotion = true, DefaultInstallRoot = @"D:\Games" };
        var target = new AppSettings { ReduceMotion = false, DefaultInstallRoot = @"C:\Local" };

        AccountSyncMap.ApplySettings(target, AccountSyncMap.SettingsValue(source).ToJsonString());

        Assert.True(target.ReduceMotion);
        Assert.Equal(@"C:\Local", target.DefaultInstallRoot);
    }

    [Fact]
    public void ThePlaytimeEntryNamesTheGameAndItsMinutes()
    {
        var start = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        var entry = JsonNode.Parse(AccountSyncMap.PlaytimeValue("steam_570", start, start.AddMinutes(42), 42))!.AsObject();

        Assert.Equal("steam_570", entry["game"]!.GetValue<string>());
        Assert.Equal(42, entry["minutes"]!.GetValue<int>());
    }

    [Fact]
    public void AStateFileOfAnotherAccountIsNotLoaded()
    {
        var dir = FreshDir();
        AccountSyncStore.Save(dir, new SyncState { AccountId = "other-account" });

        var state = AccountSyncStore.Load(dir, "this-account");

        Assert.Equal("this-account", state.AccountId);
        Assert.Empty(state.Dirty);
    }

    [Fact]
    public void TheQueueSurvivesASaveAndLoad()
    {
        var dir = FreshDir();
        var engine = new AccountSyncEngine(new SyncState { AccountId = "acc" });
        engine.Write("settings", "app", "{\"ReduceMotion\":true}", 1, DateTimeOffset.UtcNow);

        AccountSyncStore.Save(dir, engine.State);
        var loaded = AccountSyncStore.Load(dir, "acc");

        Assert.Contains("settings/app", loaded.Dirty);
        Assert.Equal("{\"ReduceMotion\":true}", loaded.Items["settings/app"].Value);
    }

    [Fact]
    public void ADamagedQueueIsSetAsideAndNotDeleted()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, AccountSyncStore.FileName);
        File.WriteAllText(path, "{ not json");

        var state = AccountSyncStore.Load(dir, "acc");

        Assert.Empty(state.Dirty);
        Assert.True(File.Exists(path + ".damaged"));
    }

    [Fact]
    public async Task ASyncedPutBodyHasTheFieldsTheServiceAccepts()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Repo.PathOf("cloud", "account", "client-fixtures", "sync.json")))!;
        var recorded = fixture["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "sync-put-create")!;
        var expected = recorded["request"]!["body"]!["items"]![0]!.AsObject().Select(p => p.Key).OrderBy(k => k).ToArray();

        var body = AccountSyncEngine.PutBody([new SyncItem("settings", "app", 0, 1, "{}", false)])["items"]![0]!.AsObject();

        Assert.Equal(expected, body.Select(p => p.Key).OrderBy(k => k).ToArray());
    }

    [Fact]
    public void ALocalChangeWithNoSignedInAccountIsNotQueued()
    {
        var dir = FreshDir();
        var host = new AccountSyncHost(dir, new ExoAccountClient(new Answer(HttpStatusCode.OK, "{}")), () => null, _ => { });

        host.Write("settings", "app", "{\"ReduceMotion\":true}");

        Assert.False(File.Exists(Path.Combine(dir, AccountSyncStore.FileName)));
    }

    [Fact]
    public async Task ASignedOutAccountKeepsItsQueueOnDisk()
    {
        var dir = FreshDir();
        var session = new AccountSession("token-value", "acc-1", "person@example.com");
        var host = new AccountSyncHost(dir,
            new ExoAccountClient(new Answer(HttpStatusCode.Unauthorized, "{\"error\":\"signed-out\"}")),
            () => session, _ => { });

        host.Write("settings", "app", "{\"ReduceMotion\":true}");
        await host.SyncAsync(CancellationToken.None);

        var saved = AccountSyncStore.Load(dir, "acc-1");
        Assert.Contains("settings/app", saved.Dirty);
        Assert.True(saved.SignedOut);
    }
}
