using System.Buffers;
using System.IO.Compression;
using System.Text.Json;

namespace ExoLauncher.Services.DiscordClient;

internal sealed class GatewayZlibInflater : IDisposable
{
    private readonly ChunkFeedStream _feed = new();
    private readonly ZLibStream _zlib;

    internal GatewayZlibInflater()
    {
        _zlib = new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
    }

    internal static bool HasFlushSuffix(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && data[^4] == 0x00
        && data[^3] == 0x00
        && data[^2] == 0xFF
        && data[^1] == 0xFF;

    internal byte[] Decompress(ReadOnlySpan<byte> compressed, int maxBytes)
    {
        var copy = ArrayPool<byte>.Shared.Rent(compressed.Length);
        try
        {
            compressed.CopyTo(copy);
            var (output, length) = DecompressToPooled(copy, compressed.Length, maxBytes);
            try
            {
                return output.AsSpan(0, length).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    internal JsonDocument ParseCompressed(byte[] compressed, int length, int maxBytes, out int decompressedLength)
    {
        var (output, total) = DecompressToPooled(compressed, length, maxBytes);
        try
        {
            decompressedLength = total;
            return Parse(output.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private (byte[] Buffer, int Length) DecompressToPooled(byte[] compressed, int length, int maxBytes)
    {
        _feed.SetInput(compressed, 0, length);
        var initial = Math.Clamp(Math.Max(64 * 1024, length * 4), 1024, Math.Max(1024, maxBytes));
        var output = ArrayPool<byte>.Shared.Rent(initial);
        var total = 0;
        try
        {
            while (true)
            {
                if (total == output.Length)
                {
                    if (total >= maxBytes) throw new IOException("Gateway message too large.");
                    var nextSize = (int)Math.Min((long)output.Length * 2, (long)maxBytes + 1);
                    var bigger = ArrayPool<byte>.Shared.Rent(nextSize);
                    output.AsSpan(0, total).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(output);
                    output = bigger;
                }
                var allowance = Math.Min(output.Length - total, maxBytes - total + 1);
                var read = _zlib.Read(output, total, allowance);
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw new IOException("Gateway message too large.");
            }
            return (output, total);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        return JsonDocument.ParseValue(ref reader);
    }

    public void Dispose()
    {
        _zlib.Dispose();
        _feed.Dispose();
    }

    private sealed class ChunkFeedStream : Stream
    {
        private byte[] _buffer = [];
        private int _pos;
        private int _end;

        internal void SetInput(byte[] buffer, int offset, int count)
        {
            _buffer = buffer;
            _pos = offset;
            _end = offset + count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _end - _pos;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var available = _end - _pos;
            if (available <= 0 || buffer.Length == 0) return 0;
            var take = Math.Min(available, buffer.Length);
            _buffer.AsSpan(_pos, take).CopyTo(buffer);
            _pos += take;
            return take;
        }
    }
}
