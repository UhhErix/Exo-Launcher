using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class DiscordLoadPerfTests
{
    private static byte[] CompressFlushed(ZLibStream writer, MemoryStream sink, string json)
    {
        var start = (int)sink.Position;
        var bytes = Encoding.UTF8.GetBytes(json);
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
        var end = (int)sink.Position;
        var chunk = new byte[end - start];
        sink.GetBuffer().AsSpan(start, chunk.Length).CopyTo(chunk);
        return chunk;
    }

    private static DiscordCache Load(string json)
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse(json);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    [Fact]
    public void GatewayZlibInflater_HasFlushSuffix_DetectsOnlyTrailingSyncFlush()
    {
        Assert.False(GatewayZlibInflater.HasFlushSuffix([]));
        Assert.False(GatewayZlibInflater.HasFlushSuffix([0x00, 0x00, 0xFF]));
        Assert.False(GatewayZlibInflater.HasFlushSuffix([0x00, 0x00, 0xFF, 0xFE]));
        Assert.True(GatewayZlibInflater.HasFlushSuffix([0x78, 0x9C, 0x00, 0x00, 0xFF, 0xFF]));
    }

    [Fact]
    public void GatewayZlibInflater_InflatesMultipleFlushedMessagesSharingDictionary()
    {
        using var sink = new MemoryStream();
        using var compressor = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);
        using var inflater = new GatewayZlibInflater();

        var payloads = new[]
        {
            """{"op":10,"d":{"heartbeat_interval":41250}}""",
            """{"op":0,"t":"READY","s":1,"d":{"session_id":"abc","guilds":[{"id":"1","name":"Alpha Gaming Server"},{"id":"2","name":"Alpha Gaming Server Backup"}]}}""",
            """{"op":0,"t":"PRESENCE_UPDATE","s":2,"d":{"user":{"id":"42"},"status":"online","guild_id":"1","activities":[{"name":"Alpha Gaming Server"}]}}""",
        };

        foreach (var expected in payloads)
        {
            var wire = CompressFlushed(compressor, sink, expected);
            Assert.True(GatewayZlibInflater.HasFlushSuffix(wire));
            using var doc = inflater.ParseCompressed(wire, wire.Length, 1024 * 1024, out var jsonBytes);
            Assert.Equal(Encoding.UTF8.GetByteCount(expected), jsonBytes);
            Assert.Equal(
                JsonDocument.Parse(expected).RootElement.GetProperty("op").GetInt32(),
                doc.RootElement.GetProperty("op").GetInt32());
            Assert.Equal(expected, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(doc.RootElement.GetRawText())));
        }
    }

    [Fact]
    public void GatewayZlibInflater_RejectsDecompressedPayloadExceedingMaxBytes()
    {
        using var sink = new MemoryStream();
        using var compressor = new ZLibStream(sink, CompressionLevel.SmallestSize, leaveOpen: true);
        using var inflater = new GatewayZlibInflater();

        var huge = "{\"pad\":\"" + new string('a', 65536) + "\"}";
        var wire = CompressFlushed(compressor, sink, huge);
        Assert.True(wire.Length < 512);

        Assert.Throws<IOException>(() => inflater.ParseCompressed(wire, wire.Length, 4096, out _));
    }

    [Fact]
    public void DiscordCache_DmByUserIndex_TracksReadyCreateDeleteRequestIgnoreAndGroupChanges()
    {
        var cache = Load("""
        {
          "session_id": "s",
          "user": { "id": "100", "username": "me" },
          "users": [
            { "id": "201", "username": "alice" },
            { "id": "202", "username": "bob" },
            { "id": "203", "username": "carol" },
            { "id": "204", "username": "dave" }
          ],
          "relationships": [
            { "id": "201", "type": 1 },
            { "id": "202", "type": 1 }
          ],
          "guilds": [],
          "private_channels": [
            { "id": "dm-201", "type": 1, "recipient_ids": ["201"] },
            { "id": "dm-203", "type": 1, "recipient_ids": ["203"] },
            { "id": "req-202", "type": 1, "recipient_ids": ["202"], "is_message_request": true },
            { "id": "gdm-1", "type": 3, "recipient_ids": ["203", "204"] }
          ],
          "read_state": { "entries": [] }
        }
        """);

        Dictionary<string, string?> FriendDms() =>
            JsonSerializer.SerializeToElement(cache.Friends())
                .EnumerateArray()
                .ToDictionary(
                    f => f.GetProperty("id").GetString()!,
                    f => f.GetProperty("dmId").ValueKind == JsonValueKind.Null ? null : f.GetProperty("dmId").GetString());

        var initial = FriendDms();
        Assert.Equal("dm-201", initial["201"]);
        Assert.Equal("req-202", initial["202"]);

        using (var presence = JsonDocument.Parse("""{"guild_id":"9","user":{"id":"203"},"status":"online","activities":[]}"""))
        {
            var events = cache.Apply("PRESENCE_UPDATE", presence.RootElement);
            Assert.Contains(events, e => e.Name == "discord.presence");
        }

        cache.Answer("req-202", accepted: false);
        Assert.Null(FriendDms()["202"]);

        using (var created = JsonDocument.Parse("""{"id":"dm-202-new","type":1,"recipients":[{"id":"202","username":"bob"}]}"""))
            cache.Apply("CHANNEL_CREATE", created.RootElement);
        Assert.Equal("dm-202-new", FriendDms()["202"]);

        using (var deleted = JsonDocument.Parse("""{"id":"dm-201","type":1,"recipients":[{"id":"201","username":"alice"}]}"""))
            cache.Apply("CHANNEL_DELETE", deleted.RootElement);
        Assert.Null(FriendDms()["201"]);

        using (var deleted203 = JsonDocument.Parse("""{"id":"dm-203","type":1,"recipients":[{"id":"203","username":"carol"}]}"""))
            cache.Apply("CHANNEL_DELETE", deleted203.RootElement);
        using (var ignoredPresence = JsonDocument.Parse("""{"guild_id":"9","user":{"id":"203"},"status":"dnd","activities":[]}"""))
        {
            var events = cache.Apply("PRESENCE_UPDATE", ignoredPresence.RootElement);
            Assert.DoesNotContain(events, e => e.Name == "discord.presence");
        }

        using (var shrunk = JsonDocument.Parse("""{"channel_id":"gdm-1","user":{"id":"204","username":"dave"}}"""))
            cache.Apply("CHANNEL_RECIPIENT_REMOVE", shrunk.RootElement);
        using (var watchedAgain = JsonDocument.Parse("""{"guild_id":"9","user":{"id":"203"},"status":"idle","activities":[]}"""))
        {
            var events = cache.Apply("PRESENCE_UPDATE", watchedAgain.RootElement);
            Assert.Contains(events, e => e.Name == "discord.presence");
        }

        using (var regrown = JsonDocument.Parse("""{"channel_id":"gdm-1","user":{"id":"204","username":"dave"}}"""))
            cache.Apply("CHANNEL_RECIPIENT_ADD", regrown.RootElement);
        using (var unwatchedAgain = JsonDocument.Parse("""{"guild_id":"9","user":{"id":"203"},"status":"online","activities":[]}"""))
        {
            var events = cache.Apply("PRESENCE_UPDATE", unwatchedAgain.RootElement);
            Assert.DoesNotContain(events, e => e.Name == "discord.presence");
        }
    }

    [Fact]
    public void DiscordCache_Unreads_FiltersHiddenReadAndMutedChannelsAndOrdersNewestFirst()
    {
        var cache = Load("""
        {
          "session_id": "s",
          "user": { "id": "100", "username": "me" },
          "guilds": [
            {
              "id": "1", "name": "Active", "owner_id": "9",
              "roles": [{ "id": "1", "permissions": "1024" }],
              "members": [{ "user": { "id": "100" }, "roles": [] }],
              "channels": [
                { "id": "10", "type": 0, "name": "read-ch", "last_message_id": "500" },
                { "id": "11", "type": 0, "name": "unread-older", "last_message_id": "600" },
                { "id": "12", "type": 0, "name": "unread-newer", "last_message_id": "800" },
                { "id": "13", "type": 0, "name": "hidden-unread", "last_message_id": "900",
                  "permission_overwrites": [{ "id": "1", "type": 0, "allow": "0", "deny": "1024" }] }
              ]
            },
            {
              "id": "2", "name": "MutedGuild", "owner_id": "9",
              "roles": [{ "id": "2", "permissions": "1024" }],
              "members": [{ "user": { "id": "100" }, "roles": [] }],
              "channels": [
                { "id": "20", "type": 0, "name": "muted-unread", "last_message_id": "950" }
              ]
            }
          ],
          "private_channels": [],
          "read_state": {
            "entries": [
              { "id": "10", "last_message_id": "500" },
              { "id": "11", "last_message_id": "400" },
              { "id": "12", "last_message_id": "400" },
              { "id": "13", "last_message_id": "400" },
              { "id": "20", "last_message_id": "400" }
            ]
          },
          "user_guild_settings": {
            "entries": [
              { "guild_id": "2", "muted": true }
            ]
          }
        }
        """);

        var unreads = JsonSerializer.SerializeToElement(cache.Unreads(10))
            .EnumerateArray()
            .Select(x => x.GetProperty("channelId").GetString()!)
            .ToArray();

        Assert.Equal(["12", "11"], unreads);
    }

    [Fact]
    public void GatewayZlibInflater_InflatesLargeMultiChunkReadyFollowedByEvents()
    {
        using var sink = new MemoryStream();
        using var compressor = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);
        using var inflater = new GatewayZlibInflater();

        var guilds = string.Join(",", Enumerable.Range(0, 600).Select(i =>
            $"{{\"id\":\"{100000 + i}\",\"name\":\"Server Number {i} Gaming Community\",\"channels\":[{{\"id\":\"{200000 + i}\",\"type\":0,\"name\":\"general-chat-{i}\"}}]}}"));
        var bigReady = "{\"op\":0,\"t\":\"READY\",\"s\":1,\"d\":{\"session_id\":\"big\",\"guilds\":[" + guilds + "]}}";
        var followUp = """{"op":0,"t":"PRESENCE_UPDATE","s":2,"d":{"user":{"id":"42"},"status":"online","activities":[{"name":"Server Number 0 Gaming Community"}]}}""";

        var wireReady = CompressFlushed(compressor, sink, bigReady);
        Assert.True(wireReady.Length < Encoding.UTF8.GetByteCount(bigReady) / 4);
        Assert.True(GatewayZlibInflater.HasFlushSuffix(wireReady));

        using (var doc = inflater.ParseCompressed(wireReady, wireReady.Length, 4 * 1024 * 1024, out var jsonBytes))
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(bigReady), jsonBytes);
            Assert.Equal("READY", doc.RootElement.GetProperty("t").GetString());
            Assert.Equal(600, doc.RootElement.GetProperty("d").GetProperty("guilds").GetArrayLength());
        }

        var wireEvent = CompressFlushed(compressor, sink, followUp);
        using (var doc2 = inflater.ParseCompressed(wireEvent, wireEvent.Length, 1024 * 1024, out var jsonBytes2))
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(followUp), jsonBytes2);
            Assert.Equal("PRESENCE_UPDATE", doc2.RootElement.GetProperty("t").GetString());
        }
    }

    [Fact]
    public void DiscordRest_EvaluateKeptBuild_DistinguishesFreshStaleAndExpired()
    {
        var fresh = DiscordRest.EvaluateKeptBuild("679055", TimeSpan.FromHours(6));
        Assert.Equal((679055, false), fresh);

        var stale = DiscordRest.EvaluateKeptBuild("679055\n", TimeSpan.FromDays(3));
        Assert.Equal((679055, true), stale);

        var expired = DiscordRest.EvaluateKeptBuild("679055", TimeSpan.FromDays(15));
        Assert.Equal((null, false), expired);

        var invalid = DiscordRest.EvaluateKeptBuild("not-a-number", TimeSpan.FromHours(1));
        Assert.Equal((null, false), invalid);

        var zero = DiscordRest.EvaluateKeptBuild("0", TimeSpan.FromHours(1));
        Assert.Equal((null, false), zero);
    }
}
