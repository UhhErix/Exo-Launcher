using System.Diagnostics;
using System.Globalization;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the session recorder can fail silently:
/// - a statistic is wrong at an edge: an empty list, one frame, every frame the same, frames over 50 ms, a frame of exactly 50 ms;
/// - a 1% low is the average, or the slowest frame, or rounds the count of frames to zero;
/// - it records only while Exo's own overlay is on, or records nothing for a game that shows its own FPS;
/// - a missing right looks like a game with no frames, so nobody can tell it is not measuring;
/// - a short session is used to judge a setting;
/// - a changed Keep value keeps the old settings version, or the same entries in another order make a new one;
/// - sessions pile up without end, or the file grows with the number of games and versions;
/// - a session is lost when the game closes, when another game takes the front, or when Exo exits;
/// - a state of the recorder has no way out, or a way out loses what was measured;
/// - the ring that carries the frames loses or repeats one, or reads one from before a pause as a frame;
/// - it starts for a Riot game, which Exo never touches.
/// </summary>
public sealed class SessionRecorderTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static List<double> Many(double ms, int count) => [.. Enumerable.Repeat(ms, count)];

    // ---------------------------------------------------------------- the statistics

    [Fact]
    public void AnEmptyListIsAllZeros()
    {
        var result = FrameStats.Of([]);
        Assert.Equal(0, result.Frames);
        Assert.Equal(0, result.Seconds);
        Assert.Equal(0, result.AverageMs);
        Assert.Equal(0, result.AverageFps);
        Assert.Equal(0, result.Low1Fps);
        Assert.Equal(0, result.Low01Fps);
        Assert.Equal(0, result.Stalls);
        Assert.Equal(FrameStats.HistogramBuckets, result.Histogram.Length);
        Assert.All(result.Histogram, count => Assert.Equal(0, count));
    }

    [Fact]
    public void OneFrameIsItsOwnAverageAndBothLows()
    {
        var result = FrameStats.Of([8]);
        Assert.Equal(1, result.Frames);
        Assert.Equal(0.008, result.Seconds, 9);
        Assert.Equal(8, result.AverageMs, 6);
        Assert.Equal(125, result.AverageFps, 4);
        Assert.Equal(125, result.Low1Fps, 4);
        Assert.Equal(125, result.Low01Fps, 4);
        Assert.Equal(1, result.Histogram[8]);
    }

    [Fact]
    public void FramesThatAreAllTheSameGiveTheSameNumberEverywhere()
    {
        var result = FrameStats.Of(Many(10, 1000));
        Assert.Equal(1000, result.Frames);
        Assert.Equal(10, result.Seconds, 6);
        Assert.Equal(10, result.AverageMs, 6);
        Assert.Equal(100, result.AverageFps, 4);
        Assert.Equal(100, result.Low1Fps, 4);
        Assert.Equal(100, result.Low01Fps, 4);
        Assert.Equal(0, result.Stalls);
        Assert.Equal(1000, result.Histogram[10]);
        Assert.Equal(1000, result.Histogram.Sum());
    }

    [Fact]
    public void TheLowsAreTheRateOfTheSlowestOnePercentAndTenthOfAPercent()
    {
        // 990 frames of 10 ms and 10 of 100 ms: the slowest 10 are the long ones, the slowest 1 is one of them.
        var result = FrameStats.Of([.. Many(10, 990), .. Many(100, 10)]);
        Assert.Equal(1000, result.Frames);
        Assert.Equal(10.9, result.AverageMs, 6);
        Assert.Equal(1000 / 10.9, result.AverageFps, 4);
        Assert.Equal(10, result.Low1Fps, 4);
        Assert.Equal(10, result.Low01Fps, 4);
        Assert.Equal(10, result.Stalls);
        Assert.Equal(990, result.Histogram[10]);
        Assert.Equal(990, result.Histogram.Sum());
    }

    [Fact]
    public void TheOnePercentLowAveragesTheSlowestFramesAndTheTenthOfAPercentIsTheSlowestOne()
    {
        // 198 frames of 10 ms, one of 20 ms, one of 40 ms: the slowest 1% of 200 is two frames, the slowest 0.1% is one.
        var result = FrameStats.Of([.. Many(10, 198), 20, 40]);
        Assert.Equal(2 / 0.060, result.Low1Fps, 4);
        Assert.Equal(1 / 0.040, result.Low01Fps, 4);
    }

    [Fact]
    public void AListTooShortForAPercentUsesTheSlowestFrame()
    {
        var result = FrameStats.Of([5, 5, 5, 20]);
        Assert.Equal(50, result.Low1Fps, 4);
        Assert.Equal(50, result.Low01Fps, 4);
    }

    [Fact]
    public void TheHistogramHasOneBucketPerMillisecondAndFiftyMillisecondsIsTheLastOne()
    {
        var result = FrameStats.Of([0.4, 0.9, 1.0, 1.99, 2, 48.5, 49, 49.99, 50]);
        Assert.Equal(2, result.Histogram[0]);
        Assert.Equal(2, result.Histogram[1]);
        Assert.Equal(1, result.Histogram[2]);
        Assert.Equal(1, result.Histogram[48]);
        Assert.Equal(3, result.Histogram[49]);
        Assert.Equal(9, result.Histogram.Sum());
        Assert.Equal(0, result.Stalls);
    }

    [Fact]
    public void FramesOverFiftyMillisecondsAreStallsAndLeaveTheHistogramButNotTheAverageOrTheLows()
    {
        var result = FrameStats.Of([60, 70, 80]);
        Assert.Equal(3, result.Frames);
        Assert.Equal(3, result.Stalls);
        Assert.Equal(70, result.AverageMs, 6);
        Assert.Equal(1000 / 80.0, result.Low01Fps, 4);
        Assert.Equal(1000 / 80.0, result.Low1Fps, 4);
        Assert.Equal(0, result.Histogram.Sum());
    }

    [Fact]
    public void OneToAHundredMillisecondsHasKnownAnswers()
    {
        var result = FrameStats.Of([.. Enumerable.Range(1, 100).Select(i => (double)i)]);
        Assert.Equal(50.5, result.AverageMs, 6);
        Assert.Equal(50, result.Stalls);
        Assert.Equal(2, result.Histogram[49]);
        Assert.Equal(1, result.Histogram[1]);
        Assert.Equal(10, result.Low1Fps, 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(5000)]
    [InlineData(60000)]
    public void SomethingThatIsNotAFrameIsNotCounted(double ms)
    {
        var result = FrameStats.Of([10, ms, 10]);
        Assert.Equal(2, result.Frames);
        Assert.Equal(10, result.AverageMs, 6);
    }

    [Fact]
    public void JustUnderTheLimitIsAStall()
    {
        var result = FrameStats.Of([4999]);
        Assert.Equal(1, result.Frames);
        Assert.Equal(1, result.Stalls);
    }

    [Fact]
    public void TheAccumulatorAgreesWithABruteForceCalculationOnARandomSession()
    {
        var random = new Random(26);
        var list = new List<double>();
        for (var i = 0; i < 20000; i++) list.Add(i % 400 == 0 ? 55 + random.NextDouble() * 200 : 2 + random.NextDouble() * 38);
        var result = FrameStats.Of(list);
        var descending = list.OrderByDescending(ms => ms).ToList();
        double Low(double share)
        {
            var count = Math.Max(1, (int)Math.Ceiling(list.Count * share - 1e-9));
            return count / (descending.Take(count).Sum() / 1000);
        }
        Assert.Equal(list.Average(), result.AverageMs, 6);
        Assert.Equal(Low(0.01), result.Low1Fps, Low(0.01) * 0.001);
        Assert.Equal(Low(0.001), result.Low01Fps, Low(0.001) * 0.001);
        Assert.Equal(list.Count(ms => ms > 50), result.Stalls);
        Assert.Equal(list.Count(ms => ms <= 50), result.Histogram.Sum());
    }

    // ---------------------------------------------------------------- the settings version

    [Fact]
    public void ThereIsNoRecordMeansNone()
    {
        Assert.Equal("none", SettingsVersion.Of(null));
        Assert.Equal("none", SettingsVersion.Of([]));
        Assert.Equal("none", SettingsVersion.ForGame(new Keep.Doc(), "steam:730"));
    }

    private static Keep.Doc DocWith(string gameId, params (string Target, string Intended)[] entries) =>
        DocWithStates(gameId, [.. entries.Select(entry => (entry.Target, entry.Intended, KeepState.Held))]);

    private static Keep.Doc DocWithStates(string gameId, params (string Target, string Intended, KeepState State)[] entries)
    {
        var game = new Keep.KeepGame { GameIds = [gameId] };
        foreach (var (target, intended, state) in entries) game.Entries[target] = new Keep.KeepEntry { Target = target, Intended = intended, State = state };
        var doc = new Keep.Doc();
        doc.Games["profile"] = game;
        return doc;
    }

    // Only a value that is Exo's in the file is part of what the frames were measured under.
    [Fact]
    public void AYieldedValueStartsANewVersion()
    {
        var held = SettingsVersion.ForGame(DocWithStates("steam:1", ("a", "1", KeepState.Held), ("b", "2", KeepState.Held)), "steam:1");
        var yielded = SettingsVersion.ForGame(DocWithStates("steam:1", ("a", "1", KeepState.Held), ("b", "2", KeepState.Yielded)), "steam:1");
        Assert.NotEqual(held, yielded);
        // A value put back is Exo's again, and counts the same as one held.
        Assert.Equal(held, SettingsVersion.ForGame(DocWithStates("steam:1", ("a", "1", KeepState.Held), ("b", "2", KeepState.PutBack)), "steam:1"));
        // Everything else is not in the file as Exo's: it leaves the version as if the entry were not there.
        var onlyA = SettingsVersion.ForGame(DocWithStates("steam:1", ("a", "1", KeepState.Held)), "steam:1");
        foreach (var state in new[] { KeepState.Yielded, KeepState.Undone, KeepState.Refused, KeepState.Waiting, KeepState.Skipped })
        {
            var version = SettingsVersion.ForGame(DocWithStates("steam:1", ("a", "1", KeepState.Held), ("b", "2", state)), "steam:1");
            Assert.NotEqual(held, version);
            Assert.Equal(onlyA, version);
        }
    }

    [Fact]
    public void TheSettingsVersionChangesWhenAKeptValueChanges()
    {
        var before = SettingsVersion.ForGame(DocWith("steam:730", ("video.cfg|fps_max", "0"), ("video.cfg|vsync", "0")), "steam:730");
        var after = SettingsVersion.ForGame(DocWith("steam:730", ("video.cfg|fps_max", "144"), ("video.cfg|vsync", "0")), "steam:730");
        var moreLines = SettingsVersion.ForGame(DocWith("steam:730", ("video.cfg|fps_max", "0"), ("video.cfg|vsync", "0"), ("video.cfg|shadows", "1")), "steam:730");
        Assert.NotEqual("none", before);
        Assert.NotEqual(before, after);
        Assert.NotEqual(before, moreLines);
        Assert.Matches("^[0-9a-f]{16}$", before);
    }

    [Fact]
    public void TheSettingsVersionIsTheSameForTheSameEntriesInAnyOrder()
    {
        var one = SettingsVersion.ForGame(DocWith("steam:730", ("a", "1"), ("b", "2"), ("c", "3")), "steam:730");
        var other = SettingsVersion.ForGame(DocWith("steam:730", ("c", "3"), ("a", "1"), ("b", "2")), "steam:730");
        Assert.Equal(one, other);
        Assert.Equal(SettingsVersion.Of([("a", "1"), ("b", "2")]), SettingsVersion.Of([("b", "2"), ("a", "1")]));
    }

    [Fact]
    public void ATargetAndAValueDoNotRunTogether()
    {
        Assert.NotEqual(SettingsVersion.Of([("ab", "c")]), SettingsVersion.Of([("a", "bc")]));
    }

    [Fact]
    public void AGameKeepHasNoRecordOfIsNone()
    {
        Assert.Equal("none", SettingsVersion.ForGame(DocWith("steam:730", ("a", "1")), "epic:other"));
    }

    // ---------------------------------------------------------------- the recorder's wiring

    [Fact]
    public void ARecorderNeedsNoOverlay()
    {
        var overlayOff = FpsCounter.Wants(overlaySetting: false, builtInFpsWithNoItems: false, recordsThisGame: true);
        Assert.False(overlayOff.Overlay);
        Assert.True(overlayOff.Recorder);
        Assert.True(overlayOff.Trace);
        // A game with its own FPS counter loses the overlay and still gets the recorder.
        var builtIn = FpsCounter.Wants(overlaySetting: true, builtInFpsWithNoItems: true, recordsThisGame: true);
        Assert.False(builtIn.Overlay);
        Assert.True(builtIn.Trace);
        // Neither wanted: no trace at all.
        Assert.False(FpsCounter.Wants(false, false, false).Trace);
        Assert.True(FpsCounter.Wants(true, false, false).Trace);
    }

    [Fact]
    public void ARiotGameIsNotRecorded()
    {
        Assert.False(SessionRecorderHost.Records(new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot }));
        Assert.True(SessionRecorderHost.Records(new GameEntry { Id = "steam:730", Title = "Counter-Strike 2", Store = StoreKind.Steam }));
    }

    // ---------------------------------------------------------------- the lifecycle

    private sealed class Harness
    {
        internal readonly List<SessionRecord> Saved = [];
        internal readonly SessionRecorder Recorder;
        internal DateTime Clock = Now;
        /// <summary>Whether the store takes a record now; false is a file that cannot be written.</summary>
        internal bool Accepts = true;
        internal Harness() => Recorder = new SessionRecorder(Save);

        /// <summary>The store's own rule: a record replaces the one with the same game, version and start.</summary>
        private bool Save(SessionRecord record)
        {
            if (!Accepts) return false;
            Saved.RemoveAll(other => other.Game == record.Game && other.Version == record.Version && other.StartedUtc == record.StartedUtc);
            Saved.Add(record);
            return true;
        }

        internal void Tick(string? game, bool inFront = true, bool rights = true, string version = "v1", params double[] frames) =>
            Recorder.Tick(new Observation(game, version, inFront, rights, frames, Clock = Clock.AddSeconds(1)));

        /// <summary>Seconds of 10 ms frames, handed over in one-second lumps.</summary>
        internal void Play(string game, int seconds, string version = "v1")
        {
            for (var i = 0; i < seconds; i++) Tick(game, version: version, frames: [.. Many(10, 100)]);
        }
    }

    [Fact]
    public void ASessionUnderFiveMinutesIsNotUsedForASettingsStep()
    {
        var short1 = new Harness();
        short1.Play("steam:1", 299);
        short1.Tick(null);
        var record = Assert.Single(short1.Saved);
        Assert.False(record.Usable);
        Assert.Equal(299, record.Seconds, 3);

        var long1 = new Harness();
        long1.Play("steam:1", 300);
        long1.Tick(null);
        Assert.True(Assert.Single(long1.Saved).Usable);
    }

    [Fact]
    public void MissingRightsMeanNothingIsRecordedAndOneStateSaysSo()
    {
        var run = new Harness();
        for (var i = 0; i < 5; i++) run.Tick("steam:1", rights: false, frames: [.. Many(10, 100)]);
        Assert.Equal(RecorderState.WaitingForRights, run.Recorder.State);
        Assert.True(run.Recorder.NeedsRights);
        run.Recorder.Exit();
        Assert.Empty(run.Saved);
        Assert.Equal(RecorderState.Stopped, run.Recorder.State);

        // The rights come later: it starts then.
        var later = new Harness();
        later.Tick("steam:1", rights: false);
        later.Tick("steam:1", rights: true, frames: [10, 10]);
        Assert.Equal(RecorderState.Recording, later.Recorder.State);
        Assert.False(later.Recorder.NeedsRights);
    }

    [Fact]
    public void ASessionIsSavedWhenTheGameCloses()
    {
        var run = new Harness();
        run.Play("steam:1", 10);
        Assert.Empty(run.Saved);
        run.Tick(null);
        var record = Assert.Single(run.Saved);
        Assert.Equal("steam:1", record.Game);
        Assert.Equal("v1", record.Version);
        Assert.Equal(1000, record.Frames);
        Assert.Equal(10, record.Seconds, 3);
        Assert.Equal(100, record.AverageFps, 3);
        Assert.Equal(RecorderState.Idle, run.Recorder.State);
    }

    [Fact]
    public void ASessionIsSavedWhenExoExitsMidSession()
    {
        var run = new Harness();
        run.Play("steam:1", 10);
        run.Recorder.Exit();
        var record = Assert.Single(run.Saved);
        Assert.Equal(1000, record.Frames);
        Assert.Equal(RecorderState.Stopped, run.Recorder.State);
        run.Recorder.Exit();
        run.Tick("steam:1", frames: [10, 10]);
        Assert.Single(run.Saved);
        Assert.Equal(RecorderState.Stopped, run.Recorder.State);
    }

    [Fact]
    public void ASessionIsSavedWhenExoExitsWhileTheGameIsNotInFront()
    {
        var run = new Harness();
        run.Play("steam:1", 5);
        run.Tick("steam:1", inFront: false);
        Assert.Equal(RecorderState.Paused, run.Recorder.State);
        run.Recorder.Exit();
        Assert.Equal(500, Assert.Single(run.Saved).Frames);
    }

    [Fact]
    public void AGameNotInFrontPausesAndComingBackGoesOnInTheSameSession()
    {
        var run = new Harness();
        run.Play("steam:1", 5);
        run.Tick("steam:1", inFront: false, frames: [10, 10, 10]);
        Assert.Equal(RecorderState.Paused, run.Recorder.State);
        run.Tick("steam:1", inFront: false);
        run.Play("steam:1", 5);
        Assert.Equal(RecorderState.Recording, run.Recorder.State);
        run.Tick(null);
        var record = Assert.Single(run.Saved);
        Assert.Equal(1000 + 3, record.Frames);
    }

    [Fact]
    public void FramesThatArriveWhileNotInFrontAfterTheFirstPausedLookAreNotCounted()
    {
        var run = new Harness();
        run.Play("steam:1", 1);
        run.Tick("steam:1", inFront: false);
        run.Tick("steam:1", inFront: false, frames: [10, 10, 10, 10]);
        run.Tick(null);
        Assert.Equal(100, Assert.Single(run.Saved).Frames);
    }

    [Fact]
    public void AnotherGameInFrontSavesTheFirstAndStartsTheSecond()
    {
        var run = new Harness();
        run.Play("steam:1", 3);
        run.Tick("steam:2");
        run.Play("steam:2", 3);
        run.Tick(null);
        Assert.Equal(2, run.Saved.Count);
        Assert.Equal("steam:1", run.Saved[0].Game);
        Assert.Equal("steam:2", run.Saved[1].Game);
        Assert.Equal(300, run.Saved[0].Frames);
        Assert.Equal(300, run.Saved[1].Frames);
    }

    [Fact]
    public void AChangedSettingsVersionIsANewSession()
    {
        var run = new Harness();
        run.Play("steam:1", 3, "v1");
        run.Play("steam:1", 3, "v2");
        run.Tick(null);
        Assert.Equal(["v1", "v2"], run.Saved.Select(record => record.Version));
    }

    [Fact]
    public void ASessionWithNoFramesIsNotSaved()
    {
        var run = new Harness();
        run.Tick("steam:1");
        run.Tick("steam:1");
        run.Tick(null);
        Assert.Empty(run.Saved);
        Assert.Equal(RecorderState.Idle, run.Recorder.State);
    }

    [Fact]
    public void ARecorderWhoseGameIsInFrontFromTheStartRecordsWithoutAnyOverlayObject()
    {
        // The recorder is driven by observations alone: nothing of the overlay is in its signature.
        var run = new Harness();
        run.Tick("steam:1", frames: [10, 10, 10]);
        Assert.Equal(RecorderState.Recording, run.Recorder.State);
    }

    // ---------------------------------------------------------------- the state table

    private static readonly RecorderState[] States = Enum.GetValues<RecorderState>();
    private static readonly RecorderEvent[] Events = Enum.GetValues<RecorderEvent>();

    [Fact]
    public void EveryStateAndEventHasARowAndNoStateLacksAnExit()
    {
        foreach (var state in States)
            foreach (var ev in Events)
            {
                var (to, action) = RecorderTable.Step(state, ev);
                Assert.True(Enum.IsDefined(to), $"{state} + {ev}");
                Assert.True(Enum.IsDefined(action), $"{state} + {ev}");
                // Stopped stays stopped and does nothing.
                if (state == RecorderState.Stopped) Assert.Equal((RecorderState.Stopped, RecorderAction.None), (to, action));
                // Exo exiting reaches Stopped from everywhere.
                if (ev == RecorderEvent.Exit) Assert.Equal(RecorderState.Stopped, to);
                // Nothing that was measured is lost on the way out of a session.
                if (state is RecorderState.Recording or RecorderState.Paused && to is not (RecorderState.Recording or RecorderState.Paused))
                    Assert.True(action is RecorderAction.Save or RecorderAction.SaveAndStop, $"{state} + {ev} drops a session");
            }
    }

    [Fact]
    public void EveryStateCanReachIdleAndStoppedWithoutAGameSpecificHelp()
    {
        foreach (var start in States.Where(state => state != RecorderState.Stopped))
        {
            Assert.True(Reaches(start, RecorderState.Stopped), $"{start} cannot stop");
            Assert.True(Reaches(start, RecorderState.Idle), $"{start} cannot return to idle");
        }
        // Every state but Stopped can be reached from Idle: no row is dead.
        foreach (var target in States.Where(state => state != RecorderState.Idle)) Assert.True(Reaches(RecorderState.Idle, target), $"{target} is unreachable");
    }

    [Fact]
    public void TheRowsThatMatterAreExactlyThese()
    {
        Assert.Equal((RecorderState.WaitingForRights, RecorderAction.CheckRights), RecorderTable.Step(RecorderState.Idle, RecorderEvent.GameFront));
        Assert.Equal((RecorderState.Recording, RecorderAction.Begin), RecorderTable.Step(RecorderState.WaitingForRights, RecorderEvent.RightsOk));
        Assert.Equal((RecorderState.WaitingForRights, RecorderAction.None), RecorderTable.Step(RecorderState.WaitingForRights, RecorderEvent.RightsMissing));
        Assert.Equal((RecorderState.Paused, RecorderAction.None), RecorderTable.Step(RecorderState.Recording, RecorderEvent.GameBack));
        Assert.Equal((RecorderState.Recording, RecorderAction.Resume), RecorderTable.Step(RecorderState.Paused, RecorderEvent.GameFront));
        Assert.Equal((RecorderState.Flushing, RecorderAction.Save), RecorderTable.Step(RecorderState.Recording, RecorderEvent.GameClosed));
        Assert.Equal((RecorderState.Flushing, RecorderAction.Save), RecorderTable.Step(RecorderState.Paused, RecorderEvent.SessionBoundary));
        Assert.Equal((RecorderState.Idle, RecorderAction.None), RecorderTable.Step(RecorderState.Flushing, RecorderEvent.FlushDone));
        Assert.Equal((RecorderState.Stopped, RecorderAction.SaveAndStop), RecorderTable.Step(RecorderState.Recording, RecorderEvent.Exit));
        Assert.Equal((RecorderState.Stopped, RecorderAction.SaveAndStop), RecorderTable.Step(RecorderState.Paused, RecorderEvent.Exit));
    }

    private static bool Reaches(RecorderState from, RecorderState target)
    {
        var seen = new HashSet<RecorderState> { from };
        var queue = new Queue<RecorderState>([from]);
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            if (state == target && state != from) return true;
            foreach (var ev in Events)
            {
                if (target != RecorderState.Stopped && ev == RecorderEvent.Exit) continue;
                var (to, _) = RecorderTable.Step(state, ev);
                if (to == target) return true;
                if (seen.Add(to)) queue.Enqueue(to);
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- the store

    private static SessionRecord Session(string game, string version, DateTime at, double seconds = 600) =>
        new(game, version, at, seconds, 60000, 10, 100, 80, 60, 2, [.. Enumerable.Range(0, 50).Select(i => 1000 + i * 37)], seconds >= 300);

    [Fact]
    public void OldSessionsArePruned()
    {
        var sessions = new List<SessionRecord>();
        for (var i = 0; i < 25; i++) sessions.Add(Session("steam:1", "v1", Now.AddHours(-i)));
        for (var i = 0; i < 5; i++) sessions.Add(Session("steam:1", "v2", Now.AddHours(-i)));
        sessions.Add(Session("steam:2", "none", Now.AddDays(-91)));
        sessions.Add(Session("steam:2", "none", Now.AddDays(-89)));
        var kept = SessionStore.Prune(sessions, Now);
        Assert.Equal(20, kept.Count(s => s.Game == "steam:1" && s.Version == "v1"));
        Assert.Equal(5, kept.Count(s => s.Version == "v2"));
        Assert.Single(kept, s => s.Game == "steam:2");
        Assert.Equal(Now.AddDays(-89), kept.Single(s => s.Game == "steam:2").StartedUtc);
        // The ones kept for a version are its newest.
        Assert.Equal(Now.AddHours(-19), kept.Where(s => s.Version == "v1").Min(s => s.StartedUtc));
    }

    [Fact]
    public void TheFileStaysSmall()
    {
        var path = Path.Combine(Path.GetTempPath(), "recorder-" + Guid.NewGuid().ToString("N"), "session-recorder.json");
        try
        {
            var at = Now.AddDays(-80);
            for (var i = 0; i < 1500; i++)
            {
                SessionStore.Add(path, Session("steam:" + i % 7, "v" + i % 40, at.AddMinutes(i * 5)), Now);
            }
            var size = new FileInfo(path).Length;
            Assert.True(size <= 256 * 1024, $"the file is {size} bytes");
            var sessions = SessionStore.Load(path);
            Assert.True(sessions.Count <= SessionStore.MaxSessions);
            // One session alone stays well under 2 KB.
            var one = Path.Combine(Path.GetDirectoryName(path)!, "one.json");
            SessionStore.Add(one, Session("steam:1", "v1", Now), Now);
            Assert.True(new FileInfo(one).Length < 2048);
        }
        finally { try { DeletionGate.DeleteDirectory(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void ASavedSessionReadsBackAsItWas()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recorder-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "session-recorder.json");
        try
        {
            var record = Session("steam:1", "abc", Now.AddMinutes(-30));
            SessionStore.Add(path, record, Now);
            var back = Assert.Single(SessionStore.Load(path));
            Assert.Equal(record.Game, back.Game);
            Assert.Equal(record.StartedUtc, back.StartedUtc);
            Assert.Equal(record.Histogram, back.Histogram);
            Assert.Equal(record.Low1Fps, back.Low1Fps);
            Assert.Equal(record.Usable, back.Usable);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void AFileAnotherProgramHoldsIsNotReplaced()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recorder-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "session-recorder.json");
        try
        {
            Assert.True(SessionStore.Add(path, Session("steam:1", "v", Now.AddHours(-2)), Now));
            var before = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write | FileShare.Delete))
            {
                Assert.False(SessionStore.Add(path, Session("steam:1", "v", Now.AddHours(-1)), Now), "the session is not kept and the caller is told");
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bad"));
            // Once the file is free the next save keeps both.
            Assert.True(SessionStore.Add(path, Session("steam:1", "v", Now.AddHours(-1)), Now));
            Assert.Equal(2, SessionStore.Load(path).Count);
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void AFileThatWasReadAndIsDamagedIsSetAsideAndTheNextSessionIsKept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session-recorder.json");
        try
        {
            Assert.Empty(SessionStore.Load(path));
            File.WriteAllText(path, "{ not json");
            Assert.Empty(SessionStore.Load(path));
            Assert.True(File.Exists(path), "reading alone sets nothing aside");
            Assert.True(SessionStore.Add(path, Session("steam:1", "v", Now), Now));
            Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
            Assert.Single(SessionStore.Load(path));
            // An empty file is damaged too.
            File.WriteAllText(path, "");
            Assert.True(SessionStore.Add(path, Session("steam:2", "v", Now), Now));
            Assert.Single(SessionStore.Load(path));
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- the frames from the trace

    private const long Frequency = 10_000_000;
    private static long Ms(double ms) => (long)(ms * Frequency / 1000);

    [Fact]
    public void TheWindowDrainsEveryFrameGapOnceAndKeepsTheLastAcrossDrains()
    {
        var window = new FpsWindow(Frequency);
        var gaps = new List<double>();
        window.Add(Ms(1000));
        window.Add(Ms(1010));
        window.Add(Ms(1030));
        Assert.Equal(0, window.Drain(gaps));
        // The first frame has no frame before it.
        Assert.Equal([10.0, 20.0], gaps);
        gaps.Clear();
        window.Add(Ms(1040));
        window.Drain(gaps);
        Assert.Equal([10.0], gaps);
        gaps.Clear();
        window.Drain(gaps);
        Assert.Empty(gaps);
    }

    [Fact]
    public void AnOverrunRingSkipsToTheNewestAndSaysHowManyWereLost()
    {
        var window = new FpsWindow(Frequency);
        var gaps = new List<double>();
        for (var i = 0; i < FpsWindow.Capacity * 3; i++) window.Add(Ms(i * 5.0));
        var lost = window.Drain(gaps);
        Assert.True(lost > 0);
        Assert.True(gaps.Count > 0 && gaps.Count < FpsWindow.Capacity);
        Assert.Equal(FpsWindow.Capacity * 3, lost + gaps.Count + 1);
        Assert.All(gaps, gap => Assert.Equal(5.0, gap, 3));
    }

    [Fact]
    public void TheSlotsDrainTheGameInFrontAndNotAnotherProcess()
    {
        var slots = new PresentSlots(Frequency);
        slots.Watch([10, 20]);
        for (var i = 0; i < 5; i++) slots.Record(10, PresentKind.Api, Ms(1000 + i * 8.0));
        for (var i = 0; i < 3; i++) slots.Record(20, PresentKind.Api, Ms(1000 + i * 16.0));
        slots.Record(99, PresentKind.Api, Ms(1000));
        var gaps = new List<double>();
        slots.Drain(10, gaps);
        Assert.Equal([8.0, 8.0, 8.0, 8.0], gaps);
        gaps.Clear();
        slots.Drain(10, gaps);
        Assert.Empty(gaps);
    }

    // ---------------------------------------------------------------- the recorder's own cost

    [Fact]
    public void TheStatisticsPathHandlesALongSessionAndItsCostIsMeasured()
    {
        // Two hours at 240 FPS, a stall now and then. This is the cost of the statistics alone, in this process, on this PC:
        // it is not a game's CPU share and says nothing about what the trace costs.
        const int frames = 2 * 3600 * 240;
        var random = new Random(7);
        var times = new double[frames];
        for (var i = 0; i < frames; i++) times[i] = i % 5000 == 0 ? 80 : 3 + random.NextDouble() * 2.5;
        var started = Stopwatch.GetTimestamp();
        var fresh = new FrameStats.Accumulator();
        foreach (var ms in times) fresh.Add(ms);
        var result = fresh.Snapshot();
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Equal(frames, result.Frames);
        var perFrameNs = elapsed.TotalMilliseconds * 1e6 / frames;
        // The recorder hands over a second's frames (240 here) once a second: the CPU share of one core the adds would use.
        var share = perFrameNs * 240 / 1e9 * 100;
        var line = string.Create(CultureInfo.InvariantCulture, $"{frames} frames in {elapsed.TotalMilliseconds:0.0} ms: {perFrameNs:0.0} ns a frame; at 240 FPS about {share:0.0000}% of one core for the adds");
        if (Environment.GetEnvironmentVariable("EXO_RECORDER_COST_OUT") is { Length: > 0 } outPath) File.WriteAllText(outPath, line);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), line);
    }

    // ---------------------------------------------------------------- the host: a rebuilt page, the kernel's events, the decision

    private sealed class FakeTrace : IFrameTrace
    {
        internal readonly List<(int Who, bool On)> Runs = [];
        internal List<double> Next = [];
        public void Run(int who, bool on) => Runs.Add((who, on));
        public int DrainFrames(int front, List<double> gapsMs)
        {
            gapsMs.AddRange(Next);
            Next = [];
            return 0;
        }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    private static readonly GameEntry Cs2 = new() { Id = "steam:730", Title = "Counter-Strike 2", Store = StoreKind.Steam };

    private static SessionRecorderHost HostWith(List<SessionRecord> saved, FakeTrace trace, out Action<int> play)
    {
        var clock = Now;
        var host = new SessionRecorderHost(record => { saved.Add(record); return true; }, () => 10, _ => "v1", () => clock = clock.AddSeconds(1), look => new Nothing());
        play = seconds =>
        {
            for (var i = 0; i < seconds; i++)
            {
                trace.Next = [.. Many(10, 100)];
                host.Look();
            }
        };
        return host;
    }

    [Fact]
    public void AWebPageRebuildEndsTheSessionAndTheNextFollowRecordsAgain()
    {
        var saved = new List<SessionRecord>();
        var trace = new FakeTrace();
        var host = HostWith(saved, trace, out var play);
        var before = SessionRecorderHost.Shared;
        SessionRecorderHost.Shared = host;
        try
        {
            host.Follow(trace, Cs2, [10]);
            play(5);
            // The bridge is detached (the page is rebuilt): the counter stops its trace and the session is saved.
            FpsCounter.Stop();
            Assert.Single(saved);
            Assert.NotEqual(RecorderState.Stopped, host.State);
            // The new bridge polls again: the game is followed and recorded.
            host.Follow(trace, Cs2, [10]);
            play(5);
            FpsCounter.Stop();
            Assert.Equal(2, saved.Count);
            Assert.All(saved, record => Assert.Equal(500, record.Frames));
        }
        finally { SessionRecorderHost.Shared = before; }
    }

    [Fact]
    public void ExosRealShutdownStopsTheRecorderForGood()
    {
        var saved = new List<SessionRecord>();
        var trace = new FakeTrace();
        var host = HostWith(saved, trace, out var play);
        var before = SessionRecorderHost.Shared;
        SessionRecorderHost.Shared = host;
        try
        {
            host.Follow(trace, Cs2, [10]);
            play(5);
            FpsCounter.Shutdown();
            Assert.Single(saved);
            Assert.Equal(RecorderState.Stopped, host.State);
            host.Follow(trace, Cs2, [10]);
            play(5);
            Assert.Single(saved);
        }
        finally { SessionRecorderHost.Shared = before; }
    }

    [Fact]
    public void TheRecordersReadAsksForTheKernelsEventsForAGameThatMakesNoApiPresents()
    {
        var now = 0L;
        var kernel = new List<bool>();
        var trace = PresentTrace.ForTest(() => { }, () => { }, kernel.Add, () => now);
        trace.Watch([10]);
        trace.Run(PresentTrace.ForRecorder, true);
        var gaps = new List<double>();
        // Two seconds in front with no DXGI or D3D9 present (Vulkan, OpenGL): the kernel's events come on, by the recorder's read alone.
        now = 3 * Stopwatch.Frequency;
        trace.DrainFrames(10, gaps);
        Assert.Equal([true], kernel);
        // An API present arrives: a frame needs one report, so the kernel's events go off again.
        trace.RecordForTest(10, PresentKind.Api, now);
        trace.DrainFrames(10, gaps);
        Assert.Equal([true, false], kernel);
    }

    [Fact]
    public void NothingAsksForTheKernelsEventsWhileNobodyWantsFrames()
    {
        var now = 0L;
        var kernel = new List<bool>();
        var trace = PresentTrace.ForTest(() => { }, () => { }, kernel.Add, () => now);
        trace.Watch([10]);
        trace.Run(PresentTrace.ForRecorder, true);
        trace.Run(PresentTrace.ForRecorder, false);
        now = 3 * Stopwatch.Frequency;
        trace.DrainFrames(10, []);
        Assert.Empty(kernel);
    }

    [Fact]
    public void TheHostsLookDrivesTheTraceThroughTheRecordersWish()
    {
        var saved = new List<SessionRecord>();
        var trace = new FakeTrace();
        var host = HostWith(saved, trace, out var play);
        host.Follow(trace, Cs2, [10]);
        play(2);
        Assert.Contains((PresentTrace.ForRecorder, true), trace.Runs);
    }

    [Fact]
    public void TheRecordingDecisionUsesOnlyTheGamesIdentityAndTheRiotRule()
    {
        // An anti-cheat game is recorded: only Windows' own present events are read.
        var folder = Path.Combine(Path.GetTempPath(), "recorder-ac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "EasyAntiCheat"));
        File.WriteAllText(Path.Combine(folder, "EasyAntiCheat", "EasyAntiCheat_EOS.exe"), "x");
        var game = new GameEntry { Id = "epic:fn", Title = "A game", Store = StoreKind.Epic, Path = Path.Combine(folder, "Game.exe") };
        try
        {
            Assert.True(SessionRecorderHost.Records(game));
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
        // The folder is gone now: the answer is the same, because the folder was never part of it.
        Assert.True(SessionRecorderHost.Records(game));
        Assert.True(SessionRecorderHost.Records(new GameEntry { Id = "epic:fn", Title = "A game", Store = StoreKind.Epic }));
        // Riot's games are never recorded: by store, and by where they are installed.
        Assert.False(SessionRecorderHost.Records(new GameEntry { Id = "riot:valorant", Title = "V", Store = StoreKind.Riot }));
        Assert.False(SessionRecorderHost.Records(new GameEntry { Id = "local:x", Title = "L", Store = StoreKind.Local, Path = @"C:\Riot Games\League of Legends\Game\x.exe" }));
    }

    // ---------------------------------------------------------------- checkpoints and a save that fails

    [Fact]
    public void ASessionInterruptedAfterTwelveMinutesLeavesAtLeastTenMinutes()
    {
        var run = new Harness();
        run.Play("steam:1", 12 * 60);
        // Nothing closed it: no game close, no exit.
        var record = Assert.Single(run.Saved);
        Assert.True(record.Seconds >= 600, $"the record holds {record.Seconds} s");
        Assert.True(record.Usable);
    }

    [Fact]
    public void AFailedSaveIsHeldAndTriedAgainUntilItSucceeds()
    {
        var run = new Harness { Accepts = false };
        run.Play("steam:1", 10);
        run.Tick(null);
        Assert.Empty(run.Saved);
        run.Tick(null);
        Assert.Empty(run.Saved);
        run.Accepts = true;
        run.Tick(null);
        var record = Assert.Single(run.Saved);
        Assert.Equal(1000, record.Frames);
        // Kept once: later looks do not keep it again.
        run.Tick(null);
        Assert.Single(run.Saved);
    }

    [Fact]
    public void AFailedCheckpointIsTriedAgainAtTheNextLook()
    {
        var run = new Harness { Accepts = false };
        run.Play("steam:1", 301);
        Assert.Empty(run.Saved);
        run.Accepts = true;
        run.Tick("steam:1", frames: [10]);
        Assert.Single(run.Saved);
    }

    [Fact]
    public void CheckpointsDoNotAddRecords()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recorder-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "session-recorder.json");
        try
        {
            var recorder = new SessionRecorder(record => SessionStore.Add(path, record, Now.AddHours(1)));
            var clock = Now;
            for (var i = 0; i < 20 * 60; i++)
            {
                clock = clock.AddSeconds(1);
                recorder.Tick(new Observation("steam:1", "v1", true, true, Many(10, 100), clock));
                if (i % 300 == 299) Assert.Single(SessionStore.Load(path));
            }
            recorder.Tick(new Observation(null, "v1", false, true, [], clock.AddSeconds(1)));
            var only = Assert.Single(SessionStore.Load(path));
            Assert.Equal(1200, only.Seconds, 3);
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }
}
