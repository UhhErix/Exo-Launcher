using System.Text.Json.Nodes;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// What a refused push does to the queue. Only an item the service refuses (bad-item) holds its item until it changes; a request
/// or account refusal (403, 409, 408, 413 without too-big, 400 for the request itself, a non-JSON reply) waits on the backoff,
/// as an outage does, so the item is sent again. Without the sweep, a refused item stays held for good, and one that the
/// service now takes never reaches it. Too-large items wait for a new value, not the sweep.
/// </summary>
public sealed class AccountSyncHoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static (AccountSyncEngine Engine, List<SyncItem> Batch) Pushing(string value = "{\"ShowLibraryTools\":true}")
    {
        var engine = new AccountSyncEngine(new SyncState { AccountId = "acc-1" });
        engine.Write("settings", "app", value, 1, Now);
        return (engine, engine.NextPush(Now.AddMinutes(1))!);
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(408, "request-timeout")]
    [InlineData(429, "slow-down")]
    public void AWholeRequestRefusalWaitsOnTheBackoffAndHoldsNothing(int status, string error)
    {
        var (engine, batch) = Pushing();

        engine.ApplyPut(batch, new AccountReply(status, null, error, null), Now.AddMinutes(1));

        Assert.Empty(engine.State.Held);
        Assert.Contains("settings/app", engine.State.Dirty);
        Assert.True(engine.State.NextAttempt > Now.AddMinutes(1));
    }

    [Theory]
    [InlineData(400, "bad-request")]
    [InlineData(409, "conflict")]
    [InlineData(413, "payload-too-large")]
    public void AnOtherRefusalHalvesTheBatchInsteadOfBackingOff(int status, string error)
    {
        var engine = new AccountSyncEngine(new SyncState { AccountId = "acc-1" });
        engine.Write("settings", "app", "{}", 1, Now);
        engine.Write("playtime", "p1", "{}", 2, Now);
        var batch = engine.NextPush(Now.AddMinutes(1))!;

        engine.ApplyPut(batch, new AccountReply(status, null, error, null), Now.AddMinutes(1));

        Assert.Equal(1, engine.State.BatchLimit);
        Assert.Empty(engine.State.Held);
        Assert.Equal(2, engine.State.Dirty.Count);
    }

    [Fact]
    public void OnePoisonedItemAmongFiftyHoldsOnlyItAndTheOtherFortyNineSync()
    {
        var engine = new AccountSyncEngine(new SyncState { AccountId = "acc-1" });
        for (var i = 0; i < 50; i++) engine.Write("playtime", "p" + i.ToString("D2"), "{}", i + 1, Now);
        const string poison = "playtime/p07";
        var now = Now;
        var synced = 0;
        for (var round = 0; round < 500; round++)
        {
            now = now.AddMinutes(1);
            if (engine.NextPush(now) is not { } batch) break;
            if (batch.Any(item => AccountSyncRules.Key(item.Type, item.Id) == poison))
            {
                engine.ApplyPut(batch, new AccountReply(400, null, "bad-request", null), now);
                continue;
            }
            var written = new JsonArray(batch.Select(item => (JsonNode)new JsonObject { ["type"] = item.Type, ["id"] = item.Id, ["version"] = 1 }).ToArray());
            engine.ApplyPut(batch, new AccountReply(200, new JsonObject { ["written"] = written }, null, null), now);
            synced += batch.Count;
        }

        Assert.Equal(new[] { poison }, engine.State.Held.ToArray());
        Assert.Equal(49, synced);
        Assert.Empty(engine.State.Dirty);
    }

    [Fact]
    public void AnItemRefusalHoldsItsItem()
    {
        var (engine, batch) = Pushing();

        engine.ApplyPut(batch, new AccountReply(400, null, "bad-item", null), Now.AddMinutes(1));

        Assert.Contains("settings/app", engine.State.Held);
        Assert.DoesNotContain("settings/app", engine.State.Dirty);
    }

    [Fact]
    public void TheSweepSendsRefusedItemsAgainButNotTooLargeOnes()
    {
        var (engine, batch) = Pushing();
        engine.ApplyPut(batch, new AccountReply(400, null, "bad-item", null), Now.AddMinutes(1));
        var tooLarge = new string('a', AccountSyncRules.MaxValueBytes + 1);
        engine.Write("playtime", "p1", tooLarge, 2, Now.AddMinutes(1));
        engine.NextPush(Now.AddMinutes(2));

        engine.ReleaseHeld();

        Assert.Contains("settings/app", engine.State.Dirty);
        Assert.DoesNotContain("settings/app", engine.State.Held);
        Assert.Contains("playtime/p1", engine.State.Held);
        Assert.Equal(AccountSyncEngine.HeldTooLarge, engine.State.HeldReasons["playtime/p1"]);
    }
}
