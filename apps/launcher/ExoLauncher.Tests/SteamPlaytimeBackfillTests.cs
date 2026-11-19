using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways play time from Steam's own process log can come out wrong:
/// - the log's local times are read as UTC, or across a daylight-saving change with the wrong offset;
/// - a session ends at the first process Steam stops tracking instead of the last, or never ends on "going away";
/// - a session still open when Steam started again (or at the log's end) is counted with a made-up end;
/// - the rotated .previous file is skipped, so a session across the rotation is lost;
/// - a session Exo counted live (its own Play, or one it adopted) is added again, or the seconds between Exo's and
///   Steam's own start and exit are added as play;
/// - when Exo closed mid-game, the rest of the session is lost, or the whole of it is added;
/// - a session read once is added again on the next read, or play from before Exo was installed is added;
/// - the first read adds the sessions Exo counted before it kept them.
/// The log lines are this PC's own (app ids and times only; commands cut down). Stateful fixture ids match nothing.
/// </summary>
public sealed class SteamPlaytimeBackfillTests : IDisposable
{
    // This PC's Phantom Blade Zero (4115450) nights, Central time (UTC-5 then): launched from Exo and closed with Steam
    // ("going away"), then 162 minutes played while Exo was closed, then a short run from Exo again.
    private const string PhantomBladeZero = """


        [2026-10-28 21:22:39] Client version: 1793235415
        [2026-10-28 21:22:39] SSGL: UI mode (0->7)
        [2026-10-28 21:23:26] AppID 4115450 adding PID 9804 as a tracked process ""C:\Program Files (x86)\Steam\steamapps\common\PBZ\steam\PBZ.exe""
        [2026-10-28 21:23:26] SSGL: InternalUpdateClientGame indicates change to games list
        [2026-10-28 21:23:27] AppID 4115450 adding PID 16020 as a tracked process
        [2026-10-28 21:23:27] SSGL: change [4115450] LCT 10706688->0
        [2026-10-28 21:23:40] AppID 4115450 adding PID 18480 as a tracked process
        [2026-10-28 21:25:29] SSGL: change [4115450] PCSer
        [2026-10-28 21:26:17] Game 4115450 going away; no longer tracking PID 9804
        [2026-10-28 21:26:17] Game 4115450 going away; no longer tracking PID 16020
        [2026-10-28 21:26:17] Game 4115450 going away; no longer tracking PID 18480


        [2026-10-28 22:21:37] Client version: 1793235415
        [2026-10-28 22:21:37] SSGL: UI mode (0->7)
        [2026-10-28 22:22:00] AppID 4115450 adding PID 9952 as a tracked process
        [2026-10-28 22:22:00] AppID 4115450 adding PID 13828 as a tracked process
        [2026-10-28 22:22:11] AppID 4115450 adding PID 1936 as a tracked process
        [2026-10-28 23:00:29] SSGL: change [4115450] LCT 0->1
        [2026-10-29 01:04:46] AppID 4115450 no longer tracking PID 1936, exit code 0
        [2026-10-29 01:04:46] AppID 4115450 no longer tracking PID 13828, exit code 0
        [2026-10-29 01:04:46] AppID 4115450 no longer tracking PID 9952, exit code 0
        [2026-10-29 01:04:46] Remove 4115450 from running list


        [2026-10-29 08:37:29] Client version: 1793235415
        [2026-10-29 08:37:29] AppID 4115450 adding PID 18824 as a tracked process
        [2026-10-29 08:37:30] AppID 4115450 adding PID 10840 as a tracked process
        [2026-10-29 08:37:43] AppID 4115450 adding PID 2932 as a tracked process
        [2026-10-29 08:40:09] AppID 4115450 no longer tracking PID 2932, exit code 1
        [2026-10-29 08:40:09] AppID 4115450 no longer tracking PID 10840, exit code 1
        [2026-10-29 08:40:09] AppID 4115450 no longer tracking PID 18824, exit code 1
        [2026-10-29 08:40:09] Remove 4115450 from running list


        [2026-10-29 09:12:17] Client version: 1793235415
        [2026-10-29 09:12:17] SSGL: UI mode (0->7)
        """;

    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset ExoInstalled = Utc("2026-09-24T12:48:28Z");
    private static readonly SteamGameProcessLog.Session Launched = new("4115450", Utc("2026-10-29T02:23:26Z"), Utc("2026-10-29T02:26:17Z"));
    private static readonly SteamGameProcessLog.Session Lost = new("4115450", Utc("2026-10-29T03:22:00Z"), Utc("2026-10-29T06:04:46Z"));
    private static readonly SteamGameProcessLog.Session Again = new("4115450", Utc("2026-10-29T13:37:29Z"), Utc("2026-10-29T13:40:09Z"));

    private static IReadOnlyList<SteamGameProcessLog.Session> Read(string log, out DateTimeOffset? readTo)
    {
        var (sessions, read) = SteamGameProcessLog.Sessions(log.ReplaceLineEndings("\r\n"), Central);
        readTo = read;
        return sessions;
    }

    private static Dictionary<string, List<PlaytimeService.Span>> Counted(params (string Start, string End)[] spans) =>
        new() { ["steam:4115450"] = spans.Select(span => new PlaytimeService.Span(Utc(span.Start), Utc(span.End))).ToList() };

    [Fact]
    public void ReadsThisPcsSessionsInUtcFromTheFirstProcessToTheLast()
    {
        var sessions = Read(PhantomBladeZero, out var readTo);
        Assert.Equal([Launched, Lost, Again], sessions);
        Assert.Equal(Utc("2026-10-29T14:12:17Z"), readTo);
    }

    [Fact]
    public void ASessionWithNoKnownEndIsLeftOut()
    {
        var sessions = Read("""
            [2026-10-20 20:00:00] AppID 2767030 adding PID 19068 as a tracked process
            [2026-10-20 21:00:00] Client version: 1793235415
            [2026-10-20 21:05:00] AppID 2767030 adding PID 10892 as a tracked process
            [2026-10-20 21:06:00] AppID 730 adding PID 5780 as a tracked process
            [2026-10-20 21:20:00] AppID 730 no longer tracking PID 10892, exit code 0
            [2026-10-20 21:30:00] AppID 730 no longer tracking PID 5780, exit code 0
            [2026-10-20 21:31:00] Remove -414123329 from running list
            """, out var readTo);
        // 2767030's first run had Steam start again under it, its second never ended; another app's PID is not its own.
        Assert.Equal([new SteamGameProcessLog.Session("730", Utc("2026-10-21T02:06:00Z"), Utc("2026-10-21T02:30:00Z"))], sessions);
        Assert.Equal(Utc("2026-10-21T02:30:00Z"), readTo);
    }

    [Fact]
    public void LocalTimesFollowDaylightSavingTime()
    {
        // Central time leaves daylight saving at 2:00 on November 1, 2026: four hours on the clock are five played.
        var session = Assert.Single(Read("""
            [2026-10-31 23:00:00] AppID 730 adding PID 5780 as a tracked process
            [2026-11-01 03:00:00] AppID 730 no longer tracking PID 5780, exit code 0
            """, out _));
        Assert.Equal(Utc("2026-11-01T04:00:00Z"), session.StartUtc);
        Assert.Equal(TimeSpan.FromHours(5), session.EndUtc - session.StartUtc);
    }

    [Fact]
    public void TheRotatedLogIsReadFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        try
        {
            Assert.Equal("", SteamGameProcessLog.ReadWithPrevious(root).Trim());
            File.WriteAllText(Path.Combine(root, "logs", "gameprocess_log.previous.txt"),
                "[2026-10-28 22:22:00] AppID 4115450 adding PID 9952 as a tracked process\r\n");
            File.WriteAllText(Path.Combine(root, "logs", "gameprocess_log.txt"),
                "\r\n[2026-10-29 01:04:46] AppID 4115450 no longer tracking PID 9952, exit code 0\r\n");
            var (sessions, _) = SteamGameProcessLog.Sessions(SteamGameProcessLog.ReadWithPrevious(root), Central);
            Assert.Equal([Lost], sessions);
        }
        finally { DeletionGate.DeleteDirectory(root, recursive: true); }
    }

    [Fact]
    public void ThisPcsLostSessionIsCreditedAndExosOwnSessionsAreNot()
    {
        var sessions = Read(PhantomBladeZero, out _);
        // Exo's own sessions as it records them now: its Play at 02:22:49 counted to its own exit check at 02:30:40
        // (Steam stopped tracking at 02:26:17), and the morning run a few seconds short of Steam's.
        var credit = Assert.Single(PlaytimeService.PlanSteamBackfill(sessions, ExoInstalled, ExoInstalled,
            Counted(("2026-10-29T02:22:49Z", "2026-10-29T02:30:40Z"), ("2026-10-29T13:37:26Z", "2026-10-29T13:39:51Z")), null));
        Assert.Equal(("steam:4115450", 162, Lost.EndUtc), credit);
    }

    [Fact]
    public void TheFirstReadOnThisPcTakesOffWhatExoCountedBeforeItKeptSessions()
    {
        // playtime.json before this change: 9 minutes of Exo's own, no sessions kept. The log holds 168 minutes 17 s.
        var credit = Assert.Single(PlaytimeService.PlanSteamBackfill(Read(PhantomBladeZero, out _), ExoInstalled, ExoInstalled,
            new Dictionary<string, List<PlaytimeService.Span>>(), new Dictionary<string, int> { ["steam:4115450"] = 9 }));
        Assert.Equal(("steam:4115450", 159, Again.EndUtc), credit);
    }

    [Fact]
    public void WhenExoClosedMidGameOnlyTheRestIsAdded()
    {
        var credit = Assert.Single(PlaytimeService.PlanSteamBackfill([Lost], ExoInstalled, ExoInstalled,
            Counted(("2026-10-29T03:22:00Z", "2026-10-29T04:00:00Z")), null));
        Assert.Equal(124, credit.Minutes);
    }

    [Fact]
    public void ASpanExoAlreadyCountedIsNotAddedAgainAndACutMinuteIsRestored()
    {
        // 1 min 56 s. Written as 1 when minutes were cut off; the span is the session Exo counted.
        var start = Utc("2026-11-01T22:08:43.6462067Z");
        var end = Utc("2026-11-01T22:10:39.8708662Z");
        Assert.Equal(2, PlaytimeService.MinutesOf(end - start));
        var spans = new Dictionary<string, List<PlaytimeService.Span>>
        {
            ["steam:999999961"] = [new(start, end)],
        };
        // Steam's log starts a second earlier and ends a few seconds later: that gap is not another session.
        var steam = new SteamGameProcessLog.Session("999999961", start.AddSeconds(-1), end.AddSeconds(3));
        Assert.Empty(PlaytimeService.PlanSteamBackfill([steam], start.AddDays(-40), start.AddDays(-40), spans, null));
        Assert.Equal(2, PlaytimeService.MinutesKept(1, spans["steam:999999961"]));
        Assert.Equal(2, PlaytimeService.MinutesKept(2, spans["steam:999999961"]));
        Assert.Equal(40, PlaytimeService.MinutesKept(40, spans["steam:999999961"]));
    }

    [Fact]
    public void FamilySharedAndOfflinePlayStayExosOwnSession()
    {
        var game = Game("999999962");
        var start = DateTimeOffset.UtcNow.AddSeconds(-116);
        PlaytimeService.AdoptSession(game.Id, start);
        PlaytimeService.EndSession(game.Id, start.AddSeconds(116));
        // Steam writes no playtime for a family share or an offline session, so the card is Exo's session alone.
        int? Minutes(int? steam) => Assert.Single(PlaytimeService.Enrich([game],
            steam is { } store ? new Dictionary<string, int> { [game.LaunchTarget!] = store } : null)).PlaytimeMinutes;
        Assert.Equal(2, Minutes(null));
        Assert.Equal(2, Minutes(0));
    }

    [Fact]
    public void NothingReadBeforeOrPlayedBeforeExoIsAdded()
    {
        var sessions = Read(PhantomBladeZero, out _);
        var none = new Dictionary<string, List<PlaytimeService.Span>>();
        // Already read through the lost session: only the morning run is new.
        Assert.Equal(2, Assert.Single(PlaytimeService.PlanSteamBackfill(sessions, ExoInstalled, Lost.EndUtc, none, null)).Minutes);
        // Exo installed at 04:00: the night before it is Steam's own, the rest of the session from then on is Exo's to add.
        var installed = Utc("2026-10-29T04:00:00Z");
        Assert.Equal(127, Assert.Single(PlaytimeService.PlanSteamBackfill(sessions, installed, installed, none, null)).Minutes);
    }

    // The one test that reads a log into PlaytimeService itself: the read-through mark is process-wide.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-steam-backfill-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public SteamPlaytimeBackfillTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
        PlaytimeService.ForgetLoaded();
    }

    public void Dispose()
    {
        PlaytimeService.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    private static GameEntry Game(string appId) => new()
    {
        Id = "steam:" + appId, Title = "Exo Backfill Fixture Qx", Store = StoreKind.Steam, LaunchTarget = appId,
    };

    [Fact]
    public void SteamsLogIsReadOnceAndNeverAddsWhatExoCounted()
    {
        GameEntry live = Game("999999951"), closed = Game("999999952");
        GameEntry Shown(GameEntry game) => Assert.Single(PlaytimeService.Enrich([game]));
        var installed = Utc("2025-01-01T00:00:00Z");

        // Exo counts one game live, 15:00 to 16:00 UTC (10:00 to 11:00 Central).
        PlaytimeService.AdoptSession(live.Id, Utc("2025-06-10T15:00:00Z"));
        PlaytimeService.EndSession(live.Id, Utc("2025-06-10T16:00:00Z"));
        var log = """
            [2025-06-10 09:59:50] AppID 999999951 adding PID 100 as a tracked process
            [2025-06-10 11:00:03] AppID 999999951 no longer tracking PID 100, exit code 0
            [2025-06-10 12:00:00] AppID 999999952 adding PID 200 as a tracked process
            [2025-06-10 13:30:00] AppID 999999952 no longer tracking PID 200, exit code 0
            """.ReplaceLineEndings("\r\n");

        PlaytimeService.BackfillSteam(log, installed, Central);
        Assert.Equal(60, Shown(live).PlaytimeMinutes);
        Assert.Equal(90, Shown(closed).PlaytimeMinutes);

        // The same log again adds nothing; a session after it adds only itself.
        PlaytimeService.BackfillSteam(log, installed, Central);
        Assert.Equal(90, Shown(closed).PlaytimeMinutes);
        PlaytimeService.BackfillSteam(log + """

            [2025-06-10 14:00:00] AppID 999999952 adding PID 300 as a tracked process
            [2025-06-10 14:30:00] AppID 999999952 no longer tracking PID 300, exit code 0
            """.ReplaceLineEndings("\r\n"), installed, Central);
        Assert.Equal(120, Shown(closed).PlaytimeMinutes);
        Assert.Equal(Utc("2025-06-10T19:30:00Z"), Shown(closed).LastPlayedUtc);
        Assert.Equal(60, Shown(live).PlaytimeMinutes);
    }
}
