using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ExoCapture.Capture;
using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using ExoCapture.Native;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>
    /// Starts the stream, stepping down when the card's encoder refuses: the settings asked, then 60 and 30 fps, then
    /// 1080p, then Windows' own H.264 encoder on the processor at up to 720p 30.
    /// </summary>
    internal static GoLive Start(long id, JsonNode p, Func<GpuDevice> gpuFor, Func<GpuDevice, IFrameSource> source)
    {
        var pipe = (nint)p.Long("pipe", 0);
        if (pipe == 0) throw new CaptureException("Exo gave no pipe for the stream.");
        var width = p.Int("width", 1920);
        var height = p.Int("height", 1080);
        var fps = Math.Clamp(p.Int("fps", 60), 5, 120);
        var bitrate = Math.Clamp(p.Int("bitrate", 8_000_000), 300_000, 80_000_000);
        var codec = p.Str("codec") == "hevc" ? Codec.Hevc : Codec.H264;
        var gpu = gpuFor();
        var steps = new List<(int W, int H, int Fps)> { (width, height, fps) };
        if (fps > 60) steps.Add((width, height, 60));
        if (fps > 30) steps.Add((width, height, 30));
        if (height > 1080) steps.Add(((int)(width * 1080.0 / height) & ~1, 1080, Math.Min(fps, 60)));
        steps.Add(((int)(width * Math.Min(1, 720.0 / height)) & ~1, Math.Min(height, 720), Math.Min(fps, 30)));
        Exception? last = null;
        foreach (var (w, h, f) in steps)
        {
            IFrameSource? src = null;
            try
            {
                src = source(gpu);
                var options = new SessionOptions(f, 50, false, RateMode.Stream, bitrate, w, h, [codec, Codec.H264], Sound: false, GopSeconds: 4);
                var session = new CaptureSession(gpu, src, options);
                return new GoLive(id, session, pipe);
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

    /// <summary>One picture of each window or screen (Go Live's picker), as base64 JPEG at most <paramref name="width"/> across.</summary>
    internal static JsonObject Thumbs(Func<nint, GpuDevice> gpuFor, JsonArray items, int width)
    {
        var result = new JsonObject();
        foreach (var item in items)
        {
            var id = item.Str("id");
            if (id is null) continue;
            try
            {
                var window = (nint)item.Long("window", 0);
                var monitor = (nint)item.Long("monitor", 0);
                var at = window != 0 ? Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) : monitor;
                if (Grab(gpuFor(at), window, monitor) is { } picture)
                {
                    var (pixels, w, h) = Imaging.Shrink(picture.Pixels, picture.Width, picture.Height, Math.Clamp(width, 64, 1920));
                    result[id] = Convert.ToBase64String(Imaging.Jpeg(pixels, w, h, 0.75f));
                }
            }
            catch (Exception ex) when (ex is CaptureException or SharpGen.Runtime.SharpGenException) { Host.Log("Go Live picture: " + ex.Message); }
        }
        return result;
    }

    /// <summary>A window or screen as it is now: Windows capture, else the screen's own copy (a window then only while it shows).</summary>
    internal static (byte[] Pixels, int Width, int Height)? Grab(GpuDevice gpu, nint window, nint monitor)
    {
        IFrameSource? source = null;
        try
        {
            if (WgcSource.Available(out _))
                source = window != 0 ? WgcSource.ForWindow(gpu, window, false, true, "(window)", TimeSpan.FromMilliseconds(16))
                    : WgcSource.ForMonitor(gpu, monitor, false, true, "(screen)", TimeSpan.FromMilliseconds(16));
            else
            {
                if (window != 0 && (!Win32.Shown(window))) return null;
                var m = window != 0 ? Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) : monitor;
                source = new DuplicationSource(gpu, m, window, false, "(screen)");
            }
            var until = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < until)
            {
                if (source.Latest() is { } frame)
                {
                    var pixels = gpu.Read(frame.Texture, out var w, out var h, out var bpp);
                    if (frame.Crop is { } c)
                        return (Imaging.Crop(pixels, w, bpp, c.Left, c.Top, c.Right - c.Left, c.Bottom - c.Top), c.Right - c.Left, c.Bottom - c.Top);
                    return (pixels, w, h);
                }
                Thread.Sleep(16);
            }
            return null;
        }
        finally { source?.Dispose(); }
    }
}
