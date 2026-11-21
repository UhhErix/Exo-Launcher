using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Compress a game: Windows' own transparent file compression (WOF, what <c>compact /exe</c> uses), which the game reads as
/// it always did, bytes unchanged, usually 20 to 60% smaller. This half is the decisions, pure and fed values: where it may
/// run, which algorithm, which files, what it saves. <see cref="GameCompressor"/> does the work and
/// <see cref="GameCompressionNative"/> talks to Windows.
///
/// The rules, each one a test in GameCompressionTests:
/// <list type="bullet">
/// <item>Algorithm (<see cref="Choose"/>): XPRESS8K by default. XPRESS4K on a processor of four threads or fewer (cheapest to
/// read back). LZX for a big game that has stopped changing (30 GB or more, nothing written for 30 days) on a processor of
/// 12 threads or more, and on a hard drive for any 10 GB game on 8 threads (a spinning disk reads fewer bytes, so it loads
/// faster). Never LZX for a game written to in the last 30 days: every update writes the files back uncompressed and
/// compressing them again is another full write, the case an SSD should not pay twice for.</item>
/// <item>Files left as they are (<see cref="Decide"/>): one cluster or smaller (nothing to save); an extension the sample
/// found to save under 5% (<see cref="PoorGain"/>); sparse, encrypted or already NTFS-compressed (LZNT1) files and links;
/// a file already in the chosen algorithm (that is how a stopped job resumes); in a game with DirectStorage, its files of
/// 64 MB and up (DirectStorage's fast path refuses compressed files); in a game with anti-cheat the player allowed, every
/// program and library and every anti-cheat file and folder.</item>
/// <item>Anti-cheat (<see cref="AntiCheatCheck.Change.Compress"/>): a game with none, all of it. One with anti-cheat, or
/// possibly Valve's, only after the player allows it for that game, naming that exact set; and then only its data, never
/// a program. Riot, Vanguard and a game Exo couldn't check: never.</item>
/// <item>Never: a drive root, Windows' folder, OneDrive, Microsoft Store (WindowsApps) installs, a drive that isn't a
/// local NTFS one (<see cref="Refusal"/>).</item>
/// </list>
/// </summary>
internal static class GameCompression
{
    /// <summary>WOF's own numbers (FILE_PROVIDER_COMPRESSION_*).</summary>
    internal enum Algorithm { Xpress4K = 0, Lzx = 1, Xpress8K = 2, Xpress16K = 3 }

    /// <summary>The chunk WOF compresses on its own (LZX: 32 KB).</summary>
    internal static int ChunkOf(Algorithm algorithm) => algorithm switch
    {
        Algorithm.Xpress4K => 4096,
        Algorithm.Xpress8K => 8192,
        Algorithm.Xpress16K => 16384,
        _ => 32768,
    };

    internal static string Name(Algorithm algorithm) => algorithm switch
    {
        Algorithm.Xpress4K => "XPRESS4K",
        Algorithm.Xpress8K => "XPRESS8K",
        Algorithm.Xpress16K => "XPRESS16K",
        _ => "LZX",
    };

    internal static Algorithm? Parse(string? name) => name?.ToUpperInvariant() switch
    {
        "XPRESS4K" => Algorithm.Xpress4K,
        "XPRESS8K" => Algorithm.Xpress8K,
        "XPRESS16K" => Algorithm.Xpress16K,
        "LZX" => Algorithm.Lzx,
        _ => null,
    };

    internal const long GB = 1L << 30;
    internal static readonly TimeSpan Settled = TimeSpan.FromDays(14);

    /// <summary>The algorithm for this game on this PC (the rules in the class summary).</summary>
    internal static Algorithm Choose(long installBytes, int logicalCores, bool hardDrive, DateTime newestWriteUtc, DateTime nowUtc)
    {
        if (logicalCores <= 4) return Algorithm.Xpress4K;
        var settled = nowUtc - newestWriteUtc >= Settled;
        if (hardDrive) return installBytes >= 10 * GB && logicalCores >= 8 && settled ? Algorithm.Lzx : Algorithm.Xpress16K;
        if (installBytes >= 15 * GB && logicalCores >= 8 && settled) return Algorithm.Lzx;
        return logicalCores >= 8 ? Algorithm.Xpress16K : Algorithm.Xpress8K;
    }

    /// <summary>Automatic CPU budget: 1 thread on HDD, capped low while idle so background compression stays unnoticeable, higher when triggered explicitly.</summary>
    internal static int Threads(int logicalCores, bool hardDrive, bool idle)
    {
        if (hardDrive) return 1;
        var cores = Math.Max(1, logicalCores);
        return idle ? Math.Clamp(cores / 4, 1, 6) : Math.Clamp(cores / 2, 1, 12);
    }

    // ------------------------------------------------------------------ where

    /// <summary>
    /// Why this install can't be compressed, one short sentence, or null. <paramref name="format"/>: the volume's file
    /// system ("NTFS"); <paramref name="fixedDrive"/>: a local fixed or removable disk, not a network share.
    /// </summary>
    internal static string? Refusal(string root, string? format, bool fixedDrive, string windowsDir, IEnumerable<string> oneDrives)
    {
        var full = Path.TrimEndingDirectorySeparator(root);
        if (Path.GetPathRoot(root) is { } driveRoot && string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), full, StringComparison.OrdinalIgnoreCase))
            return "Exo doesn't compress a whole drive.";
        if (!fixedDrive) return "Exo compresses only games on this PC's own drives.";
        if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)) return "This drive can't compress files (it isn't NTFS).";
        bool Inside(string folder) => folder.Length > 0 &&
            (full + "\\").StartsWith(Path.TrimEndingDirectorySeparator(folder) + "\\", StringComparison.OrdinalIgnoreCase);
        if (Inside(windowsDir)) return "Exo doesn't compress Windows' own folder.";
        if (full.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) || full.EndsWith("\\WindowsApps", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Store games keep their own files.";
        if (oneDrives.Any(Inside)) return "Exo doesn't compress what OneDrive syncs.";
        return null;
    }

    // ------------------------------------------------------------------ which files

    /// <summary>One file of the install as the walk saw it. <paramref name="Current"/>: its WOF algorithm, null when not WOF.</summary>
    internal sealed record FileFact(string Relative, long Length, FileAttributes Attributes, DateTime WrittenUtc, Algorithm? Current, long OnDisk);

    /// <summary>Why a file stays as it is (or None: it is compressed).</summary>
    internal enum Skip { None, Done, Small, Poor, Odd, Program, AntiCheat, DirectStorage }

    /// <summary>How a game's anti-cheat limits what is compressed: all of it, its data only, or nothing.</summary>
    internal enum Guard { All, DataOnly, Nothing }

    /// <summary>The guard from the one answer: none, all; found (or possibly Valve's) and allowed, data only; else nothing.</summary>
    internal static Guard GuardFor(GameEntry game, AntiCheatCheck.Answer answer, string? consented) =>
        !AntiCheatCheck.Allows(game, answer, AntiCheatCheck.Change.Compress, consented) ? Guard.Nothing
        : answer.Kind == AntiCheatCheck.Kind.None ? Guard.All
        : Guard.DataOnly;

    /// <summary>Programs and libraries: the code an anti-cheat checks. Left as they are in a game with anti-cheat.</summary>
    internal static readonly IReadOnlySet<string> ProgramExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl", ".scr", ".efi", ".asi", ".so", ".node", ".com", ".bat", ".cmd", ".ps1",
    };

    /// <summary>DirectStorage reads files this big through its fast path, which refuses compressed ones.</summary>
    internal const long DirectStorageBig = 64L << 20;

    /// <summary>An extension saving less than this in the sample is left alone.</summary>
    internal const double PoorGain = 0.05;

    internal static string ExtensionOf(string relative)
    {
        var ext = Path.GetExtension(relative);
        return ext.Length is > 1 and <= 16 ? ext.ToLowerInvariant() : "";
    }

    /// <summary>The decision for one file. <paramref name="poor"/>: extensions the sample found poor.</summary>
    internal static Skip Decide(FileFact file, Algorithm target, long cluster, Guard guard, bool directStorage, IReadOnlySet<string> poor)
    {
        if (file.Current == target) return Skip.Done;
        const FileAttributes odd = FileAttributes.SparseFile | FileAttributes.Encrypted | FileAttributes.Compressed;
        if ((file.Attributes & odd) != 0) return Skip.Odd;
        // A reparse point that isn't WOF's is a link; WOF's own one (another algorithm) is compressed again.
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 && file.Current is null) return Skip.Odd;
        if (file.Length <= Math.Max(cluster, 4096)) return Skip.Small;
        if (guard == Guard.DataOnly)
        {
            if (ProgramExtensions.Contains(ExtensionOf(file.Relative))) return Skip.Program;
            if (IsAntiCheatPath(file.Relative)) return Skip.AntiCheat;
        }
        if (directStorage && file.Length >= DirectStorageBig) return Skip.DirectStorage;
        if (poor.Contains(ExtensionOf(file.Relative))) return Skip.Poor;
        return Skip.None;
    }

    /// <summary>A file or any folder on its way that the anti-cheat tables name (EasyAntiCheat\, BattlEye\, a _BE launcher).</summary>
    internal static bool IsAntiCheatPath(string relative)
    {
        var parts = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var named = i == parts.Length - 1 ? AntiCheatCheck.ForFile(parts[i]) : AntiCheatCheck.ForFolder(parts[i]);
            if (named is not null) return true;
        }
        return false;
    }

    /// <summary>A plan: the files to compress, and how many bytes stay for each reason.</summary>
    internal sealed record Plan(Algorithm Algorithm, IReadOnlyList<FileFact> Files, IReadOnlyDictionary<Skip, long> Left, long Total, long DoneBytes);

    internal static Plan MakePlan(IReadOnlyList<FileFact> files, Algorithm target, long cluster, Guard guard, bool directStorage, IReadOnlySet<string> poor)
    {
        var chosen = new List<FileFact>();
        var left = new Dictionary<Skip, long>();
        long total = 0, done = 0;
        foreach (var file in files)
        {
            total += file.Length;
            var skip = Decide(file, target, cluster, guard, directStorage, poor);
            if (skip == Skip.None) { chosen.Add(file); continue; }
            if (skip == Skip.Done) done += file.Length;
            left[skip] = left.GetValueOrDefault(skip) + file.Length;
        }
        return new Plan(target, chosen, left, total, done);
    }

    /// <summary>
    /// Files an update wrote back uncompressed since <paramref name="compressedUtc"/>: worth compressing, not WOF, newer than
    /// the last compress. What "compress again" compresses, and nothing else.
    /// </summary>
    internal static (int Files, long Bytes) Changed(IReadOnlyList<FileFact> files, DateTime compressedUtc, Algorithm target, long cluster, Guard guard, bool directStorage, IReadOnlySet<string> poor)
    {
        var count = 0;
        long bytes = 0;
        foreach (var file in files)
        {
            if (file.WrittenUtc <= compressedUtc || file.Current is not null) continue;
            if (Decide(file, target, cluster, guard, directStorage, poor) != Skip.None) continue;
            count++;
            bytes += file.Length;
        }
        return (count, bytes);
    }

    /// <summary>A change worth offering again: 100 MB, or a twentieth of the game.</summary>
    internal static bool WorthAgain(long changed, long install) => changed >= 100L << 20 || (install > 0 && changed >= install / 20 && changed >= 8L << 20);

    // ------------------------------------------------------------------ what it saves

    /// <summary>What one extension's sample came to: bytes read and what WOF's chunks of them would take.</summary>
    internal readonly record struct Sample(long Raw, long Packed)
    {
        internal double Ratio => Raw <= 0 ? 1 : Math.Clamp((double)Packed / Raw, 0, 1);
        internal double Gain => 1 - Ratio;
    }

    /// <summary>
    /// What one chunk costs on disk: its compressed size, or the whole chunk when it doesn't shrink (WOF keeps such a
    /// chunk as it is). <paramref name="packed"/> null: the codec gave up on it.
    /// </summary>
    internal static long ChunkCost(int raw, long? packed) => packed is { } size && size < raw ? size : raw;

    /// <summary>LZX's chunks, sampled with the XPRESS codec at LZX's 32 KB chunk: LZX packs about this much tighter.</summary>
    internal const double LzxOverXpress = 0.93;

    /// <summary>
    /// A file's size once compressed: its chunks at <paramref name="ratio"/>, WOF's chunk table (8 bytes a chunk), rounded
    /// up to the cluster, never more than the file as it is.
    /// </summary>
    internal static long Estimate(long length, double ratio, Algorithm algorithm, long cluster)
    {
        if (length <= 0) return 0;
        var chunk = ChunkOf(algorithm);
        var chunks = (length + chunk - 1) / chunk;
        var packed = (long)Math.Ceiling(length * Math.Clamp(ratio, 0, 1)) + chunks * 8;
        var onDisk = RoundUp(packed, cluster);
        return Math.Min(onDisk, RoundUp(length, cluster));
    }

    internal static long RoundUp(long bytes, long cluster) => cluster <= 0 ? bytes : (bytes + cluster - 1) / cluster * cluster;

    /// <summary>The extensions the sample found poor (under <see cref="PoorGain"/>); one with too small a sample keeps its prior's word.</summary>
    internal static HashSet<string> Poor(IReadOnlyDictionary<string, Sample> samples)
    {
        var poor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ext, sample) in samples)
            if (sample.Raw >= MinSample ? sample.Gain < PoorGain : PriorRatio(ext, Engine.Unknown) > 1 - PoorGain) poor.Add(ext);
        return poor;
    }

    /// <summary>A sample smaller than this says too little; its extension keeps the prior.</summary>
    internal const long MinSample = 64 * 1024;

    /// <summary>
    /// The saving of a plan: each file at its extension's sampled ratio (else the local prior), what stays as it is at its
    /// size on disk. Before and after, in bytes on disk.
    /// </summary>
    internal static (long Before, long After) Saving(IReadOnlyList<FileFact> files, Plan plan, IReadOnlyDictionary<string, Sample> samples, Engine engine, long cluster)
    {
        var chosen = new HashSet<string>(plan.Files.Select(f => f.Relative), StringComparer.OrdinalIgnoreCase);
        long before = 0, after = 0;
        foreach (var file in files)
        {
            var onDisk = file.OnDisk > 0 ? file.OnDisk : RoundUp(file.Length, cluster);
            before += onDisk;
            if (!chosen.Contains(file.Relative)) { after += onDisk; continue; }
            var ext = ExtensionOf(file.Relative);
            var ratio = samples.TryGetValue(ext, out var s) && s.Raw >= MinSample ? s.Ratio : PriorRatio(ext, engine);
            if (plan.Algorithm == Algorithm.Lzx && !(samples.TryGetValue(ext, out var lzx) && lzx.Raw >= MinSample)) ratio *= LzxOverXpress;
            after += Math.Min(onDisk, Estimate(file.Length, ratio, plan.Algorithm, cluster));
        }
        return (before, after);
    }

    // ------------------------------------------------------------------ the local prior, no network

    /// <summary>The engine, read off the file names alone: it moves a few extensions' priors.</summary>
    internal enum Engine { Unknown, Unreal, Unity, Source, Source2, Frostbite, Red, IdTech, Godot, Re }

    internal static Engine EngineOf(IEnumerable<string> relatives)
    {
        foreach (var path in relatives)
        {
            // Rooted with a separator, so a folder at the install's top matches "\game\" too.
            var p = "\\" + path.Replace('/', '\\');
            var name = Path.GetFileName(p);
            if (p.Contains("\\Content\\Paks\\", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase)) return Engine.Unreal;
            if (name.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase)) return Engine.Unity;
            if (name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)) return p.Contains("\\game\\", StringComparison.OrdinalIgnoreCase) ? Engine.Source2 : Engine.Source;
            if (name.EndsWith(".cas", StringComparison.OrdinalIgnoreCase) && p.Contains("\\Data\\", StringComparison.OrdinalIgnoreCase)) return Engine.Frostbite;
            if (name.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)) return Engine.Red;
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) && p.Contains("\\base\\", StringComparison.OrdinalIgnoreCase)) return Engine.IdTech;
            if (name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) return Engine.Godot;
            if (name.StartsWith("re_chunk_", StringComparison.OrdinalIgnoreCase)) return Engine.Re;
        }
        return Engine.Unknown;
    }

    /// <summary>
    /// What an extension compresses to under XPRESS8K before anything is sampled: media and archives are already packed
    /// (the extensions CompactGUI's players most often report as poor), program code and plain data shrink a lot, the rest
    /// sits at the median of reported games (about 0.77). Used for the PC room's list before a game is sampled.
    /// </summary>
    internal static double PriorRatio(string ext, Engine engine)
    {
        switch (engine, ext)
        {
            case (Engine.Unreal, ".pak" or ".ucas" or ".utoc"): return 0.94;  // usually Oodle or zlib inside; a sample says
            case (Engine.Unity, ".assets" or ".ress"): return 0.5;
            case (Engine.Unity, ".resource"): return 0.95;                     // audio banks
            case (Engine.Unity, ".bundle"): return 0.9;                        // LZ4 asset bundles
            case (Engine.Source or Engine.Source2, ".vpk"): return 0.6;
            case (Engine.Frostbite, ".cas"): return 0.85;
            case (Engine.Red, ".archive"): return 0.9;
            case (Engine.IdTech, ".resources"): return 0.85;
            case (Engine.Godot, ".pck"): return 0.7;
            case (Engine.Re, ".pak"): return 0.9;
        }
        if (Packed.Contains(ext)) return 0.99;
        if (Shrinks.TryGetValue(ext, out var ratio)) return ratio;
        return 0.77;
    }

    /// <summary>Already-compressed media and archives: video, audio, pictures, archives and audio banks.</summary>
    internal static readonly IReadOnlySet<string> Packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".bk2", ".bik", ".webm", ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".usm", ".ogv", ".mov", ".flv", ".mpg", ".m2v", ".vob",
        ".ogg", ".mp3", ".aac", ".wma", ".flac", ".opus", ".wem", ".bnk", ".bank", ".fsb", ".awb", ".acb", ".xwm",
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".zip", ".7z", ".rar", ".cab", ".gz", ".bz2", ".xz", ".lz", ".lz4", ".zst", ".tgz", ".lzx", ".msi", ".xap",
        ".ucas", ".utoc",
    };

    private static readonly IReadOnlyDictionary<string, double> Shrinks = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = 0.5, [".dll"] = 0.48, [".pdb"] = 0.3, [".so"] = 0.5, [".json"] = 0.25, [".xml"] = 0.2, [".txt"] = 0.35,
        [".ini"] = 0.4, [".lua"] = 0.3, [".js"] = 0.3, [".csv"] = 0.3, [".cfg"] = 0.4, [".dds"] = 0.6, [".tga"] = 0.5,
        [".bmp"] = 0.4, [".wav"] = 0.85, [".pak"] = 0.8, [".assets"] = 0.5, [".resS"] = 0.55, [".bin"] = 0.7,
        [".dat"] = 0.7, [".vpk"] = 0.6, [".uasset"] = 0.55, [".uexp"] = 0.6, [".ubulk"] = 0.75, [".shader"] = 0.4,
    };
}
