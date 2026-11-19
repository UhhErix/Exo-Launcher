using ExoCapture.Audio;
using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using ExoCapture.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExoCapture.Capture;

/// <summary>What a session encodes and how.</summary>
internal sealed record SessionOptions(int Fps, int Quality, bool Hdr, RateMode Mode, int? Bitrate = null, int? Width = null, int? Height = null,
    Codec[]? Codecs = null, bool Sound = true, int GopSeconds = 1);

/// <summary>
/// A running capture: pictures from a source on the session's own clock (each tick the newest picture, evenly spaced; a
/// slower source's picture repeats), through the video processor into the card's encoder, with the sound tracks beside
/// it. Every packet goes to the session's listeners (instant replay, a recording, Go Live).
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    internal readonly GpuDevice Gpu;
    internal readonly SessionOptions Options;
    internal readonly long Epoch = Win32.Now();
    private IFrameSource _source;
    private readonly VideoConverter _converter;
    private readonly ID3D11Texture2D _last;
    private VideoEncoder _encoder;
    internal readonly SoundMixer? Sound;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _forceKey = true;
    private readonly Lock _listenerGate = new();
    private Action<Packet>[] _listeners = [];
    private Action<byte[], long, bool>[] _rawListeners = [];
    private byte[][]? _parameterSets;
    private TrackInfo? _videoTrack;

    internal int Width { get; }
    internal int Height { get; }
    internal int Fps => Options.Fps;
    internal bool Hdr { get; }
    internal Codec Codec => _encoder.Choice.Codec;
    internal string EncoderName => _encoder.Choice.Name;
    internal int Bitrate => _encoder.Bitrate;
    internal string SourceKind => _source.Kind;
    internal long Frames, Dropped;
    /// <summary>Why it stopped on its own, or null.</summary>
    internal string? Ended { get; private set; }
    /// <summary>When false (the game is not in front), no picture is kept.</summary>
    internal Func<bool>? Gate { get; set; }
    /// <summary>When the source gives nothing for this long, <see cref="Rescue"/> is asked for another.</summary>
    internal Func<IFrameSource, IFrameSource?>? Rescue { get; set; }
    internal event Action<string>? Stopped;
    /// <summary>The newest converted picture's source texture, for a screenshot of what the clip sees (on the capture thread's device).</summary>
    private ID3D11Texture2D? _sourceCopy;
    private readonly Lock _shotGate = new();

    internal CaptureSession(GpuDevice gpu, IFrameSource source, SessionOptions options)
    {
        Gpu = gpu;
        Options = options;
        _source = source;
        var (w, h) = (options.Width ?? source.Width, options.Height ?? source.Height);
        // Within what every encoder takes: 4096 a side, even sides.
        var scale = Math.Min(1.0, Math.Min(4096.0 / w, 4096.0 / h));
        Width = Math.Max(64, (int)(w * scale) & ~1);
        Height = Math.Max(64, (int)(h * scale) & ~1);
        var codecs = options.Codecs ?? [Codec.Av1, Codec.Hevc, Codec.H264];
        Hdr = options.Hdr && source.Hdr;
        _encoder = OpenEncoder(codecs, Hdr);
        if (Hdr && _encoder.Choice.Codec == Codec.H264) Hdr = false;
        var inFormat = source.Hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        _converter = new VideoConverter(gpu, source.Width, source.Height, inFormat, Width, Height, Hdr ? Format.P010 : Format.NV12, Hdr);
        _last = gpu.Texture(Width, Height, Hdr ? Format.P010 : Format.NV12);
        if (options.Sound) Sound = new SoundMixer(Epoch, (track, aac, time) => Deliver(new Packet(track, aac, time, true)));
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo Capture clock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The bit rate a clip gets: about 0.1 bit a pixel at quality 50 in HEVC, doubling every 25 points.</summary>
    internal static int ClipBitrate(int width, int height, int fps, int quality, Codec codec)
    {
        var perPixel = 0.1 * Math.Pow(2, (Math.Clamp(quality, 0, 100) - 50) / 25.0) * (codec switch { Codec.H264 => 1.5, Codec.Av1 => 0.8, _ => 1.0 });
        return (int)Math.Clamp(width * (double)height * fps * perPixel, 2_000_000, 150_000_000);
    }

    private VideoEncoder OpenEncoder(Codec[] codecs, bool hdr)
    {
        var errors = new List<string>();
        foreach (var hardware in new[] { true, false })
            foreach (var codec in codecs)
            {
                if (!hardware && codec != Codec.H264) continue;
                if (!VideoEncoder.Playable(codec)) continue;
                foreach (var choice in VideoEncoder.Find(codec, Gpu.Luid, hardware))
                {
                    var tenBit = hdr && codec != Codec.H264 && hardware;
                    var w = hardware ? Width : Math.Min(Width, 1280) & ~1;
                    if (w != Width) continue; // the processor's encoder only takes what fits it; Go Live sizes for it
                    var bitrate = Options.Bitrate ?? ClipBitrate(Width, Height, Options.Fps, Options.Quality, codec);
                    try { return new VideoEncoder(Gpu, choice, Width, Height, Options.Fps, bitrate, Options.Mode, tenBit, OnEncoded, Options.GopSeconds); }
                    catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or CaptureException)
                    {
                        errors.Add(choice.Name + ": " + ex.Message);
                    }
                }
            }
        foreach (var e in errors) Host.Log("Encoder: " + e);
        throw new CaptureException(errors.Count == 0 ? "no hardware video encoder" : "start the video encoder");
    }

    internal void AddListener(Action<Packet> listener)
    {
        lock (_listenerGate) _listeners = [.. _listeners, listener];
        _forceKey = true;
    }

    internal void RemoveListener(Action<Packet> listener)
    {
        lock (_listenerGate) _listeners = [.. _listeners.Where(l => l != listener)];
    }

    /// <summary>The encoder's own output (Annex B or OBUs), as Go Live sends it.</summary>
    internal void AddRawListener(Action<byte[], long, bool> listener)
    {
        lock (_listenerGate) _rawListeners = [.. _rawListeners, listener];
        _forceKey = true;
    }

    internal void RemoveRawListener(Action<byte[], long, bool> listener)
    {
        lock (_listenerGate) _rawListeners = [.. _rawListeners.Where(l => l != listener)];
    }

    internal void RequestKeyFrame() => _forceKey = true;

    internal void SetBitrate(int bitrate) => _encoder.SetBitrate(bitrate);

    /// <summary>The MP4 description of the video track, once the encoder has named its parameter sets.</summary>
    internal TrackInfo? VideoTrack => _videoTrack;

    internal static IReadOnlyList<TrackInfo> SoundTracks() =>
        [.. Tracks.Names.Select(n => new TrackInfo("soun", "mp4a", Bitstream.AacConfig, 0, 0, 48000, n))];

    private void OnEncoded(byte[] data, long time, bool key)
    {
        if (key && _videoTrack is null) Describe(data);
        Action<Packet>[] listeners;
        Action<byte[], long, bool>[] raw;
        lock (_listenerGate) (listeners, raw) = (_listeners, _rawListeners);
        foreach (var r in raw) r(data, time, key);
        if (listeners.Length == 0 || _videoTrack is null) return;
        var packet = new Packet(Packet.Video, Bitstream.ToSample(Codec, data), time, key);
        foreach (var l in listeners) l(packet);
    }

    private void Describe(byte[] keyFrame)
    {
        try
        {
            _videoTrack = Bitstream.Track(Codec, _encoder.SequenceHeader, keyFrame, Width, Height, Hdr);
            if (_videoTrack is null) return;
            var stream = _encoder.SequenceHeader is { } h ? [.. h, .. keyFrame] : keyFrame;
            var sets = Bitstream.ParameterSets(Codec, stream);
            _parameterSets = Codec == Codec.H264 ? [sets[7], sets[8]] : Codec == Codec.Hevc ? [sets[32], sets[33], sets[34]] : null;
        }
        catch (Exception ex) { Host.Log("Video type: " + ex.Message); }
    }

    private void Deliver(Packet packet)
    {
        Action<Packet>[] listeners;
        lock (_listenerGate) listeners = _listeners;
        foreach (var l in listeners) l(packet);
    }

    private unsafe void Run()
    {
        var timer = Win32.CreateWaitableTimerEx(0, 0, Win32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win32.TIMER_ALL_ACCESS);
        var interval = 10_000_000L / Options.Fps;
        var next = Win32.Now();
        var have = false;
        var lastFrame = Win32.Now();
        long lastTime = -1;
        try
        {
            while (!_stop)
            {
                var wait = next - Win32.Now();
                if (wait > 0)
                {
                    var due = -wait;
                    Win32.SetWaitableTimer(timer, &due, 0, 0, 0, false);
                    Win32.WaitForSingleObject(timer, 100);
                }
                var tick = next;
                next += interval;
                if (Win32.Now() - next > interval * 3) next = Win32.Now();

                Frame? frame;
                try { frame = _source.Latest(); }
                catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or CaptureException) { frame = null; Host.Log("Frame: " + ex.Message); }
                if (_source.Ended is { } ended)
                {
                    Ended = ended;
                    Stopped?.Invoke(ended);
                    return;
                }
                if (frame is { } f)
                {
                    if (f.Time != lastTime) { lastFrame = Win32.Now(); lastTime = f.Time; }
                    try
                    {
                        _converter.Convert(f.Texture, _last, f.Crop, f.Flip);
                        if (_wantShot) lock (_shotGate) { KeepForShot(f); _wantShot = false; _shotReady.Set(); }
                        have = true;
                    }
                    catch (SharpGen.Runtime.SharpGenException ex) { Host.Log("Frame: " + ex.Message); }
                }
                if (Rescue is not null && Win32.Now() - lastFrame > 50_000_000)
                {
                    lastFrame = Win32.Now();
                    if (Rescue(_source) is { } replacement) { _source.Dispose(); _source = replacement; }
                }
                if (!have || Gate?.Invoke() == false) continue;
                Gpu.Context.CopyResource(_encoder.NextInput(), _last);
                var key = _forceKey;
                if (_encoder.Encode(tick, interval, key))
                {
                    if (key) _forceKey = false;
                    Interlocked.Increment(ref Frames);
                }
                else Interlocked.Increment(ref Dropped);
                if (_encoder.Failed is { } failed)
                {
                    Ended = failed;
                    Stopped?.Invoke(failed);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Ended = "Capture stopped: " + ex.Message;
            Stopped?.Invoke(Ended);
        }
        finally { Win32.CloseHandle(timer); }
    }

    private volatile bool _wantShot;
    private readonly ManualResetEventSlim _shotReady = new(false);
    private ID3D11Texture2D? _shotSource;

    /// <summary>Asks the clock for a copy of its next picture; false when none came within a second.</summary>
    private bool Shoot()
    {
        _shotReady.Reset();
        _wantShot = true;
        return _shotReady.Wait(1000);
    }
    private RawRect? _shotCrop;
    private bool _shotFlip;

    private void KeepForShot(Frame f)
    {
        var desc = f.Texture.Description;
        if (_sourceCopy is null || _sourceCopy.Description.Width != desc.Width || _sourceCopy.Description.Height != desc.Height || _sourceCopy.Description.Format != desc.Format)
        {
            _sourceCopy?.Dispose();
            _sourceCopy = Gpu.Texture((int)desc.Width, (int)desc.Height, desc.Format);
        }
        Gpu.Context.CopyResource(_sourceCopy, f.Texture);
        (_shotSource, _shotCrop, _shotFlip) = (_sourceCopy, f.Crop, f.Flip);
    }

    /// <summary>The picture the session sees now, full size (BGRA, or FP16 for an HDR source), for a screenshot.</summary>
    internal (byte[] Pixels, int Width, int Height, bool Hdr)? Picture()
    {
        if (!Shoot()) return null;
        lock (_shotGate)
        {
            if (_shotSource is null) return null;
            var pixels = Gpu.Read(_shotSource, out var w, out var h, out var bpp);
            var crop = _shotCrop ?? new RawRect(0, 0, w, h);
            var cw = crop.Right - crop.Left;
            var ch = crop.Bottom - crop.Top;
            var outPixels = new byte[cw * ch * bpp];
            for (var y = 0; y < ch; y++)
            {
                var srcY = _shotFlip ? h - 1 - (crop.Top + y) : crop.Top + y;
                Buffer.BlockCopy(pixels, (srcY * w + crop.Left) * bpp, outPixels, y * cw * bpp, cw * bpp);
            }
            return (outPixels, cw, ch, bpp == 8);
        }
    }

    /// <summary>The newest picture as BGRA at most <paramref name="width"/> across (Go Live's preview).</summary>
    internal (byte[] Pixels, int Width, int Height)? Preview(int width)
    {
        if (!Shoot()) return null;
        lock (_shotGate)
        {
            if (_shotSource is null) return null;
            var desc = _shotSource.Description;
            var w = Math.Max(16, Math.Min(width, Width)) & ~1;
            var h = Math.Max(16, (int)(w * (double)Height / Width)) & ~1;
            using var target = Gpu.Texture(w, h, Format.B8G8R8A8_UNorm);
            using var convert = new VideoConverter(Gpu, (int)desc.Width, (int)desc.Height, desc.Format, w, h, Format.B8G8R8A8_UNorm, false);
            convert.Convert(_shotSource, target, _shotCrop, _shotFlip);
            return (Gpu.Read(target, out _, out _, out _), w, h);
        }
    }

    /// <summary>The parameter sets for Annex B key frames (H.264: SPS, PPS; HEVC: VPS, SPS, PPS).</summary>
    internal byte[][]? ParameterSets => _parameterSets;

    public void Dispose()
    {
        _stop = true;
        _thread.Join(3000);
        Sound?.Dispose();
        _encoder.Dispose();
        _converter.Dispose();
        _last.Dispose();
        _sourceCopy?.Dispose();
        _source.Dispose();
    }
}
