using System.Runtime.InteropServices;
using Vortice.WIC;

namespace ExoCapture.Media;

/// <summary>
/// An animated GIF, written here: each frame quantized to its own 256 colours by Windows (WIC's median cut and error
/// diffusion), then LZW-coded into GIF89a with a frame delay and a NETSCAPE loop.
/// </summary>
internal sealed class GifWriter : IDisposable
{
    private readonly Stream _out;
    private readonly int _width, _height;
    private static readonly Lazy<IWICImagingFactory> Factory = new(() => new IWICImagingFactory());

    internal GifWriter(Stream output, int width, int height)
    {
        (_out, _width, _height) = (output, width, height);
        Write("GIF89a"u8);
        U16(width); U16(height);
        _out.WriteByte(0x70); // no global colour table, 8-bit colour resolution
        _out.WriteByte(0); _out.WriteByte(0);
        // Loop forever.
        _out.Write([0x21, 0xFF, 0x0B]);
        Write("NETSCAPE2.0"u8);
        _out.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    /// <summary>One frame (BGRA, the GIF's size), shown for <paramref name="delayCs"/> hundredths of a second.</summary>
    internal void Add(byte[] bgra, int delayCs)
    {
        var (indices, palette) = Quantize(bgra, _width, _height);
        _out.Write([0x21, 0xF9, 0x04, 0x04]); // graphic control: disposal "leave", no transparency
        U16(delayCs);
        _out.WriteByte(0); _out.WriteByte(0);
        _out.WriteByte(0x2C);
        U16(0); U16(0); U16(_width); U16(_height);
        _out.WriteByte(0x87); // local colour table of 256
        for (var i = 0; i < 256; i++)
        {
            var c = i < palette.Length ? palette[i] : 0u;
            _out.WriteByte((byte)(c >> 16)); _out.WriteByte((byte)(c >> 8)); _out.WriteByte((byte)c);
        }
        _out.WriteByte(8);
        var data = Lzw(indices, 8);
        for (var at = 0; at < data.Length; at += 255)
        {
            var n = Math.Min(255, data.Length - at);
            _out.WriteByte((byte)n);
            _out.Write(data, at, n);
        }
        _out.WriteByte(0);
    }

    public void Dispose()
    {
        _out.WriteByte(0x3B);
        _out.Flush();
    }

    private void Write(ReadOnlySpan<byte> b) => _out.Write(b);
    private void U16(int v) { _out.WriteByte((byte)v); _out.WriteByte((byte)(v >> 8)); }

    /// <summary>The picture as palette indices and its palette (0xAARRGGBB), by WIC.</summary>
    internal static (byte[] Indices, uint[] Palette) Quantize(byte[] bgra, int width, int height)
    {
        var wic = Factory.Value;
        using var bitmap = wic.CreateBitmapFromMemory((uint)width, (uint)height, PixelFormat.Format32bppBGRA, bgra, (uint)(width * 4));
        using var palette = wic.CreatePalette();
        palette.InitializeFromBitmap(bitmap, 256, false);
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(bitmap, PixelFormat.Format8bppIndexed, BitmapDitherType.ErrorDiffusion, palette, 0, BitmapPaletteType.Custom);
        var indices = new byte[width * height];
        converter.CopyPixels((uint)width, indices);
        return (indices, palette.GetColors());
    }

    /// <summary>GIF's variable-width LZW (codes up to 12 bits, a clear code when the table fills).</summary>
    internal static byte[] Lzw(byte[] indices, int minBits)
    {
        var clear = 1 << minBits;
        var end = clear + 1;
        var output = new List<byte>(indices.Length / 2);
        int bitBuffer = 0, bitCount = 0;
        var codeSize = minBits + 1;
        var table = new Dictionary<int, int>(4096);
        var next = end + 1;
        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8) { output.Add((byte)bitBuffer); bitBuffer >>= 8; bitCount -= 8; }
        }
        Emit(clear);
        if (indices.Length == 0) { Emit(end); if (bitCount > 0) output.Add((byte)bitBuffer); return [.. output]; }
        var prefix = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var c = indices[i];
            var key = (prefix << 8) | c;
            if (table.TryGetValue(key, out var found)) { prefix = found; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                table[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear);
                table.Clear();
                next = end + 1;
                codeSize = minBits + 1;
            }
            prefix = c;
        }
        Emit(prefix);
        Emit(end);
        if (bitCount > 0) output.Add((byte)bitBuffer);
        return [.. output];
    }
}

/// <summary>
/// A WebM file (Matroska): one VP9 video track in clusters of simple blocks, a cue for every key frame, and the
/// segment's size and duration written in at the end.
/// </summary>
internal sealed class WebMWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly long _segmentData, _durationAt;
    private long _clusterStart = -1, _clusterTime;
    private long _lastMs;
    private readonly List<(long Ms, long ClusterOffset)> _cues = [];

    internal WebMWriter(string path, int width, int height)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        Element(0x1A45DFA3, Concat(
            Uint(0x4286, 1), Uint(0x42F7, 1), Uint(0x42F2, 4), Uint(0x42F3, 8),
            Str(0x4282, "webm"), Uint(0x4287, 4), Uint(0x4285, 2)));
        // Segment with an 8-byte size, patched at the end.
        WriteId(0x18538067);
        _file.Write([0x01, 0, 0, 0, 0, 0, 0, 0]);
        _segmentData = _file.Position;
        var info = Concat(Uint(0x2AD7B1, 1_000_000), Str(0x4D80, "Exo Capture"), Str(0x5741, "Exo Capture"), Float(0x4489, 0));
        var infoStart = _file.Position;
        Element(0x1549A966, info);
        // The duration float is the last 8 bytes of Info.
        _durationAt = _file.Position - 8;
        _ = infoStart;
        var video = Concat(Uint(0xB0, (ulong)width), Uint(0xBA, (ulong)height));
        var entry = Concat(Uint(0xD7, 1), Uint(0x73C5, 1), Uint(0x83, 1), Str(0x86, "V_VP9"), Uint(0x9C, 0), Bin(0xE0, video));
        Element(0x1654AE6B, Bin(0xAE, entry));
    }

    /// <summary>One frame at <paramref name="ms"/> milliseconds; a key frame starts a new cluster.</summary>
    internal void Add(byte[] frame, long ms, bool key)
    {
        if (_clusterStart < 0 || key || ms - _clusterTime > 30_000)
        {
            CloseCluster();
            _clusterStart = _file.Position;
            _clusterTime = ms;
            WriteId(0x1F43B675);
            _file.Write([0x01, 0, 0, 0, 0, 0, 0, 0]);
            _file.Write(Uint(0xE7, (ulong)ms));
            if (key) _cues.Add((ms, _clusterStart - _segmentData));
        }
        var relative = (short)(ms - _clusterTime);
        var block = new byte[4 + frame.Length];
        block[0] = 0x81; // track 1
        block[1] = (byte)(relative >> 8);
        block[2] = (byte)relative;
        block[3] = (byte)(key ? 0x80 : 0);
        frame.CopyTo(block, 4);
        _file.Write(Bin(0xA3, block));
        _lastMs = ms;
    }

    private void CloseCluster()
    {
        if (_clusterStart < 0) return;
        var end = _file.Position;
        Patch(_clusterStart + 4, end - _clusterStart - 12);
        _file.Position = end;
    }

    private void Patch(long at, long size)
    {
        var here = _file.Position;
        _file.Position = at;
        var b = new byte[8];
        b[0] = 0x01;
        for (var i = 7; i >= 1; i--) { b[i] = (byte)size; size >>= 8; }
        _file.Write(b);
        _file.Position = here;
    }

    public void Dispose()
    {
        CloseCluster();
        var cues = new MemoryStream();
        foreach (var (ms, offset) in _cues)
            cues.Write(Bin(0xBB, Concat(Uint(0xB3, (ulong)ms), Bin(0xB7, Concat(Uint(0xF7, 1), Uint(0xF1, (ulong)offset))))));
        Element(0x1C53BB6B, cues.ToArray());
        var end = _file.Position;
        Patch(_segmentData - 8, end - _segmentData);
        _file.Position = _durationAt;
        var d = BitConverter.GetBytes((double)_lastMs + 40);
        Array.Reverse(d);
        _file.Write(d);
        _file.Dispose();
    }

    // ---------------------------------------------------------------- EBML

    private void Element(uint id, byte[] body)
    {
        WriteId(id);
        _file.Write(Size(body.Length));
        _file.Write(body);
    }

    private void WriteId(uint id) => _file.Write(Id(id));

    private static byte[] Id(uint id)
    {
        var bytes = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        var b = new byte[bytes];
        for (var i = bytes - 1; i >= 0; i--) { b[i] = (byte)id; id >>= 8; }
        return b;
    }

    private static byte[] Size(long size)
    {
        var length = 1;
        while (length < 8 && size >= (1L << (7 * length)) - 1) length++;
        var b = new byte[length];
        for (var i = length - 1; i >= 0; i--) { b[i] = (byte)size; size >>= 8; }
        b[0] |= (byte)(0x80 >> (length - 1));
        return b;
    }

    private static byte[] Bin(uint id, byte[] body) => [.. Id(id), .. Size(body.Length), .. body];

    private static byte[] Uint(uint id, ulong value)
    {
        var length = 1;
        while (length < 8 && value >> (8 * length) != 0) length++;
        var b = new byte[length];
        for (var i = length - 1; i >= 0; i--) { b[i] = (byte)value; value >>= 8; }
        return Bin(id, b);
    }

    private static byte[] Float(uint id, double value)
    {
        var b = BitConverter.GetBytes(value);
        Array.Reverse(b);
        return Bin(id, b);
    }

    private static byte[] Str(uint id, string value) => Bin(id, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
