using ExoCapture.Edit;
using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;
using Vortice.DXGI;

namespace ExoCapture.Tests;

/// <summary>
/// Exo Capture's MP4 muxer. Ways it could fail: a sample's offset, size, time or sync flag is written wrong, so a clip
/// stutters, seeks to grey or won't open; a fragmented recording cut short by a crash doesn't make whole, or loses
/// what was written before the cut; making a recording plain in place moves a byte of its media; the parameter sets
/// don't reach avcC/hvcC, so Windows can't decode it; a trim starts off a key frame; two clips that can join sample
/// for sample get different signatures, or two that can't get the same.
/// </summary>
public sealed class MuxerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-capture-mp4").FullName;
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private static readonly byte[] Sps = [0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40, 0x78, 0x02, 0x27, 0xE5, 0x84];
    private static readonly byte[] Pps = [0x68, 0xEB, 0xE3, 0xCB, 0x22, 0xC0];

    private static IReadOnlyList<TrackInfo> Tracks() =>
    [
        new("vide", "avc1", Bitstream.AvcC(Sps, Pps), 1920, 1080, 90000, "Video"),
        new("soun", "mp4a", Bitstream.AacConfig, 0, 0, 48000, "Mix"),
        new("soun", "mp4a", Bitstream.AacConfig, 0, 0, 48000, "Microphone"),
    ];

    /// <summary>Three seconds: 60 fps video (a key frame each second) with sizes that tell samples apart, and 1024-sample sound.</summary>
    private static void Fill(Mp4Writer writer, int seconds = 3)
    {
        var audioAt = 0L;
        for (var f = 0; f < seconds * 60; f++)
        {
            var time = f * 10_000_000L / 60;
            writer.Write(0, Sample(1000 + f, (byte)f), time, f % 60 == 0);
            while (audioAt <= time)
            {
                writer.Write(1, Sample(300, 0xA1), audioAt, true);
                writer.Write(2, Sample(200, 0xB2), audioAt, true);
                audioAt += 1024 * 10_000_000L / 48000;
            }
        }
    }

    private static byte[] Sample(int size, byte fill) => Enumerable.Repeat(fill, size).ToArray();

    [Fact]
    public void APlainClipReadsBackSampleForSample()
    {
        var path = Path.Combine(_folder, "plain.mp4");
        using (var writer = new Mp4Writer(path, Tracks(), fragmented: false)) { Fill(writer); writer.Finish(); }
        var mp4 = Mp4File.Read(path);
        Assert.False(mp4.Fragmented);
        Assert.Equal(3, mp4.Tracks.Count);
        var video = mp4.Video!;
        Assert.Equal(180, video.Samples.Count);
        Assert.Equal("avc1", video.Info.Format);
        Assert.Equal((1920, 1080), (video.Info.Width, video.Info.Height));
        Assert.Equal(3, video.Samples.Count(s => s.Key));
        Assert.InRange(video.DurationMs, 2990, 3010);
        Assert.Equal(["Video", "Mix", "Microphone"], mp4.Tracks.Select(t => t.Info.Name));
        using var file = File.OpenRead(path);
        for (var f = 0; f < 180; f++)
        {
            var data = Mp4File.ReadSample(file, video.Samples[f]);
            Assert.Equal(1000 + f, data.Length);
            Assert.All(data, b => Assert.Equal((byte)f, b));
            Assert.InRange(video.Ticks(video.Samples[f]) - f * 10_000_000L / 60, -150, 150); // within one 90 kHz tick
        }
        Assert.All(mp4.Tracks[2].Samples, s => Assert.Equal(200, s.Size));
        // The avcC that went in comes out.
        Assert.Equal(Bitstream.AvcC(Sps, Pps), video.Info.Config);
    }

    [Fact]
    public void AFragmentedRecordingIsMadePlainInPlace()
    {
        var path = Path.Combine(_folder, "rec.mp4");
        using (var writer = new Mp4Writer(path, Tracks(), fragmented: true)) { Fill(writer, 4); writer.Finish(); }
        var mp4 = Mp4File.Read(path);
        Assert.False(mp4.Fragmented);
        Assert.Equal(240, mp4.Video!.Samples.Count);
        Assert.Equal(4, mp4.Video.Samples.Count(s => s.Key));
        Assert.Contains(mp4.TopBoxes, b => b.Type == "free");
        Assert.DoesNotContain(mp4.TopBoxes, b => b.Type == "moof");
        using var file = File.OpenRead(path);
        Assert.All(Enumerable.Range(0, 240), f => Assert.Equal((byte)f, Mp4File.ReadSample(file, mp4.Video.Samples[f])[0]));
    }

    [Fact]
    public void ARecordingCutShortKeepsEverySecondBeforeTheCut()
    {
        var path = Path.Combine(_folder, "crash.mp4");
        var writer = new Mp4Writer(path, Tracks(), fragmented: true);
        Fill(writer, 5);
        writer.Flush();
        var whole = new FileInfo(path).Length;
        // The power goes in the middle of the last fragment.
        writer.Dispose();
        using (var f = new FileStream(path, FileMode.Open)) f.SetLength(whole - 5000);
        var cut = Mp4File.Read(path);
        Assert.True(cut.Fragmented);
        Mp4File.MakePlain(path);
        var made = Mp4File.Read(path);
        Assert.False(made.Fragmented);
        var frames = made.Video!.Samples.Count;
        // The fragments before the cut stay (seconds of 60 frames), the cut one goes.
        Assert.InRange(frames, 180, 299);
        Assert.Equal(0, frames % 60);
        using var file = File.OpenRead(path);
        Assert.Equal((byte)(frames - 1), Mp4File.ReadSample(file, made.Video.Samples[frames - 1])[0]);
    }

    [Fact]
    public void ATrimStartsOnTheKeyFrameBeforeIt()
    {
        var path = Path.Combine(_folder, "clip.mp4");
        using (var writer = new Mp4Writer(path, Tracks(), fragmented: false)) { Fill(writer, 4); writer.Finish(); }
        var output = Clips.Trim(path, 1500, 3200, Path.Combine(_folder, "trim.mp4"));
        var trim = Mp4File.Read(output);
        var video = trim.Video!;
        Assert.True(video.Samples[0].Key);
        Assert.Equal(0, video.Ticks(video.Samples[0]));
        using var file = File.OpenRead(output);
        Assert.Equal((byte)60, Mp4File.ReadSample(file, video.Samples[0])[0]); // the key frame at 1 s
        // To the first frame at or after 3.2 s (frame 192), not past it.
        Assert.Equal(192 - 60, video.Samples.Count);
        Assert.All(trim.Audio, a => Assert.True(a.Samples.Count > 0));
    }

    [Fact]
    public void SignaturesSayWhatJoinsWithoutReencoding()
    {
        string Write(string name, IReadOnlyList<TrackInfo> tracks)
        {
            var p = Path.Combine(_folder, name);
            using var w = new Mp4Writer(p, tracks, fragmented: false);
            Fill(w, 1);
            w.Finish();
            return p;
        }
        var a = Clips.Signature(Mp4File.Read(Write("a.mp4", Tracks())));
        var b = Clips.Signature(Mp4File.Read(Write("b.mp4", Tracks())));
        var other = Tracks().ToList();
        other[0] = other[0] with { Width = 1280, Height = 720 };
        var c = Clips.Signature(Mp4File.Read(Write("c.mp4", other)));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>A short clip from the card's H.264 encoder (or Windows' own), muxed here, and decoded by Windows frame for frame.</summary>
    [Fact]
    public void WindowsDecodesWhatTheMuxerWrites()
    {
        GpuDevice gpu;
        try { gpu = GpuDevice.Shared(); }
        catch (CaptureException) { Assert.Skip("No graphics card here."); return; }
        var choice = VideoEncoder.Find(Codec.H264, gpu.Luid, true).Concat(VideoEncoder.Find(Codec.H264, gpu.Luid, false)).FirstOrDefault();
        if (choice is null) { Assert.Skip("No H.264 encoder here."); return; }
        const int w = 640, h = 360, frames = 30;
        var packets = new List<(byte[] Data, long Time, bool Key)>();
        var encoder = new VideoEncoder(gpu, choice, w, h, 30, 2_000_000, RateMode.Clip, false, (d, t, k) => { lock (packets) packets.Add((d, t, k)); });
        using (var source = gpu.Texture(w, h, Format.B8G8R8A8_UNorm))
        using (var convert = new VideoConverter(gpu, w, h, Format.B8G8R8A8_UNorm, w, h, Format.NV12, false))
        {
            for (var f = 0; f < frames; f++)
            {
                var pixels = new byte[w * h * 4];
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i / 4 % w + f * 8); pixels[i + 1] = (byte)(i / 4 / w); pixels[i + 2] = (byte)(f * 8); pixels[i + 3] = 255; }
                gpu.Context.UpdateSubresource(pixels, source, 0, (uint)(w * 4));
                convert.Convert(source, encoder.NextInput());
                var until = Environment.TickCount64 + 3000;
                while (!encoder.Encode(f * 10_000_000L / 30, 10_000_000L / 30, f == 0) && Environment.TickCount64 < until) { }
            }
            encoder.Dispose();
        }
        Assert.Equal(frames, packets.Count);
        var track = Bitstream.Track(Codec.H264, encoder.SequenceHeader, packets.First(p => p.Key).Data, w, h, false);
        Assert.NotNull(track);
        var path = Path.Combine(_folder, "decode.mp4");
        using (var writer = new Mp4Writer(path, [track], fragmented: false, 30))
        {
            foreach (var p in packets) writer.Write(0, Bitstream.ToSample(Codec.H264, p.Data), p.Time, p.Key);
            writer.Finish();
        }
        using var decoder = new VideoDecoder(gpu, path);
        var decoded = 0;
        while (decoder.Next() is not null) decoded++;
        Assert.Equal(frames, decoded);
    }
}
