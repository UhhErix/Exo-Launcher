using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.SteamSignIn;
using SharedApp = ExoLauncher.Adapters.SteamFamilyLibrary.SharedApp;
using Shares = ExoLauncher.Adapters.SteamFamilyLibrary.Shares;

namespace ExoLauncher.Tests;

/// <summary>
/// How the library's Steam Family list is put together from the family service, the running client and what was kept.
/// Ways it could fail:
/// - the family service's list loses to a client answer that only covered the ids Exo asked about (on 2026-11-19
///   Steam's local app list went missing and the family fell from 110 games to 3, and that was kept);
/// - a game the account owns itself is shown as lent; an excluded game, or one in a family the account left, stays;
/// - "not in a family" (keep an empty list) and "Steam did not answer" (keep the last list) are confused;
/// - with nothing answering, the kept list is not used, or a kept list is overwritten by a guess;
/// - a shared game nobody here has opened gets no row (no local name), is listed twice, or is not marked shared;
/// - owners' names: the account itself is named, an unknown owner is shown as a blank, many owners overflow;
/// - the website token: a token for another account is used, an expired or malformed one is kept, the store's answer
///   without a token reads as one;
/// - a client batch that failed (its ids unchecked) drops the kept shares it held;
/// - an artwork or grouped-card copy of a shared row loses who shares it.
/// </summary>
public sealed class SteamFamilyMergeTests
{
    private static Dictionary<string, SharedApp> Apps(params SharedApp[] apps) =>
        apps.ToDictionary(app => app.AppId, StringComparer.Ordinal);

    private static readonly HashSet<string> Owned = ["10", "11"];

    [Fact]
    public void TheFamilyServiceWinsOverAPartialClientAnswer()
    {
        var service = new Shares(4242, Apps(new SharedApp("20", "Lent", ["200"]), new("21", "Lent Too", ["300"]), new("10", "Mine", ["200"])));
        var client = new HashSet<string> { "10", "11", "20" };   // asked about too few ids: 21 is missing

        var family = SteamFamilyLibrary.Resolve(service, client, Owned, localInFamily: true, clientOffline: false, remembered: null);

        Assert.Equal(["20", "21"], family.AppIds.Order(StringComparer.Ordinal));   // 10 is the account's own
        Assert.Equal("family-service", family.Source);
        Assert.True(family.Remember);
        Assert.Equal("Lent Too", family.Apps["21"].Name);
    }

    [Fact]
    public void LeavingTheFamilyClearsWhatWasKeptButSteamNotAnsweringKeepsIt()
    {
        var kept = Apps(new SharedApp("20", "Lent", ["200"]));

        var left = SteamFamilyLibrary.Resolve(Shares.None, null, Owned, true, false, kept);
        var silent = SteamFamilyLibrary.Resolve(null, null, Owned, true, false, kept);

        Assert.Empty(left.Apps);
        Assert.True(left.Remember);
        Assert.Equal(["20"], silent.AppIds);
        Assert.Equal("remembered", silent.Source);
        Assert.False(silent.Remember);
    }

    [Fact]
    public void AKeptListIsUsedWhileSteamIsClosedOrOfflineAndLessWhatWasBoughtSince()
    {
        var kept = Apps(new SharedApp("20", "Lent", ["200"]), new("11", "Bought since", ["200"]));
        var client = new HashSet<string> { "10", "11", "20", "30" };

        var closed = SteamFamilyLibrary.Resolve(null, null, Owned, false, false, kept);
        var offline = SteamFamilyLibrary.Resolve(null, client, Owned, true, clientOffline: true, kept);

        Assert.Equal(["20"], closed.AppIds);
        Assert.Equal(["20"], offline.AppIds);   // never what Steam lists offline
        Assert.False(offline.Remember);
    }

    [Fact]
    public void TheClientAnswerKeepsTheNamesAndOwnersAKeptListHad()
    {
        var kept = Apps(new SharedApp("20", "Lent", ["200"]));
        var client = new HashSet<string> { "10", "20", "30" };

        // localconfig.vdf has no family block (Steam had not written it), but a kept list says there is a family.
        var family = SteamFamilyLibrary.Resolve(null, client, Owned, localInFamily: false, clientOffline: false, kept);

        Assert.Equal(["20", "30"], family.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal("Lent", family.Apps["20"].Name);
        Assert.Null(family.Apps["30"].Name);
        Assert.True(family.Remember);
    }

    [Fact]
    public void AClientBatchThatNeverAnsweredKeepsTheKeptSharesItHeld()
    {
        var kept = Apps(new SharedApp("20", "Lent", ["200"]), new("21", "Lent Too", ["300"]), new("22", "Stopped", ["300"]), new("11", "Bought since", ["200"]));
        // The first batch answered (20 licensed, 22 checked and no longer); the batch with 21 and 11 failed.
        var client = new HashSet<string> { "10", "20" };

        var family = SteamFamilyLibrary.Resolve(null, client, Owned, true, false, kept, clientUnchecked: ["21", "11"]);

        Assert.Equal(["20", "21"], family.AppIds.Order(StringComparer.Ordinal));   // 22 checked out; 11 is the account's own
        Assert.Equal("Lent Too", family.Apps["21"].Name);
        Assert.Equal("steam-client", family.Source);
    }

    [Fact]
    public void ArtworkAndGroupedCardCopiesKeepWhoSharesTheGame()
    {
        var steam = new GameEntry
        {
            Id = "steam:3999990", Title = "Exo Family Fixture", Store = StoreKind.Steam, Installed = true,
            FamilyShared = true, SharedBy = "Sam", CoverUrl = "ftp://not-a-cover",
        };
        var epic = new GameEntry { Id = "epic:exo-family-fixture", Title = "Exo Family Fixture", Store = StoreKind.Epic, Installed = true };

        var art = CoverArtService.WithCover(steam);
        Assert.NotEqual(steam.CoverUrl, art.CoverUrl);   // a copy was made
        Assert.True(art.FamilyShared);
        Assert.Equal("Sam", art.SharedBy);

        var card = Assert.Single(LibraryService.GroupVariants([art, epic]));
        var lent = card.Variants.Single(v => v.Store == StoreKind.Steam);
        Assert.True(lent.FamilyShared);
        Assert.Equal("Sam", lent.SharedBy);
        Assert.False(card.Variants.Single(v => v.Store == StoreKind.Epic).FamilyShared);

        GameEntry Pick(string id) => LibraryService.SelectPreferredSource(card, new Dictionary<string, string> { [card.CanonicalTitleKey!] = id });
        var asSteam = Pick(steam.Id);
        Assert.Equal(steam.Id, asSteam.Id);
        Assert.True(asSteam.FamilyShared);
        Assert.Equal("Sam", asSteam.SharedBy);
        var asEpic = Pick(epic.Id);
        Assert.Equal(epic.Id, asEpic.Id);
        Assert.False(asEpic.FamilyShared);
        Assert.Null(asEpic.SharedBy);
        Assert.Equal("Sam", lent.ToGameEntry(asEpic).SharedBy);
    }

    [Fact]
    public void WithNothingKnownThereIsNoFamilyAndNothingIsKept()
    {
        var family = SteamFamilyLibrary.Resolve(null, new HashSet<string> { "10" }, Owned, false, false, null);

        Assert.Empty(family.Apps);
        Assert.False(family.Remember);
    }

    [Fact]
    public void AServiceAnswerWithoutANameKeepsTheNameItHadBefore()
    {
        var family = SteamFamilyLibrary.Resolve(new Shares(1, Apps(new SharedApp("20", null, ["200"]))), null, null, false, false, Apps(new SharedApp("20", "Lent", [])));

        Assert.Equal("Lent", family.Apps["20"].Name);
        Assert.Equal(["200"], family.Apps["20"].OwnerAccountIds);
    }

    [Theory]
    [InlineData(new[] { "200" }, "Sam")]
    [InlineData(new[] { "200", "300" }, "Sam and Alex")]
    [InlineData(new[] { "100", "200" }, "Sam")]                     // the account itself is not named
    [InlineData(new[] { "200", "300", "400" }, "Sam and 2 others")]
    [InlineData(new[] { "999" }, null)]                             // nobody here knows the name
    [InlineData(new string[0], null)]
    public void SharedByNamesTheOwnersOnly(string[] owners, string? expected)
    {
        var names = new Dictionary<string, string> { ["100"] = "Me", ["200"] = "Sam", ["300"] = "Alex", ["400"] = "Kim", ["500"] = " " };

        Assert.Equal(expected, SteamFamilyLibrary.SharedBy(new SharedApp("20", "Lent", owners), names, "100"));
    }

    [Theory]
    [InlineData(76561197960265731UL, "3")]
    [InlineData(76561197960265728UL, null)]   // account 0
    [InlineData(103582791429521412UL, null)]  // a group, not an individual
    [InlineData(0UL, null)]
    public void AccountIdIsOnlyAnIndividualsLow32Bits(ulong steamId, string? expected) =>
        Assert.Equal(expected, SteamFamilyLibrary.AccountIdOf(steamId));

    [Fact]
    public void ASharedGameNobodyHereOpenedGetsOneRowWithItsOwner()
    {
        var family = SteamFamilyLibrary.Resolve(new Shares(1, Apps(new SharedApp("20", "Lent", ["200"]), new("60", "Installed", ["200"]))), null, Owned, false, false, null);
        // As the adapter fills them: the service's names for ids appinfo has never seen.
        var names = new Dictionary<string, SteamAppInfoNames.Entry> { ["10"] = new("Mine", "game") };
        foreach (var app in family.Apps.Values) names.TryAdd(app.AppId, new(app.Name!, "game"));
        var owned = Owned.Union(family.AppIds).ToHashSet(StringComparer.Ordinal);

        var rows = SteamAccountLibrary.UninstalledOwnedGames(owned, new HashSet<string> { "60" }, names, owned, family.AppIds,
            sharedBy: id => SteamFamilyLibrary.SharedBy(family.Apps.GetValueOrDefault(id), new Dictionary<string, string> { ["200"] = "Sam" }, "100"));

        Assert.Equal(["steam:10", "steam:20"], rows.Select(row => row.Id).Order(StringComparer.Ordinal));
        var lent = rows.Single(row => row.Id == "steam:20");
        Assert.True(lent is { FamilyShared: true, SharedBy: "Sam", CanInstall: true, EntitlementState: EntitlementState.Owned });
        Assert.Equal("Shared by Sam through Steam Family. Installs through Steam.", lent.LaunchNote);
        Assert.Null(rows.Single(row => row.Id == "steam:10").SharedBy);
    }

    [Theory]
    [InlineData("""{"success":1,"data":{"webapi_token":"abc.def.ghi"}}""", "abc.def.ghi")]
    [InlineData("""{"success":1,"data":{}}""", null)]
    [InlineData("""{"success":1,"data":{"webapi_token":""}}""", null)]
    [InlineData("""{"success":1,"data":[]}""", null)]
    [InlineData("<html>sign in</html>", null)]
    [InlineData("", null)]
    public void TheStoresAsyncConfigGivesItsTokenOrNothing(string body, string? expected) =>
        Assert.Equal(expected, SteamWebsiteToken.ParseAsyncConfig(body));

    [Fact]
    public void AWebsiteTokenServesOnlyItsOwnAccountAndOnlyWhileItIsGood()
    {
        const ulong Me = 76561197960265729;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var was = SteamWebsiteToken.Clock;
        var fetch = SteamWebsiteToken.Fetch;
        try
        {
            SteamWebsiteToken.Clock = () => now;
            SteamWebsiteToken.Fetch = null;
            SteamWebsiteToken.Forget(retrySoon: true);

            Assert.False(SteamWebsiteToken.Accept("not a token"));
            Assert.False(SteamWebsiteToken.Accept(Jwt(Me, now.AddMinutes(-1))));   // already ended
            Assert.True(SteamWebsiteToken.Accept(Jwt(Me, now.AddHours(24))));

            Assert.NotNull(SteamWebsiteToken.For(Me));
            Assert.Null(SteamWebsiteToken.For(Me + 1));                            // the website signed in as another account
            SteamWebsiteToken.Clock = () => now.AddHours(24).AddMinutes(-5);       // about to end: dropped
            Assert.Null(SteamWebsiteToken.For(Me));
        }
        finally
        {
            SteamWebsiteToken.Forget(retrySoon: true);
            SteamWebsiteToken.Clock = was;
            SteamWebsiteToken.Fetch = fetch;
        }
    }

    private static string Jwt(ulong steamId, DateTimeOffset expires)
    {
        static string Part(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Part(new { alg = "none" }) + "." + Part(new { sub = steamId.ToString(), exp = expires.ToUnixTimeSeconds() }) + ".sig";
    }
}
