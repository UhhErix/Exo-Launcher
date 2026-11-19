using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using Keys = ExoLauncher.Helpers.AmdAdlx.Keys;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile on a Radeon: Exo's profile (<see cref="GpuProfile.Radeon"/>)
/// written through ADLX's official Set calls, with the same backup and Undo as the NVIDIA profile.
/// Use saves every value it replaces to amd-profile.json first (a re-apply keeps the first backup),
/// then writes; ADLX applies each Set at once, so a write that fails part way keeps the backup and
/// Undo puts back what changed. Fluid Motion Frames follows the games Exo launches, as Exo decides for
/// each (<see cref="FluidMotionFrames"/>): Exo sets it on or off before the game starts and back to what
/// it was once no game runs, and the value to put back is saved (amd-games.json) before the switch, so a
/// crash puts it back at the next start. Anti-Lag is the profile's, on always (<see cref="GpuProfile.Radeon"/>).
/// </summary>
internal sealed class RadeonProfileService(IGpuDriver driver, string folder, Func<bool> isAmd, Func<bool> anyGameRunning, Func<bool> amdSoftwareRunning)
{
    /// <summary>
    /// The profile for this PC. Null from Read, Apply and Undo means ADLX did not answer, and the page
    /// falls back to what to set in AMD Software.
    /// </summary>
    internal sealed record State(bool Ok, string? Message, string? Gpu, string Status, bool CanUndo, bool DriverUpdated,
        bool FreeSync, string Summary);

    /// <param name="AntiLag">Only in a backup from before 3.17.2, when Anti-Lag was a choice ("games", "always" or "off").</param>
    private sealed record Backup(string? Software, Dictionary<string, int?> Before, string? AntiLag = null);

    private sealed class Games
    {
        /// <summary>Only in a file from before 3.17.2: the games the player switched Fluid Motion Frames on for, dropped at start now that Exo decides.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Afmf { get; set; }
        /// <summary>What a launch switched, and the value it had before.</summary>
        public Dictionary<string, int> Restore { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static RadeonProfileService Shared { get; } = new(AmdAdlx.Driver, PathHelper.AppDataDir,
        () => GraphicsService.Vendor() == "amd", () => App.Services.Launcher.AnyGameRunning, AmdSoftwareRunning);

    private readonly Lock _gate = new();
    private Timer? _restoreTick;
    private bool? _afmfSupported;
    private string? _gpu;

    private string BackupPath => Path.Combine(folder, "amd-profile.json");
    private string GamesPath => Path.Combine(folder, "amd-games.json");

    /// <summary>AMD Software (Adrenalin) runs: Fluid Motion Frames works only then, as Adrenalin signals the game.</summary>
    internal static bool AmdSoftwareRunning()
    {
        var found = Process.GetProcessesByName("RadeonSoftware");
        foreach (var process in found) process.Dispose();
        return found.Length > 0;
    }

    /// <summary>Follows game launches (<see cref="LaunchOrchestrator.GameLaunching"/>) and ends, and does <see cref="AtStart"/> off this thread.</summary>
    internal void Start(LaunchOrchestrator launcher, Func<GpuProfile.Screen> mainScreen)
    {
        launcher.GameLaunching += game => GameLaunching(game.Title, gpu => FluidMotionFrames.Decide(gpu, FrameGenFacts.ForLaunch(game)));
        launcher.GameSessionCompleted += _ => GameEnded();
        _ = Task.Run(() =>
        {
            try { AtStart(mainScreen); }
            catch (Exception ex) { AppLog.Warn("Radeon profile start failed: " + ex.Message); }
        });
    }

    /// <summary>
    /// Puts back what a launch before a crash switched, then writes the profile again where it was saved with Anti-Lag
    /// kept to games Exo launched or off (before 3.17.2 made it always on), or where AMD Software's driver update moved
    /// some of Exo's settings. The per-game Fluid Motion Frames list from before 3.17.2 goes: Exo decides each game now.
    /// </summary>
    internal void AtStart(Func<GpuProfile.Screen> mainScreen)
    {
        GameEnded();
        lock (_gate)
        {
            if (LoadGames() is { Afmf: { } chosen } games)
            {
                games.Afmf = null;
                if (SaveGames(games)) AppLog.Info($"Fluid Motion Frames: the {chosen.Count} game(s) switched on by hand now follow Exo's choice.");
            }
        }
        if (!isAmd()) return;
        lock (_gate)
            if (driver.Read() is { } held) RetireCap(held);
        if (LoadBackup() is { AntiLag: { } choice and not "always" })
            AppLog.Info($"Radeon profile: Anti-Lag {choice} becomes always on: {Apply(mainScreen())?.Status ?? "no answer"}.");
        else if (Read(mainScreen()) is { Status: "changed", DriverUpdated: true })
            AppLog.Info($"Radeon profile after a driver update: {Apply(mainScreen())?.Status ?? "no answer"}.");
    }

    internal State? Read(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            return Describe(state, screen, LoadBackup(), null);
        }
    }

    /// <summary>Applies the profile, backing up what it replaces first.</summary>
    internal State? Apply(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            RetireCap(state);
            var writes = GpuProfile.Radeon(state);
            if (writes.Count == 0) return Describe(state, screen, LoadBackup(), "AMD's driver offers none of the settings Exo sets.");
            // A launch's switch is not what Undo should put back: the value from before it is.
            var games = LoadGames();
            var now = state.Values.ToDictionary(item => item.Key, item => (int?)(games.Restore.TryGetValue(item.Key, out var before) ? before : item.Value));
            var earlier = LoadBackup();
            var backup = new Backup(state.Software, GpuProfile.MergeBackup(earlier?.Before, now, writes.Select(write => write.Key)));
            if (!SaveBackup(backup)) return Describe(state, screen, earlier, "Exo could not save a backup, so it changed nothing.");
            // The profile's value wins over a launch's pending put-back.
            if (writes.Any(write => games.Restore.Remove(write.Key))) SaveGames(games);
            var failed = driver.Write(writes);
            AppLog.Info($"Radeon profile applied: settings={writes.Count}; {failed ?? "all held"}.");
            return driver.Read() is { } after ? Describe(after, screen, backup, failed) : null;
        }
    }

    /// <summary>
    /// The frame rate target from when the profile capped frames: back to what it held before Exo, and out of the backup, so
    /// no target of Exo's stays behind. What the driver refuses stays for the next try.
    /// </summary>
    private void RetireCap(GpuDriverState state)
    {
        if (LoadBackup() is not { } backup) return;
        var keys = new[] { Keys.FrtcFps, Keys.Frtc }.Where(backup.Before.ContainsKey).ToList();
        if (keys.Count == 0) return;
        var writes = keys.Where(key => backup.Before[key] is not null && state.Values.ContainsKey(key))
            .Select(key => new KeyValuePair<string, int>(key, backup.Before[key]!.Value)).ToList();
        if (driver.Write(writes) is { } failed)
        {
            AppLog.Warn($"Radeon frame rate target from before: {failed}");
            return;
        }
        foreach (var key in keys) backup.Before.Remove(key);
        SaveBackup(backup);
        AppLog.Info($"Radeon frame rate target back to what it held: {string.Join(", ", writes.Select(write => $"{write.Key}={write.Value}"))}.");
    }

    /// <summary>Puts back every value the profile replaced and forgets the backup.</summary>
    internal State? Undo(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            if (LoadBackup() is not { } backup) return Describe(state, screen, null, "Nothing to undo.");
            var writes = backup.Before.Where(item => item.Value is not null && state.Values.ContainsKey(item.Key))
                .Select(item => new KeyValuePair<string, int>(item.Key, item.Value!.Value)).ToList();
            var games = LoadGames();
            if (writes.Any(write => games.Restore.Remove(write.Key))) SaveGames(games);
            if (driver.Write(writes) is { } failed) return Describe(state, screen, backup, failed);
            DeleteBackup();
            AppLog.Info($"Radeon profile undone: settings={writes.Count}.");
            return driver.Read() is { } after ? Describe(after, screen, null, null) : null;
        }
    }

    /// <summary>Whether this PC's driver has Fluid Motion Frames, the GPU's name (for <see cref="FluidMotionFrames.Decide"/>), and whether AMD Software runs.</summary>
    internal (bool Supported, string? Gpu, bool AmdSoftware) Afmf()
    {
        lock (_gate)
        {
            // Asked once per run: the GPU and its driver don't change under a running Exo (a failed read asks again).
            if (_afmfSupported is null)
            {
                if (!isAmd()) _afmfSupported = false;
                else if (driver.Read() is { } state) (_afmfSupported, _gpu) = (state.Values.ContainsKey(Keys.Afmf), state.Gpu);
            }
            return _afmfSupported == true ? (true, _gpu, amdSoftwareRunning()) : (false, null, false);
        }
    }

    /// <summary>
    /// Before a game Exo launches starts: Fluid Motion Frames as Exo decides for the game (<paramref name="decide"/>, given
    /// the GPU's name, asked outside the lock since a game not looked up yet may take two seconds), switched only when the
    /// driver holds the other value, which goes back once no game runs.
    /// </summary>
    internal void GameLaunching(string title, Func<string?, FluidMotionFrames.Decision> decide)
    {
        if (!isAmd()) return;
        var (supported, gpu, _) = Afmf();
        if (!supported)
        {
            AppLog.Info($"Radeon launch settings for {title}: this GPU or driver has no {AmdAdlx.Label(Keys.Afmf)}.");
            return;
        }
        var decision = decide(gpu);
        var wanted = decision.On ? 1 : 0;
        lock (_gate)
        {
            if (driver.Read() is not { } state || !state.Values.TryGetValue(Keys.Afmf, out var now))
            {
                AppLog.Warn($"Radeon launch settings for {title}: AMD's driver did not answer, so nothing was switched.");
                return;
            }
            var words = $"{AmdAdlx.Label(Keys.Afmf)} {(decision.On ? "on" : "off")} ({decision.Why})";
            if (now == wanted)
            {
                AppLog.Info($"Radeon launch settings for {title}: {words}, as the driver has it.");
                return;
            }
            var games = LoadGames();
            games.Restore.TryAdd(Keys.Afmf, now);
            // Saved before the switch: if Exo stops before the game ends, the next start puts it back.
            if (!SaveGames(games)) return;
            var failed = driver.Write([new(Keys.Afmf, wanted)]);
            AppLog.Info($"Radeon launch settings for {title}: {words}; {failed ?? "held"}.");
            if (decision.On && !amdSoftwareRunning())
                AppLog.Info($"Fluid Motion Frames for {title}: AMD Software isn't running, so the driver won't add frames until it is.");
            _restoreTick ??= new Timer(_ => GameEnded(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>Once no game runs: every setting a launch switched goes back to what it was.</summary>
    internal void GameEnded()
    {
        lock (_gate)
        {
            if (anyGameRunning()) return;
            var games = LoadGames();
            if (games.Restore.Count == 0)
            {
                StopTick();
                return;
            }
            if (!isAmd()) return;
            var writes = games.Restore.Select(item => new KeyValuePair<string, int>(item.Key, item.Value)).ToList();
            var failed = driver.Write(writes);
            AppLog.Info($"Radeon launch settings put back: {string.Join(", ", writes.Select(write => $"{AmdAdlx.Label(write.Key)} {(write.Value != 0 ? "on" : "off")}"))}; {failed ?? "held"}.");
            if (failed is not null) return;
            games.Restore.Clear();
            SaveGames(games);
            StopTick();
        }
    }

    private void StopTick()
    {
        _restoreTick?.Dispose();
        _restoreTick = null;
    }

    private State Describe(GpuDriverState state, GpuProfile.Screen screen, Backup? backup, string? message)
    {
        var writes = GpuProfile.Radeon(state);
        // A setting a running game switched compares as the value it goes back to.
        var restore = LoadGames().Restore;
        var held = state.Values.ToDictionary(item => item.Key, item => restore.TryGetValue(item.Key, out var before) ? before : item.Value);
        var status = writes.Count == 0 ? "none" : GpuProfile.Status(writes.ToDictionary(), held, backup is not null);
        var driverUpdated = status == "changed" && backup?.Software != state.Software;
        return new State(message is null, message, Short(state.Gpu), status, backup is not null, driverUpdated,
            GpuProfile.MainFreeSync(state.Values, screen.Name) is not null, GpuProfile.RadeonSummary(writes));
    }

    private static string? Short(string? gpu) =>
        gpu?.Replace("AMD ", "", StringComparison.OrdinalIgnoreCase).Replace("Radeon(TM) ", "Radeon ", StringComparison.OrdinalIgnoreCase).Trim();

    // amd-profile.json: {"software":"26.10.1","before":{"chill":1,"freeSync:LG ULTRAGEAR":0}}; one from before 3.17.2 also has "antiLag".
    private Backup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(BackupPath));
            var root = json.RootElement;
            var software = root.TryGetProperty("software", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            var antiLag = root.TryGetProperty("antiLag", out var choice) && choice.ValueKind == JsonValueKind.String ? choice.GetString() : null;
            var before = new Dictionary<string, int?>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("before").EnumerateObject())
                before[item.Name] = item.Value.ValueKind == JsonValueKind.Number ? item.Value.GetInt32() : null;
            return new Backup(software, before, antiLag);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("Radeon profile backup unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    private bool SaveBackup(Backup backup) => Save(BackupPath, new Dictionary<string, object?>
    {
        ["software"] = backup.Software,
        ["before"] = backup.Before.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(),
    });

    private void DeleteBackup()
    {
        try { DeletionGate.DeleteFile(BackupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Radeon profile backup not removed: " + ex.GetType().Name); }
    }

    // amd-games.json: {"restore":{"afmf":0}}; before 3.17.2 also {"afmf":["steam:1091500"]}, the games switched on by hand.
    private Games LoadGames()
    {
        try
        {
            return File.Exists(GamesPath) ? JsonSerializer.Deserialize<Games>(File.ReadAllText(GamesPath), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Radeon game settings unreadable: " + ex.GetType().Name);
            return new();
        }
    }

    private bool SaveGames(Games games) => Save(GamesPath, games);

    private bool Save(string path, object body)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(body, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Radeon settings not saved ({Path.GetFileName(path)}): {ex.GetType().Name}");
            return false;
        }
    }
}
