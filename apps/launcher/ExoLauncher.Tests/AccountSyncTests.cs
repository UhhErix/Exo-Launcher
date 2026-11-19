using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The sync engine (CLIENT.md 7 and 11) against a fake server that keeps the README's rules: a write lands only when the
/// stored version is no newer than the base, otherwise it is a conflict and nothing is overwritten. What would go wrong silently:
/// - a playtime entry counted twice, or two PCs' entries collapsing into one;
/// - a setting one PC changed lost to another PC's older copy;
/// - a queue that loops, or that drops changes while the PC is offline;
/// - two PCs that end in different states depending on the order of delivery.
/// </summary>
public sealed class AccountSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // ---- Ids and rules --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Steam_1245620")]
    [InlineData("a.b:c~d@e-f")]
    public void AMatchingGameKeyIsUsedAsItIs(string key) => Assert.Equal(key, AccountSyncRules.GameKeyId(key));

    [Theory]
    [InlineData("has a space")]
    [InlineData("unicode-ü")]
    public void AGameKeyThatBreaksTheRuleBecomesAHashedId(string key)
    {
        var id = AccountSyncRules.GameKeyId(key);

        Assert.StartsWith("h.", id);
        Assert.Equal(42, id.Length);
        Assert.True(AccountSyncRules.ValidId(id));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40], id[2..]);
    }

    [Fact]
    public void AGameKeyLongerThan128CharactersIsHashed()
    {
        var id = AccountSyncRules.GameKeyId(new string('1', 130));
        Assert.StartsWith("h.", id);
        Assert.True(AccountSyncRules.ValidId(id));
    }

    [Fact]
    public void ALogEntryIdIsTheSameForTheSameSessionAndDiffersAcrossPcs()
    {
        var a = AccountSyncRules.LogEntryId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "session-1");
        var again = AccountSyncRules.LogEntryId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "session-1");
        var otherPc = AccountSyncRules.LogEntryId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "session-1");

        Assert.Equal(a, again);
        Assert.NotEqual(a, otherPc);
        Assert.StartsWith("s.", a);
        Assert.True(AccountSyncRules.ValidId(a));
    }

    [Theory]
    [InlineData("playtime", MergeKind.Log)]
    [InlineData("riot-ledger", MergeKind.Log)]
    [InlineData("playtime-seed", MergeKind.Seed)]
    [InlineData("settings", MergeKind.Object)]
    [InlineData("cover", MergeKind.Value)]
    [InlineData("cover-image", MergeKind.Value)]
    [InlineData("collection", MergeKind.Value)]
    public void EachSyncedTypeHasOneMergeKind(string type, MergeKind kind) =>
        Assert.Equal(kind, AccountSyncRules.KindOf(type));

    [Fact]
    public void AnUnsyncedTypeHasNoKind()
    {
        Assert.Null(AccountSyncRules.KindOf("discord-token"));
        Assert.Null(AccountSyncRules.KindOf("riot-session"));
    }

    // ---- Local writes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ALogEntrySentTwiceLocallyIsOneItem()
    {
        var engine = Engine();
        engine.Write("playtime", "s.1", "{\"minutes\":30}", 1, T0);
        engine.Write("playtime", "s.1", "{\"minutes\":30}", 2, T0);

        Assert.Single(engine.State.Items);
    }

    [Fact]
    public void ALocalSeedWrittenTwiceKeepsTheFirst()
    {
        var engine = Engine();
        engine.Write("playtime-seed", "steam_1", "{\"minutes\":100}", 1, T0);
        engine.Write("playtime-seed", "steam_1", "{\"minutes\":999}", 2, T0);

        Assert.Equal("{\"minutes\":100}", engine.State.Items["playtime-seed/steam_1"].Value);
    }

    [Fact]
    public void AValueOverSixtyFourKiBIsHeldOnThisPcAndNeverSent()
    {
        var engine = Engine();
        engine.Write("cover-image", "game-1", new string('x', 64 * 1024 + 1), 1, T0);

        Assert.Null(engine.NextPush(T0.AddSeconds(5)));
        Assert.Contains("cover-image/game-1", engine.State.Held);
    }

    [Fact]
    public void AnUnsyncedTypeIsRefusedAtTheWrite()
    {
        var engine = Engine();
        Assert.Throws<ArgumentException>(() => engine.Write("discord-token", "x", "{}", 1, T0));
    }

    // ---- Push timing and batches -----------------------------------------------------------------------------------------

    [Fact]
    public void TwoChangesOneSecondApartMakeOnePutAndALaterOneMakesItsOwn()
    {
        var engine = Engine();
        engine.Write("settings", "player", "{\"volume\":70}", 1, T0);
        engine.Write("settings", "player", "{\"volume\":80}", 2, T0.AddSeconds(1));

        Assert.Null(engine.NextPush(T0.AddSeconds(2)));                 // two seconds after the last change is 3 s
        Assert.NotNull(engine.NextPush(T0.AddSeconds(3)));
        engine.State.Dirty.Clear();

        engine.Write("settings", "player", "{\"volume\":10}", 3, T0.AddSeconds(10));
        Assert.NotNull(engine.NextPush(T0.AddSeconds(12)));
    }

    [Fact]
    public void ABatchHoldsAtMostOneHundredItems()
    {
        var engine = Engine();
        for (var i = 0; i < 150; i++) engine.Write("cover", $"g{i}", "{\"source\":\"store\"}", i + 1, T0);

        var batch = engine.NextPush(T0.AddSeconds(5));

        Assert.Equal(100, batch!.Count);
    }

    // ---- Merge rules on conflict -----------------------------------------------------------------------------------------

    [Fact]
    public void AnObjectConflictKeepsTheKeysThisPcDidNotChange()
    {
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();

        pcA.Write("settings", "player", "{\"volume\":70,\"captions\":\"off\"}", 1, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        pcB.Pull(server.Pull(0), T0.AddSeconds(4));
        pcB.Write("settings", "player", "{\"volume\":70,\"captions\":\"on\"}", 2, T0.AddSeconds(5));
        pcA.Write("settings", "player", "{\"volume\":30,\"captions\":\"off\"}", 3, T0.AddSeconds(6));
        Sync(pcA, server, T0.AddSeconds(9));
        Sync(pcB, server, T0.AddSeconds(12));   // conflicts: merged, and sent again on the next round
        Sync(pcB, server, T0.AddSeconds(15));

        var merged = JsonNode.Parse(server.Value("settings/player") ?? throw new InvalidOperationException("No settings/player value."))!.AsObject();
        Assert.Equal(30, merged["volume"]!.GetValue<int>());
        Assert.Equal("on", merged["captions"]!.GetValue<string>());
    }

    [Fact]
    public void AKeyThisPcRemovedSyncsAndTheOtherPcsChangesToOtherKeysStay()
    {
        // A removed setting key must reach the server: ChangedKeys has to report a key that only the old value had.
        // Without that, the conflict keeps the server's captions key and this PC's removal never lands.
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();

        pcA.Write("settings", "player", "{\"volume\":70,\"captions\":\"on\"}", 1, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        pcB.Pull(server.Pull(0), T0.AddSeconds(4));
        pcB.Write("settings", "player", "{\"volume\":50,\"captions\":\"on\"}", 2, T0.AddSeconds(5));
        Sync(pcB, server, T0.AddSeconds(8));
        pcA.Write("settings", "player", "{\"volume\":70}", 3, T0.AddSeconds(6));   // removes captions, base is stale
        Sync(pcA, server, T0.AddSeconds(9));   // conflicts with pcB's volume change: merged and sent again
        Sync(pcA, server, T0.AddSeconds(12));

        var merged = JsonNode.Parse(server.Value("settings/player") ?? throw new InvalidOperationException("No settings/player value."))!.AsObject();
        Assert.False(merged.ContainsKey("captions"));
        Assert.Equal(50, merged["volume"]!.GetValue<int>());
    }

    [Fact]
    public void ANewerValueWinsAndAnOlderOneLoses()
    {
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();

        pcA.Write("cover", "g1", "{\"source\":\"store\"}", 100, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        pcB.Pull(server.Pull(0), T0.AddSeconds(4));
        pcB.Write("cover", "g1", "{\"source\":\"custom\"}", 200, T0.AddSeconds(5));
        pcA.Write("cover", "g1", "{\"source\":\"store\",\"v\":2}", 150, T0.AddSeconds(6));
        Sync(pcB, server, T0.AddSeconds(9));
        Sync(pcA, server, T0.AddSeconds(12));

        Assert.Equal("{\"source\":\"custom\"}", server.Value("cover/g1"));
        Assert.Equal("{\"source\":\"custom\"}", pcA.State.Items["cover/g1"].Value);
    }

    [Fact]
    public void AnEditNewerThanADeletionWinsAndAnOlderOneLoses()
    {
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();

        pcA.Write("cover", "g2", "{\"source\":\"store\"}", 100, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        pcB.Pull(server.Pull(0), T0.AddSeconds(4));
        pcB.Write("cover", "g2", null, 300, T0.AddSeconds(5));          // deletion, newer
        pcA.Write("cover", "g2", "{\"source\":\"custom\"}", 200, T0.AddSeconds(6));  // edit, older than the deletion
        Sync(pcB, server, T0.AddSeconds(9));
        Sync(pcA, server, T0.AddSeconds(12));

        Assert.Null(server.Value("cover/g2"));
    }

    [Fact]
    public void TheFirstServerWriteOfASeedWins()
    {
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();

        pcA.Write("playtime-seed", "steam_7", "{\"minutes\":100}", 1, T0);
        pcB.Write("playtime-seed", "steam_7", "{\"minutes\":250}", 1, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        Sync(pcB, server, T0.AddSeconds(6));

        Assert.Equal("{\"minutes\":100}", server.Value("playtime-seed/steam_7"));
        Assert.Equal("{\"minutes\":100}", pcB.State.Items["playtime-seed/steam_7"].Value);
        Assert.Empty(pcB.State.Dirty);
    }

    [Fact]
    public void LogEntriesFromTwoPcsAreBothKeptAndAResentEntryIsNotDoubled()
    {
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();
        var entryA = AccountSyncRules.LogEntryId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "s1");
        var entryB = AccountSyncRules.LogEntryId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "s1");

        pcA.Write("playtime", entryA, "{\"minutes\":30}", 1, T0);
        pcB.Write("playtime", entryB, "{\"minutes\":45}", 1, T0);
        Sync(pcA, server, T0.AddSeconds(3));
        Sync(pcB, server, T0.AddSeconds(6));
        Sync(pcA, server, T0.AddSeconds(9));

        Assert.Equal(2, server.Count("playtime"));
        Assert.Equal(2, pcA.State.Items.Count(item => item.Key.StartsWith("playtime/")));
    }

    // ---- Queue, offline and failures -------------------------------------------------------------------------------------

    [Fact]
    public void ChangesMadeOfflineWaitInTheQueueAndArriveOnTheNextSync()
    {
        var server = new FakeServer();
        var engine = Engine();
        engine.Write("settings", "player", "{\"volume\":55}", 1, T0);

        engine.ApplyPut(null, new AccountReply(0, null, "network", null), T0.AddSeconds(3));   // offline
        Assert.Single(engine.State.Dirty);

        Sync(engine, server, T0.AddMinutes(2));
        Assert.Equal("{\"volume\":55}", server.Value("settings/player"));
        Assert.Empty(engine.State.Dirty);
    }

    [Fact]
    public void FailuresBackOffDoublingToTenMinutesWithJitter()
    {
        var random = new Random(7);
        var delays = Enumerable.Range(1, 9).Select(n => AccountSyncRules.Backoff(n, random).TotalSeconds).ToArray();

        Assert.InRange(delays[0], 4, 6);       // 5 s, plus or minus 20%
        Assert.InRange(delays[1], 8, 12);      // 10 s
        Assert.InRange(delays[2], 16, 24);     // 20 s
        Assert.All(delays, d => Assert.InRange(d, 1, 720));   // capped at 10 minutes plus jitter
        Assert.InRange(delays[8], 480, 720);
    }

    [Fact]
    public void SignedOutStopsTheEngineAndKeepsTheQueue()
    {
        var engine = Engine();
        engine.Write("settings", "player", "{}", 1, T0);

        engine.ApplyPut(null, new AccountReply(401, null, "signed-out", null), T0.AddSeconds(3));

        Assert.True(engine.State.SignedOut);
        Assert.Null(engine.NextPush(T0.AddMinutes(10)));
        Assert.Single(engine.State.Dirty);
    }

    [Fact]
    public void AccountFullStopsNewPushesButPullsStillApply()
    {
        var server = new FakeServer();
        var engine = Engine();
        engine.Write("settings", "player", "{\"volume\":1}", 1, T0);
        engine.ApplyPut(null, new AccountReply(413, null, "account-full", null), T0.AddSeconds(3));

        Assert.True(engine.State.PushStopped);
        Assert.Null(engine.NextPush(T0.AddMinutes(5)));
        _ = server.Put([new SyncItem("cover", "other", 0, 1, "{\"source\":\"store\"}", false)]);
        engine.Pull(server.Pull(0), T0.AddMinutes(5));
        Assert.True(engine.State.Items.ContainsKey("cover/other"));
    }

    [Fact]
    public void ATooBigValueIsHeldAloneAndTheRestGoOn()
    {
        var server = new FakeServer { MaxBodyValue = 500 };
        var engine = Engine();
        engine.Write("cover-image", "big", new string('x', 1000), 1, T0);
        engine.Write("cover", "small", "{\"source\":\"store\"}", 2, T0);

        for (var step = 0; step < 10; step++)
        {
            var batch = engine.NextPush(T0.AddSeconds(3));
            if (batch is null) break;
            engine.ApplyPut(batch, server.Put(batch), T0.AddSeconds(3));
        }

        Assert.Equal(["cover-image/big"], engine.State.Held.ToArray());
        Assert.Equal("{\"source\":\"store\"}", server.Value("cover/small"));
    }

    [Fact]
    public void AnObjectThatConflictsThreeTimesWaitsBeforeItIsSentAgain()
    {
        var server = new FakeServer();
        var pc = Engine();
        pc.Write("settings", "player", "{\"volume\":1}", 1, T0);
        _ = server.Put(pc.NextPush(T0.AddSeconds(3))!);
        var now = T0.AddSeconds(3);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            now = now.AddSeconds(3);
            pc.Write("settings", "player", "{\"volume\":" + (attempt + 2) + "}", attempt + 2, now);
            server.Bump("settings/player");          // another PC moved the server on: this push conflicts
            var batch = pc.NextPush(now.AddSeconds(3));
            Assert.NotNull(batch);
            pc.ApplyPut(batch, server.Put(batch), now.AddSeconds(3));
        }

        Assert.Null(pc.NextPush(now.AddSeconds(30)));
        Assert.NotNull(pc.NextPush(now.AddMinutes(2)));
    }


    // ---- Convergence -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TwoPcsEndInTheSameStateWhateverTheDeliveryOrder()
    {
        var server = new FakeServer();
        var pcA = Engine();
        var pcB = Engine();
        var random = new Random(42);

        pcA.Write("settings", "player", "{\"volume\":10,\"captions\":\"off\"}", 1, T0);
        pcB.Write("settings", "player", "{\"volume\":90,\"captions\":\"on\"}", 2, T0.AddSeconds(1));
        pcA.Write("cover", "g9", "{\"source\":\"store\"}", 3, T0.AddSeconds(2));
        pcB.Write("cover", "g9", "{\"source\":\"custom\"}", 4, T0.AddSeconds(2));
        pcA.Write("playtime", AccountSyncRules.LogEntryId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "x"), "{\"minutes\":5}", 5, T0);
        pcB.Write("playtime", AccountSyncRules.LogEntryId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "y"), "{\"minutes\":7}", 6, T0);

        var now = T0.AddSeconds(5);
        for (var round = 0; round < 6; round++)
        {
            foreach (var pc in new[] { pcA, pcB }.OrderBy(_ => random.Next()))
            {
                pc.Pull(server.Pull(pc.State.Cursor), now);
                Sync(pc, server, now);
            }
            now = now.AddMinutes(1);
        }

        Assert.Equal(Final(pcA), Final(pcB));
        Assert.Equal(Final(pcA), server.Snapshot());
    }

    [Fact]
    public void TheQueueSurvivesARestart()
    {
        var engine = Engine();
        engine.Write("settings", "player", "{\"volume\":9}", 1, T0);

        var json = JsonSerializer.Serialize(engine.State);
        var reloaded = new AccountSyncEngine(JsonSerializer.Deserialize<SyncState>(json)!);

        Assert.Single(reloaded.State.Dirty);
        Assert.Equal("{\"volume\":9}", reloaded.State.Items["settings/player"].Value);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------

    private static AccountSyncEngine Engine() => new(new SyncState());

    /// <summary>One push and one pull, as the engine does on a timer, against the fake server.</summary>
    private static void Sync(AccountSyncEngine engine, FakeServer server, DateTimeOffset now)
    {
        var batch = engine.NextPush(now);
        if (batch is not null) engine.ApplyPut(batch, server.Put(batch), now);
        engine.Pull(server.Pull(engine.State.Cursor), now);
    }

    private static string Final(AccountSyncEngine engine) =>
        string.Join("\n", engine.State.Items.OrderBy(i => i.Key).Select(i => $"{i.Key}={i.Value.Value}"));

    /// <summary>The server's README rules: a write lands when the stored version is no newer than its base; else a conflict.</summary>
    [Fact]
    public void ARiotLedgerLineHasOneIdOnEveryPcAndTheIdIsValid()
    {
        // The same line from two PCs is one item: the id does not depend on the device; another account's line is another item.
        var id = AccountSyncRules.RiotLineId("0123456789abcdef0123", "s|riot:valorant|1");

        Assert.Equal(id, AccountSyncRules.RiotLineId("0123456789abcdef0123", "s|riot:valorant|1"));
        Assert.NotEqual(id, AccountSyncRules.RiotLineId("fedcba9876543210fedc", "s|riot:valorant|1"));
        Assert.True(AccountSyncRules.ValidId(id));
    }

    [Fact]
    public void ARiotGamesPlaytimeIsCarriedByItsLedgerNotByPlaytimeEntries()
    {
        // A Riot game's minutes sync as riot-ledger lines; a playtime entry for the same session would count them twice.
        Assert.True(AccountSyncRules.PlaytimeCarriedByLedger("riot:valorant"));
        Assert.False(AccountSyncRules.PlaytimeCarriedByLedger("steam:570"));
    }

    private sealed class FakeServer
    {
        private long version;
        private readonly Dictionary<string, (long Version, long Updated, string? Value)> items = new();

        public long MaxBodyValue { get; init; } = long.MaxValue;

        /// <summary>Moves the server ahead on its own (another PC wrote): the next push of an older base conflicts.</summary>
        public void Bump(string key) { version++; items[key] = (version, 0, items[key].Value + " "); }

        public AccountReply Put(IEnumerable<SyncItem> sent)
        {
            var whole = sent.ToList();
            if (whole.Any(item => (item.Value?.Length ?? 0) > MaxBodyValue))
                return new AccountReply(413, new JsonObject { ["error"] = "too-big" }, "too-big", null);
            sent = whole;
            var written = new JsonArray();
            var conflicts = new JsonArray();
            foreach (var item in sent)
            {
                var key = item.Type + "/" + item.Id;
                if (items.TryGetValue(key, out var stored) && stored.Version > item.Base)
                {
                    conflicts.Add(new JsonObject { ["app"] = "launcher", ["type"] = item.Type, ["id"] = item.Id, ["version"] = stored.Version, ["updated"] = stored.Updated, ["value"] = stored.Value });
                    continue;
                }
                version++;
                items[key] = (version, item.Updated, item.Deleted ? null : item.Value);
                written.Add(new JsonObject { ["app"] = "launcher", ["type"] = item.Type, ["id"] = item.Id, ["version"] = version });
            }
            return new AccountReply(200, new JsonObject { ["written"] = written, ["conflicts"] = conflicts }, null, null);
        }

        public AccountReply Pull(long after)
        {
            var page = new JsonArray();
            long cursor = after;
            foreach (var (key, stored) in items.Where(i => i.Value.Version > after).OrderBy(i => i.Value.Version))
            {
                var split = key.IndexOf('/');
                page.Add(new JsonObject
                {
                    ["app"] = "launcher", ["type"] = key[..split], ["id"] = key[(split + 1)..],
                    ["version"] = stored.Version, ["updated"] = stored.Updated,
                    ["value"] = stored.Value is null ? null : JsonValue.Create(stored.Value),
                });
                cursor = stored.Version;
            }
            return new AccountReply(200, new JsonObject { ["items"] = page, ["cursor"] = cursor, ["more"] = false }, null, null);
        }

        public string? Value(string key) => items.TryGetValue(key, out var stored) ? stored.Value : null;

        public int Count(string type) => items.Keys.Count(key => key.StartsWith(type + "/", StringComparison.Ordinal));

        public string Snapshot() =>
            string.Join("\n", items.Where(i => i.Value.Value is not null).OrderBy(i => i.Key).Select(i => $"{i.Key}={i.Value.Value}"));

    }
}
