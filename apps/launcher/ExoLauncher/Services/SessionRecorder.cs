using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The settings a game's frame times were measured under: a stable hash of that game's Keep entries (the target and the value
/// Exo intended for each, in order), or "none" for a game with no Keep record. A change to one kept value is a new version, the
/// same entries in another order are the same one, and a target and a value never run together into another pair.
/// </summary>
internal static class SettingsVersion
{
    internal const string None = "none";

    internal static string Of(IEnumerable<(string Target, string Intended)>? entries)
    {
        var lines = entries?.Select(entry => entry.Target + "\u0001" + entry.Intended).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (lines.Length == 0) return None;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..16];
    }

    /// <summary>
    /// Every Keep record that was pressed for this library game, its entries together, and only the entries whose value Exo's is
    /// in the file now (held, or put back): a value the player took over, one that could not be written, one the game won't keep
    /// and one with no line to hold are not what the frames were measured under, so they make a version of their own.
    /// </summary>
    internal static string ForGame(Keep.Doc doc, string gameId) =>
        Of(doc.Games.Values.Where(game => game.GameIds.Contains(gameId)).SelectMany(game => game.Entries.Values)
            .Where(entry => entry.State is KeepState.Held or KeepState.PutBack).Select(entry => (entry.Target, entry.Intended)));
}

/// <summary>
/// One session of one game under one settings version, as it is kept: a few hundred bytes. The histogram has one bucket per
/// millisecond from 0 to 50; frames over 50 ms are the stalls. Usable is false for a session too short to compare settings with.
/// GPU-busy and CPU-busy are not here: the present trace gives only when frames were presented, so nothing is invented for them.
/// </summary>
internal sealed record SessionRecord(string Game, string Version, DateTime StartedUtc, double Seconds, long Frames, double AverageMs, double AverageFps,
    double Low1Fps, double Low01Fps, long Stalls, int[] Histogram, bool Usable);

/// <summary>
/// The sessions, in one file in Exo's data folder, on this PC only (it is in the backup registry as such) and never sent anywhere.
/// Ways it could fail, each with a test: it grows without end (20 newest per game and version, nothing older than 90 days, 300
/// in all); a half-written file (written beside, then swapped in); a file that cannot be read stops a new session being kept
/// (it is read as nothing, and the next save replaces it).
/// </summary>
internal static class SessionStore
{
    internal const string FileName = "session-recorder.json";
    internal const int PerVersion = 20, MaxDays = 90, MaxSessions = 300;

    /// <summary>A session shorter than this (seconds of frames measured) is kept but is no evidence for a settings step.</summary>
    internal const double UsableSeconds = 300;

    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);

    private sealed class Doc
    {
        public int Version { get; set; } = 1;
        public List<SessionRecord> Sessions { get; set; } = [];
    }

    /// <summary>What stays: nothing older than 90 days, the 20 newest of each game and version, and the 300 newest in all. Oldest first.</summary>
    internal static List<SessionRecord> Prune(IEnumerable<SessionRecord> sessions, DateTime nowUtc)
    {
        var oldest = nowUtc.AddDays(-MaxDays);
        return [.. sessions
            .Where(session => session.StartedUtc >= oldest)
            .GroupBy(session => (session.Game, session.Version))
            .SelectMany(group => group.OrderByDescending(session => session.StartedUtc).Take(PerVersion))
            .OrderByDescending(session => session.StartedUtc)
            .Take(MaxSessions)
            .OrderBy(session => session.StartedUtc)];
    }

    /// <summary>The sessions kept; none when there is no file or it can't be read just now (this only reads).</summary>
    internal static List<SessionRecord> Load(string path)
    {
        lock (Gate) return LoadLocked(path, setAside: false) ?? [];
    }

    /// <summary>
    /// The sessions in the file; none for no file. Null when the file is there but can't be read (another program holds it): it
    /// is then neither replaced nor set aside, which would lose what it holds. A file that was read and is not the document
    /// (damaged, empty) is moved to <c>.bad</c> beside it, once, and counts as none; with <paramref name="setAside"/> false
    /// it is left where it is.
    /// </summary>
    private static List<SessionRecord>? LoadLocked(string path, bool setAside)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return [];
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Session recorder: the file couldn't be read: " + ex.GetType().Name);
            return null;
        }
        try
        {
            if (text.Length > 0 && JsonSerializer.Deserialize<Doc>(text, Json) is { } doc) return doc.Sessions ?? [];
        }
        catch (JsonException) { }
        AppLog.Warn("Session recorder: the file is damaged" + (setAside ? ", set aside as .bad." : "."));
        if (!setAside) return [];
        try { File.Move(path, path + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return [];
    }

    /// <summary>
    /// Keeps the session, replacing the one kept before with the same game, version and start (a checkpoint of the same
    /// session). False when it could not be kept now (the file is held by another program, or can't be written): nothing was
    /// changed, and the caller holds the session and tries again.
    /// </summary>
    internal static bool Add(string path, SessionRecord record, DateTime nowUtc)
    {
        lock (Gate)
        {
            var sessions = LoadLocked(path, setAside: true);
            if (sessions is null) return false;
            sessions.RemoveAll(other => other.Game == record.Game && other.Version == record.Version && other.StartedUtc == record.StartedUtc);
            var kept = Prune([.. sessions, record], nowUtc);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Doc { Sessions = kept }, Json);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Session recorder: the file couldn't be written: " + ex.GetType().Name);
                return false;
            }
        }
    }
}

internal enum RecorderState { Idle, WaitingForRights, Recording, Paused, Flushing, Stopped }

internal enum RecorderEvent { GameFront, GameBack, GameClosed, SessionBoundary, RightsOk, RightsMissing, FlushDone, Exit }

internal enum RecorderAction { None, CheckRights, Begin, Resume, Save, SaveAndStop }

/// <summary>
/// The recorder's life as one table. Idle: no library game in front yet. WaitingForRights: one is, and Windows has not given
/// Exo the right to count its frames (it says so and records nothing). Recording: the game is in front and its frames are
/// counted. Paused: the game runs but is not in front. Flushing: the session is being written. Stopped: Exo is closing.
/// Every way out of Recording and Paused saves; every state but Stopped leaves by Exit, and all but Idle leave towards Idle
/// by the game closing or the write finishing.
///
/// | State            | GameFront        | GameBack | GameClosed   | SessionBoundary | RightsOk  | RightsMissing | FlushDone | Exit                  |
/// | Idle             | Waiting, check   | -        | -            | -               | -         | -             | -         | Stopped               |
/// | WaitingForRights | -                | -        | Idle         | -               | Recording, begin | -      | -         | Stopped               |
/// | Recording        | -                | Paused   | Flushing, save | Flushing, save | -         | -             | -         | Stopped, save         |
/// | Paused           | Recording, resume| -        | Flushing, save | Flushing, save | -         | -             | -         | Stopped, save         |
/// | Flushing         | -                | -        | -            | -               | -         | -             | Idle      | Stopped               |
/// | Stopped          | -                | -        | -            | -               | -         | -             | -         | Stopped               |
/// </summary>
internal static class RecorderTable
{
    internal static (RecorderState To, RecorderAction Do) Step(RecorderState state, RecorderEvent ev) => (state, ev) switch
    {
        (RecorderState.Stopped, _) => (RecorderState.Stopped, RecorderAction.None),
        (RecorderState.Recording or RecorderState.Paused, RecorderEvent.Exit) => (RecorderState.Stopped, RecorderAction.SaveAndStop),
        (_, RecorderEvent.Exit) => (RecorderState.Stopped, RecorderAction.None),
        (RecorderState.Idle, RecorderEvent.GameFront) => (RecorderState.WaitingForRights, RecorderAction.CheckRights),
        (RecorderState.Idle, _) => (RecorderState.Idle, RecorderAction.None),
        (RecorderState.WaitingForRights, RecorderEvent.RightsOk) => (RecorderState.Recording, RecorderAction.Begin),
        (RecorderState.WaitingForRights, RecorderEvent.GameClosed) => (RecorderState.Idle, RecorderAction.None),
        (RecorderState.WaitingForRights, _) => (RecorderState.WaitingForRights, RecorderAction.None),
        (RecorderState.Recording, RecorderEvent.GameBack) => (RecorderState.Paused, RecorderAction.None),
        (RecorderState.Recording or RecorderState.Paused, RecorderEvent.GameClosed or RecorderEvent.SessionBoundary) => (RecorderState.Flushing, RecorderAction.Save),
        (RecorderState.Recording, _) => (RecorderState.Recording, RecorderAction.None),
        (RecorderState.Paused, RecorderEvent.GameFront) => (RecorderState.Recording, RecorderAction.Resume),
        (RecorderState.Paused, _) => (RecorderState.Paused, RecorderAction.None),
        (RecorderState.Flushing, RecorderEvent.FlushDone) => (RecorderState.Idle, RecorderAction.None),
        _ => (RecorderState.Flushing, RecorderAction.None),
    };
}

/// <summary>
/// What the recorder is told once a second: the library game that is running (null with none), the settings version it runs
/// under, whether it is in front, whether Windows lets Exo count its frames, and the frame times since the last look.
/// </summary>
internal readonly record struct Observation(string? Game, string Version, bool InFront, bool RightsOk, IReadOnlyList<double> FramesMs, DateTime NowUtc);

/// <summary>
/// One game session at a time, driven by observations alone (it knows nothing of the overlay). It keeps no frame list, only the
/// histogram. A session is saved when the game closes, when another game or settings version takes over, and when Exo exits.
/// Nothing is written while there are no rights, nothing for a session with no frames, and what is kept never leaves this PC.
/// </summary>
internal sealed class SessionRecorder(Func<SessionRecord, bool> save)
{
    /// <summary>While recording, the session so far is written this often (measured seconds), replacing its own earlier write: a crash loses at most this much.</summary>
    internal const double CheckpointSeconds = 300;

    private const int MaxPending = 10;

    private readonly Lock _gate = new();
    private readonly List<SessionRecord> _pending = [];
    private FrameStats.Accumulator _frames = new();
    private string? _game, _version;
    private DateTime _started;
    private double _checkpointAt;

    internal RecorderState State { get; private set; } = RecorderState.Idle;

    /// <summary>The one word for missing rights: a game is in front and Windows has not let Exo count it.</summary>
    internal bool NeedsRights => State == RecorderState.WaitingForRights;

    private bool Same(Observation look) => look.Game == _game && look.Version == _version;

    internal void Tick(Observation look)
    {
        lock (_gate)
        {
            if (State == RecorderState.Stopped) return;
            var added = false;
            if (State == RecorderState.Recording && Same(look))
            {
                AddFrames(look);
                added = true;
            }
            for (var i = 0; i < 8; i++)
            {
                var ev = Next(look);
                if (ev is null) break;
                if (!Apply(ev.Value, look)) break;
            }
            if (!added && State == RecorderState.Recording && Same(look)) AddFrames(look);
            if (State == RecorderState.Recording) Checkpoint();
            RetryPending();
        }
    }

    /// <summary>Exo is closing: a session in progress is saved, and the recorder takes no more.</summary>
    internal void Exit()
    {
        lock (_gate)
        {
            Apply(RecorderEvent.Exit, default);
            RetryPending();
        }
    }

    private void AddFrames(Observation look)
    {
        foreach (var ms in look.FramesMs) _frames.Add(ms);
    }

    private RecorderEvent? Next(Observation look) => State switch
    {
        RecorderState.Idle => look.Game is not null && look.InFront ? RecorderEvent.GameFront : null,
        RecorderState.WaitingForRights => look.Game is null ? RecorderEvent.GameClosed : look.RightsOk ? RecorderEvent.RightsOk : RecorderEvent.RightsMissing,
        RecorderState.Recording or RecorderState.Paused =>
            look.Game is null ? RecorderEvent.GameClosed :
            !Same(look) ? RecorderEvent.SessionBoundary :
            State == RecorderState.Recording && !look.InFront ? RecorderEvent.GameBack :
            State == RecorderState.Paused && look.InFront ? RecorderEvent.GameFront : null,
        RecorderState.Flushing => RecorderEvent.FlushDone,
        _ => null,
    };

    /// <summary>Moves by the table and does its action; false when nothing moved and nothing was done.</summary>
    private bool Apply(RecorderEvent ev, Observation look)
    {
        var (to, action) = RecorderTable.Step(State, ev);
        var moved = to != State || action != RecorderAction.None;
        State = to;
        switch (action)
        {
            case RecorderAction.Begin:
                _frames = new FrameStats.Accumulator();
                _checkpointAt = 0;
                (_game, _version, _started) = (look.Game, look.Version, look.NowUtc);
                break;
            case RecorderAction.Save or RecorderAction.SaveAndStop:
                Finish();
                break;
        }
        return moved;
    }

    private SessionRecord Build(FrameStats.Result result) =>
        new(_game!, _version!, _started, Math.Round(result.Seconds, 1), result.Frames, Math.Round(result.AverageMs, 3), Math.Round(result.AverageFps, 2),
            Math.Round(result.Low1Fps, 2), Math.Round(result.Low01Fps, 2), result.Stalls, result.Histogram, result.Seconds >= SessionStore.UsableSeconds);

    /// <summary>The session so far, written again every <see cref="CheckpointSeconds"/>; a write that fails is asked again at the next look.</summary>
    private void Checkpoint()
    {
        if (_game is null || _version is null || _frames.Seconds - _checkpointAt < CheckpointSeconds) return;
        var result = _frames.Snapshot();
        if (TrySave(Build(result))) _checkpointAt = result.Seconds;
    }

    private void Finish()
    {
        var result = _frames.Snapshot();
        var record = _game is null || _version is null || result.Frames == 0 ? null : Build(result);
        _frames = new FrameStats.Accumulator();
        _checkpointAt = 0;
        _game = _version = null;
        if (record is null) return;
        // A session that could not be written is held, and asked for again at every look until it is.
        if (!TrySave(record))
        {
            _pending.RemoveAll(other => Same(other, record));
            _pending.Add(record);
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }
    }

    private void RetryPending()
    {
        for (var at = _pending.Count - 1; at >= 0; at--)
            if (TrySave(_pending[at])) _pending.RemoveAt(at);
    }

    private static bool Same(SessionRecord one, SessionRecord other) => one.Game == other.Game && one.Version == other.Version && one.StartedUtc == other.StartedUtc;

    private bool TrySave(SessionRecord record)
    {
        try { return save(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Session recorder: a session wasn't kept: " + ex.GetType().Name);
            return false;
        }
    }
}
