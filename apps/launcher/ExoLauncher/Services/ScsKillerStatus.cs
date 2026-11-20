using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// The game sheet's Shaders line, read from the Exo Shaders engine's records
/// (its data folder keeps SCSKiller's layout): the game list (games.json,
/// written when the engine scans) and each game's record (games\&lt;id&gt;\state.json, written when a
/// compile ends), and says when either changed.
/// </summary>
internal sealed partial class ScsKillerStatus : IDisposable
{
    /// <param name="EngineId">The engine's own id for the game (it can differ from Exo's), for game.compile.</param>
    internal sealed record Status(string State, string? Reason, DateTimeOffset? WarmedAt, string? Driver, string? EngineId = null);

    internal sealed record Listed(string Id, string Name, string InstallDir, string Status, string Reason, DateTimeOffset? WarmedAt, string? Driver, string? EngineFamily = null, string? ExePath = null);

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private (DateTime Written, IReadOnlyList<Listed> Games)? _list;

    /// <summary>The app's list or a game's record changed (debounced): game sheets read their line again.</summary>
    internal event Action? Changed;

    /// <summary>The app's data folder, its AppStore.DefaultDir.</summary>
    internal static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller");

    internal void Start()
    {
        RemoveOldCopy();
        // Without the add-on every game reads "noApp": no folder of its own is made and nothing is watched.
        if (!ShadersAddon.Present) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            var watcher = new FileSystemWatcher(DataDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            watcher.Filters.Add("games.json");
            watcher.Filters.Add("state.json");
            watcher.Changed += (_, _) => Poke();
            watcher.Created += (_, _) => Poke();
            watcher.Renamed += (_, _) => Poke();
            watcher.EnableRaisingEvents = true;
            lock (_gate) _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Info("SCSKiller: cannot watch the app's data folder: " + ex.Message);
        }
    }

    private void Poke()
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => Changed?.Invoke());
            _debounce.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// "noApp" (Exo Shaders is not installed), "warmed", "stale", "ready", "record",
    /// or "unsupported" (also a game the engine does not list; its next scan, which the watcher sees, adds a new install).
    /// </summary>
    internal Status For(GameEntry game)
    {
        // The shader add-on runs the same engine on the same records.
        if (!ShadersAddon.Present) return new Status("noApp", null, null, null);
        // Riot's games are never touched, read or warmed.
        if (AntiCheatCheck.IsRiot(game)) return new Status("unsupported", "Riot's games are left alone.", null, null);
        var match = Match(game);
        if (match is null) return new Status("unsupported", "Exo Shaders does not list this game.", null, null);
        match = FromRecord(match);
        return match.Status switch
        {
            "Warmed" => Warmed(game.Id, match),
            "Stale" => new Status("stale", DriverChanged().IsMatch(match.Reason) ? "Driver changed" : "Game updated", match.WarmedAt, match.Driver, match.Id),
            "Ready" => new Status("ready", Sentence(match.Reason), null, null, match.Id),
            "NeedsRecording" => new Status("record", Sentence(match.Reason), null, null, match.Id),
            _ => new Status("unsupported", Sentence(match.Reason), null, null),
        };
    }

    /// <summary>
    /// The engine reports the game warmed: a mark it was compiled after goes (<see cref="ShaderWipeMarks.ClearWarmed"/>), and a
    /// mark it wasn't is stale until it is compiled again.
    /// </summary>
    private static Status Warmed(string gameId, Listed match)
    {
        var warmed = match.WarmedAt?.UtcDateTime;
        ShaderWipeMarks.ClearWarmed(gameId, warmed);
        return ShaderWipeMarks.IsMarked(gameId, warmed)
            ? new Status("stale", "Shaders need preparing again", match.WarmedAt, match.Driver, match.Id)
            : new Status("warmed", match.Driver is { } d ? $"Compiled for driver {d}." : null, match.WarmedAt, match.Driver, match.Id);
    }

    internal static bool LooksUnreal(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        try
        {
            if (Directory.Exists(Path.Combine(root, "Engine", "Binaries"))) return true;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (Directory.Exists(Path.Combine(dir, "Binaries", "Win64"))
                    || Directory.Exists(Path.Combine(dir, "Binaries", "WinGDK"))
                    || Directory.Exists(Path.Combine(dir, "Engine", "Binaries")))
                    return true;
            if (GameHdr.UnrealProjects(root).Count > 0) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    internal static bool SupportsFirstLaunchPrepare(string? root, string? engineFamily = null)
    {
        if (engineFamily is { Length: > 0 } && (
            engineFamily.Equals("Unreal", StringComparison.OrdinalIgnoreCase)
            || engineFamily.Equals("RE Engine", StringComparison.OrdinalIgnoreCase)
            || engineFamily.Equals("Frostbite", StringComparison.OrdinalIgnoreCase)
            || engineFamily.Equals("Stingray", StringComparison.OrdinalIgnoreCase)
            || engineFamily.Equals("Dawn", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        if (LooksUnreal(root)) return true;
        try
        {
            if (File.Exists(Path.Combine(root, "re_chunk_000.pak"))) return true;
            if (Directory.Exists(Path.Combine(root, "Data", "Win32"))
                && Directory.EnumerateFiles(Path.Combine(root, "Data"), "*.toc", SearchOption.TopDirectoryOnly).Any())
                return true;
            if (Directory.Exists(Path.Combine(root, "data"))
                && File.Exists(Path.Combine(root, "settings", "settings.ini"))
                && Directory.EnumerateFiles(root, "*stingray*.dll", SearchOption.TopDirectoryOnly).Any())
                return true;
            if (Directory.Exists(Path.Combine(root, "Engine"))
                && Directory.EnumerateFiles(root, "*dawn*.dll", SearchOption.AllDirectories).Any())
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    internal string? EngineFamilyOf(GameEntry game) => Match(game)?.EngineFamily;

    internal bool SupportsFirstLaunchPrepare(GameEntry game) =>
        SupportsFirstLaunchPrepare(game.Path, EngineFamilyOf(game));

    /// <summary>The engine's entry for an Exo game: the same id, else the same name, else the same exe in the install.</summary>
    private Listed? Match(GameEntry game) => Match(ReadList(), game);

    internal static Listed? Match(IReadOnlyList<Listed> list, GameEntry game) =>
        list.FirstOrDefault(g => g.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) && SameInstall(g.InstallDir, game.Path))
        ?? list.FirstOrDefault(g => NameKey(g.Name) == NameKey(game.Title) && SameInstall(g.InstallDir, game.Path))
        ?? MatchByExe(list, game);

    private static Listed? MatchByExe(IReadOnlyList<Listed> list, GameEntry game)
    {
        var target = game.LaunchTarget is { Length: > 0 } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? NormalizePath(t) : null;
        if (target is not null)
        {
            var exact = list.FirstOrDefault(g => SameInstall(g.InstallDir, game.Path) && string.Equals(NormalizePath(g.ExePath), target, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var targetName = Path.GetFileName(target);
            var byName = list.Where(g => SameInstall(g.InstallDir, game.Path) && string.Equals(Path.GetFileName(g.ExePath), targetName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
        }
        if (!string.IsNullOrWhiteSpace(game.Path))
        {
            var under = list.Where(g => SameInstall(g.InstallDir, game.Path) && !string.IsNullOrWhiteSpace(g.ExePath) && PathHelper.IsUnderRoot(g.ExePath, game.Path)).ToList();
            if (under.Count == 1) return under[0];
        }
        return null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private IReadOnlyList<Listed> ReadList()
    {
        var path = Path.Combine(DataDir, "games.json");
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            lock (_gate) if (_list is { } cached && cached.Written == written) return cached.Games;
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var games = new List<Listed>();
            if (doc.RootElement.TryGetProperty("Games", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var state in array.EnumerateArray())
                {
                    if (!state.TryGetProperty("Game", out var game) || Text(game, "Id") is not { } id) continue;
                    string? engineFamily = state.TryGetProperty("Engine", out var eng) ? Text(eng, "Family") : null;
                    games.Add(new Listed(id, Text(game, "Name") ?? id, Text(game, "InstallDir") ?? "",
                        Text(state, "Status") ?? "Unsupported", Text(state, "StatusReason") ?? "",
                        Date(Text(state, "WarmedAt")), Text(state, "WarmedDriverVersion"), engineFamily, Text(game, "ExePath")));
                }
            lock (_gate) _list = (written, games);
            return games;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A compile after the list was written updated the game's record: when it
    /// was for a graphics driver installed now, the game reads as compiled.
    /// </summary>
    private static Listed FromRecord(Listed listed)
    {
        try
        {
            var path = Path.Combine(DataDir, "games", listed.Id.Replace(':', '_'), "state.json");
            if (!File.Exists(path)) return listed;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var record = doc.RootElement;
            if (Date(Text(record, "WarmedAt")) is not { } warmed) return listed;
            if (listed.Status == "Warmed" && listed.WarmedAt is { } at && at >= warmed) return listed;
            // DriverId is vendor:device:subsystem:driver version, the registry's DriverVersion.
            if (Text(record, "WarmedDriverId")?.Split(':')[^1] is not { Length: > 0 } driver ||
                !GpuCapability.DriverFingerprint().Split('|').Contains(driver, StringComparer.OrdinalIgnoreCase)) return listed;
            return listed with { Status = "Warmed", Reason = "", WarmedAt = warmed, Driver = Text(record, "WarmedDriverVersion") ?? listed.Driver };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return listed;
        }
    }

    /// <summary>
    /// The installed games whose shaders the engine compiled for a driver that is not installed now: what
    /// <see cref="ShaderRebuild"/> compiles again after a driver change. A game never compiled is not one (its first compile
    /// is the player's press, or the engine's own), and Riot's games are never listed.
    /// </summary>
    internal IReadOnlyList<RebuildGame> StaleForDriver(IReadOnlyList<GameEntry> library)
    {
        if (!ShadersAddon.Present) return [];
        var installed = GpuCapability.DriverFingerprint().Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (installed.Length == 0) return [];
        var games = new List<RebuildGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in library)
        {
            if (!game.Installed || AntiCheatCheck.IsRiot(game) || Match(game) is not { } match || !seen.Add(match.Id)) continue;
            if (CompiledForAnotherDriver(WarmedDriverId(match.Id), installed))
                games.Add(new RebuildGame(match.Id, game.Title, game.PlaytimeMinutes ?? 0, game.LastPlayedUtc));
        }
        return games;
    }

    /// <summary>
    /// The installed games the engine can compile that are not compiled for the driver installed now: compiled for another
    /// driver, never compiled (ready), or updated or wiped since (stale). What <see cref="ShaderRebuild"/> compiles by
    /// itself. A game that needs a recording is not one, and Riot's games are never listed.
    /// </summary>
    internal IReadOnlyList<RebuildGame> ToCompile(IReadOnlyList<GameEntry> library)
    {
        var games = StaleForDriver(library).ToList();
        var seen = games.Select(game => game.EngineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var game in library)
        {
            if (!game.Installed || For(game) is not { State: "ready" or "stale", EngineId: { } id } || !seen.Add(id)) continue;
            games.Add(new RebuildGame(id, game.Title, game.PlaytimeMinutes ?? 0, game.LastPlayedUtc));
        }
        return games;
    }

    /// <summary>
    /// What the installed games are now, as one string: each one's id, folder, size and Steam build. It changes with an
    /// install, an uninstall or an update, which the engine sees only at its next scan.
    /// </summary>
    internal static string Installed(IReadOnlyList<GameEntry> library)
    {
        var lines = library.Where(game => game.Installed && !AntiCheatCheck.IsRiot(game))
            .Select(game => $"{game.Id}|{game.Path}|{game.SizeBytes}|{KeepContext.SteamBuildId(game)}")
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..32];
    }

    /// <summary>
    /// A game's record names the driver its shaders were compiled for (vendor:device:subsystem:driver version): true when
    /// that version is not one installed now. No record, or one that names no driver, is a game never compiled.
    /// </summary>
    internal static bool CompiledForAnotherDriver(string? warmedDriverId, IReadOnlyCollection<string> installedVersions) =>
        warmedDriverId?.Split(':')[^1] is { Length: > 0 } version && !installedVersions.Contains(version, StringComparer.OrdinalIgnoreCase);

    /// <summary>The driver a game's last compile was for, from its record; null when it has none.</summary>
    private static string? WarmedDriverId(string engineId)
    {
        try
        {
            var path = Path.Combine(DataDir, "games", engineId.Replace(':', '_'), "state.json");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return Date(Text(doc.RootElement, "WarmedAt")) is null ? null : Text(doc.RootElement, "WarmedDriverId");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ compile time

    /// <summary>
    /// Seconds each listed game takes to compile on this PC, by the engine's id: the engine's estimate
    /// (reading and preparing its files, then its plan at the rate its own cold compiles measured, else a
    /// stand-in). A game without one yet (no plan) is sized from its shader count at the median seconds
    /// per shader of the games that have both, else takes the median game. The last compile's own time
    /// isn't read: a rewarm onto a cache that still held the game took seconds. System > Shaders fills
    /// these in where the engine's snapshot has no estimate, as rough ones.
    /// </summary>
    internal IReadOnlyDictionary<string, double> CompileSeconds()
    {
        var path = Path.Combine(DataDir, "games.json");
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, double>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var games = new List<(string, TimeSpan?, long?)>();
            if (doc.RootElement.TryGetProperty("Games", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var state in array.EnumerateArray())
                    if (state.TryGetProperty("Game", out var game) && Text(game, "Id") is { } id)
                        games.Add((id, Span(Text(state, "EstimatedWarmTime")),
                            state.TryGetProperty("ShaderCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var n) ? n : null));
            return CompileSeconds(games);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, double>();
        }
    }

    internal static Dictionary<string, double> CompileSeconds(IEnumerable<(string Id, TimeSpan? Estimated, long? Shaders)> games)
    {
        var list = games.Select(g => (g.Id, Known: g.Estimated is { TotalSeconds: > 0 } est ? est.TotalSeconds : (double?)null,
            Shaders: g.Shaders is > 0 ? g.Shaders : null)).ToList();
        var perShader = Median(list.Where(g => g.Known is not null && g.Shaders is not null).Select(g => g.Known!.Value / g.Shaders!.Value));
        var typical = Median(list.Where(g => g.Known is not null).Select(g => g.Known!.Value));
        var seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, known, shaders) in list)
            if ((known ?? (shaders is { } s && perShader is { } rate ? s * rate : typical)) is { } value) seconds[id] = Math.Round(value);
        return seconds;
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static TimeSpan? Span(string? text) =>
        text is not null && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) ? span : null;

    // ------------------------------------------------------------------ the old built-in copy

    /// <summary>
    /// Earlier Exo versions downloaded their own copy of SCSKiller into
    /// tools\scskiller. Any recorder that copy put in a game folder is taken out
    /// with its own command line, then the folder goes. Once; never throws.
    /// </summary>
    private static void RemoveOldCopy()
    {
        var root = Path.Combine(PathHelper.AppDataDir, "tools", "scskiller");
        if (!Directory.Exists(root)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var cli = Directory.EnumerateFiles(root, "scskiller.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "cli");
                foreach (var id in OldRecorders(Path.Combine(root, "data", "games.json")))
                {
                    if (cli is null) { AppLog.Info($"SCSKiller: the old copy's recorder in {id} stays; its command line is gone"); continue; }
                    using var process = Process.Start(new ProcessStartInfo(cli) { ArgumentList = { "record", "uninstall", id }, UseShellExecute = false, CreateNoWindow = true });
                    if (process is not null && !process.WaitForExit(60_000)) ProcessTermination.Kill(process, entireProcessTree: true);
                    AppLog.Info($"SCSKiller: the old copy's recorder taken out of {id} (exit {process?.ExitCode})");
                }
                if (RecursiveDeleteGuard.TryValidateManagedChild(Path.Combine(PathHelper.AppDataDir, "tools"), root, out var safe, out _))
                {
                    DeletionGate.DeleteDirectory(safe, recursive: true);
                    AppLog.Info("SCSKiller: Exo's old built-in copy removed; the app does everything now");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Info("SCSKiller: the old built-in copy could not be removed yet: " + ex.Message);
            }
        });
    }

    private static IEnumerable<string> OldRecorders(string gamesJson)
    {
        try
        {
            if (!File.Exists(gamesJson)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(gamesJson));
            if (!doc.RootElement.TryGetProperty("Games", out var games) || games.ValueKind != JsonValueKind.Array) return [];
            return games.EnumerateArray()
                .Where(g => g.TryGetProperty("RecorderInstalled", out var r) && r.ValueKind == JsonValueKind.True)
                .Select(g => g.TryGetProperty("Game", out var game) ? Text(game, "Id") : null)
                .OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    // ------------------------------------------------------------------ helpers

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static string? Sentence(string reason)
    {
        var text = reason.Trim();
        if (text.Length == 0) return null;
        text = char.ToUpperInvariant(text[0]) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>The app's install folder and Exo's are the same, or one holds the other.</summary>
    private static bool SameInstall(string scsKiller, string? exo)
    {
        if (string.IsNullOrWhiteSpace(exo) || string.IsNullOrWhiteSpace(scsKiller)) return false;
        try
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scsKiller));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(File.Exists(exo) ? Path.GetDirectoryName(exo)! : exo));
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) || PathHelper.IsUnderRoot(b, a) || PathHelper.IsUnderRoot(a, b);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>"STAR WARS Jedi: Fallen Order™ " and "Star Wars Jedi: Fallen Order" are one key.</summary>
    private static string NameKey(string name) =>
        NameNoise().Replace(TradeMarks().Replace(name, "").Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), " ").Trim();

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    [GeneratedRegex(@"^driver changed", RegexOptions.IgnoreCase)]
    private static partial Regex DriverChanged();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NameNoise();

    [GeneratedRegex("[™®©]")]
    private static partial Regex TradeMarks();
}
