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

    internal const string Key = "A7Sx1n" + "Q2oXJ0" + "m3yV5f" + "G8hK2d" + "L4sP6t" + "U9wE1r" + "T3yU5i" + "O7";

    [Fact]
    public void CustomStatusCarriesNoFriendLinkKeyOrFabricatedActivity()
    {
        Assert.Null(DiscordSession.CustomActivity(null, 0));
        var custom = DiscordSession.CustomActivity(JsonNode.Parse("{\"text\":\"brb\",\"emojiName\":\"\U0001F600\"}"), 0)!;
        Assert.Equal(4, custom["type"]!.GetValue<int>());
        Assert.False(custom.ContainsKey("party"));
    }

    [Fact]
    public void DiscordApiHeadersStayOnDiscordApiRequests()
    {
        using var req = DiscordRest.BuildRequest(HttpMethod.Get, "users/@me", null);
        Assert.True(req.Headers.Contains("Origin"));
        Assert.NotNull(req.Headers.Referrer);
        Assert.True(req.Headers.Contains("X-Discord-Locale"));
        Assert.True(req.Headers.Contains("X-Discord-Timezone"));
        Assert.True(req.Headers.Contains("X-Debug-Options"));
        Assert.True(req.Headers.Contains("X-Super-Properties"));
        Assert.Equal("empty", req.Headers.GetValues("Sec-Fetch-Dest").Single());
        Assert.Equal("cors", req.Headers.GetValues("Sec-Fetch-Mode").Single());
        Assert.Equal("same-origin", req.Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("?0", req.Headers.GetValues("sec-ch-ua-mobile").Single());
        Assert.Equal("\"Windows\"", req.Headers.GetValues("sec-ch-ua-platform").Single());
        Assert.Contains("Google Chrome", req.Headers.GetValues("sec-ch-ua").Single(), StringComparison.Ordinal);
        Assert.Equal("focused", DiscordRest.Properties(focused: true)["client_app_state"]!.GetValue<string>());
        Assert.Equal("unfocused", DiscordRest.Properties(focused: false)["client_app_state"]!.GetValue<string>());
    }

    [Fact]
    public void DiscordStatusRoundTripsThroughPreloadedUserSettingsProto()
    {
        var patch = new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement("dnd") };
        var encoded = DiscordUserSettings.Patch([], patch);
        Assert.NotNull(encoded);
        var decoded = DiscordUserSettings.Decode(encoded!);
        Assert.Equal("dnd", decoded["status"]?.GetValue<string>());
    }

    [Fact]
    public void CacheHandlesSupplementalVoicePassiveUpdateV2ReactionManyAndObfuscatedChannels()
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse("""
            {
              "user": { "id": "1", "username": "me" },
              "users": [{ "id": "2", "username": "friend" }],
              "guilds": [{
                "id": "10",
                "properties": { "name": "Srv", "owner_id": "99" },
                "roles": [{ "id": "10", "permissions": "1024", "position": 0, "color": 0, "name": "@everyone" }],
                "channels": [
                  { "id": "100", "type": 0, "name": "general", "flags": 0 },
                  { "id": "101", "type": 0, "name": "___hidden___", "flags": 131072 },
                  { "id": "102", "type": 2, "name": "Voice", "flags": 0 }
                ]
              }]
            }
            """);
        cache.LoadReady(ready.RootElement);
        Assert.True(cache.CanSee("100"));
        Assert.False(cache.CanSee("101"));

        using var supp = JsonDocument.Parse("""
            {
              "guilds": [{
                "id": "10",
                "voice_states": [{ "user_id": "2", "channel_id": "102", "self_mute": false, "self_deaf": false }]
              }]
            }
            """);
        var suppEvents = cache.Apply("READY_SUPPLEMENTAL", supp.RootElement);
        Assert.Contains(suppEvents, e => e.Name == "discord.changed");

        using var passive = JsonDocument.Parse("""
            {
              "guild_id": "10",
              "removed_voice_states": ["2"],
              "updated_voice_states": []
            }
            """);
        var passiveEvents = cache.Apply("PASSIVE_UPDATE_V2", passive.RootElement);
        Assert.Contains(passiveEvents, e => e.Name == "discord.changed");

        using var many = JsonDocument.Parse("""
            {
              "channel_id": "100",
              "message_id": "500",
              "reactions": [{ "emoji": { "name": "🔥" }, "users": ["1", "2"] }]
            }
            """);
        var reactEvents = cache.Apply("MESSAGE_REACTION_ADD_MANY", many.RootElement);
        Assert.Equal(2, reactEvents.Count(e => e.Name == "discord.reaction"));
    }

    // ---- no switch (3.26.0) ----

    [Fact]
    public void TheOldListsOfWhoTurnedSharingOnAreReadByNothing()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"DiscordShareGames\":[\"" + A + "\"],\"DiscordGamesShared\":[\"" + A + "\"]}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var written = JsonSerializer.Serialize(old);
        Assert.DoesNotContain("\"DiscordShareGames\"", written);
        Assert.DoesNotContain("\"DiscordGamesShared\"", written);
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
    public void PresencePartyIdKeysAreIgnoredByLinkPeers()
    {
        var cache = CacheWithFriends();
        Assert.Empty(cache.LinkPeers());
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

    [Fact]
    public void EvaluateKeptMetaParsesBuildAndCapabilities()
    {
        var both = DiscordRest.EvaluateKeptMeta("681034 1767421", TimeSpan.FromHours(2));
        Assert.Equal(681034, both.Build);
        Assert.Equal(1767421, both.Capabilities);
        Assert.False(both.Stale);

        var legacy = DiscordRest.EvaluateKeptMeta("681034", TimeSpan.FromDays(2));
        Assert.Equal(681034, legacy.Build);
        Assert.Null(legacy.Capabilities);
        Assert.True(legacy.Stale);
    }

    [Fact]
    public void QosHeartbeatMatchesWebClientShape()
    {
        var active = DiscordSession.BuildQosHeartbeat(42, foregrounded: true, rtcConnected: true);
        Assert.Equal(42, active["seq"]!.GetValue<long>());
        Assert.Equal(31, active["qos"]!["ver"]!.GetValue<int>());
        Assert.True(active["qos"]!["active"]!.GetValue<bool>());
        Assert.Equal(["foregrounded", "rtc_connected"], active["qos"]!["reasons"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());

        var idle = DiscordSession.BuildQosHeartbeat(null, foregrounded: false, rtcConnected: false);
        Assert.Equal(0, idle["seq"]!.GetValue<long>());
        Assert.False(idle["qos"]!["active"]!.GetValue<bool>());
        Assert.Empty(idle["qos"]!["reasons"]!.AsArray());
    }

    [Fact]
    public void ReadyAndProtoUpdateDecodeStatusAndGuildFolders()
    {
        var changes = new Dictionary<string, JsonElement>
        {
            ["status"] = JsonDocument.Parse("\"dnd\"").RootElement.Clone(),
            ["guildFolders"] = JsonDocument.Parse("""[{"id":"99","name":"Squad","color":16711680,"guildIds":["200","100"]}]""").RootElement.Clone(),
        };
        var proto = DiscordUserSettings.Patch([], changes);
        Assert.NotNull(proto);
        Assert.Equal("dnd", DiscordUserSettings.DecodeStatus(proto!));
        var decodedFolders = DiscordUserSettings.DecodeFolders(proto!);
        Assert.NotNull(decodedFolders);
        Assert.Single(decodedFolders!);
        Assert.Equal("99", decodedFolders[0].Id);
        Assert.Equal("Squad", decodedFolders[0].Name);
        Assert.Equal(16711680, decodedFolders[0].Color);
        Assert.Equal(["200", "100"], decodedFolders[0].Guilds);

        var b64 = Convert.ToBase64String(proto!);
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse($$"""
            {
              "user": { "id": "{{A}}", "username": "me" },
              "guilds": [
                { "id": "100", "name": "One", "roles": [], "channels": [] },
                { "id": "200", "name": "Two", "roles": [], "channels": [] }
              ],
              "private_channels": [],
              "user_settings_proto": "{{b64}}"
            }
            """);
        cache.LoadReady(ready.RootElement);
        Assert.Equal("dnd", cache.MyStatus);

        var proto2 = DiscordUserSettings.Patch(proto!, new Dictionary<string, JsonElement>
        {
            ["status"] = JsonDocument.Parse("\"idle\"").RootElement.Clone(),
        });
        using var upd = JsonDocument.Parse($$$"""{"settings":{"type":1,"proto":"{{{Convert.ToBase64String(proto2!)}}}"}}""");
        var evs = cache.Apply("USER_SETTINGS_PROTO_UPDATE", upd.RootElement);
        Assert.Equal("idle", cache.MyStatus);
        Assert.Contains(evs, e => e.Name == "discord.presence");
    }

    [Fact]
    public void ChannelUnreadAndPinsDispatchesUpdateCache()
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse($$"""
            {
              "user": { "id": "{{A}}", "username": "me" },
              "guilds": [
                { "id": "100", "owner_id": "{{A}}", "name": "One", "roles": [], "channels": [
                  { "id": "500", "type": 0, "name": "general", "last_message_id": "10" }
                ]}
              ],
              "private_channels": [],
              "read_state": { "version": 1, "entries": [{ "id": "500", "last_message_id": "10", "mention_count": 0 }] }
            }
            """);
        cache.LoadReady(ready.RootElement);

        using var unread = JsonDocument.Parse("""{"guild_id":"100","channel_unread_updates":[{"id":"500","last_message_id":"20"}]}""");
        var uEvents = cache.Apply("CHANNEL_UNREAD_UPDATE", unread.RootElement);
        Assert.Contains(uEvents, e => e.Name == "discord.unread");

        using var pins = JsonDocument.Parse("""{"guild_id":"100","channel_id":"500"}""");
        var pEvents = cache.Apply("CHANNEL_PINS_UPDATE", pins.RootElement);
        Assert.Contains(pEvents, e => e.Name == "discord.changed");
    }
}
