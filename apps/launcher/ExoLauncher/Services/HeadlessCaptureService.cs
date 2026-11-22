using System.Diagnostics;
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
    private CaptureAddon? _addon;
    private MainWindow.CaptureSettings? _settings;
    private Dictionary<string, MainWindow.CaptureMeta>? _index;

    internal HeadlessCaptureService(AppServices services)
    {
        _services = services;
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

    private CaptureAddon? Addon
    {
        get
        {
            if (_addon is not null || !CaptureAddon.Present) return _addon;
            lock (_gate)
            {
                return _addon ??= new CaptureAddon();
            }
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

    internal object Status()
    {
        var s = Config;
        return new
        {
            ok = true,
            present = CaptureAddon.Present,
            folder = CaptureFolder,
            state = new
            {
                replay = s.ReplayMode != "off",
                recording = false,
                present = CaptureAddon.Present,
            },
            settings = new
            {
                quality = s.Quality,
                replaySeconds = s.ReplaySeconds,
                maxFps = s.MaxFps,
                replayMode = s.ReplayMode,
                cursor = s.Cursor,
                saveReplayKeys = s.SaveReplayKeys,
                recordKeys = s.RecordKeys,
                snipKeys = s.SnipKeys,
                autoShrink = s.AutoShrink,
                callVoices = s.CallVoices,
            },
        };
    }

    internal Task<object> UpdateSettingsAsync(JsonElement p)
    {
        lock (_gate)
        {
            var s = Config;
            if (p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("replayMode", out var rm) && rm.ValueKind == JsonValueKind.String && rm.GetString() is "games" or "always" or "off")
                    s.ReplayMode = rm.GetString()!;
                if (p.TryGetProperty("replaySeconds", out var rs) && rs.TryGetInt32(out var secs))
                    s.ReplaySeconds = Math.Clamp(secs, 15, 300);
                if (p.TryGetProperty("maxFps", out var fps) && fps.TryGetInt32(out var f))
                    s.MaxFps = f >= 120 ? 120 : 60;
                if (p.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qv))
                    s.Quality = Math.Clamp(qv, 10, 100);
                if (p.TryGetProperty("autoShrink", out var ash) && ash.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    s.AutoShrink = ash.GetBoolean();
                if (p.TryGetProperty("cursor", out var cur) && cur.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    s.Cursor = cur.GetBoolean();
            }
        }
        try { SaveSettings(); }
        catch { return Task.FromResult<object>(new { ok = false, message = "Could not save capture settings." }); }
        return Task.FromResult(Status());
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
        var path = FindById(id);
        if (path is null) return new { ok = false, message = "Capture not found." };
        if (Addon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
        var body = new JsonObject { ["path"] = path };
        if (!string.IsNullOrWhiteSpace(preset)) body["preset"] = preset;
        if (startMs is >= 0) body["startMs"] = startMs.Value;
        if (endMs is > 0) body["endMs"] = endMs.Value;
        try
        {
            await addon.CallAsync("edit", body, ct).ConfigureAwait(false);
            return new { ok = true };
        }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }

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
        var addon = Interlocked.Exchange(ref _addon, null);
        try { addon?.Dispose(); } catch { }
    }
}
