using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Riot lifetime hours go wrong:
/// - a session after the tracker.gg seed is added to a larger figure instead of to the seed (100 h + 2 h shows 100 h);
/// - a re-import keeps the old seed's rows or drops the ledger; rows from before the seed are counted again;
/// - a repeated line (a retried write, a torn write) counts twice, or a crash loses the whole session;
/// - two sessions of one game (or a restart that finds the process again) count the same minutes twice;
/// - one Riot ID's hours show on another's, or another Riot ID's file is taken as this one's seed;
/// - a seed for a non-Riot game, or an empty seed, is written and then counted;
/// - an hours file from before the ledger is ignored, so the seed is lost on update.
/// Account keys and dates are fixtures that match no real player.
/// </summary>
public sealed class RiotLedgerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-riot-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    private const string Account = "0123456789abcdef0123";
    private const string OtherAccount = "fedcba9876543210fedc";
    private const string Valorant = "riot:valorant";
    private const string League = "riot:league_of_legends";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public RiotLedgerTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
        RiotLedger.ForgetLoaded();
    }

    public void Dispose()
    {
        RiotLedger.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    private string LedgerFile(string account) => Path.Combine(_folder, "riot-ledger", account + ".jsonl");

    private static Dictionary<string, GameSeed> Seed(string game, int minutes) => new() { [game] = new GameSeed(minutes) };

    private static void Play(string account, string game, DateTimeOffset start, DateTimeOffset end)
    {
        RiotLedger.Begin(account, game, start);
        RiotLedger.End(game, end);
    }

    [Fact]
    public void ASessionAfterTheSeedAddsToIt()
    {
        // The reported bug: 100 h from tracker.gg, then a 2 h session, must read 102 h, not 100 h.
        Assert.True(RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10)));
        Play(Account, Valorant, Now.AddHours(-3), Now.AddHours(-1));

        Assert.Equal(6120, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void NamedModesLargerThanTheSeedAreScaledToIt()
    {
        // tracker.gg's split adds to 140 min against a 100 min total: the modes are scaled to the seed, never above it.
        RiotLedger.Seed(Account, new Dictionary<string, GameSeed> { [Valorant] = new GameSeed(100, new Dictionary<string, int> { ["competitive"] = 80, ["unrated"] = 60 }) }, Now.AddDays(-10));

        var modes = RiotLedger.ModeTotals(Account, Valorant);

        Assert.Equal(57, modes["competitive"]);
        Assert.Equal(42, modes["unrated"]);
        // The rounding minute goes to Unknown, so the total is the seed and never more.
        Assert.Equal(1, modes[RiotModes.Unknown]);
        Assert.Equal(100, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ANewSeedReplacesTheSeedAndDropsRowsBeforeIt()
    {
        RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10));
        Play(Account, Valorant, Now.AddDays(-5), Now.AddDays(-5).AddMinutes(60));
        // Re-import: tracker's new total already holds that hour, so the new seed replaces the old one.
        RiotLedger.Seed(Account, Seed(Valorant, 6100), Now.AddDays(-2));
        Play(Account, Valorant, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(30));

        Assert.Equal(6130, RiotLedger.Totals(Account, Valorant));
        // The rows are not replaced: both seeds stay on disk, the earlier session predates the newer seed.
        var lines = File.ReadAllLines(LedgerFile(Account));
        Assert.Equal(2, lines.Count(line => line.Contains("\"kind\":\"seed\"")));
        Assert.Equal(2, lines.Count(line => line.Contains("\"kind\":\"play\"")));
    }

    [Fact]
    public void ARepeatedLineCountsOnce()
    {
        Play(Account, Valorant, Now.AddHours(-2), Now.AddHours(-1));
        var line = File.ReadAllLines(LedgerFile(Account)).Single(l => l.Contains("\"kind\":\"play\""));
        File.AppendAllText(LedgerFile(Account), line + "\n");

        RiotLedger.ForgetLoaded();
        Assert.Equal(60, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ALineFromAFutureSchemaAndATornWriteAreSkipped()
    {
        Play(Account, Valorant, Now.AddHours(-2), Now.AddHours(-1));
        File.AppendAllText(LedgerFile(Account),
            "{\"schema\":2,\"kind\":\"play\",\"key\":\"x\",\"game\":\"riot:valorant\",\"minutes\":999}\n" +
            "{\"schema\":1,\"kind\":\"pl");

        RiotLedger.ForgetLoaded();
        Assert.Equal(60, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ACrashLosesNoMoreThanTheMinutesSinceTheLastCheckpoint()
    {
        var processStart = Now.AddMinutes(-30);
        RiotLedger.Begin(Account, Valorant, processStart);
        RiotLedger.Checkpoint(Now.AddMinutes(-25));
        var afterCheckpoint = RiotLedger.Totals(Account, Valorant);
        Assert.Equal(5, afterCheckpoint);

        // Exo dies here. The game keeps running, and the next Exo finds it from its own start.
        RiotLedger.ForgetLoaded();
        RiotLedger.Begin(Account, Valorant, processStart);
        RiotLedger.End(Valorant, Now.AddMinutes(-20));

        // 30 minutes of play from processStart to -20 min is 10 of them; the 5 checkpointed are not counted again.
        var total = RiotLedger.Totals(Account, Valorant);
        Assert.Equal(10, total);
        Assert.True(total >= afterCheckpoint, "the total never goes down");
    }

    [Fact]
    public void OverlappingSessionsOfOneGameCountOnce()
    {
        RiotLedger.Begin(Account, Valorant, Now.AddMinutes(-60));
        RiotLedger.Begin(Account, Valorant, Now.AddMinutes(-30));
        RiotLedger.End(Valorant, Now);

        Assert.Equal(60, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ADifferentGameKeepsItsOwnSession()
    {
        RiotLedger.Begin(Account, Valorant, Now.AddMinutes(-60));
        RiotLedger.Begin(Account, League, Now.AddMinutes(-30));
        RiotLedger.End(Valorant, Now);
        RiotLedger.End(League, Now);

        Assert.Equal(60, RiotLedger.Totals(Account, Valorant));
        Assert.Equal(30, RiotLedger.Totals(Account, League));
    }

    [Fact]
    public void ACancelledSessionCountsNothing()
    {
        // A helper process that exits at once: nothing was checkpointed, so nothing is written.
        RiotLedger.Begin(Account, Valorant, Now.AddMinutes(-2));
        RiotLedger.Cancel(Valorant);
        RiotLedger.End(Valorant, Now);

        Assert.Equal(0, RiotLedger.Totals(Account, Valorant));
        Assert.False(File.Exists(LedgerFile(Account)));
    }

    [Fact]
    public void OtherRiotIdsHoursAndSessionsStayOnTheirOwnAccount()
    {
        RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10));
        RiotLedger.Seed(OtherAccount, Seed(Valorant, 100), Now.AddDays(-10));
        Play(OtherAccount, Valorant, Now.AddHours(-1), Now);

        Assert.Equal(6000, RiotLedger.Totals(Account, Valorant));
        Assert.Equal(160, RiotLedger.Totals(OtherAccount, Valorant));
    }

    [Fact]
    public void WithoutASeedTheTrackedMinutesStillCount()
    {
        // tracker.gg blocked or private: the seed waits, the sessions count from now.
        Assert.False(RiotLedger.HasSeed(Account));
        Play(Account, Valorant, Now.AddMinutes(-30), Now);

        Assert.Equal(30, RiotLedger.Totals(Account, Valorant));
        Assert.False(RiotLedger.HasSeed(Account));
    }

    [Fact]
    public void ANonRiotOrEmptySeedIsNotWritten()
    {
        Assert.False(RiotLedger.Seed(Account, new Dictionary<string, GameSeed> { ["steam:730"] = new GameSeed(500) }, Now));
        Assert.False(RiotLedger.Seed(Account, Seed(Valorant, 0), Now));
        Assert.False(RiotLedger.HasSeed(Account));
    }

    [Fact]
    public void AnHoursFileFromBeforeTheLedgerBecomesTheSeed()
    {
        File.WriteAllText(Path.Combine(_folder, "exo-imported-lifetime.json"),
            "{\"accountKey\":\"" + Account + "\",\"observedAt\":\"2026-10-01T08:00:00+00:00\"," +
            "\"minutes\":{\"riot:valorant\":4000,\"steam:1\":5}}");

        Assert.Equal(4000, RiotLedger.Totals(Account, Valorant));
        Assert.False(File.Exists(Path.Combine(_folder, "exo-imported-lifetime.json")));
    }

    [Fact]
    public void AnotherRiotIdsHoursFileIsLeftForWhenItsIdIsUsedAgain()
    {
        var legacy = Path.Combine(_folder, "exo-imported-lifetime.json");
        File.WriteAllText(legacy,
            "{\"accountKey\":\"" + OtherAccount + "\",\"observedAt\":\"2026-10-01T08:00:00+00:00\"," +
            "\"minutes\":{\"riot:valorant\":4000}}");

        Assert.Equal(0, RiotLedger.Totals(Account, Valorant));
        Assert.True(File.Exists(legacy));
        Assert.Equal(4000, RiotLedger.Totals(OtherAccount, Valorant));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void ASessionLineCarriesTheModeTheClientReported()
    {
        RiotLedger.Begin(Account, Valorant, Now.AddHours(-1));
        RiotLedger.SetMode(Valorant, "competitive");
        RiotLedger.End(Valorant, Now);

        Assert.Contains("\"mode\":\"competitive\"", File.ReadAllText(LedgerFile(Account)));
        Assert.Equal(60, RiotLedger.ModeTotals(Account, Valorant)["competitive"]);
    }

    [Fact]
    public void ASessionNoClientNamedIsUnknown()
    {
        Play(Account, Valorant, Now.AddHours(-1), Now);

        Assert.Equal(60, RiotLedger.ModeTotals(Account, Valorant)["unknown"]);
    }

    [Fact]
    public void ModeTotalsAddTheSeedsSplitAndTheTaggedSessionsToTheLifetime()
    {
        var split = new Dictionary<string, int> { ["competitive"] = 4000, ["unrated"] = 2000 };
        RiotLedger.Seed(Account, new Dictionary<string, GameSeed> { [Valorant] = new GameSeed(6000, split) }, Now.AddDays(-10));
        RiotLedger.Begin(Account, Valorant, Now.AddHours(-1));
        RiotLedger.SetMode(Valorant, "competitive");
        RiotLedger.End(Valorant, Now);

        var modes = RiotLedger.ModeTotals(Account, Valorant);
        Assert.Equal(4060, modes["competitive"]);
        Assert.Equal(2000, modes["unrated"]);
        // The modes add up to the lifetime total, which is the seed plus the tagged session.
        Assert.Equal(6060, RiotLedger.Totals(Account, Valorant));
        Assert.Equal(RiotLedger.Totals(Account, Valorant), modes.Values.Sum());
    }

    [Fact]
    public void SeedMinutesTrackerDidNotSplitAreUnknownWhole()
    {
        RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10));
        Play(Account, Valorant, Now.AddHours(-1), Now);

        // The seed has no split, and the session had no mode read: both are unknown, and they make the whole lifetime.
        var modes = RiotLedger.ModeTotals(Account, Valorant);
        Assert.Equal(6060, modes["unknown"]);
        Assert.Equal(6060, modes.Values.Sum());
    }

    [Fact]
    public void ALineWithoutAModeFieldReadsAsUnknown()
    {
        // A line written before modes were tagged: no "mode" field at all.
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerFile(Account))!);
        File.WriteAllText(LedgerFile(Account),
            "{\"schema\":1,\"kind\":\"play\",\"key\":\"old\",\"game\":\"riot:valorant\",\"startUtc\":\"" +
            Now.AddHours(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture) +
            "\",\"minutes\":30,\"source\":\"exo\"}\n");

        RiotLedger.ForgetLoaded();
        Assert.Equal(30, RiotLedger.ModeTotals(Account, Valorant)["unknown"]);
    }

    [Fact]
    public void TheSeedsStatsAndSplitSurviveARestart()
    {
        var stats = new RiotStats(200, 100, 1.12, "Gold 2", "Platinum 1", new[] { "Jett", "Omen" });
        var split = new Dictionary<string, int> { ["competitive"] = 4000 };
        RiotLedger.Seed(Account, new Dictionary<string, GameSeed> { [Valorant] = new GameSeed(6000, split, stats) }, Now.AddDays(-10));

        RiotLedger.ForgetLoaded();
        var back = RiotLedger.Stats(Account, Valorant)!;
        Assert.Equal(200, back.Matches);
        Assert.Equal(100, back.Wins);
        Assert.Equal(1.12, back.KillDeath);
        Assert.Equal("Gold 2", back.CurrentRank);
        Assert.Equal("Platinum 1", back.PeakRank);
        Assert.Equal(new[] { "Jett", "Omen" }, back.TopPicks);
        Assert.Equal(4000, RiotLedger.ModeTotals(Account, Valorant)["competitive"]);
    }

    [Fact]
    public void ASeedWithoutStatsHasNoStats()
    {
        RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10));

        RiotLedger.ForgetLoaded();
        Assert.Null(RiotLedger.Stats(Account, Valorant));
    }

    [Fact]
    public void AFileThatCannotBeReadIsNotKeptAsAnEmptyLedger()
    {
        // A read that fails part-way (another process holds the file) must not be cached: the next read gets the whole file.
        Assert.True(RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10)));
        RiotLedger.ForgetLoaded();
        using (new FileStream(LedgerFile(Account), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _ = RiotLedger.Totals(Account, Valorant);
        }

        Assert.Equal(6000, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ASessionAppendedAfterATornLineCountsOnItsOwnLine()
    {
        // A write cut off mid-line leaves no newline; the next line must start on a line of its own, or it is lost with the torn one.
        Assert.True(RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10)));
        File.AppendAllText(LedgerFile(Account), "{\"schema\":1,\"kind\":\"play\",\"key\":\"torn");
        RiotLedger.ForgetLoaded();
        Play(Account, Valorant, Now.AddHours(-3), Now.AddHours(-1));

        RiotLedger.ForgetLoaded();
        Assert.Equal(6120, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ATornSeedLineKeepsTheLastCompleteSeedAndLaterSessionsStillCount()
    {
        // A torn seed is skipped: the lifetime stays at the last complete seed, and sessions after it still add.
        Assert.True(RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-10)));
        File.AppendAllText(LedgerFile(Account), "{\"schema\":1,\"kind\":\"seed\",\"key\":\"seed|riot:valorant|torn\",\"game\":\"riot:valorant\",\"minutes\":9");
        RiotLedger.ForgetLoaded();
        Assert.Equal(6000, RiotLedger.Totals(Account, Valorant));

        Play(Account, Valorant, Now.AddHours(-3), Now.AddHours(-1));
        RiotLedger.ForgetLoaded();
        Assert.Equal(6120, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void ALegacyHoursFileWithNamesThatDifferOnlyInCaseDoesNotThrow()
    {
        // The hours file from before the ledger may hold one game under two spellings: the migration takes the larger, and does not throw.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path.Combine(_folder, "exo-imported-lifetime.json"),
            "{\"accountKey\":\"" + Account + "\",\"observedAt\":\"2026-11-01T00:00:00Z\",\"minutes\":{\"riot:valorant\":600,\"RIOT:VALORANT\":700}}");

        Assert.Equal(700, RiotLedger.Totals(Account, Valorant));
    }

    [Fact]
    public void APartialSeedCountsAndIsMarkedUntilALifetimeSeedReplacesIt()
    {
        // A playlist-only profile seeds its sum as partial; the next lifetime seed is newer and replaces it, unmarked.
        RiotLedger.Seed(Account, new Dictionary<string, GameSeed> { [Valorant] = new GameSeed(900, Partial: true) }, Now.AddDays(-10));
        RiotLedger.ForgetLoaded();
        Assert.Equal(900, RiotLedger.Totals(Account, Valorant));
        Assert.True(RiotLedger.IsPartial(Account, Valorant));

        RiotLedger.Seed(Account, Seed(Valorant, 6000), Now.AddDays(-1));
        RiotLedger.ForgetLoaded();
        Assert.Equal(6000, RiotLedger.Totals(Account, Valorant));
        Assert.False(RiotLedger.IsPartial(Account, Valorant));
    }

    [Fact]
    public void ATrackerReadingWithOnlyPlaylistsSeedsAsPartialAndALifetimeOneWhole()
    {
        // A profile with no overview lifetime is imported as before, marked partial; a lifetime reading is whole.
        var readings = new Dictionary<string, TrackerGgPlaytime.RiotGameSeedReading>
        {
            [Valorant] = new(900, false, null, null),
            [League] = new(6000, true, null, null),
        };

        var seeds = TrackerGgPlaytime.ToSeeds(readings);

        Assert.True(seeds[Valorant].Partial);
        Assert.Equal(900, seeds[Valorant].Minutes);
        Assert.False(seeds[League].Partial);
    }

    [Fact]
    public void ALedgerLineFromAnotherPcCountsOnceAndIsNotWrittenTwice()
    {
        // A riot-ledger item pulled from the account is one line, added by key: a second pull of it changes nothing.
        var line = "{\"account\":\"" + Account + "\",\"schema\":1,\"kind\":\"play\",\"key\":\"other-pc|1\",\"game\":\"" + Valorant +
                   "\",\"mode\":\"competitive\",\"startUtc\":\"" + Now.AddHours(-3).ToString("O") + "\",\"minutes\":120,\"source\":\"exo\"}";

        RiotLedger.Merge(line);
        RiotLedger.Merge(line);

        RiotLedger.ForgetLoaded();
        Assert.Equal(120, RiotLedger.Totals(Account, Valorant));
        Assert.Single(File.ReadAllLines(LedgerFile(Account)));
    }

    [Fact]
    public void ALedgerLineWhoseAccountIsNotAKeyIsRefusedAndWritesNothing()
    {
        // The account comes from another PC's data: it must be a 20-hex key, or a line could be written outside the ledger folder.
        RiotLedger.Merge("{\"account\":\"..\\\\escape\",\"schema\":1,\"kind\":\"play\",\"key\":\"k\",\"game\":\"" + Valorant +
                         "\",\"mode\":\"competitive\",\"startUtc\":\"" + Now.ToString("O") + "\",\"minutes\":5,\"source\":\"exo\"}");

        var ledgerFolder = Path.Combine(_folder, "riot-ledger");
        Assert.False(Directory.Exists(ledgerFolder) && Directory.GetFiles(ledgerFolder).Length > 0);
    }

    [Fact]
    public void AnExoSessionOfARiotGameShowsOnlyThisRiotIdsLedgerNotTheGamesCounter()
    {
        // The per-game Exo counter holds every Riot ID's sessions and other PCs' play: a Riot row never takes it.
        Assert.Equal(0, PlaytimeService.ExoMinutesForRow(StoreKind.Riot, 500));
        Assert.Equal(500, PlaytimeService.ExoMinutesForRow(StoreKind.Steam, 500));
    }
}
