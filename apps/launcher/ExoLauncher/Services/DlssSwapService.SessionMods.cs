using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Exo's frame generation and the shader recorder in a Steam game set to "Launch without anti-cheat" (Properties ›
/// Anti-cheat, <see cref="AntiCheatCheck.WithoutAntiCheatLaunch"/>) are in its folder only for a session Exo starts. The
/// game's default Steam entry still runs its anti-cheat, so nothing of Exo's stays there between sessions. As Exo's Play
/// starts it (<see cref="ForSessionAtLaunch"/>, before <see cref="FrameGenAtLaunch"/>), what Exo held goes back in
/// (<see cref="PutInForSession"/>), or, the first time, Best settings' mod goes in by the usual install
/// (<see cref="FrameGenOnAsync"/>). Once no process of the game runs (its launcher and children run from its folder),
/// <see cref="TakeOutAfterSession"/> moves every file Exo's record lists (<see cref="FrameGenInstall.Files"/>) that is
/// still Exo's, the mod's own folder and the record itself into Exo's data folder (frame-gen\session\&lt;game&gt;), and
/// switches the recorder off in its engine, remembered to switch on for the next session. Background passes leave such a
/// game alone (<see cref="FrameGenEverywhereAsync"/>).
///
/// Ways it could fail, each handled here:
/// - Exo closed or crashed mid-session: at Exo's start, after a launch that failed, and whenever such a game stops
///   running outside an Exo session, what is in goes out (<see cref="TakeOutWhereIdle"/>).
/// - The game started outside Exo (Steam's own, protected, entry) while Exo's files are in: while only its anti-cheat's
///   bootstrapper runs they go out at once; once the game itself runs they are in use and stay, Exo warns once, and they
///   go as it exits.
/// - A Steam update or verify: one under way is never touched (<c>busy</c>); with the files out the folder is the game's
///   own; a patch that changed the program is caught after the put-in (FrameGenAtLaunch takes the mod out).
/// - A file under one of Exo's names that isn't Exo's (another mod's, or one the game ships since): never moved and
///   never written over; the put-in refuses and keeps what it holds.
/// - A move cut short: the record goes into the game first and comes out last, so either side finishes it next time.
/// Only Exo's own files move. No file the game ships is renamed, replaced or removed, so its folder is byte for byte as
/// it was before Exo's mod went in.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>What Exo holds for one game between sessions, in its held folder.</summary>
    /// <param name="Folder">The game's program folder the frame generation came out of; null when none is held.</param>
    /// <param name="Recorder">The shader recorder was on in the game: Exo switches it on again for the next session.</param>
    internal sealed record SessionHeld(string GameId, string? Folder, bool Recorder);

    internal const string SessionHeldName = "held.json";

    /// <summary>The shader recorder's settings beside a game's program: where it sits, the recorder is in.</summary>
    private const string RecorderSettingsName = "scskiller.ini";

    private string SessionRoot => Path.Combine(Path.GetDirectoryName(FrameGenIndexPath)!, "session");

    private string SessionFolder(string gameId) =>
        Path.Combine(SessionRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameId.ToUpperInvariant())))[..16].ToLowerInvariant());

    private string SessionFiles(string gameId) => Path.Combine(SessionFolder(gameId), "files");

    /// <summary>Swapped in tests: the names of the programs running from the game's folder now.</summary>
    internal Func<string, IReadOnlyList<string>> ProgramsRunningIn { get; init; } = ProcessesUnder;

    /// <summary>Swapped in tests: an anti-cheat's own process runs now (Vanguard's runs whatever the game, so it doesn't count).</summary>
    internal Func<bool> AntiCheatRunning { get; init; } = () => AntiCheatCheck.AntiCheatProcessRunning();

    /// <summary>The shader recorder on or off in a game through Exo Shaders' engine: null once done, else why not. Wired at start.</summary>
    internal static Func<string, bool, CancellationToken, Task<string?>>? RecorderSwitch { get; set; }

    /// <summary>Swapped in tests: <see cref="RecorderSwitch"/>.</summary>
    internal Func<string, bool, CancellationToken, Task<string?>> SessionRecorder { get; init; } =
        (id, on, ct) => RecorderSwitch?.Invoke(id, on, ct) ?? Task.FromResult<string?>("Exo Shaders isn't available.");

    /// <summary>Swapped in tests: Exo's files are in a game that runs with its anti-cheat (once per run of the game).</summary>
    internal Action<GameEntry> WarnFilesInUse { get; init; } = game => DiscordClient.DiscordNotifier.ShowApp(
        $"Exo's mods are in {game.Title}",
        $"{game.Title} started with its anti-cheat while Exo's frame generation was in its folder. Close it: Exo takes them out as it exits. Play it from Exo to start it without anti-cheat.");

    private static readonly ConcurrentDictionary<string, byte> WarnedInUse = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What Exo holds for the game between sessions, or null.</summary>
    internal SessionHeld? SessionHeldFor(string gameId)
    {
        var path = Path.Combine(SessionFolder(gameId), SessionHeldName);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            return JsonSerializer.Deserialize<SessionHeld>(File.ReadAllText(path)) is { } held && string.Equals(held.GameId, gameId, StringComparison.OrdinalIgnoreCase) ? held : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Exo's record of the frame generation it holds for the game, or null.</summary>
    internal FrameGenInstall? SessionHeldFrameGen(string gameId) =>
        SessionHeldFor(gameId) is { Folder: not null } && Directory.Exists(SessionFiles(gameId)) ? ReadFrameGenManifest(SessionFiles(gameId)) : null;

    private void SaveSessionHeld(SessionHeld held)
    {
        if (held is { Folder: null, Recorder: false })
        {
            DropSessionHeld(held.GameId);
            return;
        }
        var dir = SessionFolder(held.GameId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SessionHeldName);
        File.WriteAllText(path + NewSuffix, JsonSerializer.Serialize(held));
        File.Move(path + NewSuffix, path, overwrite: true);
    }

    /// <summary>
    /// What Exo holds for the game is let go (Best settings off, or the game back on "with anti-cheat"): nothing of it is
    /// in the game, so nothing goes back. True when something was held.
    /// </summary>
    internal bool DropSessionHeld(string gameId)
    {
        var dir = SessionFolder(gameId);
        if (!Directory.Exists(dir)) return false;
        Directory.CreateDirectory(SessionRoot);
        if (!RecursiveDeleteGuard.TryValidateManagedChild(SessionRoot, dir, out var safe, out var why))
        {
            AppLog.Warn($"Frame generation: what Exo held for {gameId} stays: {why}");
            return false;
        }
        try { DeletionGate.DeleteDirectory(safe, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DeletionRefusedException)
        {
            AppLog.Warn($"Frame generation: what Exo held for {gameId} stays: {ex.Message}");
            return false;
        }
        return true;
    }

    /// <summary>Best settings off: the frame generation Exo held for the game is let go; a held recorder stays held. True when one was held.</summary>
    internal bool DropSessionHeldFrameGen(string gameId)
    {
        if (SessionHeldFor(gameId) is not { Folder: not null } held) return false;
        DeleteHeldFiles(gameId);
        SaveSessionHeld(held with { Folder = null });
        return true;
    }

    /// <summary>The folders under the install where the shader recorder sits (its settings file beside the game's program).</summary>
    internal static IReadOnlyList<string> RecorderFolders(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, RecorderSettingsName, new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }).Select(path => Path.GetDirectoryName(path)!).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Exo's frame generation folder in the game (its record, not only another mod's file over its loader), or null.</summary>
    private string? FrameGenInGame(string gameId) =>
        FrameGenFolderFor(gameId) is { } folder && ReadFrameGenManifest(folder) is { Out: false } ? folder : null;

    /// <summary>Anything of Exo's for a session in the game's folder now.</summary>
    internal bool SessionFilesIn(GameEntry game, string root) => FrameGenInGame(game.Id) is not null || RecorderFolders(root).Count > 0;

    // ------------------------------------------------------------------ in for the session

    /// <summary>
    /// On the launch's own thread, before <see cref="FrameGenAtLaunch"/>, for a game starting from Steam's entry without its
    /// anti-cheat: what Exo held goes back in, and Best settings' mod goes in where the table says so (<see cref="FrameGenNext.PutIn"/>),
    /// at most a minute. The game starts whatever happens here.
    /// </summary>
    /// <param name="keepOff">Keeps a game off for good where Exo couldn't save what it learned there (<see cref="PutRemovedBack"/>).</param>
    internal void ForSessionAtLaunch(GameEntry game, string? gpu, bool bestSettings, Func<string, bool> keptOff, Action<string>? keepOff = null)
    {
        if (AntiCheatCheck.WithoutAntiCheatLaunch(game) is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            if (SessionHeldFor(game.Id) is not null && PutInForSession(game, timeout.Token).GetAwaiter().GetResult() is { } why)
                AppLog.Info($"Frame generation for {game.Title}'s session: what Exo held stays out: {why}");
            if (bestSettings && !keptOff(game.Id))
            {
                // Best settings' table, as its background pass reads it for any other game: files that went are put back
                // once, a game without the mod gets it, and one with it moves to the mod's newest release.
                var next = FrameGenRead(game, gpu, timeout.Token).Next;
                if (next == FrameGenNext.PutBack && PutRemovedBack(game, keepOff)) next = FrameGenRead(game, gpu, timeout.Token).Next;
                if (next == FrameGenNext.PutIn)
                {
                    var (ok, message) = FrameGenOnAsync(game, gpu, timeout.Token).GetAwaiter().GetResult();
                    AppLog.Info($"Frame generation in {game.Title} for its session without anti-cheat: {(ok ? "on" : message)}.");
                }
                else if (FrameGenInGame(game.Id) is not null)
                    FrameGenKeepCurrentAsync([game], gpu, _ => false, timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) { AppLog.Info($"Frame generation for {game.Title}'s session: it took too long; the game starts without it."); }
        // Last, after everything that went in: DLSSG SM86 and the recorder can hang the game before its window opens.
        if (RecorderClashes(game.Id))
        {
            using var recorderTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                if (SessionRecorder(game.Id, false, recorderTimeout.Token).GetAwaiter().GetResult() is { } off) AppLog.Warn($"Shaders: recording in {game.Title} stays on: {off}");
                else AppLog.Info($"Shaders: recording stops in {game.Title}: with its frame generation the game can hang as it starts.");
            }
            catch (OperationCanceledException) { AppLog.Warn($"Shaders: recording in {game.Title} stays on: the engine didn't answer."); }
        }
    }

    /// <summary>
    /// What Exo held for the game back in: its frame generation (the record first, then each file, none over a file that
    /// isn't byte for byte the one Exo took out) and the recorder. Null once all of it is back (or nothing was held); else
    /// why what stays out stays out, still held.
    /// </summary>
    internal async Task<string?> PutInForSession(GameEntry game, CancellationToken ct)
    {
        if (SessionHeldFor(game.Id) is not { } held) return null;
        if (ResolveInstallRoot(game.Path) is not { } root) return "The install folder is unavailable.";
        string? problem = null;
        if (held.Folder is { } folder)
        {
            using (var mutation = AcquireMutationGate(game))
                lock (mutation.Sync)
                    problem = ReturnFrameGen(game, root, folder);
            if (problem is null) held = held with { Folder = null };
        }
        if (held.Recorder && FrameGenInGame(game.Id) is { } inGame && ReadFrameGenManifest(inGame) is { } back && back.Mod == DlssgSm86.Name)
        {
            // DLSSG SM86 and the recorder can hang the game as it starts (RecorderClashes): frame generation wins, as at any launch.
            AppLog.Info($"Shaders: recording stays off in {game.Title}: with its frame generation the game can hang as it starts.");
            held = held with { Recorder = false };
        }
        if (held.Recorder)
        {
            if (await SessionRecorder(game.Id, true, ct).ConfigureAwait(false) is { } off) problem ??= off;
            else held = held with { Recorder = false };
        }
        SaveSessionHeld(held);
        return problem;
    }

    private string? ReturnFrameGen(GameEntry game, string root, string folder)
    {
        var from = SessionFiles(game.Id);
        if (!Directory.Exists(from)) return null;
        if (!Directory.Exists(folder) || !IsSafeGamePath(root, folder) && !Path.TrimEndingDirectorySeparator(folder).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            // The game moved or went: what Exo held belongs to a folder that isn't there.
            AppLog.Info($"Frame generation for {game.Title}: the folder Exo took it out of is gone; what it held is let go.");
            DeleteHeldFiles(game.Id);
            return null;
        }
        if (ReadFrameGenManifest(from) is not { } install || FrameGenModNamed(install.Mod) is not { } mod) return "Exo's record of what it held can't be read.";
        if (File.Exists(Path.Combine(folder, FrameGenManifestName))) return "Exo's frame generation is in the game already.";
        var files = HeldFiles(from).Where(rel => !rel.Equals(FrameGenManifestName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var rel in files)
        {
            if (!FrameGenPathAllowed(rel, install.Loader, mod)) return $"{rel} isn't a file Exo writes.";
            var target = Path.Combine(folder, rel);
            if (File.Exists(target) && !string.Equals(FileSha256(target), FileSha256(Path.Combine(from, rel)), StringComparison.OrdinalIgnoreCase))
                return $"The game has a file of its own named {rel} now, so Exo leaves it as it is.";
        }
        try
        {
            // The record first, as an install writes it: a put-in cut short is still Exo's to take out.
            File.Copy(Path.Combine(from, FrameGenManifestName), Path.Combine(folder, FrameGenManifestName), overwrite: false);
            foreach (var rel in files)
            {
                var source = Path.Combine(from, rel);
                var target = Path.Combine(folder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) DeletionGate.DeleteFile(source);   // the same bytes: a put-in cut short got this far
                else File.Move(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DeletionRefusedException)
        {
            AppLog.Warn($"Frame generation for {game.Title}: putting it back in stopped: {ex.Message}");
            return "Exo couldn't put its frame generation back: " + ex.Message;
        }
        RememberFrameGen(game.Id, folder);
        DeleteHeldFiles(game.Id);
        AppLog.Info($"Frame generation for {game.Title}'s session: {install.Mod} {install.Release} back in ({files.Count} files).");
        return null;
    }

    // ------------------------------------------------------------------ out after it

    /// <summary>
    /// After an Exo session of a game set to "without anti-cheat" (and at every look while no Exo session of it runs): Exo's
    /// files out of the game's folder while none of its programs runs (or only its anti-cheat's bootstrapper: the game's
    /// program hasn't loaded them yet). Null once nothing of Exo's is in it; else why something stays for now.
    /// </summary>
    internal async Task<string?> TakeOutAfterSession(GameEntry game, CancellationToken ct)
    {
        if (ResolveInstallRoot(game.Path) is not { } root) return null;
        if (GameItselfRunning(root)) return "running";
        var held = SessionHeldFor(game.Id) ?? new SessionHeld(game.Id, null, false);
        string? problem = null;
        if (RecorderFolders(root).Count > 0)
        {
            var off = await SessionRecorder(game.Id, false, ct).ConfigureAwait(false);
            if (off is null && RecorderFolders(root).Count == 0) SaveSessionHeld(held = held with { Recorder = true });
            else problem = off ?? "Exo Shaders left its recorder in the game.";
        }
        if (FrameGenInGame(game.Id) is { } folder)
        {
            using var mutation = AcquireMutationGate(game);
            lock (mutation.Sync)
            {
                if (GameItselfRunning(root)) return "running";
                problem = HoldFrameGen(game, folder, held) ?? problem;
            }
        }
        if (problem is not null) AppLog.Warn($"Frame generation in {game.Title}: after its session without anti-cheat, {problem}");
        return problem;
    }

    /// <summary>A program of the game runs from its folder, other than its anti-cheat's own bootstrapper (start_protected_game, *_EAC).</summary>
    private bool GameItselfRunning(string root) => ProgramsRunningIn(root).Any(name => AntiCheatCheck.ForFile(name) is null);

    /// <summary>
    /// Exo's frame generation out of <paramref name="folder"/> into its held folder: each file its record lists while it
    /// is still Exo's (as <see cref="RemoveFrameGen"/> judges it), every file in the mod's own folder (Exo made it; an
    /// install refuses a game that had one), then the record. A file under Exo's name that isn't Exo's stays.
    /// </summary>
    private string? HoldFrameGen(GameEntry game, string folder, SessionHeld held)
    {
        if (ReadFrameGenManifest(folder) is not { Out: false } install || FrameGenModNamed(install.Mod) is not { } mod) return null;
        var into = SessionFiles(game.Id);
        var left = new List<string>();
        var moved = 0;
        try
        {
            SaveSessionHeld(held with { Folder = folder });
            var over = false;
            foreach (var file in install.Files.OrderByDescending(file => file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase)))
            {
                var path = Path.Combine(folder, file.Path);
                if (!File.Exists(path)) continue;
                if (!StillExos(path, file, install, mod, over))
                {
                    left.Add(file.Path);
                    over |= file.Path.Equals(install.Loader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                Hold(path, Path.Combine(into, file.Path));
                moved++;
            }
            // The mod's folder as Off leaves it: its logs go, a file Exo didn't write (or one changed since) stays, empty folders go.
            if (mod.Folder is { } own && Path.Combine(folder, own) is var modFolder && Directory.Exists(modFolder) && (File.GetAttributes(modFolder) & FileAttributes.ReparsePoint) == 0)
                ClearModFolder(modFolder, mod, logs: !over, folder, left);
            if (!over) foreach (var leftover in FrameGenLeftovers(folder, mod).ToList()) TryDelete(leftover);
            // The driver's frame cap Exo moved into OptiScaler goes back for the game's other starts; the record keeps it,
            // so the next session moves it again (FollowDriverCap) and a let-go record has nothing left to undo.
            if (install.Cap is { } cap && FrameGenDriver is { } driver && !driver.SetGameCap(game, Path.Combine(folder, install.Exe), cap.Before, out _, out _))
                AppLog.Warn($"Frame generation in {game.Title}: the driver didn't take back its frame cap for the game.");
            Hold(Path.Combine(folder, FrameGenManifestName), Path.Combine(into, FrameGenManifestName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DeletionRefusedException)
        {
            return "Exo couldn't take all of its frame generation out: " + ex.Message + ". It tries again shortly.";
        }
        AppLog.Info($"Frame generation for {game.Title}: out of the game after its session without anti-cheat ({mod.Name} {install.Release}, {moved} files held by Exo).");
        return left.Count == 0 ? null : $"{string.Join(" and ", left)} isn't Exo's file as Exo wrote it, so it stays where it is.";
    }

    /// <summary>One file out of the game into Exo's held folder (another volume: copied, then gone from the game).</summary>
    private static void Hold(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        ClearReadOnly(from);
        File.Move(from, to, overwrite: true);
    }

    private static IEnumerable<string> HeldFiles(string from) =>
        Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(path => Path.GetRelativePath(from, path)).ToList();

    private void DeleteHeldFiles(string gameId)
    {
        var files = SessionFiles(gameId);
        if (!Directory.Exists(files) || !RecursiveDeleteGuard.TryValidateManagedChild(SessionFolder(gameId), files, out var safe, out _)) return;
        try { DeletionGate.DeleteDirectory(safe, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DeletionRefusedException) { AppLog.Debug("Frame generation held files: " + ex.Message); }
    }

    // ------------------------------------------------------------------ leftovers

    /// <summary>
    /// Every game set to "without anti-cheat" that no Exo session runs and no store writes: what is in its folder goes out
    /// (Exo closed or crashed mid-session, a launch that failed, a background change). While the game itself runs its
    /// files are in use and stay; with an anti-cheat running beside it, Exo warns once. A game no longer set to "without"
    /// has what Exo held let go. The games something came out of.
    /// </summary>
    internal async Task<int> TakeOutWhereIdle(IEnumerable<GameEntry> games, Func<GameEntry, bool> inExoSession, Func<GameEntry, bool> busy, CancellationToken ct)
    {
        var taken = 0;
        foreach (var game in games)
        {
            ct.ThrowIfCancellationRequested();
            if (!AntiCheatCheck.ChoseWithoutAntiCheat(game))
            {
                if (DropSessionHeld(game.Id)) AppLog.Info($"Frame generation for {game.Title}: it starts with anti-cheat now, so what Exo held is let go.");
                continue;
            }
            if (inExoSession(game) || busy(game) || ResolveInstallRoot(game.Path) is not { } root || !SessionFilesIn(game, root)) continue;
            if (GameItselfRunning(root))
            {
                if (AntiCheatRunning() && WarnedInUse.TryAdd(game.Id, 0))
                {
                    AppLog.Warn($"Frame generation in {game.Title}: the game runs with its anti-cheat while Exo's files are in its folder; they are in use, so Exo leaves them until it exits.");
                    WarnFilesInUse(game);
                }
                continue;
            }
            WarnedInUse.TryRemove(game.Id, out _);
            if (await TakeOutAfterSession(game, ct).ConfigureAwait(false) is null) taken++;
        }
        return taken;
    }

    /// <summary>The file names of the programs running from under <paramref name="root"/> (read as <see cref="IsGameRunning"/> reads them).</summary>
    internal static IReadOnlyList<string> ProcessesUnder(string root)
    {
        var names = new List<string>();
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var exe = ProcessHelper.QueryFullProcessImageName(proc.Id);
                    if (!string.IsNullOrWhiteSpace(exe) && Path.GetFullPath(exe).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileName(exe));
                }
                catch { /* access denied / exited */ }
                finally { proc.Dispose(); }
            }
        }
        catch { /* process list unavailable */ }
        return names;
    }
}
