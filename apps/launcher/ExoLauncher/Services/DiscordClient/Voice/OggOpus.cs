using System.Buffers;
using System.Buffers.Binary;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Opus in Ogg (RFC 7845), as a soundboard sound may be: the identification header (OpusHead: one channel, the encoder's
/// pre-skip, 48 kHz) on the first page, the comment header (OpusTags) on the second, then the packets, up to a second of
/// them a page (as libopusenc pages them), never one split across pages. Each audio page's granule position counts the
/// samples of every packet completed on it and before it; the last page's is the pre-skip plus the take's own samples, so
/// what the encoder added past the end is trimmed. Every page carries Ogg's CRC. <see cref="Read"/> plays such a file
/// back (a soundboard sound in Ogg).
/// </summary>
internal static class OggOpus
{
    private const int PacketsPerPage = 50, MostSegments = 255;
    internal const byte Continued = 0x01, First = 0x02, Last = 0x04;

    /// <summary>The file: <paramref name="packets"/> are 20 ms each, the last one past the end; <paramref name="samples"/> the take's own.</summary>
    internal static byte[] Write(IReadOnlyList<byte[]> packets, int preSkip, long samples, uint serial)
    {
        if (packets.Count == 0) throw new ArgumentException("A voice message has at least one packet.", nameof(packets));
        using var file = new MemoryStream();
        var sequence = 0u;
        Page(file, serial, sequence++, First, 0, [Head(preSkip)]);
        Page(file, serial, sequence++, 0, 0, [Tags()]);
        var end = preSkip + samples;
        long granule = 0;
        for (var start = 0; start < packets.Count;)
        {
            var count = 0;
            var segments = 0;
            while (start + count < packets.Count && count < PacketsPerPage && segments + Segments(packets[start + count].Length) <= MostSegments)
                segments += Segments(packets[start + count++].Length);
            granule += count * (long)Opus.FrameSamples;
            var last = start + count == packets.Count;
            Page(file, serial, sequence++, last ? Last : (byte)0, last ? Math.Min(granule, end) : granule, packets.Skip(start).Take(count));
            start += count;
        }
        return file.ToArray();
    }

    /// <summary>How many lacing values a packet takes: one per whole 255 bytes, and one for the rest (0 when it ends on 255).</summary>
    internal static int Segments(int length) => length / 255 + 1;

    /// <summary>Whether a file is Ogg whose first stream is Opus (its first page holds OpusHead).</summary>
    internal static bool IsOpus(ReadOnlySpan<byte> file) =>
        file.Length > 27 && file[..4].SequenceEqual("OggS"u8) && 27 + file[26] + 8 <= file.Length && file.Slice(27 + file[26], 8).SequenceEqual("OpusHead"u8);

    /// <summary>
    /// An Ogg Opus file (a soundboard sound, <see cref="Soundboard"/>) as 48 kHz interleaved stereo: the first stream's
    /// packets, joined across pages by their lacing, decoded by libopus (a mono stream comes out on both channels), its
    /// pre-skip dropped, its end trimmed to the last page's granule position, and its output gain applied; at most
    /// <paramref name="mostSamples"/> a channel. Null when it isn't Opus Exo plays (more than two channels, a page cut short,
    /// a packet libopus refuses).
    /// </summary>
    internal static unsafe float[]? Read(ReadOnlySpan<byte> file, int mostSamples)
    {
        var packets = new List<byte[]>();
        using var packet = new MemoryStream();
        long end = -1;
        uint? serial = null;
        for (var at = 0; at + 27 <= file.Length;)
        {
            var segments = file[at + 26];
            var body = at + 27 + segments;
            if (!file.Slice(at, 4).SequenceEqual("OggS"u8) || body > file.Length) return null;
            var size = 0;
            for (var i = 0; i < segments; i++) size += file[at + 27 + i];
            if (body + size > file.Length) return null;
            var stream = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 14)..]);
            serial ??= stream;
            if (stream == serial)
            {
                for (int i = 0, from = body; i < segments; from += file[at + 27 + i], i++)
                {
                    packet.Write(file.Slice(from, file[at + 27 + i]));
                    if (file[at + 27 + i] < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); }
                }
                // -1: no packet ends on this page.
                if (BinaryPrimitives.ReadInt64LittleEndian(file[(at + 6)..]) is var granule and >= 0 && packets.Count > 2) end = granule;
            }
            at = body + size;
        }
        if (packets.Count < 3 || packets[0] is not { Length: >= 19 } head || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)
            || head[9] is 0 or > 2 || head[18] != 0) return null;
        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        var gain = MathF.Pow(10, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)) / 256f / 20);
        var most = Math.Min((long)mostSamples + preSkip, end < 0 ? long.MaxValue : end);
        var pcm = new float[Math.Max(0, most) * Opus.Channels];
        var decoder = Opus.NewDecoder();
        var written = 0;
        try
        {
            const int Longest = 5760; // 120 ms, the longest Opus packet
            var decoded = new float[Longest * Opus.Channels];
            for (var p = 2; p < packets.Count && written < most; p++)
            {
                int samples;
                fixed (byte* data = packets[p]) fixed (float* into = decoded)
                    samples = Opus.DecodeFloat(decoder, data, packets[p].Length, into, Longest, 0);
                if (samples < 0) return null;
                var take = (int)Math.Min(samples, most - written);
                decoded.AsSpan(0, take * Opus.Channels).CopyTo(pcm.AsSpan(written * Opus.Channels));
                written += take;
            }
        }
        finally { Opus.DecoderDestroy(decoder); }
        if (written <= preSkip) return null;
        var sound = pcm.AsSpan(preSkip * Opus.Channels, (written - preSkip) * Opus.Channels).ToArray();
        if (gain != 1f) for (var i = 0; i < sound.Length; i++) sound[i] *= gain;
        return sound;
    }

    private static byte[] Head(int preSkip)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; // version
        head[9] = 1; // one channel
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), Opus.SampleRate);
        // Output gain 0, channel mapping family 0 (mono or stereo, no table).
        return head;
    }

    private static byte[] Tags()
    {
        var vendor = "Exo Launcher"u8;
        var tags = new byte[8 + 4 + vendor.Length + 4];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(tags.AsSpan(12));
        // No user comments.
        return tags;
    }

    private static void Page(Stream to, uint serial, uint sequence, byte flags, long granule, IEnumerable<byte[]> packets)
    {
        var lacing = new List<byte>();
        using var body = new MemoryStream();
        foreach (var packet in packets)
        {
            var left = packet.Length;
            for (; left >= 255; left -= 255) lacing.Add(255);
            lacing.Add((byte)left);
            body.Write(packet);
        }
        var page = new byte[27 + lacing.Count + body.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0; // stream structure version
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(page.AsSpan(27 + lacing.Count));
        // The checksum over the whole page with its own field zero, as it is now.
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        to.Write(page);
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var r = i << 24;
            for (var bit = 0; bit < 8; bit++) r = (r & 0x8000_0000) != 0 ? (r << 1) ^ 0x04C1_1DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    /// <summary>Ogg's CRC-32: polynomial 0x04C11DB7, from zero, not reflected, nothing XORed after.</summary>
    internal static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var b in data) crc = (crc << 8) ^ Table[(int)(((crc >> 24) ^ b) & 0xFF)];
        return crc;
    }
}
