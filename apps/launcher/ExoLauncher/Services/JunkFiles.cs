using System.Runtime.InteropServices;

namespace ExoLauncher.Services;

/// <summary>
/// Files nothing needs, as PC › Needs you's Junk files counts them: temporary files a day old or more (this user's and
/// Windows'), crash dumps and error reports, Explorer's thumbnail cache (rebuilt as you browse), old shader caches (of a
/// program that's gone, or an older copy of one unused for a month: <see cref="DriveSpace"/>), Exo's own old app backups,
/// and the Recycle Bin. A file in use stays, a folder link is never followed, and a shader cache still in use is left
/// alone: games load faster with it (Exo Shaders keeps them). Windows' own folders need Exo to run as administrator;
/// without it, what can't be deleted stays. All of it also goes by itself while the PC is idle (<see cref="JunkAutoClean"/>):
/// temporary files, crash reports, the thumbnail cache and Exo's old backups each day, old shader caches and what has sat
/// in the Recycle Bin a month each week; the whole Recycle Bin only on a press.
/// </summary>
internal static partial class JunkFiles
{
    internal sealed record Found(string Id, string Name, string Detail, long Bytes, int Files);

    private static readonly TimeSpan Fresh = TimeSpan.FromDays(1);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string Common => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static (string Id, string Name, string Detail, Func<IEnumerable<string>> Folders, string Pattern, bool OldOnly)[] Kinds =>
    [
        ("temp", "Temporary files", "Left by installers and apps, a day old or more", () => [Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")], "*", true),
        ("crash", "Crash dumps and error reports", "Kept after an app or game crashed", () =>
        [
            Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue"),
            Path.Combine(Common, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Common, @"Microsoft\Windows\WER\ReportQueue"),
        ], "*", false),
        ("thumbs", "Thumbnail cache", "Picture previews Explorer makes again as you browse", () => [Path.Combine(Local, @"Microsoft\Windows\Explorer")], "thumbcache_*.db", false),
        ("browsers", "Browser caches", "Disposable web and GPU caches for Brave, Chrome, Helium, Edge, Vivaldi, and Opera GX (keeps logins and cookies)", () => BrowserCacheFolders(Local, Roaming), "*", false),
        ("launchers", "Game launcher caches", "Disposable web caches and logs for Steam, Epic, Battle.net, Ubisoft, and Discord (keeps games and logins)", () => LauncherCacheFolders(Local, Roaming, ProgramFilesX86, Common), "*", false),
    ];

    private static readonly string[] ChromiumProfileCacheLeaves =
    [
        @"Cache\Cache_Data",
        @"Code Cache\js",
        @"Code Cache\wasm",
        "GPUCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "GrShaderCache",
        "GraphiteDawnCache",
        "ShaderCache",
    ];

    private static readonly string[] ChromiumUserDataRoots =
    [
        @"BraveSoftware\Brave-Browser\User Data",
        @"Google\Chrome\User Data",
        @"imput\Helium\User Data",
        @"Helium\User Data",
        @"Microsoft\Edge\User Data",
        @"Vivaldi\User Data",
    ];

    private static bool SafeExistingDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void AddExistingCacheLeaves(string profileDir, IReadOnlyList<string> leaves, List<string> target)
    {
        foreach (var leaf in leaves)
        {
            var candidate = Path.Combine(profileDir, leaf);
            if (SafeExistingDirectory(candidate)) target.Add(candidate);
        }
    }

    internal static IReadOnlyList<string> BrowserCacheFolders(string local, string roaming)
    {
        var folders = new List<string>();
        foreach (var rel in ChromiumUserDataRoots)
        {
            var userData = Path.Combine(local, rel);
            if (!SafeExistingDirectory(userData)) continue;
            AddExistingCacheLeaves(userData, ["GrShaderCache", "GraphiteDawnCache", "ShaderCache"], folders);
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(userData))
                {
                    if (!SafeExistingDirectory(sub)) continue;
                    var name = Path.GetFileName(sub);
                    if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                        AddExistingCacheLeaves(sub, ChromiumProfileCacheLeaves, folders);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var operaRoot in new[] { Path.Combine(local, @"Opera Software\Opera GX Stable"), Path.Combine(roaming, @"Opera Software\Opera GX Stable") })
        {
            if (SafeExistingDirectory(operaRoot))
                AddExistingCacheLeaves(operaRoot, ChromiumProfileCacheLeaves, folders);
        }
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyList<string> LauncherCacheFolders(string local, string roaming, string programFilesX86, string common)
    {
        var folders = new List<string>();
        string[] fixedPaths =
        [
            Path.Combine(local, @"Steam\htmlcache\Cache\Cache_Data"),
            Path.Combine(local, @"Steam\htmlcache\Code Cache"),
            Path.Combine(local, @"Steam\htmlcache\GPUCache"),
            Path.Combine(programFilesX86, @"Steam\appcache\httpcache"),
            Path.Combine(local, @"EpicGamesLauncher\Saved\Logs"),
            Path.Combine(local, @"Battle.net\BrowserCache"),
            Path.Combine(common, @"Blizzard Entertainment\Battle.net\Cache"),
            Path.Combine(local, @"Ubisoft Game Launcher\cache\http2"),
        ];
        foreach (var path in fixedPaths)
            if (SafeExistingDirectory(path)) folders.Add(path);

        var epicSaved = Path.Combine(local, @"EpicGamesLauncher\Saved");
        if (SafeExistingDirectory(epicSaved))
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(epicSaved, "webcache*"))
                    if (SafeExistingDirectory(sub)) folders.Add(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string[] discordLeaves = [@"Cache\Cache_Data", @"Code Cache\js", @"Code Cache\wasm", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];
        foreach (var channel in new[] { "discord", "discordptb", "discordcanary" })
        {
            var discordRoot = Path.Combine(roaming, channel);
            if (SafeExistingDirectory(discordRoot))
                AddExistingCacheLeaves(discordRoot, discordLeaves, folders);
        }
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>How much of each kind there is now, the Recycle Bin last; kinds with nothing are left out. The kinds walk different folders, so they are counted side by side.</summary>
    internal static List<Found> Scan()
    {
        var kinds = Kinds.Select(kind => Task.Run(() =>
        {
            long bytes = 0;
            var files = 0;
            foreach (var file in FilesOf(kind.Folders(), kind.Pattern, kind.OldOnly)) { bytes += file.Length; files++; }
            return files > 0 ? new Found(kind.Id, kind.Name, kind.Detail, bytes, files) : null;
        })).ToArray();
        var backups = Task.Run(DriveSpace.ExoOld);
        var found = new List<Found>();
        foreach (var kind in kinds) if (kind.GetAwaiter().GetResult() is { } counted) found.Add(counted);
        if (backups.GetAwaiter().GetResult() is { Folders: > 0 } kept)
            found.Add(new Found("exo", "Exo's old backups", "Copies of Exo set aside by an update", kept.Bytes, kept.Folders));
        var bin = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
        if (SHQueryRecycleBinW(null, ref bin) == 0 && bin.Items > 0)
            found.Add(new Found("bin", "Recycle Bin", "What you deleted, gone for good", bin.Bytes, (int)Math.Min(bin.Items, int.MaxValue)));
        return found;
    }

    // A press and the automatic clean (JunkAutoClean) never delete at once.
    private static readonly Lock Cleaning = new();

    /// <summary>How many files one clean remembers as stayed, for the message that names who holds them.</summary>
    private const int MaxFailed = 100;

    /// <summary>
    /// One kind deleted: what went (bytes, files). Files in use or out of reach stay; their paths go to
    /// <paramref name="failed"/> when given (the clean's message names who holds them, the log their names).
    /// </summary>
    internal static (long Bytes, int Files) Clean(string id, List<string>? failed = null)
    {
        lock (Cleaning) return CleanNow(id, failed);
    }

    /// <summary>
    /// What has sat in this user's Recycle Bin, on every fixed drive, longer than <paramref name="age"/> deleted for good,
    /// as Windows' Storage Sense does: each item's $I record says when it went in, and the item ($R) goes with its record.
    /// </summary>
    internal static (long Bytes, int Files) CleanOldBin(TimeSpan age)
    {
        lock (Cleaning) return OldBin(age, DateTime.UtcNow);
    }

    private static (long Bytes, int Files) OldBin(TimeSpan age, DateTime now)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value is not { } sid) return (0, 0);
        long bytes = 0;
        var files = 0;
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
        {
            var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            List<string> records;
            try { records = Directory.Exists(bin) ? [.. Directory.EnumerateFiles(bin, "$I*")] : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var record in records)
            {
                try
                {
                    if (DeletedAt(File.ReadAllBytes(record)) is not { } at || now - at < age) continue;
                    var item = Path.Combine(bin, "$R" + Path.GetFileName(record)[2..]);
                    long size = 0;
                    if (Directory.Exists(item))
                    {
                        size = new DirectoryInfo(item).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                            .Sum(file => file.Length);
                        if (!DeletionGate.Clean(item, null, out _)) continue;
                    }
                    else if (File.Exists(item))
                    {
                        size = new FileInfo(item).Length;
                        if (!DeletionGate.Clean(item, null, out _)) continue;
                    }
                    if (!DeletionGate.Clean(record, null, out _)) continue;
                    bytes += size;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return (bytes, files);
    }

    /// <summary>When an item went into the Recycle Bin: its $I record holds its version (1 or 2), its size, then that time (a FILETIME, UTC).</summary>
    internal static DateTime? DeletedAt(byte[] record)
    {
        if (record.Length < 24 || BitConverter.ToInt64(record, 0) is not (1 or 2)) return null;
        var time = BitConverter.ToInt64(record, 16);
        return time > 0 && time < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(time) : null;
    }

    /// <summary>Exo's old backups that <paramref name="which"/> picks deleted (the automatic clean's: <see cref="JunkAutoClean.BackupOld"/>).</summary>
    internal static (long Bytes, int Files) CleanExoBackups(Func<string, bool> which)
    {
        lock (Cleaning) return DriveSpace.CleanExoOld(which);
    }

    private static (long Bytes, int Files) CleanNow(string id, List<string>? failed)
    {
        if (id == "bin")
        {
            var bin = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
            SHQueryRecycleBinW(null, ref bin);
            // No confirmation, progress or sound of Windows' own: Exo asked first.
            return DeletionNative.EmptyRecycleBin() ? (bin.Bytes, (int)Math.Min(bin.Items, int.MaxValue)) : (0, 0);
        }
        if (id == "exo") return DriveSpace.CleanExoOld(failed: failed);
        if (Kinds.FirstOrDefault(kind => kind.Id == id) is not { Id: not null } chosen) return (0, 0);
        long bytes = 0;
        var files = 0;
        var folders = chosen.Folders().ToList();
        foreach (var file in FilesOf(folders, chosen.Pattern, chosen.OldOnly).ToList())
        {
            var length = file.Length;
            if (!DeletionGate.Clean(file.FullName, null, out _))
            {
                if (failed is { Count: < MaxFailed }) failed.Add(file.FullName);
                continue;
            }
            bytes += length;
            files++;
        }
        // Folders the files left empty go too; the kind's own folders stay.
        foreach (var folder in folders.Where(Directory.Exists)) RemoveEmpty(folder, keep: true);
        return (bytes, files);
    }

    /// <summary>
    /// The files of a kind. Temporary files (<paramref name="oldOnly"/>) go by folder: a folder anything wrote to in the
    /// last day is some tool's work in progress (a build, a setup waiting for a restart) and stays whole; loose files go by age.
    /// </summary>
    private static IEnumerable<FileInfo> FilesOf(IEnumerable<string> folders, string pattern, bool oldOnly)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var cutoff = DateTime.UtcNow - Fresh;
        bool Old(FileInfo file) => file.LastWriteTimeUtc < cutoff && file.LastAccessTimeUtc < cutoff;
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            List<FileInfo> files;
            try
            {
                var root = new DirectoryInfo(folder);
                if (!oldOnly) files = [.. root.EnumerateFiles(pattern, options)];
                else
                {
                    files = [.. root.EnumerateFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = options.AttributesToSkip }).Where(Old)];
                    foreach (var sub in root.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = options.AttributesToSkip }))
                    {
                        var inside = sub.EnumerateFiles("*", options).ToList();
                        if (inside.All(Old)) files.AddRange(inside);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static void RemoveEmpty(string folder, bool keep)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) RemoveEmpty(sub, keep: false);
            if (!keep && !Directory.EnumerateFileSystemEntries(folder).Any()) DeletionGate.Clean(folder, null, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? root, ref RecycleInfo info);
}
