using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Optimize stays on. What the player turned on is kept (Keep's record, keep.json), and each time Exo looks while the game and
/// its launcher are closed (before a launch from Exo, as the game closes however it was started, however long it takes, and as
/// Exo starts) it reads every value Exo wrote back (<see cref="KeepEvidence"/>, one log line per value that moved) and decides
/// what the movement was by the cause rules of <see cref="KeepRules"/>:
/// - evidence of a reset (the game's build or content changed, the file's own mark of its defaults changed, Call of Duty's
///   marker was there as the session started, or the file was rebuilt) puts back every moved line, however few, worked out
///   again from the file as it is now, and never replays an old value; a group whose key went missing or whose allowed values
///   changed fails closed and waits;
/// - with none, a line is put back once and the value found is remembered; the same value found again is the player's and
///   stays (<see cref="KeepState.Yielded"/>); Optimize takes it back;
/// - the game's own cloud restoring its files is yielded to, never fought;
/// - put back three sessions running with evidence each time, the game keeps undoing it: refused, said once.
/// The groups kept on are written by the rules <see cref="Set"/> follows (only values the lines allow, never while the game, its
/// anti-cheat launcher or its helpers run, what each line held before Exo kept for Put back). A group turned off in Exo isn't
/// kept on, and Put back forgets it all. The game's page says so until the game next runs, a notice says so as it happens,
/// and app.log has the lines. The game's NVIDIA profile lives in the driver, not the game's files: an update leaves it, and a
/// driver install has its own restore (<see cref="RestoreNvidiaGames"/>).
/// The window mode chosen on the line goes further (<see cref="Hold"/>): those lines alone go back whatever moved them, the
/// game's menu too, as it closes, as Exo starts and just before a launch from Exo, until Put back. They are never the
/// player's.
/// </summary>
internal static partial class GameSettings
{
    /// <summary>
    /// What Optimize keeps on, as the page and the older code read it: the groups on; the lines that are the player's or the game's
    /// to keep (file key and line id: yielded and refused ones); each file's mark; the game's program and its write time when
    /// Exo last looked after the game ran; and when Exo last wrote again after an update (null once the game has run since).
    /// </summary>
    internal sealed record Keeping(List<string> Groups, List<string> Theirs, Dictionary<string, string> Marks, string? Program, DateTime? Built,
        DateTime? Again, bool Pending = false);

    /// <summary>A game's name, after Exo wrote its Optimize again because an update reset it.</summary>
    internal static event Action<string>? AppliedAgain;

    /// <summary>A game's name, after Exo put its window mode back: its page reads it again, with no notice.</summary>
    internal static event Action<string>? WindowModeHeld;

    /// <summary>No facts about this PC: enough to tell whether a game has a profile, not for its values.</summary>
    private static readonly Pc Unknown = new(null, null, null);

    /// <summary>The groups Optimize keeps on.</summary>
    internal static IReadOnlyCollection<string> KeptOn(Profile profile)
    {
        lock (Gate) return LoadKeeping(profile)?.Groups ?? [];
    }

    /// <summary>
    /// Whether the game, its anti-cheat launcher or a helper of its launcher runs: nothing is written then, and a look waits
    /// (<see cref="HelperRunning"/> says which helper it is). The launcher's helpers (<see cref="Profile.Helpers"/>) can run
    /// while the game does not, and a write as they start makes the game write its older values back at exit, which reads as a reset.
    /// </summary>
    internal static AsyncLocal<Func<Profile, bool>?> BusySeam { get; } = new();
    internal static bool Busy(Profile profile) => BusySeam.Value is { } seam ? seam(profile) : Running(profile) || HelperRunning(profile) is not null;


    /// <summary>
    /// Puts the window mode lines back at the mode chosen, once Optimize keeps their group on and with the game closed: those
    /// lines and no others, in the main file, by the usual rules. A short read, and a short write only when they left it, so
    /// it can run just before Play. One line in app.log when it writes, so a game that fights it shows there. True when it did.
    /// </summary>
    internal static bool Hold(Profile profile, string when)
    {
        if (profile.WindowLines is not { } names) return false;
        lock (Gate)
        {
            if (LoadKeeping(profile) is not { Pending: false } keeping || Busy(profile) || Read(profile) is not { } file) return false;
            var changes = profile.Groups(file).Where(group => keeping.Groups.Contains(group.Id)).SelectMany(group => group.Changes)
                .Where(change => names.Contains(change.Key)).ToList();
            if (changes.Count == 0) return false;
            var kept = KeptOf(profile.Id);
            var found = string.Join(' ', file.Settings.Where(line => names.Contains(line.Name)).Select(line => $"{line.Name}={line.Value}"));
            var writes = Edits([new OpenFile(profile.File, "", file)], [new Group("window", "", changes)], true, kept);
            if (writes.Count == 0 || Commit(profile, writes, kept) is not Committed.Written) return false;
            AppLog.Info($"Game settings: {profile.Title} {when}: its window mode had left {profile.WindowMode}, put back ({found}).");
            WindowModeHeld?.Invoke(profile.Title);
            return true;
        }
    }

    /// <summary>Whether Exo wrote the game's Optimize again after an update, and the game hasn't run since.</summary>
    internal static bool Again(Profile profile)
    {
        lock (Gate) return LoadKeeping(profile)?.Again is not null;
    }

    /// <summary>Optimize was pressed before the game had a settings file: it is written after the first time the game runs.</summary>
    internal static bool Waiting(Profile profile)
    {
        lock (Gate) return LoadKeeping(profile)?.Pending == true;
    }

    /// <summary>
    /// What a look did: whether it wrote, whether what it wrote was a reset put back (an update's, a marker's), and whether
    /// what Optimize keeps on could be saved (false: the look stops here and says so).
    /// </summary>
    private sealed record Outcome(bool Wrote, bool ResetWritten, bool Saved = true);

    /// <summary>The look at Exo's start (<paramref name="afterSession"/> false) or as a session ends.</summary>
    internal static bool KeepOn(Profile profile, string? program, bool afterSession) =>
        KeepOn(profile, program, afterSession, afterSession ? "after the game closed" : "as Exo started");

    /// <summary>
    /// Looks at a game's files while it, its launcher and its helpers are closed and keeps its Optimize on, by the cause rules
    /// above; <paramref name="when"/> names the moment in the log ("before launch" for a launch from Exo). After a session
    /// (<paramref name="afterSession"/>) the program and facts as they are now are what the next look compares with; at Exo's
    /// start and before a launch they aren't, since the game may not have run since its update. True when it wrote.
    /// </summary>
    internal static bool KeepOn(Profile profile, string? program, bool afterSession, string when)
    {
        var wrote = false;
        var reset = false;
        lock (Gate)
        {
            if (Busy(profile)) return false;
            // What the record says and what is kept, before the look's facts: a game that starts while the look reads its
            // files and facts still writes nothing, and leaves the record as it is (the checks in Check and Commit).
            var record = GameRecord(profile.Id);
            var keeping = record?.Keeping;
            var kept = KeptOf(profile.Id);
            if (keeping is null && kept.Count == 0) return false;
            // A file the game or an antivirus holds says nothing: nothing is changed, and the next look tries again.
            if (!TryReadAll(profile, out var files)) { AppLog.Info($"Game settings: {profile.Title}'s files couldn't be read just now, so nothing is changed."); return false; }
            if (files.Count == 0) return false;
            program ??= keeping?.Program;
            var built = ProgramTime(program);
            var gameFacts = profile.Facts?.Invoke();
            // A game or a helper that started while the files were read: nothing is judged and nothing is written, so the next
            // look still finds what this one would have (Read back is all that has happened).
            if (Busy(profile)) return false;
            // A record brought in from the old files: this first look with the game's own file fills in what they could not say
            // (each entry's group, each file's shape), so nothing here is judged a rebuild and a press can take a value back.
            if (FillOwners(profile, files))
            {
                record = GameRecord(profile.Id);
                keeping = record?.Keeping;
                kept = KeptOf(profile.Id);
            }
            var lastLook = record?.CheckedAt;
            // Only the log for now: ReadBack's write of the record is kept for once this look has passed every check that
            // returns early (below), so a look that stops leaves the record byte-identical and the cloud signal unspent.
            var seen = KeepEvidence.ReadBack(profile, files, program, when, save: false).Seen;
            // And again after it: a game that started as the values were read has the file open and writes it as it closes.
            if (Busy(profile)) return false;
            var pendingWrites = new List<(OpenFile Open, Dictionary<int, string> Edits)>();
            if (keeping?.Pending == true)
            {
                var pendingGroups = profile.Groups(files[0].File).Where(g => keeping.Groups.Contains("*") || keeping.Groups.Contains(g.Id)).ToList();
                pendingWrites = Edits(files, pendingGroups, true, kept);
                // The one check directly before the first file is written: a game or a helper that started as the values were
                // read behaves as one that was running all along — nothing written, and the record as it was.
                if (pendingWrites.Count > 0 && Busy(profile)) return false;
            }
            if (keeping?.Pending == true)
            {
                var pendingGroups = profile.Groups(files[0].File).Where(g => keeping.Groups.Contains("*") || keeping.Groups.Contains(g.Id)).ToList();
                if (Commit(profile, pendingWrites, kept) is not Committed.Written) return false;
                // Past the last check that could stop this look: only now does the record hold what was read back.
                KeepEvidence.SaveSeen(profile, files, program, seen);
                wrote = pendingWrites.Count > 0;
                var ids = pendingGroups.Select(g => g.Id).ToList();
                if (!SaveKeeping(profile, wrote ? ReadAll(profile) : files, ids, program, built, null, gameFacts))
                {
                    AppLog.Warn($"Game settings: {profile.Title}: what Optimize keeps on couldn't be saved, so the look stops here.");
                    return false;
                }
                NoteOptimizePressed(profile, ids, all: true, clearSignals: afterSession || wrote);
                if (wrote)
                    AppLog.Info($"Game settings: {profile.Title}: Optimize written after the first time it ran, {pendingWrites.Sum(w => w.Edits.Count)} lines in {pendingWrites.Count} files.");
            }
            else if (keeping is null)
            {
                // Optimized before Exo kept it on (3.17.0 and 3.17.1 kept only the lines): every group, as Optimize turned them on.
                if (!SaveKeeping(profile, files, [.. profile.Groups(files[0].File).Select(g => g.Id)], program, built, null, gameFacts))
                {
                    AppLog.Warn($"Game settings: {profile.Title}: what Optimize keeps on couldn't be saved, so the look stops here.");
                    return false;
                }
                // A line that is not Exo's as this begins is the player's: nothing says otherwise.
                var now = files.SelectMany(open => open.File.Settings.Select(line => (Key: open.Key + line.Id, line.Value))).GroupBy(pair => pair.Key)
                    .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
                // Past the check that could stop this look, and before the only write: the record holds what was read back.
                KeepEvidence.SaveSeen(profile, files, program, seen);
                Keep.Update(doc =>
                {
                    if (!doc.Games.TryGetValue(profile.Id, out var game)) return;
                    foreach (var (key, entry) in game.Entries)
                        if (now.TryGetValue(key, out var value) && value != entry.Intended) entry.Go(KeepEvent.DriftAgain, value, "found not Exo's when Exo began to keep it on");
                });
                return false;
            }
            else
            {
                var outcome = Check(profile, record!, files, program, built, gameFacts, afterSession, lastLook, seen);
                if (!outcome.Saved) return false;
                wrote = outcome.Wrote;
                reset = outcome.ResetWritten;
            }
        }
        // After an update's rewrite, which says so: what's left of the window mode goes back by itself.
        Hold(profile, afterSession ? "as it closed" : when == "before launch" ? "before launch" : "as Exo started");
        if (reset) AppliedAgain?.Invoke(profile.Title);
        return wrote;
    }

    /// <summary>One value found moved, with what decides it.</summary>
    private sealed record Seen(string Key, Keep.KeepEntry Entry, string Found, string FileKey);

    /// <summary>
    /// The look itself: every entry of the game judged by <see cref="KeepRules"/>, what must be written written, the record
    /// kept. <paramref name="lastLook"/> is when the look before this one read the values back, which the cloud signal is
    /// read against (the record's own is set by <see cref="KeepEvidence.SaveSeen"/>, once this look has judged).
    /// <paramref name="seen"/> is what <see cref="KeepEvidence.ReadBack"/> found. Under the gate.
    /// </summary>
    private static Outcome Check(Profile profile, Keep.KeepGame record, List<OpenFile> files, string? program, DateTime? built,
        IReadOnlyDictionary<string, string>? gameFacts, bool afterSession, DateTime? lastLook, KeepEvidence.Seen seen)
    {
        var keeping = record.Keeping!;
        var facts = gameFacts ?? new Dictionary<string, string>();
        var kept = KeptOf(profile.Id);
        var updated = (built is not null && keeping.Built is not null && built != keeping.Built)
            || facts.Any(fact => keeping.Facts.TryGetValue(fact.Key, out var was) && was != fact.Value);
        var allGroups = profile.Groups(files[0].File).ToList();
        var groupsOn = allGroups.Where(g => keeping.Groups.Contains(g.Id)).ToList();
        var windowLines = profile.WindowLines ?? [];
        var marker = record.Signals.Contains("marker");
        var cloud = profile.CloudChanged?.Invoke(files, lastLook, afterSession) == true;

        var current = new Dictionary<string, (Line Line, OpenFile Open)>(StringComparer.Ordinal);
        foreach (var open in files)
            foreach (var line in open.File.Settings) current.TryAdd(open.Key + line.Id, (line, open));
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in allGroups)
            foreach (var open in files)
                foreach (var (line, value) in Targets(open.File, group)) wanted[open.Key + line.Id] = value;

        var markChanged = files.Where(open => profile.Mark?.Invoke(open.File) is { } mark && keeping.Marks.TryGetValue(open.Key, out var was) && mark != was)
            .Select(open => open.Key).ToHashSet(StringComparer.Ordinal);
        var shapeChanged = files.Where(open => record.Shapes.TryGetValue(open.Key, out var was) && was != ShapeOf(open.File))
            .Select(open => open.Key).ToHashSet(StringComparer.Ordinal);
        bool FingerprintOf(string fileKey) => updated || markChanged.Contains(fileKey);
        static string FileKeyOf(string key) => key.IndexOf('|') is var bar and >= 0 ? key[..(bar + 1)] : "";

        // A group fails closed when, after an update or while it waits, a line Exo wrote in it is gone or no longer takes its value.
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var byGroup in record.Entries.Where(pair => !windowLines.Contains(LineNameOf(pair.Key))).GroupBy(pair => GroupOf(pair.Value)))
        {
            if (!byGroup.Any(pair => FingerprintOf(FileKeyOf(pair.Key)) || pair.Value.State == KeepState.Waiting)) continue;
            var asked = allGroups.FirstOrDefault(g => g.Id == byGroup.Key)?.Changes.Select(change => change.Key).ToHashSet(StringComparer.Ordinal) ?? [];
            if (byGroup.Any(pair => !current.ContainsKey(pair.Key) || (asked.Contains(LineNameOf(pair.Key)) && !wanted.ContainsKey(pair.Key)))) closed.Add(byGroup.Key);
        }

        var events = new List<(string Key, KeepEvent Event, string? Seen, string Cause)>();
        var moved = new List<Seen>();
        foreach (var (key, entry) in record.Entries)
        {
            if (closed.Contains(GroupOf(entry))) { events.Add((key, KeepEvent.GroupFailsClosed, null, "a line it wrote is gone or no longer takes its value after an update")); continue; }
            if (!current.TryGetValue(key, out var found)) { events.Add((key, KeepEvent.LineGone, null, "the file no longer holds the line")); continue; }
            if (found.Line.Value == entry.Intended) events.Add((key, KeepEvent.SeenHeld, found.Line.Value, "held"));
            else if (entry.State is not (KeepState.Yielded or KeepState.Refused)) moved.Add(new Seen(key, entry, found.Line.Value, FileKeyOf(key)));
        }

        // The verdict on each moved line, with the evidence around its file.
        var decided = new Dictionary<string, KeepRules.Verdict>(StringComparer.Ordinal);
        var causes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var byFile in moved.GroupBy(seen => seen.FileKey))
        {
            var rebuilt = KeepRules.Rebuilt(shapeChanged.Contains(byFile.Key), [.. byFile.Select(seen => (seen.Found, seen.Entry.Before))]);
            var signals = new KeepRules.Signals(FingerprintOf(byFile.Key), marker, rebuilt, cloud);
            var why = string.Join(", ", new[]
            {
                signals.Fingerprint ? (updated ? "the game's build or content changed" : "the file's own mark of its defaults changed") : null,
                signals.Marker ? "a safe-mode marker was there as the session started" : null,
                signals.Rebuilt ? "the file was rebuilt" : null,
                signals.Cloud ? "the game's cloud wrote its files" : null,
            }.Where(text => text is not null));
            foreach (var seenLine in byFile)
            {
                // The window mode is held by <see cref="Hold"/>, whatever moved it: only evidence of a reset puts it back here too.
                if (windowLines.Contains(LineNameOf(seenLine.Key)) && !signals.Evidence) continue;
                decided[seenLine.Key] = KeepRules.Decide(seenLine.Entry.Intended, seenLine.Found, seenLine.Entry.Found, signals, seenLine.Entry.ResetStreak);
                causes[seenLine.Key] = why.Length > 0 ? why : "no evidence of a reset";
            }
        }
        foreach (var seenLine in moved.Where(seen => decided.ContainsKey(seen.Key))) events.Add((seenLine.Key, KeepRules.EventOf(decided[seenLine.Key]), seenLine.Found, causes[seenLine.Key]));

        // What must be written: the table says which events write. Evidence puts back the whole file worked out again; none, only the moved lines.
        KeepTable.Row RowOf(string key, KeepEvent ev) => KeepTable.Step(record.Entries[key].State, ev);
        var resetKeys = events.Where(e => e.Event == KeepEvent.Reset && RowOf(e.Key, e.Event).Action == KeepAction.Write).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var driftKeys = events.Where(e => RowOf(e.Key, e.Event).Action == KeepAction.WriteAndRemember).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var theirs = record.Entries.Where(pair => pair.Value.State is KeepState.Yielded or KeepState.Refused).Select(pair => pair.Key)
            .Concat(events.Where(e => e.Event is KeepEvent.DriftAgain or KeepEvent.CloudRestore or KeepEvent.BackOff or KeepEvent.GroupFailsClosed or KeepEvent.LineGone).Select(e => e.Key))
            .ToHashSet(StringComparer.Ordinal);
        var writable = groupsOn.Where(group => !closed.Contains(group.Id)).ToList();
        var resetFiles = resetKeys.Select(FileKeyOf).ToHashSet(StringComparer.Ordinal);
        var writes = Edits(files.Where(open => resetFiles.Contains(open.Key)), writable, true, kept, theirs);
        writes.AddRange(Edits(files.Where(open => !resetFiles.Contains(open.Key) && driftKeys.Any(key => FileKeyOf(key) == open.Key)), writable, true, kept, theirs, only: driftKeys));

        if (writes.Count > 0 && Busy(profile))
        {
            AppLog.Info($"Game settings: {profile.Title} or its launcher started as Exo was about to write, so nothing is changed.");
            return new Outcome(false, false);
        }
        var committed = writes.Count > 0 ? Commit(profile, writes, kept) : Committed.Written;
        // Refused at the last moment (a helper started as the values were worked out): nothing was written, so the record is
        // left as it is — no state moves, no streak changes, no signal is used up — and the next look judges it all again.
        if (committed is Committed.Busy) return new Outcome(false, false);
        var failed = committed is Committed.Failed;
        KeepEvidence.SaveSeen(profile, files, program, seen);
        var written = failed ? [] : writes.SelectMany(w => w.Edits.Keys.Select(index => w.Open.Key + w.Open.File.Settings.First(line => line.Index == index).Id)).ToHashSet(StringComparer.Ordinal);
        var resetWritten = !failed && resetKeys.Overlaps(written);

        Keep.Update(doc =>
        {
            var game = doc.Games[profile.Id];
            foreach (var (key, ev, seenValue, cause) in events)
            {
                if (!game.Entries.TryGetValue(key, out var entry)) continue;
                var putting = KeepTable.Step(entry.State, ev).Action is KeepAction.Write or KeepAction.WriteAndRemember;
                entry.Go(ev, seenValue, cause);
                if (putting && !written.Contains(key))
                {
                    // Asked to be put back and not written: the file is locked (undone, tried again), or no longer takes Exo's value (skipped).
                    entry.Go(failed ? KeepEvent.PutBackFailed : KeepEvent.LineGone, seenValue, failed ? "the file could not be written" : "the file no longer takes Exo's value");
                    continue;
                }
                if (ev == KeepEvent.Drift) entry.Found = seenValue;
                if (putting && entry.ReadBack != entry.Intended) entry.Go(KeepEvent.PutBackRefused, entry.ReadBack, "a fresh read of the file says something else");
            }
            if (afterSession)
            {
                // A marker is used up by the session it explains: one whose put back was written, or one in which nothing
                // moved and no group failed closed. A write that failed keeps it, and so does a group that fails closed or
                // waits, so the look that re-opens it still knows why the values moved.
                if (writes.Count > 0
                        ? !failed
                        : moved.Count == 0 && closed.Count == 0 && record.Entries.Values.All(entry => entry.State != KeepState.Waiting))
                    game.Signals = [];
                // Only sessions running in which this one value was put back with reset evidence count towards its back off.
                foreach (var (key, entry) in game.Entries)
                    entry.ResetStreak = resetKeys.Contains(key) && written.Contains(key) ? entry.ResetStreak + 1 : 0;
            }
            if (events.Any(e => e.Event == KeepEvent.BackOff) && !game.RefusedSaid)
            {
                game.RefusedSaid = true;
                AppLog.Info($"Game settings: {profile.Title}: Exo put its settings back {KeepRules.Sessions} sessions running and the game undid them each time, so Exo stops and says so.");
            }
        });

        foreach (var yielded in events.Where(e => e.Event is KeepEvent.DriftAgain or KeepEvent.CloudRestore))
            AppLog.Info($"Game settings: {profile.Title}: {LineNameOf(yielded.Key)} found {yielded.Seen} " +
                $"{(yielded.Event == KeepEvent.CloudRestore ? "after the game's cloud wrote its files, so Exo yields to the cloud and leaves it" : "again, so it is the player's now and Exo leaves it")}.");
        foreach (var group in closed)
            AppLog.Info($"Game settings: {profile.Title}: after the update a line in '{group}' is gone or no longer takes Exo's value, so that group waits for the game's first run and nothing from before is written.");
        var wroteAny = !failed && writes.Count > 0;
        if (wroteAny)
            AppLog.Info($"Game settings: {profile.Title}: put back {writes.Sum(w => w.Edits.Count)} lines in {writes.Count} files " +
                $"({string.Join("; ", events.Where(e => e.Event is KeepEvent.Reset or KeepEvent.Drift).Select(e => causes.GetValueOrDefault(e.Key, "")).Distinct())}).");

        var keptOnRecord = SaveKeeping(profile, wroteAny ? ReadAll(profile) : files, keeping.Groups, program, afterSession || wroteAny ? built : keeping.Built,
            resetWritten ? DateTime.UtcNow : afterSession ? null : keeping.Again, afterSession || wroteAny ? facts : keeping.Facts);
        if (!keptOnRecord)
        {
            AppLog.Warn($"Game settings: {profile.Title}: what Optimize keeps on couldn't be saved, so the look stops here.");
            return new Outcome(wroteAny, resetWritten, Saved: false);
        }
        return new Outcome(wroteAny, resetWritten);
    }

    /// <summary>
    /// Keeps every pressed Optimize on: after a library game closes (however it started), just before a launch from Exo, and once
    /// as Exo starts. Every value Exo wrote is read back and logged each time, with the window mode put back just before a launch.
    /// </summary>
    internal static void StartKeeping()
    {
        App.Services.Launcher.GameRunStateChanged += (gameId, state) =>
        {
            // The look waits for the game to be gone: a game that starts again ends the wait, and the next exit starts one.
            var waiting = new CancellationTokenSource();
            if (Waiters.TryRemove(gameId, out var previous)) { previous.Cancel(); previous.Dispose(); }
            if (state.IsRunning) { waiting.Dispose(); return; }
            Waiters[gameId] = waiting;
            if (App.Services.Library.Find(gameId) is { } game) _ = Task.Run(() => KeepAfterAsync(game, waiting.Token));
        };
        App.Services.Launcher.GameLaunching += game =>
        {
            try
            {
                if (For(game, Unknown) is { } profile) Hold(profile, "before launch");
                KeepBeforeLaunch(game);
            }
            catch (Exception ex) { AppLog.Warn($"Game settings: keeping {game.Title}'s Optimize before its launch failed: {ex.GetType().Name}"); }
        };
        _ = Task.Run(KeepAllAsync);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> Waiters = new(StringComparer.Ordinal);

    /// <summary>
    /// Just before a launch from Exo: everything Exo wrote in this game's files is read back and logged, and what the cause rules
    /// say to put back is put back, with the game, its launcher and its helpers closed (one that is starting is not touched).
    /// </summary>
    private static void KeepBeforeLaunch(GameEntry game)
    {
        if (Pressed(game) is null || For(game, RecentPc()) is not { } profile) return;
        NoteGame(profile.Id, game.Id);
        KeepOn(profile, profile.Program?.Invoke(game), afterSession: false, "before launch");
    }

    private static Pc? _recentPc;
    private static DateTime _recentPcAt;

    /// <summary>This PC's facts, no older than half a minute: a launch must not wait on reading the graphics card again.</summary>
    internal static Pc RecentPc()
    {
        lock (Gate)
        {
            if (_recentPc is null || DateTime.UtcNow - _recentPcAt > TimeSpan.FromSeconds(30))
            {
                _recentPc = ThisPc();
                _recentPcAt = DateTime.UtcNow;
            }
            return _recentPc;
        }
    }

    /// <summary>A game's profile when it has one and its Optimize was pressed, without reading this PC.</summary>
    private static Profile? Pressed(GameEntry game)
    {
        if (For(game, Unknown) is not { } profile) return null;
        lock (Gate) return GameRecord(profile.Id) is { } record && (record.Keeping is not null || record.Entries.Count > 0) ? profile : null;
    }

    private static async Task KeepAfterAsync(GameEntry game, CancellationToken cancel)
    {
        try
        {
            if (Pressed(game) is null || For(game, ThisPc()) is not { } profile) return;
            NoteGame(profile.Id, game.Id);
            // The game writes its file as it closes, and its anti-cheat launcher and helpers can outlast it: look again every few
            // seconds for as long as it takes, with no time limit, until all are gone (or the game starts again).
            if (await KeepEvidence.UntilFreeAsync(() => Busy(profile), TimeSpan.FromSeconds(5), Task.Delay, cancel,
                    () => HelperRunning(profile), profile.Title).ConfigureAwait(false))
                KeepOn(profile, profile.Program?.Invoke(game), afterSession: true);
        }
        catch (Exception ex) { AppLog.Warn($"Game settings: keeping {game.Title}'s Optimize on failed: {ex.GetType().Name}"); }
    }

    private static async Task KeepAllAsync()
    {
        try
        {
            // After the start's own work: a game that is running is looked at as it closes.
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var library = await App.Services.Library.GetLibraryAsync().ConfigureAwait(false);
            Pc? pc = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var game in library)
                if (Pressed(game) is { } pressed && seen.Add(pressed.Id) && For(game, pc ??= ThisPc()) is { } profile)
                    KeepOn(profile, profile.Program?.Invoke(game), afterSession: false);
            // After the looks, which can bring a record in from the old files: every library entry this record belongs to is
            // noted. One update for all of it, and nothing is dropped for not being in the library, which can be empty here.
            NoteLibrary(library);
        }
        catch (Exception ex) { AppLog.Warn($"Game settings: keeping Optimize on as Exo starts failed: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// The start-up look's note of the library in the records: every library entry whose profile has a record is noted for it
    /// (a game the library lists twice is noted under both ids, so the page's status is told under both). It only adds, for an
    /// id the record does not hold yet: the library at start can be empty or partial (a first run, a games drive not
    /// connected, a store that did not answer), so no id is ever dropped for not being in it. One update, and none at all
    /// when nothing is missing.
    /// </summary>
    internal static void NoteLibrary(IReadOnlyList<GameEntry> library)
    {
        var noted = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var game in library)
            if (For(game, Unknown) is { } profile)
                (noted.TryGetValue(profile.Id, out var list) ? list : noted[profile.Id] = []).Add(game.Id);
        lock (Gate)
        {
            var doc = Keep.Load();
            var missing = noted.Any(pair => doc.Games.TryGetValue(pair.Key, out var game)
                && pair.Value.Any(id => !game.GameIds.Contains(id, StringComparer.Ordinal)));
            if (!missing) return;
            Keep.Update(saved =>
            {
                foreach (var (profileId, gameIds) in noted)
                    if (saved.Games.TryGetValue(profileId, out var game))
                        foreach (var gameId in gameIds)
                            if (!game.GameIds.Contains(gameId, StringComparer.Ordinal)) game.GameIds.Add(gameId);
            });
        }
    }

    private static DateTime? ProgramTime(string? program)
    {
        try { return program is not null && File.Exists(program) ? File.GetLastWriteTimeUtc(program) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
