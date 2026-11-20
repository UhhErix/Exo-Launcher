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
using Microsoft.Win32.SafeHandles;

namespace ExoCapture;

internal sealed partial class Controller
{
    // ---------------------------------------------------------------- instant replay

    private readonly Dictionary<string, (string? Game, double Ms)> _pendingVoice = new(StringComparer.OrdinalIgnoreCase);

    private JsonNode SaveReplay()
    {
        List<Packet> packets;
        CaptureSession? session;
        GameTarget? game;
        lock (_gate)
        {
            session = _session;
            game = _game;
            if (_config.ReplayMode == "off") throw new CaptureException("Instant replay is off");
            if (session is null && _config.ReplayMode == "games" && _game is null) throw new CaptureException("It runs while a game is open");
            packets = _replay.Take(_config.ReplaySeconds);
        }
        if (session?.VideoTrack is not { } videoTrack || packets.Count(p => p.IsVideo) < 2)
        {
            _ui.Toast("Nothing to save yet", "", ToastMonitor());
            throw new CaptureException("Nothing to save yet");
        }
        var path = NewPath(game?.Title, " clip", ".mp4");
        var start = packets.First(p => p.IsVideo).Time;
        try
        {
            using (var writer = new Mp4Writer(path, [videoTrack, .. CaptureSession.SoundTracks()], fragmented: false, session.Fps))
            {
                foreach (var p in packets)
                {
                    if (p.Time < start) continue;
                    writer.Write(p.IsVideo ? 0 : p.Track + 1, p.Data, p.Time - start, p.Key);
                }
                writer.Finish();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Clips.TryDelete(path);
            Host.Log("Replay save failed: " + ex.Message);
            _ui.Toast("Clip not saved", "The captures folder can't be written.", ToastMonitor());
            throw new CaptureException("The captures folder can't be written.");
        }
        var ms = (packets.Last(p => p.IsVideo).Time - start) / 10_000.0;
        _ui.Toast("Clip saved", $"{ms / 1000:0} s", ToastMonitor());
        // The microphone was kept raw: Exo cleans it now, then the clip is announced.
        var sound = session.Sound;
        if (_config.CleanClips && sound is not null && !sound.Fed && ClipVoice.Export(path, VoiceFolder, out var raw, out var clean))
        {
            var fed = new JsonArray();
            foreach (var (from, to) in sound.FedSpans())
            {
                var a = Math.Max(0, (from - start) / 10_000.0);
                var b = Math.Min(ms, (to - start) / 10_000.0);
                if (b > a) fed.Add((JsonNode)new JsonArray(Math.Round(a), Math.Round(b)));
            }
            lock (_pendingVoice) _pendingVoice[path] = (game?.Id, ms);
            Host.Event("clip.voice", new JsonObject { ["path"] = path, ["raw"] = raw, ["clean"] = clean, ["fed"] = fed, ["kind"] = "clip", ["game"] = game?.Id ?? "", ["durationMs"] = Math.Round(ms) });
        }
        else Saved(path, "clip", game?.Id, ms, game is not null);
        return State();
    }

    private string VoiceFolder => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_config.Thumbs.Length > 0 ? _config.Thumbs : Path.GetTempPath()))!, "voice");

    /// <summary>Exo cleaned (or couldn't clean) a clip's microphone: its voice goes into the clip, then the clip is announced.</summary>
    private JsonNode ClipVoiced(JsonNode? p)
    {
        var path = p.Str("path") ?? "";
        (string? Game, double Ms) pending;
        lock (_pendingVoice)
        {
            if (!_pendingVoice.Remove(path, out pending)) return false;
        }
        var ok = p.Bool("ok", false);
        var callVoices = _config.CallVoices;
        Task.Run(() =>
        {
            if (ok)
                try { ClipVoice.Remix(path, VoiceFolder, callVoices); }
                catch (Exception ex) { Host.Log("Clip voice: the microphone stays raw in this clip: " + ex.Message); }
            ClipVoice.Clean(path, VoiceFolder);
            Saved(path, "clip", pending.Game, pending.Ms, pending.Game is not null);
        });
        return true;
    }

    // ---------------------------------------------------------------- recording

    private JsonNode ToggleRecording()
    {
        Recorder? finishing = null;
        lock (_gate)
        {
            if (_recorder is { } running) finishing = running;
            else
            {
                var game = _game;
                var path = NewPath(game?.Title, "", ".mp4");
                _recorder = new Recorder(path, game?.Id);
                if (_session is null)
                {
                    var key = _game is null ? "desktop" : _gameWindow != 0 ? "game:" + _gameWindow : null;
                    if (key is null) { _recorder = null; throw new CaptureException("Recording could not start"); }
                    _replayRestartAt = 0;
                    StartSession(key);
                }
                if (_session is not { } session)
                {
                    _recorder = null;
                    throw new CaptureException(_error ?? "Recording could not start");
                }
                _recorder.Attach(session);
                Host.Log("Recording started.");
                _ui.Toast("Recording", "Stop with " + _config.Hotkeys.GetValueOrDefault("record", "Ctrl+Alt+F9"), ToastMonitor());
            }
        }
        if (finishing is not null) FinishRecording(finishing, null);
        Publish(always: true);
        return State();
    }

    private void FinishRecording(Recorder recorder, string? why)
    {
        lock (_gate)
        {
            if (_recorder != recorder) return;
            _recorder = null;
        }
        if (why is not null) Host.Log("Recording failed: " + why);
        var (path, ms) = recorder.Finish();
        if (path is null) { _ui.Toast("Recording failed", why ?? "", ToastMonitor()); return; }
        Host.Log($"Recording saved ({ms / 1000:0} s).");
        _ui.Toast("Recording saved", $"{(int)(ms / 60000)}:{(int)(ms / 1000) % 60:00}", ToastMonitor());
        Saved(path, "recording", recorder.GameId, ms, recorder.GameId is not null);
        Publish(always: true);
    }

    // ---------------------------------------------------------------- snips and screenshots

    private int _snipping;
    private static GpuDevice GpuFor(nint monitor) => GpuDevice.ForMonitor(monitor);

    /// <summary>The screens as they are this moment: one SDR picture of the virtual screen, and each HDR screen's own FP16 picture.</summary>
    internal sealed record Frozen(Win32.RECT Screen, byte[] Bgra, List<(nint Monitor, Win32.RECT Rect, byte[] Half, float White, float Peak)> Hdr);

    private Frozen Freeze()
    {
        var screen = Win32.VirtualScreen();
        var bgra = new byte[screen.Width * screen.Height * 4];
        var hdr = new List<(nint, Win32.RECT, byte[], float, float)>();
        foreach (var monitor in Win32.Monitors())
        {
            var rect = Win32.MonitorInfo(monitor).Monitor;
            var gpu = GpuFor(monitor);
            using var source = new DuplicationSource(gpu, monitor, 0, hdr: true, "(screen)");
            if (source.Grab() is { } texture)
            {
                var pixels = gpu.Read(texture, out var w, out var h, out var bpp);
                byte[] sdr;
                if (bpp == 8)
                {
                    using var output = DuplicationSource.OutputOf(gpu, monitor, out var description);
                    var white = Imaging.SdrWhiteNits(monitor);
                    var peak = description.MaxLuminance > 0 ? description.MaxLuminance : 1000;
                    hdr.Add((monitor, rect, pixels, white, peak));
                    sdr = Imaging.ToneMap(pixels, w, h, white, peak);
                }
                else sdr = pixels;
                CopyMonitorBgra(bgra, screen, rect, sdr, w, h);
            }
            else if (GrabMonitorGdi(rect) is { } gdi)
            {
                CopyMonitorBgra(bgra, screen, rect, gdi, rect.Width, rect.Height);
            }
        }
        return new Frozen(screen, bgra, hdr);
    }

    private static void CopyMonitorBgra(byte[] bgra, Win32.RECT screen, Win32.RECT rect, byte[] sdr, int w, int h)
    {
        var copyW = Math.Min(w, rect.Width);
        for (var y = 0; y < Math.Min(h, rect.Height); y++)
            Buffer.BlockCopy(sdr, y * w * 4, bgra, ((rect.Top - screen.Top + y) * screen.Width + rect.Left - screen.Left) * 4, copyW * 4);
    }

    private static unsafe byte[]? GrabMonitorGdi(Win32.RECT rect)
    {
        if (rect.Width < 2 || rect.Height < 2) return null;
        var screenDc = Win32.GetDC(0);
        if (screenDc == 0) return null;
        nint memDc = 0, dib = 0, oldObj = 0;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == 0) return null;
            var bi = new Win32.BITMAPINFOHEADER
            {
                Size = (uint)sizeof(Win32.BITMAPINFOHEADER),
                Width = rect.Width,
                Height = -rect.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };
            void* bits;
            dib = Win32.CreateDIBSection(screenDc, &bi, 0, &bits, 0, 0);
            if (dib == 0 || bits is null) return null;
            oldObj = Win32.SelectObject(memDc, dib);
            if (!Win32.CopyBits(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.Left, rect.Top, 0x00CC0020)) return null;
            var raw = new byte[rect.Width * rect.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy((nint)bits, raw, 0, raw.Length);
            for (var i = 3; i < raw.Length; i += 4) raw[i] = 255;
            return raw;
        }
        finally
        {
            if (oldObj != 0 && memDc != 0) Win32.SelectObject(memDc, oldObj);
            if (dib != 0) Win32.DeleteObject(dib);
            if (memDc != 0) Win32.DeleteDC(memDc);
            Win32.ReleaseDC(0, screenDc);
        }
    }

    private JsonNode Snip()
    {
        if (Interlocked.Exchange(ref _snipping, 1) == 1) return State();
        try
        {
            var game = _game;
            var inGame = game is not null && _gameWindow != 0 && Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) == _gameWindow;
            var frozen = Freeze();
            var own = (uint)Environment.ProcessId;
            var windows = Win32.TopWindows()
                .Where(w => Win32.Shown(w) && Win32.ProcessOf(w) != own && Win32.ClassName(w) is not ("Progman" or "WorkerW"))
                .Select(w => (w, Bounds: Win32.FrameBounds(w))).Where(x => x.Bounds is not null).Select(x => (x.w, x.Bounds!.Value)).ToList();
            var done = new TaskCompletionSource<SnipChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ui.Invoke(() => new SnipOverlay(_ui, frozen.Screen, frozen.Bgra, windows, choice => done.TrySetResult(choice)).Show());
            if (!done.Task.Wait(TimeSpan.FromMinutes(5)) || done.Task.Result is not { } chosen) return State();
            SaveSnip(frozen, chosen, game, inGame, "snip");
            return State();
        }
        finally { Interlocked.Exchange(ref _snipping, 0); }
    }

    private void SaveSnip(Frozen frozen, SnipChoice chosen, GameTarget? game, bool inGame, string kind)
    {
        var s = frozen.Screen;
        var r = chosen.Rect;
        var left = Math.Clamp(r.Left, s.Left, s.Right - 1);
        var top = Math.Clamp(r.Top, s.Top, s.Bottom - 1);
        var right = Math.Clamp(r.Right, left + 1, s.Right);
        var bottom = Math.Clamp(r.Bottom, top + 1, s.Bottom);
        var crop = Imaging.Crop(frozen.Bgra, s.Width, 4, left - s.Left, top - s.Top, right - left, bottom - top);
        // Only a snip inside one HDR screen gets the HDR copy.
        var hdr = frozen.Hdr.FirstOrDefault(m => left >= m.Rect.Left && top >= m.Rect.Top && right <= m.Rect.Right && bottom <= m.Rect.Bottom);
        WritePicture(crop, right - left, bottom - top, hdr.Half is null ? null : Imaging.Crop(hdr.Half, hdr.Rect.Width, 8, left - hdr.Rect.Left, top - hdr.Rect.Top, right - left, bottom - top),
            game, inGame, kind);
    }

    private void WritePicture(byte[] bgra, int width, int height, byte[]? half, GameTarget? game, bool inGame, string kind)
    {
        var path = NewPath(game?.Title, "", ".png");
        JsonObject? extra = null;
        try
        {
            Imaging.SavePng(path, bgra, width, height);
            if (half is not null)
            {
                var hdrPath = Path.ChangeExtension(path, ".jxr");
                try
                {
                    Imaging.SaveJxr(hdrPath, half, width, height);
                    extra = new JsonObject { ["hdr"] = true, ["hdrPath"] = hdrPath };
                }
                catch (Exception ex) { Host.Log("HDR snip: " + ex.Message + "; the plain one is kept."); }
            }
        }
        catch (Exception ex)
        {
            Host.Log("Snip save failed: " + ex.Message);
            _ui.Toast("Snip copied, not saved", "The captures folder can't be written.", ToastMonitor());
            return;
        }
        try { Imaging.CopyImage(path); } catch (Exception ex) { Host.Log("Snip copy: " + ex.Message); }
        _ui.Toast(kind == "screenshot" ? "Screenshot saved" : "Snip saved", "Copied", ToastMonitor());
        Saved(path, kind, game?.Id, 0, inGame, extra);
    }

    /// <summary>
    /// The game's whole window, as the clip sees it (inside an exclusive full-screen game too, through the session's
    /// source), else the window in front: saved and copied at once, no overlay, the game left in front.
    /// </summary>
    private JsonNode Screenshot()
    {
        var game = _game;
        var session = _session;
        (byte[] Pixels, int Width, int Height, bool Hdr)? picture = null;
        if (game is not null && session is not null && _gameWindow != 0) picture = session.Picture();
        var window = game is not null && _gameWindow != 0 ? _gameWindow : Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT);
        if (picture is null && window != 0)
        {
            var monitor = Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST);
            var gpu = GpuFor(monitor);
            if (GameWindow.FillsMonitor(window, monitor))
            {
                var frozen = Freeze();
                SaveSnip(frozen, new SnipChoice(Win32.MonitorInfo(monitor).Monitor, window, monitor, "screen"), game, game is not null, "screenshot");
                return State();
            }
            if (GoLive.Grab(gpu, window, 0) is { } grabbed) picture = (grabbed.Pixels, grabbed.Width, grabbed.Height, false);
        }
        if (picture is not { } p) throw new CaptureException("That window gives no picture; is it minimized?");
        if (p.Hdr)
        {
            var monitor = Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST);
            using var output = DuplicationSource.OutputOf(GpuFor(monitor), monitor, out var description);
            var sdr = Imaging.ToneMap(p.Pixels, p.Width, p.Height, Imaging.SdrWhiteNits(monitor), description.MaxLuminance > 0 ? description.MaxLuminance : 1000);
            WritePicture(sdr, p.Width, p.Height, p.Pixels, game, game is not null, "screenshot");
        }
        else WritePicture(p.Pixels, p.Width, p.Height, null, game, game is not null, "screenshot");
        return State();
    }

    // ---------------------------------------------------------------- edits

    private static string Beside(string path, string suffix, string extension)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path) + suffix;
        var output = Path.Combine(folder, stem + extension);
        for (var n = 2; File.Exists(output); n++) output = Path.Combine(folder, $"{stem} {n}{extension}");
        return output;
    }

    private JsonNode Trim(JsonNode? p)
    {
        var path = Inside(p.Str("path"));
        var output = Clips.Trim(path, p.Num("startMs") ?? 0, p.Num("endMs") ?? 0, Beside(path, " trim", ".mp4"));
        Saved(output, "trim", null, Mp4File.Read(output).DurationMs, false);
        return new JsonObject { ["path"] = output };
    }

    private JsonNode Export(JsonNode? p)
    {
        var path = Inside(p.Str("path"));
        var preset = p.Str("preset") ?? "youtube";
        var start = p.Num("startMs") ?? 0;
        var end = p.Num("endMs") ?? 0;
        var gpu = GpuDevice.Shared();
        string output;
        using (Background())
        {
            output = preset switch
            {
                "gif" => Clips.Gif(gpu, path, start, end, Beside(path, "", ".gif"), CancellationToken.None),
                "webm" => Clips.WebM(gpu, path, start, end, Beside(path, "", ".webm"), CancellationToken.None),
                "discord" => Clips.Export(gpu, path, preset, start, end, Beside(path, " for Discord", ".mp4"), CancellationToken.None),
                "vertical" => Clips.Export(gpu, path, preset, start, end, Beside(path, " vertical", ".mp4"), CancellationToken.None),
                "youtube" => Clips.Export(gpu, path, preset, start, end, Beside(path, " full", ".mp4"), CancellationToken.None),
                _ => throw new CaptureException("Unknown export."),
            };
        }
        var ms = output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? Mp4File.Read(output).DurationMs : 0;
        Saved(output, "export", null, ms, false);
        return new JsonObject { ["path"] = output };
    }

    private int _shrinking;

    private JsonNode Shrink(JsonNode? p)
    {
        if (Interlocked.Exchange(ref _shrinking, 1) == 1) return new JsonObject { ["ok"] = false, ["skipped"] = "busy", ["detail"] = "another file is being shrunk" };
        var cts = _shrinkCts = new CancellationTokenSource();
        try
        {
            using (Background())
                return Clips.Shrink(GpuDevice.Shared(), Inside(p.Str("input")), p.Int("targetQuality", 70), _recorder?.FinalPath, cts.Token);
        }
        catch (Skip skip)
        {
            Host.Log("Shrink: " + skip.Reason + ": " + skip.Message);
            return new JsonObject { ["ok"] = false, ["skipped"] = skip.Reason, ["detail"] = skip.Message };
        }
        catch (OperationCanceledException) { return new JsonObject { ["ok"] = false, ["skipped"] = "cancelled" }; }
        catch (CaptureException ex) { return new JsonObject { ["ok"] = false, ["skipped"] = "failed", ["detail"] = ex.Message }; }
        finally
        {
            _shrinkCts = null;
            Interlocked.Exchange(ref _shrinking, 0);
        }
    }

    private int _joining;

    private JsonNode Concat(JsonNode? p)
    {
        if (Interlocked.Exchange(ref _joining, 1) == 1) return new JsonObject { ["ok"] = false, ["skipped"] = "busy", ["detail"] = "another join is running" };
        var cts = _concatCts = new CancellationTokenSource();
        try
        {
            var paths = (p?["paths"] as JsonArray ?? []).Select(x => Inside(x?.GetValue<string>())).ToList();
            var output = Inside(p.Str("output") ?? throw new CaptureException("name the new file"));
            var last = -1;
            string made;
            using (Background())
                made = Clips.Concat(GpuDevice.Shared(), paths, output, p.Str("mode") ?? "copy", p.Int("width", 1920), p.Int("height", 1080), percent =>
                {
                    var whole = (int)percent;
                    if (whole == last) return;
                    last = whole;
                    Host.Event("concat", new JsonObject { ["percent"] = whole });
                }, cts.Token);
            Saved(made, "concat", p.Str("game") is { Length: > 0 } g ? g : null, Mp4File.Read(made).DurationMs, false);
            return new JsonObject { ["ok"] = true, ["path"] = made };
        }
        catch (Skip skip) { return new JsonObject { ["ok"] = false, ["skipped"] = skip.Reason, ["detail"] = skip.Message }; }
        catch (OperationCanceledException) { return new JsonObject { ["ok"] = false, ["skipped"] = "cancelled" }; }
        catch (CaptureException ex) { return new JsonObject { ["ok"] = false, ["skipped"] = "failed", ["detail"] = ex.Message }; }
        finally
        {
            _concatCts = null;
            Interlocked.Exchange(ref _joining, 0);
        }
    }

    /// <summary>Long work (exports, shrinking, joins) runs below every game's priority.</summary>
    private static IDisposable Background()
    {
        var thread = Win32.GetCurrentThread();
        Win32.SetThreadPriority(thread, -2 /* LOWEST */);
        return new Restore(thread);
    }

    private sealed class Restore(nint thread) : IDisposable
    {
        public void Dispose() => Win32.SetThreadPriority(thread, 0);
    }

    // ---------------------------------------------------------------- Go Live

    private GoLive? StreamFor(JsonNode? p)
    {
        var id = p.Long("id", 0);
        lock (_gate) return _streams.Get(id);
    }

    private JsonNode StartStream(JsonNode? p)
    {
        if (p is null) throw new CaptureException("Unknown request.");
        var window = (nint)p.Long("window", 0);
        var monitor = (nint)p.Long("monitor", 0);
        if (window != 0 && !Win32.IsWindow(window)) throw new CaptureException("That window or screen is gone.");
        if (window != 0 && Win32.IsIconic(window)) throw new CaptureException("That window gives no picture; is it minimized?");
        var at = window != 0 ? Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) : monitor;
        var cursor = p.Bool("cursor", true);
        var id = Interlocked.Increment(ref _nextStream);
        var stream = GoLive.Start(id, p, () => GpuFor(at), gpu =>
            CaptureSources.OpenCaptureSource(gpu, window != 0, window != 0 ? window : monitor, cursor, TimeSpan.FromMilliseconds(8)));
        stream.Ended += reason =>
        {
            lock (_gate) _streams.Remove(id, out _);
            Host.Log("Go Live ended: " + reason);
            Host.Event("stream.ended", new JsonObject { ["id"] = id, ["reason"] = reason });
            Task.Run(stream.Dispose);
        };
        lock (_gate) _streams.Add(id, stream);
        Host.Log($"Go Live: {stream.Width}x{stream.Height} at {stream.Fps} fps, {stream.Bitrate / 1000} kbit/s, {Name(stream.Codec)}{(window != 0 ? " (window)" : " (screen)")}.");
        return new JsonObject
        {
            ["id"] = id,
            ["width"] = stream.Width,
            ["height"] = stream.Height,
            ["fps"] = stream.Fps,
            ["bitrate"] = stream.Bitrate,
            ["codec"] = stream.Codec == Codec.Hevc ? "hevc" : "h264",
            ["hevc"] = Available(Codec.Hevc),
        };
    }

    private JsonNode StopStream(JsonNode? p)
    {
        List<GoLive> toStop;
        lock (_gate) toStop = _streams.TakeStop(p);
        foreach (var s in toStop) s.Dispose();
        return true;
    }

    // ---------------------------------------------------------------- Exo's cleaned voice

    private JsonNode StartFeed(JsonNode? p)
    {
        var pipe = (nint)p.Long("pipe", 0);
        if (pipe == 0) throw new CaptureException("Exo gave no pipe for the voice.");
        var feed = new VoiceFeed(pipe, () => _config.Mic);
        lock (_gate)
        {
            _feed = feed;
            feed.Mixer = _session?.Sound;
        }
        feed.Ended += () => { lock (_gate) if (_feed == feed) _feed = null; };
        Host.Log("Sound: the microphone track takes Exo's cleaned voice");
        return true;
    }
}

/// <summary>A recording: written fragmented to <c>.mp4.part</c> from the next key frame, made plain and named <c>.mp4</c> when it ends.</summary>
internal sealed class Recorder(string finalPath, string? gameId)
{
    internal string FinalPath { get; } = finalPath;
    internal string PartPath { get; } = finalPath + ".part";
    internal string? GameId { get; } = gameId;
    private Mp4Writer? _writer;
    private CaptureSession? _session;
    private long _start = -1, _last;
    private readonly Lock _gate = new();
    internal string? Failed { get; private set; }
    private readonly long _began = Environment.TickCount64;

    internal long ElapsedMs => Environment.TickCount64 - _began;

    internal void Attach(CaptureSession session)
    {
        _session = session;
        session.AddListener(OnPacket);
    }

    private void OnPacket(Packet p)
    {
        lock (_gate)
        {
            if (Failed is not null) return;
            try
            {
                if (_writer is null)
                {
                    if (!p.IsVideo || !p.Key || _session?.VideoTrack is not { } video) return;
                    _writer = new Mp4Writer(PartPath, [video, .. CaptureSession.SoundTracks()], fragmented: true, _session.Fps);
                    _start = p.Time;
                }
                if (p.Time < _start) return;
                _writer.Write(p.IsVideo ? 0 : p.Track + 1, p.Data, p.Time - _start, p.Key);
                if (p.IsVideo) _last = p.Time;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Failed = ex.Message; }
        }
    }

    /// <summary>The finished file and its length, or null when nothing was written.</summary>
    internal (string? Path, double Ms) Finish()
    {
        _session?.RemoveListener(OnPacket);
        lock (_gate)
        {
            if (_writer is null) return (null, 0);
            try
            {
                _writer.Finish();
                _writer.Dispose();
                File.Move(PartPath, FinalPath);
                return (FinalPath, (_last - _start) / 10_000.0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Host.Log("Recording: " + ex.Message + "; it is recovered at the next start.");
                return (null, 0);
            }
        }
    }
}

/// <summary>
/// Exo's cleaned voice for the microphone track (voice.feed): 20 ms frames down an anonymous pipe, each a 16-byte
/// header (i64 QPC time in 100 ns, i32 sample frames, i32 channels) and 16-bit samples.
/// </summary>
internal sealed class VoiceFeed
{
    internal volatile SoundMixer? Mixer;
    internal event Action? Ended;
    private readonly Func<string> _mic;

    internal VoiceFeed(nint pipe, Func<string> mic)
    {
        _mic = mic;
        new Thread(() => Read(pipe)) { IsBackground = true, Name = "Exo Capture voice feed" }.Start();
    }

    /// <summary>A packet's header (RecordedVoice.Send writes it): its time, sample frames and channels; null when it can't be one.</summary>
    internal static (long At, int Frames, int Channels)? Parse(ReadOnlySpan<byte> header)
    {
        var at = BitConverter.ToInt64(header);
        var frames = BitConverter.ToInt32(header[8..]);
        var channels = BitConverter.ToInt32(header[12..]);
        return frames is <= 0 or > 4800 || channels is < 1 or > 2 ? null : (at, frames, channels);
    }

    private void Read(nint pipe)
    {
        using var stream = new FileStream(new SafeFileHandle(pipe, true), FileAccess.Read, 1);
        var header = new byte[16];
        var floats = new float[4800 * 2];
        try
        {
            while (true)
            {
                stream.ReadExactly(header);
                if (Parse(header) is not var (at, frames, channels)) break;
                var bytes = new byte[frames * channels * 2];
                stream.ReadExactly(bytes);
                for (var i = 0; i < frames; i++)
                {
                    var l = BitConverter.ToInt16(bytes, i * channels * 2) / 32768f;
                    var r = channels == 2 ? BitConverter.ToInt16(bytes, i * 4 + 2) / 32768f : l;
                    floats[i * 2] = l;
                    floats[i * 2 + 1] = r;
                }
                Mixer?.Feed(floats, frames, at);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException) { }
        Host.Log("Sound: Exo's voice feed ended");
        Mixer?.FeedEnded(_mic());
        Ended?.Invoke();
    }
}

internal sealed class StreamTable<T> where T : class
{
    private readonly Dictionary<long, T> _items = [];
    private readonly List<long> _order = [];
    private long _currentId;

    internal long CurrentId => _currentId;
    internal T? Current => _currentId != 0 && _items.TryGetValue(_currentId, out var item) ? item : null;
    internal IEnumerable<T> Values => _items.Values;
    internal T? Get(long id) => id > 0 && _items.TryGetValue(id, out var item) ? item : Current;

    internal void Clear()
    {
        _items.Clear();
        _order.Clear();
        _currentId = 0;
    }

    internal void Add(long id, T item)
    {
        _items[id] = item;
        _order.Remove(id);
        _order.Add(id);
        _currentId = id;
    }

    internal bool Remove(long id, out T? item)
    {
        if (!_items.Remove(id, out item)) return false;
        _order.Remove(id);
        if (_currentId == id) _currentId = _order.Count > 0 ? _order[^1] : 0;
        return true;
    }

    internal List<T> TakeStop(JsonNode? p)
    {
        if (p.Bool("all", false))
        {
            var all = _items.Values.ToList();
            _items.Clear();
            _order.Clear();
            _currentId = 0;
            return all;
        }
        var id = p.Long("id", 0);
        if (id <= 0 || !Remove(id, out var item) || item is null) return [];
        return [item];
    }
}
