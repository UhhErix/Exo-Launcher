using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using Vortice.DXGI;

namespace ExoCapture.Edit;

/// <summary>Decode, convert, encode: a clip's video read by Windows' decoder, through the video processor, into an encoder or into pictures.</summary>
internal static class Transcode
{
    internal sealed record Result(TrackInfo Track, int Frames);

    /// <summary>
    /// The video of [start, stop) re-encoded: every frame (or one per <paramref name="fps"/>), at the size and crop given,
    /// times from <paramref name="start"/>. <paramref name="output"/> gets MP4 samples, or the encoder's own frames when
    /// <paramref name="raw"/> (WebM).
    /// </summary>
    internal static Result Video(GpuDevice gpu, string path, long start, long stop, int width, int height, RawRect? crop, Codec codec, int bitrate, RateMode mode, int fps,
        bool tenBit, Action<byte[], long, bool> output, Action<double>? progress, CancellationToken ct, bool raw = false, bool frameTimes = false)
    {
        var source = Mp4File.Read(path).Video ?? throw new CaptureException("the file has no video");
        using var decoder = new VideoDecoder(gpu, path);
        var sourceFps = decoder.Fps > 0 ? decoder.Fps : 60;
        var outFps = fps > 0 ? (int)Math.Min(fps, Math.Round(sourceFps)) : (int)Math.Round(sourceFps);
        var choices = codec == Codec.Vp9 ? VideoEncoder.Find(codec, gpu.Luid, false) : [.. VideoEncoder.Find(codec, gpu.Luid, true), .. VideoEncoder.Find(codec, gpu.Luid, false)];
        if (choices.Count == 0) throw new CaptureException("no-encoder");
        TrackInfo? track = null;
        var outFormat = tenBit ? Format.P010 : Format.NV12;
        VideoEncoder? encoder = null;
        foreach (var choice in choices)
        {
            try
            {
                VideoEncoder? self = null;
                self = new VideoEncoder(gpu, choice, width, height, outFps, bitrate, mode, tenBit && choice.Hardware, (data, time, key) =>
                {
                    if (key && track is null && codec != Codec.Vp9) track = Bitstream.Track(codec, self?.SequenceHeader, data, width, height, tenBit);
                    output(raw ? data : Bitstream.ToSample(codec, data), time, key);
                }, gopSeconds: 2);
                encoder = self;
                break;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or CaptureException) { Host.Log("Encoder " + choice.Name + ": " + ex.Message); }
        }
        if (encoder is null) throw new CaptureException("start the video encoder");
        var sourceCrop = crop ?? new RawRect(0, 0, source.Info.Width, source.Info.Height);
        using var converter = new VideoConverter(gpu, decoder.Width, decoder.Height, decoder.TenBit ? Format.P010 : Format.NV12, width, height, outFormat, tenBit);
        var frames = 0;
        try
        {
            if (start > 0) decoder.Seek(start);
            var interval = 10_000_000L / outFps;
            var next = start;
            var length = Math.Max(1, Math.Min(stop, decoder.DurationTicks > 0 ? decoder.DurationTicks : stop) - start);
            while (decoder.Next() is { } frame)
            {
                ct.ThrowIfCancellationRequested();
                if (frame.Time >= stop) break;
                if (frame.Time < start - 5_000) continue;
                if (!frameTimes && frame.Time < next - interval / 2) continue;
                next = Math.Max(next + interval, frame.Time + interval / 2);
                converter.Convert(frame.Texture, encoder.NextInput(), sourceCrop, slice: frame.Slice, fill: crop is not null);
                var time = frameTimes ? frame.Time - start : (long)frames * interval;
                var until = Environment.TickCount64 + 5000;
                while (!encoder.Encode(Math.Max(0, time), interval, frames == 0))
                {
                    if (encoder.Failed is { } failed) throw new CaptureException(failed);
                    if (Environment.TickCount64 > until) throw new CaptureException("encoder stalled");
                    ct.ThrowIfCancellationRequested();
                }
                frames++;
                progress?.Invoke(Math.Clamp((frame.Time - start) / (double)length, 0, 1));
            }
        }
        finally { encoder.Dispose(); }
        if (frames == 0) throw new CaptureException("nothing was encoded");
        track ??= new TrackInfo("vide", "vp09", [], width, height, 90000, "Video");
        if (track.Config.Length == 0 && codec != Codec.Vp9) throw new CaptureException("the encoder gave no picture");
        return new Result(track, frames);
    }

    /// <summary>The pictures of [start, stop) as BGRA at the size given, one per <paramref name="fps"/>, in order (a GIF).</summary>
    internal static void Pictures(GpuDevice gpu, string path, long start, long stop, int width, int height, int fps, Action<byte[], long> picture, CancellationToken ct)
    {
        var source = Mp4File.Read(path).Video ?? throw new CaptureException("the file has no video");
        using var decoder = new VideoDecoder(gpu, path);
        using var target = gpu.Texture(width, height, Format.B8G8R8A8_UNorm);
        using var converter = new VideoConverter(gpu, decoder.Width, decoder.Height, decoder.TenBit ? Format.P010 : Format.NV12, width, height, Format.B8G8R8A8_UNorm, false);
        var crop = new RawRect(0, 0, source.Info.Width, source.Info.Height);
        if (start > 0) decoder.Seek(start);
        var interval = 10_000_000L / Math.Max(1, fps);
        var next = start;
        while (decoder.Next() is { } frame)
        {
            ct.ThrowIfCancellationRequested();
            if (frame.Time >= stop) break;
            if (frame.Time < next - interval / 2) continue;
            next += interval;
            converter.Convert(frame.Texture, target, crop, slice: frame.Slice);
            picture(gpu.Read(target, out _, out _, out _), frame.Time - start);
        }
    }

    /// <summary>Chosen frames (by their place in the clip) as BGRA at the size given.</summary>
    internal static Dictionary<int, byte[]> Frames(GpuDevice gpu, string path, int width, int height, IReadOnlySet<int> picks, CancellationToken ct)
    {
        var source = Mp4File.Read(path).Video ?? throw new CaptureException("the file has no video");
        var found = new Dictionary<int, byte[]>();
        using var decoder = new VideoDecoder(gpu, path);
        using var target = gpu.Texture(width, height, Format.B8G8R8A8_UNorm);
        using var converter = new VideoConverter(gpu, decoder.Width, decoder.Height, decoder.TenBit ? Format.P010 : Format.NV12, width, height, Format.B8G8R8A8_UNorm, false);
        var crop = new RawRect(0, 0, source.Info.Width, source.Info.Height);
        var last = picks.Count == 0 ? -1 : picks.Max();
        var index = 0;
        while (index <= last && decoder.Next() is { } frame)
        {
            ct.ThrowIfCancellationRequested();
            if (picks.Contains(index))
            {
                converter.Convert(frame.Texture, target, crop, slice: frame.Slice);
                found[index] = gpu.Read(target, out _, out _, out _);
            }
            index++;
        }
        return found;
    }
}
