using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Models;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// The friend link's pure parts. Ways it could fail, none of which shows as an error:
/// - two friends derive different topics or keys, so they silently never meet;
/// - a third party, a server-mate who copies a friend's key, or someone with another key reads or joins the pair's topic;
/// - a key from a presence that isn't a real point crashes the session, or a malformed activity reads as a key;
/// - a message is replayed in the other direction or later, or moved to another pair, and shows an old game;
/// - a message's size says how long its title is;
/// - a newer Exo's section breaks an older one, or one bad section loses the good ones;
/// - a title a friend's Exo couldn't have sent (too long, control characters, a bidi override, a lone surrogate) reaches the page;
/// - the key isn't on the person's real custom status, changes that status, or comes back from Discord unread;
/// - the switch's old opt-in list is read as the new opt-out list, turning off the people who chose sharing;
/// - Exo goes on keeping the account's own Share my activity off, or turns it off for those who chose it;
/// - a friend's game shown after they stopped being a friend, or a game kept when its link ended.
/// </summary>
public sealed class FriendLinkTests
{
    internal const string A = "111111111111111111", B = "222222222222222222", C = "333333333333333333";
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    private static (FriendLinkFormat.Keys Low, FriendLinkFormat.Keys High) Pair()
    {
        using var a = NewKey();
        using var b = NewKey();
        return (FriendLinkFormat.Derive(a, A, B, FriendLinkFormat.EncodeKey(b))!, FriendLinkFormat.Derive(b, B, A, FriendLinkFormat.EncodeKey(a))!);
    }

    private static Dictionary<string, JsonNode?> Game(string title = "Hades II") =>
        new() { [FriendGame.Section] = new JsonObject { ["title"] = title, ["start"] = Now - 1000 } };

    [Fact]
    public void TheKeyTravelsInFortyFourCharactersAndAPointComesBackWhole()
    {
        using var key = NewKey();
        var text = FriendLinkFormat.EncodeKey(key);
        Assert.Equal(FriendLinkFormat.KeyChars, text.Length);
        Assert.True(FriendLinkFormat.LooksLikeKey(text));
    }

    [Fact]
    public void BothSidesDeriveTheSameTopicAndKeyAndOppositeDirections()
    {
        // Over enough keys for both parities of y: a wrong root or sign would differ between the two sides.
        for (var i = 0; i < 32; i++)
        {
            var (low, high) = Pair();
            Assert.Equal(low.Topic, high.Topic);
            Assert.Equal(low.Key, high.Key);
            Assert.Matches("^[0-9a-f]{32}$", low.Topic);
            Assert.Equal(0, low.Direction);
            Assert.Equal(1, high.Direction);
        }
    }

    [Fact]
    public void AThirdPartysKeyGivesAnotherTopicAndCantOpenAMessage()
    {
        using var a = NewKey();
        using var b = NewKey();
        using var c = NewKey();
        var ab = FriendLinkFormat.Derive(a, A, B, FriendLinkFormat.EncodeKey(b))!;
        var ca = FriendLinkFormat.Derive(c, C, A, FriendLinkFormat.EncodeKey(a))!;
        Assert.NotEqual(ab.Topic, ca.Topic);
        Assert.Null(FriendLinkFormat.Open(ca, FriendLinkFormat.Seal(ab, Now, false, Game())!));
    }

    [Fact]
    public void SomeoneShowingAFriendsKeyAsTheirOwnGetsAnotherTopic()
    {
        using var a = NewKey();
        using var b = NewKey();
        // C copies B's public key into C's presence; A derives with it, believing it is C's.
        var withB = FriendLinkFormat.Derive(a, A, B, FriendLinkFormat.EncodeKey(b))!;
        var withCopy = FriendLinkFormat.Derive(a, A, C, FriendLinkFormat.EncodeKey(b))!;
        Assert.NotEqual(withB.Topic, withCopy.Topic);
        Assert.NotEqual(withB.Key, withCopy.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // prefix 0: not a compressed point
    [InlineData("BP__________________________________________")] // x past the field
    [InlineData("A!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // not base64url
    public void AKeyThatIsntAPointIsRefused(string text)
    {
        using var a = NewKey();
        Assert.Null(FriendLinkFormat.Derive(a, A, B, text));
    }

    [Fact]
    public void AnXWithNoPointOnTheCurveIsRefusedAndOneWithAPointIsnt()
    {
        using var a = NewKey();
        // About half of all x have a point. Small ones are enough to see both.
        var answers = Enumerable.Range(1, 24).Select(x =>
        {
            var point = new byte[33];
            point[0] = 2;
            point[32] = (byte)x;
            return FriendLinkFormat.Derive(a, A, B, Convert.ToBase64String(point).Replace('+', '-').Replace('/', '_')) is not null;
        }).ToList();
        Assert.Contains(true, answers);
        Assert.Contains(false, answers);
    }

    [Fact]
    public void OnesOwnKeyOrIdOrAnIdThatIsntOneIsRefused()
    {
        using var a = NewKey();
        using var b = NewKey();
        Assert.Null(FriendLinkFormat.Derive(a, A, B, FriendLinkFormat.EncodeKey(a)));
        Assert.Null(FriendLinkFormat.Derive(a, A, A, FriendLinkFormat.EncodeKey(b)));
        Assert.Null(FriendLinkFormat.Derive(a, A, "not-an-id", FriendLinkFormat.EncodeKey(b)));
    }

    [Fact]
    public void AMessageOpensForTheOtherSideOnlyNotBackAtItsSender()
    {
        var (low, high) = Pair();
        var sealedByLow = FriendLinkFormat.Seal(low, Now, true, Game())!;
        var opened = FriendLinkFormat.Open(high, sealedByLow);
        Assert.NotNull(opened);
        Assert.True(opened.Hello);
        Assert.Equal(Now, opened.AtMs);
        Assert.Equal("Hades II", FriendGame.Read(opened, Now)!.Title);
        Assert.Null(FriendLinkFormat.Open(low, sealedByLow));
    }

    [Fact]
    public void AChangedOrCutMessageDoesntOpen()
    {
        var (low, high) = Pair();
        var data = FriendLinkFormat.Seal(low, Now, false, Game())!;
        foreach (var at in new[] { 0, 1, 20, data.Length - 1 })
        {
            var changed = (byte[])data.Clone();
            changed[at] ^= 1;
            Assert.Null(FriendLinkFormat.Open(high, changed));
        }
        Assert.Null(FriendLinkFormat.Open(high, data[..^1]));
        Assert.Null(FriendLinkFormat.Open(high, []));
        Assert.Null(FriendLinkFormat.Open(high, new byte[data.Length]));
    }

    [Fact]
    public void EveryMessageIsTheSameSizeWhateverItSays()
    {
        var (low, _) = Pair();
        int Size(IEnumerable<KeyValuePair<string, JsonNode?>> sections) => FriendLinkFormat.Seal(low, Now, false, sections)!.Length;
        var nothing = Size(new Dictionary<string, JsonNode?> { [FriendGame.Section] = null });
        Assert.Equal(nothing, Size(Game("x")));
        Assert.Equal(nothing, Size(Game(new string('x', 128))));
        Assert.Equal(nothing, Size(Game(string.Concat(Enumerable.Repeat("\U0001F600", 64)))));
        Assert.True(nothing <= 2048, "the relay takes 2048 bytes");
    }

    [Fact]
    public void AMessageTooBigToSealIsntSent()
    {
        var (low, _) = Pair();
        Assert.Null(FriendLinkFormat.Seal(low, Now, false, new Dictionary<string, JsonNode?> { ["big"] = new string('x', FriendLinkFormat.PlainBytes) }));
    }

    [Fact]
    public void AnOlderOrFarOffMessageIsRefusedAndTheLastOneAgainIsNot()
    {
        var receipt = new FriendLinkFormat.Receipt();
        Assert.True(receipt.Accept(Now, Now));
        Assert.False(receipt.Accept(Now - 1, Now));                                  // replayed from before the last
        Assert.True(receipt.Accept(Now, Now + 1000));                                // what a reconnect is handed
        Assert.True(receipt.Accept(Now + 5, Now + 1000));
        Assert.False(new FriendLinkFormat.Receipt().Accept(Now - FriendLinkFormat.FreshMs - 1, Now));
        Assert.False(new FriendLinkFormat.Receipt().Accept(Now + FriendLinkFormat.FreshMs + 1, Now));
    }

    [Fact]
    public void ANewerExosSectionsAreIgnoredAndTheGameStillReads()
    {
        var (low, high) = Pair();
        var sections = Game();
        sections["song"] = new JsonObject { ["title"] = "Get Lucky", ["whatever"] = new JsonArray(1, 2, 3) };
        sections["invite"] = "anything";
        var opened = FriendLinkFormat.Open(high, FriendLinkFormat.Seal(low, Now, false, sections)!);
        Assert.Equal("Hades II", FriendGame.Read(opened, Now)!.Title);
    }

    [Fact]
    public void ABadGameSectionLosesOnlyItself()
    {
        var (low, high) = Pair();
        var opened = FriendLinkFormat.Open(high, FriendLinkFormat.Seal(low, Now, false, new Dictionary<string, JsonNode?>
        {
            [FriendGame.Section] = new JsonObject { ["title"] = 42 },
            ["other"] = "still here",
        })!);
        Assert.NotNull(opened);
        Assert.Null(FriendGame.Read(opened, Now));
        Assert.Equal("still here", opened.Sections["other"].GetString());
    }

    [Theory]
    [InlineData("{\"title\":\"\",\"start\":0}")]
    [InlineData("{\"title\":\"   \",\"start\":0}")]
    [InlineData("{\"start\":0}")]
    [InlineData("{\"title\":\"x\"}")]
    [InlineData("{\"title\":\"x\",\"start\":\"soon\"}")]
    [InlineData("{\"title\":\"a\\u0007b\",\"start\":0}")]
    [InlineData("{\"title\":\"a\\u202Eb\",\"start\":0}")]
    [InlineData("{\"title\":\"a\\uD800b\",\"start\":0}")]
    [InlineData("\"Hades II\"")]
    [InlineData("[1,2]")]
    public void AGameThatExoWouldntSendIsDropped(string json)
    {
        Assert.Null(FriendGame.Read(Message(json.Replace("\"start\":0", "\"start\":" + Now)), Now));
    }

    [Fact]
    public void ATitleOverTheBoundOrAStartFarOffIsDropped()
    {
        Assert.Null(FriendGame.Read(Message($"{{\"title\":\"{new string('x', 129)}\",\"start\":{Now}}}"), Now));
        Assert.NotNull(FriendGame.Read(Message($"{{\"title\":\"{new string('x', 128)}\",\"start\":{Now}}}"), Now));
        Assert.Null(FriendGame.Read(Message($"{{\"title\":\"x\",\"start\":{Now + 6 * 60_000}}}"), Now));
        Assert.Null(FriendGame.Read(Message($"{{\"title\":\"x\",\"start\":{Now - 8L * 24 * 3_600_000}}}"), Now));
        Assert.Null(FriendGame.Read(Message("null"), Now));
        Assert.Null(FriendGame.Read(null, Now));
    }

    private static FriendLinkFormat.Message Message(string gameJson)
    {
        using var document = JsonDocument.Parse(gameJson);
        return new(Now, false, new Dictionary<string, JsonElement> { [FriendGame.Section] = document.RootElement.Clone() });
    }

    [Fact]
    public void WhatIsSentOfAGameIsItsCleanTitleAndStartAndNeverItsStoreId()
    {
        var section = FriendGame.Write(new DiscordGamesOpened.Game("steam:1", "  Ha\u0007des‮ II ", 5))!;
        Assert.Equal("Hades II", section["title"]!.GetValue<string>());
        Assert.Equal(5, section["start"]!.GetValue<long>());
        Assert.Equal(2, section.Count);
        Assert.Null(FriendGame.Write(null));
        Assert.Null(FriendGame.Write(new("steam:1", " \u0007 ", 5)));
        // Cut at the bound without splitting a pair.
        var cut = FriendGame.Write(new("steam:1", new string('x', 127) + "\U0001F600", 5))!["title"]!.GetValue<string>();
        Assert.Equal(new string('x', 127), cut);
    }

    // ---- the carrier ----

    internal const string Key = "A7Sx1n" + "Q2oXJ0" + "m3yV5f" + "G8hK2d" + "L4sP6t" + "U9wE1r" + "T3yU5i" + "O7";

    [Fact]
    public void TheKeyGoesOnARealCustomStatusAndChangesNothingElseOfIt()
    {
        var custom = DiscordSession.CustomActivity(JsonNode.Parse("{\"text\":\"brb\",\"emojiName\":\"\U0001F600\"}"), 0)!;
        var before = custom.DeepClone().AsObject();
        var sent = FriendKeyCarrier.Put(custom, Key);
        Assert.Equal(Key, sent["party"]!["id"]!.GetValue<string>());
        sent.Remove("party");
        Assert.True(JsonNode.DeepEquals(before, sent));
    }

    [Fact]
    public void WithNoStatusTheKeyGoesOnACustomStatusThatSaysNothing()
    {
        var sent = FriendKeyCarrier.Put(null, Key);
        Assert.Equal(4, sent["type"]!.GetValue<int>());
        Assert.False(sent.ContainsKey("state"));
        Assert.False(sent.ContainsKey("emoji"));
        Assert.Equal(Key, sent["party"]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData($"{{\"activities\":[{{\"type\":4,\"party\":{{\"id\":\"{Key}\"}}}}]}}", true)]
    [InlineData($"{{\"hidden_activities\":[{{\"type\":4,\"party\":{{\"id\":\"{Key}\"}}}}]}}", true)]
    [InlineData($"{{\"activities\":[{{\"type\":0,\"party\":{{\"id\":\"{Key}\"}}}}]}}", false)] // a game's party, whatever it says
    [InlineData("{\"activities\":[{\"type\":4,\"party\":{\"id\":\"spotify:1\"}}]}", false)]
    [InlineData("{\"activities\":[{\"type\":4,\"party\":{\"id\":42}}]}", false)]
    [InlineData("{\"activities\":[{\"type\":4,\"party\":\"x\"}]}", false)]
    [InlineData("{\"activities\":[{\"type\":\"4\"}]}", false)]
    [InlineData("{\"activities\":[1,null,\"x\"]}", false)]
    [InlineData("{\"activities\":{}}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public void AKeyIsReadOnlyFromWhereItGoes(string presence, bool found)
    {
        using var document = JsonDocument.Parse(presence);
        Assert.Equal(found ? Key : null, FriendKeyCarrier.Read(document.RootElement));
    }

    [Fact]
    public void TheEchoSaysWhetherTheKeyCameBackAndWhichFieldsDid()
    {
        JsonElement Sessions(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        var kept = Sessions($"[{{\"session_id\":\"all\",\"activities\":[{{\"type\":4,\"name\":\"Custom Status\",\"party\":{{\"id\":\"{Key}\"}}}}]}}]");
        Assert.Equal(FriendKeyCarrier.Echo.Shown, FriendKeyCarrier.Find(kept, Key));
        Assert.Equal("name, party, type", FriendKeyCarrier.FieldsKept(kept));
        var stripped = Sessions("[{\"session_id\":\"all\",\"activities\":[{\"type\":4,\"name\":\"Custom Status\"}]}]");
        Assert.Equal(FriendKeyCarrier.Echo.Absent, FriendKeyCarrier.Find(stripped, Key));
        Assert.Equal("name, type", FriendKeyCarrier.FieldsKept(stripped));
        Assert.Equal(FriendKeyCarrier.Echo.Hidden, FriendKeyCarrier.Find(Sessions($"[{{\"hidden_activities\":[{{\"type\":4,\"party\":{{\"id\":\"{Key}\"}}}}]}}]"), Key));
        Assert.Equal(FriendKeyCarrier.Echo.Absent, FriendKeyCarrier.Find(Sessions("[]"), Key));
        Assert.Equal("none", FriendKeyCarrier.FieldsKept(Sessions("{}")));
    }

    [Theory]
    [InlineData(4002, 100, true)]
    [InlineData(4000, 4999, true)]
    [InlineData(4002, 5000, false)] // long after: not the key's doing
    [InlineData(1000, 100, false)]
    [InlineData(4004, 100, false)]
    [InlineData(null, 100, false)]
    public void OnlyACloseRightAfterTheKeyBlamesIt(int? code, long ms, bool blamed) =>
        Assert.Equal(blamed, DiscordSession.KeyRefused(code, ms));

    // ---- the switch ----

    [Fact]
    public void NobodyIsSharingUntilTheyTurnItOn_AndTheOldListSharedToDiscordNotToFriends()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"DiscordShareGames\":[\"" + A + "\"]}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(old.DiscordGamesShared);
        Assert.DoesNotContain("\"DiscordShareGames\"", JsonSerializer.Serialize(old));
    }

    [Fact]
    public void ExoLeavesTheAccountsShareMyActivityAlone()
    {
        Assert.False(DiscordSession.KeptOff.ContainsKey("showCurrentGame"));
        foreach (var on in new[] { true, false })
        {
            var values = new JsonObject { ["showCurrentGame"] = on };
            foreach (var (name, value) in DiscordSession.KeptOff) values[name] = value;
            values["locale"] = "en-US";
            values["timestampHourCycle"] = 1;
            Assert.Empty(DiscordSession.PrivacyChanges(values, "en-US", "h:mm tt"));
        }
    }

    // ---- what a friend's Exo said, as the page reads it ----

    private static DiscordCache.Rich Spotify => new(2, "Spotify", "Get Lucky", "Daft Punk", 1, null);

    [Fact]
    public void AFriendsGameComesFirstAndIsMarkedAndGoesAgain()
    {
        var plain = new DiscordCache.Presence("online", "Daft Punk", 2, Spotify, [Spotify]);
        var merged = DiscordCache.WithExoGame(plain, new("Hades II", 5));
        Assert.Equal("Hades II", merged.Rich!.Name);
        Assert.True(merged.Rich.ThroughExo);
        Assert.Equal(0, merged.Kind);
        Assert.Equal("Hades II", merged.Activity);
        Assert.Equal(2, merged.All!.Count);
        Assert.Null(merged.Rich.Details); // no mark in any text
    }

    [Fact]
    public void ACustomStatusStaysTheLineAndAGameDiscordAlsoReportsIsOne()
    {
        var discordsOwn = new DiscordCache.Rich(0, "HADES II", "In Elysium", null, 2, "pic");
        var plain = new DiscordCache.Presence("online", "brb", 4, discordsOwn, [discordsOwn]);
        var merged = DiscordCache.WithExoGame(plain, new("Hades II", 5));
        Assert.Equal("brb", merged.Activity);
        Assert.Equal(4, merged.Kind);
        Assert.Single(merged.All!);
        Assert.Equal("In Elysium", merged.Rich!.Details);
        Assert.True(merged.Rich.ThroughExo);
    }

    internal static DiscordCache CacheWithFriends()
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse($$"""
            {
              "user": { "id": "{{A}}", "username": "me" },
              "guilds": [], "private_channels": [],
              "relationships": [
                { "id": "{{B}}", "type": 1, "user": { "id": "{{B}}", "username": "friend" } },
                { "id": "{{C}}", "type": 3, "user": { "id": "{{C}}", "username": "asking" } }
              ],
              "presences": [
                { "user": { "id": "{{B}}" }, "status": "online", "activities": [{ "type": 4, "name": "Custom Status", "party": { "id": "{{Key}}" } }] },
                { "user": { "id": "{{C}}" }, "status": "online", "activities": [{ "type": 4, "name": "Custom Status", "party": { "id": "{{Key}}" } }] }
              ]
            }
            """);
        cache.LoadReady(ready.RootElement);
        return cache;
    }

    [Fact]
    public void OnlyAnActualFriendWhoIsOnlineWithAKeyIsLinked()
    {
        var cache = CacheWithFriends();
        Assert.Equal((B, Key), Assert.Single(cache.LinkPeers()));
        Assert.True(cache.TakeLinkPeersChanged());
        Assert.False(cache.TakeLinkPeersChanged());
        using var offline = JsonDocument.Parse($"{{\"user\":{{\"id\":\"{B}\"}},\"status\":\"offline\",\"activities\":[]}}");
        cache.Apply("PRESENCE_UPDATE", offline.RootElement);
        Assert.Empty(cache.LinkPeers());
        Assert.True(cache.TakeLinkPeersChanged());
    }

    [Fact]
    public void AGameIsShownForAFriendOnlyAndGoesWhenTheLinksEnd()
    {
        var cache = CacheWithFriends();
        Assert.NotEmpty(cache.SetExoGame(B, new("Hades II", 5)));
        Assert.Empty(cache.SetExoGame(B, new("Hades II", 5)));  // nothing new: no event
        Assert.Empty(cache.SetExoGame(C, new("Hades II", 5)));  // a request, not a friend
        // A presence update while the game is known keeps it.
        using var update = JsonDocument.Parse($"{{\"user\":{{\"id\":\"{B}\"}},\"status\":\"idle\",\"activities\":[]}}");
        cache.Apply("PRESENCE_UPDATE", update.RootElement);
        var text = JsonSerializer.Serialize(cache.Friends());
        Assert.Contains("Hades II", text);
        Assert.NotEmpty(cache.ClearExoGames());
        Assert.DoesNotContain("Hades II", JsonSerializer.Serialize(cache.Friends()));
    }
}
