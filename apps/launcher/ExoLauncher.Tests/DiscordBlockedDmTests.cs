using System.Text.Json;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// A blocked person's direct message. Ways it shows: on the rail's unread conversations, in Home, or in Messages, all of
/// which read Home's DM list. A group DM that includes a blocked member stays (Discord keeps the group); a DM with an
/// unblocked person stays.
/// </summary>
public sealed class DiscordBlockedDmTests
{
    private static List<string> HomeDmIds()
    {
        var json = Load();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json.Home()));
        return doc.RootElement.GetProperty("dms").EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
    }

    [Fact]
    public void ABlockedPersonsDirectMessageIsNotInHome() =>
        Assert.DoesNotContain("d-blocked", HomeDmIds());

    [Fact]
    public void AnUnblockedPersonsDirectMessageStays() =>
        Assert.Contains("d-friend", HomeDmIds());

    [Fact]
    public void AGroupWithABlockedMemberStays() =>
        Assert.Contains("g-group", HomeDmIds());

    private static DiscordCache Load()
    {
        const string json = """
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [],
          "relationships": [ { "id": "200", "type": 2, "user": { "id": "200", "username": "blocked" } } ],
          "private_channels": [
            { "id": "d-blocked", "type": 1, "last_message_id": "900", "recipients": [ { "id": "200", "username": "blocked" } ] },
            { "id": "d-friend", "type": 1, "last_message_id": "800", "recipients": [ { "id": "300", "username": "friend" } ] },
            { "id": "g-group", "type": 3, "last_message_id": "700", "owner_id": "300", "recipients": [ { "id": "200", "username": "blocked" }, { "id": "300", "username": "friend" } ] }
          ],
          "read_state": { "entries": [] }
        }
        """;
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse(json);
        cache.LoadReady(doc.RootElement);
        return cache;
    }
}
