using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam achievements read through the user's own sign-in can go wrong:
/// - a signed-in user sees the local cache's partial list instead of the game's full schema with unlocks;
/// - an unlock is read as locked, or a locked one as unlocked, or an unknown api name adds a row;
/// - the account's coverage key differs from the local account's, so the snapshot is refused;
/// - an unreadable or refused answer throws to the UI instead of falling back to the local cache;
/// - a signed-out user waits on the network, or the signed-in read replaces a good local snapshot with an empty one.
/// Fixtures are shaped like Steam's answers; app ids and names are fixtures.
/// </summary>
public sealed class SteamAccountAchievementsTests
{
    private const string SchemaJson = """
    {"game":{"gameName":"Fixture","availableGameStats":{"achievements":[
      {"name":"FIRST","defaultvalue":0,"displayName":"First steps","hidden":0,"description":"Start.","icon":"https://cdn.example/a.jpg","icon_gray":"https://cdn.example/a_gray.jpg"},
      {"name":"SECOND","defaultvalue":0,"displayName":"Second","hidden":1,"description":"Hidden one.","icon":"https://cdn.example/b.jpg","icon_gray":"https://cdn.example/b_gray.jpg"},
      {"name":"THIRD","defaultvalue":0,"displayName":"Third","hidden":0,"description":"Locked.","icon":"https://cdn.example/c.jpg","icon_gray":"https://cdn.example/c_gray.jpg"}
    ]}}}
    """;

    private const string UnlocksJson = """
    {"playerstats":{"steamID":"76561198000000001","gameName":"Fixture","success":true,"achievements":[
      {"apiname":"FIRST","achieved":1,"unlocktime":1700000000},
      {"apiname":"SECOND","achieved":0,"unlocktime":0},
      {"apiname":"NOT_IN_SCHEMA","achieved":1,"unlocktime":1700000100}
    ]}}
    """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void TheSchemaNamesEveryAchievementAndItsIcons()
    {
        var schema = SteamAccountAchievements.ParseSchema(Json(SchemaJson));

        Assert.NotNull(schema);
        Assert.Equal(["FIRST", "SECOND", "THIRD"], schema!.Select(row => row.ApiName).ToArray());
        Assert.Equal("First steps", schema[0].Name);
        Assert.True(schema[1].Hidden);
        Assert.Equal("https://cdn.example/a_gray.jpg", schema[0].IconGray);
    }

    [Fact]
    public void AnAnswerWithNoSchemaIsUnavailableNotEmpty()
    {
        Assert.Null(SteamAccountAchievements.ParseSchema(Json("""{"game":{"availableGameStats":{}}}""")));
        Assert.Null(SteamAccountAchievements.ParseSchema(Json("""{"game":{"availableGameStats":{"achievements":[]}}}""")));
        Assert.Null(SteamAccountAchievements.ParseSchema(Json("""{"error":"refused"}""")));
    }

    [Fact]
    public void UnlocksReadAchievedAndItsTime()
    {
        var unlocks = SteamAccountAchievements.ParseUnlocks(Json(UnlocksJson));

        Assert.NotNull(unlocks);
        Assert.True(unlocks!["FIRST"].Achieved);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), unlocks["FIRST"].At);
        Assert.False(unlocks["SECOND"].Achieved);
        Assert.Null(unlocks["SECOND"].At);
    }

    [Fact]
    public void AnUnsuccessfulUnlockAnswerIsUnavailable()
    {
        Assert.Null(SteamAccountAchievements.ParseUnlocks(Json("""{"playerstats":{"success":false,"error":"Requested app has no stats"}}""")));
        Assert.Null(SteamAccountAchievements.ParseUnlocks(Json("""{"other":1}""")));
    }

    [Fact]
    public void TheSnapshotIsTheFullSchemaWithTheSignedInAccountsUnlocks()
    {
        var schema = SteamAccountAchievements.ParseSchema(Json(SchemaJson))!;
        var unlocks = SteamAccountAchievements.ParseUnlocks(Json(UnlocksJson))!;
        var observed = DateTimeOffset.UtcNow;
        var coverage = AchievementCoverageKeys.FromAccount("steam", "38000000");

        var snapshot = SteamAccountAchievements.Build("570", coverage, schema, unlocks, observed);

        Assert.NotNull(snapshot);
        Assert.Equal(AchievementCoverageStatus.Complete, snapshot!.Coverage);
        Assert.Equal("steam", snapshot.ProviderId);
        Assert.Equal("570", snapshot.SourceGameId);
        Assert.Equal(coverage, snapshot.CoverageKey);
        Assert.Equal(3, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        // The unlock that is not in the schema adds no row; every schema row is there, locked or not.
        Assert.Equal(3, snapshot.Entries.Count);
        var first = snapshot.Entries.Single(entry => entry.Definition.ExternalId == "FIRST");
        Assert.True(first.State.Unlocked);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), first.State.UnlockedAtUtc);
        Assert.False(snapshot.Entries.Single(entry => entry.Definition.ExternalId == "THIRD").State.Unlocked);
        Assert.True(snapshot.Entries.Single(entry => entry.Definition.ExternalId == "SECOND").Definition.Hidden);
    }

    [Fact]
    public void ASchemaWithNoRowsMakesNoSnapshot()
    {
        Assert.Null(SteamAccountAchievements.Build("570", "steam:x", [], new Dictionary<string, (bool, DateTimeOffset?)>(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheCoverageKeyIsTheLocalAccountsHashForTheSameSteamAccount()
    {
        // userdata folders name the 32-bit account id: SteamID64 minus 76561197960265728 (the individual-account base).
        Assert.Equal("38000000", SteamAccountAchievements.AccountIdOf(76561197998265728).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task TheProviderUsesTheSignedInSnapshotWithoutTheLocalCache()
    {
        var signed = SteamAccountAchievements.Build("570", "steam:x",
            SteamAccountAchievements.ParseSchema(Json(SchemaJson))!,
            SteamAccountAchievements.ParseUnlocks(Json(UnlocksJson))!, DateTimeOffset.UtcNow)!;
        var provider = new SteamLibraryCacheAchievementProvider(
            () => throw new InvalidOperationException("the local cache must not be read"),
            _ => throw new InvalidOperationException("the local cache must not be read"),
            (_, _) => Task.FromResult<AchievementSnapshot?>(signed));

        var game = new GameEntry { Id = "steam:570", Title = "Fixture", Store = StoreKind.Steam, LaunchTarget = "570" };
        var snapshot = await provider.GetSnapshotAsync(game, TestContext.Current.CancellationToken);

        Assert.Same(signed, snapshot);
    }

    [Fact]
    public async Task SignedOutOrRefusedFallsBackToTheLocalCache()
    {
        var provider = new SteamLibraryCacheAchievementProvider(
            () => null,
            _ => null,
            (_, _) => Task.FromResult<AchievementSnapshot?>(null));

        var game = new GameEntry { Id = "steam:570", Title = "Fixture", Store = StoreKind.Steam, LaunchTarget = "570" };
        var snapshot = await provider.GetSnapshotAsync(game, TestContext.Current.CancellationToken);

        // The local path ran: with no Steam root it says so, as it always has.
        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Equal("Steam is not installed.", snapshot.Message);
    }
}
