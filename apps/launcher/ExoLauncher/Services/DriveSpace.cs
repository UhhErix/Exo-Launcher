using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Drives' "what's using space", without walking whole drives: games by the size their store reports, shader caches
/// matched to them (Direct3D's per-program caches name the program in a small SQLite file; Steam's are folders named by
/// app id), the graphics driver's own caches, Exo's own folder, and leftovers (a Steam library's game folder no installed
/// game owns); Other is what's left of the used space. Old shader caches (of a program that's gone, or an older copy of
/// one unused for a month; never Exo Shaders' compile copies', which it handles itself) are Clean up's
/// (<see cref="JunkFiles"/>), as are Exo's old app backups: nothing else here is
/// ever deleted, and never a game's files. For a game that loads through DirectStorage (it ships dstorage.dll), its
/// biggest files are asked whether they can use the fast path (<see cref="Drives.BypassOf"/>). Read at most every ten
/// minutes, never while a game whose files it would open runs.
/// </summary>
internal static partial class DriveSpace
{
    /// <summary>A shader cache folder: the program it is for (Direct3D's) or the Steam app (Steam's), its size and last write.</summary>
    internal sealed record CacheFolder(string Path, string? Exe, string? SteamApp, long Bytes, DateTime Written);

    internal sealed record Report(
        IReadOnlyList<Drives.Game> Games, IReadOnlyList<CacheFolder> Caches, IReadOnlyList<CacheFolder> Old,
        IReadOnlyDictionary<string, long> Exo, long ExoOld, IReadOnlyDictionary<string, long> DriverCaches, IReadOnlyDictionary<string, long> Leftovers);

    /// <summary>A copy of a program unused this long, while a newer one of it is in use, is an old build's.</summary>
    internal static readonly TimeSpan OldBuild = TimeSpan.FromDays(30);

    private const long BigFile = 64L << 20;

    // ---- Decisions ----

    /// <summary>
    /// The old caches: Steam's for an app no longer installed; Direct3D's for a program that's gone from a drive that's
    /// there (an unplugged drive says nothing); and of several caches for one program (the same path in any case), each
    /// older one unused for <see cref="OldBuild"/>. A cache whose program can't be read is kept, and so is one of a program
    /// in Exo Shaders' data folder (<paramref name="shaders"/>, null when it isn't there): a compile's staged copy, whose
    /// cache Exo Shaders itself moves into its game's or deletes once the compile is done.
    /// </summary>
    internal static List<CacheFolder> Old(IReadOnlyList<CacheFolder> caches, Func<string, bool?> programThere, Func<string, bool> steamInstalled, DateTime now, string? shaders)
    {
        var inShaders = shaders is null ? null : Path.TrimEndingDirectorySeparator(shaders) + Path.DirectorySeparatorChar;
        caches = [.. caches.Where(c => inShaders is null || c.Exe is null || !Plain(c.Exe).StartsWith(inShaders, StringComparison.OrdinalIgnoreCase))];
        var old = new List<CacheFolder>();
        foreach (var cache in caches)
        {
            if (cache.SteamApp is { } app) { if (!steamInstalled(app)) old.Add(cache); }
            else if (cache.Exe is { } exe && programThere(Plain(exe)) == false) old.Add(cache);
        }
        foreach (var copies in caches.Where(c => c.Exe is not null && c.SteamApp is null && !old.Contains(c)).GroupBy(c => Plain(c.Exe!), StringComparer.OrdinalIgnoreCase))
        {
            var newest = copies.Max(c => c.Written);
            old.AddRange(copies.Where(c => c.Written < newest && now - c.Written > OldBuild));
        }
        return old;
    }

    /// <summary>The library game a cache is for: by Steam app id, else the game whose install folder holds its program (the deepest).</summary>
    internal static string? GameOf(CacheFolder cache, IReadOnlyList<(string Id, string? Folder, string? SteamApp)> games)
    {
        if (cache.SteamApp is { } app) return games.FirstOrDefault(g => g.SteamApp == app).Id;
        if (cache.Exe is null) return null;
        var exe = Plain(cache.Exe);
        return games.Where(g => g.Folder is { Length: > 3 } folder && exe.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Folder!.Length).Select(g => g.Id).FirstOrDefault();
    }

    /// <summary>A program path without Windows' \\?\ prefix.</summary>
    internal static string Plain(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    /// <summary>The drive letter a path is on ("C:"), or "" for none.</summary>
    internal static string LetterOf(string? path) => path is { Length: >= 2 } && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) + ":" : "";

    // ---- Reading this PC ----

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (Report Report, DateTimeOffset At)? _last;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    /// <summary>The space report, from memory for ten minutes (a removed old cache reads again at once).</summary>
    internal static async Task<Report> ReadAsync(IReadOnlyList<GameEntry> library, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_last is { } last && DateTimeOffset.UtcNow - last.At < Fresh) return last.Report;
            var report = await Task.Run(() => Scan(library), ct).ConfigureAwait(false);
            _last = (report, DateTimeOffset.UtcNow);
            return report;
        }
        finally { Gate.Release(); }
    }

    private static void Forget() => _last = null;

    private static Report Scan(IReadOnlyList<GameEntry> library)
    {
        var installed = library.Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path)).ToList();
        var owners = installed.Select(g => (g.Id, (string?)g.Path, g.Store == StoreKind.Steam ? g.LaunchTarget : null)).ToList();
        var (caches, old) = Caches();
        var cacheOf = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var cache in caches)
            if (GameOf(cache, owners) is { } id) cacheOf[id] = cacheOf.GetValueOrDefault(id) + cache.Bytes;
        var running = MainWindow.RunningLibraryGames();
        var games = installed.Select(g =>
        {
            var (uses, blocked, files) = running.ContainsKey(g.Id) ? (false, null, 0) : DirectStorageOf(g.Path!);
            return new Drives.Game(g.Id, g.Title, LetterOf(g.Path), g.SizeBytes ?? 0, cacheOf.GetValueOrDefault(g.Id), uses, blocked, files);
        }).ToList();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var exoRoot = PathHelper.AppDataDir;
        var exo = new Dictionary<string, long> { [LetterOf(exoRoot)] = Size(exoRoot, skip: Path.Combine(exoRoot, "Games")) };
        var driver = new Dictionary<string, long> { [LetterOf(local)] = DriverCacheFolders(local).Sum(f => Size(f)) };
        return new Report(games, caches, old, exo, ExoOldFolders().Sum(f => Size(f)), driver, Leftovers(installed));
    }

    /// <summary>
    /// Every shader cache this PC keeps per program, and the old ones among them. <paramref name="sizeOldOnly"/>: only the old
    /// ones are measured (<c>All</c> then has no sizes), which is all the junk count and its clean need; walking every cache for a
    /// size nothing reads was most of a junk scan.
    /// </summary>
    internal static (IReadOnlyList<CacheFolder> All, List<CacheFolder> Old) Caches(bool sizeOldOnly = false)
    {
        var all = new List<CacheFolder>();
        var d3d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
        foreach (var folder in Folders(d3d))
            all.Add(new CacheFolder(folder, ProgramOf(folder), null, sizeOldOnly ? 0 : Size(folder), Written(folder)));
        var steamInstalled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in SteamLibraries())
        {
            foreach (var manifest in Files(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
                if (ManifestId().Match(Path.GetFileName(manifest)) is { Success: true } m) steamInstalled.Add(m.Groups[1].Value);
            foreach (var folder in Folders(Path.Combine(library, "steamapps", "shadercache")))
                if (Path.GetFileName(folder) is { } app && app.All(char.IsAsciiDigit))
                    all.Add(new CacheFolder(folder, null, app, sizeOldOnly ? 0 : Size(folder), Written(folder)));
        }
        var old = Old(all, ProgramThere, steamInstalled.Contains, DateTime.UtcNow, Directory.Exists(ScsKillerStatus.DataDir) ? ScsKillerStatus.DataDir : null);
        if (sizeOldOnly) old = [.. old.Select(cache => cache with { Bytes = Size(cache.Path) })];
        return (all, old);
    }

    /// <summary>A program on a drive that's there: whether it still exists; null when its drive isn't (unplugged, a share).</summary>
    private static bool? ProgramThere(string exe)
    {
        try { return Path.GetPathRoot(exe) is { Length: > 0 } root && Directory.Exists(root) ? File.Exists(exe) : null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly ConcurrentDictionary<string, string?> Programs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The program a Direct3D cache folder is for: its small database's app_id table names it. The database (and its
    /// write-ahead log) is copied first, since Windows' shader compiler may hold it open, and read from the copy. A folder's
    /// program never changes, so each is read once.
    /// </summary>
    private static string? ProgramOf(string folder) => Programs.GetOrAdd(folder, static path =>
    {
        var database = Files(path, "*.dxcache").Where(f => !Path.GetFileName(f).Contains("_VEN_", StringComparison.OrdinalIgnoreCase)).OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
        if (database is null) return null;
        var copy = Directory.CreateTempSubdirectory("exo-d3dcache-").FullName;
        try
        {
            var target = Path.Combine(copy, "cache.db");
            File.Copy(database, target);
            if (File.Exists(database + "-wal")) File.Copy(database + "-wal", target + "-wal");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            connection.Open();
            ExoSqlite.ApplyReadOnlyPragmas(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT exe_path FROM app_id LIMIT 1";
            return command.ExecuteScalar() as string is { Length: > 3 } exe ? exe : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException) { return null; }
        finally
        {
            try { DeletionGate.DeleteDirectory(copy, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    });

    /// <summary>The size of the graphics drivers' own caches now.</summary>
    /// <summary>The size of Direct3D's own shader cache folder now.</summary>
    internal static long DirectXCacheBytes() => Size(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"));

    internal static long DriverCacheBytes() => DriverCacheFolders(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).Sum(f => Size(f));

    /// <summary>The graphics drivers' own caches (NVIDIA's DXCache, GLCache and per-driver folder, AMD's, Intel's): shown, never deleted.</summary>
    private static IEnumerable<string> DriverCacheFolders(string local)
    {
        var low = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
        return new[]
        {
            Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"), Path.Combine(low, "NVIDIA", "PerDriverVersion"),
            Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"), Path.Combine(local, "AMD", "VkCache"), Path.Combine(local, "AMD", "GLCache"),
            Path.Combine(local, "Intel", "ShaderCache"),
        }.Where(Directory.Exists);
    }

    /// <summary>Exo's own app backups: what an update or a developer swap set aside (app.previous-*, app.incoming-*), never the app it runs from.</summary>
    internal static IEnumerable<string> ExoOldFolders()
    {
        var root = PathHelper.AppDataDir;
        var running = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return Folders(root).Where(f => Path.GetFileName(f) is { } name
            && (name.StartsWith("app.previous", StringComparison.OrdinalIgnoreCase) || name.StartsWith("app.incoming", StringComparison.OrdinalIgnoreCase))
            && !running.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A Steam library's game folders that no installed game's manifest names and nothing wrote to in two days: what an
    /// uninstall left (saves, mods, configs). Sized by drive, shown, never deleted.
    /// </summary>
    private static Dictionary<string, long> Leftovers(IReadOnlyList<GameEntry> installed)
    {
        var bytes = new Dictionary<string, long>();
        foreach (var library in SteamLibraries())
        {
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var read = true;
            foreach (var manifest in Files(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
            {
                try { if (InstallDir().Match(File.ReadAllText(manifest)) is { Success: true } m) kept.Add(m.Groups[1].Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { read = false; }
            }
            // A manifest that couldn't be read could name any folder: none of this library's is called left over.
            if (!read) continue;
            foreach (var folder in Folders(Path.Combine(library, "steamapps", "common")))
            {
                if (kept.Contains(Path.GetFileName(folder)) || installed.Any(g => string.Equals(Path.TrimEndingDirectorySeparator(g.Path!), folder, StringComparison.OrdinalIgnoreCase))) continue;
                if (DateTime.UtcNow - Written(folder) < TimeSpan.FromDays(2)) continue;
                var letter = LetterOf(folder);
                bytes[letter] = bytes.GetValueOrDefault(letter) + Size(folder);
            }
        }
        return bytes;
    }

    private static IEnumerable<string> SteamLibraries() =>
        Adapters.SteamInstallation.ResolveRoot() is { } root ? SteamLeftoverCleanup.CollectLibraries(root) : [];

    /// <summary>
    /// Whether a game loads through DirectStorage (dstorage.dll within four folders of its install), and if so what keeps its
    /// big files (64 MB and up) off the fast path: its biggest file asked, and its biggest sparse, compressed or linked one.
    /// A block of the whole stack (a filter driver, the volume) counts every big file; a file's own state, the files like it.
    /// </summary>
    private static (bool Uses, Drives.Blocked? Blocked, int Files) DirectStorageOf(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return (false, null, 0);
            var shallow = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            if (!Directory.EnumerateFiles(folder, "dstorage.dll", shallow).Any()) return (false, null, 0);
            var big = new FileSystemEnumerable<(string Path, long Length, FileAttributes Attributes)>(folder,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.Attributes), new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.Length >= BigFile,
                ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            }.OrderByDescending(f => f.Length).ToList();
            if (big.Count == 0) return (true, null, 0);
            const FileAttributes odd = FileAttributes.SparseFile | FileAttributes.Compressed | FileAttributes.ReparsePoint | FileAttributes.Encrypted;
            if (Drives.BypassOf(big[0].Path) is { } stack && (big[0].Attributes & odd) == 0) return (true, stack, big.Count);
            var flagged = big.Where(f => (f.Attributes & odd) != 0).ToList();
            return flagged.Count > 0 && Drives.BypassOf(flagged[0].Path) is { } own ? (true, own, flagged.Count) : (true, null, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (false, null, 0); }
    }

    // ---- Clean up (JunkFiles) ----

    internal static (long Bytes, int Folders) ExoOld()
    {
        var folders = ExoOldFolders().ToList();
        return (folders.Sum(f => Size(f)), folders.Count);
    }

    /// <summary>Deletes Exo's old app backups (<paramref name="which"/>: only those it picks); what went, and what stayed to <paramref name="failed"/>.</summary>
    internal static (long Bytes, int Folders) CleanExoOld(Func<string, bool>? which = null, List<string>? failed = null)
    {
        long bytes = 0;
        var count = 0;
        foreach (var folder in ExoOldFolders().Where(which ?? (_ => true)).ToList())
        {
            var size = Size(folder);
            if (!Remove(PathHelper.AppDataDir, folder, failed)) continue;
            bytes += size;
            count++;
        }
        Forget();
        return (bytes, count);
    }

    /// <summary>One folder deleted inside its root, checked to go through no folder link (<see cref="RecursiveDeleteGuard"/>); a delete that failed goes to <paramref name="failed"/>.</summary>
    private static bool Remove(string root, string folder, List<string>? failed = null)
    {
        if (!RecursiveDeleteGuard.TryValidateManagedChild(root, folder, out var safe, out var why))
        {
            AppLog.Debug($"Drives: {folder} left: {why}");
            return false;
        }
        if (DeletionGate.Clean(safe, null, out var kept)) return true;
        failed?.Add(folder);
        AppLog.Debug($"Drives: {folder} not removed: {kept}");
        return false;
    }

    // ---- Files ----

    private static IEnumerable<string> Folders(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Files(string root, string pattern)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateFiles(root, pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A folder's size: its files' lengths, through no folder link (<paramref name="skip"/>: a subfolder left out).</summary>
    private static long Size(string folder, string? skip = null)
    {
        try
        {
            return new FileSystemEnumerable<long>(folder, (ref FileSystemEntry e) => e.Length, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry e) => skip is null || !e.ToFullPath().Equals(skip, StringComparison.OrdinalIgnoreCase),
            }.Sum();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The newest write to anything in a folder (its own time when it holds nothing).</summary>
    private static DateTime Written(string folder)
    {
        try
        {
            var newest = Directory.GetLastWriteTimeUtc(folder);
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (file.LastWriteTimeUtc > newest) newest = file.LastWriteTimeUtc;
            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.UtcNow; }
    }

    [GeneratedRegex(@"^appmanifest_(\d{1,10})\.acf$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestId();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDir();
}
