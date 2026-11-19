using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways rebuilding shaders after a driver change could go wrong silently:
/// - a driver installed by anyone resets the compiled shaders and nothing compiles them again;
/// - the queue is built for a half-installed driver, or during the install;
/// - it compiles under a game, on battery or while the player uses the PC, or never pauses when they come back;
/// - the least played game compiles first;
/// - a driver that changes again mid-queue keeps compiling for the old one;
/// - the queue is lost at a restart, or every look builds it again;
/// - a game that can never compile restarts the engine for ever;
/// - a game compiled for the driver installed now is queued;
/// - a new, never compiled or updated game waits for someone to press Compile, or a game it gave up on is queued for ever;
/// - an install or an update is never scanned, or is scanned at every look or while the PC is in use.
/// </summary>
public sealed class ShaderRebuildTests
{
    private const string Old = "32.0.15.6094";
    private const string New = "32.0.15.7270";

    private sealed class Rig
    {
        internal string? Identity { get; set; } = Old;
        internal bool Installing { get; set; }
        internal bool Idle { get; set; } = true;
        internal bool Compiling { get; set; }
        internal bool StartWorks { get; set; } = true;
        internal bool SaveFails { get; set; }
        internal DateTime Now { get; set; } = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        internal List<RebuildGame> Stale { get; } = [];
        internal ShaderRebuild.Record Record { get; set; } = new() { Driver = Old };
        internal string? Installed { get; set; }
        internal int Scans { get; private set; }
        internal List<IReadOnlyList<string>> Starts { get; } = [];
        internal int Pauses { get; private set; }
        internal int Saves { get; private set; }
        internal int Changes { get; private set; }
        internal List<string> Log { get; } = [];

        private ShaderRebuild? _rebuild;

        internal ShaderRebuild Rebuild
        {
            get
            {
                if (_rebuild is not null) return _rebuild;
                _rebuild = new ShaderRebuild(
                    () => Identity, () => Installing, () => Idle, () => Stale, () => Compiling,
                    ids => { Starts.Add(ids); if (StartWorks) Compiling = true; return StartWorks; },
                    () => { Pauses++; Compiling = false; },
                    () => Record,
                    record => { Saves++; if (SaveFails) return false; Record = record; return true; },
                    Log.Add,
                    () => Now,
                    () => Installed,
                    () => { Scans++; return true; });
                _rebuild.Changed += () => Changes++;
                return _rebuild;
            }
        }

        internal void Games(params (string Id, int Minutes)[] games)
        {
            Stale.Clear();
            foreach (var (id, minutes) in games) Stale.Add(new RebuildGame(id, id, minutes, null));
        }

        /// <summary>The engine finished a game: it reads as compiled for the driver installed now.</summary>
        internal void Finish(string id) => Stale.RemoveAll(game => game.EngineId == id);

        /// <summary>A driver install by anyone: the first look only sees it, and two minutes pass.</summary>
        internal void Change(string to)
        {
            Identity = to;
            Rebuild.Tick();
            Now = Now.AddMinutes(2);
        }
    }

    [Fact]
    public void ADriverChangeQueuesTheGamesMostPlayedFirstAndCompilesThem()
    {
        var rig = new Rig();
        rig.Games(("rocket", 40), ("control", 900), ("thymesia", 120));
        rig.Change(New);

        var status = rig.Rebuild.Tick();

        Assert.Equal(new RebuildStatus("running", 0, 3), status);
        Assert.Equal(["control", "thymesia", "rocket"], rig.Record.Queue);
        Assert.Equal(["control", "thymesia", "rocket"], Assert.Single(rig.Starts));
        Assert.Equal(New, rig.Record.Driver);

        // Each game that finishes counts, and nothing is started again while the engine compiles.
        rig.Finish("control");
        Assert.Equal(new RebuildStatus("running", 1, 3), rig.Rebuild.Tick());
        Assert.Single(rig.Starts);

        rig.Finish("thymesia");
        rig.Finish("rocket");
        rig.Compiling = false;
        Assert.Equal(new RebuildStatus("done", 3, 3), rig.Rebuild.Tick());
        Assert.Contains(rig.Log, line => line.Contains("ready for the new driver, 3 of 3 games", StringComparison.Ordinal));

        // Done stays done and says nothing more.
        var lines = rig.Log.Count;
        Assert.Equal(new RebuildStatus("done", 3, 3), rig.Rebuild.Tick());
        Assert.Equal(lines, rig.Log.Count);
        Assert.Single(rig.Starts);
    }

    [Fact]
    public void ANewDriverIsQueuedOnlyAfterItHasStoodTwoMinutes()
    {
        var rig = new Rig();
        rig.Games(("control", 900));
        rig.Identity = New;

        Assert.Equal("idle", rig.Rebuild.Tick().State);
        rig.Now = rig.Now.AddMinutes(1);
        Assert.Equal("idle", rig.Rebuild.Tick().State);
        Assert.Empty(rig.Starts);
        Assert.Equal(Old, rig.Record.Driver);

        // The installer rolled back (the game is compiled for the driver back in place): the sighting is forgotten, and
        // the same driver later starts its wait over.
        rig.Identity = Old;
        rig.Stale.Clear();
        rig.Rebuild.Tick();
        Assert.Null(rig.Record.Seen);
        rig.Now = rig.Now.AddHours(1);
        rig.Games(("control", 900));
        rig.Identity = New;
        Assert.Equal("idle", rig.Rebuild.Tick().State);
        Assert.Empty(rig.Starts);

        rig.Now = rig.Now.AddMinutes(2);
        Assert.Equal("running", rig.Rebuild.Tick().State);
    }

    [Fact]
    public void ExosOwnInstallIsQueuedAtOnce()
    {
        var rig = new Rig();
        rig.Games(("control", 900));
        rig.Identity = New;

        Assert.Equal(new RebuildStatus("running", 0, 1), rig.Rebuild.Tick(settled: true));
    }

    [Fact]
    public void NothingIsQueuedDuringAnInstall()
    {
        var rig = new Rig { Installing = true, Identity = New };
        rig.Games(("control", 900));

        rig.Rebuild.Tick();
        rig.Now = rig.Now.AddMinutes(5);
        rig.Rebuild.Tick(settled: true);

        Assert.Empty(rig.Record.Queue);
        Assert.Equal(Old, rig.Record.Driver);
        Assert.Empty(rig.Starts);
    }

    [Fact]
    public void ItWaitsWhileThePcIsInUseAndPausesWhenThePlayerComesBack()
    {
        var rig = new Rig { Idle = false };
        rig.Games(("control", 900), ("rocket", 40));
        rig.Change(New);

        // In use (a game, battery, input): queued, nothing started.
        Assert.Equal(new RebuildStatus("waiting", 0, 2), rig.Rebuild.Tick());
        Assert.Empty(rig.Starts);

        rig.Idle = true;
        Assert.Equal("running", rig.Rebuild.Tick().State);
        Assert.Single(rig.Starts);

        // The player comes back: paused once, not at every look.
        rig.Idle = false;
        Assert.Equal("paused", rig.Rebuild.Tick().State);
        Assert.Equal("paused", rig.Rebuild.Tick().State);
        Assert.Equal(1, rig.Pauses);

        // Away again: it goes on with what is left.
        rig.Finish("control");
        rig.Idle = true;
        Assert.Equal(new RebuildStatus("running", 1, 2), rig.Rebuild.Tick());
        Assert.Equal(["rocket"], rig.Starts[^1]);
    }

    [Fact]
    public void ADriverInstallPausesTheCompile()
    {
        var rig = new Rig();
        rig.Games(("control", 900));
        rig.Change(New);
        Assert.Equal("running", rig.Rebuild.Tick().State);

        rig.Installing = true;

        Assert.Equal("paused", rig.Rebuild.Tick().State);
        Assert.Equal(1, rig.Pauses);
    }

    [Fact]
    public void ADriverThatChangesAgainMidQueueStartsOverForTheNewOne()
    {
        var rig = new Rig();
        rig.Games(("control", 900), ("rocket", 40));
        rig.Change(New);
        Assert.Equal("running", rig.Rebuild.Tick().State);
        rig.Finish("control");

        // Another driver: everything compiled for the one before is stale again.
        rig.Games(("control", 900), ("rocket", 40), ("thymesia", 120));
        rig.Change("33.0.15.1");
        var status = rig.Rebuild.Tick();

        Assert.Equal("33.0.15.1", rig.Record.Driver);
        Assert.Equal(["control", "thymesia", "rocket"], rig.Record.Queue);
        Assert.Equal(1, rig.Pauses);
        Assert.Equal(new RebuildStatus("running", 0, 3), status);
    }

    [Fact]
    public void TheQueueSurvivesARestart()
    {
        var rig = new Rig();
        rig.Games(("control", 900), ("rocket", 40));
        rig.Change(New);
        rig.Rebuild.Tick();
        rig.Finish("control");

        // Exo restarts: the engine is not compiling, the record still holds the queue.
        var again = new Rig { Identity = New, Record = rig.Record };
        again.Games(("rocket", 40));

        Assert.Equal(new RebuildStatus("running", 1, 2), again.Rebuild.Tick());
        Assert.Equal(["rocket"], Assert.Single(again.Starts));
        Assert.DoesNotContain(again.Log, line => line.Contains("queued", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameThatNeverCompilesEndsTheRunAfterThreeStarts()
    {
        var rig = new Rig();
        rig.Games(("control", 900), ("broken", 10));
        rig.Change(New);
        rig.Rebuild.Tick();
        rig.Finish("control");
        rig.Compiling = false;

        // The engine stops by itself each time with "broken" still not compiled.
        for (var look = 0; look < 10 && rig.Rebuild.Status.State != "done"; look++)
        {
            rig.Rebuild.Tick();
            rig.Compiling = false;
        }

        Assert.Equal(new RebuildStatus("done", 1, 2), rig.Rebuild.Status);
        Assert.InRange(rig.Starts.Count, 2, 1 + ShaderRebuild.StallLimit + 1);
        Assert.Equal(1, rig.Log.Count(line => line.Contains("did not compile", StringComparison.Ordinal)));
        var starts = rig.Starts.Count;
        rig.Rebuild.Tick();
        Assert.Equal(starts, rig.Starts.Count);
    }

    [Fact]
    public void PausingALongGameManyTimesIsNotAStall()
    {
        var rig = new Rig();
        rig.Games(("control", 900));
        rig.Change(New);
        rig.Rebuild.Tick();

        for (var round = 0; round < 8; round++)
        {
            rig.Idle = false;
            Assert.Equal("paused", rig.Rebuild.Tick().State);
            rig.Idle = true;
            Assert.Equal("running", rig.Rebuild.Tick().State);
        }
        Assert.Equal(0, rig.Record.StalledStarts);
    }

    [Fact]
    public void AnEngineThatCannotBeAskedIsTriedAgainAndThenLeft()
    {
        var rig = new Rig { StartWorks = false };
        rig.Games(("control", 900));
        rig.Change(New);

        for (var look = 0; look < 10 && rig.Rebuild.Status.State != "done"; look++)
            Assert.NotEqual("running", rig.Rebuild.Tick().State);

        Assert.Equal(new RebuildStatus("done", 0, 1), rig.Rebuild.Status);
        Assert.Equal(ShaderRebuild.StallLimit, rig.Starts.Count);
    }

    [Fact]
    public void NewAndUpdatedGamesAreQueuedWithNoDriverChange()
    {
        var rig = new Rig();
        Assert.Equal("idle", rig.Rebuild.Tick().State);

        // A game installed (never compiled) and one updated since its compile.
        rig.Games(("new", 0), ("updated", 300));
        var status = rig.Rebuild.Tick();

        Assert.Equal(new RebuildStatus("running", 0, 2), status);
        Assert.Equal(["updated", "new"], Assert.Single(rig.Starts));
        Assert.Equal(Old, rig.Record.Driver);
        Assert.Contains(rig.Log, line => line.Contains("2 new or updated games queued", StringComparison.Ordinal));

        rig.Finish("new");
        rig.Finish("updated");
        rig.Compiling = false;
        Assert.Equal("done", rig.Rebuild.Tick().State);

        // Another install after the run is queued too.
        rig.Games(("later", 0));
        Assert.Equal(new RebuildStatus("running", 0, 1), rig.Rebuild.Tick());
    }

    [Fact]
    public void NewGamesWaitWhileThePcIsInUse()
    {
        var rig = new Rig { Idle = false };
        rig.Games(("new", 0));

        Assert.Equal(new RebuildStatus("waiting", 0, 1), rig.Rebuild.Tick());
        Assert.Empty(rig.Starts);
        rig.Idle = true;
        Assert.Equal("running", rig.Rebuild.Tick().State);
    }

    [Fact]
    public void AGameGivenUpOnIsNotQueuedAgainUntilTheDriverChanges()
    {
        var rig = new Rig();
        rig.Games(("broken", 10));
        for (var look = 0; look < 10 && rig.Rebuild.Status.State != "done"; look++)
        {
            rig.Rebuild.Tick();
            rig.Compiling = false;
        }
        Assert.Equal("done", rig.Rebuild.Status.State);
        Assert.Equal(["broken"], rig.Record.GaveUp);

        var starts = rig.Starts.Count;
        rig.Rebuild.Tick();
        rig.Rebuild.Tick();
        Assert.Equal(starts, rig.Starts.Count);

        rig.Change(New);
        Assert.Equal("running", rig.Rebuild.Tick().State);
        Assert.Empty(rig.Record.GaveUp);
    }

    [Fact]
    public void AnInstallOrUpdateIsScannedOnceWhileThePcIsIdle()
    {
        var rig = new Rig { Installed = "a", Idle = false };
        rig.Rebuild.Tick();
        Assert.Equal(0, rig.Scans);

        rig.Idle = true;
        rig.Rebuild.Tick();
        rig.Rebuild.Tick();
        Assert.Equal(1, rig.Scans);
        Assert.Equal("a", rig.Record.Scanned);

        // Not while the engine compiles, and again once the games change.
        rig.Installed = "b";
        rig.Compiling = true;
        rig.Rebuild.Tick();
        Assert.Equal(1, rig.Scans);
        rig.Compiling = false;
        rig.Rebuild.Tick();
        Assert.Equal(2, rig.Scans);
    }

    [Fact]
    public void ADriverChangeWithNothingCompiledSaysNothing()
    {
        var rig = new Rig();
        rig.Change(New);

        Assert.Equal(new RebuildStatus("idle", 0, 0), rig.Rebuild.Tick());
        Assert.Equal(New, rig.Record.Driver);
        Assert.Empty(rig.Starts);
        Assert.Equal(0, rig.Changes);
    }

    [Fact]
    public void TheFirstLookOnlyNotesTheDriver()
    {
        var rig = new Rig { Record = new ShaderRebuild.Record() };
        rig.Games(("control", 900));

        Assert.Equal("idle", rig.Rebuild.Tick().State);
        Assert.Equal(Old, rig.Record.Driver);
        Assert.Empty(rig.Record.Queue);
    }

    [Fact]
    public void ARecordThatCannotBeSavedDoesNotQueueAtEveryLook()
    {
        var rig = new Rig { SaveFails = true };
        rig.Games(("control", 900));
        rig.Identity = New;

        rig.Rebuild.Tick(settled: true);
        for (var look = 0; look < 5; look++) rig.Rebuild.Tick(settled: true);

        Assert.Equal(1, rig.Log.Count(line => line.Contains("queued", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnIdentityThatCannotBeReadDoesNothing()
    {
        var rig = new Rig { Identity = null };
        rig.Games(("control", 900));

        Assert.Equal("idle", rig.Rebuild.Tick().State);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public void ThePageIsToldOnlyWhenTheStatusChanges()
    {
        var rig = new Rig();
        rig.Games(("control", 900));
        rig.Change(New);

        rig.Rebuild.Tick();
        rig.Rebuild.Tick();
        rig.Rebuild.Tick();

        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public void OnlyAGameCompiledForAnotherDriverIsStale()
    {
        string[] installed = ["32.0.15.7270", "31.0.101.4502"];
        Assert.True(ScsKillerStatus.CompiledForAnotherDriver("10DE:2484:1458:32.0.15.6094", installed));
        Assert.False(ScsKillerStatus.CompiledForAnotherDriver("10DE:2484:1458:32.0.15.7270", installed));
        // Never compiled, or a record that names no driver.
        Assert.False(ScsKillerStatus.CompiledForAnotherDriver(null, installed));
        Assert.False(ScsKillerStatus.CompiledForAnotherDriver("", installed));
        Assert.False(ScsKillerStatus.CompiledForAnotherDriver("10DE:2484:1458:", installed));
    }

    [Fact]
    public void TheOrderIsMostPlayedThenLastPlayedThenName()
    {
        var day = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var order = ShaderRebuild.Order(
        [
            new("b", "Beta", 10, day),
            new("a", "Alpha", 10, day),
            new("c", "Recent", 10, day.AddDays(3)),
            new("d", "Most", 500, null),
        ]);
        Assert.Equal(["d", "c", "a", "b"], order.Select(game => game.EngineId));
    }

    [Fact]
    public void TheRecordRoundTripsThroughItsFile()
    {
        var folder = Directory.CreateTempSubdirectory("exo-rebuild-").FullName;
        try
        {
            var path = Path.Combine(folder, "shader-rebuild.json");
            Assert.Null(ShaderRebuild.LoadFrom(path).Driver);
            var record = new ShaderRebuild.Record { Driver = New, State = RebuildState.Paused, Queue = ["control", "rocket"], Ready = 1 };
            Assert.True(ShaderRebuild.SaveTo(path, record));
            var read = ShaderRebuild.LoadFrom(path);
            Assert.Equal(New, read.Driver);
            Assert.Equal(RebuildState.Paused, read.State);
            Assert.Equal(["control", "rocket"], read.Queue);
            Assert.Equal(1, read.Ready);
            // A file that is not a record reads as none, never as an error.
            File.WriteAllText(path, "{ not json");
            Assert.Null(ShaderRebuild.LoadFrom(path).Driver);
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
    }

    [Fact]
    public void EveryRowOfTheTableIsWalked()
    {
        var rows = ShaderRebuild.Table;
        var states = Enum.GetValues<RebuildState>();
        var events = Enum.GetValues<RebuildEvent>();
        Assert.Equal(states.Length * events.Length, rows.Count);
        foreach (var state in states)
            foreach (var ev in events)
                Assert.Same(rows.Single(row => row.State == state && row.Event == ev), ShaderRebuild.Row(state, ev));

        // No state lacks an exit: a driver change leaves every state for Waiting, and every working state can reach Done.
        foreach (var state in states)
            Assert.Equal(RebuildState.Waiting, ShaderRebuild.Row(state, RebuildEvent.DriverChanged).Next);
        foreach (var state in new[] { RebuildState.Waiting, RebuildState.Running, RebuildState.Paused })
        {
            Assert.Equal(RebuildState.Done, ShaderRebuild.Row(state, RebuildEvent.AllReady).Next);
            Assert.Equal(RebuildState.Done, ShaderRebuild.Row(state, RebuildEvent.Stalled).Next);
        }

        // Each row, from a rig standing in its state, gives its next state and does what it says and nothing else.
        foreach (var row in rows.Where(row => row.Event != RebuildEvent.GamesWaiting && (row.State is RebuildState.Waiting or RebuildState.Running or RebuildState.Paused || row.Event == RebuildEvent.DriverChanged)))
        {
            var rig = new Rig { Identity = New, Record = new ShaderRebuild.Record { Driver = New, State = row.State, Queue = ["control"] } };
            rig.Games(("control", 900));
            rig.Compiling = row.State == RebuildState.Running;
            switch (row.Event)
            {
                case RebuildEvent.DriverChanged:
                    rig.Identity = "33.0.15.1";
                    rig.Idle = false;
                    break;
                case RebuildEvent.GatesClosed: rig.Idle = false; break;
                case RebuildEvent.AllReady: rig.Stale.Clear(); break;
                case RebuildEvent.Stalled: rig.Record.StalledStarts = ShaderRebuild.StallLimit; break;
            }
            rig.Rebuild.Tick(settled: true);
            var label = $"{row.State} on {row.Event}";
            Assert.True(row.Next == rig.Record.State, label + " goes to " + row.Next + ", was " + rig.Record.State);
            Assert.True(row.Action.HasFlag(RebuildAction.Pause) == (rig.Pauses == 1), label + " pause");
            Assert.True(row.Action.HasFlag(RebuildAction.Queue) == rig.Log.Any(line => line.Contains("queued", StringComparison.Ordinal)), label + " queue");
            // Start asks the engine only when it does not already compile.
            Assert.True((row.Action.HasFlag(RebuildAction.Start) && row.State != RebuildState.Running) == (rig.Starts.Count == 1), label + " start");
        }
    }
}
