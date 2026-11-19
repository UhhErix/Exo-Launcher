using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The byte cap on the waiting changes. Ways this could fail: a log line (a play session, a Riot ledger line) is dropped to make
/// room, so a played session never reaches the account; the cap is ignored and the file grows without bound; the newest object
/// item is dropped while an older one stays. Fixture values are made up.
/// </summary>
public sealed class PendingCapTests
{
    private static PendingItem Object(string id, int size) => new("settings", id, new string('x', size), 1);
    private static PendingItem Log(string id, int size) => new("playtime", id, new string('x', size), 1);

    [Fact]
    public void OverTheCapTheOldestObjectItemGoesFirstAndALogIsKept()
    {
        var items = new List<PendingItem>();
        AccountSyncPending.Add(items, Log("session-1", 400), cap: 1_000);
        AccountSyncPending.Add(items, Object("old", 400), cap: 1_000);
        AccountSyncPending.Add(items, Object("new", 400), cap: 1_000);

        Assert.Contains(items, item => item.Id == "session-1");
        Assert.DoesNotContain(items, item => item.Id == "old");
        Assert.Contains(items, item => item.Id == "new");
    }

    [Fact]
    public void ALogIsNeverDroppedEvenWhenLogsAloneAreOverTheCap()
    {
        var items = new List<PendingItem>();
        AccountSyncPending.Add(items, Log("session-1", 600), cap: 500);
        AccountSyncPending.Add(items, Log("session-2", 600), cap: 500);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void ARepeatOfAnItemReplacesItsEarlierValue()
    {
        var items = new List<PendingItem>();
        AccountSyncPending.Add(items, Object("same", 10), cap: 10_000);
        AccountSyncPending.Add(items, Object("same", 20), cap: 10_000);

        var only = Assert.Single(items);
        Assert.Equal(20, only.Value!.Length);
    }
}
