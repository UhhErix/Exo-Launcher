using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// Settings > Account's data paths. What would go wrong silently:
/// - an export that stops after the first page, or drops a deleted item, or keeps an older copy of a changed item;
/// - a held item with no reason, so the page shows a number nobody can act on;
/// - the queue of a deleted account surviving into the next account.
/// </summary>
public sealed class AccountSettingsTests
{
    /// <summary>Answers each export page by its after= value, as the service pages them.</summary>
    private sealed class ExportPages(Dictionary<long, string> byAfter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var after = long.Parse(request.RequestUri!.Query.Split('=')[1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(byAfter[after], Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task TheExportFollowsEveryPageAndKeepsTombstonesAndTheNewestCopy()
    {
        var pages = new Dictionary<long, string>
        {
            [0] = """
                {"exported":1,"account":{"id":"acc-1","created":1},
                 "items":[
                   {"app":"launcher","type":"settings","id":"app","version":2,"updated":1,"value":"{\"a\":1}"},
                   {"app":"launcher","type":"cover","id":"g1","version":3,"updated":1,"value":null}],
                 "cursor":3,"more":true}
                """,
            [3] = """
                {"exported":1,"account":{"id":"acc-1","created":1},
                 "items":[
                   {"app":"launcher","type":"settings","id":"app","version":4,"updated":2,"value":"{\"b\":2}"}],
                 "cursor":4,"more":false}
                """,
        };
        using var client = new ExoAccountClient(new ExportPages(pages));

        var result = await AccountExport.CollectAsync(client, "token-value", DateTimeOffset.UnixEpoch);

        Assert.True(result.Ok);
        var file = result.File!;
        Assert.Equal(1, file["format"]!.GetValue<int>());
        Assert.Equal("acc-1", file["account"]!["id"]!.GetValue<string>());
        var items = file["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        var settings = items.Single(i => i!["type"]!.GetValue<string>() == "settings")!;
        Assert.Equal(4, settings["version"]!.GetValue<int>());
        Assert.Equal("{\"b\":2}", settings["value"]!.GetValue<string>());
        var cover = items.Single(i => i!["type"]!.GetValue<string>() == "cover")!;
        Assert.Null(cover["value"]);
    }

    [Fact]
    public void AHeldItemSaysWhyItIsHeld()
    {
        var engine = new AccountSyncEngine(new SyncState { AccountId = "acc" });
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        engine.Write("cover-image", "big", new string('x', 70_000), 1, now);
        var batch = engine.NextPush(now.AddSeconds(3));

        Assert.Null(batch);
        Assert.Contains("cover-image/big", engine.State.Held);
        Assert.Equal(AccountSyncEngine.HeldTooLarge, engine.State.HeldReasons["cover-image/big"]);
    }

    [Fact]
    public void ARefusedItemSaysWhatTheServiceSaid()
    {
        var engine = new AccountSyncEngine(new SyncState { AccountId = "acc" });
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        engine.Write("cover", "g9", "{\"source\":\"store\"}", 1, now);
        var batch = engine.NextPush(now.AddSeconds(3))!;

        engine.ApplyPut(batch, new AccountReply(400, null, "bad-item", null), now.AddSeconds(3));

        Assert.Contains("cover/g9", engine.State.Held);
        Assert.Equal(AccountSyncEngine.HeldRefused, engine.State.HeldReasons["cover/g9"]);
    }
}
