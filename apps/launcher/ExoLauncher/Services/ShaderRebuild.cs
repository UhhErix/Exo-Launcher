using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>Where the rebuild of shaders for a new driver stands (kept in <c>shader-rebuild.json</c>, so it survives a restart).</summary>
internal enum RebuildState
{
    /// <summary>Nothing to rebuild and nothing to say.</summary>
    Idle,
    /// <summary>Games are queued for the new driver, and the PC is in use (or a game or a driver install runs).</summary>
    Waiting,
    /// <summary>The engine compiles the queue.</summary>
    Running,
    /// <summary>The compile was paused because the PC came into use; it goes on from where it stopped.</summary>
    Paused,
    /// <summary>The queue was worked through for the driver installed now.</summary>
    Done,
}

internal enum RebuildEvent
{
    /// <summary>The driver's identity differs from the one the record was last rebuilt for, and has stood.</summary>
    DriverChanged,
    /// <summary>Nobody uses the PC: no game, no driver install, mains power, no input.</summary>
    GatesOpen,
    /// <summary>The PC is in use, on battery, in a game or in a driver install.</summary>
    GatesClosed,
    /// <summary>Every queued game reads as compiled for the driver installed now.</summary>
    AllReady,
    /// <summary>The engine was started three times running and no queued game became ready.</summary>
    Stalled,
    /// <summary>With nothing queued, games wait to be compiled for the driver installed now: new, never compiled or updated.</summary>
    GamesWaiting,
}

/// <summary>What a row does, in this order: pause, queue, start, finish.</summary>
[Flags]
internal enum RebuildAction
{
    None = 0,
    /// <summary>Pause the engine's compile.</summary>
    Pause = 1,
    /// <summary>Queue every game not compiled for the driver installed now, most played first, and record the identity.</summary>
    Queue = 2,
    /// <summary>Have the engine compile what is still queued, unless it already compiles.</summary>
    Start = 4,
    /// <summary>Say once in the log how it ended.</summary>
    Finish = 8,
}

internal sealed record RebuildRow(RebuildState State, RebuildEvent Event, RebuildState Next, RebuildAction Action);

/// <summary>A game whose shaders are not compiled for the driver installed now: compiled for another, never compiled, or updated since.</summary>
/// <param name="EngineId">The engine's id for the game.</param>
internal sealed record RebuildGame(string EngineId, string Title, int PlayMinutes, DateTimeOffset? LastPlayed);

/// <summary>What the page is told: a state and two counts, nothing else.</summary>
/// <param name="State">idle, waiting, running, paused or done.</param>
internal sealed record RebuildStatus(string State, int Ready, int Total);

/// <summary>
/// Exo keeps its games' shaders compiled by itself (misc/optimizer/shaders-after-driver.md §5): after any graphics driver
/// change, and for a game that is new, never compiled or updated (<see cref="RebuildEvent.GamesWaiting"/>; an install or an
/// update has the engine scan first, <paramref name="installed"/>). No scheduled task, no start-up entry, nothing to press.
/// One look (<see cref="Tick"/>) compares the driver's identity
/// (<see cref="DriverIdentity"/>, the signal the driver side of Keep uses) with the one the record was last rebuilt for;
/// a change that has stood for two minutes queues every game compiled for another driver, most played first, and the
/// engine's own queue compiles them while nobody uses the PC. One table (<see cref="Table"/>) decides every step, and every
/// call outside is a function given to the constructor, so it is tested with no driver, engine or game.
/// Ways it could fail: compiling under a game, during a driver install or on battery (the gates close and the compile
/// pauses); acting on a half-installed driver (two looks two minutes apart); a game that can never compile restarting the
/// engine for ever (three starts with no game becoming ready end the run, said once); a driver that changes again mid-queue
/// (the queue starts over for the new one); losing the queue at a restart (kept in the record); a record that cannot be
/// saved making every look queue again (this run remembers the identity it queued for).
/// </summary>
/// <param name="identity">The installed display drivers' identity now, or null when it cannot be read.</param>
/// <param name="installing">A driver install runs now.</param>
/// <param name="idle">Nobody uses the PC: no game, mains power, no input, sound or full screen.</param>
/// <param name="stale">The games not compiled for the driver installed now (never compiled, updated, or for another driver).</param>
/// <param name="compiling">The engine compiles now.</param>
/// <param name="start">Has the engine compile these engine ids; false when it could not be asked.</param>
/// <param name="pause">Pauses the engine's compile.</param>
/// <param name="load">The record.</param>
/// <param name="save">Saves the record; false when it could not be saved.</param>
/// <param name="log">One line for app.log.</param>
/// <param name="now">The time now (UTC); a test sets it.</param>
/// <param name="installed">What the installed games are now (ids, folders, builds, sizes), as one string; null when unknown.</param>
/// <param name="scan">Has the engine scan the games again, so it sees new installs and updates; false when it could not be asked.</param>
internal sealed class ShaderRebuild(
    Func<string?> identity,
    Func<bool> installing,
    Func<bool> idle,
    Func<IReadOnlyList<RebuildGame>> stale,
    Func<bool> compiling,
    Func<IReadOnlyList<string>, bool> start,
    Action pause,
    Func<ShaderRebuild.Record> load,
    Func<ShaderRebuild.Record, bool> save,
    Action<string> log,
    Func<DateTime>? now = null,
    Func<string?>? installed = null,
    Func<bool>? scan = null)
{
    /// <summary>What <c>shader-rebuild.json</c> holds, for this PC only.</summary>
    internal sealed class Record
    {
        /// <summary>The driver identity the queue was last built for (or first seen under).</summary>
        public string? Driver { get; set; }
        /// <summary>A changed identity waiting to be seen standing, and when it was first seen.</summary>
        public string? Seen { get; set; }
        public DateTime? SeenAt { get; set; }
        public RebuildState State { get; set; }
        /// <summary>The engine ids queued for <see cref="Driver"/>, in the order they compile.</summary>
        public List<string> Queue { get; set; } = [];
        /// <summary>Starts of the engine running that left no more games ready than before.</summary>
        public int StalledStarts { get; set; }
        /// <summary>How many queued games were ready at the last start.</summary>
        public int ReadyAtStart { get; set; }
        /// <summary>How many queued games read as compiled for <see cref="Driver"/> at the last look.</summary>
        public int Ready { get; set; }
        /// <summary>The games a run gave up on (they did not compile after three starts): not queued again until the driver changes.</summary>
        public List<string> GaveUp { get; set; } = [];
        /// <summary>The installed games the engine last scanned (<paramref name="installed"/>'s string).</summary>
        public string? Scanned { get; set; }
    }

    /// <summary>How long a changed identity must stand before the queue is built: an installer swaps the driver in steps.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    /// <summary>Starts running with no game becoming ready before the run ends.</summary>
    internal const int StallLimit = 3;

    private static RebuildRow R(RebuildState state, RebuildEvent ev, RebuildState next, RebuildAction action = RebuildAction.None) => new(state, ev, next, action);

    /// <summary>Every state and event and what happens. A test walks every row.</summary>
    internal static readonly IReadOnlyList<RebuildRow> Table =
    [
        R(RebuildState.Idle, RebuildEvent.DriverChanged, RebuildState.Waiting, RebuildAction.Queue),
        R(RebuildState.Idle, RebuildEvent.GatesOpen, RebuildState.Idle),
        R(RebuildState.Idle, RebuildEvent.GatesClosed, RebuildState.Idle),
        R(RebuildState.Idle, RebuildEvent.AllReady, RebuildState.Idle),
        R(RebuildState.Idle, RebuildEvent.Stalled, RebuildState.Idle),
        R(RebuildState.Idle, RebuildEvent.GamesWaiting, RebuildState.Waiting, RebuildAction.Queue),

        R(RebuildState.Waiting, RebuildEvent.DriverChanged, RebuildState.Waiting, RebuildAction.Queue),
        R(RebuildState.Waiting, RebuildEvent.GatesOpen, RebuildState.Running, RebuildAction.Start),
        R(RebuildState.Waiting, RebuildEvent.GatesClosed, RebuildState.Waiting),
        R(RebuildState.Waiting, RebuildEvent.AllReady, RebuildState.Done, RebuildAction.Finish),
        R(RebuildState.Waiting, RebuildEvent.Stalled, RebuildState.Done, RebuildAction.Finish),
        R(RebuildState.Waiting, RebuildEvent.GamesWaiting, RebuildState.Waiting),

        R(RebuildState.Running, RebuildEvent.DriverChanged, RebuildState.Waiting, RebuildAction.Pause | RebuildAction.Queue),
        R(RebuildState.Running, RebuildEvent.GatesOpen, RebuildState.Running, RebuildAction.Start),
        R(RebuildState.Running, RebuildEvent.GatesClosed, RebuildState.Paused, RebuildAction.Pause),
        R(RebuildState.Running, RebuildEvent.AllReady, RebuildState.Done, RebuildAction.Finish),
        R(RebuildState.Running, RebuildEvent.Stalled, RebuildState.Done, RebuildAction.Finish),
        R(RebuildState.Running, RebuildEvent.GamesWaiting, RebuildState.Running),

        R(RebuildState.Paused, RebuildEvent.DriverChanged, RebuildState.Waiting, RebuildAction.Queue),
        R(RebuildState.Paused, RebuildEvent.GatesOpen, RebuildState.Running, RebuildAction.Start),
        R(RebuildState.Paused, RebuildEvent.GatesClosed, RebuildState.Paused),
        R(RebuildState.Paused, RebuildEvent.AllReady, RebuildState.Done, RebuildAction.Finish),
        R(RebuildState.Paused, RebuildEvent.Stalled, RebuildState.Done, RebuildAction.Finish),
        R(RebuildState.Paused, RebuildEvent.GamesWaiting, RebuildState.Paused),

        R(RebuildState.Done, RebuildEvent.DriverChanged, RebuildState.Waiting, RebuildAction.Queue),
        R(RebuildState.Done, RebuildEvent.GatesOpen, RebuildState.Done),
        R(RebuildState.Done, RebuildEvent.GatesClosed, RebuildState.Done),
        R(RebuildState.Done, RebuildEvent.AllReady, RebuildState.Done),
        R(RebuildState.Done, RebuildEvent.Stalled, RebuildState.Done),
        R(RebuildState.Done, RebuildEvent.GamesWaiting, RebuildState.Waiting, RebuildAction.Queue),
    ];

    private static readonly Dictionary<(RebuildState, RebuildEvent), RebuildRow> Index = Table.ToDictionary(row => (row.State, row.Event));

    /// <summary>The row for a state and an event; every pair has one.</summary>
    internal static RebuildRow Row(RebuildState state, RebuildEvent ev) => Index[(state, ev)];

    /// <summary>Most played first, then most recently played, then by name: the game the player opens next is ready soonest.</summary>
    internal static IReadOnlyList<RebuildGame> Order(IEnumerable<RebuildGame> games) =>
        [.. games.OrderByDescending(game => game.PlayMinutes)
            .ThenByDescending(game => game.LastPlayed ?? DateTimeOffset.MinValue)
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)];

    private readonly object gate = new();
    private readonly Func<DateTime> clock = now ?? (() => DateTime.UtcNow);
    // The identity this run queued for, for a record that cannot be saved.
    private string? _queuedFor;
    private RebuildStatus _last = new("idle", 0, 0);

    /// <summary>The status changed (a state or a count): the page reads it again.</summary>
    internal event Action? Changed;

    /// <summary>What the page is told now, as of the last look.</summary>
    internal RebuildStatus Status { get { lock (gate) return _last; } }

    /// <summary>
    /// One look: a driver change that has stood (or one Exo's own install just finished: <paramref name="settled"/>) queues
    /// the games; then, with a queue, the gates and the games' state decide the rest. One look at a time.
    /// </summary>
    internal RebuildStatus Tick(bool settled = false)
    {
        if (!Monitor.TryEnter(gate)) return _last;
        RebuildStatus status;
        bool changed;
        try
        {
            status = Look(settled);
            changed = status != _last;
            _last = status;
        }
        finally { Monitor.Exit(gate); }
        if (changed) Changed?.Invoke();
        return status;
    }

    private RebuildStatus Look(bool settled)
    {
        var record = load();
        if (identity() is not { Length: > 0 } id) return StatusOf(record, null);
        if (string.IsNullOrEmpty(record.Driver))
        {
            // The first look on this PC only notes the driver: whatever is compiled was compiled for it or before Exo knew.
            record.Driver = id;
            save(record);
            return StatusOf(record, null);
        }
        if (id != record.Driver && id != _queuedFor)
        {
            if (installing()) return StatusOf(record, null);
            if (!settled && (record.Seen != id || record.SeenAt is not { } at || clock() < at))
            {
                record.Seen = id;
                record.SeenAt = clock();
                save(record);
                return StatusOf(record, null);
            }
            if (!settled && clock() - record.SeenAt!.Value < Settle) return StatusOf(record, null);
            Act(record, RebuildEvent.DriverChanged, id, null);
        }
        else if (record.Seen is not null)
        {
            record.Seen = null;
            record.SeenAt = null;
            save(record);
        }

        if (record.State is not (RebuildState.Waiting or RebuildState.Running or RebuildState.Paused))
        {
            // Nothing queued: an install or an update since the engine's last scan has it scan while nobody uses the PC (it
            // then lists the game as ready or updated), and games left to compile are queued.
            if (installed?.Invoke() is { Length: > 0 } games && games != record.Scanned && !installing() && idle() && !compiling() && scan?.Invoke() == true)
            {
                record.Scanned = games;
                save(record);
                return StatusOf(record, null);
            }
            if (Waiting(record).Count == 0) return StatusOf(record, null);
            Act(record, RebuildEvent.GamesWaiting, id, null);
        }
        var left = Left(record);
        record.Ready = record.Queue.Count - left.Count;
        if (left.Count == 0) Act(record, RebuildEvent.AllReady, id, left);
        else if (record.StalledStarts >= StallLimit) Act(record, RebuildEvent.Stalled, id, left);
        else Act(record, !installing() && idle() ? RebuildEvent.GatesOpen : RebuildEvent.GatesClosed, id, left);
        return StatusOf(record, left);
    }

    /// <summary>The games to compile that no run gave up on.</summary>
    private List<RebuildGame> Waiting(Record record) =>
        stale().Where(game => !record.GaveUp.Contains(game.EngineId, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>The queued engine ids that are still not compiled for the driver installed now, in queue order.</summary>
    private List<string> Left(Record record)
    {
        var still = stale().Select(game => game.EngineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return record.Queue.Where(still.Contains).ToList();
    }

    private void Act(Record record, RebuildEvent ev, string id, List<string>? left)
    {
        var state = record.State;
        var row = Row(state, ev);
        var action = row.Action;
        if (action == RebuildAction.None && row.Next == state) return;

        if (action.HasFlag(RebuildAction.Pause))
        {
            pause();
            if (!action.HasFlag(RebuildAction.Queue)) log("Shaders: the rebuild for the new driver waits while the PC is in use.");
        }
        if (action.HasFlag(RebuildAction.Queue))
        {
            if (ev == RebuildEvent.DriverChanged) record.GaveUp = [];
            var games = Order(Waiting(record));
            var was = record.Driver;
            record.Driver = id;
            record.Seen = null;
            record.SeenAt = null;
            record.Queue = games.Select(game => game.EngineId).ToList();
            record.StalledStarts = 0;
            record.ReadyAtStart = 0;
            record.Ready = 0;
            _queuedFor = id;
            var first = games.Count > 0 ? ", " + games[0].Title + " first" : "";
            log(ev == RebuildEvent.DriverChanged
                ? $"Shaders: driver {id} in place of {was ?? "none"}: {games.Count} game{(games.Count == 1 ? "" : "s")} queued to be compiled again{first}."
                : $"Shaders: {games.Count} new or updated game{(games.Count == 1 ? "" : "s")} queued to be compiled{first}.");
        }
        var next = row.Next;
        if (action.HasFlag(RebuildAction.Start) && !compiling())
        {
            var ready = record.Queue.Count - left!.Count;
            // The engine stopped by itself and no more games are ready than at the start before: that counts against the
            // run. A start after a wait or a pause does not (a long game is paused many times), and progress clears it.
            if (ready > record.ReadyAtStart) record.StalledStarts = 0;
            else if (state == RebuildState.Running) record.StalledStarts++;
            record.ReadyAtStart = ready;
            if (start(left))
            {
                if (state != RebuildState.Running) log($"Shaders: compiling for the new driver, {left.Count} game{(left.Count == 1 ? "" : "s")} left.");
            }
            else
            {
                // The engine could not be asked: wait and try at the next look, and count it.
                next = RebuildState.Waiting;
                record.StalledStarts++;
            }
        }
        if (action.HasFlag(RebuildAction.Finish))
        {
            var ready = record.Queue.Count - (left?.Count ?? 0);
            if (ev == RebuildEvent.Stalled) record.GaveUp = [.. record.GaveUp.Union(left ?? [], StringComparer.OrdinalIgnoreCase)];
            if (record.Queue.Count > 0)
                log(ev == RebuildEvent.Stalled
                    ? $"Shaders: {ready} of {record.Queue.Count} games are ready for the new driver; the rest did not compile after {StallLimit} tries, so Exo stops until the driver changes."
                    : $"Shaders: ready for the new driver, {ready} of {record.Queue.Count} games.");
        }
        record.State = next;
        if (save(record)) _queuedFor = null;
    }

    private static RebuildStatus StatusOf(Record record, List<string>? left)
    {
        var total = record.Queue.Count;
        // Nothing was queued: nothing to say, whatever the state.
        if (total == 0) return new("idle", 0, 0);
        if (left is not null) record.Ready = total - left.Count;
        return new(record.State.ToString().ToLowerInvariant(), Math.Clamp(record.Ready, 0, total), total);
    }

    // ------------------------------------------------------------------ the record on disk

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string Path => System.IO.Path.Combine(PathHelper.AppDataDir, "shader-rebuild.json");

    /// <summary>The record at <paramref name="path"/>; an empty one when there is none or it cannot be read.</summary>
    internal static Record LoadFrom(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json) ?? new Record() : new Record();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Record();
        }
    }

    /// <summary>Saves the record whole (a temporary file, then a replace); false when it could not be saved.</summary>
    internal static bool SaveTo(string path, Record record)
    {
        if (HostMode.IsDisposable) return false;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
