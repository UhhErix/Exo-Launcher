namespace ExoLauncher.Services;

/// <summary>Where one look at the driver side finds the PC (docs/architecture/keep.md, "The driver side").</summary>
internal enum DriverSituation
{
    /// <summary>The installed drivers cannot be read at all: never taken for a driver change.</summary>
    UnknownIdentity,
    /// <summary>A driver install (Exo's or anyone's, as far as Exo knows) runs: nothing is written into it.</summary>
    Installing,
    /// <summary>A game runs: nothing is written while it does.</summary>
    GameRunning,
    /// <summary>The record has never held a driver: the identity is recorded and nothing is written.</summary>
    NoDriverRecorded,
    /// <summary>The driver is the one Exo's settings were last held under.</summary>
    SameDriver,
    /// <summary>A new driver, and Exo has no driver-side setting of its own to put back.</summary>
    NothingToPutBack,
    /// <summary>A new driver, and Exo has settings of its own to put back.</summary>
    DriverChanged,
    /// <summary>A changed identity seen for the first time: nothing is written until it has stood for two looks at least two minutes apart.</summary>
    NewDriverFirstSeen,
    /// <summary>A changed identity seen before, but not yet for two minutes: nothing is written.</summary>
    NewDriverSettling,
    /// <summary>A sighting is on record and the identity is the recorded one again: the sighting is forgotten.</summary>
    ChangeUndone,
}

/// <summary>What one row of the table does.</summary>
[Flags]
internal enum DriverStep
{
    None = 0,
    /// <summary>Record the identity as the one Exo's settings are held under (and forget how many tries it took).</summary>
    NoteDriver = 1,
    /// <summary>The driver changed: forget the setting ids it listed (it may list others now).</summary>
    ForgetIds = 2,
    /// <summary>Put back Exo's global profile and each game's, and record the identity when every one reads back applied.</summary>
    PutBack = 4,
    /// <summary>A changed identity is seen for the first time: remember it and when, write nothing.</summary>
    NoteSighting = 8,
    /// <summary>The identity went back to the recorded one: forget the sighting.</summary>
    ClearSighting = 16,
}

/// <summary>What a look decided, as the page and the log say it.</summary>
internal enum DriverOutcome
{
    /// <summary>The identity could not be read: nothing was decided.</summary>
    Unknown,
    /// <summary>An install or a game holds it: nothing was written, and the next look tries again.</summary>
    Waiting,
    /// <summary>The identity is recorded and nothing was written.</summary>
    Noted,
    /// <summary>The driver is the one Exo's settings were last held under: nothing was written.</summary>
    Same,
    /// <summary>A new driver, and Exo's settings were put back and read back as applied.</summary>
    PutBack,
    /// <summary>A new driver, and a put back did not read back as applied: the next look tries again.</summary>
    Failed,
    /// <summary>Three looks running failed for the same driver: the identity is recorded and Exo stopped trying.</summary>
    GaveUp,
    /// <summary>A changed identity has not yet stood for two looks at least two minutes apart: nothing was written, and the next look decides.</summary>
    Pending,
}

/// <summary>One row: in <see cref="Situation"/>, do <see cref="Step"/>, answer <see cref="Outcome"/>.</summary>
internal sealed record DriverRow(DriverSituation Situation, DriverStep Step, DriverOutcome Outcome);

/// <summary>
/// The driver side of Keep: Exo's NVIDIA global profile and each game's NVIDIA profile are put back after a graphics driver
/// change, however the driver was installed (Exo, Windows Update, the vendor's app or by hand), and the driver's setting ids
/// are read again. One look compares a driver identity (<see cref="DriverIdentity"/>, one registry read) with the identity the
/// record says Exo's settings were last held under, and decides by <see cref="Table"/>; nothing is ever written during an
/// install or while a game runs. Every call into a driver is a function given to the constructor, so the table is tested with
/// no driver at all.
/// Ways it could fail: writing into an install or a game; writing Exo's profile again when it is already in place (Exo's own
/// install restores it: <see cref="NoteExosOwnRestore"/> records the identity and writes nothing); a put back that reads back
/// as changed recorded as held; a driver that refuses for ever (three looks running, then Exo stops and says so once); the
/// driver's old id list kept after a change.
/// </summary>
/// <param name="identity">The installed display drivers' identity now, or null when it cannot be read.</param>
/// <param name="installing">A driver install runs now.</param>
/// <param name="gameRunning">A library game runs now.</param>
/// <param name="hasGlobalProfile">Exo's global GPU profile was applied (its backup is there).</param>
/// <param name="gameProfiles">How many games have an NVIDIA profile of Exo's to put back; asked only when a driver changed.</param>
/// <param name="putBackGlobal">Puts the global profile back; true when it reads back as applied.</param>
/// <param name="putBackGames">Puts each game's profile back; how many it wrote, or <see cref="DriverKeep.GamesFailed"/>.</param>
/// <param name="refreshIds">Forgets the cached list of setting ids the driver lists.</param>
/// <param name="reloadNvapi">Loads NvAPI again for the driver installed now: it keeps talking to the one it found, which an install by anyone else removed.</param>
/// <param name="load">What the record holds for this PC.</param>
/// <param name="save">Changes what the record holds for this PC and saves it; false when it could not be saved.</param>
/// <param name="log">One line for app.log.</param>
/// <param name="now">The time now (UTC); a test sets it.</param>
internal sealed class DriverKeep(
    Func<string?> identity,
    Func<bool> installing,
    Func<bool> gameRunning,
    Func<bool> hasGlobalProfile,
    Func<int> gameProfiles,
    Func<bool> putBackGlobal,
    Func<int> putBackGames,
    Action refreshIds,
    Action reloadNvapi,
    Func<Keep.KeepPc> load,
    Func<Action<Keep.KeepPc>, bool> save,
    Action<string> log,
    Func<DateTime>? now = null)
{
    /// <summary>Looks running that may fail for one identity before Exo stops trying.</summary>
    internal const int Tries = 3;

    /// <summary>What <see cref="putBackGames"/> answers when a game's profile did not read back as applied.</summary>
    internal const int GamesFailed = -1;

    /// <summary>How long a changed identity must stand before Exo acts on it: an installer swaps the driver in steps, and may roll back.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly Func<DateTime> clock = now ?? (() => DateTime.UtcNow);
    // This run's memory, for a record that cannot be saved: the identity settled, the sighting, and the failed tries.
    private string? _held, _seen, _tryOn;
    private DateTime? _seenAt;
    private int _tries;

    /// <summary>How long the page says Exo put the driver's settings back with no game said to have run since: a day.</summary>
    internal static readonly TimeSpan PutBackShowsFor = TimeSpan.FromHours(24);

    /// <summary>
    /// The one Exo uses, with the real calls: the identity is one registry read (<see cref="DriverIdentity"/>); an install
    /// is <see cref="DriverInstallSignal.Installing"/>; a game running is <see cref="AnyGameRunning"/> (a read of a watched
    /// counter, safe from a background thread, unlike the process scan The page's own bridge uses); the global profile goes
    /// back through <see cref="DriverRestore.PutProfileBack"/> (the same write and read-back retry Exo's own install uses);
    /// each game's through <see cref="GameSettings.RestoreNvidiaGames"/>; the id list through <see cref="GraphicsService"/>.
    /// </summary>
    internal static DriverKeep Shared { get; } = new(
        DriverIdentity.Current,
        () => DriverInstallSignal.Installing,
        AnyGameRunning,
        () => GpuProfileService.HasBackup,
        () => GameSettings.NvidiaGames().Count(game => game.Set.Count > 0),
        // HasBackup again: a backup removed between the two asks means nothing was written, which is never "applied".
        () => GpuProfileService.HasBackup && DriverRestore.PutProfileBack() is null,
        () =>
        {
            var (done, failed) = GameSettings.RestoreNvidiaGames();
            return failed.Count > 0 ? GamesFailed : done.Count;
        },
        GraphicsService.ForgetKnownIds,
        () => NvidiaProfiles.Reinitialize(),
        () => Keep.Load().Pc,
        change => Keep.Update(doc => change(doc.Pc)),
        AppLog.Info);

    /// <summary>Every situation and what it does. A test walks every row, and that no situation is missing one.</summary>
    internal static readonly IReadOnlyList<DriverRow> Table =
    [
        new(DriverSituation.UnknownIdentity, DriverStep.None, DriverOutcome.Unknown),
        new(DriverSituation.Installing, DriverStep.None, DriverOutcome.Waiting),
        new(DriverSituation.GameRunning, DriverStep.None, DriverOutcome.Waiting),
        new(DriverSituation.NoDriverRecorded, DriverStep.NoteDriver, DriverOutcome.Noted),
        new(DriverSituation.SameDriver, DriverStep.None, DriverOutcome.Same),
        new(DriverSituation.NothingToPutBack, DriverStep.ForgetIds | DriverStep.NoteDriver, DriverOutcome.Noted),
        new(DriverSituation.DriverChanged, DriverStep.ForgetIds | DriverStep.PutBack, DriverOutcome.PutBack),
        new(DriverSituation.NewDriverFirstSeen, DriverStep.NoteSighting, DriverOutcome.Pending),
        new(DriverSituation.NewDriverSettling, DriverStep.None, DriverOutcome.Pending),
        new(DriverSituation.ChangeUndone, DriverStep.ClearSighting, DriverOutcome.Same),
    ];

    private static readonly Dictionary<DriverSituation, DriverRow> Index = Table.ToDictionary(row => row.Situation);

    /// <summary>The row for a situation; every situation has one.</summary>
    internal static DriverRow Row(DriverSituation situation) => Index[situation];

    /// <summary>
    /// Where the PC stands, in the table's order: the identity, then an install, then a game, then the record. Whether Exo
    /// has settings of its own is asked only when the driver changed.
    /// </summary>
    internal DriverSituation Situation() => Situation(identity());

    private DriverSituation Situation(string? id)
    {
        if (string.IsNullOrEmpty(id)) return DriverSituation.UnknownIdentity;
        if (installing()) return DriverSituation.Installing;
        if (gameRunning()) return DriverSituation.GameRunning;
        // This run already settled this identity and the record could not be told: nothing is done twice.
        if (_held == id) return DriverSituation.SameDriver;
        var pc = load();
        if (string.IsNullOrEmpty(pc.Driver)) return DriverSituation.NoDriverRecorded;
        var (seen, seenAt) = Sighting(pc);
        if (pc.Driver == id) return seen is null ? DriverSituation.SameDriver : DriverSituation.ChangeUndone;
        // A clock set back since the sighting starts the wait over rather than waiting for ever.
        if (seen != id || seenAt is not { } at || clock() < at) return DriverSituation.NewDriverFirstSeen;
        if (clock() - at < Settle) return DriverSituation.NewDriverSettling;
        return hasGlobalProfile() || gameProfiles() > 0 ? DriverSituation.DriverChanged : DriverSituation.NothingToPutBack;
    }

    /// <summary>The changed identity that waits and when it was first seen: the record's, or this run's when the record could not be saved.</summary>
    private (string? Id, DateTime? At) Sighting(Keep.KeepPc pc) =>
        pc.DriverSeen is { Length: > 0 } ? (pc.DriverSeen, pc.DriverSeenAt) : (_seen, _seenAt);

    /// <summary>
    /// Decides and acts: the table's row for where the PC stands, its steps, then what it says happened. One look at a time:
    /// a look that arrives while another is deciding or writing answers Waiting and writes nothing.
    /// </summary>
    internal DriverOutcome Look(string when)
    {
        if (!Monitor.TryEnter(gate)) return DriverOutcome.Waiting;
        try
        {
            var id = identity();
            var row = Row(Situation(id));
            if (string.IsNullOrEmpty(id)) return row.Outcome;
            if (row.Step.HasFlag(DriverStep.ForgetIds)) refreshIds();
            if (row.Step.HasFlag(DriverStep.PutBack)) return PutBack(id, load().Driver, when);
            if (row.Step.HasFlag(DriverStep.NoteSighting)) See(id, load().Driver, when);
            if (row.Step.HasFlag(DriverStep.ClearSighting)) Unsee();
            if (row.Step.HasFlag(DriverStep.NoteDriver)) Note(id);
            return row.Outcome;
        }
        finally { Monitor.Exit(gate); }
    }

    /// <summary>A changed identity seen for the first time: remembered with the time (in this run too, for a record that cannot be saved), and nothing is written.</summary>
    private void See(string id, string? was, string when)
    {
        var at = clock();
        _seen = id;
        _seenAt = at;
        save(pc =>
        {
            pc.DriverSeen = id;
            pc.DriverSeenAt = at;
        });
        log($"Driver: {Called(when)} saw driver {Show(id)} in place of {Show(was)}; Exo's settings go back once it has stood for two minutes.");
    }

    /// <summary>The identity is the recorded one again: the sighting is forgotten.</summary>
    private void Unsee()
    {
        _seen = null;
        _seenAt = null;
        save(ForgetSighting);
    }

    private static void ForgetSighting(Keep.KeepPc pc)
    {
        pc.DriverSeen = null;
        pc.DriverSeenAt = null;
    }

    /// <summary>
    /// Records the identity Exo's settings are held under, with no try standing against it. When the record cannot be saved
    /// this run remembers it, so nothing is tried or said again until the driver changes or Exo restarts.
    /// </summary>
    private bool Note(string id)
    {
        _seen = null;
        _seenAt = null;
        _tries = 0;
        _tryOn = null;
        var saved = save(pc =>
        {
            pc.Driver = id;
            pc.DriverTries = 0;
            pc.DriverTryOn = null;
            ForgetSighting(pc);
            if (pc.DriverSkippedDriver != id) ForgetSkipped(pc);
        });
        _held = saved ? null : id;
        return saved;
    }

    /// <summary>
    /// The settings the old driver skipped are forgotten with it: a new driver may list and take them, and the page must not
    /// keep saying they are not there. The ids are read again (<see cref="DriverStep.ForgetIds"/>) for the same reason.
    /// </summary>
    internal static void ForgetSkipped(Keep.KeepPc pc)
    {
        pc.DriverSkipped = [];
        pc.DriverSkippedDriver = null;
    }

    /// <summary>
    /// A new driver: put back the global profile (when Exo had one) and each game's, then read the put back as a fresh read
    /// says. Nothing is recorded when one did not take, so the next look tries again; after <see cref="Tries"/> looks running
    /// for the same new identity the identity is recorded anyway, and the log says once that Exo stopped.
    /// </summary>
    private DriverOutcome PutBack(string id, string? was, string when)
    {
        // NvAPI still talks to the driver it found when Exo started, which an install by anyone else removed.
        reloadNvapi();
        var global = false;
        string? problem = null;
        if (hasGlobalProfile())
        {
            global = putBackGlobal();
            if (!global) problem = "Exo's GPU profile did not read back as applied";
        }
        var games = gameProfiles() > 0 ? putBackGames() : 0;
        if (games == GamesFailed) problem ??= "a game's NVIDIA profile did not read back as applied";
        if (problem is null)
        {
            _seen = null;
            _seenAt = null;
            _tries = 0;
            _tryOn = null;
            var at = clock();
            var saved = save(pc =>
            {
                pc.Driver = id;
                pc.DriverTries = 0;
                pc.DriverTryOn = null;
                pc.DriverPutBack = id;
                pc.DriverPutBackAt = at;
                ForgetSighting(pc);
                // The global profile went back in above, which keeps the ids this driver skipped or refused for it.
            });
            // A record that cannot be saved must not make every look write the profiles again: this run remembers.
            _held = saved ? null : id;
            log($"Driver: {Called(when)} found driver {Show(id)} in place of {Show(was)}: put back {Did(global, games)}." +
                (saved ? "" : " That couldn't be recorded, so Exo looks again after a restart."));
            return DriverOutcome.PutBack;
        }
        var tries = Failed(id);
        if (tries >= Tries)
        {
            Note(id);
            log($"Driver: {problem}, and {Called(when)} has tried {tries} looks running for driver {Show(id)}: Exo stops trying until the driver changes again.");
            return DriverOutcome.GaveUp;
        }
        log($"Driver: {problem}; {Called(when)} tries again next time.");
        return DriverOutcome.Failed;
    }

    /// <summary>A failed put back under this identity: one more look running, and how many it has been.</summary>
    private int Failed(string id)
    {
        if (_tryOn != id) { _tryOn = id; _tries = 0; }
        _tries++;
        var count = 0;
        var saved = save(pc =>
        {
            if (pc.DriverTryOn != id) { pc.DriverTryOn = id; pc.DriverTries = 0; }
            pc.DriverTries = count = pc.DriverTries + 1;
        });
        // A record that cannot be saved still counts, in this run: a driver that refuses for ever is not tried for ever.
        return saved ? count : _tries;
    }

    private static string Did(bool global, int games) => global
        ? games > 0 ? $"Exo's GPU profile and {games} game profile{(games == 1 ? "" : "s")}" : "Exo's GPU profile"
        : games == 1 ? "1 game's NVIDIA profile" : $"{games} games' NVIDIA profiles";

    private static string Called(string when) => "the " + when + " look";

    private static string Show(string? id) => id ?? "none";

    /// <summary>
    /// Whether a library game runs now. <see cref="GameRunningSignal.Running"/> is the run-state poll's own answer (a
    /// watched int: free from any thread), and <see cref="LaunchOrchestrator.AnyGameRunning"/> covers Exo's own launches in
    /// the moment before the poll sees them. Neither scans processes, which <see cref="MainWindow.RunningLibraryGames"/>
    /// does: that one is for the page, not for a timer.
    /// </summary>
    internal static bool AnyGameRunning()
    {
        if (GameRunningSignal.Running) return true;
        try { return App.Services.Launcher.AnyGameRunning; }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException) { return false; }
    }

    /// <summary>
    /// Whether the page says Exo put the driver's settings back after the update: the identity Exo put them back under is the
    /// one installed now, and no game has run since (the last session's end, when the code knows one; otherwise a day). A
    /// driver Exo never put anything back under, or one that cannot be read now, says nothing.
    /// </summary>
    internal static bool PutBackShows(Keep.KeepPc pc, string? identity, DateTime? lastGameEnded, DateTime now)
    {
        if (pc.DriverPutBack is not { Length: > 0 } putBack || pc.DriverPutBackAt is not { } at) return false;
        if (identity is not { Length: > 0 } || putBack != identity) return false;
        if (lastGameEnded is { } ended && ended > at) return false;
        return now >= at && now - at < PutBackShowsFor;
    }

    /// <summary>
    /// The last time a library game's session ended, read from the per-game last-played playtime writes (a session's end on
    /// this PC; a game another PC played has no time here). Null when none: the page then goes by
    /// <see cref="PutBackShowsFor"/> alone.
    /// </summary>
    internal static DateTime? LastGameEnded()
    {
        try { return PlaytimeService.LastExoSessionEnd(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Exo's own install has just put its settings back itself (<see cref="DriverRestore.AfterInstallAsync"/>): the record says
    /// the new identity is held and nothing is written again, so a look after it finds <see cref="DriverSituation.SameDriver"/>.
    /// </summary>
    internal void NoteExosOwnRestore(bool allDone)
    {
        if (identity() is not { Length: > 0 } id) return;
        _seen = null;
        _seenAt = null;
        var at = clock();
        var saved = save(pc =>
        {
            pc.Driver = id;
            pc.DriverTries = 0;
            pc.DriverTryOn = null;
            ForgetSighting(pc);
            // The restoring step's own Apply has just remembered what this driver refused: only an older driver's list goes.
            if (pc.DriverSkippedDriver != id) ForgetSkipped(pc);
            if (!allDone) return;
            pc.DriverPutBack = id;
            pc.DriverPutBackAt = at;
        });
        _held = saved ? null : id;
        log($"Driver: Exo's own install is done: the record holds driver {Show(id)}" +
            (allDone ? ", and Exo's settings went back." : "; its restoring step could not put everything back."));
    }
}
