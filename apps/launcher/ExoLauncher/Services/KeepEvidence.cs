using System.Globalization;
using ExoLauncher.Helpers;
using static ExoLauncher.Services.GameSettings;

namespace ExoLauncher.Services;

/// <summary>
/// The evidence for what moves: after every write Exo keeps what it wrote (<see cref="Keep.KeepEntry"/>, read back at once), and
/// before a launch from Exo, after the game and its launcher have exited, and as Exo starts it reads every value back and
/// logs one line per value that moved as "file: line: Exo's X -> now Y", with when it was written, when it was last seen
/// held, and what else changed since (game build, driver, Windows build, screens, default audio device, the file's time).
/// A target Exo works out from the PC at the moment of a press (the screen count, the default audio device, the file's own
/// resolution or refresh rate) is recorded with the inputs it was worked out from, so "Exo's own target moved" is told apart
/// from "the file moved" and is never counted as a drift.
/// Ways it could fail: a line reads as moved because Exo's own target changed; a value is logged without its file (two
/// accounts' files hold the same line); the log names an account's id; a game still running is read mid-write; the
/// evidence replaces what undoes (it only reads, and the undo data is the game's, kept elsewhere).
/// </summary>
internal static class KeepEvidence
{
    /// <summary>One value that moved: its file and line, what Exo wrote, what it reads now, and the line's key in the record.</summary>
    internal sealed record Moved(string Key, string File, string Line, string Exos, string Now);

    /// <summary>Every sentence this writes, as it logs it (tests listen; the app's log gets the same).</summary>
    internal static event Action<string>? Logged;

    private static void Say(string line)
    {
        AppLog.Info(line);
        Logged?.Invoke(line);
    }

    private static void Warn(string line)
    {
        AppLog.Warn(line);
        Logged?.Invoke(line);
    }

    /// <summary>The name of a file, without the account folder, for the log.</summary>
    private static string FileOf(string key) => key.IndexOf('|') is var bar and >= 0 && key[..bar].Split('/') is [.., var name] ? name : "";

    private static string LineOf(string key) => key[(key.IndexOf('|') + 1)..];

    /// <summary>What the target values were worked out from: the PC's facts and the main file's lines the targets follow (the groups are picked from it).</summary>
    internal static string InputsOf(Profile profile, SettingsFile file)
    {
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in profile.Inputs ?? new Dictionary<string, string>()) inputs[name] = value;
        foreach (var name in profile.InputLines ?? [])
            if (file.Settings.FirstOrDefault(line => line.Name == name) is { } line) inputs[name] = line.Value;
        return string.Join(';', inputs.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static Dictionary<string, string> Parse(string? inputs) => (inputs ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2).GroupBy(parts => parts[0]).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);

    /// <summary>
    /// What a write changed, kept: each edited line that Exo now keeps (it is in <paramref name="kept"/>) becomes an entry with
    /// what Exo set, what it held before, the inputs its target came from and a fresh read of the file; an edit that put a
    /// value back (it is no longer kept) removes its entry. Called after the file was written.
    /// </summary>
    internal static void Record(Profile profile, IReadOnlyList<(OpenFile Open, Dictionary<int, string> Edits)> writes, IReadOnlyDictionary<string, Remembered> kept)
    {
        if (writes.Count == 0) return;
        var now = DateTime.UtcNow;
        var fresh = ReadAll(profile).ToDictionary(open => open.Key, open => open.File, StringComparer.Ordinal);
        var groups = fresh.TryGetValue("", out var mainFile) ? GroupsByLine(profile, mainFile) : [];
        Keep.Update(doc =>
        {
            var game = doc.Games.TryGetValue(profile.Id, out var existing) ? existing : doc.Games[profile.Id] = new Keep.KeepGame();
            foreach (var (open, edits) in writes)
            {
                var inputs = InputsOf(profile, fresh.GetValueOrDefault("") ?? open.File);
                foreach (var line in open.File.Settings.Where(line => edits.ContainsKey(line.Index)))
                {
                    var key = open.Key + line.Id;
                    if (!kept.TryGetValue(key, out var remembered)) { game.Entries.Remove(key); continue; }
                    var readBack = fresh.TryGetValue(open.Key, out var file) ? file.Settings.FirstOrDefault(l => l.Id == line.Id)?.Value : null;
                    if (!game.Entries.TryGetValue(key, out var entry)) game.Entries[key] = entry = new Keep.KeepEntry { Target = key };
                    entry.Owner = profile.Id + "/" + groups.GetValueOrDefault(line.Name, "");
                    entry.Intended = edits[line.Index];
                    entry.Before = remembered.Was;
                    entry.WrittenAt = now;
                    entry.ExoVersion = Keep.ExoVersion;
                    entry.ReadBack = readBack;
                    entry.ReadBackAt = now;
                    entry.Inputs = inputs;
                    entry.LastSeen = readBack;
                    entry.LastCheckedAt = now;
                    entry.Found = null;
                    // A write the file did not take is never shown as held.
                    if (readBack != edits[line.Index])
                    {
                        entry.Go(KeepEvent.PressedRefused, readBack, "a fresh read of the file says something else");
                        Warn($"Keep: {profile.Title}: {FileOf(key)}: {LineOf(key)}: Exo wrote {edits[line.Index]} and a fresh read of the file says {readBack ?? "nothing"}.");
                    }
                }
            }
            game.Context = new Dictionary<string, string>(KeepContext.Now(profile, null), StringComparer.Ordinal);
            game.HeldAt = now;
            game.CheckedAt = now;
        });
    }

    /// <summary>
    /// Reads every value Exo wrote in this game's files back, logs one line for each that moved and one for each target
    /// that Exo's own inputs moved, and keeps what it saw. <paramref name="when"/> says the moment ("before launch", "after the
    /// game closed", "as Exo started"). Changes nothing in the game's files. Returns what moved. With
    /// <paramref name="save"/> false it only reads and logs: the record is not touched, and <see cref="SaveSeen"/> does it
    /// for a caller that has passed every check that would leave the look half done.
    /// </summary>
    internal static ReadBackResult ReadBack(Profile profile, IReadOnlyList<OpenFile> files, string? program, string when, bool save = true)
    {
        var moved = new List<Moved>();
        if (files.Count == 0) return new ReadBackResult(moved, new Seen(when, new Dictionary<string, string>(), new Dictionary<string, string?>(), false, DateTime.UtcNow));
        var doc = Keep.Load();
        if (!doc.Games.TryGetValue(profile.Id, out var game) || game.Entries.Count == 0)
            return new ReadBackResult(moved, new Seen(when, new Dictionary<string, string>(), new Dictionary<string, string?>(), false, DateTime.UtcNow));

        var current = new Dictionary<string, (Line Line, OpenFile Open)>(StringComparer.Ordinal);
        foreach (var open in files)
            foreach (var line in open.File.Settings) current.TryAdd(open.Key + line.Id, (line, open));

        // What Exo would ask for now, worked out from the file and this PC as they are.
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in profile.Groups(files[0].File))
            foreach (var open in files)
                foreach (var (line, value) in Targets(open.File, group)) wanted[open.Key + line.Id] = value;

        var facts = KeepContext.Now(profile, program);
        var changed = game.Context.Count == 0 ? "no earlier look to compare with" : KeepContext.Diff(game.Context, facts);
        var then = game.HeldAt is { } held ? held.ToString("u", CultureInfo.InvariantCulture) : "never";
        var inputsNow = new Dictionary<string, string>();
        var targetMoves = new List<string>();
        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, entry) in game.Entries)
        {
            if (!current.TryGetValue(key, out var found))
            {
                seen[key] = null;
                continue;
            }
            seen[key] = found.Line.Value;
            if (found.Line.Value != entry.Intended)
                moved.Add(new Moved(key, FileOf(key), LineOf(key), entry.Intended, found.Line.Value));
            else if (!wanted.TryGetValue(key, out var target) || target != entry.Intended)
            {
                inputsNow = Parse(InputsOf(profile, files[0].File));
                targetMoves.Add($"Keep: {profile.Title} {when}: {(FileOf(key).Length > 0 ? FileOf(key) + ": " : "")}{LineOf(key)}: Exo's own target moved " +
                    $"({entry.Intended} -> {(target is null ? "no longer asked for" : target)}), the file did not; worked out from {Describe(Parse(entry.Inputs), inputsNow)}.");
            }
        }
        foreach (var line in targetMoves) Say(line);
        foreach (var move in moved)
        {
            var target = wanted.TryGetValue(move.Key, out var w) && w != move.Exos ? $"; Exo's own target is now {w} ({Describe(Parse(game.Entries[move.Key].Inputs), Parse(InputsOf(profile, files[0].File)))})" : "";
            Say($"Keep: {profile.Title} {when}: {(move.File.Length > 0 ? move.File + ": " : "")}{move.Line}: Exo's {move.Exos} -> now {move.Now} " +
                $"(written {game.Entries[move.Key].WrittenAt:u}, last seen held {then}; since: {changed}{target}).");
        }
        var looked = new Seen(when, facts, seen, moved.Count == 0 && seen.Values.All(value => value is not null), DateTime.UtcNow);
        if (save) Save(profile, looked);
        return new ReadBackResult(moved, looked);
    }

    /// <summary>What a read back found: the values that moved, and what it saw (for <see cref="SaveSeen"/>).</summary>
    internal sealed record ReadBackResult(List<Moved> Moved, Seen Seen);

    /// <summary>What one read of a game's values saw: the values by key (null for a line the file no longer holds), and whether every one of them was found.</summary>
    internal sealed record Seen(string When, IReadOnlyDictionary<string, string> Facts, IReadOnlyDictionary<string, string?> Values, bool AllFound, DateTime At);

    /// <summary>
    /// Keeps what <see cref="ReadBack"/> saw, for a look that has passed every check: each entry's last seen value and when,
    /// and the record's own <see cref="Keep.KeepGame.CheckedAt"/> (its <see cref="Keep.KeepGame.Context"/> and
    /// <see cref="Keep.KeepGame.HeldAt"/> only when the whole file held every value).
    /// </summary>
    internal static void SaveSeen(Profile profile, IReadOnlyList<OpenFile> files, string? program, Seen seen)
    {
        if (files.Count == 0) return;
        Save(profile, seen);
    }

    private static void Save(Profile profile, Seen seen)
    {
        Keep.Update(saved =>
        {
            if (!saved.Games.TryGetValue(profile.Id, out var stored)) return;
            foreach (var (key, value) in seen.Values)
                if (stored.Entries.TryGetValue(key, out var entry)) { entry.LastSeen = value; entry.LastCheckedAt = seen.At; }
            stored.CheckedAt = seen.At;
            if (seen.AllFound)
            {
                stored.Context = new Dictionary<string, string>(seen.Facts, StringComparer.Ordinal);
                stored.HeldAt = seen.At;
            }
        });
    }

    /// <summary>"screens 2 -> 1" for each input that differs; "the same inputs" when none does.</summary>
    private static string Describe(Dictionary<string, string> then, Dictionary<string, string> now)
    {
        var changes = now.Where(pair => then.TryGetValue(pair.Key, out var was) && was != pair.Value).Select(pair => $"{pair.Key} {then[pair.Key]} -> {pair.Value}").ToList();
        return changes.Count == 0 ? "inputs that are the same as when it was written" : "inputs that changed: " + string.Join(", ", changes);
    }

    /// <summary>
    /// Waits until <paramref name="busy"/> says no (the game and its launcher have both exited), looking again every
    /// <paramref name="interval"/> with no time limit, and false when <paramref name="cancel"/> ends the wait first (the game
    /// started again, or Exo is closing). A game that outlasts a minute is still looked at once it is gone. A launcher helper
    /// that outlasts the game is said once, and once more after ten minutes (by its process name only); from then on it goes on
    /// looking every thirty seconds rather than every five.
    /// </summary>
    internal static async Task<bool> UntilFreeAsync(Func<bool> busy, TimeSpan interval, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancel,
        Func<string?>? helper = null, string title = "")
    {
        var waited = TimeSpan.Zero;
        string? said = null, again = null;
        var look = interval;
        while (!cancel.IsCancellationRequested)
        {
            try { await delay(look, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            if (helper?.Invoke() is { } now && now != said)
            {
                said = now;
                Say($"Keep: {title}: waiting for its launcher helper {now} to close before looking at its settings.");
            }
            if (said is { } waiting && again is null && waited >= TimeSpan.FromMinutes(10))
            {
                again = waiting;
                Say($"Keep: {title}: still waiting on {waiting} after 10 minutes; Exo keeps looking every 30 seconds, and looks at its settings once it is gone.");
            }
            if (!busy()) return !cancel.IsCancellationRequested;
            waited += look;
            if (waited >= TimeSpan.FromMinutes(10)) look = TimeSpan.FromSeconds(30);
        }
        return false;
    }
}
