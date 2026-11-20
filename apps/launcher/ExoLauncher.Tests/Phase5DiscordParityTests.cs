using System.Text.Json;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Regression tests for Phase 5.3 Discord protocol parity:
/// - Stage channel moderation permission checks and STAGE_INSTANCE_CREATE/UPDATE/DELETE lifecycle;
/// - Read-only EMBEDDED_ACTIVITY_UPDATE / V2 and READY_SUPPLEMENTAL voice channel activity status;
/// - discord.gift/code and discord.com/gifts/code parsing and path-traversal rejection.
/// </summary>
public sealed class Phase5DiscordParityTests
{
    private const ulong ViewChannel = 1UL << 10;
    private const ulong ManageChannels = 1UL << 4;
    private const ulong MuteMembers = 1UL << 22;
    private const ulong MoveMembers = 1UL << 24;

    private static DiscordCache ReadyWithRoles(ulong everyonePerms, ulong role50Perms, params string[] myRoles)
    {
        var cache = new DiscordCache();
        var rolesJson = myRoles.Length == 0 ? "" : string.Join(",", myRoles.Select(r => $"\"{r}\""));
        var json = $$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "9",
            "roles": [
              { "id": "1", "permissions": "{{everyonePerms}}" },
              { "id": "50", "permissions": "{{role50Perms}}" }
            ],
            "members": [ { "user": { "id": "100" }, "roles": [{{rolesJson}}] } ],
            "channels": [
              { "id": "10", "type": 0, "name": "general", "position": 0 },
              { "id": "20", "type": 2, "name": "Lounge", "position": 1 },
              { "id": "30", "type": 13, "name": "Town Hall", "position": 2 }
            ]
          }],
          "private_channels": []
        }
        """;
        using var doc = JsonDocument.Parse(json);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static JsonElement ChannelById(DiscordCache cache, string channelId) =>
        JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels").EnumerateArray()
            .First(c => c.GetProperty("id").GetString() == channelId);

    [Fact]
    public void CanModerateStageRequiresStageChannelAndModeratorPermissions()
    {
        var listener = ReadyWithRoles(ViewChannel, 0);
        Assert.False(listener.CanModerateStage("30"));
        Assert.False(listener.CanModerateStage("10"));

        var manage = ReadyWithRoles(ViewChannel, ManageChannels, "50");
        Assert.True(manage.CanModerateStage("30"));
        Assert.False(manage.CanModerateStage("10"));
        Assert.False(manage.CanModerateStage("20"));

        var mute = ReadyWithRoles(ViewChannel, MuteMembers, "50");
        Assert.True(mute.CanModerateStage("30"));

        var move = ReadyWithRoles(ViewChannel, MoveMembers, "50");
        Assert.True(move.CanModerateStage("30"));
    }

    [Fact]
    public void StageInstanceGatewayEventsCreateUpdateAndDeleteTopic()
    {
        var cache = ReadyWithRoles(ViewChannel, ManageChannels, "50");

        using var create = JsonDocument.Parse("""{ "guild_id": "1", "channel_id": "30", "topic": "Dev Q&A" }""");
        var createEvents = cache.Apply("STAGE_INSTANCE_CREATE", create.RootElement);
        Assert.Contains(createEvents, e => e.Name == "discord.changed");
        Assert.Equal("Dev Q&A", ChannelById(cache, "30").GetProperty("stageTopic").GetString());
        Assert.True(ChannelById(cache, "30").GetProperty("canModerateStage").GetBoolean());

        using var update = JsonDocument.Parse("""{ "guild_id": "1", "channel_id": "30", "topic": "Patch Notes Live" }""");
        cache.Apply("STAGE_INSTANCE_UPDATE", update.RootElement);
        Assert.Equal("Patch Notes Live", ChannelById(cache, "30").GetProperty("stageTopic").GetString());

        using var delete = JsonDocument.Parse("""{ "guild_id": "1", "channel_id": "30" }""");
        cache.Apply("STAGE_INSTANCE_DELETE", delete.RootElement);
        Assert.Equal(JsonValueKind.Null, ChannelById(cache, "30").GetProperty("stageTopic").ValueKind);
    }

    [Fact]
    public void EmbeddedActivitiesTrackAcrossReadySupplementalAndV1V2Updates()
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse("""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "9",
            "roles": [{ "id": "1", "permissions": "1024" }],
            "members": [{ "user": { "id": "100" }, "roles": [] }],
            "channels": [
              { "id": "20", "type": 2, "name": "Lounge", "position": 0 },
              { "id": "21", "type": 2, "name": "Squad", "position": 1 }
            ],
            "embedded_activities": [
              { "channel_id": "20", "embedded_activity": { "name": "Poker Night" }, "users": ["100"] }
            ]
          }],
          "private_channels": []
        }
        """);
        cache.LoadReady(ready.RootElement);
        Assert.Equal("Poker Night", ChannelById(cache, "20").GetProperty("activity").GetString());

        using var supp = JsonDocument.Parse("""
        {
          "guilds": [{
            "id": "1",
            "embedded_activities": [
              { "channel_id": "21", "activity": { "name": "Watch Together" }, "users": ["101", "102"] }
            ]
          }]
        }
        """);
        var suppEvents = cache.Apply("READY_SUPPLEMENTAL", supp.RootElement);
        Assert.Contains(suppEvents, e => e.Name == "discord.changed");
        Assert.Equal(JsonValueKind.Null, ChannelById(cache, "20").GetProperty("activity").ValueKind);
        Assert.Equal("Watch Together", ChannelById(cache, "21").GetProperty("activity").GetString());

        using var v1Clear = JsonDocument.Parse("""{ "guild_id": "1", "channel_id": "21", "embedded_activity": { "name": "Watch Together" }, "users": [] }""");
        var clearEvents = cache.Apply("EMBEDDED_ACTIVITY_UPDATE", v1Clear.RootElement);
        Assert.Contains(clearEvents, e => e.Name == "discord.changed");
        Assert.Equal(JsonValueKind.Null, ChannelById(cache, "21").GetProperty("activity").ValueKind);

        using var v2Set = JsonDocument.Parse("""
        {
          "location": { "guild_id": "1", "channel_id": "20" },
          "embedded_activity": { "name": "Chess in the Park" },
          "participants": [{ "user_id": "100" }]
        }
        """);
        var v2Events = cache.Apply("EMBEDDED_ACTIVITY_UPDATE_V2", v2Set.RootElement);
        Assert.Contains(v2Events, e => e.Name == "discord.changed");
        Assert.Equal("Chess in the Park", ChannelById(cache, "20").GetProperty("activity").GetString());
    }

    [Theory]
    [InlineData("https://discord.gift/AbCdEfGh1234", "AbCdEfGh1234")]
    [InlineData("https://discord.com/gifts/Valid-Gift-Code_X", null)]
    [InlineData("https://discord.com/gifts/Valid-Gift-Code9", "Valid-Gift-Code9")]
    [InlineData("https://discordapp.com/gifts/MyGift123456?utm=chat#top", "MyGift123456")]
    [InlineData("BareCode1234", "BareCode1234")]
    [InlineData("short", null)]
    [InlineData("https://discord.gift/short", null)]
    [InlineData("../../SecretGift123", null)]
    [InlineData("https://discord.gift/../SecretGift123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GiftCodeParsesValidLinksAndRejectsInvalidOrTraversalInputs(string? raw, string? expected)
    {
        Assert.Equal(expected, WebHostBridge.GiftCode(raw));
    }
}
