using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// What the room's Home and Messages rows say from the connection alone, which fail silently when wrong: a spoiler or a raw
/// mention shown in a list, a blocked person's words on a row, an old page putting an older line over a newer one, a deleted
/// message's words staying, one row's author on another's; a muted server or an unseen channel listed as waiting, words from
/// before what was read shown for a new mention; voice rooms in a channel you can't see or the AFK channel, a room of friends
/// sorted under strangers, a big room sending every face.
/// </summary>
public sealed class DiscordNowTests
{
    private const string Me = "1", Ada = "2", Kai = "3", Dm = "50", Group = "51", Guild = "60", General = "61", Lobby = "62", Afk = "63", Secret = "64";

    private static string Ready(object? extra = null) => JsonSerializer.Serialize(new
    {
        user = new { id = Me, username = "me" },
        relationships = new object[] { new { id = Ada, type = 1, user = new { id = Ada, username = "ada" } } },
        private_channels = new object[]
        {
            new { id = Dm, type = 1, recipient_ids = new[] { Ada }, last_message_id = "100" },
            new { id = Group, type = 3, recipient_ids = new[] { Ada, Kai }, last_message_id = "90" },
        },
        users = new object[] { new { id = Ada, username = "ada", global_name = "Ada" }, new { id = Kai, username = "kai", global_name = "Kai" } },
        guilds = new object[]
        {
            new
            {
                id = Guild, name = "Night Raid", properties = new { name = "Night Raid", banner = "abc", afk_channel_id = Afk },
                roles = new object[] { new { id = Guild, permissions = "68608" /* view, send, history */ } },
                channels = new object[]
                {
                    new { id = General, type = 0, name = "general", last_message_id = "200" },
                    new { id = Lobby, type = 2, name = "Lobby", user_limit = 5 },
                    new { id = Afk, type = 2, name = "AFK" },
                    new { id = Secret, type = 2, name = "Secret", permission_overwrites = new object[] { new { id = Guild, type = 0, allow = "0", deny = "1024" } } },
                },
                voice_states = new object[]
                {
                    new { user_id = Kai, channel_id = Lobby },
                    new { user_id = Ada, channel_id = Lobby },
                    new { user_id = "4", channel_id = Afk },
                    new { user_id = "5", channel_id = Secret },
                },
                members = Array.Empty<object>(),
            },
        },
        read_state = new { entries = new object[] { new { id = General, last_message_id = "150", mention_count = 2 } } },
    });

    private static DiscordCache Cache()
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse(Ready());
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static List<(string Name, object Data)> Dispatch(DiscordCache cache, string type, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return cache.Apply(type, doc.RootElement.Clone());
    }

    private static string Message(string id, string channel, string author, string content, object? extra = null) => JsonSerializer.Serialize(new
    {
        id, channel_id = channel, content, author = new { id = author, username = author == Me ? "me" : author == Ada ? "ada" : "kai", global_name = author == Me ? "Me" : author == Ada ? "Ada" : "Kai" },
        mentions = extra ?? Array.Empty<object>(), attachments = Array.Empty<object>(),
    });

    private static JsonElement Home(DiscordCache cache) => JsonSerializer.SerializeToElement(cache.Home());

    private static JsonElement DmOf(DiscordCache cache, string id) => Home(cache).GetProperty("dms").EnumerateArray().First(d => d.GetProperty("id").GetString() == id);

    private static string? LineOf(DiscordCache cache, string id) =>
        DmOf(cache, id).TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.String ? line.GetString() : null;

    [Fact]
    public void ADmRowSaysTheLastMessageInPlainWords()
    {
        var cache = Cache();
        var events = Dispatch(cache, "MESSAGE_CREATE", Message("101", Dm, Ada, "see **this** ||secret||  <:wow:123>\nnext line"));
        Assert.Equal("see this Spoiler :wow: next line", LineOf(cache, Dm));
        Assert.Contains(events, e => e.Name == "discord.dmLine");
    }

    [Fact]
    public void YoursAndAGroupsAreNamed()
    {
        var cache = Cache();
        Dispatch(cache, "MESSAGE_CREATE", Message("101", Dm, Me, "on my way"));
        Dispatch(cache, "MESSAGE_CREATE", Message("91", Group, Kai, "hello"));
        Assert.Equal("You: on my way", LineOf(cache, Dm));
        Assert.Equal("Kai: hello", LineOf(cache, Group));
    }

    [Fact]
    public void ABlockedPersonSaysNothing()
    {
        var cache = Cache();
        Dispatch(cache, "RELATIONSHIP_ADD", """{ "id": "2", "type": 2 }""");
        Dispatch(cache, "MESSAGE_CREATE", Message("101", Dm, Ada, "hello"));
        // A blocked person's DM is not in Home at all (its row says nothing, and it is not on the rail either).
        Assert.DoesNotContain(Home(cache).GetProperty("dms").EnumerateArray(), d => d.GetProperty("id").GetString() == Dm);
    }

    [Fact]
    public void AnOlderReadNeverReplacesANewerLine()
    {
        var cache = Cache();
        Dispatch(cache, "MESSAGE_CREATE", Message("105", Dm, Ada, "newest"));
        using var page = JsonDocument.Parse("[" + Message("101", Dm, Ada, "older") + "]");
        Assert.Empty(cache.KeepNewest(Dm, page.RootElement));
        Assert.Equal("newest", LineOf(cache, Dm));
    }

    [Fact]
    public void AReadOfTheNewestPageFillsTheRow()
    {
        var cache = Cache();
        using var page = JsonDocument.Parse("[" + Message("100", Dm, Ada, "the newest") + "," + Message("99", Dm, Ada, "before it") + "]");
        Assert.Single(cache.KeepNewest(Dm, page.RootElement));
        Assert.Equal("the newest", LineOf(cache, Dm));
    }

    [Fact]
    public void ADeletedOrEditedLastMessageChangesTheRow()
    {
        var cache = Cache();
        Dispatch(cache, "MESSAGE_CREATE", Message("101", Dm, Ada, "first"));
        Dispatch(cache, "MESSAGE_UPDATE", Message("101", Dm, Ada, "edited"));
        Assert.Equal("edited", LineOf(cache, Dm));
        Dispatch(cache, "MESSAGE_DELETE", JsonSerializer.Serialize(new { id = "101", channel_id = Dm }));
        Assert.Null(LineOf(cache, Dm));
    }

    [Fact]
    public void WhenALastMessageWasComesFromItsId()
    {
        var at = DmOf(Cache(), Dm).GetProperty("lastAt").GetInt64();
        Assert.Equal(1420070400000L, at); // id 100 is the first moment of Discord's epoch, to the millisecond.
    }

    [Fact]
    public void VoiceRoomsLeaveOutAfkAndWhatYouCannotSeeAndPutFriendsFirst()
    {
        var room = Assert.Single(JsonSerializer.SerializeToElement(Cache().VoiceNow()).EnumerateArray());
        Assert.Equal(Lobby, room.GetProperty("channelId").GetString());
        Assert.Equal("Night Raid", room.GetProperty("guild").GetString());
        Assert.Equal(2, room.GetProperty("count").GetInt32());
        Assert.Equal(1, room.GetProperty("friends").GetInt32());
        Assert.Equal(5, room.GetProperty("limit").GetInt32());
        Assert.Equal(Ada, room.GetProperty("people")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void ABigRoomSendsAFewFaces()
    {
        var cache = Cache();
        for (var i = 0; i < 20; i++)
            Dispatch(cache, "VOICE_STATE_UPDATE", JsonSerializer.Serialize(new { guild_id = Guild, channel_id = Lobby, user_id = (1000 + i).ToString() }));
        var room = Assert.Single(JsonSerializer.SerializeToElement(cache.VoiceNow()).EnumerateArray());
        Assert.Equal(22, room.GetProperty("count").GetInt32());
        Assert.Equal(8, room.GetProperty("people").GetArrayLength());
    }

    [Fact]
    public void HomeCarriesTheBanner()
    {
        var guild = Home(Cache()).GetProperty("guilds")[0];
        Assert.Equal("abc", guild.GetProperty("banner").GetString());
    }
}
