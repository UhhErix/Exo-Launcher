using System.Buffers.Binary;
using System.Text;

namespace ExoCapture.Media;

/// <summary>A track of an MP4: what it carries and how its sample entry describes it.</summary>
internal sealed record TrackInfo(string Handler, string Format, byte[] Config, int Width, int Height, int Timescale, string Name, bool Hdr = false)
{
    internal bool IsVideo => Handler == "vide";
}

/// <summary>One sample: where it is in the file, its size, its decode time in the track's timescale, its duration, a sync point.</summary>
internal readonly record struct Mp4Sample(long Offset, int Size, long Time, int Duration, bool Key);

internal sealed class Mp4Track(TrackInfo info)
{
    internal TrackInfo Info { get; set; } = info;
    internal readonly List<Mp4Sample> Samples = [];
    internal long DurationUnits => Samples.Count == 0 ? 0 : Samples[^1].Time + Samples[^1].Duration - Samples[0].Time;
    internal double DurationMs => DurationUnits * 1000.0 / Info.Timescale;
    internal long StartUnits => Samples.Count == 0 ? 0 : Samples[0].Time;
    /// <summary>A sample's time in 100 ns units.</summary>
    internal long Ticks(Mp4Sample s) => (long)((Int128)s.Time * 10_000_000 / Info.Timescale);
}

/// <summary>
/// Exo Capture's own ISO-BMFF muxer: H.264 (avc1), HEVC (hvc1) or AV1 (av01) video and any number of AAC tracks. A clip
/// is written plain (one mdat, the moov after it). A recording is written fragmented (a moof and mdat each second), so a
/// crash or a power cut leaves a file that plays to its last second; when it ends, the file is made plain in place: the
/// fragment boxes become 'free' and a whole moov goes on the end, pointing into the mdats already there. No copy.
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    private readonly FileStream _file;
    private readonly string _path;
    private readonly Mp4Track[] _tracks;
    private readonly bool _fragmented;
    private readonly (byte[] Data, long Time, bool Key)?[] _pending;
    private readonly List<(int Track, byte[] Data, long Time, int Duration, bool Key)> _fragment = [];
    private long _mdatStart;
    private uint _sequence;
    private long _fragmentStart = -1;
    private bool _finished;
    private readonly int[] _defaultDuration;

    internal string Path => _path;
    internal long BytesWritten => _file.Position;

    internal Mp4Writer(string path, IReadOnlyList<TrackInfo> tracks, bool fragmented, int fps = 60)
    {
        _path = path;
        _tracks = [.. tracks.Select(t => new Mp4Track(t))];
        _fragmented = fragmented;
        _pending = new (byte[], long, bool)?[tracks.Count];
        _defaultDuration = [.. tracks.Select(t => t.IsVideo ? Math.Max(1, t.Timescale / Math.Max(1, fps)) : 1024)];
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        WriteFtyp(_file, fragmented);
        if (fragmented)
        {
            var moov = Boxes.Moov(_tracks, fragmented: true, offsetShift: 0);
            _file.Write(moov);
            _file.Flush(true);
        }
        else
        {
            _mdatStart = _file.Position;
            // A 64-bit mdat: size 1, type, then the real size, patched at the end.
            _file.Write([0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 0, 0, 0, 0, 0, 0, 0, 0]);
        }
    }

    /// <summary>
    /// Replaces a track's description (the encoder named its parameter sets only with its first key frame): any time in a
    /// plain file, whose moov is written last; in a fragmented one only before the first sample, as its moov comes first.
    /// </summary>
    internal void Describe(int track, TrackInfo info)
    {
        if (!_fragmented || _tracks[track].Samples.Count == 0) _tracks[track].Info = info;
    }

    /// <summary>One sample (already in MP4 form), at its time in 100 ns from the start of the file.</summary>
    internal void Write(int track, byte[] data, long time, bool key)
    {
        if (_finished) return;
        var ts = _tracks[track].Info.Timescale;
        var units = (long)Math.Round(time * (double)ts / 10_000_000);
        if (_pending[track] is { } previous)
        {
            var duration = (int)Math.Clamp(units - previous.Time, 1, int.MaxValue);
            Commit(track, previous.Data, previous.Time, duration, previous.Key);
            if (units <= previous.Time) units = previous.Time + 1;
        }
        _pending[track] = (data, units, key);
        if (_fragmented && _tracks[track].Info.IsVideo && key && _fragmentStart >= 0 && time - _fragmentStart >= 10_000_000)
            Flush();
        if (_fragmented && _fragmentStart < 0) _fragmentStart = time;
    }

    private void Commit(int track, byte[] data, long time, int duration, bool key)
    {
        if (_fragmented)
        {
            _fragment.Add((track, data, time, duration, key));
            return;
        }
        var offset = _file.Position;
        _file.Write(data);
        _tracks[track].Samples.Add(new Mp4Sample(offset, data.Length, time, duration, key));
    }

    /// <summary>Writes the fragment held so far (fragmented files only) and makes it durable.</summary>
    internal void Flush()
    {
        if (!_fragmented || _fragment.Count == 0) { _fragmentStart = -1; return; }
        var moof = Boxes.Moof(++_sequence, _fragment, _tracks.Length, out var runs);
        var moofStart = _file.Position;
        _file.Write(moof);
        var payload = _fragment.Sum(f => (long)f.Data.Length);
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + payload));
        "mdat"u8.CopyTo(header.AsSpan(4));
        _file.Write(header);
        var at = _file.Position;
        foreach (var run in runs)
            foreach (var index in run)
            {
                var f = _fragment[index];
                _tracks[f.Track].Samples.Add(new Mp4Sample(at, f.Data.Length, f.Time, f.Duration, f.Key));
                _file.Write(f.Data);
                at += f.Data.Length;
            }
        _ = moofStart;
        _fragment.Clear();
        _fragmentStart = -1;
        _file.Flush(true);
    }

    /// <summary>Ends the file: the last samples, then the moov (a fragmented one is made plain in place).</summary>
    internal void Finish()
    {
        if (_finished) return;
        for (var t = 0; t < _tracks.Length; t++)
            if (_pending[t] is { } last) Commit(t, last.Data, last.Time, _defaultDuration[t], last.Key);
        Array.Clear(_pending);
        _finished = true;
        if (_fragmented)
        {
            Flush();
            _file.Dispose();
            Mp4File.MakePlain(_path);
            return;
        }
        var end = _file.Position;
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)(end - _mdatStart));
        _file.Position = _mdatStart + 8;
        _file.Write(size);
        _file.Position = end;
        _file.Write(Boxes.Moov(_tracks, fragmented: false, offsetShift: 0));
        _file.Flush(true);
        _file.Dispose();
    }

    internal static void WriteFtyp(Stream s, bool fragmented)
    {
        var b = new BoxBuilder();
        b.Begin("ftyp");
        b.Ascii("isom"); b.U32(0x200);
        b.Ascii("isom"); b.Ascii("iso6"); b.Ascii("mp41");
        if (fragmented) b.Ascii("dash");
        b.End();
        s.Write(b.ToArray());
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            try { if (_fragmented) Flush(); } catch (IOException) { }
            _file.Dispose();
        }
    }
}

/// <summary>An MP4 read back: its tracks and samples (plain or fragmented), and where its top-level boxes are.</summary>
internal sealed class Mp4File
{
    internal readonly List<Mp4Track> Tracks = [];
    internal readonly List<(string Type, long Offset, long Size)> TopBoxes = [];
    internal bool Fragmented;
    internal string Path = "";

    internal Mp4Track? Video => Tracks.FirstOrDefault(t => t.Info.IsVideo);
    internal IEnumerable<Mp4Track> Audio => Tracks.Where(t => !t.Info.IsVideo);
    internal double DurationMs => Video?.DurationMs ?? Tracks.Select(t => t.DurationMs).DefaultIfEmpty(0).Max();

    internal static Mp4File Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var mp4 = new Mp4File { Path = path };
        var trackById = new Dictionary<uint, Mp4Track>();
        var defaults = new Dictionary<uint, (int Duration, int Size, uint Flags)>();
        long position = 0;
        var length = file.Length;
        while (position + 8 <= length)
        {
            file.Position = position;
            var header = new byte[16];
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8;
            if (size == 1) { file.ReadExactly(header, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); headerSize = 16; }
            else if (size == 0) size = length - position;
            if (size < headerSize) break;
            // A box past the end is a file cut short: what came before it still reads.
            var whole = position + size <= length;
            mp4.TopBoxes.Add((type, position, size));
            if (type is "moov" or "moof" && whole)
            {
                var body = new byte[size - headerSize];
                file.ReadExactly(body);
                if (type == "moov") ReadMoov(mp4, body, trackById, defaults);
                else ReadMoof(mp4, body, position, trackById, defaults);
            }
            if (!whole) break;
            position += size;
        }
        return mp4;
    }

    // ---------------------------------------------------------------- moov

    private static void ReadMoov(Mp4File mp4, byte[] moov, Dictionary<uint, Mp4Track> byId, Dictionary<uint, (int, int, uint)> defaults)
    {
        foreach (var (type, body) in Children(moov, 0, moov.Length))
        {
            if (type == "mvex")
            {
                mp4.Fragmented = true;
                foreach (var (t, trex) in Children(moov, body.Start, body.End))
                    if (t == "trex")
                    {
                        var s = moov.AsSpan(trex.Start);
                        defaults[BE32(s[4..])] = ((int)BE32(s[12..]), (int)BE32(s[16..]), BE32(s[20..]));
                    }
            }
            if (type != "trak") continue;
            uint id = 0;
            int width = 0, height = 0, timescale = 1000;
            string handler = "", name = "", format = "";
            byte[] config = [];
            var hdr = false;
            List<(uint Count, uint Delta)> stts = [];
            List<uint> stss = [], stsz = [];
            List<(uint First, uint Per)> stsc = [];
            List<long> offsets = [];
            uint fixedSize = 0, sampleCount = 0;
            void Walk(int start, int end)
            {
                foreach (var (t, b) in Children(moov, start, end))
                {
                    var s = moov.AsSpan(b.Start, b.End - b.Start);
                    switch (t)
                    {
                        case "mdia" or "minf" or "stbl": Walk(b.Start, b.End); break;
                        case "tkhd":
                            id = s[0] == 1 ? BE32(s[20..]) : BE32(s[12..]);
                            var dims = s[^8..];
                            width = (int)(BE32(dims) >> 16);
                            height = (int)(BE32(dims[4..]) >> 16);
                            break;
                        case "mdhd": timescale = (int)(s[0] == 1 ? BE32(s[20..]) : BE32(s[12..])); break;
                        case "hdlr":
                            handler = Encoding.ASCII.GetString(s.Slice(8, 4));
                            name = s.Length > 24 ? Encoding.UTF8.GetString(s[24..]).TrimEnd('\0') : "";
                            break;
                        case "stsd":
                            if (s.Length >= 16)
                            {
                                format = Encoding.ASCII.GetString(s.Slice(12, 4));
                                (config, hdr) = SampleEntryConfig(moov, b.Start + 8, b.End, format);
                            }
                            break;
                        case "stts":
                            for (var i = 0; i < BE32(s[4..]); i++) stts.Add((BE32(s[(8 + i * 8)..]), BE32(s[(12 + i * 8)..])));
                            break;
                        case "stss":
                            for (var i = 0; i < BE32(s[4..]); i++) stss.Add(BE32(s[(8 + i * 4)..]));
                            break;
                        case "stsz":
                            fixedSize = BE32(s[4..]);
                            sampleCount = BE32(s[8..]);
                            if (fixedSize == 0) for (var i = 0; i < sampleCount; i++) stsz.Add(BE32(s[(12 + i * 4)..]));
                            break;
                        case "stsc":
                            for (var i = 0; i < BE32(s[4..]); i++) stsc.Add((BE32(s[(8 + i * 12)..]), BE32(s[(12 + i * 12)..])));
                            break;
                        case "stco":
                            for (var i = 0; i < BE32(s[4..]); i++) offsets.Add(BE32(s[(8 + i * 4)..]));
                            break;
                        case "co64":
                            for (var i = 0; i < BE32(s[4..]); i++) offsets.Add((long)BinaryPrimitives.ReadUInt64BigEndian(s[(8 + i * 8)..]));
                            break;
                    }
                }
            }
            Walk(body.Start, body.End);
            if (handler is not ("vide" or "soun")) continue;
            var track = new Mp4Track(new TrackInfo(handler, format, config, width, height, timescale, name, hdr));
            // The sample tables, unrolled.
            var count = (int)Math.Max(sampleCount, (uint)stsz.Count);
            var sizes = stsz.Count > 0 ? stsz : Enumerable.Repeat(fixedSize, count).ToList();
            var keys = stss.Count == 0 ? null : new HashSet<uint>(stss);
            long time = 0;
            var durations = new List<int>(count);
            foreach (var (n, delta) in stts) for (var i = 0; i < n; i++) durations.Add((int)delta);
            var sample = 0;
            for (var chunk = 0; chunk < offsets.Count && sample < count; chunk++)
            {
                uint per = 1;
                foreach (var (first, p) in stsc) if (first <= chunk + 1) per = p; else break;
                var offset = offsets[chunk];
                for (var i = 0; i < per && sample < count; i++, sample++)
                {
                    var d = sample < durations.Count ? durations[sample] : 0;
                    track.Samples.Add(new Mp4Sample(offset, (int)sizes[sample], time, d, keys is null || keys.Contains((uint)sample + 1)));
                    offset += sizes[sample];
                    time += d;
                }
            }
            mp4.Tracks.Add(track);
            byId[id] = track;
        }
    }

    private static (byte[] Config, bool Hdr) SampleEntryConfig(byte[] data, int start, int end, string format)
    {
        // stsd's body: version/flags, count, then the entry box.
        var entry = start; // the entry box header
        var entrySize = (int)BE32(data.AsSpan(entry));
        var childrenStart = entry + 8 + (format == "mp4a" ? 28 : 78);
        byte[] config = [];
        var hdr = false;
        foreach (var (t, b) in Children(data, childrenStart, Math.Min(end, entry + entrySize)))
        {
            var s = data.AsSpan(b.Start, b.End - b.Start);
            switch (t)
            {
                case "avcC" or "hvcC" or "av1C": config = s.ToArray(); break;
                case "esds": config = s.ToArray(); break;
                case "colr" when s.Length >= 10 && Encoding.ASCII.GetString(s[..4]) == "nclx":
                    hdr = BinaryPrimitives.ReadUInt16BigEndian(s[6..]) == 16;
                    break;
            }
        }
        return (config, hdr);
    }

    // ---------------------------------------------------------------- moof

    private static void ReadMoof(Mp4File mp4, byte[] moof, long moofOffset, Dictionary<uint, Mp4Track> byId, Dictionary<uint, (int Duration, int Size, uint Flags)> defaults)
    {
        mp4.Fragmented = true;
        foreach (var (type, traf) in Children(moof, 0, moof.Length))
        {
            if (type != "traf") continue;
            Mp4Track? track = null;
            uint id = 0;
            long baseOffset = moofOffset, decodeTime = -1;
            int defDuration = 0, defSize = 0;
            uint defFlags = 0;
            foreach (var (t, b) in Children(moof, traf.Start, traf.End))
            {
                var s = moof.AsSpan(b.Start, b.End - b.Start);
                var flags = BE32(s) & 0xFFFFFF;
                switch (t)
                {
                    case "tfhd":
                        id = BE32(s[4..]);
                        track = byId.GetValueOrDefault(id);
                        if (defaults.TryGetValue(id, out var d)) (defDuration, defSize, defFlags) = d;
                        var at = 8;
                        if ((flags & 0x1) != 0) { baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(s[at..]); at += 8; }
                        if ((flags & 0x2) != 0) at += 4;
                        if ((flags & 0x8) != 0) { defDuration = (int)BE32(s[at..]); at += 4; }
                        if ((flags & 0x10) != 0) { defSize = (int)BE32(s[at..]); at += 4; }
                        if ((flags & 0x20) != 0) defFlags = BE32(s[at..]);
                        break;
                    case "tfdt":
                        decodeTime = s[0] == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(s[4..]) : BE32(s[4..]);
                        break;
                    case "trun" when track is not null:
                        var n = (int)BE32(s[4..]);
                        var p = 8;
                        long offset = baseOffset;
                        if ((flags & 0x1) != 0) { offset = baseOffset + BinaryPrimitives.ReadInt32BigEndian(s[p..]); p += 4; }
                        uint first = defFlags;
                        var firstSet = false;
                        if ((flags & 0x4) != 0) { first = BE32(s[p..]); firstSet = true; p += 4; }
                        var time = decodeTime >= 0 ? decodeTime : track.Samples.Count > 0 ? track.Samples[^1].Time + track.Samples[^1].Duration : 0;
                        for (var i = 0; i < n; i++)
                        {
                            var duration = defDuration; var size = defSize; var sflags = i == 0 && firstSet ? first : defFlags;
                            if ((flags & 0x100) != 0) { duration = (int)BE32(s[p..]); p += 4; }
                            if ((flags & 0x200) != 0) { size = (int)BE32(s[p..]); p += 4; }
                            if ((flags & 0x400) != 0) { sflags = BE32(s[p..]); p += 4; }
                            if ((flags & 0x800) != 0) p += 4;
                            track.Samples.Add(new Mp4Sample(offset, size, time, duration, (sflags & 0x10000) == 0));
                            offset += size;
                            time += duration;
                        }
                        decodeTime = time;
                        break;
                }
            }
        }
    }

    /// <summary>
    /// A fragmented file made plain in place: its moov (which says fragments follow) and every moof become 'free', and a
    /// whole moov describing every sample goes on the end. A fragment cut short at the end is dropped from the tables.
    /// </summary>
    internal static void MakePlain(string path)
    {
        var mp4 = Read(path);
        if (!mp4.Fragmented) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long end = 0;
        foreach (var (type, offset, size) in mp4.TopBoxes)
        {
            if (offset + size > file.Length) break; // a box cut short ends the file
            end = offset + size;
        }
        // Samples past the last whole box are not in the file.
        foreach (var t in mp4.Tracks) t.Samples.RemoveAll(s => s.Offset + s.Size > end);
        foreach (var (type, offset, size) in mp4.TopBoxes)
            if (type is "moov" or "moof" && offset + size <= end)
            {
                file.Position = offset + 4;
                file.Write("free"u8);
            }
        file.SetLength(end);
        file.Position = end;
        file.Write(Boxes.Moov(mp4.Tracks, fragmented: false, offsetShift: 0));
        file.Flush(true);
    }

    // ---------------------------------------------------------------- helpers

    internal static uint BE32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);

    private static IEnumerable<(string Type, (int Start, int End) Body)> Children(byte[] data, int start, int end)
    {
        var at = start;
        while (at + 8 <= end)
        {
            long size = BE32(data.AsSpan(at));
            var type = Encoding.ASCII.GetString(data, at + 4, 4);
            var header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8)); header = 16; }
            else if (size == 0) size = end - at;
            if (size < header || at + size > end) yield break;
            yield return (type, (at + header, (int)(at + size)));
            at += (int)size;
        }
    }

    /// <summary>The raw bytes of a sample.</summary>
    internal static byte[] ReadSample(FileStream file, Mp4Sample sample)
    {
        var data = new byte[sample.Size];
        file.Position = sample.Offset;
        file.ReadExactly(data);
        return data;
    }
}

/// <summary>Builds boxes with sizes patched as they close.</summary>
internal sealed class BoxBuilder
{
    private readonly MemoryStream _m = new();
    private readonly Stack<long> _open = new();

    internal void Begin(string type)
    {
        _open.Push(_m.Position);
        U32(0);
        Ascii(type);
    }

    internal void Full(string type, byte version, uint flags)
    {
        Begin(type);
        U32(((uint)version << 24) | flags);
    }

    internal void End()
    {
        var start = _open.Pop();
        var size = _m.Position - start;
        var at = _m.Position;
        _m.Position = start;
        U32((uint)size);
        _m.Position = at;
    }

    internal void U8(int v) => _m.WriteByte((byte)v);
    internal void U16(int v) { _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); }
    internal void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _m.Write(b); }
    internal void I32(int v) => U32(unchecked((uint)v));
    internal void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _m.Write(b); }
    internal void Ascii(string s) => _m.Write(Encoding.ASCII.GetBytes(s));
    internal void Bytes(ReadOnlySpan<byte> b) => _m.Write(b);
    internal void Zeros(int n) { for (var i = 0; i < n; i++) _m.WriteByte(0); }
    internal byte[] ToArray() => _m.ToArray();
}

internal static class Boxes
{
    private static readonly uint[] Identity = [0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000];

    internal static byte[] Moov(IReadOnlyList<Mp4Track> tracks, bool fragmented, long offsetShift)
    {
        const int movieScale = 1000;
        var b = new BoxBuilder();
        b.Begin("moov");
        var duration = tracks.Select(t => t.DurationUnits * movieScale / Math.Max(1, t.Info.Timescale)).DefaultIfEmpty(0).Max();
        b.Full("mvhd", 1, 0);
        b.U64(0); b.U64(0); b.U32(movieScale); b.U64((ulong)duration);
        b.U32(0x10000); b.U16(0x100); b.Zeros(10);
        foreach (var m in Identity) b.U32(m);
        b.Zeros(24);
        b.U32((uint)tracks.Count + 1);
        b.End();
        for (var i = 0; i < tracks.Count; i++) Trak(b, tracks[i], (uint)(i + 1), movieScale, fragmented, offsetShift);
        if (fragmented)
        {
            b.Begin("mvex");
            for (var i = 0; i < tracks.Count; i++)
            {
                b.Full("trex", 0, 0);
                b.U32((uint)(i + 1)); b.U32(1); b.U32(0); b.U32(0); b.U32(0);
                b.End();
            }
            b.End();
        }
        b.End();
        return b.ToArray();
    }

    private static void Trak(BoxBuilder b, Mp4Track track, uint id, int movieScale, bool fragmented, long shift)
    {
        var info = track.Info;
        var units = track.DurationUnits;
        b.Begin("trak");
        // Video and the first sound track (the mix) play; the other sound tracks are there to pick.
        b.Full("tkhd", 1, info.IsVideo || id == 2 ? 3u : 2u);
        b.U64(0); b.U64(0); b.U32(id); b.U32(0);
        b.U64((ulong)(units * movieScale / Math.Max(1, info.Timescale)));
        b.Zeros(8); b.U16(0);
        b.U16(info.IsVideo ? 0 : 1); // alternate group: the sound tracks are alternatives to each other
        b.U16(info.IsVideo ? 0 : (id == 2 ? 0x100 : 0)); b.U16(0);
        foreach (var m in Identity) b.U32(m);
        b.U32((uint)info.Width << 16); b.U32((uint)info.Height << 16);
        b.End();
        b.Begin("mdia");
        b.Full("mdhd", 1, 0);
        b.U64(0); b.U64(0); b.U32((uint)info.Timescale); b.U64((ulong)units);
        b.U16(0x55C4); b.U16(0); // undetermined language
        b.End();
        b.Full("hdlr", 0, 0);
        b.U32(0); b.Ascii(info.Handler); b.Zeros(12); b.Bytes(System.Text.Encoding.UTF8.GetBytes(info.Name)); b.U8(0);
        b.End();
        b.Begin("minf");
        if (info.IsVideo) { b.Full("vmhd", 0, 1); b.Zeros(8); b.End(); }
        else { b.Full("smhd", 0, 0); b.Zeros(4); b.End(); }
        b.Begin("dinf"); b.Full("dref", 0, 0); b.U32(1); b.Full("url ", 0, 1); b.End(); b.End(); b.End();
        b.Begin("stbl");
        SampleDescription(b, info);
        var samples = fragmented ? [] : track.Samples;
        // stts: runs of equal durations.
        var runs = new List<(uint Count, int Delta)>();
        foreach (var s in samples)
            if (runs.Count > 0 && runs[^1].Delta == s.Duration) runs[^1] = (runs[^1].Count + 1, s.Duration);
            else runs.Add((1, s.Duration));
        b.Full("stts", 0, 0); b.U32((uint)runs.Count); foreach (var (c, d) in runs) { b.U32(c); b.U32((uint)d); } b.End();
        if (info.IsVideo && !fragmented)
        {
            var keys = samples.Select((s, i) => (s, i)).Where(x => x.s.Key).Select(x => (uint)x.i + 1).ToList();
            b.Full("stss", 0, 0); b.U32((uint)keys.Count); foreach (var k in keys) b.U32(k); b.End();
        }
        // Chunks: each run of samples next to each other in the file.
        var chunks = new List<(long Offset, int Count)>();
        long next = -1;
        foreach (var s in samples)
        {
            if (chunks.Count > 0 && s.Offset == next && chunks[^1].Count < 4096) chunks[^1] = (chunks[^1].Offset, chunks[^1].Count + 1);
            else chunks.Add((s.Offset + shift, 1));
            next = s.Offset + s.Size;
        }
        var stsc = new List<(int First, int Per)>();
        for (var i = 0; i < chunks.Count; i++)
            if (stsc.Count == 0 || stsc[^1].Per != chunks[i].Count) stsc.Add((i + 1, chunks[i].Count));
        b.Full("stsc", 0, 0); b.U32((uint)stsc.Count); foreach (var (f, p) in stsc) { b.U32((uint)f); b.U32((uint)p); b.U32(1); } b.End();
        b.Full("stsz", 0, 0); b.U32(0); b.U32((uint)samples.Count); foreach (var s in samples) b.U32((uint)s.Size); b.End();
        b.Full("co64", 0, 0); b.U32((uint)chunks.Count); foreach (var c in chunks) b.U64((ulong)c.Offset); b.End();
        b.End(); // stbl
        b.End(); // minf
        b.End(); // mdia
        b.End(); // trak
    }

    private static void SampleDescription(BoxBuilder b, TrackInfo info)
    {
        b.Full("stsd", 0, 0);
        b.U32(1);
        b.Begin(info.Format);
        b.Zeros(6); b.U16(1);
        if (info.IsVideo)
        {
            b.Zeros(16);
            b.U16(info.Width); b.U16(info.Height);
            b.U32(0x480000); b.U32(0x480000); b.U32(0); b.U16(1);
            var name = new byte[32];
            var label = System.Text.Encoding.ASCII.GetBytes("Exo Capture");
            name[0] = (byte)label.Length;
            label.CopyTo(name, 1);
            b.Bytes(name);
            b.U16(0x18); b.U16(0xFFFF);
            var configType = info.Format switch { "avc1" => "avcC", "hvc1" => "hvcC", _ => "av1C" };
            b.Begin(configType); b.Bytes(StripBoxHeader(info.Config, configType)); b.End();
            b.Begin("colr");
            b.Ascii("nclx");
            b.U16(info.Hdr ? 9 : 1); b.U16(info.Hdr ? 16 : 1); b.U16(info.Hdr ? 9 : 1); b.U8(0);
            b.End();
        }
        else
        {
            b.Zeros(8); b.U16(2); b.U16(16); b.Zeros(4); b.U32(48000u << 16);
            b.Full("esds", 0, 0);
            var asc = info.Config.Length == 2 ? info.Config : null;
            if (asc is null) b.Bytes(info.Config.AsSpan(4)); // an esds read from a file, as it was
            else
            {
                // ES_Descriptor > DecoderConfigDescriptor > DecoderSpecificInfo, then the SL config.
                b.U8(3); b.U8(25); b.U16(0); b.U8(0);
                b.U8(4); b.U8(17); b.U8(0x40); b.U8(0x15); b.U8(0); b.U16(0); b.U32(160000); b.U32(160000);
                b.U8(5); b.U8(2); b.Bytes(asc);
                b.U8(6); b.U8(1); b.U8(2);
            }
            b.End();
        }
        b.End();
        b.End();
    }

    // A config read from a file came with its version/flags-free body; one built here is the body too.
    private static ReadOnlySpan<byte> StripBoxHeader(byte[] config, string type) => config;

    /// <summary>A fragment: mfhd and one traf per track with samples, each run's data in track order after the moof.</summary>
    internal static byte[] Moof(uint sequence, List<(int Track, byte[] Data, long Time, int Duration, bool Key)> samples, int trackCount, out List<List<int>> runs)
    {
        var grouped = new List<List<int>>();
        for (var t = 0; t < trackCount; t++)
        {
            var run = new List<int>();
            for (var i = 0; i < samples.Count; i++) if (samples[i].Track == t) run.Add(i);
            if (run.Count > 0) grouped.Add(run);
        }
        runs = grouped;
        // Two passes: the trun data offsets depend on the moof's own size.
        byte[] Build(int moofSize)
        {
            var b = new BoxBuilder();
            b.Begin("moof");
            b.Full("mfhd", 0, 0); b.U32(sequence); b.End();
            var offset = moofSize + 8;
            foreach (var run in grouped)
            {
                var track = samples[run[0]].Track;
                b.Begin("traf");
                b.Full("tfhd", 0, 0x020000); b.U32((uint)track + 1); b.End();
                b.Full("tfdt", 1, 0); b.U64((ulong)samples[run[0]].Time); b.End();
                b.Full("trun", 0, 0x000701); b.U32((uint)run.Count); b.I32(offset);
                foreach (var i in run)
                {
                    var s = samples[i];
                    b.U32((uint)s.Duration); b.U32((uint)s.Data.Length); b.U32(s.Key ? 0x02000000u : 0x01010000u);
                    offset += s.Data.Length;
                }
                b.End();
                b.End();
            }
            b.End();
            return b.ToArray();
        }
        var first = Build(0);
        return Build(first.Length);
    }
}
