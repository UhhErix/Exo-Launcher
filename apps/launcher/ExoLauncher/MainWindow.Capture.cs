using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// Capture: Exo Capture (<see cref="Services.CaptureAddon"/>) records instant replays,
/// recordings and snips in its own process; Exo keeps its settings, tells it which
/// game is running, lists the captures folder for the Captures sheet and the game
/// page, and passes trims, exports and edits to it. Captures stay on this PC.
/// Settings and the list of favorites live in <c>capture\settings.json</c>,
/// what each saved file belongs to in <c>capture\index.json</c>, thumbnails in
/// <c>capture\thumbs</c>, all under Exo's data folder.
/// </summary>
public sealed partial class MainWindow
{
    internal const string CaptureMediaHost = "captures.exo-launcher.local";
    internal const string CaptureThumbHost = "capture-thumbs.exo-launcher.local";

    internal static string CaptureData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "capture");
    internal static string CaptureThumbs => Path.Combine(CaptureData, "thumbs");
    internal static string CaptureSettingsPath => Path.Combine(CaptureData, "settings.json");
    internal static string CaptureIndexPath => Path.Combine(CaptureData, "index.json");
    internal static string DefaultCaptureFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Exo Captures");

    private static readonly JsonSerializerOptions CaptureJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _captureGate = new();
    private Services.CaptureAddon? _captureAddon;
    private CaptureSettings? _captureSettings;
    private Dictionary<string, CaptureMeta>? _captureIndex;
    private JsonNode? _captureState;
    private volatile bool _captureBusy;
    private string? _captureGameId;
    private string? _captureRoot;
    private long? _captureConfiguredGeneration;
    private readonly Services.CaptureGameTarget _captureTarget = new();

    internal sealed class CaptureSettings
    {
        public int Quality { get; set; } = 50;
        public int ReplaySeconds { get; set; } = 60;
        /// <summary>60, or 120 (up to the screen's refresh rate).</summary>
        public int MaxFps { get; set; } = 60;
        /// <summary>games (while a game runs), always, or off.</summary>
        public string ReplayMode { get; set; } = "games";
        public bool Cursor { get; set; } = true;
        public string SaveReplayKeys { get; set; } = "Ctrl+Alt+F10";
        public string RecordKeys { get; set; } = "Ctrl+Alt+F9";
        public string SnipKeys { get; set; } = "Ctrl+Alt+F8";
        public bool PrintScreen { get; set; }
        /// <summary>edit: a snip opens in the editor (not one taken over a game); copy: it is only copied and saved.</summary>
        public string AfterSnip { get; set; } = "edit";
        /// <summary>Null: Videos\Exo Captures.</summary>
        public string? Folder { get; set; }
        /// <summary>0: no limit.</summary>
        public int KeepGb { get; set; }
        public int KeepDays { get; set; }
        /// <summary>Exo Capture re-encodes recordings to smaller files that look the same, while no game runs.</summary>
        public bool AutoShrink { get; set; } = true;
        /// <summary>
        /// Record the call's voices: the call on a track of its own and in the mix, and Exo tells the call it's recorded
        /// (Discord's clips flag). Off, the call is on no track and nowhere in the mix; your microphone and the game stay.
        /// </summary>
        public bool CallVoices { get; set; }
        /// <summary>Paths relative to the captures folder; never deleted by the limits.</summary>
        public List<string> Favourites { get; set; } = new();
    }

    internal sealed class CaptureMeta
    {
        public string? GameId { get; set; }
        public double DurationMs { get; set; }
        /// <summary>done once shrunk, else why the add-on left it as it is; null: not tried.</summary>
        public string? Shrink { get; set; }
        /// <summary>A trim, an export or a reel made from other clips: a highlight reel leaves it out (its picture is in the original).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool Derived { get; set; }
    }

    private CaptureSettings CaptureConfig
    {
        get
        {
            lock (_captureGate)
            {
                if (_captureSettings is not null) return _captureSettings;
                try
                {
                    if (File.Exists(CaptureSettingsPath))
                        _captureSettings = JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(CaptureSettingsPath), CaptureJson);
                }
                catch (Exception ex) { AppLog.Warn("Capture settings unreadable, using defaults: " + ex.Message); }
                return _captureSettings ??= new CaptureSettings();
            }
        }
    }

    private Dictionary<string, CaptureMeta> CaptureIndex
    {
        get
        {
            lock (_captureGate)
            {
                if (_captureIndex is not null) return _captureIndex;
                try
                {
                    if (File.Exists(CaptureIndexPath))
                        _captureIndex = JsonSerializer.Deserialize<Dictionary<string, CaptureMeta>>(File.ReadAllText(CaptureIndexPath), CaptureJson) is { } read
                            ? new Dictionary<string, CaptureMeta>(read, StringComparer.OrdinalIgnoreCase) : null;
                }
                catch (Exception ex) { AppLog.Warn("Capture index unreadable: " + ex.Message); }
                return _captureIndex ??= new Dictionary<string, CaptureMeta>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, CaptureJson));
        File.Move(temp, path, overwrite: true);
    }

    private void SaveCaptureSettings() { lock (_captureGate) WriteJsonAtomic(CaptureSettingsPath, CaptureConfig); }
    private void SaveCaptureIndex() { lock (_captureGate) WriteJsonAtomic(CaptureIndexPath, CaptureIndex); }

    /// <summary>The captures folder, full and without a trailing separator.</summary>
    private string CaptureFolder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(CaptureConfig.Folder ?? DefaultCaptureFolder));

    // ------------------------------------------------------------------ add-on

    private readonly SemaphoreSlim _captureSyncGate = new(1, 1);
    private readonly Lock _addonGate = new();

    private Services.CaptureAddon? CaptureAddon
    {
        get
        {
            if (_captureAddon is not null || !Services.CaptureAddon.Present) return _captureAddon;
            using var scope = _addonGate.EnterScope();
            if (_captureAddon is not null) return _captureAddon;
            var addon = new Services.CaptureAddon();
            addon.Event += OnCaptureEvent;
            // A game starting or closing, however it was started: the bridge's run-state poll
            // reports games started from a store, a shortcut or before Exo as well.
            App.Services.Launcher.GameRunStateChanged += (gameId, state) => _ = Task.Run(async () =>
            {
                if (state.IsRunning)
                {
                    CancelShrink();
                    StopReelForGame();
                }
                await SyncCaptureGameAsync().ConfigureAwait(false);
            });
            return _captureAddon = addon;
        }
    }

    /// <summary>Exo Capture for Discord's Go Live (pictures of windows and screens, and the stream itself); null when it isn't installed.</summary>
    internal Services.CaptureAddon? StreamCapture => CaptureAddon;

    /// <summary>A text file Windows opened with Exo while it runs: in Captures › Notes, or once the page is up.</summary>
    /// <summary>A place an exolauncher:// link names: shown now, or once the page is up.</summary>
    internal void OpenLink(Services.ExoLinks.Place place)
    {
        if (_bridge is { } bridge) bridge.OpenLink(place);
        else Services.WebHostBridge.QueueOpen(place);
    }

    internal void OpenNoteFromWindows(string path)
    {
        if (_bridge is { } bridge) bridge.OpenNoteFromWindows(path);
        else Services.WebHostBridge.QueueNoteFromWindows(path);
    }

    /// With the connected rooms: Exo Capture, so its hotkeys work from the start.
    private Task PrewarmCapture() => CaptureAddon is { } addon ? Task.Run(() =>
    {
        addon.Start();
        _ = Task.Run(EnforceCaptureLimits);
    }) : Task.CompletedTask;

    private async Task ConfigureCaptureAsync(bool retry = false)
    {
        if (HostMode.IsDisposable || CaptureAddon is not { } addon) return;
        var generation = addon.ReadyGeneration;
        if (generation is null) { addon.Start(); return; }
        var attemptAgain = false;
        await _captureSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (addon.ReadyGeneration != generation) return;
            await addon.CallAsync("configure", CaptureParameters(addon.SupportsReplayBudget), _lifetimeCts.Token, generation, applyState: true).ConfigureAwait(false);
            if (addon.ReadyGeneration != generation) return;
            _captureConfiguredGeneration = generation;
            ScheduleShrink(TimeSpan.FromMinutes(3));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Warn("Capture configure: " + ex.Message);
            attemptAgain = retry && addon.ReadyGeneration == generation;
        }
        finally { _captureSyncGate.Release(); }
        if (attemptAgain)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (addon.ReadyGeneration == generation && !_lifetimeCts.IsCancellationRequested)
                await ConfigureCaptureAsync().ConfigureAwait(false);
        }
        else await SyncCaptureGameAsync().ConfigureAwait(false);
    }

    private static readonly Lazy<ulong> CapturePhysicalMemory = new(() =>
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    });

    private JsonObject CaptureParameters(bool supportsReplayBudget)
    {
        var folder = CaptureFolder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { AppLog.Warn("Captures folder: " + ex.Message); }
        lock (_captureGate)
        {
            var s = CaptureConfig;
            var replay = Services.CaptureReplayBudget.For(s.ReplaySeconds, CapturePhysicalMemory.Value, supportsReplayBudget);
            var parameters = new JsonObject
            {
                ["quality"] = s.Quality,
                ["replaySeconds"] = replay.Seconds,
                ["maxFps"] = s.MaxFps,
                ["replayMode"] = s.ReplayMode,
                ["cursor"] = s.Cursor,
                ["folder"] = folder,
                ["thumbs"] = CaptureThumbs,
                ["printScreen"] = s.PrintScreen,
                ["callVoices"] = s.CallVoices,
                ["mic"] = App.Services.Settings.Shared.DiscordInputDevice ?? "",
                ["cleanClips"] = Services.DiscordClient.Voice.RecordedVoice.CanCleanLater,
                ["font"] = CaptureFont(),
                ["hotkeys"] = new JsonObject { ["saveReplay"] = s.SaveReplayKeys, ["record"] = s.RecordKeys, ["snip"] = s.SnipKeys },
            };
            if (replay.MaxBytes is long bytes) parameters["replayMaxBytes"] = bytes;
            return parameters;
        }
    }

    /// <summary>Exo's Input Device changed: a replay's microphone track opens the new one.</summary>
    internal void CaptureMicChanged()
    {
        if (_captureAddon is not null) _ = ConfigureCaptureAsync();
    }

    /// <summary>
    /// Tells the add-on which installed game is running now, however it was started: the one
    /// it already follows while that still runs, else the first running one; none ends it.
    /// </summary>
    private async Task SyncCaptureGameAsync(bool retry = true)
    {
        if (_captureAddon is not { } addon || _lifetimeCts.IsCancellationRequested) return;
        var attemptAgain = false;
        await _captureSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Read the desired target when this turn reaches the front, not when its event was queued.
            var running = RunningLibraryGames();
            var before = _captureGameId;
            var id = before is not null && running.ContainsKey(before) ? before : running.Keys.FirstOrDefault();
            var game = id is null ? null : App.Services.Library.Find(id);
            if (game is null || string.IsNullOrWhiteSpace(game.Path)) id = null;
            _captureGameId = id;
            if (id is null && before is not null)
            {
                ScheduleShrink(TimeSpan.FromMinutes(1));
                ResumeReel();
            }
            else if (id is not null && !string.Equals(id, before, StringComparison.OrdinalIgnoreCase))
                AppLog.Info("Capture: following " + game!.Title + ".");

            if (addon.ReadyGeneration is not long generation || _captureConfiguredGeneration != generation
                || !_captureTarget.Begin(generation, id)) return;
            // Unknown or protected games never get an injected hook.
            var target = id is null ? new JsonObject() : new JsonObject
            {
                ["id"] = id,
                ["title"] = game!.Title,
                ["folder"] = game.Path,
                ["hook"] = Services.AntiCheatCheck.Allows(game, Services.AntiCheatCheck.Cached(game), Services.AntiCheatCheck.Change.InjectedHook),
            };
            await addon.CallAsync("game", target, _lifetimeCts.Token, generation).ConfigureAwait(false);
            _captureTarget.Acknowledge(generation, id);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Debug("Capture: game target not applied: " + ex.GetType().Name);
            attemptAgain = retry;
        }
        finally { _captureSyncGate.Release(); }
        if (attemptAgain)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await SyncCaptureGameAsync(retry: false).ConfigureAwait(false);
        }
    }

    /// <summary>Installed library games running now, however they were started.</summary>
    internal static Dictionary<string, Services.GameRunState> RunningLibraryGames()
    {
        var installed = App.Services.Library.PeekCachedLibrary()
            .Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path))
            .ToArray();
        return installed.Length == 0
            ? new Dictionary<string, Services.GameRunState>()
            : App.Services.Launcher.GetGameRunStates(installed, underRootOnly: true)
                .Where(row => row.Value.IsRunning)
                .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Exo's own type (Goeist), so the add-on's toast and windows read like Exo.</summary>
    private static string CaptureFont()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Goeist-Variable.ttf");
        return File.Exists(path) ? path : "";
    }

    private void OnCaptureEvent(string name, JsonNode? data)
    {
        switch (name)
        {
            case "ready":
            case "state":
                _captureState = data;
                // A replay or recording of any kind: the shell keeps running for it (ShellSleepRules).
                _captureBusy = data?["buffering"]?.GetValue<bool>() == true || data?["recording"]?.GetValue<bool>() == true;
                ReevaluateShellSleep();
                // Recording: its microphone track takes Exo's cleaned voice. Keeping a replay: the add-on holds the microphone raw, and Exo cleans what is saved.
                if (CaptureAddon is { } voiceAddon)
                    Services.DiscordClient.Voice.RecordedVoice.Set(voiceAddon, data?["buffering"]?.GetValue<bool>() == true, data?["recording"]?.GetValue<bool>() == true, data?["cleansClips"]?.GetValue<bool>() == true);
                // Only a replay or recording with the call's voices in it: Exo's Discord client says so in calls, as Discord's clips do.
                Services.DiscordClient.DiscordSession.Recording = CaptureHoldsCall(data);
                _bridge?.PostCaptureEvent("capture.state", CaptureStateDto());
                if (name == "ready") _ = Task.Run(() => ConfigureCaptureAsync(retry: true));
                break;
            case "exited":
                if (CaptureAddon is { } goneAddon) Services.DiscordClient.Voice.RecordedVoice.Set(goneAddon, false, false, false);
                Services.DiscordClient.DiscordSession.Recording = false;
                _captureState = null;
                _captureBusy = false;
                ReevaluateShellSleep();
                _bridge?.PostCaptureEvent("capture.state", CaptureStateDto());
                break;
            case "failed":
                _captureState = null;
                _captureBusy = false;
                ReevaluateShellSleep();
                _bridge?.PostCaptureEvent("capture.state", CaptureStateDto());
                break;
            case "concat":
                OnReelProgress(data);
                break;
            case "clip.voice":
                if (CaptureAddon is { } cleaner && data is not null)
                    Services.DiscordClient.Voice.ClipVoice.Start(cleaner, data, () => OnCaptureEvent("saved", data));
                break;
            case "saved":
                if (data?["path"]?.GetValue<string>() is not { } path) return;
                var relative = RelativeCapture(path);
                if (relative is null) return;
                var kind = data["kind"]?.GetValue<string>();
                lock (_captureGate)
                {
                    var game = data["game"]?.GetValue<string>();
                    // A trim or export belongs to the game of the file it came from.
                    CaptureIndex[relative] = new CaptureMeta
                    {
                        GameId = string.IsNullOrEmpty(game) ? GameOfFolder(relative) : game,
                        DurationMs = data["durationMs"]?.GetValue<double>() ?? 0,
                        Derived = kind is "trim" or "export" or "concat",
                    };
                }
                try { SaveCaptureIndex(); } catch (Exception ex) { AppLog.Warn("Capture index: " + ex.Message); }
                _ = Task.Run(EnforceCaptureLimits);
                // A snip opens in the page's editor, Exo in front for it, unless After a snip is Copy only; one taken over
                // the game leaves the game in front (Exo Capture said it was saved) and waits in Captures.
                var edit = Services.CaptureShare.OpensEditor(kind, data["inGame"]?.GetValue<bool>() == true, "edit");
                if (ItemFor(path) is { } item) _bridge?.PostCaptureEvent("capture.saved", new { item, edit });
                if (kind != "snip") ScheduleShrink(Services.CaptureShrink.Settle + TimeSpan.FromSeconds(5));
                if (edit) DispatcherQueue.Enqueue(() => RestoreAndActivate("a snip opens in the editor"));
                break;
        }
    }

    /// <summary>
    /// Exo Capture keeps a replay or records, and its sound takes the call's voices (Record the call's voices). An add-on
    /// that doesn't say records the call: every one before 3.18 did.
    /// </summary>
    internal static bool CaptureHoldsCall(JsonNode? state) =>
        (state?["buffering"]?.GetValue<bool>() == true || state?["recording"]?.GetValue<bool>() == true)
        && state?["callVoices"]?.GetValue<bool>() != false;

    private object CaptureStateDto()
    {
        var state = _captureState;
        return new
        {
            present = Services.CaptureAddon.Present,
            running = state is not null,
            buffering = state?["buffering"]?.GetValue<bool>() ?? false,
            replayRetainedMs = Services.CaptureReplayBudget.RetainedMs(state),
            replayLimitSeconds = _captureAddon is { ReadyGeneration: not null } addon
                ? (int?)Services.CaptureReplayBudget.For(CaptureConfig.ReplaySeconds, CapturePhysicalMemory.Value, addon.SupportsReplayBudget).Seconds
                : null,
            recording = state?["recording"]?.GetValue<bool>() ?? false,
            holdsCall = CaptureHoldsCall(state),
            recordingMs = state?["recordingMs"]?.GetValue<double>() ?? 0,
            codec = state?["codec"]?.GetValue<string>(),
            encoder = state?["encoder"]?.GetValue<string>(),
            width = state?["width"]?.GetValue<double>(),
            height = state?["height"]?.GetValue<double>(),
            fps = state?["fps"]?.GetValue<double>(),
            hdr = state?["hdr"]?.GetValue<bool>() ?? false,
            error = state?["error"]?.GetValue<string>(),
            hotkeys = new
            {
                saveReplay = state?["hotkeys"]?["saveReplay"]?["ok"]?.GetValue<bool>() ?? true,
                record = state?["hotkeys"]?["record"]?["ok"]?.GetValue<bool>() ?? true,
                snip = state?["hotkeys"]?["snip"]?["ok"]?.GetValue<bool>() ?? true,
                printScreen = state?["printScreenOk"]?.GetValue<bool>() ?? true,
            },
        };
    }

    private object CaptureSettingsDto()
    {
        var s = CaptureConfig;
        return new
        {
            s.Quality,
            s.ReplaySeconds,
            s.MaxFps,
            s.ReplayMode,
            s.Cursor,
            s.SaveReplayKeys,
            s.RecordKeys,
            s.SnipKeys,
            s.PrintScreen,
            afterSnip = Services.CaptureShare.NormalizeAfterSnip(s.AfterSnip),
            folder = CaptureFolder,
            s.KeepGb,
            s.KeepDays,
            s.AutoShrink,
            s.CallVoices,
        };
    }

    // ------------------------------------------------------------------ bridge

    /// <summary>capture.status: whether the add-on is here, what it is doing, and the settings.</summary>
    internal object CaptureStatus()
    {
        if (CaptureAddon is { } addon) addon.Start();
        return new { ok = true, state = CaptureStateDto(), settings = CaptureSettingsDto() };
    }

    /// <summary>capture.settings: a patch of settings; saved, then passed to the add-on.</summary>
    internal async Task<object> CaptureSetSettingsAsync(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return new { ok = false, message = "Nothing to change." };
        var s = CaptureConfig;
        string? error = null;
        lock (_captureGate)
        {
            foreach (var prop in p.EnumerateObject())
            {
                var v = prop.Value;
                int? n = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                switch (prop.Name)
                {
                    case "quality" when n is { } quality: s.Quality = Math.Clamp(quality, 0, 100); break;
                    case "replaySeconds" when n is { } seconds: s.ReplaySeconds = Math.Clamp(seconds, 30, 600); break;
                    case "maxFps" when n is { } fps: s.MaxFps = fps >= 120 ? 120 : 60; break;
                    case "replayMode" when v.ValueKind == JsonValueKind.String && v.GetString() is "games" or "always" or "off": s.ReplayMode = v.GetString()!; break;
                    case "cursor" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.Cursor = v.GetBoolean(); break;
                    case "printScreen" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.PrintScreen = v.GetBoolean(); break;
                    case "afterSnip" when v.ValueKind == JsonValueKind.String: s.AfterSnip = Services.CaptureShare.NormalizeAfterSnip(v.GetString()); break;
                    case "saveReplayKeys" or "recordKeys" or "snipKeys" when v.ValueKind == JsonValueKind.String:
                        var keys = v.GetString()!.Trim();
                        if (CaptureHotkeyProblem(keys) is { } problem) { error = problem; break; }
                        // Exo's own other action would hold them, and the add-on would report "another app".
                        var holder = new[] { ("saveReplayKeys", "Save replay", s.SaveReplayKeys), ("recordKeys", "Record", s.RecordKeys), ("snipKeys", "Snip", s.SnipKeys) }
                            .FirstOrDefault(other => other.Item1 != prop.Name && string.Equals(other.Item3, keys, StringComparison.OrdinalIgnoreCase));
                        if (holder.Item2 is { } taken) { error = "Already used for " + taken + "."; break; }
                        if (prop.Name == "saveReplayKeys") s.SaveReplayKeys = keys;
                        else if (prop.Name == "recordKeys") s.RecordKeys = keys;
                        else s.SnipKeys = keys;
                        break;
                    case "keepGb" when n is { } gb: s.KeepGb = Math.Clamp(gb, 0, 10_000); break;
                    case "keepDays" when n is { } days: s.KeepDays = Math.Clamp(days, 0, 3650); break;
                    case "autoShrink" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.AutoShrink = v.GetBoolean(); break;
                    case "callVoices" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.CallVoices = v.GetBoolean(); break;
                    case "folder" when v.ValueKind == JsonValueKind.String:
                        var folder = v.GetString()!;
                        // The page maps this folder to itself and lists and recycles its pictures and clips: only one just picked in Windows' picker.
                        if (!PickedPaths.Consume(folder)) error = "Pick the folder with Change.";
                        else if (CaptureFolderProblem(folder) is { } folderProblem) error = folderProblem;
                        else s.Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                        break;
                    case "folder" when v.ValueKind == JsonValueKind.Null: s.Folder = null; break;
                }
            }
        }
        try { SaveCaptureSettings(); }
        catch (Exception ex) { AppLog.Warn("Capture settings: " + ex.Message); return new { ok = false, message = "Exo could not save the capture settings." }; }
        DispatcherQueue.Enqueue(MapCaptureHosts);
        await ConfigureCaptureAsync().ConfigureAwait(true);
        _ = Task.Run(EnforceCaptureLimits);
        ScheduleShrink(TimeSpan.FromSeconds(5));
        return new { ok = error is null, message = error, state = CaptureStateDto(), settings = CaptureSettingsDto() };
    }

    /// <summary>Keys Exo will not take: ones ShadowPlay or Game Bar use, or a bare key that would stop typing it.</summary>
    private static string? CaptureHotkeyProblem(string keys)
    {
        if (keys.Length is 0 or > 40) return "Pick a key combination.";
        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = parts.LastOrDefault() ?? "";
        bool ctrl = parts.Contains("Ctrl"), alt = parts.Contains("Alt"), shift = parts.Contains("Shift"), win = parts.Contains("Win");
        if (win) return "Windows keeps Win combinations for itself.";
        if (!ctrl && !alt && !(key.Length > 1 && key[0] == 'F')) return "Use Ctrl or Alt with a letter or number, or an F key.";
        // NVIDIA's overlay (ShadowPlay) and Steam's own keys.
        var nvidia = new[] { "Alt+F10", "Alt+F9", "Alt+F1", "Alt+Z", "Alt+Shift+F10", "Alt+F2", "Alt+F3" };
        var normalized = string.Join('+', new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, key }.Where(x => x is not null));
        if (nvidia.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return normalized + " is NVIDIA's overlay key.";
        if (normalized is "F12") return "F12 is Steam's screenshot key.";
        return null;
    }

    private static string? CaptureFolderProblem(string folder)
    {
        try
        {
            // A network share or a \\?\ device path would dodge the guards below (and CreateDirectory would reach out to the share).
            if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
                return "Pick a folder on this PC for captures.";
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Path.IsPathFullyQualified(folder) || Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
                return "Pick a folder, not a whole drive.";
            var guarded = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };
            if (guarded.Any(g => !string.IsNullOrEmpty(g) && (string.Equals(full, g, StringComparison.OrdinalIgnoreCase) ||
                g != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && full.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                return "Pick a folder of your own for captures.";
            Directory.CreateDirectory(full);
            return null;
        }
        catch { return "That folder can't be used."; }
    }

    /// <summary>capture.act: saveReplay, record, snip, openFolder.</summary>
    internal async Task<object> CaptureActAsync(string? action)
    {
        if (action == "openFolder")
        {
            try
            {
                Directory.CreateDirectory(CaptureFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + CaptureFolder + "\"") { UseShellExecute = true });
                return new { ok = true };
            }
            catch (Exception ex) { return CaptureFailure("Captures folder", ex); }
        }
        if (CaptureAddon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
        var method = action switch { "saveReplay" => "replay.save", "record" => "record.toggle", "snip" => "snip", "screenshot" => "screenshot", _ => null };
        if (method is null) return new { ok = false, message = "Unknown action." };
        try
        {
            // The snip overlay freezes the screen as it is: the page takes its menu off the screen before it asks.
            if (method == "snip") addon.AllowForeground();
            await addon.CallAsync(method, new JsonObject(), _lifetimeCts.Token, applyState: true).ConfigureAwait(true);
            return new { ok = true, state = CaptureStateDto() };
        }
        catch (Exception ex) { return CaptureFailure("Capture " + method, ex); }
    }

    /// <summary>
    /// The add-on's own failures (InvalidOperationException) are lines meant for the user; file and
    /// Windows errors carry paths, which stay in native code: those go to the log.
    /// </summary>
    private static object CaptureFailure(string what, Exception ex)
    {
        if (ex is InvalidOperationException) return new { ok = false, message = ex.Message };
        AppLog.Warn(what + ": " + ex.GetType().Name + ": " + ex.Message);
        return new { ok = false, message = "Exo could not finish that. The details are in the log." };
    }

    // ------------------------------------------------------------------ the captures folder

    private static readonly string[] CaptureExtensions = [".mp4", ".png", ".gif", ".webm"];

    /// <summary>FNV-1a of the full path's UTF-8: the item id and the thumbnail's name (the add-on works it out the same way).</summary>
    private static string CaptureId(string fullPath)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(fullPath)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }

    private string? RelativeCapture(string path)
    {
        var full = Path.GetFullPath(path);
        var root = CaptureFolder + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : null;
    }

    private IEnumerable<string> CaptureFiles()
    {
        var root = CaptureFolder;
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(root).ToList(); } catch { yield break; }
        foreach (var folder in folders)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(folder).Where(f => CaptureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private string? GameOfFolder(string relative)
    {
        var folder = relative.Split(Path.DirectorySeparatorChar)[0];
        foreach (var game in App.Services.Library.PeekCachedLibrary())
            if (string.Equals(SafeCaptureName(game.Title), folder, StringComparison.OrdinalIgnoreCase)) return game.Id;
        return null;
    }

    /// <summary>The folder name the add-on gives a game (its SafeName).</summary>
    private static string SafeCaptureName(string title)
    {
        var chars = title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.').Trim();
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        return name.Length == 0 ? "Capture" : name;
    }

    internal sealed record CaptureItem(string Id, string Kind, string Game, string? GameId, string Name, string Created,
        long Size, double? DurationMs, bool Favourite, string Url, string? Thumb, bool Derived);

    private CaptureItem? ItemFor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var relative = RelativeCapture(info.FullName);
            if (relative is null) return null;
            var id = CaptureId(info.FullName);
            CaptureMeta? meta;
            lock (_captureGate) CaptureIndex.TryGetValue(relative, out meta);
            var folder = relative.Split(Path.DirectorySeparatorChar)[0];
            var image = info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || info.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            var url = LocalFileHost.Url(CaptureMediaHost, string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)));
            var thumb = File.Exists(Path.Combine(CaptureThumbs, id + ".jpg"))
                ? LocalFileHost.Url(CaptureThumbHost, id + ".jpg?v=") + info.LastWriteTimeUtc.Ticks.ToString("x")
                : image ? url : null;
            bool favourite;
            lock (_captureGate) favourite = CaptureConfig.Favourites.Contains(relative, StringComparer.OrdinalIgnoreCase);
            return new CaptureItem(id, image ? "image" : "video", folder, meta?.GameId ?? GameOfFolder(relative),
                Path.GetFileNameWithoutExtension(info.Name), info.CreationTimeUtc.ToString("o"), info.Length,
                meta?.DurationMs > 0 ? meta.DurationMs : null, favourite, url + "?v=" + info.LastWriteTimeUtc.Ticks.ToString("x"), thumb, meta?.Derived == true);
        }
        catch { return null; }
    }

    private string? PathOfCapture(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 16) return null;
        return CaptureFiles().FirstOrDefault(f => CaptureId(Path.GetFullPath(f)) == id);
    }


    /// <summary>capture.list: every capture, newest first; with a game id, only that game's.</summary>
    internal object CaptureList(string? gameId, int? limit)
    {
        // Lazy past the sort: a game page's strip of six builds six items, not one per file.
        var items = CaptureFiles()
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => ItemFor(f.FullName))
            .Where(i => i is not null);
        if (!string.IsNullOrEmpty(gameId))
        {
            var title = App.Services.Library.Find(gameId)?.Title;
            var folder = title is null ? null : SafeCaptureName(title);
            items = items.Where(i => string.Equals(i!.GameId, gameId, StringComparison.OrdinalIgnoreCase) ||
                folder is not null && string.Equals(i.Game, folder, StringComparison.OrdinalIgnoreCase));
        }
        if (limit is > 0) items = items.Take(limit.Value);
        return new { ok = true, items = items.ToList() };
    }

    /// <summary>capture.item: favourite, unfavourite, delete (to the Recycle Bin), reveal, copy, shrink.</summary>
    internal async Task<object> CaptureItemAsync(string? id, string? action)
    {
        var path = PathOfCapture(id);
        if (path is null) return new { ok = false, message = "That capture is gone." };
        var relative = RelativeCapture(path)!;
        switch (action)
        {
            case "favourite":
            case "unfavourite":
                lock (_captureGate)
                {
                    CaptureConfig.Favourites.RemoveAll(f => string.Equals(f, relative, StringComparison.OrdinalIgnoreCase));
                    if (action == "favourite") CaptureConfig.Favourites.Add(relative);
                }
                SaveCaptureSettings();
                return new { ok = true, item = ItemFor(path) };
            case "delete":
                if (!RecycleFile(path)) return new { ok = false, message = "Windows could not move it to the Recycle Bin." };
                ForgetCapture(path, relative);
                return new { ok = true };
            case "reveal":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return new { ok = true };
            case "shrink":
                return StartManualShrink(path, relative);
            case "copy":
                if (CaptureAddon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
                var image = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                try
                {
                    // A snip copies as a picture; a video as a file to paste.
                    await addon.CallAsync(image ? "copyImage" : "copy", new JsonObject { ["path"] = path }, CancellationToken.None).ConfigureAwait(true);
                    return new { ok = true };
                }
                catch (Exception ex) { return CaptureFailure("Capture copy", ex); }
        }
        return new { ok = false, message = "Unknown action." };
    }

    /// <summary>capture.edit: trim (lossless) or an export preset (discord, youtube, vertical) between two points.</summary>
    internal async Task<object> CaptureEditAsync(string? id, string? preset, double? startMs, double? endMs)
    {
        if (CaptureAddon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
        var path = PathOfCapture(id);
        if (path is null || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That clip is gone." };
        var parameters = new JsonObject { ["path"] = path, ["startMs"] = Math.Max(0, startMs ?? 0), ["endMs"] = Math.Max(0, endMs ?? 0) };
        try
        {
            JsonNode? result;
            if (preset is null or "trim") result = await addon.CallAsync("trim", parameters, CancellationToken.None).ConfigureAwait(true);
            else if (preset is "discord" or "youtube" or "vertical" or "gif" or "webm")
            {
                parameters["preset"] = preset;
                result = await addon.CallAsync("export", parameters, CancellationToken.None).ConfigureAwait(true);
            }
            else return new { ok = false, message = "Unknown export." };
            var made = result?["path"]?.GetValue<string>();
            return new { ok = true, item = made is null ? null : ItemFor(made) };
        }
        catch (Exception ex) { return CaptureFailure("Capture edit", ex); }
    }

    /// <summary>capture.saveImage: the snip editor's result over the snip (PNG, at most 64 MB), then copied.</summary>
    internal async Task<object> CaptureSaveImageAsync(string? id, string? base64)
    {
        if (HostMode.IsDisposable) return new { ok = false, message = (string?)HostMode.Refusal };
        var path = PathOfCapture(id);
        if (path is null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That snip is gone." };
        if (string.IsNullOrEmpty(base64) || base64.Length > 90_000_000) return new { ok = false, message = "Nothing to save." };
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch { return new { ok = false, message = "Nothing to save." }; }
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P') return new { ok = false, message = "Nothing to save." };
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(true);
            File.Move(temp, path, overwrite: true);
            try { DeletionGate.DeleteFile(Path.Combine(CaptureThumbs, CaptureId(path) + ".jpg")); } catch { }
            if (CaptureAddon is { } addon)
                await addon.CallAsync("copyImage", new JsonObject { ["path"] = path }, CancellationToken.None).ConfigureAwait(true);
            return new { ok = true, item = ItemFor(path) };
        }
        catch (Exception ex) { return CaptureFailure("Snip save", ex); }
    }

    private void ForgetCapture(string path, string relative)
    {
        lock (_captureGate)
        {
            CaptureIndex.Remove(relative);
            CaptureConfig.Favourites.RemoveAll(f => string.Equals(f, relative, StringComparison.OrdinalIgnoreCase));
        }
        try { DeletionGate.DeleteFile(Path.Combine(CaptureThumbs, CaptureId(path) + ".jpg")); } catch { }
        try { SaveCaptureIndex(); SaveCaptureSettings(); } catch { }
    }

    /// <summary>
    /// Capture settings' storage limit (in the Captures sheet): past N days, then the oldest until under N GB, to the Recycle Bin.
    /// Only captures Exo Capture saved (in the index) count or go: the folder may be Videos or OneDrive,
    /// full of the user's own media. Favorites are never removed.
    /// </summary>
    private void EnforceCaptureLimits()
    {
        var s = CaptureConfig;
        if (s.KeepDays <= 0 && s.KeepGb <= 0) return;
        try
        {
            var files = new List<(string Relative, DateTime CreatedUtc, long Length)>();
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in CaptureFiles())
            {
                var info = new FileInfo(path);
                if (!info.Exists || RelativeCapture(info.FullName) is not { } relative) continue;
                files.Add((relative, info.CreationTimeUtc, info.Length));
                paths[relative] = info.FullName;
            }
            HashSet<string> indexed, favourites;
            lock (_captureGate)
            {
                indexed = new HashSet<string>(CaptureIndex.Keys, StringComparer.OrdinalIgnoreCase);
                favourites = new HashSet<string>(s.Favourites, StringComparer.OrdinalIgnoreCase);
            }
            int removed = 0;
            foreach (var relative in CapturesPastLimit(files, indexed, favourites, s.KeepDays, s.KeepGb, DateTime.UtcNow))
            {
                if (!RecycleFile(paths[relative])) { AppLog.Debug("Capture limit: could not recycle a capture."); continue; }
                ForgetCapture(paths[relative], relative);
                removed++;
            }
            if (removed > 0) AppLog.Info($"Capture storage limit moved {removed} old capture(s) to the Recycle Bin.");
        }
        catch (Exception ex) { AppLog.Warn("Capture storage limit: " + ex.Message); }
    }

    /// <summary>The captures the limit removes, oldest first. Only indexed ones count toward the size or go.</summary>
    internal static List<string> CapturesPastLimit(IEnumerable<(string Relative, DateTime CreatedUtc, long Length)> files,
        IReadOnlySet<string> indexed, IReadOnlySet<string> favourites, int keepDays, int keepGb, DateTime nowUtc)
    {
        var ours = files.Where(f => indexed.Contains(f.Relative)).OrderBy(f => f.CreatedUtc).ToList();
        long total = ours.Sum(f => f.Length);
        var cutoff = nowUtc.AddDays(-keepDays);
        long limit = (long)keepGb * 1024 * 1024 * 1024;
        var remove = new List<string>();
        foreach (var f in ours)
        {
            if (favourites.Contains(f.Relative)) continue;
            bool old = keepDays > 0 && f.CreatedUtc < cutoff;
            bool over = keepGb > 0 && total > limit;
            if (!old && !over) continue;
            remove.Add(f.Relative);
            total -= f.Length;
        }
        return remove;
    }

    internal static bool RecycleFile(string path) => DeletionNative.SendToRecycleBin(path);

    // ------------------------------------------------------------------ the shell page

    /// <summary>
    /// The captures folder and the thumbnails, on their own hosts for the shell page (img and media in its CSP), served
    /// by LocalFileHost: they are the user's own clips and screenshots, and the page frames outside players. The snip
    /// editor reads a snip into its canvas with CORS, which LocalFileHost answers for the shell alone. Called when the
    /// shell's WebView starts; the folder is read again when it changes.
    /// </summary>
    internal void MapCaptureHosts(LocalFileHost files)
    {
        MapCaptureHosts();
        files.Serve(CaptureMediaHost, () => Volatile.Read(ref _captureRoot), CaptureTypes);
        files.Serve(CaptureThumbHost, () => CaptureThumbs, ThumbTypes);
    }

    private static readonly Dictionary<string, string> CaptureTypes = new(StringComparer.OrdinalIgnoreCase) { [".mp4"] = "video/mp4", [".png"] = "image/png" };
    private static readonly Dictionary<string, string> ThumbTypes = new(StringComparer.OrdinalIgnoreCase) { [".jpg"] = "image/jpeg" };

    /// <summary>The captures folder as the file host reads it (off the window's thread), set again when it changes.</summary>
    private void MapCaptureHosts()
    {
        try
        {
            var folder = CaptureFolder;
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(CaptureThumbs);
            Volatile.Write(ref _captureRoot, folder);
        }
        catch (Exception ex) { AppLog.Warn("Captures folder could not be served: " + ex.Message); }
    }
}
