using System.Globalization;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways enriched hours can come out wrong:
/// - a figure an earlier pass borrowed (Galaxy, tracker.gg) is read back as the store's own, loses its note and is added to a sibling store;
/// - Steam Web API hours land after the source was chosen, so Galaxy or a tracker wins over Steam's own figure;
/// - one account-wide tracker.gg total is counted once per store on a grouped card;
/// - a second Riot ID's import keeps the first player's larger hours, or a cleared Riot ID keeps them at all;
/// - Exo's own measured minutes are hidden behind a smaller store figure (Steam offline counted 2 of 171), or are
///   added to it, counting the same session twice, or beat a larger store figure from play on other PCs;
/// - a game found running (started outside Exo, or still running when Exo started again) counts from detection
///   instead of its process's start, or from before what was already counted, so a restart loses or doubles time;
/// - adopting a game Exo launched itself moves that session's start;
/// - an adopted session reaches disk without its start, so a crash right after loses the hours before it.
/// Fixture ids and titles match nothing on this PC (no Steam app, Galaxy row or tracker.gg game).
/// </summary>
public sealed class PlaytimeEnrichTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-enrich-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public PlaytimeEnrichTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
        RiotLedger.ForgetLoaded();
        PlaytimeService.ForgetLoaded();
    }

    public void Dispose()
    {
        RiotLedger.ForgetLoaded();
        PlaytimeService.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    private const string SteamAppId = "999999917";

    private static GameEntry Steam(int? minutes) => new()
    {
        Id = "steam:" + SteamAppId, Title = "Exo Enrich Fixture Qx", Store = StoreKind.Steam,
        LaunchTarget = SteamAppId, PlaytimeMinutes = minutes,
    };

    [Fact]
    public void AnIncomingFigureIsNeverTakenAsTheStores()
    {
        // A painted or cached row still carrying an earlier pass's borrowed figure.
        var enriched = Assert.Single(PlaytimeService.Enrich([Steam(777)]));
        Assert.Null(enriched.PlaytimeMinutes);
    }

    [Fact]
    public void SteamWebApiHoursAreTheStoresFigureAndStayOnReEnrich()
    {
        var account = new Dictionary<string, int> { [SteamAppId] = 300 };
        var once = Assert.Single(PlaytimeService.Enrich([Steam(null)], account));
        Assert.Equal(300, once.PlaytimeMinutes);
        var twice = Assert.Single(PlaytimeService.Enrich([once], account));
        Assert.Equal(300, twice.PlaytimeMinutes);
    }

    [Fact]
    public void StoreFiguresFromDifferentStoresStillAddUp()
    {
        var steam = new GameEntry { Id = "steam:exo-add-fixture", Title = "Exo Add Fixture", Store = StoreKind.Steam, PlaytimeMinutes = 100 };
        var epic = new GameEntry { Id = "epic:exo-add-fixture", Title = "Exo Add Fixture", Store = StoreKind.Epic, PlaytimeMinutes = 50 };
        Assert.Equal(150, Assert.Single(LibraryService.GroupVariants([steam, epic])).PlaytimeMinutes);
    }

    private static void SaveRiotId(string riotId) => Assert.True(RiotIdStore.Save(riotId));

    [Fact]
    public void ARiotGameShowsItsLedgerLifetimeAndSessionsAddToTheSeed()
    {
        // tracker.gg's lifetime total is the start; Exo's session since then adds on, whatever the mode was.
        SaveRiotId("PlayerC#EUW");
        var account = RiotLedger.AccountKey("PlayerC#EUW")!;
        var riot = new GameEntry { Id = "riot:exo-fixture-lifetime", Title = "Exo Riot Lifetime Fixture Qx", Store = StoreKind.Riot };
        int? Minutes() => Assert.Single(PlaytimeService.Enrich([riot])).PlaytimeMinutes;

        RiotLedger.Seed(account, new Dictionary<string, GameSeed> { [riot.Id] = new GameSeed(6000) }, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(6000, Minutes());
        RiotLedger.Begin(account, riot.Id, DateTimeOffset.UtcNow.AddHours(-2));
        RiotLedger.End(riot.Id, DateTimeOffset.UtcNow);
        Assert.Equal(6120, Minutes());
    }

    [Fact]
    public void RiotHoursBelongToTheRiotIdInEffect()
    {
        var riot = new GameEntry { Id = "riot:exo-fixture", Title = "Exo Riot Fixture Qx", Store = StoreKind.Riot };
        int? Minutes() => Assert.Single(PlaytimeService.Enrich([riot])).PlaytimeMinutes;
        var seeded = DateTimeOffset.UtcNow.AddDays(-1);

        SaveRiotId("PlayerA#EUW");
        RiotLedger.Seed(RiotLedger.AccountKey("PlayerA#EUW")!, new Dictionary<string, GameSeed> { [riot.Id] = new GameSeed(600) }, seeded);
        Assert.Equal(600, Minutes());

        // Another player's Riot ID has no seed yet: not A's hours, not shown.
        SaveRiotId("PlayerB#EUW");
        Assert.Null(Minutes());
        RiotLedger.Seed(RiotLedger.AccountKey("PlayerB#EUW")!, new Dictionary<string, GameSeed> { [riot.Id] = new GameSeed(100) }, seeded);
        Assert.Equal(100, Minutes());

        SaveRiotId("PlayerA#EUW");
        Assert.Equal(600, Minutes());
    }

    private static GameEntry SteamGame(string appId) => new()
    {
        Id = "steam:" + appId, Title = "Exo Session Fixture Qx", Store = StoreKind.Steam, LaunchTarget = appId,
    };

    private static int? Shown(GameEntry game, int? steamMinutes = null) =>
        Assert.Single(PlaytimeService.Enrich([game],
            steamMinutes is { } store ? new Dictionary<string, int> { [game.LaunchTarget!] = store } : null)).PlaytimeMinutes;

    [Fact]
    public void ExosMeasuredMinutesShowWhenTheStoreCountedLessAndNeverAddToIt()
    {
        var game = SteamGame("999999932");
        PlaytimeService.AdoptSession(game.Id, DateTimeOffset.UtcNow.AddMinutes(-171));
        PlaytimeService.EndSession(game.Id);

        Assert.Equal(171, Shown(game));
        Assert.Equal(171, Shown(game, steamMinutes: 2));
        // Steam's own figure from play on other PCs is larger: it shows, with nothing of Exo's added.
        Assert.Equal(600, Shown(game, steamMinutes: 600));
    }

    [Fact]
    public void AGameFoundRunningCountsFromItsProcessStartAndARestartNeitherLosesNorDoubles()
    {
        var game = SteamGame("999999933");
        var processStart = DateTimeOffset.UtcNow.AddMinutes(-120);
        PlaytimeService.AdoptSession(game.Id, processStart);
        // Exo closes an hour in, while the game keeps running.
        PlaytimeService.EndSession(game.Id, processStart.AddMinutes(60));
        // Exo starts again and finds the same process: only the time since what was counted is added.
        PlaytimeService.AdoptSession(game.Id, processStart);
        PlaytimeService.EndSession(game.Id);

        Assert.Equal(120, Shown(game));
    }

    [Fact]
    public void AnUnreadableStartCountsFromDetectionAndAnOpenSessionKeepsItsStart()
    {
        var unread = SteamGame("999999934");
        PlaytimeService.AdoptSession(unread.Id, processStartedAt: null);
        PlaytimeService.EndSession(unread.Id, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(10, Shown(unread));

        var launched = SteamGame("999999935");
        PlaytimeService.BeginSession(launched.Id);
        PlaytimeService.AdoptSession(launched.Id, DateTimeOffset.UtcNow.AddMinutes(-300));
        PlaytimeService.EndSession(launched.Id, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(5, Shown(launched));
    }

    [Fact]
    public void AnAdoptedSessionIsOnDiskFromItsProcessStartAndSeenNow()
    {
        var game = SteamGame("999999936");
        var processStart = DateTimeOffset.UtcNow.AddMinutes(-90);
        PlaytimeService.AdoptSession(game.Id, processStart);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "playtime.json")));
            var session = doc.RootElement.GetProperty("activeSessions").GetProperty(game.Id);
            Assert.Equal(processStart, DateTimeOffset.Parse(session.GetProperty("startUtc").GetString()!, CultureInfo.InvariantCulture));
            var checkpoint = DateTimeOffset.Parse(session.GetProperty("checkpointUtc").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(checkpoint, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        }
        finally { PlaytimeService.EndSession(game.Id); }
    }

    [Fact]
    public void ARiotSessionPlayedBeforeAnIdIsSetStillShowsAndMovesIntoItsLedgerOnce()
    {
        // Without a Riot ID the minutes go to a local unassigned ledger, so the row never drops to 0. Setting the ID moves
        // them into its ledger once: the row reads the same before and after, and a second claim moves nothing.
        var riot = new GameEntry { Id = "riot:exo-fixture-unassigned", Title = "Exo Riot Unassigned Fixture Qx", Store = StoreKind.Riot };
        int? Minutes() => Assert.Single(PlaytimeService.Enrich([riot])).PlaytimeMinutes;

        RiotLedger.Begin(RiotLedger.Unassigned, riot.Id, DateTimeOffset.UtcNow.AddHours(-3));
        RiotLedger.End(riot.Id, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Null(RiotIdStore.TryRead());
        Assert.Equal(120, RiotLedger.Totals(RiotLedger.Unassigned, riot.Id));
        Assert.Equal(120, Minutes());

        SaveRiotId("PlayerU#EUW");
        var account = RiotLedger.AccountKey("PlayerU#EUW")!;
        Assert.Equal(120, Minutes());
        RiotLedger.ForgetLoaded();
        Assert.Equal(120, RiotLedger.Totals(account, riot.Id));
        Assert.False(File.Exists(Path.Combine(_folder, "riot-ledger", RiotLedger.Unassigned + ".jsonl")));

        RiotLedger.ClaimUnassigned(account);
        Assert.Equal(120, Minutes());
    }

    [Fact]
    public void AXboxSeedPulledFromAnotherPcShowsOnceAndAPulledDuplicateAddsNothing()
    {
        // Two PCs each convert their own reported hours for one title. The first seed wins on every PC, so the second pull
        // (the other PC's copy of the same title) never adds to the minutes already seeded.
        var game = new GameEntry { Id = "xbox:exo-fixture-seed", Title = "Exo Xbox Seed Fixture Qx", Store = StoreKind.Xbox };
        int? Minutes() => Assert.Single(PlaytimeService.Enrich([game])).PlaytimeMinutes;

        PlaytimeService.AddSyncedSeed(SeedJson("EXO-SEED-1", "Exo Xbox Seed Fixture Qx", 600));
        Assert.Equal(600, Minutes());

        PlaytimeService.AddSyncedSeed(SeedJson("EXO-SEED-1", "Exo Xbox Seed Fixture Qx", 900));
        Assert.Equal(600, Minutes());
    }

    private static string SeedJson(string title, string name, int minutes) =>
        new System.Text.Json.Nodes.JsonObject { ["title"] = title, ["name"] = name, ["minutes"] = minutes, ["lastPlayed"] = null }.ToJsonString();
}
