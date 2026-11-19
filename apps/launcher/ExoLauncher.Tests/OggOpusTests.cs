using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Services.DiscordClient;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways an Ogg file could read wrong for the soundboard (and for the tests' own sounds, written with this class):
/// - a page's CRC isn't Ogg's (polynomial, start value or the field not zeroed while summing), so demuxers drop it;
/// - the first page isn't marked as the stream's first, the last isn't marked its last, or page numbers skip or repeat;
/// - a page holds over 255 lacing values, or a packet is split across pages or laced wrong (one of exactly 255 bytes
///   needs a 0 after it, or it runs into the next);
/// - granule positions don't count the samples of the packets completed on their page, go backwards, or end anywhere but
///   the pre-skip plus the take's own samples (the last 6.5 ms cut, or the encoder's padding left in);
/// - OpusHead says two channels, the wrong pre-skip or the wrong rate (played as stereo, or at the wrong speed);
/// </summary>
public sealed class OggOpusTests
{
    private sealed record Page(byte Flags, long Granule, uint Serial, uint Sequence, int Segments, List<byte[]> Packets);

    // Reads an Ogg stream back, checking each page's capture pattern, version and CRC as a demuxer does.
    private static List<Page> Pages(byte[] ogg)
    {
        var pages = new List<Page>();
        for (var at = 0; at < ogg.Length;)
        {
            Assert.Equal("OggS", Encoding.ASCII.GetString(ogg, at, 4));
            Assert.Equal(0, ogg[at + 4]);
            var segments = ogg[at + 26];
            var lacing = ogg.AsSpan(at + 27, segments).ToArray();
            var size = 27 + segments + lacing.Sum(l => l);
            var page = ogg.AsSpan(at, size).ToArray();
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
            page.AsSpan(22, 4).Clear();
            Assert.Equal(crc, OggOpus.Crc(page));
            var packets = new List<byte[]>();
            var body = at + 27 + segments;
            var current = new List<byte>();
            foreach (var l in lacing)
            {
                current.AddRange(ogg.AsSpan(body, l).ToArray());
                body += l;
                if (l < 255) { packets.Add([.. current]); current.Clear(); }
            }
            Assert.Empty(current); // no packet runs on into the next page
            pages.Add(new Page(ogg[at + 5], BinaryPrimitives.ReadInt64LittleEndian(ogg.AsSpan(at + 6)), BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(at + 14)),
                BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(at + 18)), segments, packets));
            at += size;
        }
        return pages;
    }

    [Fact]
    public void TheChecksumIsOggs()
    {
        // CRC-32 with polynomial 0x04C11DB7, from zero, unreflected, nothing XORed after: "123456789" checks to 0x89A1897F.
        Assert.Equal(0x89A1897Fu, OggOpus.Crc("123456789"u8));
        Assert.Equal(0u, OggOpus.Crc([]));
    }

    [Fact]
    public void PagesAreWholeNumberedAndCountTheirSamples()
    {
        // 140 packets of every awkward size (one of exactly 255 bytes, one of 510, Opus's largest), the last one the
        // encoder's lookahead past the end.
        var sizes = Enumerable.Range(0, 140).Select(i => i switch { 3 => 255, 4 => 510, 5 => 1275, 6 => 1, _ => 60 + i % 40 }).ToList();
        var packets = sizes.Select((size, i) => Enumerable.Repeat((byte)i, size).ToArray()).ToList();
        const int preSkip = 312;
        const long samples = 139 * 960L;
        var pages = Pages(OggOpus.Write(packets, preSkip, samples, 0xC0FFEE));

        Assert.All(pages, p => Assert.Equal(0xC0FFEEu, p.Serial));
        Assert.Equal(Enumerable.Range(0, pages.Count).Select(i => (uint)i), pages.Select(p => p.Sequence));
        Assert.Equal(OggOpus.First, pages[0].Flags);
        Assert.Equal(OggOpus.Last, pages[^1].Flags);
        Assert.All(pages.Skip(1).SkipLast(1), p => Assert.Equal(0, p.Flags));
        Assert.All(pages, p => Assert.InRange(p.Segments, 1, 255));

        // Headers: OpusHead alone on the first page, OpusTags alone on the second, both at granule 0.
        var head = Assert.Single(pages[0].Packets);
        Assert.Equal("OpusHead", Encoding.ASCII.GetString(head, 0, 8));
        Assert.Equal(19, head.Length);
        Assert.Equal(1, head[8]); // version
        Assert.Equal(1, head[9]); // one channel, as Discord's clients record
        Assert.Equal(preSkip, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)));
        Assert.Equal(0, head[18]); // mapping family 0
        var tags = Assert.Single(pages[1].Packets);
        Assert.Equal("OpusTags", Encoding.ASCII.GetString(tags, 0, 8));
        Assert.Equal(0, pages[0].Granule);
        Assert.Equal(0, pages[1].Granule);

        // Audio: every packet back, whole and in order; each page's granule the samples of the packets through it.
        var audio = pages.Skip(2).ToList();
        Assert.Equal(packets, audio.SelectMany(p => p.Packets));
        Assert.All(audio, p => Assert.InRange(p.Packets.Count, 1, 50)); // a second a page at most
        long through = 0;
        foreach (var page in audio.SkipLast(1))
        {
            through += page.Packets.Count * 960L;
            Assert.Equal(through, page.Granule);
        }
        // The last page ends exactly at the take's end: the pre-skip plus its own samples, the lookahead's padding trimmed.
        Assert.Equal(preSkip + samples, audio[^1].Granule);
        Assert.True(audio[^1].Granule <= packets.Count * 960L);
        Assert.True(audio.Zip(audio.Skip(1)).All(pair => pair.First.Granule <= pair.Second.Granule));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(254, 1)]
    [InlineData(255, 2)] // 255 then a 0
    [InlineData(256, 2)]
    [InlineData(510, 3)]
    [InlineData(1275, 6)]
    public void PacketsAreLacedInFull(int length, int segments) => Assert.Equal(segments, OggOpus.Segments(length));

    [Fact]
    public void PagesNeverPassTwoHundredFiftyFiveSegments()
    {
        // Large packets (6 lacing values each) fill a page before a second of them does.
        var packets = Enumerable.Range(0, 120).Select(_ => new byte[1275]).ToList();
        var pages = Pages(OggOpus.Write(packets, 312, 119 * 960L, 1));
        Assert.All(pages, p => Assert.InRange(p.Segments, 1, 255));
        Assert.Equal(120, pages.Skip(2).Sum(p => p.Packets.Count));
    }
}
