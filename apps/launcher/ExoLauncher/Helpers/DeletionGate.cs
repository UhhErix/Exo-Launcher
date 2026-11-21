using System.Diagnostics;

namespace ExoLauncher.Helpers;

/// <summary>Raised by an Exo-owned delete the gate refuses. An IOException, so the callers' existing handlers still catch it.</summary>
internal sealed class DeletionRefusedException(string message) : IOException(message);

/// <summary>
/// Every file and folder the host deletes goes through here (BannedSymbols.txt bans File.Delete, Directory.Delete and the
/// FileInfo/DirectoryInfo deletes everywhere else). Two callers. <b>Owned</b> (<see cref="DeleteFile"/>,
/// <see cref="DeleteDirectory"/>): Exo's own files; it refuses only what Exo keeps for everyone and a shader cache while a
/// compile runs. <b>Clean</b> (<see cref="Clean"/>): the junk and leftover cleaners; it also refuses sign-in data, credentials,
/// game saves (a folder holding one stays whole), Exo's own data (except its old backups), every shader cache, and a program
/// that owns the files while it runs. Each call decides afresh: a refusal is never remembered.
/// </summary>
internal static class DeletionGate
{
    /// <summary>What a decision reads: Exo's own folders, its data folder, the shader roots, the running programs (null when unknown) and the shader lock.</summary>
    internal sealed record Policy(
        IReadOnlyList<string> KeptRoots,
        string? ExoData,
        IReadOnlyList<string> ShaderRoots,
        Func<IReadOnlyCollection<string>?> Running,
        Func<bool> ShaderBusy);

    /// <summary>The policy of the running PC: read at each call.</summary>
    internal static Policy Live => new(
        DeletionProtections.KeptRoots,
        DeletionProtections.ExoDataDir,
        DeletionProtections.ShaderRoots,
        RunningNow,
        () => ShaderCacheLock.Held);

    /// <summary>
    /// Why a delete of <paramref name="path"/> is refused, or null when it may go. <paramref name="clean"/>: a cleaner's delete
    /// (see the class summary). <paramref name="owners"/>: program names (no .exe) that hold the files; any running refuses.
    /// </summary>
    internal static string? Refusal(string path, bool clean, IReadOnlyCollection<string>? owners, Policy policy, bool allowAppBrowserFiles = false)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path ?? "")); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return "Exo could not check this path, so it kept it."; }
        if (full.Length == 0) return "Exo could not check this path, so it kept it.";

        // Inside a kept root, or a folder that holds one (deleting the ancestor would take the backups with it).
        if (policy.KeptRoots.Any(root => Within(full, root) || Within(root, full))) return "Exo keeps its own backups in Documents.";

        // The name rule is for the cleaners. Exo's own user-started features (a Riot account swap clears the client's sign-in
        // folders; a retired WebView2 profile goes) are Owned deletes and keep their own rules.
        var patterns = allowAppBrowserFiles ? DeletionProtections.HardKeptSegments : DeletionProtections.KeptSegments;
        if (clean && KeptBy(full, patterns))
            return "Exo keeps this: it holds sign-in data, a credential or a game save.";

        var shader = policy.ShaderRoots.Any(root => Within(full, root)) || KeptBy(full, DeletionProtections.ShaderSegments);
        if (shader)
        {
            if (clean) return "Exo Shaders owns shader caches, so Exo never deletes them.";
            if (policy.ShaderBusy()) return "Exo Shaders is compiling a game now, so it keeps the shader caches.";
        }

        if (!clean) return null;
        if (policy.ExoData is { } data && Within(full, data) && !IsExoBackup(full, data))
            return "Exo keeps its own data; only its old backups are cleaned.";
        if (owners is { Count: > 0 })
        {
            var running = policy.Running();
            if (running is null) return "Exo could not check which programs are running, so it kept this.";
            if (owners.FirstOrDefault(owner => running.Contains(owner, StringComparer.OrdinalIgnoreCase)) is { } holder)
                return $"{holder} is running and has these files; close it first.";
        }
        return null;
    }

    /// <summary>A cleaner's verdict on a file or folder, whole: <see cref="Refusal"/>, plus a folder holding a kept item. Null when it may go.</summary>
    internal static string? Check(string path, IReadOnlyCollection<string>? owners = null, Policy? policy = null, bool allowAppBrowserFiles = false)
    {
        policy ??= Live;
        if (Refusal(path, clean: true, owners, policy, allowAppBrowserFiles) is { } why) return why;
        if (Directory.Exists(path) && SubtreeKept(path, allowAppBrowserFiles))
            return "Exo keeps part of this: a sign-in file, a credential or a save is inside it.";
        return null;
    }

    /// <summary>
    /// A cleaner's delete of a file or a folder (whole, recursively). False with <paramref name="why"/> when refused, or when a
    /// file in it fails; true when it is gone (or was never there).
    /// </summary>
    internal static bool Clean(string path, IReadOnlyCollection<string>? owners, out string? why, Policy? policy = null, bool allowAppBrowserFiles = false)
    {
        why = Check(path, owners, policy, allowAppBrowserFiles);
        if (why is not null) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return true;
        try
        {
#pragma warning disable RS0030 // The gate is the one place that deletes.
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
#pragma warning restore RS0030
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return false;
        }
    }

    /// <summary>An Exo-owned delete of a file. Throws <see cref="DeletionRefusedException"/> when the gate refuses it.</summary>
    internal static void DeleteFile(string path)
    {
        if (Refusal(path, clean: false, owners: null, Live) is { } why) throw new DeletionRefusedException(why);
#pragma warning disable RS0030 // The gate is the one place that deletes.
        File.Delete(path);
#pragma warning restore RS0030
    }

    /// <summary>An Exo-owned delete of a folder. Throws <see cref="DeletionRefusedException"/> when the gate refuses it.</summary>
    internal static void DeleteDirectory(string path, bool recursive = false)
    {
        if (Refusal(path, clean: false, owners: null, Live) is { } why) throw new DeletionRefusedException(why);
#pragma warning disable RS0030 // The gate is the one place that deletes.
        Directory.Delete(path, recursive);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Retention of Exo's own backups: deletes one file that sits directly in <paramref name="folder"/> and carries a backup's
    /// name (LauncherBackup.IsBackupFileName). Any other file, or one elsewhere, throws <see cref="DeletionRefusedException"/>.
    /// </summary>
    internal static void DeleteBackup(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !parent.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase)
            || !IsBackupName(Path.GetFileName(full)))
            throw new DeletionRefusedException("Exo deletes only its own old backups.");
#pragma warning disable RS0030 // The gate is the one place that deletes.
        File.Delete(full);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Runs <paramref name="work"/> and reports the change in free bytes on the path's drive: what was really freed, never the
    /// summed sizes (other writers move the number too). Never negative.
    /// </summary>
    internal static (T Result, long Freed) Measure<T>(string path, Func<T> work, Func<string, long>? freeBytes = null)
    {
        var read = freeBytes ?? FreeBytesOf;
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        var before = read(root);
        var result = work();
        var after = read(root);
        return (result, Math.Max(0, after - before));
    }

    /// <summary>Free bytes on a drive, read now (0 when it can't be read).</summary>
    internal static long FreeBytesOf(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    /// <summary>The running programs' names, or null when they can't all be read (unknown means keep).</summary>
    private static IReadOnlyCollection<string>? RunningNow()
    {
        if (!ProcessSnapshot.TryRead(out var rows)) return null;
        var names = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Pid > 0 && !string.IsNullOrEmpty(row.Name))
                names.Add(ProcessSnapshot.StripExe(row.Name));
        }
        return names;
    }

    /// <summary>A backup's file name: the Launcher's daily, before-restore and export files (LauncherBackup names them so).</summary>
    private static bool IsBackupName(string name) =>
        name.StartsWith("exo-launcher-", StringComparison.Ordinal)
        && (name.EndsWith(".json", StringComparison.Ordinal) || (name.StartsWith("exo-launcher-picture-", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal))
            || (name.StartsWith("exo-launcher-picture-", StringComparison.Ordinal) && name.EndsWith(".jpg", StringComparison.Ordinal)));

    // Compared as written, and as Windows opens them (a junction or link into a kept folder is that folder).
    private static bool Within(string full, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return Covers(trimmed, full) || Covers(Resolved(trimmed), Resolved(full));
    }

    private static bool Covers(string root, string full) =>
        full.Equals(root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The path where Windows opens it: the deepest part that exists, with its links followed, and the rest as written.</summary>
    private static string Resolved(string path)
    {
        var rest = "";
        for (var step = path; !string.IsNullOrEmpty(step); step = Path.GetDirectoryName(step))
        {
            var name = Path.GetFileName(step);
            if (FinalPath.Of(step) is { } final) return rest.Length == 0 ? final : Path.Combine(final, rest);
            rest = rest.Length == 0 ? name : Path.Combine(name, rest);
        }
        return path;
    }

    private static bool IsExoBackup(string full, string data)
    {
        var parent = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        return parent is not null && parent.Equals(Path.TrimEndingDirectorySeparator(data), StringComparison.OrdinalIgnoreCase)
               && (name.StartsWith("app.previous", StringComparison.OrdinalIgnoreCase) || name.StartsWith("app.incoming", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the path holds any of the segment sequences, anywhere in it.</summary>
    private static bool KeptBy(string full, string[][] patterns)
    {
        var parts = full.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in patterns)
            for (var start = 0; start + pattern.Length <= parts.Length; start++)
                if (pattern.Select((name, i) => string.Equals(parts[start + i], name, StringComparison.OrdinalIgnoreCase)).All(same => same))
                    return true;
        return false;
    }

    /// <summary>True when a folder holds a kept item anywhere inside it (no folder link is followed).</summary>
    private static bool SubtreeKept(string folder, bool allowAppBrowserFiles = false)
    {
        var patterns = allowAppBrowserFiles ? DeletionProtections.HardKeptSegments : DeletionProtections.KeptSegments;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                if (KeptBy(entry.FullName, patterns)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }
}

/// <summary>
/// Exo Shaders' compile and cache-delete state as the gate reads it. A compile holds it (<see cref="Hold"/>), and so does
/// the engine's own word: compiling (app.status "compiling", read by ShadersAddon) and deleting (its cache-delete mutex, or a
/// game's compile.lock while a delete or Clear cache runs; probed by ShaderCacheWatch). Every engine report goes stale after
/// <see cref="EngineSilence"/>, so a flag the engine no longer backs releases itself. While held, an Exo-owned delete in a
/// shader cache waits.
/// </summary>
internal static class ShaderCacheLock
{
    /// <summary>The engine's cache-delete mutex (SCSKiller's ScsKiller.DeletingName, production scope: no suffix).</summary>
    internal const string EngineDeleteMutex = @"Local\SCSKiller.CacheDelete";

    /// <summary>A report older than this no longer holds: the engine went away or stopped reporting.</summary>
    internal static readonly TimeSpan EngineSilence = TimeSpan.FromMinutes(2);

    private static readonly Lock Gate = new();
    private static int _holds;
    private static bool _engineCompiling, _engineDeleting;
    private static DateTime _compilingAt = DateTime.MinValue, _deletingAt = DateTime.MinValue;
    private static long _lastActivityTicks;

    /// <summary>Held now: a compile or delete runs in this process, or the engine reported one recently.</summary>
    internal static bool Held => HeldAt(DateTime.UtcNow);

    internal static bool HeldAt(DateTime now)
    {
        lock (Gate)
        {
            var compiling = _engineCompiling && now - _compilingAt < EngineSilence;
            var deleting = _engineDeleting && now - _deletingAt < EngineSilence;
            return Volatile.Read(ref _holds) > 0 || compiling || deleting;
        }
    }

    /// <summary>When Exo last held or saw a compile or delete (UTC). Exo's own work on the caches is never read as an outside wipe.</summary>
    internal static DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>The engine's report of its compile state (false when it isn't running or no longer compiles).</summary>
    internal static void ReportEngineCompiling(bool compiling, DateTime now)
    {
        lock (Gate)
        {
            if (compiling) Touch(now);
            _engineCompiling = compiling;
            _compilingAt = now;
        }
    }

    /// <summary>
    /// The engine's report of a cache delete. A delete, and the check right after one ends, count as activity, so no outside
    /// wipe is read from them.
    /// </summary>
    internal static void ReportEngineDeleting(bool deleting, DateTime now)
    {
        lock (Gate)
        {
            if (deleting || _engineDeleting) Touch(now);
            _engineDeleting = deleting;
            _deletingAt = now;
        }
    }

    internal static IDisposable Hold()
    {
        Interlocked.Increment(ref _holds);
        Touch(DateTime.UtcNow);
        return new Scope();
    }

    private static void Touch(DateTime at) => Interlocked.Exchange(ref _lastActivityTicks, at.Ticks);

    private sealed class Scope : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Decrement(ref _holds);
            Touch(DateTime.UtcNow);
        }
    }
}
