using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

[Collection("AppDataIsolation")]
public sealed class ReduceMotionSyncTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-rm-sync-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public ReduceMotionSyncTests()
    {
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);
        AccountSyncHooks.Attach(null);
    }

    public void Dispose()
    {
        AccountSyncHooks.Attach(null);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_data, recursive: true); } catch { }
    }

    [Fact]
    public void OlderLocalReduceMotionTrueDoesNotOverwriteNewerCloudFalseOnPull()
    {
        var server = new MockSyncServer();
        var pcA = new AccountSyncEngine(new SyncState());
        var pcB = new AccountSyncEngine(new SyncState());

        pcB.Write("settings", "app", "{\"ReduceMotion\":true,\"SortMode\":\"name\"}", 1000, T0);
        pcA.Write("settings", "app", "{\"ReduceMotion\":false,\"SortMode\":\"name\"}", 2000, T0.AddSeconds(1));
        Sync(pcA, server, T0.AddSeconds(5));

        pcB.Pull(server.Pull(pcB.State.Cursor), T0.AddSeconds(6));
        Sync(pcB, server, T0.AddSeconds(10));

        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        var localB = JsonNode.Parse(pcB.State.Items["settings/app"].Value!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.False(localB["ReduceMotion"]!.GetValue<bool>());
        Assert.Empty(pcB.State.Dirty);
    }

    [Fact]
    public void OlderLocalReduceMotionTrueDoesNotOverwriteNewerCloudFalseOnConflict()
    {
        var server = new MockSyncServer();
        var pcA = new AccountSyncEngine(new SyncState());
        var pcB = new AccountSyncEngine(new SyncState());

        pcA.Write("settings", "app", "{\"ReduceMotion\":true,\"SortMode\":\"name\"}", 500, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        pcB.Pull(server.Pull(0), T0.AddSeconds(4));

        pcB.Write("settings", "app", "{\"ReduceMotion\":false,\"SortMode\":\"name\"}", 800, T0.AddSeconds(5));
        pcB.Write("settings", "app", "{\"ReduceMotion\":true,\"SortMode\":\"name\"}", 1000, T0.AddSeconds(6));

        pcA.Write("settings", "app", "{\"ReduceMotion\":false,\"SortMode\":\"name\"}", 2000, T0.AddSeconds(7));
        Sync(pcA, server, T0.AddSeconds(10));

        Sync(pcB, server, T0.AddSeconds(12));
        Sync(pcB, server, T0.AddSeconds(16));

        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.Empty(pcB.State.Dirty);
    }

    [Fact]
    public void NewerLocalReduceMotionFalseBeatsOlderCloudTrueAndPreservesOtherCloudKeys()
    {
        var server = new MockSyncServer();
        var pcA = new AccountSyncEngine(new SyncState());
        var pcB = new AccountSyncEngine(new SyncState());

        pcA.Write("settings", "app", "{\"ReduceMotion\":true,\"SortMode\":\"played\",\"TextSize\":\"large\"}", 1000, T0);
        Sync(pcA, server, T0.AddSeconds(3));

        pcB.Write("settings", "app", "{\"ReduceMotion\":false,\"SortMode\":\"name\",\"TextSize\":\"normal\"}", 2000, T0.AddSeconds(4), ["ReduceMotion"]);
        pcB.Pull(server.Pull(0), T0.AddSeconds(5));
        Sync(pcB, server, T0.AddSeconds(8));

        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.Equal("played", cloud["SortMode"]!.GetValue<string>());
        Assert.Equal("large", cloud["TextSize"]!.GetValue<string>());
    }

    [Fact]
    public async Task StartupSyncAppVersionBeforeFirstPullDoesNotClobberCloudReduceMotionFalse()
    {
        var server = new MockSyncServer();
        var cloudSettings = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = false, SortMode = "played" }).ToJsonString();
        server.Seed("settings", "app", cloudSettings, 2000);

        File.WriteAllText(PathHelper.SettingsPath, "{\"reduceMotion\":true,\"appVersion\":\"3.30.3\"}");

        var settings = new SettingsService();
        settings.Load();
        Assert.True(settings.Current.ReduceMotion);

        var session = new AccountSession("tok", "acc-1", "person@example.com");
        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item => AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId)));
        AccountSyncHooks.Attach(host);

        settings.SyncAppVersion("3.31.0");

        await host.SyncAsync(CancellationToken.None);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);
        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.Empty(AccountSyncStore.Load(_data, "acc-1").Dirty);
    }

    [Fact]
    public void ApplyingPulledSettingsDoesNotRequeueSyncDirty()
    {
        var server = new MockSyncServer();
        var settings = new SettingsService();
        settings.Load();

        var session = new AccountSession("tok", "acc-1", "person@example.com");
        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item => AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId)));
        AccountSyncHooks.Attach(host);

        var partialFromOlderBuild = "{\"ReduceMotion\":false,\"SortMode\":\"played\"}";
        AccountSyncApply.Pulled(new AccountSyncHost.PulledItem("settings", AccountSyncMap.SettingsId, partialFromOlderBuild, IsNew: false), settings);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);
        Assert.False(File.Exists(Path.Combine(_data, AccountSyncStore.FileName)));
    }

    [Fact]
    public async Task PutConflictBetweenPullAndPushAppliesCloudReduceMotionFalseImmediately()
    {
        var server = new MockSyncServer();
        var initial = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = true, SortMode = "name" }).ToJsonString();
        server.Seed("settings", "app", initial, 1000);

        File.WriteAllText(PathHelper.SettingsPath, "{\"reduceMotion\":true,\"sortMode\":\"name\"}");
        var settings = new SettingsService();
        settings.Load();

        var session = new AccountSession("tok", "acc-1", "person@example.com");
        var injectConflict = false;
        server.BeforeFirstPut = () =>
        {
            if (!injectConflict) return;
            injectConflict = false;
            var otherPcFalse = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = false, SortMode = "name" }).ToJsonString();
            server.Seed("settings", "app", otherPcFalse, DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
        };

        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item => AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId)));
        AccountSyncHooks.Attach(host);

        await host.SyncAsync(CancellationToken.None);
        Assert.True(settings.Current.ReduceMotion);

        settings.ApplyPatch(sortMode: "played");
        injectConflict = true;
        await host.SyncAsync(CancellationToken.None, ignoreDebounce: true);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);

        settings.SyncAppVersion("3.31.1");
        settings.ApplyPatch(textSize: "large");
        await host.SyncAsync(CancellationToken.None, ignoreDebounce: true);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);
        Assert.Equal("large", settings.Current.TextSize);
        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SignedOutReduceMotionFalseChangeSticksOverOlderCloudTrueOnSignIn()
    {
        var server = new MockSyncServer();
        var olderCloud = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = true, SortMode = "played" }).ToJsonString();
        server.Seed("settings", "app", olderCloud, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());

        File.WriteAllText(PathHelper.SettingsPath, "{\"reduceMotion\":true,\"sortMode\":\"name\"}");
        var settings = new SettingsService();
        settings.Load();

        AccountSession? session = null;
        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item => AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId)));
        AccountSyncHooks.Attach(host);

        settings.ApplyPatch(reduceMotion: false);
        Assert.False(settings.Current.ReduceMotion);

        session = new AccountSession("tok", "acc-1", "person@example.com");
        await host.SyncAsync(CancellationToken.None, ignoreDebounce: true);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);
        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.Equal("played", cloud["SortMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task SignedOutOlderReduceMotionTrueLosesToNewerCloudFalseOnSignIn()
    {
        var server = new MockSyncServer();
        var newerCloud = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = false, SortMode = "played" }).ToJsonString();
        server.Seed("settings", "app", newerCloud, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());

        File.WriteAllText(PathHelper.SettingsPath, "{\"reduceMotion\":false,\"sortMode\":\"name\"}");
        var settings = new SettingsService();
        settings.Load();

        AccountSession? session = null;
        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item => AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId)));
        AccountSyncHooks.Attach(host);

        settings.ApplyPatch(reduceMotion: true);
        Assert.True(settings.Current.ReduceMotion);

        session = new AccountSession("tok", "acc-1", "person@example.com");
        await host.SyncAsync(CancellationToken.None, ignoreDebounce: true);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);
        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.Empty(AccountSyncStore.Load(_data, "acc-1").Dirty);
    }

    [Fact]
    public async Task UserToggleDuringPullApplyRaceIsNotOverwrittenByStalePulledSnapshot()
    {
        var server = new MockSyncServer();
        var cloudTrue = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = true, SortMode = "played" }).ToJsonString();
        server.Seed("settings", "app", cloudTrue, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        File.WriteAllText(PathHelper.SettingsPath, "{\"reduceMotion\":true,\"sortMode\":\"name\"}");
        var settings = new SettingsService();
        settings.Load();

        var session = new AccountSession("tok", "acc-1", "person@example.com");
        AccountSyncHost? host = null;
        var simulatedUserEdit = false;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item =>
            {
                if (!simulatedUserEdit && item.Type == "settings")
                {
                    simulatedUserEdit = true;
                    settings.ApplyPatch(reduceMotion: false);
                }
                return AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId));
            });
        AccountSyncHooks.Attach(host);

        await host.SyncAsync(CancellationToken.None, ignoreDebounce: true);

        Assert.False(settings.Current.ReduceMotion);
        Assert.Equal("played", settings.Current.SortMode);
        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SyncNowPushesFreshReduceMotionToggleImmediately()
    {
        var server = new MockSyncServer();
        var cloudTrue = AccountSyncMap.SettingsValue(new AppSettings { ReduceMotion = true }).ToJsonString();
        server.Seed("settings", "app", cloudTrue, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        File.WriteAllText(PathHelper.SettingsPath, "{\"reduceMotion\":true}");
        var settings = new SettingsService();
        settings.Load();

        var session = new AccountSession("tok", "acc-1", "person@example.com");
        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            _data,
            new ExoAccountClient(server.Handler()),
            () => session,
            item => AccountSyncApply.Pulled(item, settings, () => host?.KnownValue("settings", AccountSyncMap.SettingsId)));
        AccountSyncHooks.Attach(host);

        await host.SyncAsync(CancellationToken.None);
        settings.ApplyPatch(reduceMotion: false);
        await AccountSyncHooks.SyncNowAsync();

        var cloud = JsonNode.Parse(server.Value("settings/app")!)!.AsObject();
        Assert.False(cloud["ReduceMotion"]!.GetValue<bool>());
        Assert.Empty(AccountSyncStore.Load(_data, "acc-1").Dirty);
    }

    private static void Sync(AccountSyncEngine engine, MockSyncServer server, DateTimeOffset now)
    {
        var batch = engine.NextPush(now);
        if (batch is not null) engine.ApplyPut(batch, server.Put(batch), now);
        engine.Pull(server.Pull(engine.State.Cursor), now);
    }

    private sealed class MockSyncServer
    {
        private readonly Dictionary<string, (string Type, string Id, long Version, long Updated, string? Value, bool Deleted)> _rows = [];
        private long _seq;

        public Action? BeforeFirstPut { get; set; }

        public string? Value(string key) => _rows.TryGetValue(key, out var row) ? row.Value : null;

        public void Seed(string type, string id, string? value, long updated)
        {
            _seq++;
            _rows[AccountSyncRules.Key(type, id)] = (type, id, _seq, updated, value, value is null);
        }

        public AccountReply Pull(long after)
        {
            var items = new JsonArray();
            var last = after;
            foreach (var row in _rows.Values.Where(r => r.Version > after).OrderBy(r => r.Version))
            {
                var node = new JsonObject
                {
                    ["app"] = AccountSyncRules.App,
                    ["type"] = row.Type,
                    ["id"] = row.Id,
                    ["version"] = row.Version,
                    ["updated"] = row.Updated,
                    ["deleted"] = row.Deleted,
                };
                if (!row.Deleted) node["value"] = row.Value;
                items.Add(node);
                last = row.Version;
            }
            return new AccountReply(200, new JsonObject { ["items"] = items, ["cursor"] = last, ["more"] = false }, null, null);
        }

        public AccountReply Put(IReadOnlyList<SyncItem> batch)
        {
            var written = new JsonArray();
            var conflicts = new JsonArray();
            foreach (var item in batch)
            {
                var key = AccountSyncRules.Key(item.Type, item.Id);
                if (_rows.TryGetValue(key, out var existing) && existing.Version > item.Base)
                {
                    var conflict = new JsonObject
                    {
                        ["app"] = AccountSyncRules.App,
                        ["type"] = existing.Type,
                        ["id"] = existing.Id,
                        ["version"] = existing.Version,
                        ["updated"] = existing.Updated,
                    };
                    if (!existing.Deleted) conflict["value"] = existing.Value;
                    conflicts.Add(conflict);
                }
                else
                {
                    _seq++;
                    _rows[key] = (item.Type, item.Id, _seq, item.Updated, item.Deleted ? null : item.Value, item.Deleted);
                    written.Add(new JsonObject
                    {
                        ["app"] = AccountSyncRules.App,
                        ["type"] = item.Type,
                        ["id"] = item.Id,
                        ["version"] = _seq,
                    });
                }
            }
            return new AccountReply(200, new JsonObject { ["written"] = written, ["conflicts"] = conflicts }, null, null);
        }

        public HttpMessageHandler Handler() => new ServerHandler(this);

        private sealed class ServerHandler(MockSyncServer server) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                AccountReply reply;
                if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sync")
                {
                    var query = request.RequestUri.Query;
                    var after = 0L;
                    var prefix = "after=";
                    var idx = query.IndexOf(prefix, StringComparison.Ordinal);
                    if (idx >= 0 && long.TryParse(query[(idx + prefix.Length)..].Split('&')[0], out var parsed)) after = parsed;
                    reply = server.Pull(after);
                }
                else if (request.Method == HttpMethod.Put && request.RequestUri?.AbsolutePath == "/v1/sync")
                {
                    server.BeforeFirstPut?.Invoke();
                    var text = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var body = JsonNode.Parse(text)!.AsObject();
                    var items = new List<SyncItem>();
                    foreach (var node in body["items"]!.AsArray().OfType<JsonObject>())
                    {
                        items.Add(new SyncItem(
                            node["type"]!.GetValue<string>(),
                            node["id"]!.GetValue<string>(),
                            node["base"]!.GetValue<long>(),
                            node["updated"]!.GetValue<long>(),
                            node["value"]?.GetValue<string>(),
                            node["deleted"]?.GetValue<bool>() == true));
                    }
                    reply = server.Put(items);
                }
                else
                {
                    reply = new AccountReply(404, new JsonObject { ["error"] = "not-found" }, "not-found", null);
                }
                return new HttpResponseMessage((HttpStatusCode)reply.Status)
                {
                    Content = new StringContent(reply.Body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
