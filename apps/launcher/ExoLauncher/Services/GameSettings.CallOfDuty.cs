using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Call of Duty: Modern Warfare 4 (Call of Duty HQ's cod26). Its graphics settings are one text file for the PC,
/// %LOCALAPPDATA%\Activision\Call of Duty\players\s.1.0.cod26.txt, written by the game's own menu. The graphics follow
/// BenchmarKing's "Optimized (Competitive)" list, every setting measured on a 5700X3D with an RTX 3060 Ti 8 GB and a 4070
/// Super 12 GB (youtu.be/tjFDuyc8hSY, October 24 2026): the costly ones low (Shader, Shadow, Volumetric, Reflections), and
/// what costs next to nothing kept up, because lowering it only loses picture (Screen Space Shadows, Occlusion, Surface,
/// Weather, Water, Bullet Impacts, Persistent Effects). Competitive and not his Quality list, because this profile is for
/// multiplayer frames on any card: its other choices below are too. Two of his can't be written: On-demand Texture
/// Streaming is in no text file and Deferred Physics Quality's line stops at Low, so the Optimize line asks for those two
/// in the game's menu. Beyond him, where the guides, benchmarks, shared configs and creators agree (October 2026: PC
/// Gamer's per-setting tests, HDOpti, Hone, metka.gg, cod-mw4.wiki, Dot Esports, esports.gg, Tier1Settings, MurkTweaks',
/// ItalianTweaks' and RivalsXO's configs), set for this PC: the card's own latency switch with V-Sync and frame generation off; render threads for the
/// processor; the full render resolution with the card's own upscaler (DLSS's CNN model on an RTX 20 or 30 at 1440p and
/// 4K, with MW4's NVIDIA profile let off the global override so it really runs; its preset by the resolution the game
/// outputs); no blur of any kind; textures and the video memory budget
/// for the card's memory (an older card without DLSS, FSR 4 or XeSS keeps its own upscaler and render resolution). Borderless by default on the screen's highest refresh (true full screen gains nothing on DX12 under
/// Windows 11 and can hide the game from Exo Capture; borderless runs at the desktop's rate, which PC › Display sets to the
/// top one), the mouse kept in the game with a second screen; or true full screen, where it can't leave anyway, when that's
/// chosen on the line. Never a frame cap. Music and cutscenes muted and effects and hit markers full, so footsteps carry; the
/// upscaler's sharpening at the middle the guides agree on; no intros. Left as the player has them: resolution, field
/// of view, voice and master volume, keys, and the hidden values only one config family sets with no measurement behind
/// them (subdivision, sun-shadow clipmap, upload heaps, corpses).
/// </summary>
internal static partial class GameSettings
{
    internal static string CallOfDutyPlayers =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Activision", "Call of Duty", "players");

    internal static string CallOfDutyDocumentsPlayers =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Call of Duty", "players");

    internal static bool IsCallOfDuty(Models.GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        if (game.Id == "steam:1938090") return true;
        if (CallOfDutyProgram(game) is not null) return true;
        try
        {
            return new[] { game.Path, Path.Combine(game.Path, "_retail_") }
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Any(folder => CallOfDutyTitle().IsMatch(Path.GetFileName(folder)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string FindCallOfDutyConfig(IEnumerable<string>? roots = null)
    {
        var search = roots ?? [CallOfDutyPlayers, CallOfDutyDocumentsPlayers];
        var candidates = new List<string>();
        foreach (var root in search)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "s.1.0.cod*.txt"));
                candidates.AddRange(Directory.EnumerateFiles(root, "options.4.cod*.cst"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? Path.Combine(CallOfDutyDocumentsPlayers, "options.4.cod24.cst");
    }

    internal static Profile CallOfDutyWarzone(Pc pc, string? configFile = null)
    {
        var filePath = configFile ?? FindCallOfDutyConfig();
        return new("cod", "Call of Duty", filePath, file => CallOfDutyGroups(pc, file), ["cod", "ModernWarfare", "BlackOpsColdWar"],
            null, ModernWarfare4Nvidia(), Program: CallOfDutyProgram, Mark: CallOfDutyMark, WindowMode: pc.Fullscreen ? Fullscreen : Borderless,
            WindowLines: ["DisplayMode", "PreferredDisplayMode"], Helpers: ["bootstrapper", "CODBrokerService"],
            InputLines: ["Resolution", "RefreshRate", "GPUName"], GameFacts: game => CallOfDutyFacts(game.Path),
            CloudChanged: CallOfDutyCloudChanged, Advice: file => CallOfDutyAdvice(pc, file));
    }

    /// <summary>
    /// Call of Duty's running marker: __cod in the install folder, holding the process id of the cod.exe that made it.
    /// The game deletes it as it quits; one left behind (a crash, a forced close) makes the next start ask to run in safe
    /// mode, which resets its settings. Before Exo starts the game, a marker whose process is gone is deleted, so it
    /// starts as it was set. Returns whether one was.
    /// <paramref name="read"/> and <paramref name="delay"/> are the seams a test fakes.
    /// </summary>
    internal static bool ClearLeftoverMarker(Models.GameEntry game, Func<string, (bool Read, int? Pid)>? read = null,
        Func<TimeSpan, Task>? delay = null)
    {
        if (HostMode.IsDisposable) return false;
        if (string.IsNullOrEmpty(game.Path)) return false;
        var marker = Path.Combine(game.Path, "__cod");
        if (!File.Exists(marker)) return false;
        // The game's own file at its known path: the own-interface rule, and never while the game or its anti-cheat runs.
        if (AntiCheatCheck.DeletionRefusal(game, AntiCheatCheck.Cached(game)) is { } refused)
        {
            AppLog.Info($"Safe mode: {game.Title}'s marker stays: {refused}");
            return false;
        }
        // A marker the game or an antivirus holds cannot say whether its process is alive. This is the one-shot delete before a
        // launch and nothing tries it again, and a marker left there makes the game ask for safe mode, which resets its
        // settings: so it is tried three times, 100 ms apart, and a marker still unreadable then is deleted anyway.
        read ??= MarkerPid;
        var held = read(marker);
        for (var attempt = 1; !held.Read && attempt < 3; attempt++)
        {
            AppLog.Info($"Safe mode: {game.Title}'s marker couldn't be read, so it is tried again before the launch.");
            try { (delay ?? (span => Task.Delay(span)))(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* the wait was cut short: the next try happens at once */ }
            held = read(marker);
        }
        if (held.Read && held.Pid is { } pid && CodIsRunning(pid)) return false;
        if (!held.Read)
            AppLog.Info($"Safe mode: {game.Title}'s marker still couldn't be read after three tries, so it is deleted before the launch.");
        DeletionGate.DeleteFile(marker);
        return true;
    }

    /// <summary>
    /// The process id the marker holds, and whether it could be read at all. A marker the game or its anti-cheat has open
    /// cannot be read (an IOException), which says nothing about its process: it is not known gone. A file that was read and
    /// holds no usable id (shorter than four bytes, or zero) is one left behind.
    /// </summary>
    private static (bool Read, int? Pid) MarkerPid(string marker)
    {
        try
        {
            var bytes = File.ReadAllBytes(marker);
            return (true, bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) is var pid and > 0 ? pid : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (false, null); }
    }

    /// <summary>
    /// The process ids of the processes with this name, disposed as they are read. The one place Exo asks Windows which
    /// processes carry a name for the marker: a test swaps it for its own in the flow of one test.
    /// </summary>
    internal static Func<string, IReadOnlyList<int>> ProcessIdsNamed { get; set; } = RealProcessIdsNamed;

    private static IReadOnlyList<int> RealProcessIdsNamed(string name)
    {
        var found = System.Diagnostics.Process.GetProcessesByName(name);
        try { return [.. found.Select(process => process.Id)]; }
        finally { foreach (var process in found) process.Dispose(); }
    }

    /// <summary>
    /// Whether the game itself has this process id: a process <em>named</em> cod does, and nothing else does. A protected
    /// process (an anti-cheat service, Defender) that Windows gave the marker's reused id is not the game, and a process whose
    /// name cannot be read is not taken for it either: only the id of a running cod.exe is.
    /// </summary>
    internal static bool CodIsRunning(int pid) => ProcessIdsNamed("cod").Contains(pid);

    /// <summary>How long the marker's process must have been gone before the marker is cleared, and how long Exo keeps looking.</summary>
    internal static readonly TimeSpan MarkerGone = TimeSpan.FromSeconds(5), MarkerCap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The safe-mode guard: clears a leftover marker once its process has been gone for 5 seconds, looking every second for up to
    /// 2 minutes (a game that exits slowly is still cleared; one that never exits is left alone, and a game that is running is
    /// never touched). A marker that cannot be read at all says nothing about its process, so it is not known gone and the
    /// clock starts over; one that was read and holds no id is one left behind. A cleared marker is noted for Keep as evidence
    /// for the next look (<see cref="NoteMarker"/>): the session that followed it may have been a safe-mode one, which resets
    /// the settings. True when it cleared one.
    /// <paramref name="delay"/>, <paramref name="alive"/>, <paramref name="profileOf"/>, <paramref name="clear"/> and
    /// <paramref name="read"/> are the seams a test fakes.
    /// </summary>
    internal static async Task<bool> ClearMarkerWhenGoneAsync(Models.GameEntry game, Func<TimeSpan, Task>? delay = null, Func<int, bool>? alive = null,
        Func<Models.GameEntry, string?>? profileOf = null, Func<Models.GameEntry, bool>? clear = null, Func<string, (bool Read, int? Pid)>? read = null)
    {
        delay ??= span => Task.Delay(span);
        alive ??= CodIsRunning;
        clear ??= game => ClearLeftoverMarker(game);
        read ??= MarkerPid;
        if (string.IsNullOrEmpty(game.Path)) return false;
        var marker = Path.Combine(game.Path, "__cod");
        var elapsed = TimeSpan.Zero;
        TimeSpan? goneSince = null;
        while (elapsed <= MarkerCap)
        {
            if (!File.Exists(marker)) return false;
            // Not known gone: unreadable (the game or its anti-cheat has it open), or its process still runs.
            var held = read(marker);
            if (!held.Read || (held.Pid is { } pid && alive(pid))) goneSince = null;
            else
            {
                goneSince ??= elapsed;
                var gone = elapsed - goneSince.Value;
                // Tried again every 5 seconds: the game's anti-cheat can still be closing, which refuses a deletion.
                if (gone >= MarkerGone && (gone - MarkerGone).TotalSeconds % 5 == 0 && clear(game))
                {
                    NoteMarker(profileOf?.Invoke(game) ?? ProfileIdOf(game), game.Id);
                    return true;
                }
            }
            await delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            elapsed += TimeSpan.FromSeconds(1);
        }
        AppLog.Info($"Safe mode: {game.Title}'s marker is still there after {MarkerCap.TotalMinutes:0} minutes; Exo leaves it.");
        return false;
    }

    /// <summary>The marker was cleared for a game that has an Optimize: the next look treats a moved line as a reset's (the session may have run in safe mode).</summary>
    internal static void NoteMarker(string? profileId, string? gameId = null)
    {
        if (profileId is null) return;
        Keep.Update(doc =>
        {
            if (doc.Games.TryGetValue(profileId, out var game) && game.Keeping is not null && !game.Signals.Contains("marker")) game.Signals.Add("marker");
        });
        if (gameId is not null) NoteGame(profileId, gameId);
    }

    private static string? ProfileIdOf(Models.GameEntry game)
    {
        try { return For(game, Unknown)?.Id; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Clears the marker now when its process is gone (the look before a launch from Exo) and notes it for Keep.</summary>
    internal static bool ClearLeftoverMarkerNoted(Models.GameEntry game)
    {
        if (!ClearLeftoverMarker(game)) return false;
        NoteMarker(ProfileIdOf(game), game.Id);
        return true;
    }

    [GeneratedRegex(@"^(cod|sp|mp)\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex CallOfDutyTitle();

    /// <summary>
    /// Call of Duty's own shader caches: shadercache in each title's folder of the install (Steam's Call of Duty HQ\cod26,
    /// Battle.net's Call of Duty\_retail_\cod26). Deleting them is the fix for colors or textures gone wrong after a game
    /// or driver update; the game builds them again as it starts. Null for a game that isn't Call of Duty; a cache that is
    /// a link to elsewhere is never listed.
    /// </summary>
    internal static IReadOnlyList<string>? ShaderCaches(Models.GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path) || !game.Title.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase)) return null;
        var titles = new[] { game.Path, Path.Combine(game.Path, "_retail_") }.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories).Where(folder => CallOfDutyTitle().IsMatch(Path.GetFileName(folder))).ToList();
        if (titles.Count == 0) return null;
        return [.. titles.Select(folder => Path.Combine(folder, "shadercache"))
            .Where(cache => new DirectoryInfo(cache) is { Exists: true } info && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))];
    }

    internal static long ShaderCacheBytes(IEnumerable<string> caches) => caches.Sum(cache => new DirectoryInfo(cache)
        .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
        .Sum(file => file.Length));

    internal static string DefaultShaderCacheBackupRoot => Path.Combine(PathHelper.AppDataDir, "backups", "shader-caches");

    internal static bool IsCacheProvablyInvalid(string? recordedDriver, string? currentDriver, string? recordedBuild, string? currentBuild)
    {
        if (!string.IsNullOrWhiteSpace(recordedDriver)
            && !string.IsNullOrWhiteSpace(currentDriver)
            && !string.Equals(recordedDriver, currentDriver, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(recordedBuild)
            && !string.IsNullOrWhiteSpace(currentBuild)
            && !string.Equals(recordedBuild, currentBuild, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static IReadOnlyList<string> ClearShaderCaches(IEnumerable<string> caches, string? backupRoot = null)
    {
        if (HostMode.IsDisposable) return [];
        var root = backupRoot ?? DefaultShaderCacheBackupRoot;
        var backups = new List<string>();
        foreach (var cache in caches)
        {
            if (!Directory.Exists(cache)) continue;
            var info = new DirectoryInfo(cache);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var parentName = info.Parent?.Name ?? "game";
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var backupDir = Path.Combine(root, $"{stamp}_{parentName}_{info.Name}");
            CopyDirectoryVerified(cache, backupDir);
            backups.Add(backupDir);
            DeletionGate.DeleteDirectory(cache, recursive: true);
        }
        return backups;
    }

    internal static IReadOnlyList<string> ClearStaleShaderCaches(
        IEnumerable<string> caches,
        string? recordedDriver,
        string? currentDriver,
        string? recordedBuild,
        string? currentBuild,
        string? backupRoot = null)
    {
        if (!IsCacheProvablyInvalid(recordedDriver, currentDriver, recordedBuild, currentBuild))
            return [];
        return ClearShaderCaches(caches, backupRoot);
    }

    private static void CopyDirectoryVerified(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var srcInfo = new FileInfo(file);
            var target = Path.Combine(destDir, srcInfo.Name);
            File.Copy(file, target, overwrite: true);
            if (new FileInfo(target).Length != srcInfo.Length)
                throw new IOException($"Shader cache backup size mismatch for {srcInfo.Name}.");
        }
        foreach (var sub in Directory.EnumerateDirectories(sourceDir, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var name = Path.GetFileName(sub);
            CopyDirectoryVerified(sub, Path.Combine(destDir, name));
        }
    }

    /// <summary>
    /// MW4 has no version mark in its files (the "1" heading them is the format's), so what says it was rebuilt is its
    /// fingerprint (<see cref="CallOfDutyFacts"/>: Steam's build number, the time of ccs_history.json, which Call of Duty HQ
    /// changes without touching cod.exe, and cod.exe's own time), the mark built from what the game detects of this PC
    /// (<see cref="CallOfDutyMark"/>: a driver or a hardware re-detect is a rebuild), and the marker of a session that did not
    /// quit cleanly (<see cref="ClearLeftoverMarker"/>). The install's main folder is no fact: Windows changes a folder's time
    /// whenever a file is added or removed in it, which ordinary play does. Helpers: the launcher and broker that can run
    /// while cod.exe does not (nothing is written while any runs).
    /// </summary>
    internal static Profile ModernWarfare4(Pc pc) =>
        new("mw4", "Modern Warfare 4", Path.Combine(CallOfDutyPlayers, "s.1.0.cod26.txt"), file => CallOfDutyGroups(pc, file), ["cod"],
            () => PlayerFiles("cod26"), ModernWarfare4Nvidia(), Program: CallOfDutyProgram, Mark: CallOfDutyMark, WindowMode: pc.Fullscreen ? Fullscreen : Borderless,
            WindowLines: ["DisplayMode", "PreferredDisplayMode"], Helpers: ["bootstrapper", "CODBrokerService"],
            InputLines: ["Resolution", "RefreshRate", "GPUName"], GameFacts: game => CallOfDutyFacts(game.Path), CloudChanged: CallOfDutyCloudChanged,
            Advice: file => CallOfDutyAdvice(pc, file));

    internal static IReadOnlyList<string> CallOfDutyAdvice(Pc pc, SettingsFile? file)
    {
        List<string> notes = [];
        var hasStreamingLine = file?.Settings.Any(line => line.Name is "TextureDownload") == true;
        if ((pc.LowBandwidth || pc.VideoMemoryGb is <= 8.5) && !hasStreamingLine)
            notes.Add("Set On-Demand Texture Streaming to Minimal in the game's graphics menu.");
        notes.Add("Deferred Physics Quality stops at Low in the config file; set it to Off in-game if needed.");
        return notes;
    }

    /// <summary>What the game detected of this PC and what it recommended: GPUDriverVersion|LastUsedGPU|RecommendedSet; null when the file holds none of them.</summary>
    internal static string? CallOfDutyMark(SettingsFile file)
    {
        string?[] parts = [ValueOf(file, "GPUDriverVersion"), ValueOf(file, "LastUsedGPU"), ValueOf(file, "RecommendedSet")];
        return parts.All(part => part is null) ? null : string.Join('|', parts.Select(part => part ?? ""));
    }

    /// <summary>
    /// Call of Duty's content facts: the time of ccs_history.json (its content updates change it without touching cod.exe;
    /// Battle.net keeps it under _retail_). Never the install's main folder: Windows changes a folder's time whenever a file
    /// is added or removed in it, which ordinary play does, so it would read as an update when there was none. Read only.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> CallOfDutyFacts(string? install)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(install)) return facts;
        try
        {
            foreach (var root in new[] { install, Path.Combine(install, "_retail_") })
            {
                if (File.Exists(Path.Combine(root, "ccs_history.json"))) facts["ccs"] = File.GetLastWriteTimeUtc(Path.Combine(root, "ccs_history.json")).ToString("O", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no content facts */ }
        return facts;
    }

    /// <summary>
    /// Whether Activision's own cloud, and not the player or Exo, wrote the settings: its flag (ConfigCloudStorageEnabled) is on and
    /// a .b file (the SteamCloud ones and the .l.b twins) changed since Exo last looked with no session here to explain it (so never
    /// right after one). Only the times are read; the .b files are never opened or touched.
    /// </summary>
    internal static bool CallOfDutyCloudChanged(IReadOnlyList<OpenFile> files, DateTime? since, bool afterSession)
    {
        if (afterSession || since is not { } last || files.Count == 0) return false;
        if (!string.Equals(ValueOf(files[0].File, "ConfigCloudStorageEnabled"), "true", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return Directory.EnumerateFiles(Path.GetDirectoryName(files[0].Path)!, "*.b", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Any(file => File.GetLastWriteTimeUtc(file) > last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>cod.exe: Steam's Call of Duty HQ holds it in the install, Battle.net's in _retail_.</summary>
    internal static string? CallOfDutyProgram(Models.GameEntry game) => string.IsNullOrEmpty(game.Path) ? null
        : new[] { Path.Combine(game.Path, "cod.exe"), Path.Combine(game.Path, "_retail_", "cod.exe") }.FirstOrDefault(File.Exists);

    /// <summary>
    /// Each account's own settings beside the PC's: players\{account}\g.{game}.1.0.l.txt and g.{game}.pm.1.0.l.txt, where
    /// the field of view and the audio live. The game reads these text files (tried on MW4: values written there came
    /// back from the game as it loaded and saved), though it keeps binary .b files beside them.
    /// </summary>
    internal static IReadOnlyList<string> PlayerFiles(string game)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(CallOfDutyPlayers)
                .SelectMany(account => new[] { Path.Combine(account, $"g.{game}.1.0.l.txt"), Path.Combine(account, $"g.{game}.pm.1.0.l.txt") })
                .Where(File.Exists)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The first line named <paramref name="name"/>'s value, if the file has one.</summary>
    internal static string? ValueOf(SettingsFile file, string name) => file.Settings.FirstOrDefault(line => line.Name == name)?.Value;

    [GeneratedRegex(@"RTX\s*(?<series>[2-9])0[5-9]0", RegexOptions.IgnoreCase)]
    private static partial Regex GeForceRtx();

    [GeneratedRegex(@"Radeon\s+RX\s+(?<series>[5-9])\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRx();

    [GeneratedRegex(@"(?<width>\d{3,5})\s*x\s*(?<height>\d{3,5})")]
    private static partial Regex Size();

    internal static IReadOnlyList<Group> CallOfDutyGroups(Pc pc, SettingsFile file)
    {
        Change[] noWaiting = [new("VSync", "disabled"), new("DLSSFrameGeneration", "false"), new("FSRFrameInterpolation", "false"), new("IntelXeFG", "false")];
        var latency = pc.Vendor switch
        {
            "nvidia" => new Group("latency", "Reflex on, V-Sync and frame generation off", [new("NvidiaReflex", "Enabled"), .. noWaiting]),
            "amd" => new Group("latency", "Anti-Lag 2 on, V-Sync and frame generation off", [new("AmdAntilag2", "true"), .. noWaiting]),
            _ => new Group("latency", "V-Sync and frame generation off", noWaiting),
        };
        List<Group> groups = [latency, Display(pc, ValueOf(file, "RefreshRate"))];
        if (pc.RenderThreads is { } threads)
            groups.Add(new("threads", $"{threads} render threads", [new("RendererWorkerCount", threads.ToString(CultureInfo.InvariantCulture))]));
        var gpu = ValueOf(file, "GPUName") ?? pc.Gpu;
        groups.Add(new("picture", "No blur, steady resolution",
        [
            new("DynamicSceneResolution", "false"), new("NVIDIAImageScalingMP", "false"),
            new("MotionBlur", "Off"), new("WeaponMotionBlur", "false"), new("EnableVelocityBasedBlur", "false"),
            new("DofEnable", "Off"), new("DofWeaponDisable", "true"), new("DofWorldDisable", "true"), new("DepthOfFieldQuality", "Low"),
        ]));
        if (Upscaler(gpu, ValueOf(file, "Resolution")) is { } upscaler) groups.Add(upscaler);
        if (pc.VideoMemoryGb is { } memory) groups.Add(Memory(memory));
        if (pc.LowBandwidth || pc.VideoMemoryGb is <= 8.5)
            groups.Add(new("streaming", "On-demand texture streaming off",
            [
                new("TextureDownload", "false", Spare: true),
                new("TextureDownload", "Minimal", Spare: true),
                new("TextureDownloadLimitEnabled", "true", Spare: true),
                new("TextureDownloadCacheSize", "16", Spare: true),
            ]));
        if (VrrFrameCap(pc) is { } cap)
        {
            var capText = cap.ToString(CultureInfo.InvariantCulture);
            groups.Add(new("framecap", $"Frame cap at {capText} FPS for VRR",
            [
                new("CapFps", "true", Spare: true),
                new("MaxFpsInGame", capText, Spare: true),
                new("CustomFramerateLimit", "true", Spare: true),
                new("GameplayCustomFramerateLimit", capText, Spare: true),
            ]));
        }
        groups.Add(new("clutter", "Blood and shell casings off",
            [new("ShowBlood", "false"), new("BloodLimit", "true"), new("ShowBrass", "false")]));
        // BenchmarKing's Competitive list, by the line that holds each: Shader Quality is by far the costliest (32% from Low
        // to Medium in his hard scene, 50% to High), Shadow 5-6%, Volumetric 8-15% from High up; Screen Space Reflections he
        // keeps at Low (grain from Normal up). Occlusion, Screen Space Shadows, Surface, Weather Grid, Water, Bullet Impacts and
        // Persistent Effects he measured as free. Surface/Shading is the ShadingQuality line, Static Reflection High is full
        // resolution probes, Local Texture Streaming's Default is WorldStreamingQuality's High, In-Game Video Standard is the
        // fallback movies, Water All is caustics and wetness. Variable Rate Shading he keeps on where it runs (FSR 1, Filmic).
        groups.Add(new("effects", "Costly effects low, cheap ones kept",
        [
            new("DxrMode", "Off"), new("SSRQuality", "Low"), new("ScreenSpaceShadowQuality", "High"), new("AmbientLightingQuality", "Ultra"),
            new("ShadowQuality", "Low"), new("ShadowFilteringQuality", "Medium"), new("ShadingQuality", "High"), new("ShaderQuality", "Low"),
            new("ModelQuality", "Medium Quality"), new("TerrainQuality", "Medium"),
            new("ParticleQuality", "very low"), new("VRS", "true"),
            new("VolumetricQuality", "QUALITY_LOW"), new("WeatherGridVolumesQuality", "Ultra"), new("WaterCausticsMode", "High Quality"),
            new("WaterWaveWetness", "true"), new("DeferredPhysics", "Low Quality"),
            new("ReflectionProbeHalfResolution", "false"), new("WorldStreamingQuality", "High"), new("CinematicQuality", "true"),
            new("BulletImpacts", "true"), new("PersistentDamageLayer", "true"),
            // Shaders keep preloading in the background: off (as two configs set it) means stutter while they compile.
            new("EnableOfflineGameUpdater", "true"),
            new("ShaderPreload", "true", Spare: true),
        ]));
        // In the PC's file and each player's: the game takes the player's and copies it over the PC's.
        groups.Add(new("audio", "Footsteps over music", [
            new("MusicVolume", "0"), new("CinematicVolume", "0"), new("EffectsVolume", "1"), new("HitMarkersVolume", "1"),
            new("AltShellShock", "true"), new("MonoSound", "false")]));
        groups.Add(View(ValueOf(file, "Resolution")));
        // The mouse as it moves, raw: no acceleration, filtering or smoothing; the ADS multipliers eased in, not snapped,
        // so hip-fire comes straight back after aiming; and no delay between scroll-wheel weapon switches.
        groups.Add(new("mouse", "Raw mouse input", [
            new("MouseAcceleration", "0"), new("MouseFilter", "0"), new("MouseSmoothing", "false"),
            new("ADSTimingSensitivity", "interpolated"), new("WeaponCycleDelay", "0")]));
        if (pc.Headphones)
            // MW4's headphones mix (its own spelling), stereo, and no Windows Sonic layered on it: the mix places sounds itself.
            groups.Add(new("headphones", "Mixed for headphones", [
                new("AudioMixPreset", "headphones_rex", Free: true), new("AudioWantedChannelsNumber", "2"), new("WindowsSonicEnable", "false")]));
        groups.Add(new("idle", "No quality drop when idle, no intros", [
            new("SustainabilityReduceQualityIdle", "off"), new("SustainabilityPauseRendering", "false"), new("SustainabilityPauseRendering", "0"),
            new("SkipIntro", "true"), new("ViewedSplashScreen", "true"), new("SkipSeasonVideo", "true"), new("SkipSeasonIntroVideo", "true")]));
        return groups;
    }

    /// <summary>
    /// The widest view, set for the screen: 120 on a 16:9 or 16:10 screen (what most competitive players and guides run),
    /// 110 on an ultrawide, which the game already widens; aiming scales with it ("Affected"), and the third-person view
    /// at its widest.
    /// </summary>
    internal static Group View(string? resolution)
    {
        var wide = resolution is not null && Size().Match(resolution) is { Success: true } size &&
            double.Parse(size.Groups["width"].Value, CultureInfo.InvariantCulture) / double.Parse(size.Groups["height"].Value, CultureInfo.InvariantCulture) > 2;
        return new("view", "Widest field of view for your screen",
            [new("Fov", wide ? "110" : "120"), new("ADSFovScaling", "true"), new("ThirdPersonFov", "90")]);
    }

    /// <summary>
    /// The window mode chosen, in the menu's own words for both lines, on the screen's highest refresh: a refresh rate the
    /// player fixed lower is set back to the game's Auto (its own spelling, "Auto:" and the rate it wrote, which it then
    /// reads as the highest). Borderless with a second screen keeps the mouse in the game, so a click near the edge isn't
    /// lost to it; true full screen owns the screen, so that line is left to the player.
    /// </summary>
    internal static Group Display(Pc pc, string? refresh)
    {
        var mode = pc.Fullscreen ? "Fullscreen" : "Fullscreen borderless window";
        List<Change> changes = [new("DisplayMode", mode), new("PreferredDisplayMode", mode), new("FocusedMode", "false")];
        if (refresh is { Length: > 0 } && !refresh.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
            changes.Add(new("RefreshRate", "Auto:" + refresh, Free: true));
        if (pc.Screens > 1 && !pc.Fullscreen) changes.Add(new("ConstrainMouse", "true"));
        return new("display", pc.Fullscreen ? "Fullscreen at the highest refresh" : "Borderless at the highest refresh", changes);
    }

    /// <summary>NVIDIA's Super Resolution preset override (0: none, the game's own choice runs).</summary>
    private const uint DlssPreset = 0x10E41DF3;

    /// <summary>
    /// MW4's own NVIDIA profile, on top of Exo's global one: Exo's global profile forces the transformer's presets in
    /// every game (0x10E41DF3), which replaces the menu's CNN (Exo's log showed Preset K in every MW4 session), so where
    /// Optimize picks CNN the game's profile gets no override and the menu's model runs. Everything else carries over
    /// from the global profile. NVIDIA's own MW4 profile is cod.exe with cod26.txt beside it.
    /// </summary>
    internal static NvidiaGame ModernWarfare4Nvidia() => new(
        "cod26.txt",
        file => Upscaler(ValueOf(file, "GPUName"), ValueOf(file, "Resolution")) is { } upscaler
                && upscaler.Changes.Any(c => c.Key == "DLSSModelUI" && c.Value == "CNN")
            ? [new(DlssPreset, 0, "DLSS model", "the game's own, CNN")]
            : []);

    /// <summary>
    /// The card's own upscaler at the preset its output suits, by the card the game found: DLSS on a GeForce RTX, FSR on
    /// a Radeon RX 7000 or 9000, XeSS on Intel Arc. DLSS's model: CNN on an RTX 20 or 30 at 1440p and 4K, where it is
    /// 0.6-3 ms a frame cheaper (NVIDIA's own timing tables; PC Gamer measured 0.93 ms in MW4 on a 3060 Ti) and looks
    /// close; the transformer at 1080p, where CNN works from a 720p picture and smears in motion, and on an RTX 40 or 50,
    /// where FP8 leaves CNN a 0.2-0.6 ms gain that isn't worth its look (.claude/research/cod-dlss-model.md). Exo's
    /// driver profile forces the transformer's presets in every game, so a CNN choice also takes MW4's own NVIDIA profile
    /// off that override (<see cref="ModernWarfare4Nvidia"/>). Maximum Quality at 1080p,
    /// Balanced at 1440p, Maximum Performance at 4K, with the render resolution at 100% so the upscaler, not the
    /// multiplier, lowers it. Null for any other card: an older one keeps its own upscaler and render resolution.
    /// </summary>
    internal static Group? Upscaler(string? gpu, string? resolution)
    {
        if (string.IsNullOrEmpty(gpu)) return null;
        var height = resolution is not null && Size().Match(resolution) is { Success: true } size ? int.Parse(size.Groups["height"].Value, CultureInfo.InvariantCulture) : 1080;
        var preset = height >= 2000 ? "Maximum Performance" : height >= 1300 ? "Balanced" : "Maximum Quality";
        if (GeForceRtx().Match(gpu) is { Success: true } rtx)
            return new("upscaler", "DLSS for your card", [
                new("ResolutionMultiplier", "100"), new("DLSSSharpnessMP", "0.75"), new("AATechniquePreferredMP", "DLSS"), new("DLSSModeMP", "DLSS"), new("DLSSPerfModeMP", preset),
                new("DLSSModelUI", rtx.Groups["series"].Value is "2" or "3" && height >= 1300 ? "CNN" : "TRANSFORMER"),
                new("Upscaling", "NVIDIA DLSS", Spare: true),
            ]);
        if (RadeonRx().Match(gpu) is { Success: true } radeon && radeon.Groups["series"].Value is "7" or "9")
            return new("upscaler", "FSR for your card", [new("ResolutionMultiplier", "100"), new("AATechniquePreferredMP", "FSR AA"),
                new("AMDSuperResolution2Quality", preset), new("AMDContrastAdaptiveSharpeningStrength", "0.75"),
                new("Upscaling", "AMD FSR 3", Spare: true)]);
        if (gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return new("upscaler", "XeSS for your card", [new("ResolutionMultiplier", "100"), new("AATechniquePreferredMP", "XeSS"), new("XeSSQuality", preset),
                new("Upscaling", "Intel XeSS", Spare: true)]);
        return null;
    }

    /// <summary>
    /// Textures and the share of video memory the game may fill, by the card's memory: the game's own budget is set
    /// lower so it doesn't thrash (and crash on 8 GB), and textures go as high as that leaves room for (3 is the lowest,
    /// which flattens the map and hurts spotting; 0, the highest, drops the 1% lows even on big cards). BenchmarKing's 8 GB
    /// rule: Normal with Minimal on-demand streaming, Low with Optimized, which is what 8 GB gets and the line asks for.
    /// </summary>
    internal static Group Memory(double gb) => gb switch
    {
        <= 4.5 => Textures("3", "0.7"),
        <= 8.5 => Textures("2", "0.7"),
        < 10 => Textures("2", "0.75"),
        _ => Textures("1", "0.8"),
    };

    // 16x filtering keeps angled ground sharp and measured free (BenchmarKing).
    private static Group Textures(string quality, string memory) => new("memory", "Textures and video memory for your card",
        [new("TextureQuality", quality), new("VideoMemoryScaleMP", memory), new("VideoMemoryScale", memory, Spare: true), new("TextureFilter", "aniso 16x")]);

    [GeneratedRegex(@"RTX\s*(30[89]0|40[789]0|50[789]0)|RX\s*(79\d0|9070)", RegexOptions.IgnoreCase)]
    private static partial Regex StrongCard();

    /// <summary>
    /// A card with headroom: GeForce RTX 3080 and up, 4070 and up, 5070 and up; Radeon RX 7900 and 9070. It takes the
    /// upper of the two values BenchmarKing gives where he gives two (Hell is Us, Phantom Blade Zero).
    /// </summary>
    internal static bool Strong(string? gpu) => gpu is not null && StrongCard().IsMatch(gpu);
}
