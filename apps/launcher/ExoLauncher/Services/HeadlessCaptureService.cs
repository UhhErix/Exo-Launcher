using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed class HeadlessCaptureService : IDisposable
{
    private static readonly string[] Extensions = [".mp4", ".png", ".gif", ".webm"];
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AppServices _services;
    private readonly Lock _gate = new();
    private readonly Lock _reelGate = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CaptureGameTarget _target = new();
    private CaptureAddon? _addon;
    private Action<string, object?>? _publishEvent;
    private JsonNode? _lastState;
    private MainWindow.CaptureSettings? _settings;
    private Dictionary<string, MainWindow.CaptureMeta>? _index;
    private long _configuredGeneration;
    private string? _gameId;
    private CancellationTokenSource? _reelCts;
    private List<string>? _reelWaiting;
    private bool _reelStoppedByGame;
    private bool _subscribed;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static readonly Lazy<ulong> PhysicalMemory = new(() =>
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    });

    internal HeadlessCaptureService(AppServices services)
    {
        _services = services;
    }

    internal void Attach(Action<string, object?> publishEvent)
    {
        Volatile.Write(ref _publishEvent, publishEvent);
    }

    internal void Detach()
    {
        Volatile.Write(ref _publishEvent, null);
    }

    internal static string DataDir => MainWindow.CaptureData;

    internal static string ThumbsDir => MainWindow.CaptureThumbs;

    private static string SettingsPath => MainWindow.CaptureSettingsPath;

    private static string IndexPath => MainWindow.CaptureIndexPath;

    private static string DefaultFolder => MainWindow.DefaultCaptureFolder;

    internal string CaptureFolder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Config.Folder ?? DefaultFolder));

    private MainWindow.CaptureSettings Config
    {
        get
        {
            lock (_gate)
            {
                if (_settings is not null) return _settings;
                try
                {
                    if (File.Exists(SettingsPath))
                        _settings = JsonSerializer.Deserialize<MainWindow.CaptureSettings>(File.ReadAllText(SettingsPath), JsonOpts);
                }
                catch { }
                return _settings ??= new MainWindow.CaptureSettings();
            }
        }
    }

    private Dictionary<string, MainWindow.CaptureMeta> Index
    {
        get
        {
            lock (_gate)
            {
                if (_index is not null) return _index;
                try
                {
                    if (File.Exists(IndexPath))
                        _index = JsonSerializer.Deserialize<Dictionary<string, MainWindow.CaptureMeta>>(File.ReadAllText(IndexPath), JsonOpts) is { } read
                            ? new Dictionary<string, MainWindow.CaptureMeta>(read, StringComparer.OrdinalIgnoreCase)
                            : null;
                }
                catch { }
                return _index ??= new Dictionary<string, MainWindow.CaptureMeta>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    internal CaptureAddon? StreamCapture => Addon;

    internal void Prewarm()
    {
        if (HostMode.IsDisposable || Addon is not { } addon) return;
        addon.Start();
    }

    internal void MicChanged()
    {
        if (_addon is not null) _ = ConfigureAsync();
    }

    private CaptureAddon? Addon
    {
        get
        {
            if (_addon is not null || !CaptureAddon.Present) return _addon;
            lock (_gate)
            {
                if (_addon is null)
                {
                    var addon = new CaptureAddon();
                    addon.Event += HandleAddonEvent;
                    _addon = addon;
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        DriverInstallSignal.Changed += OnDriverInstallChanged;
                        _services.Launcher.GameRunStateChanged += OnGameRunStateChanged;
                    }
                }
                return _addon;
            }
        }
    }

    private void OnDriverInstallChanged(DriverInstallSignal.Step step)
    {
        if (_addon is not { } addon) return;
        if (step.Phase is "installing" or "reloading") addon.Suspend();
        else if (step.Phase is "done" or "failed") addon.Resume();
    }

    private void OnGameRunStateChanged(string gameId, GameRunState state)
    {
        _ = Task.Run(async () =>
        {
            if (state.IsRunning) StopReelForGame();
            await SyncGameAsync().ConfigureAwait(false);
        });
    }

    private JsonObject CaptureParameters(bool supportsReplayBudget)
    {
        var folder = CaptureFolder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { AppLog.Warn("Captures folder: " + ex.Message); }
        lock (_gate)
        {
            var s = Config;
            var replay = CaptureReplayBudget.For(s.ReplaySeconds, PhysicalMemory.Value, supportsReplayBudget);
            var parameters = new JsonObject
            {
                ["quality"] = s.Quality,
                ["replaySeconds"] = replay.Seconds,
                ["maxFps"] = s.MaxFps,
                ["replayMode"] = s.ReplayMode,
                ["cursor"] = s.Cursor,
                ["folder"] = folder,
                ["thumbs"] = ThumbsDir,
                ["printScreen"] = s.PrintScreen,
                ["callVoices"] = s.CallVoices,
                ["mic"] = _services.Settings.Shared.DiscordInputDevice ?? "",
                ["cleanClips"] = DiscordClient.Voice.RecordedVoice.CanCleanLater,
                ["font"] = MainWindow.CaptureFont(),
                ["hotkeys"] = new JsonObject { ["saveReplay"] = s.SaveReplayKeys, ["record"] = s.RecordKeys, ["snip"] = s.SnipKeys },
            };
            if (replay.MaxBytes is long bytes) parameters["replayMaxBytes"] = bytes;
            return parameters;
        }
    }

    private async Task ConfigureAsync(bool retry = false)
    {
        if (HostMode.IsDisposable || Addon is not { } addon || _lifetimeCts.IsCancellationRequested) return;
        var generation = addon.ReadyGeneration;
        if (generation is null) { addon.Start(); return; }
        var attemptAgain = false;
        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (addon.ReadyGeneration != generation) return;
            await addon.CallAsync("configure", CaptureParameters(addon.SupportsReplayBudget), _lifetimeCts.Token, generation, applyState: true).ConfigureAwait(false);
            if (addon.ReadyGeneration != generation) return;
            Volatile.Write(ref _configuredGeneration, generation.Value);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Warn("Capture configure: " + ex.Message);
            attemptAgain = retry && addon.ReadyGeneration == generation;
        }
        finally { _syncGate.Release(); }
        if (attemptAgain)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (addon.ReadyGeneration == generation && !_lifetimeCts.IsCancellationRequested)
                await ConfigureAsync().ConfigureAwait(false);
        }
        else await SyncGameAsync().ConfigureAwait(false);
    }

    private async Task SyncGameAsync(bool retry = true)
    {
        if (_addon is not { } addon || _lifetimeCts.IsCancellationRequested) return;
        var attemptAgain = false;
        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var running = MainWindow.RunningLibraryGames();
            var before = _gameId;
            var id = before is not null && running.ContainsKey(before) ? before : running.Keys.FirstOrDefault();
            var game = id is null ? null : _services.Library.Find(id);
            if (game is null || string.IsNullOrWhiteSpace(game.Path)) id = null;
            _gameId = id;
            if (id is null && before is not null) ResumeReel();
            else if (id is not null && !string.Equals(id, before, StringComparison.OrdinalIgnoreCase))
                AppLog.Info("Capture: following " + game!.Title + ".");

            if (addon.ReadyGeneration is not long generation || Volatile.Read(ref _configuredGeneration) != generation
                || !_target.Begin(generation, id)) return;
            var target = id is null ? new JsonObject() : new JsonObject
            {
                ["id"] = id,
                ["title"] = game!.Title,
                ["folder"] = game.Path,
                ["hook"] = AntiCheatCheck.Allows(game, AntiCheatCheck.Cached(game), AntiCheatCheck.Change.InjectedHook),
            };
            await addon.CallAsync("game", target, _lifetimeCts.Token, generation).ConfigureAwait(false);
            _target.Acknowledge(generation, id);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Debug("Capture: game target not applied: " + ex.GetType().Name);
            attemptAgain = retry;
        }
        finally { _syncGate.Release(); }
        if (attemptAgain)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await SyncGameAsync(retry: false).ConfigureAwait(false);
        }
    }

    internal void HandleAddonEvent(string name, JsonNode? data)
    {
        var publish = Volatile.Read(ref _publishEvent);
        switch (name)
        {
            case "ready":
                Volatile.Write(ref _lastState, data);
                publish?.Invoke("capture.state", StateDto());
                _ = ConfigureAsync(retry: true);
                break;
            case "state":
                Volatile.Write(ref _lastState, data);
                publish?.Invoke("capture.state", StateDto());
                break;
            case "exited":
            case "failed":
                Volatile.Write(ref _lastState, null);
                publish?.Invoke("capture.state", StateDto());
                break;
            case "saved":
                if (data?["path"]?.GetValue<string>() is not { } path) return;
                var relative = RelativePath(path);
                if (relative is null) return;
                var kind = data["kind"]?.GetValue<string>();
                lock (_gate)
                {
                    var game = data["game"]?.GetValue<string>();
                    Index[relative] = new MainWindow.CaptureMeta
                    {
                        GameId = string.IsNullOrEmpty(game) ? null : game,
                        DurationMs = data["durationMs"]?.GetValue<double>() ?? 0,
                        Derived = kind is "trim" or "export" or "concat",
                    };
                }
                SaveIndex();
                if (MapItem(path) is { } item)
                    publish?.Invoke("capture.saved", new { item, edit = string.Equals(kind, "snip", StringComparison.OrdinalIgnoreCase) });
                break;
            case "concat":
                if (data?["percent"]?.GetValue<double>() is { } pct) PostReel("working", pct);
                break;
        }
    }

    private object StateDto()
    {
        var s = Config;
        var state = Volatile.Read(ref _lastState);
        return new
        {
            present = CaptureAddon.Present,
            running = state is not null,
            replay = s.ReplayMode != "off",
            buffering = state?["buffering"]?.GetValue<bool>() ?? false,
            recording = state?["recording"]?.GetValue<bool>() ?? false,
            recordingMs = state?["recordingMs"]?.GetValue<double>() ?? 0,
            codec = state?["codec"]?.GetValue<string>(),
            encoder = state?["encoder"]?.GetValue<string>(),
            width = state?["width"]?.GetValue<double>(),
            height = state?["height"]?.GetValue<double>(),
            fps = state?["fps"]?.GetValue<double>(),
            hdr = state?["hdr"]?.GetValue<bool>() ?? false,
            error = state?["error"]?.GetValue<string>(),
        };
    }

    private object SettingsDto()
    {
        lock (_gate)
        {
            var s = Config;
            return new
            {
                quality = s.Quality,
                replaySeconds = s.ReplaySeconds,
                maxFps = s.MaxFps,
                replayMode = s.ReplayMode,
                cursor = s.Cursor,
                printScreen = s.PrintScreen,
                afterSnip = CaptureShare.NormalizeAfterSnip(s.AfterSnip),
                saveReplayKeys = s.SaveReplayKeys,
                recordKeys = s.RecordKeys,
                snipKeys = s.SnipKeys,
                keepGb = s.KeepGb,
                keepDays = s.KeepDays,
                autoShrink = s.AutoShrink,
                callVoices = s.CallVoices,
                folder = CaptureFolder,
            };
        }
    }

    private void SaveSettings()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, JsonOpts));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
    }

    private void SaveIndex()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(DataDir);
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Index, JsonOpts));
                File.Move(tmp, IndexPath, overwrite: true);
            }
        }
        catch { }
    }

    internal object Status() => new
    {
        ok = true,
        present = CaptureAddon.Present,
        folder = CaptureFolder,
        state = StateDto(),
        settings = SettingsDto(),
    };

    internal async Task<object> UpdateSettingsAsync(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return new { ok = false, message = "Nothing to change." };
        string? error = null;
        lock (_gate)
        {
            var s = Config;
            foreach (var prop in p.EnumerateObject())
            {
                var v = prop.Value;
                int? n = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                switch (prop.Name)
                {
                    case "quality" when n is { } quality: s.Quality = Math.Clamp(quality, 0, 100); break;
                    case "replaySeconds" when n is { } secs: s.ReplaySeconds = Math.Clamp(secs, 15, 600); break;
                    case "maxFps" when n is { } fps: s.MaxFps = fps >= 120 ? 120 : 60; break;
                    case "replayMode" when v.ValueKind == JsonValueKind.String && v.GetString() is "games" or "always" or "off": s.ReplayMode = v.GetString()!; break;
                    case "cursor" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.Cursor = v.GetBoolean(); break;
                    case "printScreen" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.PrintScreen = v.GetBoolean(); break;
                    case "afterSnip" when v.ValueKind == JsonValueKind.String: s.AfterSnip = CaptureShare.NormalizeAfterSnip(v.GetString()); break;
                    case "saveReplayKeys" or "recordKeys" or "snipKeys" when v.ValueKind == JsonValueKind.String:
                        var keys = v.GetString()!.Trim();
                        if (MainWindow.CaptureHotkeyProblem(keys) is { } problem) { error = problem; break; }
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
                        if (!PickedPaths.Consume(folder)) error = "Pick the folder with Change.";
                        else if (MainWindow.CaptureFolderProblem(folder) is { } folderProblem) error = folderProblem;
                        else s.Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                        break;
                    case "folder" when v.ValueKind == JsonValueKind.Null: s.Folder = null; break;
                }
            }
        }
        try { SaveSettings(); }
        catch { return new { ok = false, message = "Could not save capture settings." }; }
        await ConfigureAsync().ConfigureAwait(false);
        return new { ok = error is null, message = error, present = CaptureAddon.Present, folder = CaptureFolder, state = StateDto(), settings = SettingsDto() };
    }

    internal async Task<object> ActAsync(string? action, CancellationToken ct)
    {
        if (action == "openFolder")
        {
            try
            {
                Directory.CreateDirectory(CaptureFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + CaptureFolder + "\"") { UseShellExecute = true });
                return new { ok = true };
            }
            catch { return new { ok = false, message = "Could not open captures folder." }; }
        }
        if (Addon is not { } addon)
            return new { ok = false, message = "Exo Capture is not installed." };
        var method = action switch
        {
            "saveReplay" => "replay.save",
            "record" => "record.toggle",
            "snip" => "snip",
            "screenshot" => "screenshot",
            _ => null,
        };
        if (method is null) return new { ok = false, message = "Unknown capture action." };
        try
        {
            if (method == "snip") addon.AllowForeground();
            await addon.CallAsync(method, new JsonObject(), ct, applyState: true).ConfigureAwait(false);
            return new { ok = true };
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    internal object List(string? gameId, int? limit)
    {
        var items = EnumerateFiles()
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => MapItem(f.FullName))
            .Where(i => i is not null);
        if (!string.IsNullOrEmpty(gameId))
        {
            var title = _services.Library.Find(gameId)?.Title;
            items = items.Where(i =>
                string.Equals(i!.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                || (title is not null && string.Equals(i.Game, SafeName(title), StringComparison.OrdinalIgnoreCase)));
        }
        if (limit is > 0) items = items.Take(limit.Value);
        return new { ok = true, items = items.ToList() };
    }

    internal async Task<object> ItemAsync(string? id, string? action)
    {
        var path = FindById(id);
        if (path is null) return new { ok = false, message = "Capture not found." };
        var rel = RelativePath(path);
        if (rel is null) return new { ok = false, message = "Invalid capture path." };
        switch (action)
        {
            case "favourite":
            case "unfavourite":
                lock (_gate)
                {
                    Config.Favourites.RemoveAll(f => string.Equals(f, rel, StringComparison.OrdinalIgnoreCase));
                    if (action == "favourite") Config.Favourites.Add(rel);
                }
                SaveSettings();
                return new { ok = true, item = MapItem(path) };
            case "delete":
                if (!DeletionNative.SendToRecycleBin(path))
                    return new { ok = false, message = "Windows could not move that clip to Recycle Bin." };
                lock (_gate)
                {
                    Index.Remove(rel);
                    Config.Favourites.RemoveAll(f => string.Equals(f, rel, StringComparison.OrdinalIgnoreCase));
                }
                try { DeletionGate.DeleteFile(Path.Combine(ThumbsDir, CaptureId(Path.GetFullPath(path)) + ".jpg")); } catch { }
                SaveIndex();
                SaveSettings();
                return new { ok = true };
            case "reveal":
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return new { ok = true };
            case "copy":
                if (Addon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
                var method = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "copyImage" : "copy";
                await addon.CallAsync(method, new JsonObject { ["path"] = path }, CancellationToken.None).ConfigureAwait(false);
                return new { ok = true };
            default:
                return new { ok = false, message = "Unknown action." };
        }
    }

    internal async Task<object> EditAsync(string? id, string? preset, double? startMs, double? endMs, CancellationToken ct)
    {
        if (Addon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
        var path = FindById(id);
        if (path is null || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That clip is gone." };
        var parameters = new JsonObject { ["path"] = path, ["startMs"] = Math.Max(0, startMs ?? 0), ["endMs"] = Math.Max(0, endMs ?? 0) };
        try
        {
            JsonNode? result;
            if (preset is null or "trim") result = await addon.CallAsync("trim", parameters, ct).ConfigureAwait(false);
            else if (preset is "discord" or "youtube" or "vertical" or "gif" or "webm")
            {
                parameters["preset"] = preset;
                result = await addon.CallAsync("export", parameters, ct).ConfigureAwait(false);
            }
            else return new { ok = false, message = "Unknown export." };
            var made = result?["path"]?.GetValue<string>();
            return new { ok = true, item = made is null ? null : MapItem(made) };
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

    internal async Task<object> TextAsync(string? id, double? x, double? y, double? width, double? height)
    {
        var path = FindById(id);
        if (path is null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That snip is gone." };
        try
        {
            var (text, error) = await CaptureText.ReadAsync(path, x, y, width, height).ConfigureAwait(false);
            return new { ok = text is not null, text, message = error };
        }
        catch (Exception ex) { AppLog.Warn("Snip text: " + ex.Message); return new { ok = false, message = "Exo could not read text from that snip." }; }
    }

    internal async Task<object> SaveImageAsync(string? id, string? base64)
    {
        if (HostMode.IsDisposable) return new { ok = false, message = (string?)HostMode.Refusal };
        var path = FindById(id);
        if (path is null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That snip is gone." };
        if (string.IsNullOrEmpty(base64) || base64.Length > 90_000_000) return new { ok = false, message = "Nothing to save." };
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch { return new { ok = false, message = "Nothing to save." }; }
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P') return new { ok = false, message = "Nothing to save." };
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            try { DeletionGate.DeleteFile(Path.Combine(ThumbsDir, CaptureId(Path.GetFullPath(path)) + ".jpg")); } catch { }
            if (Addon is { } addon)
                await addon.CallAsync("copyImage", new JsonObject { ["path"] = path }, CancellationToken.None).ConfigureAwait(false);
            return new { ok = true, item = MapItem(path) };
        }
        catch (Exception ex) { AppLog.Warn("Snip save: " + ex.Message); return new { ok = false, message = "Exo could not save that snip." }; }
    }

    internal async Task<MainWindow.CaptureUpload> UploadAsync(string? id, double? startMs, double? endMs, CancellationToken ct)
    {
        var path = FindById(id);
        if (path is null || CaptureShare.TypeOf(path) is not { } type) return new(null, "That capture is gone.", null);
        try
        {
            var video = type == "video/mp4";
            var trimmed = CaptureShare.Trimmed(startMs, endMs);
            MainWindow.CaptureItem? made = null;
            switch (CaptureShare.PlanFor(video, new FileInfo(path).Length, trimmed))
            {
                case CaptureShare.Plan.TooBig:
                    return new(null, CaptureShare.TooBigPicture, null);
                case CaptureShare.Plan.DiscordCopy:
                    if (Addon is not { } addon) return new(null, "Exo Capture is not installed.", null);
                    var parameters = new JsonObject
                    {
                        ["path"] = path,
                        ["startMs"] = Math.Max(0, startMs ?? 0),
                        ["endMs"] = Math.Max(0, endMs ?? 0),
                        ["preset"] = "discord",
                    };
                    var result = await addon.CallAsync("export", parameters, ct).ConfigureAwait(false);
                    if (result?["path"]?.GetValue<string>() is not { } copy || !File.Exists(copy)) return new(null, "Exo Capture didn't make the Discord copy.", null);
                    path = copy;
                    made = MapItem(copy);
                    if (new FileInfo(path).Length > CaptureShare.DiscordLimit) return new(null, CaptureShare.TooBigCopy, made);
                    break;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return new(new DiscordClient.DiscordRest.Upload(Path.GetFileName(path), type, bytes), null, made);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) { return new(null, ex.Message, null); }
        catch (Exception ex)
        {
            AppLog.Warn("Capture send: " + ex.GetType().Name + ": " + ex.Message);
            return new(null, "Exo could not read that capture. The details are in the log.", null);
        }
    }

    internal object ReelStart(JsonElement p)
    {
        if (Addon is null) return new { ok = false, message = "Exo Capture is not installed." };
        var ids = new List<string>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var id in list.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: 16 } text && !ids.Contains(text, StringComparer.Ordinal) && ids.Count <= CaptureReel.MostClips)
                    ids.Add(text);
        if (ids.Count < 2) return new { ok = false, message = "Pick at least two clips." };
        var wait = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("wait", out var asked) && asked.ValueKind == JsonValueKind.True;
        lock (_reelGate)
        {
            var gate = CaptureReel.Admit(_reelCts is not null || _reelWaiting is not null, ReelGameRunning(), ReelRecording(), wait);
            switch (gate.Go)
            {
                case CaptureReel.Go.Refuse:
                    return new { ok = false, message = gate.Message, wait = gate.Waits };
                case CaptureReel.Go.Wait:
                    _reelWaiting = ids;
                    PostReel("waiting");
                    return new { ok = true };
            }
            BeginReel(ids);
        }
        return new { ok = true };
    }

    internal object ReelCancel()
    {
        lock (_reelGate)
        {
            if (_reelWaiting is not null)
            {
                _reelWaiting = null;
                PostReel("stopped");
            }
            else _reelCts?.Cancel();
        }
        return new { ok = true };
    }

    private bool ReelGameRunning() => _gameId is not null || MainWindow.RunningLibraryGames().Count > 0;

    private bool ReelRecording() => Volatile.Read(ref _lastState)?["recording"]?.GetValue<bool>() == true;

    private void StopReelForGame()
    {
        lock (_reelGate)
        {
            if (_reelCts is null) return;
            _reelStoppedByGame = true;
            _reelCts.Cancel();
        }
    }

    private void ResumeReel()
    {
        if (Volatile.Read(ref _reelWaiting) is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _lifetimeCts.Token).ConfigureAwait(false);
                lock (_reelGate)
                {
                    if (_reelWaiting is not { } ids) return;
                    var gate = CaptureReel.Admit(_reelCts is not null, ReelGameRunning(), ReelRecording(), wait: true);
                    if (gate.Go == CaptureReel.Go.Wait) return;
                    _reelWaiting = null;
                    if (gate.Go == CaptureReel.Go.Refuse) PostReel("failed", message: gate.Message);
                    else BeginReel(ids);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void BeginReel(List<string> ids)
    {
        var cts = _reelCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _reelStoppedByGame = false;
        PostReel("working");
        _ = Task.Run(async () =>
        {
            CaptureReel.Outcome outcome;
            try { outcome = await RunReelAsync(ids, cts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLog.Warn("Capture reel: " + ex.GetType().Name + ": " + ex.Message);
                outcome = new CaptureReel.Outcome(false, null, "Exo could not finish that. The details are in the log.");
            }
            bool byGame;
            lock (_reelGate)
            {
                _reelCts = null;
                byGame = _reelStoppedByGame;
            }
            cts.Dispose();
            if (outcome.Detail is not null) AppLog.Warn("Capture reel: " + outcome.Detail);
            if (outcome.Ok && outcome.Path is { } made) PostReel("done", item: MapItem(made));
            else if (outcome.Cancelled && byGame) PostReel("failed", message: "A game started, so Exo stopped the reel.", wait: true);
            else if (outcome.Cancelled) PostReel("stopped");
            else PostReel("failed", message: outcome.Message);
        });
    }

    private async Task<CaptureReel.Outcome> RunReelAsync(List<string> ids, CancellationToken ct)
    {
        if (Addon is not { } addon) return new CaptureReel.Outcome(false, null, "Exo Capture is not installed.");
        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        var clips = new List<CaptureReel.Clip>();
        foreach (var path in EnumerateFiles())
        {
            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(path);
            if (!wanted.Contains(CaptureId(full)) || RelativePath(full) is not { } relative) continue;
            var info = new FileInfo(full);
            MainWindow.CaptureMeta? meta;
            lock (_gate) Index.TryGetValue(relative, out meta);
            clips.Add(new CaptureReel.Clip(full, info.CreationTimeUtc, info.Length, relative.Split(Path.DirectorySeparatorChar)[0], meta?.GameId));
        }
        if (clips.Count != ids.Count) return new CaptureReel.Outcome(false, null, "A clip is gone.");
        return await CaptureReel.MakeAsync(clips, CaptureFolder, FreeBytes(CaptureFolder), new MainWindow.AddonReelEngine(addon), ct).ConfigureAwait(false);
    }

    private static long FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    private void PostReel(string state, double percent = 0, string? message = null, bool wait = false, object? item = null) =>
        Volatile.Read(ref _publishEvent)?.Invoke("capture.reel", new { state, percent, message, wait, item });

    private IEnumerable<string> EnumerateFiles()
    {
        var root = CaptureFolder;
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(root).ToList(); } catch { yield break; }
        foreach (var folder in folders)
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private string? FindById(string? id) =>
        string.IsNullOrEmpty(id) || id.Length != 16
            ? null
            : EnumerateFiles().FirstOrDefault(f => CaptureId(Path.GetFullPath(f)) == id);

    private string? RelativePath(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = CaptureFolder + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : null;
    }

    private MainWindow.CaptureItem? MapItem(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var rel = RelativePath(info.FullName);
            if (rel is null) return null;
            var id = CaptureId(info.FullName);
            MainWindow.CaptureMeta? meta;
            bool fav;
            lock (_gate)
            {
                Index.TryGetValue(rel, out meta);
                fav = Config.Favourites.Contains(rel, StringComparer.OrdinalIgnoreCase);
            }
            var folder = rel.Split(Path.DirectorySeparatorChar)[0];
            var isImage = info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || info.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            var url = LocalFileHost.Url(MainWindow.CaptureMediaHost, string.Join('/', rel.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)));
            var thumb = File.Exists(Path.Combine(ThumbsDir, id + ".jpg"))
                ? LocalFileHost.Url(MainWindow.CaptureThumbHost, id + ".jpg?v=") + info.LastWriteTimeUtc.Ticks.ToString("x")
                : isImage ? url : null;
            return new MainWindow.CaptureItem(
                id,
                isImage ? "image" : "video",
                folder,
                meta?.GameId,
                Path.GetFileNameWithoutExtension(info.Name),
                info.CreationTimeUtc.ToString("o"),
                info.Length,
                meta?.DurationMs > 0 ? meta.DurationMs : null,
                fav,
                url + "?v=" + info.LastWriteTimeUtc.Ticks.ToString("x"),
                thumb,
                meta?.Derived == true);
        }
        catch { return null; }
    }

    private static string CaptureId(string fullPath)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(fullPath))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");
    }

    private static string SafeName(string title)
    {
        var chars = title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.').Trim();
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        return name.Length == 0 ? "Capture" : name;
    }

    public void Dispose()
    {
        Detach();
        try { _lifetimeCts.Cancel(); } catch { }
        lock (_gate)
        {
            if (_subscribed)
            {
                _subscribed = false;
                DriverInstallSignal.Changed -= OnDriverInstallChanged;
                _services.Launcher.GameRunStateChanged -= OnGameRunStateChanged;
            }
        }
        var addon = Interlocked.Exchange(ref _addon, null);
        if (addon is not null)
        {
            addon.Event -= HandleAddonEvent;
            try { addon.Dispose(); } catch { }
        }
        _lifetimeCts.Dispose();
        _syncGate.Dispose();
    }
}
