using System.Runtime.InteropServices;
using ExoCapture.Capture;
using ExoCapture.Edit;
using ExoCapture.Encode;
using ExoCapture.Media;

namespace ExoCapture.Tests;

/// <summary>
/// Pictures and short clips. Ways it could fail: the HDR tone map clips highlights, darkens SDR white, shifts hue or
/// lets brighter become darker; a GIF's LZW codes go out of step with a decoder (garbage after the first rows); a GIF
/// or WebM plan promises 10 MB and isn't, or refuses a clip that fits; For Discord picks a rate that can't fit, or the
/// wrong size and frame rate; SSIM calls two identical pictures different; the bitstream boxes miss a field a decoder
/// needs; OBS's hook block is written at the wrong offsets, so the hook reads garbage offsets and the game crashes.
/// </summary>
public sealed class PictureTests
{
    private static byte[] Half(params float[] rgba)
    {
        var bytes = new byte[rgba.Length * 2];
        var halves = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
        for (var i = 0; i < rgba.Length; i++) halves[i] = (Half)rgba[i];
        return bytes;
    }

    [Fact]
    public void SdrWhiteIsWhiteAndBlackIsBlack()
    {
        // 240 nits SDR white is 3.0 in scRGB (80 nits each).
        var white = Imaging.ToneMap(Half(3, 3, 3, 1), 1, 1, 240, 1000);
        Assert.InRange(white[0], 250, 255);
        Assert.Equal(white[0], white[2]);
        var black = Imaging.ToneMap(Half(0, 0, 0, 1), 1, 1, 240, 1000);
        Assert.Equal([0, 0, 0, 255], black);
        // Mid grey keeps sRGB's curve: 18% linear is about 118.
        var grey = Imaging.ToneMap(Half(0.18f * 3, 0.18f * 3, 0.18f * 3, 1), 1, 1, 240, 1000);
        Assert.InRange(grey[0], 115, 120);
    }

    [Fact]
    public void HighlightsRollOffWithoutClippingOrReversing()
    {
        var previous = -1f;
        for (var x = 0f; x <= 6; x += 0.01f)
        {
            var y = Imaging.Shoulder(x, 5);
            Assert.InRange(y, 0, 1);
            Assert.True(y >= previous, $"brighter must not be darker at {x}");
            previous = y;
        }
        Assert.Equal(Imaging.ShoulderStart, Imaging.Shoulder(Imaging.ShoulderStart, 5), 5);
        Assert.Equal(1f, Imaging.Shoulder(5, 5), 4);
        // Below the knee nothing changes.
        Assert.Equal(0.5f, Imaging.Shoulder(0.5f, 5));
        // Above SDR white is still told apart: 2× and 3× white differ.
        Assert.True(Imaging.Shoulder(3, 5) > Imaging.Shoulder(2, 5) + 0.01f);
    }

    [Fact]
    public void AColourKeepsItsHueThroughTheShoulder()
    {
        // A bright orange 4× over white: red rolls off, and green keeps its share of red.
        var p = Imaging.ToneMap(Half(4 * 3, 2 * 3, 0, 1), 1, 1, 240, 1000);
        var (b, g, r) = (p[0], p[1], p[2]);
        Assert.Equal(0, b);
        Assert.True(r > g && g > 0);
        Assert.InRange(Linear(g) / Linear(r), 0.45, 0.55);
    }

    private static double Linear(byte v)
    {
        var s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    [Fact]
    public void GifLzwDecodesBackToTheSamePicture()
    {
        var random = new Random(4);
        foreach (var length in new[] { 1, 2, 300, 70_000 })
        {
            var indices = new byte[length];
            for (var i = 0; i < length; i++) indices[i] = (byte)(i % 7 == 0 ? random.Next(256) : i / 50 % 256);
            Assert.Equal(indices, LzwDecode(GifWriter.Lzw(indices, 8), 8));
        }
    }

    /// <summary>A plain GIF LZW decoder, as a browser's.</summary>
    private static byte[] LzwDecode(byte[] data, int minBits)
    {
        var clear = 1 << minBits;
        var end = clear + 1;
        var output = new List<byte>();
        var table = new List<byte[]>();
        void Reset() { table.Clear(); for (var i = 0; i < clear; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
        Reset();
        var size = minBits + 1;
        int bits = 0, count = 0, pos = 0;
        byte[]? previous = null;
        while (true)
        {
            while (count < size && pos < data.Length) { bits |= data[pos++] << count; count += 8; }
            if (count < size) break;
            var code = bits & ((1 << size) - 1);
            bits >>= size;
            count -= size;
            if (code == clear) { Reset(); size = minBits + 1; previous = null; continue; }
            if (code == end) break;
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else entry = [.. previous!, previous![0]];
            output.AddRange(entry);
            if (previous is not null && table.Count < 4096) table.Add([.. previous, entry[0]]);
            if (table.Count == 1 << size && size < 12) size++;
            previous = entry;
        }
        return [.. output];
    }

    [Theory]
    [InlineData(5, 480, 15)]
    [InlineData(70, 400, 12)]
    [InlineData(160, 240, 10)]
    public void AGifPlanFitsTenMegabytes(double seconds, int width, int fps)
    {
        var plan = Clips.GifPlan(1920, 1080, seconds);
        Assert.Equal((width, fps), (plan.Width, plan.Fps));
        var h = (int)(plan.Width * 1080.0 / 1920);
        Assert.True(plan.Width * (double)h * plan.Fps * seconds * 0.11 <= Clips.DiscordBytes * 0.9);
    }

    [Fact]
    public void AGifTooLongSaysHowLongFits()
    {
        var ex = Assert.Throws<CaptureException>(() => Clips.GifPlan(1920, 1080, 1000));
        Assert.Contains($"{Clips.MaxGifSeconds(1920, 1080)} seconds or less", ex.Message);
        // That length does fit.
        Clips.GifPlan(1920, 1080, Clips.MaxGifSeconds(1920, 1080));
    }

    [Fact]
    public void AnOversizedGifStepsDown()
    {
        var first = Clips.GifPlan(1920, 1080, 5, step: 0);
        var second = Clips.GifPlan(1920, 1080, 5, step: 1);
        Assert.True(second.Width * second.Fps < first.Width * first.Fps);
    }

    [Theory]
    [InlineData(10, 6_000_000)]
    [InlineData(60, 1_258_291)]
    public void AWebMFitsTenMegabytes(double seconds, int bitrate)
    {
        Assert.InRange(Clips.WebMBitrate(seconds), bitrate - 1000, bitrate + 1000);
        Assert.True(Clips.WebMBitrate(seconds) * seconds / 8 <= Clips.DiscordBytes * 0.9 + 1);
    }

    [Fact]
    public void AWebMTooLongIsRefused() => Assert.Throws<CaptureException>(() => Clips.WebMBitrate(400));

    [Theory]
    [InlineData(10, 1920, 1080, 60, 1080, 60)] // 7.5 Mbit/s: 1080p 60
    [InlineData(30, 1920, 1080, 60, 720, 60)]  // 2.4 Mbit/s: 720p, 30 fps below 2.5
    [InlineData(60, 2560, 1440, 120, 720, 30)]
    public void ForDiscordFitsItsLimit(double seconds, int w, int h, double fps, int height, int outFps)
    {
        var plan = Clips.Plan("discord", w, h, fps, seconds);
        Assert.Equal(height, plan.Height);
        Assert.True(plan.Fps <= outFps);
        Assert.True((plan.Bitrate + 160_000) * seconds / 8 <= Clips.DiscordBytes);
        Assert.Equal(0, plan.Width % 2);
    }

    [Fact]
    public void ForDiscordTooLongSaysHowLongFits()
    {
        var ex = Assert.Throws<CaptureException>(() => Clips.Plan("discord", 1920, 1080, 60, 600));
        Assert.Equal($"Too long for Discord's 10 MB. Trim it to {Clips.MaxDiscordSeconds} seconds or less.", ex.Message);
    }

    [Fact]
    public void VerticalIsTheMiddleNineBySixteen()
    {
        var plan = Clips.Plan("vertical", 1920, 1080, 60, 10);
        Assert.Equal((1080, 1920), (plan.Width, plan.Height));
        var crop = plan.Crop!.Value;
        Assert.Equal(606, crop.Right - crop.Left);
        Assert.InRange((crop.Left + crop.Right) / 2, 1920 / 2 - 2, 1920 / 2 + 2);
    }

    [Fact]
    public void SsimOfAPictureWithItselfIsOne()
    {
        var random = new Random(1);
        var a = new byte[64 * 64 * 4];
        random.NextBytes(a);
        Assert.Equal(1.0, Ssim.Compare(a, a, 64, 64), 6);
        var b = (byte[])a.Clone();
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(255 - b[i]);
        Assert.True(Ssim.Compare(a, b, 64, 64) < 0.2);
    }

    [Fact]
    public void AnnexBRoundTripsThroughMp4Samples()
    {
        byte[] sps = [0x67, 1, 2], pps = [0x68, 3], idr = [0x65, 9, 9, 9, 0, 0, 0, 0];
        byte[] frame = [0, 0, 0, 1, 0x09, 0x10, 0, 0, 0, 1, .. sps, 0, 0, 1, .. pps, 0, 0, 0, 1, .. idr];
        var sets = Bitstream.ParameterSets(Codec.H264, frame);
        Assert.Equal(sps, sets[7]);
        Assert.Equal(pps, sets[8]);
        var sample = Bitstream.ToSample(Codec.H264, frame);
        // Only the picture: delimiter and parameter sets out, trailing zeros not part of the NAL.
        Assert.Equal<byte>([0, 0, 0, 4, 0x65, 9, 9, 9], sample);
        Assert.Equal<byte>([0, 0, 0, 1, .. sps, 0, 0, 0, 1, .. pps, 0, 0, 0, 1, 0x65, 9, 9, 9], Bitstream.ToAnnexB(sample, [sps, pps]));
    }

    [Fact]
    public void AvcCCarriesProfileLevelAndBothSets()
    {
        byte[] sps = [0x67, 100, 0, 40, 0xAC], pps = [0x68, 0xEB];
        var avcc = Bitstream.AvcC(sps, pps);
        Assert.Equal<byte>([1, 100, 0, 40, 0xFF, 0xE1, 0, 5], avcc[..8]);
        Assert.Equal(sps, avcc[8..13]);
        Assert.Equal<byte>([1, 0, 2, 0x68, 0xEB], avcc[13..18]);
        Assert.Equal(4, avcc.Length - 18); // High profile's chroma and bit depth
    }

    [Fact]
    public void HvcCCopiesTheProfileTierLevel()
    {
        // SPS: NAL header (2), then vps id / sub-layers / nesting (1), then 12 bytes of profile_tier_level.
        byte[] ptl = [0x01, 0x60, 0, 0, 0, 0x90, 0, 0, 0, 0, 0, 0x7B];
        byte[] sps = [0x42, 0x01, 0x01, .. ptl, 0xA0];
        var hvcc = Bitstream.HvcC([0x40, 0x01, 0x0C], sps, [0x44, 0x01, 0xC1], tenBit: true);
        Assert.Equal(1, hvcc[0]);
        Assert.Equal(ptl, hvcc[1..13]);
        Assert.Equal(0xFA, hvcc[17]); // 10-bit luma
        Assert.Equal(0xFA, hvcc[18]); // 10-bit chroma
        Assert.Equal(3, hvcc[22]); // VPS, SPS, PPS arrays
    }

    [Fact]
    public void Av1TemporalDelimitersLeaveSamples()
    {
        // A temporal delimiter (type 2, size 0), a sequence header (type 1, size 2), a frame (type 6, size 1).
        byte[] obus = [0x12, 0x00, 0x0A, 0x02, 0x00, 0x00, 0x32, 0x01, 0xFF];
        Assert.Equal<byte>([0x0A, 0x02, 0x00, 0x00, 0x32, 0x01, 0xFF], Bitstream.StripTemporalDelimiters(obus));
        Assert.Equal<byte>([0x0A, 0x02, 0x00, 0x00], Bitstream.Av1SequenceHeader(obus)!);
    }

    [Fact]
    public void ObsOffsetsParseFromItsHelper()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphics-offsets64.txt"));
        var o = HookSource.ParseOffsets(text)!.Value;
        Assert.NotEqual(0u, o.DxgiPresent);
        Assert.NotEqual(0u, o.DxgiResize);
        Assert.NotEqual(0u, o.D3d9Present);
        Assert.Null(HookSource.ParseOffsets("[d3d9]\npresent=0x10\n"));
    }

    [Fact]
    public unsafe void TheHookBlockIsWrittenAtObsOffsets()
    {
        var block = new byte[HookSource.HookInfoSize];
        var offsets = new HookSource.GraphicsOffsets(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        fixed (byte* p = block) HookSource.WriteOptions(p, offsets, 166_666);
        Assert.Equal(166_666UL, BitConverter.ToUInt64(block, 56)); // frame_interval
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, block[64..68]); // use_scale, force_shmem, capture_overlay, allow_srgb_alias
        uint At(int index) => BitConverter.ToUInt32(block, 68 + index * 4);
        Assert.Equal(1u, At(0)); // d3d8.present
        Assert.Equal(new uint[] { 2, 3, 4, 5, 6 }, Enumerable.Range(1, 5).Select(At)); // d3d9
        Assert.Equal(new uint[] { 7, 8, 9 }, Enumerable.Range(6, 3).Select(At)); // dxgi present, resize, present1
        Assert.All(Enumerable.Range(9, 8), i => Assert.Equal(0u, At(i))); // ddraw
        Assert.Equal(10u, At(17)); // dxgi2.release
        Assert.Equal(11u, At(18)); // d3d12
        Assert.Equal(144, 68 + 19 * 4); // reserved[126] follows, to 648
        Assert.All(block[144..], b => Assert.Equal(0, b));
    }
}
