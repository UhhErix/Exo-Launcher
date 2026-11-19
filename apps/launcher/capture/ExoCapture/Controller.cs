using System.Text.Json.Nodes;
using ExoCapture.Audio;
using ExoCapture.Capture;
using ExoCapture.Edit;
using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using ExoCapture.Native;
using ExoCapture.Live;
using ExoCapture.Ui;

namespace ExoCapture;

/// <summary>What Exo sets with <c>configure</c> (CONTRACT.md has each field).</summary>
internal sealed record Config
{
    public int Quality { get; init; } = 50;
    public int ReplaySeconds { get; init; } = 60;
    public long? ReplayMaxBytes { get; init; }
    public int MaxFps { get; init; } = 60;
    public string ReplayMode { get; init; } = "games";
    public bool Cursor { get; init; } = true;
    public string Folder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Exo Captures");
    public string Thumbs { get; init; } = "";
    public bool PrintScreen { get; init; }
    public bool CallVoices { get; init; }
    public string Mic { get; init; } = "";
    public bool CleanClips { get; init; }
    public string Font { get; init; } = "";
    public Dictionary<string, string> Hotkeys { get; init; } = new() { ["saveReplay"] = "Ctrl+Alt+F10", ["record"] = "Ctrl+Alt+F9", ["snip"] = "Ctrl+Alt+F8", ["screenshot"] = "Ctrl+Alt+F7" };

    internal static Config From(JsonNode? p, Config old)
    {
        var keys = new Dictionary<string, string>(old.Hotkeys);
        if (p?["hotkeys"] is JsonObject hk)
            foreach (var name in new[] { "saveReplay", "record", "snip", "screenshot" })
                if (hk.Str(name) is { } k) keys[name] = k;
        return old with
        {
            Quality = Math.Clamp(p.Int("quality", old.Quality), 0, 100),
            ReplaySeconds = Math.Clamp(p.Int("replaySeconds", old.ReplaySeconds), 5, 600),
            ReplayMaxBytes = p.Num("replayMaxBytes") is { } b ? (long)Math.Clamp(b, 64L << 20, 8L << 30) : old.ReplayMaxBytes,
            MaxFps = p.Int("maxFps", old.MaxFps) >= 120 ? 120 : 60,
            ReplayMode = p.Str("replayMode") is "games" or "always" or "off" ? p.Str("replayMode")! : old.ReplayMode,
            Cursor = p.Bool("cursor", old.Cursor),
            Folder = p.Str("folder") is { Length: > 0 } f ? f : old.Folder,
            Thumbs = p.Str("thumbs") ?? old.Thumbs,
            PrintScreen = p.Bool("printScreen", old.PrintScreen),
            CallVoices = p.Bool("callVoices", old.CallVoices),
            Mic = p.Str("mic") ?? old.Mic,
            CleanClips = p.Bool("cleanClips", old.CleanClips),
            Font = p.Str("font") ?? old.Font,
            Hotkeys = keys,
        };
    }
}

/// <summary>The game Exo says is running: its library id, title, install folder, and whether OBS's hook may go in.</summary>
internal sealed record GameTarget(string Id, string Title, string Folder, bool Hook);

/// <summary>
/// Exo Capture's brain: Exo's requests in, the capture session, instant replay, the recording, snips, Go Live and the
/// edits out. A supervisor every two seconds keeps the session matched to what is wanted (a game, always, or off),
/// follows the game's window and restarts what stopped.
/// </summary>
internal sealed partial class Controller : IDisposable
{
    private readonly Lock _gate = new();
    private Config _config = new();
    private GameTarget? _game;
    private nint _gameWindow;
    private CaptureSession? _session;
    private string? _sessionKey;
    private readonly ReplayBuffer _replay = new(60, 512L << 20);
    private Recorder? _recorder;
    private string? _error;
    private readonly UiThread _ui;
    private GpuDevice? _gpu;
    private readonly Dictionary<string, bool> _hotkeyOk = [];
    private bool _printScreenOk = true;
    private readonly Timer _supervisor;
    private bool _configured;
    private string? _lastState;
    private long _replayRestartAt;
    private readonly string _obs = Path.Combine(AppContext.BaseDirectory, "obs");
    private bool _hookFailed;
    private readonly StreamTable<GoLive> _streams = new();
    private long _nextStream;
    private volatile bool _closing;
    private CancellationTokenSource? _shrinkCts, _concatCts;
    private VoiceFeed? _feed;

    private const int KeySaveReplay = 1, KeyRecord = 2, KeySnip = 3, KeyPrintScreen = 4, KeyScreenshot = 5;

    internal Controller()
    {
        _ui = new UiThread();
        _ui.Hotkey += OnHotkey;
        _supervisor = new Timer(_ => Supervise(), null, 2000, 2000);
    }

    // ---------------------------------------------------------------- the line

    internal JsonNode? Handle(string method, JsonNode? p, long id)
    {
        switch (method)
        {
            case "configure": return Configure(p);
            case "game": return SetGame(p);
            case "replay.save": return SaveReplay();
            case "record.toggle": return ToggleRecording();
            case "snip": return Snip();
            case "screenshot": return Screenshot();
            case "trim": return Trim(p);
            case "export": return Export(p);
            case "shrink": return Shrink(p);
            case "shrink.cancel": _shrinkCts?.Cancel(); return true;
            case "concat.facts": return new JsonArray([.. (p?["paths"] as JsonArray ?? []).Select(x => (JsonNode?)Clips.Facts(Inside(x?.GetValue<string>())))]);
            case "concat": return Concat(p);
            case "concat.cancel": _concatCts?.Cancel(); return true;
            case "probe": return Clips.Facts(Inside(p.Str("path")));
            case "copy": Imaging.CopyFile(Inside(p.Str("path"))); return true;
            case "copyImage": Imaging.CopyImage(Inside(p.Str("path"))); return true;
            case "stream.start": return StartStream(p);
            case "stream.stop": return StopStream(p);
            case "stream.thumbs": return GoLive.Thumbs(GpuDevice.ForMonitor, p?["items"] as JsonArray ?? [], p.Int("width", 320));
            case "stream.preview": return StreamFor(p)?.Preview(p.Int("width", 480)) is { } jpeg ? JsonValue.Create(jpeg) : JsonValue.Create("");
            case "stream.bitrate": StreamFor(p)?.SetBitrate(Math.Clamp(p.Int("bitrate", 6_000_000), 300_000, 80_000_000)); return true;
            case "stream.keyframe": StreamFor(p)?.Keyframe(); return true;
            case "voice.feed": return StartFeed(p);
            case "clip.voiced": return ClipVoiced(p);
            default: throw new CaptureException("Unknown request.");
        }
    }

    /// <summary>A path Exo named, which must be in the captures folder (Exo only ever names its own captures).</summary>
    private string Inside(string? path)
    {
        if (string.IsNullOrEmpty(path)) throw new CaptureException("That file is not in the captures folder.");
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_config.Folder)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new CaptureException("That file is not in the captures folder.");
        return full;
    }

    // ---------------------------------------------------------------- state

    internal JsonObject State()
    {
        lock (_gate)
        {
            var s = _session;
            var state = new JsonObject
            {
                ["buffering"] = s is not null && ReplayWanted(),
                ["recording"] = _recorder is not null,
                ["callVoices"] = _config.CallVoices,
                ["cleansClips"] = true,
                ["recordingMs"] = _recorder?.ElapsedMs ?? 0,
                ["bufferedMs"] = s is not null && ReplayWanted() ? _replay.RetainedMs : 0,
                ["replayRetainedMs"] = s is not null && ReplayWanted() ? _replay.RetainedMs : 0,
                ["bufferBytes"] = _replay.Bytes,
                ["codec"] = s is null ? null : Name(s.Codec),
                ["encoder"] = s?.EncoderName,
                ["av1"] = Available(Codec.Av1),
                ["hevc"] = Available(Codec.Hevc),
                ["hotkeys"] = new JsonObject
                {
                    ["saveReplay"] = Hk("saveReplay"),
                    ["record"] = Hk("record"),
                    ["snip"] = Hk("snip"),
                    ["screenshot"] = Hk("screenshot"),
                },
                ["printScreenOk"] = _printScreenOk,
            };
            if (s is not null)
            {
                state["width"] = s.Width;
                state["height"] = s.Height;
                state["fps"] = s.Fps;
                state["hdr"] = s.Hdr;
                state["target"] = s.SourceKind;
            }
            if (_error is not null) state["error"] = _error;
            return state;
        }
    }

    private JsonObject Hk(string name) => new() { ["keys"] = _config.Hotkeys.GetValueOrDefault(name, ""), ["ok"] = _hotkeyOk.GetValueOrDefault(name, true) };

    internal static string Name(Codec c) => c switch { Codec.H264 => "H.264", Codec.Hevc => "HEVC", Codec.Av1 => "AV1", _ => "VP9" };

    private readonly Dictionary<Codec, bool> _available = [];
    private bool Available(Codec codec)
    {
        if (_available.TryGetValue(codec, out var known)) return known;
        var gpu = _gpu ?? GpuDevice.Shared();
        var found = VideoEncoder.Playable(codec) && VideoEncoder.Find(codec, gpu.Luid, true) is { Count: > 0 };
        return _available[codec] = found;
    }

    /// <summary>Sends state when it changed (or always, when asked).</summary>
    private void Publish(bool always = false)
    {
        var state = State();
        var text = state.ToJsonString();
        if (!always && text == _lastState) return;
        _lastState = text;
        Host.Event("state", state);
    }

    internal JsonObject Ready()
    {
        var state = State();
        state["replayMemoryBudget"] = true;
        state["screenshot"] = true;
        state["exports"] = new JsonArray("discord", "youtube", "vertical", "gif", "webm");
        state["hdrCopy"] = true;
        return state;
    }

    // ---------------------------------------------------------------- configure, game

    private JsonNode Configure(JsonNode? p)
    {
        Config old, next;
        lock (_gate)
        {
            old = _config;
            next = _config = Config.From(p, _config);
            _configured = true;
            _error = null;
            _replay.Set(next.ReplaySeconds, next.ReplayMaxBytes ?? 512L << 20);
        }
        _ui.UseFont(next.Font);
        ApplyHotkeys(next);
        try { Directory.CreateDirectory(next.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error = "The captures folder can't be written."; }
        if (old.Folder != next.Folder || !_recovered) Recover(next.Folder);
        lock (_gate)
            if (_session is not null && (old.MaxFps != next.MaxFps || old.Quality != next.Quality || old.Cursor != next.Cursor)) StopSession();
        if (_session?.Sound is { } sound) ApplySound(sound, _game);
        Supervise();
        return State();
    }

    private void ApplyHotkeys(Config c)
    {
        var map = new (int Id, string Name)[] { (KeySaveReplay, "saveReplay"), (KeyRecord, "record"), (KeySnip, "snip"), (KeyScreenshot, "screenshot") };
        foreach (var (id, name) in map)
        {
            var keys = c.Hotkeys.GetValueOrDefault(name);
            var parsed = Hotkey.Parse(keys);
            _hotkeyOk[name] = string.IsNullOrEmpty(keys) || parsed is not null && _ui.SetHotkey(id, parsed);
        }
        _printScreenOk = !c.PrintScreen || _ui.SetHotkey(KeyPrintScreen, new Hotkey(0, Win32.VK_SNAPSHOT));
        if (!c.PrintScreen) _ui.SetHotkey(KeyPrintScreen, null);
    }

    private JsonNode SetGame(JsonNode? p)
    {
        var id = p.Str("id");
        var next = string.IsNullOrEmpty(id) ? null : new GameTarget(id, p.Str("title") ?? "Game", p.Str("folder") ?? "", p.Bool("hook", false));
        lock (_gate)
        {
            if (next == _game) return State();
            _game = next;
            _gameWindow = 0;
            _hookFailed = false;
            if (_session is not null && _recorder is null) StopSession();
            if (next is null) _replay.Clear();
        }
        Supervise();
        return State();
    }

    // ---------------------------------------------------------------- the session

    private bool ReplayWanted() => _config.ReplayMode == "always" || _config.ReplayMode == "games" && _game is not null;

    private void Supervise()
    {
        if (_closing || !_configured) return;
        try
        {
            lock (_gate)
            {
                if (_session is { Ended: { } ended })
                {
                    Host.Log("Capture stopped: " + ended);
                    var recording = _recorder;
                    StopSession();
                    if (recording is not null) FinishRecording(recording, "the capture stopped");
                    _replayRestartAt = Environment.TickCount64 + 2000;
                }
                var wanted = ReplayWanted() || _recorder is not null;
                if (_game is { } g && (_gameWindow == 0 || !Win32.IsWindow(_gameWindow))) _gameWindow = GameWindow.Find(g.Folder);
                var key = _game is null ? "desktop" : _gameWindow == 0 ? null : "game:" + _gameWindow;
                if (_session is not null && (!wanted || key != _sessionKey && _recorder is null)) StopSession();
                if (wanted && _session is null && key is not null && Environment.TickCount64 >= _replayRestartAt) StartSession(key);
            }
            if (_recorder is { } r && r.Failed is { } why) FinishRecording(r, why);
        }
        catch (Exception ex) { Host.Log("Supervisor: " + ex.Message); }
        Publish();
    }

    /// <summary>Under the gate.</summary>
    private void StartSession(string key)
    {
        try
        {
            var game = _game;
            var window = game is null ? 0 : _gameWindow;
            var monitor = window != 0 ? Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) : Win32.MonitorFromPoint(default, 1 /* PRIMARY */);
            var luid = GpuDevice.LuidOfMonitor(monitor);
            _gpu = GpuDevice.Shared(luid);
            var fps = Math.Min(_config.MaxFps, RefreshRate(monitor));
            var (source, gate) = OpenSource(_gpu, window, monitor);
            var session = new CaptureSession(_gpu, source, new SessionOptions(fps, _config.Quality, true, RateMode.Clip));
            session.Gate = gate;
            if (window != 0 && game is not null) session.Rescue = current => Rescue(current, window, game, fps);
            _replay.Set(_config.ReplaySeconds, _config.ReplayMaxBytes ?? 512L << 20);
            _replay.Clear();
            session.AddListener(_replay.Add);
            if (session.Sound is { } sound) ApplySound(sound, game);
            _session = session;
            _sessionKey = key;
            _error = null;
            Host.Log($"Capture started: {session.Width}x{session.Height} at {fps} fps, {Name(session.Codec)} on {session.EncoderName} {session.SourceKind}" +
                (game is null ? "" : " for " + game.Title) + ".");
            if (_feed is not null) _feed.Mixer = session.Sound;
        }
        catch (Exception ex) when (ex is CaptureException or SharpGen.Runtime.SharpGenException)
        {
            _error = "Capture could not start: " + ex.Message;
            Host.Log(_error);
            _replayRestartAt = Environment.TickCount64 + 30_000;
        }
    }

    /// <summary>
    /// A game's window, or the screen it fills (which keeps the game on its direct path to the screen; frames are then
    /// kept only while the game is in front), or the desktop. Windows capture when it runs here, else the screen's own copy.
    /// </summary>
    private (IFrameSource Source, Func<bool>? Gate) OpenSource(GpuDevice gpu, nint window, nint monitor)
    {
        var interval = TimeSpan.FromTicks(10_000_000 / Math.Max(1, _config.MaxFps));
        var hdr = DuplicationSource.MonitorIsHdr(gpu, monitor);
        if (window == 0)
            return (CaptureSources.OpenCaptureSource(gpu, false, monitor, _config.Cursor, interval, hdr, "(screen)"), null);
        var fills = GameWindow.FillsMonitor(window, monitor);
        Func<bool>? gate = fills ? () => Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) == window : null;
        var source = CaptureSources.OpenCaptureSource(gpu, !fills, fills ? monitor : window, _config.Cursor, interval, hdr,
            fills ? "(the game's screen)" : "(game window)");
        return (source, gate);
    }

    /// <summary>Five seconds without a picture from the game: OBS's hook, if this game may have it.</summary>
    private IFrameSource? Rescue(IFrameSource current, nint window, GameTarget game, int fps)
    {
        if (current is HookSource || _hookFailed) return null;
        if (!game.Hook)
        {
            Host.Log("Windows capture gets no picture from the game, and the game hook is not for this game.");
            _hookFailed = true;
            _ui.Toast("Exo Capture can't record this game in exclusive full screen", "Set it to borderless", Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST));
            return null;
        }
        if (HookSource.Refusal(window, Win32.ImagePath(Win32.ProcessOf(window))) is { } why)
        {
            Host.Log("Game hook: not used, " + why);
            _hookFailed = true;
            return null;
        }
        try
        {
            Host.Log("Windows capture gets no picture from the game; taking it from inside the game.");
            return HookSource.Start(_gpu!, window, _obs, fps);
        }
        catch (CaptureException ex)
        {
            Host.Log("Game hook: " + ex.Message);
            _hookFailed = true;
            return null;
        }
    }

    private void ApplySound(SoundMixer sound, GameTarget? game)
    {
        var c = _config;
        var gamePid = game is null ? 0u : Win32.ProcessOf(_gameWindow);
        var voicePid = VoiceApp.Find();
        sound.VoiceInMix = c.CallVoices;
        // The game's track: the game and what it started; on the desktop, everything but the call (or, recording the call, everything).
        if (gamePid != 0) sound.Use(Tracks.Game, SoundSource.App, gamePid);
        else if (!c.CallVoices && voicePid != 0) sound.Use(Tracks.Game, SoundSource.AllBut, voicePid);
        else sound.Use(Tracks.Game, SoundSource.System);
        sound.Use(Tracks.Voice, c.CallVoices && voicePid != 0 ? SoundSource.App : null, voicePid);
        if (!sound.Fed) sound.Use(Tracks.Mic, SoundSource.Microphone, 0, c.Mic);
    }

    /// <summary>Under the gate.</summary>
    private void StopSession()
    {
        var session = _session;
        _session = null;
        _sessionKey = null;
        if (_feed is not null) _feed.Mixer = null;
        session?.Dispose();
    }

    private static int RefreshRate(nint monitor)
    {
        var device = Win32.MonitorDevice(monitor);
        return DisplayRefresh.Of(device) is int hz && hz >= 100 ? 120 : 60;
    }

    // ---------------------------------------------------------------- hotkeys

    private void OnHotkey(int id)
    {
        Task.Run(() =>
        {
            try
            {
                switch (id)
                {
                    case KeySaveReplay: SaveReplay(); break;
                    case KeyRecord: ToggleRecording(); break;
                    case KeySnip or KeyPrintScreen: Snip(); break;
                    case KeyScreenshot: Screenshot(); break;
                }
            }
            catch (CaptureException ex) { _ui.Toast(ex.Message, "", ToastMonitor()); }
            catch (Exception ex) { Host.Log("Hotkey: " + ex.Message); }
        });
    }

    private nint ToastMonitor() => _gameWindow != 0 ? Win32.MonitorFromWindow(_gameWindow, Win32.MONITOR_DEFAULTTONEAREST) : 0;

    // ---------------------------------------------------------------- files

    /// <summary>The folder a capture of this game goes in (Exo's SafeCaptureName: the title, without what Windows refuses in a name).</summary>
    internal static string SafeName(string title)
    {
        var chars = title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.').Trim();
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        return name.Length == 0 ? "Capture" : name;
    }

    /// <summary>A new file's path: the game's folder (or Desktop), the time, a suffix, never over an existing file.</summary>
    internal string NewPath(string? title, string suffix, string extension, DateTime? at = null)
    {
        var folder = Path.Combine(_config.Folder, title is null ? "Desktop" : SafeName(title));
        Directory.CreateDirectory(folder);
        var t = at ?? DateTime.Now;
        var stem = $"{t:yyyy-MM-dd HH.mm.ss}{suffix}";
        var path = Path.Combine(folder, stem + extension);
        for (var n = 2; File.Exists(path) || File.Exists(path + ".part"); n++) path = Path.Combine(folder, $"{stem} {n}{extension}");
        return path;
    }

    /// <summary>FNV-1a of the full path's UTF-8: the item id Exo uses, and the thumbnail's name.</summary>
    internal static string CaptureId(string fullPath)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(fullPath)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }

    private void Thumbnail(string path)
    {
        if (string.IsNullOrEmpty(_config.Thumbs)) return;
        try { Clips.Thumbnail(GpuDevice.Shared(), path, Path.Combine(_config.Thumbs, CaptureId(Path.GetFullPath(path)) + ".jpg")); }
        catch (Exception ex) { Host.Log("Thumbnail: " + ex.Message); }
    }

    private void Saved(string path, string kind, string? gameId, double durationMs, bool inGame, JsonObject? extra = null)
    {
        if (kind is not ("snip" or "screenshot")) Thumbnail(path);
        var data = new JsonObject { ["path"] = path, ["kind"] = kind, ["game"] = gameId ?? "", ["durationMs"] = Math.Round(durationMs), ["inGame"] = inGame };
        if (extra is not null) foreach (var (k, v) in extra) data[k] = v?.DeepClone();
        Host.Event("saved", data);
    }

    private bool _recovered;

    /// <summary>A recording a crash or power cut left as .mp4.part: made whole (it plays to its last second) and saved.</summary>
    private void Recover(string folder)
    {
        _recovered = true;
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var part in Directory.EnumerateFiles(folder, "*.mp4.part", SearchOption.AllDirectories))
                {
                    if (_recorder?.PartPath == part) continue;
                    try
                    {
                        Mp4File.MakePlain(part);
                        var final = part[..^5];
                        File.Move(part, final);
                        var ms = Mp4File.Read(final).DurationMs;
                        Host.Log($"Recovered a recording that was cut short ({ms / 1000:0} s).");
                        Saved(final, "recording", null, ms, false);
                    }
                    catch (Exception ex) { Host.Log("Recording recovery: " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    public void Dispose()
    {
        _closing = true;
        _supervisor.Dispose();
        lock (_gate)
        {
            if (_recorder is { } r) FinishRecording(r, null);
            foreach (var s in _streams.Values) s.Dispose();
            _streams.Clear();
            StopSession();
        }
    }
}

/// <summary>Finding the game's window from its install folder.</summary>
internal static class GameWindow
{
    /// <summary>The biggest shown top-level window whose program is inside <paramref name="folder"/>.</summary>
    internal static nint Find(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return 0;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        nint best = 0;
        long area = 0;
        var images = new Dictionary<uint, string?>();
        foreach (var hwnd in Win32.TopWindows())
        {
            if (!Win32.Shown(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != 0) continue;
            var pid = Win32.ProcessOf(hwnd);
            if (!images.TryGetValue(pid, out var image)) images[pid] = image = Win32.ImagePath(pid);
            if (image is null || !image.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (Win32.FrameBounds(hwnd) is not { } r) continue;
            var a = (long)r.Width * r.Height;
            if (a > area) (best, area) = (hwnd, a);
        }
        return best;
    }

    internal static bool FillsMonitor(nint window, nint monitor)
    {
        if (Win32.FrameBounds(window) is not { } r) return false;
        var m = Win32.MonitorInfo(monitor).Monitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}

/// <summary>The app that plays the call: the Discord app when it runs, else Exo itself (its own Discord client).</summary>
internal static class VoiceApp
{
    internal static uint Find()
    {
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary", "ExoLauncher" })
        {
            var all = System.Diagnostics.Process.GetProcessesByName(name);
            try
            {
                // The tree's root: the one whose parent isn't the same app.
                var first = all.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
                if (first is not null) return (uint)first.Id;
            }
            finally { foreach (var p in all) p.Dispose(); }
        }
        return 0;
    }
}

/// <summary>A screen's refresh rate, from its current display mode.</summary>
internal static unsafe class DisplayRefresh
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public fixed uint Rest[8];
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, DevMode* devMode);

    internal static int? Of(string device)
    {
        var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        return EnumDisplaySettingsW(device, -1, &mode) ? (int)mode.DisplayFrequency : null;
    }
}
