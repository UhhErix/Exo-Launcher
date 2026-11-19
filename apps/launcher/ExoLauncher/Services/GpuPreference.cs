using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// A PC with two graphics chips (a laptop's built-in one and its GeForce or Radeon) leaves Windows to pick which runs each game,
/// and it sometimes picks the slow one. Windows keeps the choice per program in the user's own registry
/// (<c>Settings > System > Display > Graphics</c>): <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>, the program's full path
/// naming a string such as <c>GpuPreference=2;</c> (2 high performance). With the switch on, Exo writes that for the programs of a game
/// it starts, keeping what each value held before in <c>gpu-preference.json</c>, and Undo puts every one back. No administrator, no
/// change to a game's files, and never for a Riot game or an anti-cheat's own program.
/// </summary>
internal sealed class GpuPreference(GpuPreference.IStore store, string statePath, Func<Keep.KeepPc>? keepLoad = null, Func<Action<Keep.KeepPc>, bool>? keepSave = null)
{
    private readonly Func<Keep.KeepPc> loadKeep = keepLoad ?? (() => Keep.Load().Pc);
    private readonly Func<Action<Keep.KeepPc>, bool> saveKeep = keepSave ?? (change => Keep.Update(doc => change(doc.Pc)));

    internal const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal const int HighPerformance = 2;
    private const int MaxProgramsPerGame = 24;
    private static readonly object Gate = new();

    /// <summary>One program's value in the user's graphics preferences.</summary>
    internal interface IStore
    {
        string? Get(string program);
        void Set(string program, string data);
        void Delete(string program);
    }

    private sealed class RegistryStore : IStore
    {
        public string? Get(string program)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(program) as string;
        }

        public void Set(string program, string data)
        {
            if (HostMode.IsDisposable) return;
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(program, data, RegistryValueKind.String);
        }

        public void Delete(string program)
        {
            if (HostMode.IsDisposable) return;
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(program, throwOnMissingValue: false);
        }
    }

    internal static GpuPreference Shared { get; } = new(new RegistryStore(), Path.Combine(PathHelper.AppDataDir, "gpu-preference.json"));

    private const string GlobalValue = "DirectXUserGlobalSettings";

    /// <summary>
    /// Windows' "Optimizations for windowed games" (Settings › Display › Graphics), on for every PC by default: DirectX 10
    /// and 11 games in a window or borderless present the flip way, for lower latency and variable refresh as in full
    /// screen. One token in the user's own global value (SwapEffectUpgradeEnable=1), the others kept; no administrator.
    /// Written once, after recording what the value held (<see cref="UseWindowedGames"/>); a person who turned it off in
    /// Windows (=0) keeps that, and Undo puts it back exactly (<see cref="UndoWindowedGames"/>).
    /// </summary>
    internal static void WindowedGamesOptimized() => Shared.UseWindowedGames();

    /// <summary>The global value with SwapEffectUpgradeEnable=1 added; null when it already says 1 or a person set it to 0.</summary>
    internal static string? WithSwapUpgrade(string? data)
    {
        var parts = (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Any(part => part.StartsWith("SwapEffectUpgradeEnable=", StringComparison.OrdinalIgnoreCase))) return null;
        parts.Add("SwapEffectUpgradeEnable=1");
        return string.Join(';', parts) + ";";
    }

    /// <summary>The value's text with GpuPreference set to <paramref name="preference"/>, other entries (SwapEffectUpgradeEnable=1;) kept.</summary>
    internal static string With(string? data, int preference)
    {
        var parts = (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Insert(0, "GpuPreference=" + preference);
        return string.Join(';', parts) + ";";
    }

    /// <summary>The GpuPreference in a value's text (0 Windows decides, 1 power saving, 2 high performance); null when it has none.</summary>
    internal static int? Preference(string? data)
    {
        foreach (var part in (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase) && int.TryParse(part["GpuPreference=".Length..], out var value))
                return value;
        return null;
    }

    /// <summary>Programs that are not the game's (an uninstaller, a crash reporter, a redistributable) or are anti-cheat's own.</summary>
    internal static bool IsGameProgram(string fileName)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileNameWithoutExtension(fileName);
        string[] helpers = ["unins", "uninst", "crash", "report", "setup", "install", "redist", "vcredist", "dxsetup", "dotnet", "directx", "ndp", "oalinst", "updater", "diagnos", "bugsplat", "sentry"];
        if (helpers.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        return AntiCheatCheck.ForFile(fileName) is null;
    }

    /// <summary>
    /// The programs that can be a game: its own launch program, then the programs in its folder and in the usual places below it
    /// (bin, Binaries\Win64, x64, Win64), an Unreal Engine 4 or 5 project's {Project}\Binaries\Win64 (the program at the top
    /// only starts the one there, which draws the game; the engine's own Engine\Binaries are helpers), and platform\{store}
    /// (Phantom Blade Zero's game, which steam\PBZ.exe starts). Few and bounded: this is a preference per path, not a scan of
    /// the disk. None for a Riot game.
    /// </summary>
    internal static IReadOnlyList<string> ProgramsOf(GameEntry game)
    {
        if (game.Store == StoreKind.Riot || string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path)) return [];
        List<string> Subfolders(string parent)
        {
            try
            {
                var dir = Path.Combine(game.Path, parent);
                return Directory.Exists(dir) ? [.. Directory.EnumerateDirectories(dir).Take(16).Select(sub => Path.GetRelativePath(game.Path, sub))] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
        var found = new List<string>();
        void Add(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (found.Count < MaxProgramsPerGame && IsGameProgram(Path.GetFileName(full)) && File.Exists(full) &&
                    PathHelper.IsUnderRoot(full, game.Path) && !found.Contains(full, StringComparer.OrdinalIgnoreCase))
                    found.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { /* not a path */ }
        }
        if (game.LaunchTarget is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Add(target);
        string[] usual = ["", "bin", "Bin", "x64", "Win64", @"Binaries\Win64", @"Binaries\WinGDK", @"bin\x64"];
        var projects = Subfolders("").Where(top => !top.Equals("Engine", StringComparison.OrdinalIgnoreCase)).Select(top => Path.Combine(top, "Binaries", "Win64"));
        foreach (var folder in usual.Concat(projects).Concat(Subfolders("platform")))
        {
            try
            {
                var dir = Path.Combine(game.Path, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(40)) Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a folder that can't be read has no programs here */ }
        }
        return found;
    }

    private sealed record State(Dictionary<string, string?> Previous);

    private State Load()
    {
        try
        {
            if (File.Exists(statePath) && new FileInfo(statePath).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) is { Previous: { } previous })
                return new State(new Dictionary<string, string?>(previous, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Warn("GPU preference record unreadable: " + ex.GetType().Name); }
        return new State(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    }

    private void Save(State state)
    {
        var temp = statePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("GPU preference record not saved: " + ex.GetType().Name);
            try { DeletionGate.DeleteFile(temp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>What the page is told when Windows kept none of the choices Exo wrote or put back.</summary>
    internal const string NotTaken = "Windows didn't take the setting.";

    /// <summary>What one <see cref="UseReport"/> came to: programs now on the fast chip (read back), and programs Windows did not take.</summary>
    internal sealed record Used(int Changed, int Failed);

    /// <summary>What one <see cref="UndoReport"/> came to: programs put back (read back), and programs (or the windowed-games value) Windows did not take back.</summary>
    internal sealed record Undone(int Restored, int Failed);

    /// <summary>High performance for these programs; how many took. What each held is kept first, once.</summary>
    internal int Use(IEnumerable<string> programs) => UseReport(programs).Changed;

    /// <summary>
    /// High performance for these programs, each read back: a write Windows accepted and did not keep, or refused, is a failure
    /// and not counted. What each held is recorded first, once (a program with no record is never touched by Undo).
    /// </summary>
    internal Used UseReport(IEnumerable<string> programs)
    {
        if (HostMode.IsDisposable) return new(0, 0);
        lock (Gate)
        {
            var state = Load();
            int changed = 0, failed = 0;
            foreach (var program in programs)
            {
                try
                {
                    var now = store.Get(program);
                    if (Preference(now) == HighPerformance) continue;
                    // Written before the record changes hands: a program with no record is never touched by Undo.
                    state.Previous.TryAdd(program, now);
                    Save(state);
                    store.Set(program, With(now, HighPerformance));
                    if (Preference(store.Get(program)) == HighPerformance) changed++;
                    else
                    {
                        failed++;
                        AppLog.Warn("GPU preference: Windows didn't keep the choice for one program.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    failed++;
                    AppLog.Warn("GPU preference not set for one program: " + ex.GetType().Name);
                }
            }
            return new(changed, failed);
        }
    }

    /// <summary>
    /// <see cref="UseReport"/> as the page is told: Graphics for games turned on again starts the windowed-games value over too, and
    /// <see cref="NotTaken"/> comes back only when nothing that should have been written was.
    /// </summary>
    internal string? UseAndSay(IEnumerable<string> programs)
    {
        ForgetWindowedGamesChoice();
        UseWindowedGames();
        var used = UseReport(programs);
        AppLog.Info($"GPU preference: the fast graphics chip asked for {used.Changed} program(s){(used.Failed > 0 ? $", Windows didn't take {used.Failed}" : "")}.");
        return used.Changed == 0 && used.Failed > 0 ? NotTaken : null;
    }

    /// <summary>After a Windows update: High performance again for each program Exo set that exists and that Windows reset; how many were put back.</summary>
    internal int Recheck()
    {
        if (HostMode.IsDisposable) return 0;
        lock (Gate)
        {
            var programs = Load().Previous.Keys.Where(File.Exists).ToList();
            return UseReport(programs).Changed;
        }
    }

    /// <summary>"Optimizations for windowed games" on, once, recording what the global value held first. A player's Undo stands until Graphics for games is turned on again.</summary>
    internal void UseWindowedGames()
    {
        if (HostMode.IsDisposable) return;
        lock (Gate)
        {
            try
            {
                var kept = loadKeep().WindowedGames;
                if (kept is { Declined: true }) return;
                var now = store.Get(GlobalValue);
                // Already says 1, or a person set it to 0: nothing to write, so nothing to put back.
                if (WithSwapUpgrade(now) is not { } next) return;
                if (kept is null && !saveKeep(pc => pc.WindowedGames ??= new Keep.WindowedGamesKeep { Before = now is null ? null : RegValue.Str(now) }))
                {
                    AppLog.Warn("Graphics: windowed-game optimizations not set: the record couldn't be saved.");
                    return;
                }
                store.Set(GlobalValue, next);
                if (store.Get(GlobalValue) is { } read && WithSwapUpgrade(read) is null) AppLog.Info("Graphics: optimizations for windowed games on.");
                else AppLog.Warn("Graphics: Windows didn't keep the windowed-game optimizations.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                AppLog.Debug("Graphics: windowed-game optimizations not set: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>The player turned Graphics for games on: a standing Undo of the windowed-games value is forgotten, so it starts over.</summary>
    private void ForgetWindowedGamesChoice()
    {
        if (HostMode.IsDisposable) return;
        lock (Gate)
        {
            if (loadKeep().WindowedGames is { Declined: true }) saveKeep(pc => pc.WindowedGames = null);
        }
    }

    /// <summary>
    /// Puts the windowed-games value back exactly as it was (deleting it when it was not set), reads it back, and forgets the
    /// record; true when it is back, or Exo never wrote it. When a person has changed the value since, only Exo's token comes
    /// out and what they changed stays. <paramref name="remember"/>: the player's Undo, which Exo does not write over at the
    /// next start; the uninstaller's forgets it, so a reinstall starts over.
    /// </summary>
    internal bool UndoWindowedGames(bool remember = true)
    {
        if (HostMode.IsDisposable) return true;
        lock (Gate)
        {
            try
            {
                var kept = loadKeep().WindowedGames;
                if (kept is { Declined: false })
                {
                    var now = store.Get(GlobalValue);
                    var wrote = WithSwapUpgrade(kept.Before is { Kind: Microsoft.Win32.RegistryValueKind.String, Text: { } text } ? text : null);
                    string? wanted;
                    if (now == wrote) wanted = kept.Before?.Text;                     // as Exo wrote it: back to exactly what was there
                    else if (now is not null && WithoutSwapUpgrade(now) is { } stripped) wanted = stripped;  // changed since: only Exo's token comes out
                    else wanted = now;                                               // the token is not there any more: nothing of Exo's to remove
                    if (wanted != now)
                    {
                        if (wanted is null) store.Delete(GlobalValue); else store.Set(GlobalValue, wanted);
                        if (store.Get(GlobalValue) != wanted)
                        {
                            AppLog.Warn("Graphics: Windows didn't put the windowed-game optimizations back.");
                            return false;
                        }
                    }
                }
                saveKeep(pc => pc.WindowedGames = remember ? new Keep.WindowedGamesKeep { Declined = true } : null);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                AppLog.Warn("Graphics: windowed-game optimizations not put back: " + ex.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>The global value without its SwapEffectUpgradeEnable=1 token, the others kept; null when it has none, and null text (deleted) when nothing else is left.</summary>
    internal static string? WithoutSwapUpgrade(string data)
    {
        var parts = data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.RemoveAll(part => part.Equals("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase)) == 0) return null;
        return parts.Count == 0 ? "" : string.Join(';', parts) + ";";
    }

    /// <summary>Every program Exo set goes back to what it held; one the user changed since is left as they have it. How many went back.</summary>
    internal int Undo(bool remember = true) => UndoReport(remember).Restored;

    /// <summary>
    /// <see cref="Undo"/> with each put back read back (one that did not take keeps its record, so a later Undo tries again),
    /// and the windowed-games value put back too. <paramref name="remember"/>: the player's Undo (see <see cref="UndoWindowedGames"/>).
    /// </summary>
    internal Undone UndoReport(bool remember = true)
    {
        if (HostMode.IsDisposable) return new(0, 0);
        lock (Gate)
        {
            var state = Load();
            int restored = 0, failed = 0;
            foreach (var (program, previous) in state.Previous.ToList())
            {
                try
                {
                    if (Preference(store.Get(program)) == HighPerformance)
                    {
                        if (previous is null) store.Delete(program);
                        else store.Set(program, previous);
                        if (store.Get(program) != previous)
                        {
                            failed++;
                            AppLog.Warn("GPU preference: Windows didn't put one program back.");
                            continue;
                        }
                        restored++;
                    }
                    state.Previous.Remove(program);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    failed++;
                    AppLog.Warn("GPU preference not put back for one program: " + ex.GetType().Name);
                }
            }
            Save(state);
            if (!UndoWindowedGames(remember)) failed++;
            return new(restored, failed);
        }
    }

    /// <summary><see cref="UndoReport"/> as the page is told: <see cref="NotTaken"/> comes back only when nothing that should have been put back was.</summary>
    internal string? UndoAndSay()
    {
        var undone = UndoReport();
        AppLog.Info($"GPU preference: {undone.Restored} program(s) put back{(undone.Failed > 0 ? $", Windows didn't take {undone.Failed}" : "")}.");
        return undone.Restored == 0 && undone.Failed > 0 ? NotTaken : null;
    }
}
