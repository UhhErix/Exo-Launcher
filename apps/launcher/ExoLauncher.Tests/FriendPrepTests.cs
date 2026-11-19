using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Share when I'm getting ready (FriendPrep, DiscordPrep). Ways it could fail, each of them something a friend sees or the relay pays for:
/// - a percent that changes every few hundred milliseconds is sent each time, flooding the relay;
/// - an install that failed, was cancelled or went idle is still said to be going on;
/// - a game you hid, or one you added by hand, is named because it was downloading;
/// - "ready" is still said hours later, or after the game started;
/// - a compile is hidden behind an install, or a compile that ended without its shaders is called ready;
/// - a title with control characters, a kind nobody sends, or a percent out of range reaches a friend's screen;
/// - a friend's readiness shows after they stopped being a friend, or stays after the links ended;
/// - a new switch that is on before the person turned it on.
/// </summary>
public sealed class FriendPrepTests
{
    private const long T = 1_000_000;
    private static Func<(string Title, bool May)?> Known(string title = "Marvel Rivals", bool may = true) => () => (title, may);

    private static FriendLinkFormat.Message Message(string prepJson)
    {
        using var document = JsonDocument.Parse(prepJson);
        return new(T, false, new Dictionary<string, JsonElement> { [FriendPrep.Section] = document.RootElement.Clone() });
    }

    // ---- the wire ----

    [Fact]
    public void AnInstallIsSentInTensAndNothingElseCarriesAPercent()
    {
        var sent = FriendPrep.Write(new("Marvel Rivals", FriendPrep.Installing, 47))!;
        Assert.Equal("Marvel Rivals", sent["title"]!.GetValue<string>());
        Assert.Equal(40, sent["pct"]!.GetValue<int>());
        Assert.False(FriendPrep.Write(new("Marvel Rivals", FriendPrep.Compiling, 47))!.ContainsKey("pct"));
        Assert.False(FriendPrep.Write(new("Marvel Rivals", FriendPrep.Ready, 47))!.ContainsKey("pct"));
        Assert.Equal(100, FriendPrep.Write(new("x", FriendPrep.Installing, 250))!["pct"]!.GetValue<int>());
    }

    [Fact]
    public void NothingIsSentForNoStateAnUnknownKindOrAnEmptyTitle()
    {
        Assert.Null(FriendPrep.Write(null));
        Assert.Null(FriendPrep.Write(new("x", "downloading", null)));
        Assert.Null(FriendPrep.Write(new("\u0007\u202E", FriendPrep.Ready, null)));
    }

    [Fact]
    public void WhatWasSentIsReadBack()
    {
        Assert.Equal(new DiscordCache.ExoPrep("Marvel Rivals", "installing", 60), FriendPrep.Read(Message("{\"title\":\"Marvel Rivals\",\"kind\":\"installing\",\"pct\":60}")));
        Assert.Equal(new DiscordCache.ExoPrep("Hades II", "compiling", null), FriendPrep.Read(Message("{\"title\":\"Hades II\",\"kind\":\"compiling\"}")));
        Assert.Equal(new DiscordCache.ExoPrep("Hades II", "ready", null), FriendPrep.Read(Message("{\"title\":\"Hades II\",\"kind\":\"ready\"}")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"ready\"}")]                                               // no title
    [InlineData("{\"title\":\"x\"}")]                                                  // no kind
    [InlineData("{\"title\":\"x\",\"kind\":\"deleting\"}")]                            // a kind nobody sends
    [InlineData("{\"title\":7,\"kind\":\"ready\"}")]
    [InlineData("{\"title\":\"a\\u0007b\",\"kind\":\"ready\"}")]                       // control character
    [InlineData("{\"title\":\"a\\u202Eb\",\"kind\":\"ready\"}")]                       // bidi override
    [InlineData("{\"title\":\"   \",\"kind\":\"ready\"}")]
    [InlineData("{\"title\":\"x\",\"kind\":\"installing\",\"pct\":101}")]
    [InlineData("{\"title\":\"x\",\"kind\":\"installing\",\"pct\":-1}")]
    [InlineData("{\"title\":\"x\",\"kind\":\"installing\",\"pct\":4.5}")]
    [InlineData("{\"title\":\"x\",\"kind\":\"installing\",\"pct\":\"40\"}")]
    [InlineData("{\"title\":\"x\",\"kind\":\"ready\",\"pct\":40}")]                    // a percent on something that has none
    [InlineData("{\"title\":\"\\ud800\",\"kind\":\"ready\"}")]                         // a lone surrogate
    public void WhatAFriendsExoCouldNotHaveSentIsDropped(string json) => Assert.Null(FriendPrep.Read(Message(json)));

    [Fact]
    public void AMessageWithoutTheSectionOrNoMessageSaysNothing()
    {
        Assert.Null(FriendPrep.Read(null));
        Assert.Null(FriendPrep.Read(new(T, false, new Dictionary<string, JsonElement>())));
    }

    [Fact]
    public void TheTitleIsAsLongAsAGameTitleMayBe()
    {
        Assert.NotNull(FriendPrep.Read(Message($"{{\"title\":\"{new string('x', 128)}\",\"kind\":\"ready\"}}")));
        Assert.Null(FriendPrep.Read(Message($"{{\"title\":\"{new string('x', 129)}\",\"kind\":\"ready\"}}")));
        Assert.Equal(128, FriendPrep.Write(new(new string('x', 300), FriendPrep.Ready, null))!["title"]!.GetValue<string>().Length);
    }

    // ---- what this Exo says ----

    [Fact]
    public void AnInstallIsSaidInStepsOfTenAndNotAgainWithinOne()
    {
        var prep = new DiscordPrep();
        var asked = 0;
        Func<(string Title, bool May)?> resolve = () => { asked++; return ("Marvel Rivals", true); };
        Assert.True(prep.Install("g", InstallPhase.Downloading, 41, resolve, T));
        Assert.Equal(new FriendPrep.State("Marvel Rivals", "installing", 40), prep.Current(T));
        Assert.False(prep.Install("g", InstallPhase.Downloading, 44.9, resolve, T + 1)); // same step: nothing to send
        Assert.False(prep.Install("g", InstallPhase.Downloading, 40, resolve, T + 2));
        Assert.Equal(1, asked);                                                            // and the library is not asked again
        Assert.True(prep.Install("g", InstallPhase.Downloading, 51, resolve, T + 3));
        Assert.Equal(50, prep.Current(T + 3)!.Percent);
    }

    [Fact]
    public void AnInstallWithNoPercentIsStillSaidAndAFinishedOneIsReady()
    {
        var prep = new DiscordPrep();
        Assert.True(prep.Install("g", InstallPhase.Preparing, null, Known(), T));
        Assert.Equal(new FriendPrep.State("Marvel Rivals", "installing", null), prep.Current(T));
        Assert.True(prep.Install("g", InstallPhase.Completed, 100, Known(), T + 5));
        Assert.Equal(new FriendPrep.State("Marvel Rivals", "ready", null), prep.Current(T + 5));
    }

    [Theory]
    [InlineData(InstallPhase.Failed)]
    [InlineData(InstallPhase.Cancelled)]
    [InlineData(InstallPhase.Idle)]
    public void AnInstallThatStoppedIsNoLongerSaid(InstallPhase phase)
    {
        var prep = new DiscordPrep();
        prep.Install("g", InstallPhase.Downloading, 20, Known(), T);
        Assert.True(prep.Install("g", phase, null, Known(), T + 1));
        Assert.Null(prep.Current(T + 1));
    }

    [Fact]
    public void ACompletedInstallThatWasNeverSeenGoingIsNotInvented()
    {
        var prep = new DiscordPrep();
        Assert.False(prep.Install("g", InstallPhase.Completed, 100, Known(), T));
        Assert.Null(prep.Current(T));
    }

    [Fact]
    public void AGameYouHidOrAddedByHandIsNeverNamed()
    {
        var prep = new DiscordPrep();
        Assert.False(prep.Install("g", InstallPhase.Downloading, 30, Known("Secret Game", may: false), T));
        Assert.Null(prep.Current(T));
        Assert.False(prep.Install("g", InstallPhase.Downloading, null, () => null, T)); // not in the library at all
        Assert.False(prep.Compile("g", running: true, prepared: false, Known(may: false), T));
        Assert.Null(prep.Current(T));
    }

    [Fact]
    public void ReadyGoesAfterItsTimeAndWhenTheGameStarts()
    {
        var prep = new DiscordPrep();
        prep.Install("g", InstallPhase.Downloading, 99, Known(), T);
        prep.Install("g", InstallPhase.Completed, 100, Known(), T);
        Assert.NotNull(prep.Current(T + DiscordPrep.ReadyForMs - 1));
        Assert.Null(prep.Current(T + DiscordPrep.ReadyForMs)); // never shown past its time, even before the timer runs
        Assert.Equal(DiscordPrep.ReadyForMs - 5, prep.NextExpiryIn(T + 5));   // the timer is set for the first to run out
        Assert.False(prep.Expire(T + DiscordPrep.ReadyForMs - 1));
        Assert.True(prep.Expire(T + DiscordPrep.ReadyForMs));
        Assert.Null(prep.NextExpiryIn(T + DiscordPrep.ReadyForMs));

        prep.Install("h", InstallPhase.Downloading, 99, Known("Hades II"), T);
        prep.Install("h", InstallPhase.Completed, 100, Known("Hades II"), T);
        Assert.True(prep.Launched("h"));
        Assert.Null(prep.Current(T));
        Assert.False(prep.Launched("h"));
    }

    [Fact]
    public void TheTimerIsSetForTheFirstReadyToRunOutNotTheLast()
    {
        var prep = new DiscordPrep();
        prep.Install("a", InstallPhase.Downloading, 50, Known("A"), T);
        prep.Install("a", InstallPhase.Completed, 100, Known("A"), T);
        prep.Install("b", InstallPhase.Downloading, 50, Known("B"), T + 20 * 60_000);
        prep.Install("b", InstallPhase.Completed, 100, Known("B"), T + 20 * 60_000);
        Assert.Equal(DiscordPrep.ReadyForMs - 10, prep.NextExpiryIn(T + 10));
        Assert.True(prep.Expire(T + DiscordPrep.ReadyForMs));                      // a goes, b stays
        Assert.Equal(new FriendPrep.State("B", "ready", null), prep.Current(T + DiscordPrep.ReadyForMs));
    }

    [Fact]
    public void CompilingIsSaidBeforeInstallingBeforeReady()
    {
        var prep = new DiscordPrep();
        prep.Install("a", InstallPhase.Downloading, 70, Known("A"), T);
        Assert.Equal("installing", prep.Current(T)!.Kind);
        prep.Compile("b", running: true, prepared: false, Known("B"), T + 1);
        Assert.Equal(new FriendPrep.State("B", "compiling", null), prep.Current(T + 1));
        Assert.False(prep.Compile("b", running: true, prepared: false, Known("B"), T + 2)); // still the same
        prep.Compile("b", running: false, prepared: false, Known("B"), T + 3);              // ended without its shaders: not ready
        Assert.Equal("installing", prep.Current(T + 3)!.Kind);
        prep.Install("a", InstallPhase.Completed, 100, Known("A"), T + 4);
        prep.Compile("c", running: true, prepared: false, Known("C"), T + 5);
        prep.Compile("c", running: false, prepared: true, Known("C"), T + 6);               // ended with them: ready
        Assert.Equal(new FriendPrep.State("C", "ready", null), prep.Current(T + 6));         // the latest ready
    }

    [Fact]
    public void ClearingForgetsEverything()
    {
        var prep = new DiscordPrep();
        prep.Install("a", InstallPhase.Downloading, 70, Known(), T);
        prep.Install("b", InstallPhase.Completed, 100, Known(), T);
        prep.Clear();
        Assert.Null(prep.Current(T));
        Assert.Null(prep.NextExpiryIn(T));
    }

    // ---- what a friend's Exo said ----

    [Fact]
    public void ReadinessIsShownForAFriendOnlyAndGoesWhenTheLinksEnd()
    {
        var cache = FriendLinkTests.CacheWithFriends();
        var told = new DiscordCache.ExoPrep("Hades II", "compiling", null);
        Assert.NotEmpty(cache.SetExoPrep(FriendLinkTests.B, told));
        Assert.Empty(cache.SetExoPrep(FriendLinkTests.B, told));  // nothing new: no event
        Assert.Empty(cache.SetExoPrep(FriendLinkTests.C, told));  // a request, not a friend
        var friends = JsonSerializer.Serialize(cache.Friends());
        Assert.Contains("compiling", friends);
        Assert.Contains("Hades II", friends);
        using var update = JsonDocument.Parse($"{{\"user\":{{\"id\":\"{FriendLinkTests.B}\"}},\"status\":\"idle\",\"activities\":[]}}");
        cache.Apply("PRESENCE_UPDATE", update.RootElement);        // a presence update keeps it
        Assert.Contains("Hades II", JsonSerializer.Serialize(cache.Friends()));
        Assert.NotEmpty(cache.ClearExoGames());
        Assert.DoesNotContain("Hades II", JsonSerializer.Serialize(cache.Friends()));
    }

    [Fact]
    public void AFriendWhoSaysNothingAnyMoreIsClearedWithOneEvent()
    {
        var cache = FriendLinkTests.CacheWithFriends();
        cache.SetExoPrep(FriendLinkTests.B, new("Hades II", "ready", null));
        var one = Assert.Single(cache.SetExoPrep(FriendLinkTests.B, null));
        Assert.Equal("discord.presence", one.Name);
        Assert.DoesNotContain("Hades II", JsonSerializer.Serialize(one.Data));
        Assert.Empty(cache.SetExoPrep(FriendLinkTests.B, null));
    }

    [Fact]
    public void ThePresenceEventCarriesWhatTheirExoSaid()
    {
        var cache = FriendLinkTests.CacheWithFriends();
        var one = Assert.Single(cache.SetExoPrep(FriendLinkTests.B, new("Hades II", "installing", 60)));
        var json = JsonSerializer.Serialize(one.Data);
        Assert.Contains("\"Percent\":60", json);
        Assert.Contains("installing", json);
    }

    // ---- on the link ----

    private static (FriendLinkFormat.Keys Low, FriendLinkFormat.Keys High) Pair()
    {
        using var a = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var b = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return (FriendLinkFormat.Derive(a, FriendLinkTests.A, FriendLinkTests.B, FriendLinkFormat.EncodeKey(b))!, FriendLinkFormat.Derive(b, FriendLinkTests.B, FriendLinkTests.A, FriendLinkFormat.EncodeKey(a))!);
    }

    [Fact]
    public void ASealedMessageCarriesTheGameAndGettingReadySideBySide()
    {
        var (low, high) = Pair();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sections = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            [FriendGame.Section] = new System.Text.Json.Nodes.JsonObject { ["title"] = "Hades II", ["start"] = now - 1000 },
            [FriendPrep.Section] = FriendPrep.Write(new("Marvel Rivals", FriendPrep.Installing, 60)),
        };
        var opened = FriendLinkFormat.Open(high, FriendLinkFormat.Seal(low, now, false, sections)!)!;
        Assert.Equal(new DiscordCache.ExoPrep("Marvel Rivals", "installing", 60), FriendPrep.Read(opened));
        Assert.Equal("Hades II", FriendGame.Read(opened, now)?.Title);
    }

    [Fact]
    public void TwoTitlesAsLongAsTheyMayBeStillFitOneMessage()
    {
        // Seal returns null when the plain JSON passes its fixed size, and nothing is sent then: the worst case is every character in two titles taking its widest form.
        var (low, high) = Pair();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var unit in new[] { "x", "é", "日", "\U0001F600" })
        {
            var title = string.Concat(Enumerable.Repeat(unit, unit.Length == 2 ? 64 : 128));
            var sections = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
            {
                [FriendGame.Section] = new System.Text.Json.Nodes.JsonObject { ["title"] = title, ["start"] = now },
                [FriendPrep.Section] = FriendPrep.Write(new(title, FriendPrep.Installing, 90)),
            };
            var written = FriendPrep.Write(new(title, FriendPrep.Installing, 90))!["title"]!.GetValue<string>();
            Assert.NotEmpty(written);
            Assert.DoesNotContain('�', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(written))); // never cut through a surrogate pair
            var sealedMessage = FriendLinkFormat.Seal(low, now, true, sections);
            Assert.NotNull(sealedMessage);
            Assert.NotNull(FriendPrep.Read(FriendLinkFormat.Open(high, sealedMessage)));
        }
    }

    // ---- the switch ----

    [Fact]
    public void ItIsOffForEveryAccountUntilItIsTurnedOn()
    {
        Assert.Empty(new AppSettings().DiscordReadyShared);
        Assert.Empty(JsonSerializer.Deserialize<AppSettings>("{}")!.DiscordReadyShared);
    }
}
