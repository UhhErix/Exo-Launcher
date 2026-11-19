using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways putting Exo's driver settings back after a driver change could go wrong silently:
/// - a driver installed outside Exo (Windows Update, the vendor's app, by hand) resets Exo's global profile and each game's,
///   and nothing puts them back;
/// - the driver's old list of setting ids is kept, so settings the new driver lists are never written;
/// - something is written into a running install or under a running game;
/// - a put back that a fresh read says did not take is recorded as held, so it is never tried again;
/// - a driver that refuses for ever is tried at every look for ever, or the giving up is said every time;
/// - Exo's own install puts its settings back, and a look then writes them a second time;
/// - an old record with no PC section is read as "this driver is the one Exo's settings are held under", or as unreadable.
/// </summary>
public sealed class DriverKeepTests
{
    private const string Old = "32.0.15.6094|31.0.101.4502";
    private const string New = "32.0.15.7270|31.0.101.4502";

    /// <summary>Every function the class takes, faked, with what it was asked and what the record holds.</summary>
    private sealed class Rig
    {
        internal string? Identity { get; set; }
        internal bool Installing { get; set; }
        internal bool GameRunning { get; set; }
        internal bool HasGlobal { get; set; }
        internal int Games { get; set; }
        internal bool GlobalTakes { get; set; } = true;
        internal int GamesAnswer { get; set; }
        /// <summary>The record cannot be saved (locked, or written by a newer Exo): a save changes nothing and answers false.</summary>
        internal bool SaveFails { get; set; }
        /// <summary>The time now; a look moves nothing but the test does.</summary>
        internal DateTime Now { get; set; } = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        /// <summary>Called inside the global put back, before it answers.</summary>
        internal Action? InGlobal { get; set; }

        internal Keep.KeepPc Pc { get; } = new();
        internal int Saves { get; private set; }
        internal int Refreshes { get; private set; }
        internal int PutBacks { get; private set; }
        internal List<string> Written { get; } = [];
        /// <summary>Every call into the driver, in order: "reload", "global", "games".</summary>
        internal List<string> Order { get; } = [];
        internal List<string> Log { get; } = [];

        private DriverKeep? _keep;

        internal DriverKeep Keep => _keep ??= new(
            () => Identity,
            () => Installing,
            () => GameRunning,
            () => HasGlobal,
            () => Games,
            () => { lock (Order) { PutBacks++; Written.Add("global"); Order.Add("global"); } InGlobal?.Invoke(); return GlobalTakes; },
            () => { PutBacks++; Written.Add("games"); Order.Add("games"); return GamesAnswer; },
            () => Refreshes++,
            () => Order.Add("reload"),
            () => Pc,
            save: change => { Saves++; if (SaveFails) return false; change(Pc); return true; },
            log: line => { lock (Log) Log.Add(line); },
            now: () => Now);

        /// <summary>A driver install by anyone: the identity changes, the first look only sees it, and two minutes pass (the log and the counts start over).</summary>
        internal void Change(string to)
        {
            Identity = to;
            Assert.Equal(DriverOutcome.Pending, Keep.Look("sighting"));
            Now = Now.AddMinutes(2);
            Log.Clear();
            Saves = 0;
        }
    }

    /// <summary>A PC Exo's settings were held under the old driver, with Exo's global profile applied.</summary>
    private static Rig Held() => new() { Identity = Old, Pc = { Driver = Old }, HasGlobal = true };

    [Fact]
    public void ADriverInstalledOutsideExoPutsTheProfileBack()
    {
        var rig = Held();
        rig.Change(New);

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(DriverOutcome.PutBack, outcome);
        Assert.Contains("global", rig.Written);
        Assert.Equal(New, rig.Pc.Driver);
        Assert.Equal(New, rig.Pc.DriverPutBack);
        Assert.NotNull(rig.Pc.DriverPutBackAt);
        Assert.Single(rig.Log);
        Assert.Contains(Old, rig.Log[0], StringComparison.Ordinal);
        Assert.Contains(New, rig.Log[0], StringComparison.Ordinal);
        Assert.Contains("put back Exo's GPU profile", rig.Log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePerGameProfilesComeBackToo()
    {
        var rig = Held();
        rig.Change(New);
        rig.HasGlobal = false;
        rig.Games = 2;
        rig.GamesAnswer = 2;

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(DriverOutcome.PutBack, outcome);
        Assert.DoesNotContain("global", rig.Written);
        Assert.Contains("games", rig.Written);
        Assert.Equal(New, rig.Pc.Driver);
        Assert.Contains("2 games' NVIDIA profiles", rig.Log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsWrittenDuringAnInstall()
    {
        var rig = Held();
        rig.Identity = New;
        rig.Installing = true;

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(DriverOutcome.Waiting, outcome);
        Assert.Empty(rig.Written);
        Assert.Equal(0, rig.Saves);
        Assert.Equal(Old, rig.Pc.Driver);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void NothingIsWrittenWhileAGameRuns()
    {
        var rig = Held();
        rig.Identity = New;
        rig.GameRunning = true;

        var outcome = rig.Keep.Look("every ten minutes");

        Assert.Equal(DriverOutcome.Waiting, outcome);
        Assert.Empty(rig.Written);
        Assert.Equal(0, rig.Saves);
        Assert.Equal(Old, rig.Pc.Driver);
    }

    [Fact]
    public void TheFirstLookOnlyNotesTheDriver()
    {
        var rig = Held();
        rig.Pc.Driver = null;

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(DriverOutcome.Noted, outcome);
        Assert.Empty(rig.Written);
        Assert.Equal(Old, rig.Pc.Driver);
    }

    [Fact]
    public void TheSameDriverWritesNothing()
    {
        var rig = Held();

        var outcome = rig.Keep.Look("every ten minutes");

        Assert.Equal(DriverOutcome.Same, outcome);
        Assert.Empty(rig.Written);
        Assert.Equal(0, rig.Saves);
        Assert.Equal(0, rig.Refreshes);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void ANewDriverWithNothingOfExosOnlyNotesIt()
    {
        var rig = Held();
        rig.Change(New);
        rig.HasGlobal = false;
        rig.Games = 0;

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(DriverOutcome.Noted, outcome);
        Assert.Empty(rig.Written);
        Assert.Equal(New, rig.Pc.Driver);
        Assert.Null(rig.Pc.DriverPutBack);
    }

    [Fact]
    public void TheIdListIsForgottenWhenTheDriverChanges()
    {
        var rig = Held();
        Assert.Equal(DriverOutcome.Same, rig.Keep.Look("on start"));
        Assert.Equal(0, rig.Refreshes);

        rig.Change(New);
        Assert.Equal(DriverOutcome.PutBack, rig.Keep.Look("on start"));
        Assert.Equal(1, rig.Refreshes);

        // Nothing of Exo's to put back is still a driver change: the id list goes.
        var quiet = Held();
        quiet.Change(New);
        quiet.HasGlobal = false;
        Assert.Equal(DriverOutcome.Noted, quiet.Keep.Look("on start"));
        Assert.Equal(1, quiet.Refreshes);
    }

    [Fact]
    public void APutBackThatDidNotTakeIsTriedAgainAtTheNextLook()
    {
        var rig = Held();
        rig.Change(New);
        rig.GlobalTakes = false;

        Assert.Equal(DriverOutcome.Failed, rig.Keep.Look("on start"));
        // Nothing is recorded, so the next look sees the same new driver and writes again.
        Assert.Null(rig.Pc.DriverPutBack);
        Assert.Single(rig.Log);
        Assert.DoesNotContain("stopped", rig.Log[0], StringComparison.Ordinal);

        rig.GlobalTakes = true;
        Assert.Equal(DriverOutcome.PutBack, rig.Keep.Look("every ten minutes"));
        Assert.Equal(2, rig.PutBacks);
        Assert.Equal(New, rig.Pc.Driver);
    }

    [Fact]
    public void AfterThreeFailedLooksExoStopsAndSaysSoOnce()
    {
        var rig = Held();
        rig.Change(New);
        rig.GlobalTakes = false;

        Assert.Equal(DriverOutcome.Failed, rig.Keep.Look("on start"));
        Assert.Equal(DriverOutcome.Failed, rig.Keep.Look("every ten minutes"));
        Assert.Equal(DriverOutcome.GaveUp, rig.Keep.Look("every ten minutes"));
        Assert.Equal(New, rig.Pc.Driver);

        var stops = rig.Log.Count(line => line.Contains("stops trying", StringComparison.Ordinal));
        Assert.Equal(1, stops);
        // Recorded, so nothing is tried again and nothing more is said.
        Assert.Equal(DriverOutcome.Same, rig.Keep.Look("every ten minutes"));
        Assert.Equal(3, rig.PutBacks);
        Assert.Equal(1, rig.Log.Count(line => line.Contains("stops trying", StringComparison.Ordinal)));

        // A different driver starts the count again.
        rig.Change("33.0.15.1");
        rig.GlobalTakes = true;
        Assert.Equal(DriverOutcome.PutBack, rig.Keep.Look("on start"));
        Assert.Equal(0, rig.Pc.DriverTries);
    }

    [Fact]
    public void ExosOwnInstallIsNotPutBackTwice()
    {
        var rig = Held();
        rig.Identity = New;

        rig.Keep.NoteExosOwnRestore(allDone: true);
        var outcome = rig.Keep.Look("on start");

        Assert.Equal(DriverOutcome.Same, outcome);
        Assert.Empty(rig.Written);
        Assert.Equal(0, rig.PutBacks);
        Assert.Equal(New, rig.Pc.Driver);
        Assert.Equal(New, rig.Pc.DriverPutBack);

        // Every look after it writes nothing either, told which driver it is by the record alone.
        Assert.Equal(DriverOutcome.Same, rig.Keep.Look("every ten minutes"));
        Assert.Empty(rig.Written);

        // And the watch never looks at a step of Exo's own install, from its "installing" step to its final "done" or "failed"
        // after restoring: a "done" before the "restoring" step (the installer's progress marker, carrying no outcome) is
        // Exo's too, so no look races Exo's own restore. A "done" with no install of Exo's before it is anyone's.
        var own = false;
        Assert.True(DriverKeepWatch.IsExosOwn(new("installing", "621.14", "Installing the driver"), ref own));
        Assert.True(DriverKeepWatch.IsExosOwn(new("reloading", "621.14", "Loading the new driver"), ref own));
        Assert.True(DriverKeepWatch.IsExosOwn(new("done", "621.14", "Driver 621.14 installed"), ref own));
        Assert.True(DriverKeepWatch.IsExosOwn(new("restoring", "621.14", "Putting your settings back"), ref own));
        Assert.True(DriverKeepWatch.IsExosOwn(new("done", "621.14", "Driver 621.14 installed", ["Exo's GPU profile"], []), ref own));
        // The final "done" with its outcome ends the path: a later "done" is not Exo's own.
        Assert.False(own);
        Assert.False(DriverKeepWatch.IsExosOwn(new("done", "621.14", "Driver 621.14 installed"), ref own));
        var outside = false;
        Assert.False(DriverKeepWatch.IsExosOwn(new("done", "621.14", "Driver 621.14 installed"), ref outside));
        // A failure ends the path it was in, and is never a driver to look at.
        own = true;
        Assert.True(DriverKeepWatch.IsExosOwn(new("failed", "621.14", "Windows didn't get permission, so nothing changed"), ref own));
        Assert.False(own);
        // An install that waits for a game or a call (Exo's own) is not looked at either.
        Assert.True(DriverKeepWatch.IsExosOwn(new("waiting", "621.14", "A game is running"), ref own));
        var someoneElses = new DriverInstallSignal.Step("done", "621.14", "Installed by Windows Update", [], []);
        Assert.False(DriverKeepWatch.IsExosOwn(someoneElses, ref outside));
    }

    [Fact]
    public void ARecordWithNoPcSectionLoads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "keepdrv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "keep.json");
        File.WriteAllText(path, """{"version":1,"games":{}}""");
        using var forced = Keep.ForcePathForTest(path);

        var old = Keep.Load();
        Assert.NotNull(old.Pc);
        Assert.Null(old.Pc.Driver);
        Assert.Null(old.Pc.DriverPutBack);

        Assert.True(Keep.Update(doc => doc.Pc.Driver = New));
        var again = Keep.Load();
        Assert.Equal(New, again.Pc.Driver);
        Assert.Null(again.Pc.DriverPutBack);
    }

    [Fact]
    public void ThePcSaysPutBackAfterTheDriverUpdateUntilAGameRuns()
    {
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var pc = new Keep.KeepPc { Driver = New, DriverPutBack = New, DriverPutBackAt = at };

        // The driver Exo put them back under is the one installed, and no game has run since.
        Assert.True(DriverKeep.PutBackShows(pc, New, lastGameEnded: null, at.AddMinutes(1)));
        Assert.True(DriverKeep.PutBackShows(pc, New, lastGameEnded: at.AddMinutes(-5), at.AddMinutes(1)));
        // A game has run since.
        Assert.False(DriverKeep.PutBackShows(pc, New, lastGameEnded: at.AddMinutes(5), at.AddHours(1)));
        // Another driver since.
        Assert.False(DriverKeep.PutBackShows(pc, "33.0.15.1", null, at.AddMinutes(1)));
        // Nothing put back, or nothing recorded about it.
        Assert.False(DriverKeep.PutBackShows(new Keep.KeepPc { Driver = New, DriverPutBack = null, DriverPutBackAt = at }, New, null, at));
        Assert.False(DriverKeep.PutBackShows(new Keep.KeepPc { Driver = New, DriverPutBack = New, DriverPutBackAt = null }, New, null, at));
        // No last session time at all, and the clock set back before the put back: a day, then no more.
        Assert.True(DriverKeep.PutBackShows(pc, New, null, at.AddHours(23)));
        Assert.False(DriverKeep.PutBackShows(pc, New, null, at.AddDays(2)));
        Assert.False(DriverKeep.PutBackShows(pc, New, null, at.AddSeconds(-1)));
        // The identity cannot be read now: not said.
        Assert.False(DriverKeep.PutBackShows(pc, null, null, at.AddMinutes(1)));
    }

    [Fact]
    public void ANewDriverIsActedOnOnlyAfterItHasBeenStable()
    {
        var rig = Held();
        rig.Identity = New;

        // The first look only sees it: the identity and the time go in the record, and nothing is written.
        Assert.Equal(DriverOutcome.Pending, rig.Keep.Look("on start"));
        var first = rig.Now;
        Assert.Empty(rig.Written);
        Assert.Equal(Old, rig.Pc.Driver);
        Assert.Equal(New, rig.Pc.DriverSeen);
        Assert.Equal(first, rig.Pc.DriverSeenAt);

        // A look a minute on is too soon: still nothing, and the first sighting's time is kept.
        rig.Now = first.AddMinutes(1);
        Assert.Equal(DriverOutcome.Pending, rig.Keep.Look("when the driver install finished"));
        Assert.Empty(rig.Written);
        Assert.Equal(first, rig.Pc.DriverSeenAt);

        // It goes back to the recorded one (an installer that was rolled back): the sighting is forgotten...
        rig.Identity = Old;
        Assert.Equal(DriverOutcome.Same, rig.Keep.Look("every ten minutes"));
        Assert.Null(rig.Pc.DriverSeen);
        Assert.Null(rig.Pc.DriverSeenAt);

        // ...so a long time later the same new identity starts its two minutes over instead of being acted on at once.
        rig.Now = first.AddHours(1);
        rig.Identity = New;
        Assert.Equal(DriverOutcome.Pending, rig.Keep.Look("every ten minutes"));
        Assert.Empty(rig.Written);

        // Two minutes after that sighting it is acted on, once, and the sighting goes with the put back.
        rig.Now = first.AddHours(1).AddMinutes(2);
        Assert.Equal(DriverOutcome.PutBack, rig.Keep.Look("every ten minutes"));
        Assert.Equal(["global"], rig.Written);
        Assert.Equal(New, rig.Pc.Driver);
        Assert.Null(rig.Pc.DriverSeen);

        // Another identity while one waits starts the clock again for that one.
        var other = Held();
        other.Identity = New;
        other.Keep.Look("on start");
        other.Now = other.Now.AddMinutes(5);
        other.Identity = "33.0.15.1";
        Assert.Equal(DriverOutcome.Pending, other.Keep.Look("every ten minutes"));
        Assert.Equal("33.0.15.1", other.Pc.DriverSeen);
        Assert.Empty(other.Written);
    }

    [Fact]
    public void TheWatchLooksEveryTwoMinutesWhileAChangeWaitsAndEveryTenOtherwise()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), DriverKeepWatch.NextLook(DriverOutcome.Pending));
        foreach (var outcome in Enum.GetValues<DriverOutcome>().Where(outcome => outcome != DriverOutcome.Pending))
            Assert.Equal(TimeSpan.FromMinutes(10), DriverKeepWatch.NextLook(outcome));
        Assert.Equal(TimeSpan.FromMinutes(10), DriverKeepWatch.NextLook(null));
    }

    [Fact]
    public void NvapiIsReloadedBeforeAPutBack()
    {
        var rig = Held();
        rig.HasGlobal = true;
        rig.Games = 1;
        rig.GamesAnswer = 1;
        rig.Identity = New;

        // A look that only waits, or finds nothing changed, never reloads.
        Assert.Equal(DriverOutcome.Pending, rig.Keep.Look("on start"));
        Assert.Empty(rig.Order);
        rig.Now = rig.Now.AddMinutes(2);

        Assert.Equal(DriverOutcome.PutBack, rig.Keep.Look("every ten minutes"));

        // NvAPI points at the driver that was removed until it is loaded again: that comes first, then both put backs.
        Assert.Equal(["reload", "global", "games"], rig.Order);
        Assert.Equal(DriverOutcome.Same, rig.Keep.Look("every ten minutes"));
        Assert.Equal(["reload", "global", "games"], rig.Order);

        // Every try after a put back that did not take loads it again too.
        var failing = Held();
        failing.GlobalTakes = false;
        failing.Change(New);
        Assert.Equal(DriverOutcome.Failed, failing.Keep.Look("every ten minutes"));
        Assert.Equal(DriverOutcome.Failed, failing.Keep.Look("every ten minutes"));
        Assert.Equal(["reload", "global", "reload", "global"], failing.Order);
    }

    [Fact]
    public void APutBackThatCannotBeRecordedIsNotRepeated()
    {
        var rig = Held();
        rig.SaveFails = true;
        rig.Identity = New;
        Assert.Equal(DriverOutcome.Pending, rig.Keep.Look("on start"));
        rig.Now = rig.Now.AddMinutes(2);

        // The put back took, and the record cannot say so.
        Assert.Equal(DriverOutcome.PutBack, rig.Keep.Look("every ten minutes"));
        Assert.Equal(["global"], rig.Written);
        Assert.Equal(Old, rig.Pc.Driver);

        // Every look after it knows, from this run's memory, that this identity was put back: nothing more is written, nothing more is said.
        for (var look = 0; look < 3; look++)
        {
            rig.Now = rig.Now.AddMinutes(10);
            Assert.Equal(DriverOutcome.Same, rig.Keep.Look("every ten minutes"));
        }
        Assert.Equal(["global"], rig.Written);
        Assert.Equal(1, rig.Log.Count(line => line.Contains("couldn't be recorded", StringComparison.Ordinal)));

        // A put back that never takes, with a record that cannot be saved, still stops after three looks, and says so once.
        var stuck = Held();
        stuck.SaveFails = true;
        stuck.GlobalTakes = false;
        stuck.Identity = New;
        Assert.Equal(DriverOutcome.Pending, stuck.Keep.Look("on start"));
        stuck.Now = stuck.Now.AddMinutes(2);
        Assert.Equal(DriverOutcome.Failed, stuck.Keep.Look("every ten minutes"));
        Assert.Equal(DriverOutcome.Failed, stuck.Keep.Look("every ten minutes"));
        Assert.Equal(DriverOutcome.GaveUp, stuck.Keep.Look("every ten minutes"));
        Assert.Equal(DriverOutcome.Same, stuck.Keep.Look("every ten minutes"));
        Assert.Equal(3, stuck.PutBacks);
        Assert.Equal(1, stuck.Log.Count(line => line.Contains("stops trying", StringComparison.Ordinal)));

        // A different driver is a different identity: tried again.
        stuck.GlobalTakes = true;
        stuck.Identity = "33.0.15.1";
        Assert.Equal(DriverOutcome.Pending, stuck.Keep.Look("every ten minutes"));
        stuck.Now = stuck.Now.AddMinutes(2);
        Assert.Equal(DriverOutcome.PutBack, stuck.Keep.Look("every ten minutes"));
    }

    [Fact]
    public async Task TwoLooksAtOnceWriteOnce()
    {
        var rig = Held();
        rig.Change(New);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.InGlobal = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3));
        };
        var keep = rig.Keep;

        // The timer's look is inside the put back when the install's own "done" look arrives.
        var timer = Task.Run(() => keep.Look("every ten minutes"), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var second = keep.Look("when the driver install finished");
        release.Set();

        Assert.Equal(DriverOutcome.Waiting, second);
        Assert.Equal(DriverOutcome.PutBack, await timer);
        Assert.Equal(["global"], rig.Written);
        Assert.Single(rig.Log);
    }

    [Fact]
    public void ExosOwnInstallKeepsWhatTheNewDriverRefused()
    {
        var rig = Held();
        rig.Identity = New;
        // The restoring step's Apply has just remembered what the new driver refused, under the new identity.
        rig.Pc.DriverSkipped = ["0x10835000"];
        rig.Pc.DriverSkippedDriver = New;

        rig.Keep.NoteExosOwnRestore(allDone: true);

        Assert.Equal(["0x10835000"], rig.Pc.DriverSkipped);
        Assert.Equal(New, rig.Pc.DriverSkippedDriver);
        Assert.Equal(New, rig.Pc.Driver);

        // What the old driver refused is not said of the new one.
        var old = Held();
        old.Identity = New;
        old.Pc.DriverSkipped = ["0x10835000"];
        old.Pc.DriverSkippedDriver = Old;
        old.Keep.NoteExosOwnRestore(allDone: true);
        Assert.Empty(old.Pc.DriverSkipped);
        Assert.Null(old.Pc.DriverSkippedDriver);
    }

    [Fact]
    public void AnIntelOrVirtualDisplayDriverChangeIsNotANvidiaChange()
    {
        (string Name, string Version)[] before =
        [
            ("Intel(R) UHD Graphics 770", "31.0.101.4502"),
            ("NVIDIA GeForce RTX 3070", "32.0.15.6094"),
        ];
        var nvidia = DriverIdentity.Of("nvidia", before);

        // The iGPU's driver updated: Exo's NVIDIA profile is not touched by that.
        Assert.Equal(nvidia, DriverIdentity.Of("nvidia", [("Intel(R) UHD Graphics 770", "31.0.101.5000"), before[1]]));
        // A virtual display driver (a streaming or an indirect display one) came or went.
        Assert.Equal(nvidia, DriverIdentity.Of("nvidia", [.. before, ("Virtual Display Driver", "1.0.0.0")]));
        Assert.Equal(nvidia, DriverIdentity.Of("nvidia", [before[1]]));
        // The NVIDIA driver itself is a change.
        Assert.NotEqual(nvidia, DriverIdentity.Of("nvidia", [before[0], ("NVIDIA GeForce RTX 3070", "32.0.15.7270")]));
        // A PC with no NVIDIA adapter has no NVIDIA identity at all.
        Assert.True(string.IsNullOrEmpty(DriverIdentity.Of("nvidia", [before[0]])));
    }

    [Fact]
    public void AReorderOfTheSameDriversIsNotAChange()
    {
        (string Name, string Version)[] cards =
        [
            ("NVIDIA GeForce RTX 3070", "32.0.15.6094"),
            ("NVIDIA GeForce RTX 3060", "32.0.15.5000"),
        ];
        Assert.Equal(DriverIdentity.Of("nvidia", cards), DriverIdentity.Of("nvidia", [cards[1], cards[0]]));
    }

    [Fact]
    public void AProfileAlreadyAppliedIsNotWrittenAgain()
    {
        var screen = new GpuProfile.Screen(null, 0, false);
        GpuProfileService.State Read(string status) => new(true, null, "nvidia", "RTX 3070", 30, screen, status, true, false, []);
        var writes = 0;
        var waits = 0;

        // The profile reads applied after the look's reload: the player's changes in NVIDIA's own panel since are not written over.
        Assert.Null(DriverRestore.PutProfileBack(() => Read("applied"), _ => { writes++; return Read("applied"); }, _ => waits++));
        Assert.Equal(0, writes);

        // One that reads changed is written, and written again while it reads changed (the older-driver retry).
        var answers = new Queue<string>(["changed", "applied"]);
        Assert.Null(DriverRestore.PutProfileBack(() => Read("changed"), _ => { writes++; return Read(answers.Dequeue()); }, _ => waits++));
        Assert.Equal(2, writes);
        Assert.Equal(1, waits);

        // Exo never applied one: nothing to put back.
        Assert.Null(DriverRestore.PutProfileBack(() => Read("changed") with { CanUndo = false }, _ => { writes++; return Read("applied"); }, _ => waits++));
        Assert.Equal(2, writes);
    }

    [Fact]
    public void EveryRowOfTheTableIsWalked()
    {
        var rows = DriverKeep.Table;
        // One row per situation, and it is the one a situation gets.
        Assert.Equal(Enum.GetValues<DriverSituation>().Length, rows.Count);
        Assert.Equal(rows.Count, rows.Select(row => row.Situation).Distinct().Count());
        foreach (var row in rows) Assert.Same(row, DriverKeep.Row(row.Situation));

        // Every row walks: a rig made to stand in it gives that row's outcome, and its step is what happens.
        foreach (var row in rows)
        {
            var rig = Walk(row.Situation);
            var outcome = rig.Keep.Look("walking");

            Assert.Equal(row.Outcome, outcome);
            Assert.Equal(row.Step.HasFlag(DriverStep.ForgetIds) ? 1 : 0, rig.Refreshes);
            Assert.Equal((row.Step & (DriverStep.NoteDriver | DriverStep.PutBack | DriverStep.NoteSighting | DriverStep.ClearSighting)) != 0 ? 1 : 0, rig.Saves);
            Assert.Equal(row.Step.HasFlag(DriverStep.PutBack) ? 1 : 0, rig.PutBacks);
            // The table is the only place a step is decided: no row does nothing it does not say.
            Assert.Equal(row.Step.HasFlag(DriverStep.PutBack), rig.Written.Count > 0);
            // A row that writes nothing says nothing.
            if ((row.Step & (DriverStep.PutBack | DriverStep.NoteDriver | DriverStep.NoteSighting)) == 0) Assert.Empty(rig.Log);
            // NvAPI is loaded again for a put back, and only for one.
            Assert.Equal(row.Step.HasFlag(DriverStep.PutBack), rig.Order.Contains("reload"));
        }
    }

    /// <summary>A rig made to stand in one situation of the table.</summary>
    private static Rig Walk(DriverSituation situation)
    {
        var rig = new Rig();
        switch (situation)
        {
            case DriverSituation.UnknownIdentity:
                rig.Identity = null;
                rig.Pc.Driver = Old;
                break;
            case DriverSituation.Installing:
                rig.Identity = New;
                rig.Pc.Driver = Old;
                rig.Installing = true;
                break;
            case DriverSituation.GameRunning:
                rig.Identity = New;
                rig.Pc.Driver = Old;
                rig.GameRunning = true;
                break;
            case DriverSituation.NoDriverRecorded:
                rig.Identity = Old;
                break;
            case DriverSituation.SameDriver:
                rig.Identity = Old;
                rig.Pc.Driver = Old;
                break;
            case DriverSituation.NothingToPutBack:
                rig.Identity = New;
                rig.Pc.Driver = Old;
                Stable(rig);
                break;
            case DriverSituation.DriverChanged:
                rig.Identity = New;
                rig.Pc.Driver = Old;
                rig.HasGlobal = true;
                Stable(rig);
                break;
            case DriverSituation.NewDriverFirstSeen:
                rig.Identity = New;
                rig.Pc.Driver = Old;
                rig.HasGlobal = true;
                break;
            case DriverSituation.NewDriverSettling:
                rig.Identity = New;
                rig.Pc.Driver = Old;
                rig.HasGlobal = true;
                rig.Pc.DriverSeen = New;
                rig.Pc.DriverSeenAt = rig.Now.AddMinutes(-1);
                break;
            case DriverSituation.ChangeUndone:
                rig.Identity = Old;
                rig.Pc.Driver = Old;
                rig.Pc.DriverSeen = New;
                rig.Pc.DriverSeenAt = rig.Now.AddMinutes(-5);
                break;
        }
        return rig;
    }

    /// <summary>The changed identity was first seen three minutes ago.</summary>
    private static void Stable(Rig rig)
    {
        rig.Pc.DriverSeen = New;
        rig.Pc.DriverSeenAt = rig.Now.AddMinutes(-3);
    }
}
