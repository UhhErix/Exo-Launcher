using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Notices when something outside Exo wipes the DirectX and graphics-driver shader caches (Windows' Disk Cleanup, Storage
/// Sense, another cleaner): the folder is gone or was made again, or the caches lost most of their size, and Exo did
/// nothing to them since the last look. Then the games that were warmed are marked "Shaders need preparing again"
/// (<see cref="ShaderWipeMarks"/>), which the Shaders page reads as stale, so automatic preparation queues them.
/// Exo's own compiles and deletes (<see cref="ShaderCacheLock"/>) never count as a wipe. Looked at every ten minutes.
/// </summary>
internal static class ShaderCacheWatch
{
    /// <summary>The caches as one look saw them: total bytes, whether the DirectX folder is there and when it was made.</summary>
    internal sealed record Snapshot(long Bytes, bool Exists, long CreatedTicks, DateTime At);

    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>A drop below this is ordinary churn; below it the drop rule does not apply.</summary>
    internal const long Significant = 64L << 20;

    /// <summary>
    /// A wipe since <paramref name="previous"/>: no Exo activity on the caches since that look, and either the DirectX folder
    /// was removed or made again, or the caches fell to a fifth of a significant size.
    /// </summary>
    internal static bool Wiped(Snapshot? previous, Snapshot now, DateTime lastExoActivity)
    {
        if (previous is null) return false;
        // Exo itself worked on these caches since the last look: its own work is not a wipe, and the next look starts again.
        if (lastExoActivity > previous.At) return false;
        if (previous.Exists && (!now.Exists || now.CreatedTicks != previous.CreatedTicks)) return true;
        return previous.Bytes >= Significant && now.Bytes <= previous.Bytes / 5;
    }

    /// <summary>The marks with <paramref name="games"/> marked at <paramref name="at"/> (a new dictionary).</summary>
    internal static Dictionary<string, DateTime> Mark(IReadOnlyDictionary<string, DateTime> marks, IEnumerable<string> games, DateTime at)
    {
        var next = new Dictionary<string, DateTime>(marks, StringComparer.OrdinalIgnoreCase);
        foreach (var id in games) next[id] = at;
        return next;
    }

    /// <summary>
    /// One pass, each minute: the engine's delete state is probed, the warmed games' marks are cleared by what the engine
    /// reports, and a snapshot is taken every <see cref="Every"/>. A wipe marks the games the engine reports warmed.
    /// </summary>
    internal static void Observe(IEnumerable<GameEntry> games, Func<GameEntry, ScsKillerStatus.Status> statusOf, DateTime now)
    {
        var looked = games.Select(game => (Game: game, Status: statusOf(game))).ToList();
        ProbeEngine(looked.Select(item => item.Status.EngineId), now);
        foreach (var item in looked)
            if (item.Status is { State: "warmed", WarmedAt: { } warmed }) ShaderWipeMarks.ClearWarmed(item.Game.Id, warmed.UtcDateTime);

        var previous = Load();
        if (previous is not null && now - previous.At < Every) return;
        var snapshot = Read(now);
        if (Wiped(previous, snapshot, ShaderCacheLock.LastActivity))
        {
            var warmed = looked.Where(item => item.Status.State == "warmed").Select(item => item.Game.Id);
            ShaderWipeMarks.Replace(Mark(ShaderWipeMarks.All, warmed, now));
            AppLog.Warn("Shaders: the shader caches were cleared outside Exo; the games it had prepared need preparing again.");
        }
        Save(snapshot);
    }

    /// <summary>
    /// Whether the engine is deleting a cache now: its cache-delete mutex is held, or one of the games' compile locks is (a
    /// delete or Clear cache of that game). Reported to <see cref="ShaderCacheLock"/>.
    /// </summary>
    internal static void ProbeEngine(IEnumerable<string?> engineIds, DateTime now)
    {
        var deleting = MutexHeld(ShaderCacheLock.EngineDeleteMutex)
                       || engineIds.Any(id => id is { Length: > 0 } && FileHeld(CompileLockPath(id)));
        ShaderCacheLock.ReportEngineDeleting(deleting, now);
    }

    /// <summary>The engine's compile lock file for a game (its id with ':' as '_', as ScsKillerStatus reads it).</summary>
    internal static string CompileLockPath(string engineId) =>
        Path.Combine(ScsKillerStatus.DataDir, "games", engineId.Replace(':', '_'), "compile.lock");

    /// <summary>
    /// A named mutex some process holds. Opened and tried, never kept: a mutex it takes is released again. True when held
    /// or when it can't be asked (unknown means keep).
    /// </summary>
    internal static bool MutexHeld(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        try
        {
            if (!mutex.WaitOne(0)) return true;
            mutex.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
        finally { mutex.Dispose(); }
    }

    /// <summary>A file another handle holds open exclusively (the engine's compile lock). True when held or unreadable.</summary>
    internal static bool FileHeld(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>The caches now: the DirectX folder and the graphics drivers' own caches.</summary>
    internal static Snapshot Read(DateTime now)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
        var exists = Directory.Exists(folder);
        var created = exists ? Directory.GetCreationTimeUtc(folder).Ticks : 0;
        return new Snapshot(DriveSpace.DirectXCacheBytes() + DriveSpace.DriverCacheBytes(), exists, created, now);
    }

    private static string StatePath => Path.Combine(PathHelper.AppDataDir, "shader-cache-watch.json");

    private static Snapshot? Load()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(StatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Save(Snapshot snapshot)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(snapshot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Shaders: cache look not saved: " + ex.Message); }
    }
}

/// <summary>
/// The games marked "Shaders need preparing again" after an outside wipe, with when they were marked. A mark holds until a
/// compile finishes for the game after it (its warmed time is later). Kept in shader-wipe.json, read once.
/// </summary>
internal static class ShaderWipeMarks
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, DateTime>? _marks;

    private static string Path => System.IO.Path.Combine(PathHelper.AppDataDir, "shader-wipe.json");

    /// <summary>Marked here, and not warmed again since the mark (warmedAt null means never warmed since it was marked).</summary>
    internal static bool Marked(IReadOnlyDictionary<string, DateTime> marks, string id, DateTime? warmedAt) =>
        marks.TryGetValue(id, out var at) && (warmedAt is not { } warmed || warmed < at);

    internal static Dictionary<string, DateTime> All
    {
        get
        {
            lock (Gate) return _marks ??= Read();
        }
    }

    /// <summary>Whether the game was marked and has not been compiled since (warmedAt: when its last compile ended, if any).</summary>
    internal static bool IsMarked(string id, DateTime? warmedAt) => Marked(All, id, warmedAt);

    internal static void Replace(Dictionary<string, DateTime> marks)
    {
        lock (Gate)
        {
            _marks = marks;
            Write(marks);
        }
    }

    /// <summary>Whether a warmed report (when the engine finished the game's compile) is after the game's mark, so the mark goes.</summary>
    internal static bool ClearedBy(IReadOnlyDictionary<string, DateTime> marks, string id, DateTime? warmedAt) =>
        warmedAt is { } warmed && marks.TryGetValue(id, out var at) && warmed >= at;

    /// <summary>The engine reports the game warmed: its mark goes when the compile is after it.</summary>
    internal static void ClearWarmed(string id, DateTime? warmedAt)
    {
        lock (Gate)
        {
            if (!ClearedBy(All, id, warmedAt)) return;
            var marks = new Dictionary<string, DateTime>(All, StringComparer.OrdinalIgnoreCase);
            marks.Remove(id);
            _marks = marks;
            Write(marks);
        }
    }

    /// <summary>A game compiled after its mark: the mark goes.</summary>
    internal static void Clear(string id)
    {
        lock (Gate)
        {
            var marks = new Dictionary<string, DateTime>(All, StringComparer.OrdinalIgnoreCase);
            if (!marks.Remove(id)) return;
            _marks = marks;
            Write(marks);
        }
    }

    private static Dictionary<string, DateTime> Read()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(Path)) is { } loaded ? new(loaded, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void Write(Dictionary<string, DateTime> marks)
    {
        try { File.WriteAllText(Path, System.Text.Json.JsonSerializer.Serialize(marks)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Shaders: wipe marks not saved: " + ex.Message); }
    }
}
