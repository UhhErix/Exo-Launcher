using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Keep: the one record of what Exo wrote into the PC, so it can be read back, watched and put back (docs/architecture/keep.md).
/// This file is the record and its store: keep.json in Exo's data folder, per PC (it goes into the backup, and is never merged
/// into another PC by sync: paths and device ids mean nothing there). Each entry is one value Exo wrote: what it intended, what
/// it replaced (<see cref="KeepEntry.Before"/>, which Put back uses), what a fresh read said right after, and what Keep has
/// seen since. The state of an entry moves only by <see cref="KeepTable"/>.
/// Ways it could fail, each with a test: a half-written file or an empty one drops every other game's undo data (saved
/// beside, flushed, then swapped in with a copy of the last good one kept; an unreadable file is set aside, never read as
/// "nothing kept"; one another program holds is neither set aside nor saved over); an entry is lost in a migration; two
/// writers lose each other's change (one lock); a newer Exo's record is read and never saved over.
/// </summary>
internal static class Keep
{
    internal const int Version = 1;

    private static readonly Lock Gate = new();

    /// <summary>A record written by a newer Exo was found and said so already.</summary>
    private static bool NewerSaid;

    private static readonly AsyncLocal<string?> ForcedPath = new();

    /// <summary>Tests only: the record lives at this path for the rest of this test's flow, until disposed.</summary>
    internal static IDisposable ForcePathForTest(string path)
    {
        var previous = ForcedPath.Value;
        ForcedPath.Value = path;
        return new Restore(() => ForcedPath.Value = previous);
    }

    /// <summary>Tests only: a record of this test's own, in a new folder, along with the path it lives at.</summary>
    internal static IDisposable RecordForTest(out string path)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keep-record-" + Guid.NewGuid().ToString("N"));
        path = System.IO.Path.Combine(folder, "keep.json");
        return ForcePathForTest(path);
    }

    /// <summary>Tests only: the record cannot be saved for the rest of this test's flow, until disposed.</summary>
    internal static IDisposable FailSavesForTest()
    {
        var previous = FailingSave.Value;
        FailingSave.Value = true;
        return new Restore(() => FailingSave.Value = previous);
    }

    private static readonly AsyncLocal<bool> FailingSave = new();

    /// <summary>Tests only: the next <paramref name="count"/> reads of the record fail as one another program holds it does, until disposed.</summary>
    internal static IDisposable FailReadsForTest(int count)
    {
        var previous = FailingReads.Value;
        FailingReads.Value = count;
        return new Restore(() => FailingReads.Value = previous);
    }

    private static readonly AsyncLocal<int> FailingReads = new();

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    internal static string Path => ForcedPath.Value ?? System.IO.Path.Combine(PathHelper.AppDataDir, "keep.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The whole record: one <see cref="KeepGame"/> per game that has a profile (by profile id), and what this PC holds.</summary>
    internal sealed class Doc
    {
        public int Version { get; set; } = Keep.Version;
        public Dictionary<string, KeepGame> Games { get; set; } = new(StringComparer.Ordinal);
        /// <summary>This PC: which driver Exo's driver-side settings were last held under, and what it last put back.</summary>
        public KeepPc Pc { get; set; } = new();
        /// <summary>What a store client's quiet values held before Exo's first write of each, by client and account ("steam/&lt;account id&gt;").</summary>
        public Dictionary<string, KeepStore> Stores { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// One store client's account: every value Exo's quiet wrote into the client's files, with what it held before the first
    /// write (Off returns exactly that). Per PC like the rest of the record; nothing here is synced.
    /// </summary>
    internal sealed class KeepStore
    {
        /// <summary>By target (file, section path and key): what the value held.</summary>
        public Dictionary<string, KeepHeld> Held { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// What one value held before Exo's first write: its text as the file had it (<see cref="Raw"/>), or nothing
    /// (<see cref="Absent"/>, with how many of its sections the file had, so a section Exo added goes with it).
    /// </summary>
    internal sealed class KeepHeld
    {
        public string File { get; set; } = "";
        public string[] Path { get; set; } = [];
        public string Key { get; set; } = "";
        public bool Absent { get; set; }
        public string Raw { get; set; } = "";
        public int Depth { get; set; }
    }

    /// <summary>
    /// What Keep holds for one game. Entries: every value Exo wrote, by file key and line id. Context: the facts in force the
    /// last time Keep saw all of them held. Held: when that was.
    /// </summary>
    internal sealed class KeepGame
    {
        public Dictionary<string, KeepEntry> Entries { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Context { get; set; } = new(StringComparer.Ordinal);
        public DateTime? HeldAt { get; set; }
        public DateTime? CheckedAt { get; set; }
        /// <summary>What Optimize keeps on for this game; null when it was never pressed or was put back.</summary>
        public Keeping? Keeping { get; set; }
        /// <summary>The game's own record of this PC was brought in from kept.json and its .on.json (they stay as they were).</summary>
        public bool Migrated { get; set; }
        /// <summary>The library games this record was pressed for, by game id: the status the page gets is told under each.</summary>
        public List<string> GameIds { get; set; } = [];
        /// <summary>Evidence noted for the next look after a session (Call of Duty's marker found at a session's start).</summary>
        public List<string> Signals { get; set; } = [];
        /// <summary>The game won't keep some settings was said already (said once, until Optimize is pressed).</summary>
        public bool RefusedSaid { get; set; }
        /// <summary>Each file's line order as last seen (by file key), to tell a rebuilt file.</summary>
        public Dictionary<string, string> Shapes { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// What Keep holds for this PC (<see cref="DriverKeep"/>): <see cref="Driver"/> is the driver identity under which Exo's
    /// driver-side settings were last seen in place; <see cref="DriverPutBack"/> is the identity Exo last put them back under
    /// by itself, with its time (so the page can say so until the next game runs); <see cref="DriverTries"/> is how many looks
    /// running have failed to put them back under a new identity, so Exo stops after three.
    /// </summary>
    internal sealed class KeepPc
    {
        public string? Driver { get; set; }
        public DateTime? DriverPutBackAt { get; set; }
        public string? DriverPutBack { get; set; }
        /// <summary>Looks running that put back Exo's settings under a new identity and did not read them back as applied.</summary>
        public int DriverTries { get; set; }
        /// <summary>The new identity those looks ran for; a different driver starts the count again.</summary>
        public string? DriverTryOn { get; set; }
        /// <summary>A driver identity that differs from <see cref="Driver"/>, as first seen; it is acted on only once it has stood for two looks at least two minutes apart.</summary>
        public string? DriverSeen { get; set; }
        /// <summary>When <see cref="DriverSeen"/> was first seen.</summary>
        public DateTime? DriverSeenAt { get; set; }

        /// <summary>
        /// The global profile's settings this driver skipped or refused, in hex, as applied (or put back): none of them is
        /// in the driver, so none of them is shown as on. Cleared when the driver identity changes, which may take them.
        /// </summary>
        public List<string> DriverSkipped { get; set; } = [];
        /// <summary>The identity <see cref="DriverSkipped"/> belongs to; null when nothing was applied under it yet.</summary>
        public string? DriverSkippedDriver { get; set; }
        /// <summary>The Windows side (<see cref="WindowsKeep"/>): Game Mode's state and what it held before Exo's first write; null until Exo first looks.</summary>
        public GameModeKeep? GameMode { get; set; }
        /// <summary>Game Bar: what each value and link handler held before Exo's first write, and whether the player turned Exo's keeping off.</summary>
        public GameBarKeep? GameBar { get; set; }
        /// <summary>"Optimizations for windowed games": what the global graphics value held before Exo's first write, or that the player undid it.</summary>
        public WindowedGamesKeep? WindowedGames { get; set; }
        /// <summary>The Windows build and update revision ("26300.9711") under which the Windows-side settings were last seen in place.</summary>
        public string? Windows { get; set; }
    }

    /// <summary>Game Mode: where it stands by <see cref="WindowsGameMode.Table"/>, and the value Exo's first write replaced (null: it was not set). Kept only while Exo holds it.</summary>
    internal sealed class GameModeKeep
    {
        public GameModeState State { get; set; }
        public RegValue? Before { get; set; }
    }

    /// <summary>Game Bar: where it stands by <see cref="WindowsGameBar.Table"/>, and what Exo's first write replaced, per value and per link handler.</summary>
    internal sealed class GameBarKeep
    {
        public GameBarState State { get; set; }
        public List<GameBarValueKeep> Values { get; set; } = [];
        public List<GameBarHandlerKeep> Handlers { get; set; } = [];
    }

    /// <summary>One of Game Bar's four DWORDs as it was before Exo wrote it (null: not set).</summary>
    internal sealed class GameBarValueKeep
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public RegValue? Before { get; set; }
    }

    /// <summary>One link handler's whole key as it was before Exo replaced it (null: there was no key).</summary>
    internal sealed class GameBarHandlerKeep
    {
        public string Link { get; set; } = "";
        public RegTree? Before { get; set; }
    }

    /// <summary>The windowed-games value before Exo's first write (null: not set), or <see cref="Declined"/>: the player undid it and Exo does not write it again.</summary>
    internal sealed class WindowedGamesKeep
    {
        public RegValue? Before { get; set; }
        public bool Declined { get; set; }
    }

    /// <summary>
    /// What Optimize keeps on: the groups on; each file's mark of the defaults it was built from; the game's program and the
    /// time it had (Built) and the game's other build facts (Facts: Steam's build number, a launcher's content times) when
    /// Exo last looked after the game ran; when Exo last wrote again after an update (null once the game ran since); and
    /// Pending: Optimize was pressed before the game had a file.
    /// </summary>
    internal sealed class Keeping
    {
        public List<string> Groups { get; set; } = [];
        public Dictionary<string, string> Marks { get; set; } = new(StringComparer.Ordinal);
        public string? Program { get; set; }
        public DateTime? Built { get; set; }
        public Dictionary<string, string> Facts { get; set; } = new(StringComparer.Ordinal);
        public DateTime? Again { get; set; }
        public bool Pending { get; set; }
    }

    /// <summary>One thing that happened to an entry: when, what was seen, why, and what was done.</summary>
    internal sealed record Step(DateTime At, string? Seen, string Cause, string Action);

    /// <summary>
    /// One value Exo wrote. Domain and Target say where (gameFile: the file key and line id). Owner: the game and group.
    /// Intended is what Exo set, Before what it held (Undo). ReadBack is what a fresh read said right after the write
    /// (ReadBackAt when). Inputs: what a target Exo worked out from the PC at that moment was worked out from (screens,
    /// audio device, the file's own resolution), so a target that moved is told from a file that did.
    /// </summary>
    internal sealed class KeepEntry
    {
        public string Domain { get; set; } = "gameFile";
        public string Target { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Intended { get; set; } = "";
        public string Before { get; set; } = "";
        public DateTime WrittenAt { get; set; }
        public string ExoVersion { get; set; } = "";
        public string? ReadBack { get; set; }
        public DateTime? ReadBackAt { get; set; }
        public string? Inputs { get; set; }
        public string? LastSeen { get; set; }
        public DateTime? LastCheckedAt { get; set; }
        public KeepState State { get; set; } = KeepState.Held;
        /// <summary>The value found the last time this one was put back with no evidence; the same value again is the player's.</summary>
        public string? Found { get; set; }
        /// <summary>Sessions running in which this value was put back with reset evidence: three running is a back off.</summary>
        public int ResetStreak { get; set; }
        /// <summary>The last 10 things that happened.</summary>
        public List<Step> History { get; set; } = [];

        /// <summary>Moves the entry by <see cref="KeepTable"/> and notes why; returns the row, whose action the caller does.</summary>
        internal KeepTable.Row Go(KeepEvent ev, string? seen, string cause)
        {
            var row = KeepTable.Step(State, ev);
            if (row.To != State || row.Action != KeepAction.None || ev is not KeepEvent.SeenHeld)
            {
                History.Add(new Step(DateTime.UtcNow, seen, cause, row.Action == KeepAction.None ? ev.ToString() : row.Action.ToString()));
                if (History.Count > 10) History.RemoveRange(0, History.Count - 10);
            }
            State = row.To;
            return row;
        }
    }

    /// <summary>The record as it is now; an empty one when there is none. An unreadable one is set aside and the last good copy used.</summary>
    internal static Doc Load()
    {
        lock (Gate) return LoadLocked() ?? new Doc();
    }

    /// <summary>
    /// Whether the record can be read at all: false when another program holds it, which means an empty document is not "nothing
    /// kept" and nothing may be migrated or written over what it holds.
    /// </summary>
    internal static bool Available()
    {
        lock (Gate) return LoadLocked() is not null;
    }

    /// <summary>
    /// The record as it is now, or null when it is there but cannot be read at all (another program holds it): nothing is then
    /// known about it, so nothing is set aside and nothing is saved over it. An unreadable one (empty, not JSON) is set aside
    /// and the last good copy used; a newer Exo's is returned as it is, never saved over.
    /// </summary>
    private static Doc? LoadLocked()
    {
        if (FailingReads.Value > 0)
        {
            FailingReads.Value--;
            AppLog.Warn("Keep: the record couldn't be read: IOException");
            return null;
        }
        foreach (var path in new[] { Path, Path + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                if (text.Length > 0 && JsonSerializer.Deserialize<Doc>(text, Json) is { } doc)
                {
                    if (doc.Version > Version && !NewerSaid)
                    {
                        NewerSaid = true;
                        AppLog.Warn($"Keep: the record was written by a newer Exo (version {doc.Version}), so it is read and never saved over.");
                    }
                    return doc;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another program: what it holds is unknown, so neither the last good copy nor an empty one stands in for it.
                AppLog.Warn($"Keep: the record couldn't be read: {ex.GetType().Name}");
                return null;
            }
            catch (JsonException ex)
            {
                AppLog.Warn($"Keep: the record couldn't be read: {ex.GetType().Name}");
            }
            // Set aside, never overwritten as if there had been nothing: the next save keeps the last good copy beside it.
            if (path == Path) SetAside();
        }
        return new Doc();
    }

    private static void SetAside()
    {
        try { if (File.Exists(Path)) File.Move(Path, Path + ".unreadable", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left where it is */ }
    }

    /// <summary>Changes the record under the lock and saves it; false when it couldn't be read, or is a newer Exo's, or couldn't be saved.</summary>
    internal static bool Update(Action<Doc> change)
    {
        lock (Gate)
        {
            if (LoadLocked() is not { } doc || doc.Version > Version) return false;
            change(doc);
            return SaveLocked(doc);
        }
    }

    /// <summary>Beside it, flushed to the disk, then swapped in with the last good one kept as .bak.</summary>
    private static bool SaveLocked(Doc doc)
    {
        if (FailingSave.Value) return false;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + "." + Environment.ProcessId + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, doc, Json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak");
            else File.Move(temporary, Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Keep: the record couldn't be saved: {ex.GetType().Name}");
            return false;
        }
    }

    internal static string ExoVersion { get; } = typeof(Keep).Assembly.GetName().Version?.ToString(3) ?? "";
}
