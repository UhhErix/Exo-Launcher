using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using Vortice.DXGI;

namespace ExoCapture.Edit;

/// <summary>A clip refused for a reason Exo names to the person (CaptureShrink.Say, CaptureReel): the reason word and the detail for the log.</summary>
internal sealed class Skip(string reason, string detail) : Exception(detail)
{
    internal string Reason { get; } = reason;
}

/// <summary>
/// What is done to a saved clip: a key-frame trim (no re-encode), the export presets (For Discord under 10 MB, Full
/// quality, Vertical, and a GIF or WebM short clip), a join of several clips, and shrinking a recording. Each reads the
/// clip with Exo Capture's own MP4 reader or Windows' decoder and writes a new file beside it; the original is only read.
/// </summary>
internal static class Clips
{
    internal const long DiscordBytes = 10L * 1024 * 1024;

    // ---------------------------------------------------------------- trim

    /// <summary>The samples of [start, end) ms: video from the key frame at or before the start, sound from that key frame's time.</summary>
    internal static string Trim(string path, double startMs, double endMs, string output)
    {
        var mp4 = Mp4File.Read(path);
        var video = mp4.Video ?? throw new CaptureException("the file has no video");
        var (from, to) = Range(video, startMs, endMs);
        if (to <= from) throw new CaptureException("Nothing to save.");
        var startTicks = video.Ticks(video.Samples[from]);
        var endTicks = to < video.Samples.Count ? video.Ticks(video.Samples[to]) : long.MaxValue;
        var tracks = mp4.Tracks.Select(t => t.Info).ToList();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var writer = new Mp4Writer(output, tracks, fragmented: false);
        try
        {
            for (var t = 0; t < mp4.Tracks.Count; t++)
            {
                var track = mp4.Tracks[t];
                for (var i = 0; i < track.Samples.Count; i++)
                {
                    var s = track.Samples[i];
                    var time = track.Ticks(s);
                    if (track.Info.IsVideo ? i < from || i >= to : time < startTicks || time >= endTicks) continue;
                    writer.Write(t, Mp4File.ReadSample(file, s), time - startTicks, s.Key);
                }
            }
            writer.Finish();
        }
        finally { writer.Dispose(); }
        return output;
    }

    /// <summary>The video samples a trim keeps: from the last key frame at or before the start to the first sample at or after the end (end 0: to the end).</summary>
    internal static (int From, int To) Range(Mp4Track video, double startMs, double endMs)
    {
        var samples = video.Samples;
        var from = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var ms = video.Ticks(samples[i]) / 10_000.0;
            if (ms > startMs + 0.5) break;
            if (samples[i].Key) from = i;
        }
        var to = samples.Count;
        if (endMs > 0)
            for (var i = from; i < samples.Count; i++)
                if (video.Ticks(samples[i]) / 10_000.0 >= endMs) { to = i; break; }
        return (from, to);
    }

    // ---------------------------------------------------------------- export

    internal sealed record ExportPlan(int Width, int Height, int Fps, int Bitrate, RawRect? Crop, string Suffix, Codec Codec);

    /// <summary>
    /// What a preset makes of a clip (its size, rate and bit rate): For Discord fits 10 MB (720p, or 1080p when the rate
    /// allows, 30 fps below 2.5 Mbit/s), Full quality keeps the size at a generous rate, Vertical is the 9:16 middle at 1080×1920.
    /// </summary>
    internal static ExportPlan Plan(string preset, int width, int height, double fps, double seconds)
    {
        var sourceFps = (int)Math.Round(Math.Clamp(fps, 15, 120));
        switch (preset)
        {
            case "discord":
                var bits = DiscordBytes * 8 * 0.92 / Math.Max(0.5, seconds) - 160_000;
                if (bits < 500_000)
                    throw new CaptureException($"Too long for Discord's 10 MB. Trim it to {MaxDiscordSeconds} seconds or less.");
                var tall = bits >= 4_000_000 ? 1080 : 720;
                var (w, h) = FitHeight(width, height, Math.Min(tall, height));
                return new ExportPlan(w, h, bits < 2_500_000 ? Math.Min(30, sourceFps) : Math.Min(60, sourceFps), (int)Math.Min(bits, 12_000_000), null, " for Discord", Codec.H264);
            case "vertical":
                var cropW = Math.Min(width, (int)(height * 9.0 / 16) & ~1);
                var x = ((width - cropW) / 2) & ~1;
                return new ExportPlan(1080, 1920, Math.Min(60, sourceFps), 16_000_000, new RawRect(x, 0, x + cropW, height), " vertical", Codec.H264);
            default:
                return new ExportPlan(width & ~1, height & ~1, sourceFps, Capture.CaptureSession.ClipBitrate(width, height, sourceFps, 85, Codec.H264), null, " full", Codec.H264);
        }
    }

    internal static int MaxDiscordSeconds => (int)(DiscordBytes * 8 * 0.92 / 660_000);

    private static (int W, int H) FitHeight(int width, int height, int tall)
    {
        var h = Math.Max(64, tall & ~1);
        var w = Math.Max(64, (int)Math.Round(width * (double)h / height) & ~1);
        return (w, h);
    }

    /// <summary>Re-encodes [start, end) to H.264 with the preset's plan; the mix track's sound is copied as it is.</summary>
    internal static string Export(GpuDevice gpu, string path, string preset, double startMs, double endMs, string output, CancellationToken ct)
    {
        var mp4 = Mp4File.Read(path);
        var video = mp4.Video ?? throw new CaptureException("the file has no video");
        if (video.Info.Width <= 0 || video.Info.Height <= 0) throw new CaptureException("The video has no picture size.");
        var end = endMs > 0 ? Math.Min(endMs, mp4.DurationMs) : mp4.DurationMs;
        var seconds = (end - startMs) / 1000.0;
        if (seconds <= 0.05) throw new CaptureException("Nothing to export.");
        var fps = video.Samples.Count / Math.Max(0.001, video.DurationMs / 1000.0);
        var plan = Plan(preset, video.Info.Width, video.Info.Height, fps, seconds);
        var mix = mp4.Audio.FirstOrDefault();
        var tracks = new List<TrackInfo> { new("vide", "avc1", [], plan.Width, plan.Height, 90000, "Video") };
        if (mix is not null) tracks.Add(mix.Info);
        var writer = new Mp4Writer(output, tracks, fragmented: false, plan.Fps);
        try
        {
            var start = (long)(startMs * 10_000);
            var stop = (long)(end * 10_000);
            var info = Transcode.Video(gpu, path, start, stop, plan.Width, plan.Height, plan.Crop, plan.Codec, plan.Bitrate, RateMode.Clip, plan.Fps, false,
                (sample, time, key) => writer.Write(0, sample, time, key), null, ct);
            writer.Describe(0, info.Track);
            if (mix is not null) CopyTrack(path, mix, start, stop, (data, time) => writer.Write(1, data, time, true));
            writer.Finish();
        }
        catch { writer.Dispose(); TryDelete(output); throw; }
        writer.Dispose();
        return output;
    }

    /// <summary>A sound track's samples in [start, stop) as they are, at their time from start.</summary>
    internal static void CopyTrack(string path, Mp4Track track, long start, long stop, Action<byte[], long> write)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var s in track.Samples)
        {
            var time = track.Ticks(s);
            if (time < start || time >= stop) continue;
            write(Mp4File.ReadSample(file, s), time - start);
        }
    }

    // ---------------------------------------------------------------- GIF and WebM

    internal sealed record ShortPlan(int Width, int Fps);

    /// <summary>
    /// The size and rate a GIF of these seconds fits Discord's 10 MB in: 480 wide at 15 fps first, then smaller and
    /// slower, from an estimate of about 0.11 bytes a pixel a frame (real game footage after quantizing); the export
    /// checks the file and steps down again if it is still over.
    /// </summary>
    internal static ShortPlan GifPlan(int width, int height, double seconds, long limit = DiscordBytes, int step = 0)
    {
        (int W, int Fps)[] ladder = [(480, 15), (480, 12), (400, 12), (360, 10), (320, 10), (240, 10), (200, 8), (160, 8)];
        for (var i = step; i < ladder.Length; i++)
        {
            var w = Math.Min(width, ladder[i].W) & ~1;
            var h = Math.Max(2, (int)(w * (double)height / width));
            var estimate = w * (double)h * ladder[i].Fps * seconds * 0.11;
            if (estimate <= limit * 0.9) return new ShortPlan(w, ladder[i].Fps);
        }
        throw new CaptureException($"Too long for a GIF under 10 MB. Trim it to {MaxGifSeconds(width, height)} seconds or less.");
    }

    internal static int MaxGifSeconds(int width, int height)
    {
        var w = Math.Min(width, 160);
        var h = Math.Max(2, (int)(w * (double)height / width));
        return Math.Max(1, (int)(DiscordBytes * 0.9 / (w * (double)h * 8 * 0.11)));
    }

    /// <summary>The bit rate a WebM of these seconds fits 10 MB at (VP9, video only), at most 6 Mbit/s.</summary>
    internal static int WebMBitrate(double seconds, long limit = DiscordBytes)
    {
        var bits = limit * 8 * 0.9 / Math.Max(0.5, seconds);
        if (bits < 300_000) throw new CaptureException($"Too long for a WebM under 10 MB. Trim it to {(int)(limit * 8 * 0.9 / 300_000)} seconds or less.");
        return (int)Math.Min(bits, 6_000_000);
    }

    internal static string Gif(GpuDevice gpu, string path, double startMs, double endMs, string output, CancellationToken ct)
    {
        var mp4 = Mp4File.Read(path);
        var video = mp4.Video ?? throw new CaptureException("the file has no video");
        var end = endMs > 0 ? Math.Min(endMs, mp4.DurationMs) : mp4.DurationMs;
        var seconds = (end - startMs) / 1000.0;
        if (seconds <= 0.05) throw new CaptureException("Nothing to export.");
        for (var step = 0; step < 8; step++)
        {
            var plan = GifPlan(video.Info.Width, video.Info.Height, seconds, step: step);
            var h = Math.Max(2, (int)(plan.Width * (double)video.Info.Height / video.Info.Width)) & ~1;
            var temp = output + ".part";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var gif = new GifWriter(file, plan.Width, h))
            {
                var delay = (int)Math.Round(100.0 / plan.Fps);
                Transcode.Pictures(gpu, path, (long)(startMs * 10_000), (long)(end * 10_000), plan.Width, h, plan.Fps, (bgra, _) => gif.Add(bgra, delay), ct);
            }
            if (new FileInfo(temp).Length <= DiscordBytes || step == 7)
            {
                File.Move(temp, output, overwrite: true);
                return output;
            }
            TryDelete(temp);
            Host.Log("GIF: over 10 MB, a smaller one.");
            step = Math.Max(step, 0);
        }
        return output;
    }

    internal static string WebM(GpuDevice gpu, string path, double startMs, double endMs, string output, CancellationToken ct)
    {
        var mp4 = Mp4File.Read(path);
        var video = mp4.Video ?? throw new CaptureException("the file has no video");
        var end = endMs > 0 ? Math.Min(endMs, mp4.DurationMs) : mp4.DurationMs;
        var seconds = (end - startMs) / 1000.0;
        if (seconds <= 0.05) throw new CaptureException("Nothing to export.");
        var bitrate = WebMBitrate(seconds);
        var (w, h) = FitHeight(video.Info.Width, video.Info.Height, Math.Min(bitrate >= 2_500_000 ? 1080 : 720, video.Info.Height));
        var fps = Math.Min(60, (int)Math.Round(video.Samples.Count / Math.Max(0.001, video.DurationMs / 1000.0)));
        if (bitrate < 2_000_000) fps = Math.Min(fps, 30);
        var temp = output + ".part";
        try
        {
            using (var webm = new WebMWriter(temp, w, h))
                Transcode.Video(gpu, path, (long)(startMs * 10_000), (long)(end * 10_000), w, h, null, Codec.Vp9, bitrate, RateMode.Clip, fps, false,
                    (frame, time, key) => webm.Add(frame, time / 10_000, key), null, ct, raw: true);
            File.Move(temp, output, overwrite: true);
            return output;
        }
        catch (CaptureException ex) when (ex.Message is "no-encoder")
        {
            TryDelete(temp);
            throw new CaptureException("This PC has no WebM encoder. Get VP9 Video Extensions from the Microsoft Store, then try again.");
        }
        catch { TryDelete(temp); throw; }
    }

    // ---------------------------------------------------------------- joins

    /// <summary>concat.facts for one clip: whether it reads, and what must match for a join without re-encoding.</summary>
    internal static JsonObject Facts(string path)
    {
        try
        {
            var mp4 = Mp4File.Read(path);
            var video = mp4.Video;
            if (video is null || video.Samples.Count == 0) return new JsonObject { ["ok"] = false, ["detail"] = "a clip has no picture" };
            var fps = video.Samples.Count / Math.Max(0.001, video.DurationMs / 1000.0);
            return new JsonObject
            {
                ["ok"] = true,
                ["signature"] = Signature(mp4),
                ["width"] = video.Info.Width,
                ["height"] = video.Info.Height,
                ["fps"] = Math.Round(fps, 2),
                ["soundTracks"] = mp4.Audio.Count(),
                ["hdr"] = video.Info.Hdr,
                ["durationMs"] = Math.Round(mp4.DurationMs),
            };
        }
        catch (Exception ex) when (ex is IOException or CaptureException or ArgumentException or UnauthorizedAccessException)
        {
            return new JsonObject { ["ok"] = false, ["detail"] = "unreadable" };
        }
    }

    /// <summary>Two clips with one signature join sample for sample: the same codec and parameter sets, size, and sound tracks.</summary>
    internal static string Signature(Mp4File mp4)
    {
        var parts = new List<string>();
        foreach (var t in mp4.Tracks)
        {
            var hash = Convert.ToHexString(SHA256.HashData(t.Info.Config))[..16].ToLowerInvariant();
            parts.Add(t.Info.IsVideo
                ? $"v:{t.Info.Format}:{t.Info.Width}x{t.Info.Height}:{t.Info.Timescale}:{(t.Info.Hdr ? "hdr" : "sdr")}:{hash}"
                : $"a:{t.Info.Format}:{t.Info.Timescale}:{hash}");
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Clips into one, in the order given: "copy" joins the samples (one signature), "reencode" decodes each and encodes
    /// them at one size (the mix track's sound copied). Progress 0 to 100 as it goes; a cancel stops within a frame.
    /// </summary>
    internal static string Concat(GpuDevice gpu, IReadOnlyList<string> paths, string output, string mode, int width, int height, Action<double> progress, CancellationToken ct)
    {
        if (paths.Count < 2) throw new Skip("failed", "Pick at least two clips.");
        var files = paths.Select(Mp4File.Read).ToList();
        if (files.Any(f => f.Video is null)) throw new Skip("unreadable", "a clip has no picture");
        var total = files.Sum(f => f.DurationMs);
        var done = 0.0;
        var temp = output + ".part";
        try
        {
            if (mode == "copy")
            {
                if (files.Select(Signature).Distinct().Count() != 1) throw new Skip("incompatible", "the clips are not of one kind");
                var writer = new Mp4Writer(temp, [.. files[0].Tracks.Select(t => t.Info)], fragmented: false);
                using (writer)
                {
                    long offset = 0;
                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var file = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var video = f.Video!;
                        var start = video.Ticks(video.Samples[0]);
                        for (var t = 0; t < f.Tracks.Count; t++)
                            foreach (var s in f.Tracks[t].Samples)
                                writer.Write(t, Mp4File.ReadSample(file, s), offset + f.Tracks[t].Ticks(s) - start, s.Key);
                        offset += (long)(f.DurationMs * 10_000);
                        done += f.DurationMs;
                        progress(100 * done / total);
                    }
                    writer.Finish();
                }
            }
            else
            {
                var fps = (int)Math.Round(files.Max(f => f.Video!.Samples.Count / Math.Max(0.001, f.Video.DurationMs / 1000.0)));
                var mix = files.All(f => f.Audio.Any());
                var tracks = new List<TrackInfo> { new("vide", "avc1", [], width & ~1, height & ~1, 90000, "Video") };
                if (mix) tracks.Add(files[0].Audio.First().Info);
                var writer = new Mp4Writer(temp, tracks, fragmented: false, fps);
                using (writer)
                {
                    long offset = 0;
                    TrackInfo? described = null;
                    var bitrate = Capture.CaptureSession.ClipBitrate(width, height, fps, 70, Codec.H264);
                    foreach (var f in files)
                    {
                        var length = (long)(f.DurationMs * 10_000);
                        var at = offset;
                        var result = Transcode.Video(gpu, f.Path, 0, length, width & ~1, height & ~1, null, Codec.H264, bitrate, RateMode.Clip, fps, false,
                            (sample, time, key) => writer.Write(0, sample, at + time, key), p => progress(100 * (done + p * f.DurationMs) / total), ct);
                        described ??= result.Track;
                        if (mix) CopyTrack(f.Path, f.Audio.First(), 0, length, (data, time) => writer.Write(1, data, at + time, true));
                        offset += length;
                        done += f.DurationMs;
                    }
                    if (described is not null) writer.Describe(0, described);
                    writer.Finish();
                }
            }
            File.Move(temp, output, overwrite: true);
            return output;
        }
        catch { TryDelete(temp); throw; }
    }

    // ---------------------------------------------------------------- shrink

    internal const double NeedMean = 0.985, NeedLowest = 0.95;

    /// <summary>
    /// A recording re-encoded smaller by the card's careful encoder (AV1, else HEVC, else H.264), its sound tracks copied
    /// byte for byte, then checked before it is offered: every track there, the same length and frame count, the same
    /// HDR, the sound identical, and SSIM on 24 frames against the original. The original is never touched here.
    /// </summary>
    internal static JsonObject Shrink(GpuDevice gpu, string input, int targetQuality, string? recordingPath, CancellationToken ct)
    {
        var output = Path.ChangeExtension(input, null) + ".shrink.mp4";
        if (!File.Exists(input) || new FileInfo(input).Length == 0) throw new Skip("not-found", "the file is missing or empty");
        if (input.EndsWith(".shrink.mp4", StringComparison.OrdinalIgnoreCase)) throw new Skip("already-shrunk", "this is a shrunk file");
        if (recordingPath is not null && string.Equals(Path.GetFullPath(recordingPath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            throw new Skip("recording-in-progress", "the recording is still open or waits to be recovered");
        if (File.Exists(output)) throw new Skip("output-exists", "the output file exists");
        Mp4File mp4;
        try { mp4 = Mp4File.Read(input); } catch (Exception ex) { throw new Skip("source-unreadable", ex.Message); }
        if (mp4.Fragmented) throw new Skip("recording-in-progress", "the recording is still open or waits to be recovered");
        var video = mp4.Video ?? throw new Skip("unsupported-video", "the file has no video");
        if (Bitstream.CodecOf(video.Info.Format) is not { } sourceCodec) throw new Skip("unsupported-video", "the video is not H.264, HEVC or AV1");
        if (video.Info.Width % 2 != 0 || video.Info.Height % 2 != 0 || video.Info.Width == 0) throw new Skip("unsupported-video", "the picture size is odd or missing");
        if (mp4.Audio.Any(a => a.Info.Format != "mp4a")) throw new Skip("unsupported-tracks", "a sound track cannot be copied as it is");
        var seconds = video.DurationMs / 1000.0;
        if (seconds <= 0) throw new Skip("source-unreadable", "the file has no duration");
        var codec = new[] { Codec.Av1, Codec.Hevc, Codec.H264 }.FirstOrDefault(c => VideoEncoder.Playable(c) && VideoEncoder.Find(c, gpu.Luid, true).Count > 0, (Codec)(-1));
        if ((int)codec < 0) throw new Skip("no-encoder", "no hardware video encoder");
        if (Rank(codec) < Rank(sourceCodec)) throw new Skip("no-better-encoder", "no encoder here beats the recording's own");
        if (video.Info.Hdr && codec == Codec.H264) throw new Skip("hdr-unsupported", "10-bit H.264 has no hardware encoder to go back to");
        var videoBits = video.Samples.Sum(s => (long)s.Size) * 8 / seconds;
        var ratio = targetQuality >= 90 ? 0.5 : 0.33;
        var bitrate = (int)Math.Clamp(videoBits * ratio * (codec == sourceCodec ? 1.0 : 0.9), 400_000, 80_000_000);
        if (videoBits < 1_500_000) throw new Skip("not-smaller", "the recording is already small");
        var fps = (int)Math.Round(video.Samples.Count / seconds);
        var temp = output + ".part";
        try
        {
            var writer = new Mp4Writer(temp, [.. mp4.Tracks.Select(t => t.Info)], fragmented: false, fps);
            using (writer)
            {
                var result = Transcode.Video(gpu, input, 0, long.MaxValue, video.Info.Width, video.Info.Height, null, codec, bitrate, RateMode.Careful, 0, video.Info.Hdr,
                    (sample, time, key) => writer.Write(0, sample, time, key), null, ct, frameTimes: true);
                writer.Describe(0, result.Track with { Name = video.Info.Name.Length > 0 ? video.Info.Name : "Video" });
                var start = video.Ticks(video.Samples[0]);
                for (var t = 0; t < mp4.Tracks.Count; t++)
                    if (!mp4.Tracks[t].Info.IsVideo)
                    {
                        var index = t;
                        CopyTrack(input, mp4.Tracks[t], start, long.MaxValue, (data, time) => writer.Write(index, data, time, true));
                    }
                writer.Finish();
            }
            var check = Verify(gpu, input, temp, ct);
            var before = new FileInfo(input).Length;
            var after = new FileInfo(temp).Length;
            if (after >= before) throw new Skip("not-smaller", $"the new file is {after} bytes, not under {before}");
            if (check.Mean < NeedMean || check.Lowest < NeedLowest)
                throw new Skip("quality-loss", $"SSIM {check.Mean:0.0000} mean and {check.Lowest:0.0000} lowest of {check.Frames} frames; {NeedMean:0.000} and {NeedLowest:0.00} are needed");
            File.Move(temp, output);
            Host.Log($"Shrink: {before} to {after} bytes ({after * 100.0 / before:0}%), SSIM {check.Mean:0.0000} mean, {check.Lowest:0.0000} lowest.");
            return new JsonObject
            {
                ["ok"] = true,
                ["output"] = output,
                ["inputBytes"] = before,
                ["outputBytes"] = after,
                ["ratio"] = Math.Round(after / (double)before, 4),
                ["ssim"] = new JsonObject { ["mean"] = Math.Round(check.Mean, 4), ["lowest"] = Math.Round(check.Lowest, 4), ["frames"] = check.Frames },
            };
        }
        catch { TryDelete(temp); throw; }
    }

    private static int Rank(Codec c) => c switch { Codec.Av1 => 3, Codec.Hevc => 2, _ => 1 };

    internal readonly record struct Check(double Mean, double Lowest, int Frames);

    /// <summary>The shrunk file against its original: tracks, length, frames, HDR and sound, then the picture.</summary>
    internal static Check Verify(GpuDevice gpu, string original, string candidate, CancellationToken ct)
    {
        var a = Mp4File.Read(original);
        var b = Mp4File.Read(candidate);
        if (a.Tracks.Count != b.Tracks.Count) throw new Skip("audio-tracks", $"{a.Tracks.Count} tracks became {b.Tracks.Count}");
        var va = a.Video!;
        var vb = b.Video ?? throw new Skip("output-unreadable", "the new file has no video");
        if (va.Samples.Count != vb.Samples.Count) throw new Skip("frame-time", $"{va.Samples.Count} frames became {vb.Samples.Count}");
        if (Math.Abs(va.DurationMs - vb.DurationMs) > 1000.0 / 24) throw new Skip("duration", $"{va.DurationMs:0} ms became {vb.DurationMs:0} ms");
        if (va.Info.Hdr != vb.Info.Hdr) throw new Skip("color-mismatch", "the HDR settings changed");
        if (va.Info.Width != vb.Info.Width || va.Info.Height != vb.Info.Height) throw new Skip("picture-size", "the picture size changed");
        using (var fa = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var fb = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            foreach (var (ta, tb) in a.Audio.Zip(b.Audio))
            {
                if (ta.Samples.Count != tb.Samples.Count) throw new Skip("audio-changed", "a sound track's packets differ");
                for (var i = 0; i < ta.Samples.Count; i++)
                    if (!Mp4File.ReadSample(fa, ta.Samples[i]).AsSpan().SequenceEqual(Mp4File.ReadSample(fb, tb.Samples[i])))
                        throw new Skip("audio-changed", "a sound track's packets differ");
            }
        // 24 frames across the clip, compared at 640 wide on luma.
        var count = va.Samples.Count;
        var picks = new HashSet<int>(Enumerable.Range(0, 24).Select(i => (int)((i + 0.5) * count / 24)));
        var w = Math.Min(640, va.Info.Width) & ~1;
        var h = Math.Max(2, (int)(w * (double)va.Info.Height / va.Info.Width)) & ~1;
        var left = Transcode.Frames(gpu, original, w, h, picks, ct);
        var right = Transcode.Frames(gpu, candidate, w, h, picks, ct);
        var scores = new List<double>();
        foreach (var (index, pixels) in left)
            if (right.TryGetValue(index, out var other)) scores.Add(Ssim.Compare(pixels, other, w, h));
        if (scores.Count < 12) throw new Skip("decode-error", $"only {scores.Count} frames could be compared");
        return new Check(scores.Average(), scores.Min(), scores.Count);
    }

    // ---------------------------------------------------------------- thumbnails

    /// <summary>A video's first picture as the JPEG the Captures sheet shows (480 wide).</summary>
    internal static void Thumbnail(GpuDevice gpu, string path, string thumbPath)
    {
        var mp4 = Mp4File.Read(path);
        var video = mp4.Video;
        if (video is null || video.Info.Width == 0) return;
        var w = Math.Min(480, video.Info.Width) & ~1;
        var h = Math.Max(2, (int)(w * (double)video.Info.Height / video.Info.Width)) & ~1;
        var frames = Transcode.Frames(gpu, path, w, h, new HashSet<int> { 0 }, CancellationToken.None);
        if (frames.TryGetValue(0, out var pixels))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
            Imaging.SaveJpeg(thumbPath, pixels, w, h);
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>SSIM on luma, over 8×8 blocks: how alike two pictures look (1 is the same).</summary>
internal static class Ssim
{
    internal static double Compare(byte[] a, byte[] b, int width, int height)
    {
        const double c1 = 6.5025, c2 = 58.5225; // (0.01·255)², (0.03·255)²
        double total = 0;
        var blocks = 0;
        for (var by = 0; by + 8 <= height; by += 8)
            for (var bx = 0; bx + 8 <= width; bx += 8)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        var i = ((by + y) * width + bx + x) * 4;
                        var la = Luma(a, i);
                        var lb = Luma(b, i);
                        sa += la; sb += lb; saa += la * la; sbb += lb * lb; sab += la * lb;
                    }
                const int n = 64;
                var ma = sa / n;
                var mb = sb / n;
                var va = saa / n - ma * ma;
                var vb = sbb / n - mb * mb;
                var cov = sab / n - ma * mb;
                total += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                blocks++;
            }
        return blocks == 0 ? 1 : total / blocks;
    }

    private static double Luma(byte[] bgra, int i) => 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
}
