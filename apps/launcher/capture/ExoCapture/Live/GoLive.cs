using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ExoCapture.Capture;
using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using ExoCapture.Native;
using Microsoft.Win32.SafeHandles;
using Vortice.Mathematics;

namespace ExoCapture.Live;

/// <summary>
/// Go Live's picture for Exo's Discord client: a window or a screen, encoded at a constant bit rate with low delay
/// (H.264, or HEVC when Discord's server picks it), each frame written down the pipe Exo handed over: a 16-byte
/// header (u32 length, u32 flags with 1 for a key frame, i64 capture time in 100 ns, little-endian), then the frame
/// in Annex B with its parameter sets in front of every key frame.
/// </summary>
internal sealed class GoLive : IDisposable
{
    internal readonly long Id;
    private readonly CaptureSession _session;
    private readonly FileStream _pipe;
    private readonly BlockingCollection<(byte[] Data, long Time, bool Key)> _queue = new(boundedCapacity: 30);
    private readonly Thread _writer;
    private volatile bool _closed;
    internal event Action<string>? Ended;
    internal readonly int Width, Height, Fps;
    internal int Bitrate => _session.Bitrate;
    internal readonly Codec Codec;

    private GoLive(long id, CaptureSession session, nint pipe)
    {
        Id = id;
        _session = session;
        (Width, Height, Fps, Codec) = (session.Width, session.Height, session.Fps, session.Codec);
        _pipe = new FileStream(new SafeFileHandle(pipe, ownsHandle: true), FileAccess.Write, 1);
        _writer = new Thread(Write) { IsBackground = true, Name = "Exo Capture Go Live" };
        _writer.Start();
        session.AddRawListener(OnFrame);
        session.Stopped += reason => End(reason);
    }

    internal static (int W, int H) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        width = Math.Max(64, width & ~1);
        height = Math.Max(64, height & ~1);
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        var w = Math.Min(maxWidth & ~1, Math.Max(64, ((int)Math.Round(width * scale)) & ~1));
        var h = Math.Min(maxHeight & ~1, Math.Max(64, ((int)Math.Round(height * scale)) & ~1));
        return (w, h);
    }

    internal static (List<(int W, int H, int Fps)> Steps, Codec[] Codecs) BuildPlan(int width, int height, int fps, Codec codec, bool allowFallback)
    {
        width = Math.Max(64, width & ~1);
        height = Math.Max(64, height & ~1);
        fps = Math.Clamp(fps, 5, 120);
        var steps = new List<(int W, int H, int Fps)> { (width, height, fps) };
        if (fps > 60) steps.Add((width, height, 60));
        if (fps > 30) steps.Add((width, height, 30));
        if (height > 1080) steps.Add((Math.Max(64, ((int)Math.Round(width * 1080.0 / height)) & ~1), 1080, Math.Min(fps, 60)));
        var (sw, sh) = FitWithin(width, height, 1280, 720);
        steps.Add((sw, sh, Math.Min(fps, 30)));
        Codec[] codecs = codec == Codec.H264 || !allowFallback ? [codec] : [codec, Codec.H264];
        return ([.. steps.Distinct()], codecs);
    }

    /// <summary>
    /// Starts the stream, stepping down when the card's encoder refuses: the settings asked, then 60 and 30 fps, then
    /// 1080p, then Windows' own H.264 encoder on the processor inside 1280x720.
    /// </summary>
    internal static GoLive Start(long id, JsonNode p, Func<GpuDevice> gpuFor, Func<GpuDevice, IFrameSource> source)
    {
        var pipe = (nint)p.Long("pipe", 0);
        if (pipe == 0) throw new CaptureException("Exo gave no pipe for the stream.");
        var pipeTransferred = false;
        try
        {
            var width = p.Int("width", 1920);
            var height = p.Int("height", 1080);
            var fps = Math.Clamp(p.Int("fps", 60), 5, 120);
            var bitrate = Math.Clamp(p.Int("bitrate", 8_000_000), 300_000, 80_000_000);
            var codec = p.Str("codec") == "hevc" ? Codec.Hevc : Codec.H264;
            var allowFallback = p.Bool("allowFallback", true);
            var gpu = gpuFor();
            var (steps, codecs) = BuildPlan(width, height, fps, codec, allowFallback);
            Exception? last = null;
            foreach (var (w, h, f) in steps)
            {
                IFrameSource? src = null;
                try
                {
                    src = source(gpu);
                    var options = new SessionOptions(f, 50, false, RateMode.Stream, bitrate, w, h, codecs, Sound: false, GopSeconds: 4);
                    var session = new CaptureSession(gpu, src, options);
                    var live = new GoLive(id, session, pipe);
                    pipeTransferred = true;
                    return live;
                }
                catch (CaptureException ex) when (ex.Message.Contains("encoder", StringComparison.Ordinal))
                {
                    src?.Dispose();
                    last = ex;
                    Host.Log($"Go Live: {w}x{h} at {f} fps did not start ({ex.Message}).");
                }
            }
            throw last ?? new CaptureException("no hardware video encoder");
        }
        finally
        {
            if (!pipeTransferred) Win32.CloseHandle(pipe);
        }
    }

    private void OnFrame(byte[] data, long time, bool key)
    {
        if (_closed) return;
        if (key && Codec != Codec.Av1 && _session.ParameterSets is { } sets)
        {
            var nals = Bitstream.Nals(data);
            var has = nals.Any(n => Bitstream.IsParameterSet(Codec, Bitstream.NalType(Codec, n)));
            if (!has)
            {
                using var m = new MemoryStream();
                foreach (var s in sets) { m.Write([0, 0, 0, 1]); m.Write(s); }
                m.Write(data);
                data = m.ToArray();
            }
        }
        // A frame Exo can't take in time is dropped; a key frame waits its turn.
        if (!_queue.TryAdd((data, time, key), key ? 500 : 0) && key) End("Exo stopped reading the stream");
    }

    private void Write()
    {
        var header = new byte[16];
        try
        {
            foreach (var (data, time, key) in _queue.GetConsumingEnumerable())
            {
                Header(header, data.Length, key, time);
                _pipe.Write(header);
                _pipe.Write(data);
                _pipe.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { End("Exo stopped reading the stream"); }
    }

    /// <summary>A frame's 16-byte header: u32 length, u32 flags (1: key frame), i64 capture time in 100 ns, little-endian (GoLiveCapture.ParseHeader reads it).</summary>
    internal static void Header(Span<byte> header, int length, bool key, long time)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], key ? 1u : 0u);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], time);
    }

    private void End(string reason)
    {
        if (_closed) return;
        _closed = true;
        Ended?.Invoke(reason);
    }

    internal void Keyframe() => _session.RequestKeyFrame();
    internal void SetBitrate(int bitrate) => _session.SetBitrate(bitrate);

    internal string? Preview(int width)
    {
        if (_session.Preview(width) is not { } p) return null;
        return Convert.ToBase64String(Imaging.Jpeg(p.Pixels, p.Width, p.Height, 0.75f));
    }

    public void Dispose()
    {
        _closed = true;
        _session.RemoveRawListener(OnFrame);
        _queue.CompleteAdding();
        _session.Dispose();
        _writer.Join(1000);
        try { _pipe.Dispose(); } catch (IOException) { }
    }

    private const int ThumbItemTimeoutMs = 150, ThumbBatchTimeoutMs = 3500;

    /// <summary>One picture of each window or screen (Go Live's picker), as base64 JPEG at most <paramref name="width"/> across.</summary>
    internal static JsonObject Thumbs(Func<nint, GpuDevice> gpuFor, JsonArray items, int width)
    {
        var result = new JsonObject();
        var deadline = Environment.TickCount64 + ThumbBatchTimeoutMs;
        var wgc = WgcSource.Available(out _);
        Dictionary<nint, DuplicationSource> heldMonitors = [];
        try
        {
            foreach (var item in items)
            {
                if (Environment.TickCount64 >= deadline) break;
                var id = item.Str("id");
                if (id is null) continue;
                try
                {
                    var window = (nint)item.Long("window", 0);
                    var monitor = (nint)item.Long("monitor", 0);
                    var at = window != 0 ? Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) : monitor;
                    var timeout = (int)Math.Clamp(deadline - Environment.TickCount64, 1, ThumbItemTimeoutMs);
                    (byte[] Pixels, int Width, int Height)? picture = null;
                    if (wgc)
                        picture = Grab(gpuFor(at), window, monitor, timeout);
                    else if (window != 0)
                    {
                        picture = GrabWindowViaPrint(window);
                        if (picture is null && Win32.Shown(window))
                            picture = GrabMonitorWithReuse(gpuFor, heldMonitors, at, window, timeout);
                    }
                    else
                        picture = GrabMonitorWithReuse(gpuFor, heldMonitors, monitor, 0, timeout);

                    if (picture is { } pic && pic.Width > 0 && pic.Height > 0)
                    {
                        var (pixels, w, h) = Imaging.Shrink(pic.Pixels, pic.Width, pic.Height, Math.Clamp(width, 64, 1920));
                        result[id] = Convert.ToBase64String(Imaging.Jpeg(pixels, w, h, 0.75f));
                    }
                }
                catch (Exception ex) when (ex is CaptureException or SharpGen.Runtime.SharpGenException) { Host.Log("Go Live picture: " + ex.Message); }
            }
        }
        finally
        {
            foreach (var dup in heldMonitors.Values) dup.Dispose();
        }
        return result;
    }

    private static (byte[] Pixels, int Width, int Height)? GrabMonitorWithReuse(
        Func<nint, GpuDevice> gpuFor, Dictionary<nint, DuplicationSource> held, nint monitor, nint window, int timeoutMs)
    {
        if (monitor == 0) return null;
        var gpu = gpuFor(monitor);
        if (!held.TryGetValue(monitor, out var source))
            held[monitor] = source = new DuplicationSource(gpu, monitor, 0, false, "(screen)", cursor: false);

        Win32.RECT? crop = null;
        if (window != 0)
        {
            if (Win32.IsIconic(window) || Win32.FrameBounds(window) is not { } bounds) return null;
            var desktop = Win32.MonitorInfo(monitor).Monitor;
            crop = DuplicationSource.IntersectWindow(bounds, desktop, source.Width, source.Height);
            if (crop is null) return null;
        }

        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (source.Latest() is { } frame)
            {
                var pixels = gpu.Read(frame.Texture, out var w, out var h, out var bpp);
                var (sdr, sw, sh) = ToSdrBgra(gpu, monitor, pixels, w, h, bpp, crop);
                return sw > 0 && sh > 0 ? (sdr, sw, sh) : null;
            }
            if (Environment.TickCount64 >= until) return null;
            Thread.Sleep(10);
        }
    }

    /// <summary>Captures an open window into a top-down BGRA DIB via PrintWindow(PW_RENDERFULLCONTENT) so occluded pickers don't cover it.</summary>
    internal static unsafe (byte[] Pixels, int Width, int Height)? GrabWindowViaPrint(nint hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd) || !Win32.Shown(hwnd)) return null;
        Win32.RECT wr;
        if (!Win32.GetWindowRect(hwnd, &wr) || wr.Width < 2 || wr.Height < 2) return null;
        var fb = Win32.FrameBounds(hwnd);

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
                Width = wr.Width,
                Height = -wr.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };
            void* bits;
            dib = Win32.CreateDIBSection(screenDc, &bi, 0, &bits, 0, 0);
            if (dib == 0 || bits is null) return null;
            oldObj = Win32.SelectObject(memDc, dib);
            new Span<byte>(bits, wr.Width * wr.Height * 4).Clear();
            if (!Win32.PrintWindow(hwnd, memDc, Win32.PW_RENDERFULLCONTENT)) return null;
            Win32.GdiFlush();
            var raw = new byte[wr.Width * wr.Height * 4];
            Marshal.Copy((nint)bits, raw, 0, raw.Length);
            for (var i = 3; i < raw.Length; i += 4) raw[i] = 255;
            if (fb is { } frame &&
                frame.Left >= wr.Left && frame.Top >= wr.Top &&
                frame.Right <= wr.Right && frame.Bottom <= wr.Bottom &&
                frame.Width >= 2 && frame.Height >= 2 &&
                (frame.Width != wr.Width || frame.Height != wr.Height))
            {
                return (Imaging.Crop(raw, wr.Width, 4, frame.Left - wr.Left, frame.Top - wr.Top, frame.Width, frame.Height), frame.Width, frame.Height);
            }
            return (raw, wr.Width, wr.Height);
        }
        finally
        {
            if (oldObj != 0 && memDc != 0) Win32.SelectObject(memDc, oldObj);
            if (dib != 0) Win32.DeleteObject(dib);
            if (memDc != 0) Win32.DeleteDC(memDc);
            Win32.ReleaseDC(0, screenDc);
        }
    }

    internal static (byte[] Pixels, int Width, int Height) ToSdrBgra(byte[] pixels, int width, int height, int bpp, Win32.RECT? crop, float sdrWhiteNits, float peakNits)
    {
        if (crop is { } c)
        {
            var x = Math.Clamp(c.Left, 0, width);
            var y = Math.Clamp(c.Top, 0, height);
            var cw = Math.Min(c.Width, width - x);
            var ch = Math.Min(c.Height, height - y);
            if (cw <= 0 || ch <= 0) return ([], 0, 0);
            pixels = Imaging.Crop(pixels, width, bpp, x, y, cw, ch);
            width = cw;
            height = ch;
        }
        if (bpp == 8) pixels = Imaging.ToneMap(pixels, width, height, sdrWhiteNits, peakNits);
        return (pixels, width, height);
    }

    private static (byte[] Pixels, int Width, int Height) ToSdrBgra(GpuDevice gpu, nint monitor, byte[] pixels, int width, int height, int bpp, Win32.RECT? crop)
    {
        if (bpp != 8) return ToSdrBgra(pixels, width, height, bpp, crop, 80f, 1000f);
        using var output = DuplicationSource.OutputOf(gpu, monitor, out var desc);
        var peak = desc.MaxLuminance > 0 ? desc.MaxLuminance : 1000f;
        return ToSdrBgra(pixels, width, height, bpp, crop, Imaging.SdrWhiteNits(monitor), peak);
    }

    /// <summary>A window or screen as it is now: Windows capture, else PrintWindow or desktop duplication.</summary>
    internal static (byte[] Pixels, int Width, int Height)? Grab(GpuDevice gpu, nint window, nint monitor, int timeoutMs = ThumbItemTimeoutMs)
    {
        var isWindow = window != 0;
        var at = isWindow ? Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) : monitor;
        if (isWindow && (!Win32.IsWindow(window) || Win32.IsIconic(window) || !Win32.Shown(window))) return null;
        if (isWindow && !WgcSource.Available(out _))
        {
            if (GrabWindowViaPrint(window) is { } printed) return printed;
        }
        using var source = CaptureSources.OpenCaptureSource(gpu, isWindow, isWindow ? window : monitor, cursor: false, TimeSpan.FromMilliseconds(16));
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (source.Latest() is { } frame)
            {
                var pixels = gpu.Read(frame.Texture, out var w, out var h, out var bpp);
                Win32.RECT? crop = frame.Crop is { } c ? new Win32.RECT { Left = c.Left, Top = c.Top, Right = c.Right, Bottom = c.Bottom } : null;
                var (sdr, sw, sh) = ToSdrBgra(gpu, at, pixels, w, h, bpp, crop);
                return sw > 0 && sh > 0 ? (sdr, sw, sh) : null;
            }
            if (Environment.TickCount64 >= until) return null;
            Thread.Sleep(16);
        }
    }
}
