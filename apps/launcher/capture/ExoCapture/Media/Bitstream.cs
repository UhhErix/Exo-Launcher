using ExoCapture.Encode;

namespace ExoCapture.Media;

/// <summary>
/// The encoders' bitstreams as MP4 wants them: Annex B NAL units (start codes) become length-prefixed samples with the
/// parameter sets moved into the track's avcC or hvcC box; AV1's temporal delimiters go and its sequence header becomes
/// av1C. And back again for Go Live, which sends Annex B.
/// </summary>
internal static class Bitstream
{
    /// <summary>The NAL units of an Annex B buffer, without their start codes.</summary>
    internal static List<ArraySegment<byte>> Nals(byte[] data)
    {
        var list = new List<ArraySegment<byte>>();
        var i = 0;
        var start = -1;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1 || i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1))
            {
                var code = data[i + 2] == 1 ? 3 : 4;
                if (start >= 0) list.Add(new ArraySegment<byte>(data, start, Trim(data, start, i) - start));
                i += code;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0 && start < data.Length)
        {
            var end = Trim(data, start, data.Length);
            if (end > start) list.Add(new ArraySegment<byte>(data, start, end - start));
        }
        return list;
    }

    // Trailing zero bytes before the next start code belong to no NAL.
    private static int Trim(byte[] data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        return end;
    }

    internal static int NalType(Codec codec, ArraySegment<byte> nal) =>
        nal.Count == 0 ? -1 : codec == Codec.H264 ? nal[0] & 0x1F : (nal[0] >> 1) & 0x3F;

    internal static bool IsParameterSet(Codec codec, int type) =>
        codec == Codec.H264 ? type is 7 or 8 : type is 32 or 33 or 34;

    private static bool IsDelimiter(Codec codec, int type) => codec == Codec.H264 ? type == 9 : type == 35;

    /// <summary>The parameter sets in a buffer (SPS, PPS, and VPS for HEVC), by NAL type; the last of each wins.</summary>
    internal static Dictionary<int, byte[]> ParameterSets(Codec codec, byte[] annexB)
    {
        var sets = new Dictionary<int, byte[]>();
        foreach (var nal in Nals(annexB))
        {
            var type = NalType(codec, nal);
            if (IsParameterSet(codec, type)) sets[type] = nal.ToArray();
        }
        return sets;
    }

    /// <summary>An encoded frame as an MP4 sample: 4-byte lengths, no start codes, parameter sets and delimiters out.</summary>
    internal static byte[] ToSample(Codec codec, byte[] frame)
    {
        if (codec == Codec.Av1) return StripTemporalDelimiters(frame);
        var nals = Nals(frame);
        var size = 0;
        foreach (var n in nals)
        {
            var t = NalType(codec, n);
            if (!IsParameterSet(codec, t) && !IsDelimiter(codec, t)) size += 4 + n.Count;
        }
        var sample = new byte[size];
        var at = 0;
        foreach (var n in nals)
        {
            var t = NalType(codec, n);
            if (IsParameterSet(codec, t) || IsDelimiter(codec, t)) continue;
            sample[at++] = (byte)(n.Count >> 24);
            sample[at++] = (byte)(n.Count >> 16);
            sample[at++] = (byte)(n.Count >> 8);
            sample[at++] = (byte)n.Count;
            n.AsSpan().CopyTo(sample.AsSpan(at));
            at += n.Count;
        }
        return sample;
    }

    /// <summary>A length-prefixed MP4 sample back to Annex B, with the parameter sets in front of a key frame (Go Live, decoders).</summary>
    internal static byte[] ToAnnexB(byte[] sample, IReadOnlyList<byte[]>? prefix)
    {
        using var output = new MemoryStream(sample.Length + 64);
        if (prefix is not null)
            foreach (var p in prefix) { output.Write([0, 0, 0, 1]); output.Write(p); }
        var i = 0;
        while (i + 4 <= sample.Length)
        {
            var n = (sample[i] << 24) | (sample[i + 1] << 16) | (sample[i + 2] << 8) | sample[i + 3];
            i += 4;
            if (n <= 0 || i + n > sample.Length) break;
            output.Write([0, 0, 0, 1]);
            output.Write(sample, i, n);
            i += n;
        }
        return output.ToArray();
    }

    // ---------------------------------------------------------------- AV1

    /// <summary>AV1 OBUs without temporal delimiters (type 2), which MP4 samples must not carry.</summary>
    internal static byte[] StripTemporalDelimiters(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        foreach (var (start, length, type) in Obus(data))
            if (type != 2) output.Write(data, start, length);
        return output.ToArray();
    }

    /// <summary>Each OBU in low-overhead form: where it starts, its whole length and its type.</summary>
    internal static IEnumerable<(int Start, int Length, int Type)> Obus(byte[] data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var header = data[i];
            var type = (header >> 3) & 0xF;
            var extension = (header & 0x4) != 0;
            var hasSize = (header & 0x2) != 0;
            var at = i + 1 + (extension ? 1 : 0);
            long size;
            if (hasSize)
            {
                size = 0;
                for (var b = 0; b < 8 && at < data.Length; b++)
                {
                    var v = data[at++];
                    size |= (long)(v & 0x7F) << (7 * b);
                    if ((v & 0x80) == 0) break;
                }
            }
            else size = data.Length - at;
            var end = (int)Math.Min(data.Length, at + size);
            yield return (i, end - i, type);
            i = end;
        }
    }

    internal static byte[]? Av1SequenceHeader(byte[] data)
    {
        foreach (var (start, length, type) in Obus(data))
            if (type == 1) return data.AsSpan(start, length).ToArray();
        return null;
    }

    // ---------------------------------------------------------------- configuration boxes

    /// <summary>avcC: profile, compatibility and level from the SPS, 4-byte lengths, one SPS and one PPS, 8-bit 4:2:0.</summary>
    internal static byte[] AvcC(byte[] sps, byte[] pps)
    {
        using var m = new MemoryStream();
        m.WriteByte(1);
        m.WriteByte(sps.Length > 1 ? sps[1] : (byte)100);
        m.WriteByte(sps.Length > 2 ? sps[2] : (byte)0);
        m.WriteByte(sps.Length > 3 ? sps[3] : (byte)40);
        m.WriteByte(0xFF);
        m.WriteByte(0xE1);
        WriteU16(m, sps.Length); m.Write(sps);
        m.WriteByte(1);
        WriteU16(m, pps.Length); m.Write(pps);
        if (sps.Length > 1 && sps[1] is 100 or 110 or 122 or 244)
        {
            m.WriteByte(0xFD); // chroma_format_idc 1
            m.WriteByte(0xF8); // 8-bit luma
            m.WriteByte(0xF8); // 8-bit chroma
            m.WriteByte(0);
        }
        return m.ToArray();
    }

    /// <summary>hvcC from the VPS, SPS and PPS: profile_tier_level copied from the SPS.</summary>
    internal static byte[] HvcC(byte[] vps, byte[] sps, byte[] pps, bool tenBit)
    {
        var rbsp = Unescape(sps.AsSpan(2));
        // sps_video_parameter_set_id(4) max_sub_layers_minus1(3) temporal_id_nesting(1), then profile_tier_level.
        var ptl = rbsp.Length >= 13 ? rbsp.AsSpan(1, 12).ToArray() : [0x01, 0x60, 0, 0, 0, 0x90, 0, 0, 0, 0, 0, 0x5D];
        using var m = new MemoryStream();
        m.WriteByte(1);
        m.Write(ptl); // profile space/tier/idc, 32 compatibility flags, 48 constraint flags, level
        WriteU16(m, 0xF000);
        m.WriteByte(0xFC);
        m.WriteByte(0xFD); // 4:2:0
        m.WriteByte((byte)(0xF8 | (tenBit ? 2 : 0)));
        m.WriteByte((byte)(0xF8 | (tenBit ? 2 : 0)));
        WriteU16(m, 0);
        m.WriteByte(0x0F); // one temporal layer, nested, 4-byte lengths
        m.WriteByte(3);
        foreach (var (type, nal) in new[] { (32, vps), (33, sps), (34, pps) })
        {
            m.WriteByte((byte)(0x80 | type));
            WriteU16(m, 1);
            WriteU16(m, nal.Length);
            m.Write(nal);
        }
        return m.ToArray();
    }

    /// <summary>av1C from the sequence header OBU: profile and level from its first bits (a header without timing info), else Main at level 5.1.</summary>
    internal static byte[] Av1C(byte[] sequenceHeader, bool tenBit)
    {
        int profile = 0, level = 13;
        var payload = sequenceHeader.AsSpan();
        var header = payload.Length > 0 ? payload[0] : (byte)0;
        var at = 1 + ((header & 0x4) != 0 ? 1 : 0);
        if ((header & 0x2) != 0) while (at < payload.Length && (payload[at++] & 0x80) != 0) { }
        if (at < payload.Length)
        {
            var reader = new BitReader(payload[at..].ToArray());
            profile = reader.Bits(3);
            reader.Bits(1); // still_picture
            var reduced = reader.Bits(1);
            if (reduced == 1) level = reader.Bits(5);
            else if (reader.Bits(1) == 0) // timing_info_present_flag
            {
                reader.Bits(1); // initial_display_delay_present_flag
                var count = reader.Bits(5) + 1;
                if (count >= 1) { reader.Bits(12); level = reader.Bits(5); }
            }
        }
        using var m = new MemoryStream();
        m.WriteByte(0x81);
        m.WriteByte((byte)((profile << 5) | (level & 0x1F)));
        m.WriteByte((byte)((tenBit ? 0x40 : 0) | 0x0C)); // high_bitdepth, subsampling x and y
        m.WriteByte(0);
        m.Write(sequenceHeader);
        return m.ToArray();
    }

    /// <summary>The MP4 description of a video track from its first key frame (and the encoder's sequence header, if it named one).</summary>
    internal static TrackInfo? Track(Codec codec, byte[]? sequenceHeader, byte[] keyFrame, int width, int height, bool hdr)
    {
        var stream = sequenceHeader is { } header ? [.. header, .. keyFrame] : keyFrame;
        switch (codec)
        {
            case Codec.H264:
                var avc = ParameterSets(codec, stream);
                return avc.ContainsKey(7) && avc.ContainsKey(8)
                    ? new TrackInfo("vide", "avc1", AvcC(avc[7], avc[8]), width, height, 90000, "Video", hdr) : null;
            case Codec.Hevc:
                var hevc = ParameterSets(codec, stream);
                return hevc.ContainsKey(32) && hevc.ContainsKey(33) && hevc.ContainsKey(34)
                    ? new TrackInfo("vide", "hvc1", HvcC(hevc[32], hevc[33], hevc[34], hdr), width, height, 90000, "Video", hdr) : null;
            default:
                return Av1SequenceHeader(stream) is { } sequence
                    ? new TrackInfo("vide", "av01", Av1C(sequence, hdr), width, height, 90000, "Video", hdr) : null;
        }
    }

    /// <summary>The codec of an MP4 sample entry.</summary>
    internal static Codec? CodecOf(string format) => format switch
    {
        "avc1" or "avc3" => Codec.H264,
        "hvc1" or "hev1" => Codec.Hevc,
        "av01" => Codec.Av1,
        _ => null,
    };

    /// <summary>AudioSpecificConfig: AAC-LC at 48 kHz, stereo.</summary>
    internal static readonly byte[] AacConfig = [0x11, 0x90];

    internal static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        var zeros = 0;
        foreach (var b in data)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. output];
    }

    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    internal sealed class BitReader(byte[] data)
    {
        private int _bit;
        internal int Bits(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++)
            {
                var index = _bit >> 3;
                var b = index < data.Length ? (data[index] >> (7 - (_bit & 7))) & 1 : 0;
                v = (v << 1) | b;
                _bit++;
            }
            return v;
        }
    }
}
