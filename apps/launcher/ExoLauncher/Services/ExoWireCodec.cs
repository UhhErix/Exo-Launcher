using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal enum ExoWireFrameKind : byte
{
    Request = 1,
    ResponseOk = 2,
    ResponseErr = 3,
    Event = 4,
    BinaryMedia = 5,
    Hello = 6,
    HelloAck = 7,
}

internal enum ExoWireEncoding : byte
{
    Empty = 0,
    JsonUtf8 = 1,
    BinaryTyped = 2,
}

internal readonly struct ExoWireFrameHeader(
    byte version,
    ExoWireFrameKind kind,
    ushort methodId,
    ulong reqId,
    uint payloadLen)
{
    internal byte Version { get; } = version;
    internal ExoWireFrameKind Kind { get; } = kind;
    internal ushort MethodId { get; } = methodId;
    internal ulong ReqId { get; } = reqId;
    internal uint PayloadLen { get; } = payloadLen;

    internal void WriteTo(Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest, ExoWireCodec.WireMagic);
        dest[4] = Version;
        dest[5] = (byte)Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], MethodId);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[8..], ReqId);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[16..], PayloadLen);
    }

    internal static bool TryRead(ReadOnlySpan<byte> src, out ExoWireFrameHeader header)
    {
        header = default;
        if (src.Length < ExoWireCodec.HeaderLen) return false;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(src);
        if (magic != ExoWireCodec.WireMagic) return false;
        var version = src[4];
        if (version != ExoWireCodec.WireVersion) return false;
        var kindByte = src[5];
        if (kindByte is < 1 or > 7) return false;
        var methodId = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]);
        var reqId = BinaryPrimitives.ReadUInt64LittleEndian(src[8..]);
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(src[16..]);
        if (payloadLen > ExoWireCodec.MaxFrameBytes) return false;
        header = new ExoWireFrameHeader(version, (ExoWireFrameKind)kindByte, methodId, reqId, payloadLen);
        return true;
    }
}

internal readonly struct ExoWireHello(byte minVersion, byte maxVersion, ushort flags, uint maxFrameBytes, ulong schemaHash)
{
    internal byte MinVersion { get; } = minVersion;
    internal byte MaxVersion { get; } = maxVersion;
    internal ushort Flags { get; } = flags;
    internal uint MaxFrameBytes { get; } = maxFrameBytes;
    internal ulong SchemaHash { get; } = schemaHash;

    internal static bool TryRead(ReadOnlySpan<byte> src, out ExoWireHello hello)
    {
        hello = default;
        if (src.Length != ExoWireCodec.HelloPayloadLen) return false;
        hello = new ExoWireHello(
            src[0],
            src[1],
            BinaryPrimitives.ReadUInt16LittleEndian(src[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[8..]));
        return true;
    }
}

internal readonly struct ExoWireHelloAck(byte negotiatedVersion, byte status, ushort flags, uint maxFrameBytes, ulong hostSchemaHash)
{
    internal byte NegotiatedVersion { get; } = negotiatedVersion;
    internal byte Status { get; } = status;
    internal ushort Flags { get; } = flags;
    internal uint MaxFrameBytes { get; } = maxFrameBytes;
    internal ulong HostSchemaHash { get; } = hostSchemaHash;

    internal void WriteTo(Span<byte> dest)
    {
        dest[0] = NegotiatedVersion;
        dest[1] = Status;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], MaxFrameBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[8..], HostSchemaHash);
    }
}

internal readonly struct ExoWireOutboundFrame(byte[] buffer, int length, bool pooled, bool isBinaryWire)
{
    internal byte[] Buffer { get; } = buffer;
    internal int Length { get; } = length;
    internal bool Pooled { get; } = pooled;
    internal bool IsBinaryWire { get; } = isBinaryWire;

    internal void Release()
    {
        if (Pooled && Buffer is not null)
            ArrayPool<byte>.Shared.Return(Buffer);
    }
}

internal sealed class ExoWireInboundRequest : IDisposable
{
    private byte[]? _pooledPayload;

    internal ulong ReqId { get; init; }
    internal ushort MethodId { get; init; }
    internal string MethodName { get; init; } = "";
    internal ExoWireEncoding Encoding { get; init; }
    internal int PayloadOffset { get; init; }
    internal int PayloadLength { get; init; }
    internal string? BinaryIdParam { get; init; }
    internal byte BinaryFlags { get; init; }
    internal ulong BinaryCancelTargetId { get; init; }

    internal ExoWireInboundRequest(byte[]? pooledPayload)
    {
        _pooledPayload = pooledPayload;
    }

    internal ReadOnlyMemory<byte> JsonPayloadMemory =>
        _pooledPayload is null || PayloadLength <= 0
            ? ReadOnlyMemory<byte>.Empty
            : _pooledPayload.AsMemory(PayloadOffset, PayloadLength);

    public void Dispose()
    {
        var buf = Interlocked.Exchange(ref _pooledPayload, null);
        if (buf is not null)
            ArrayPool<byte>.Shared.Return(buf);
    }
}

internal sealed class ExoWirePooledWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    internal ExoWirePooledWriter(int initialCapacity = 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
        _written = 0;
    }

    internal int WrittenCount => _written;

    internal void ReserveHeader()
    {
        Ensure(ExoWireCodec.HeaderLen);
        _written = ExoWireCodec.HeaderLen;
    }

    public void Advance(int count)
    {
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint <= 0 ? 256 : sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint <= 0 ? 256 : sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void Ensure(int needed)
    {
        if (_written + needed <= _buffer.Length) return;
        var nextSize = Math.Max(_buffer.Length * 2, _written + needed);
        var next = ArrayPool<byte>.Shared.Rent(nextSize);
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    internal void WriteByte(byte v)
    {
        Ensure(1);
        _buffer[_written++] = v;
    }

    internal void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    internal void WriteUInt16(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_written), v);
        _written += 2;
    }

    internal void WriteUInt32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_written), v);
        _written += 4;
    }

    internal void WriteInt32(int v)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_written), v);
        _written += 4;
    }

    internal void WriteUInt64(ulong v)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_written), v);
        _written += 8;
    }

    internal void WriteDouble(double v)
    {
        Ensure(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_written), v);
        _written += 8;
    }

    internal void WriteOptUInt32(int? v)
    {
        if (v is { } n)
        {
            WriteByte(1);
            WriteUInt32((uint)Math.Max(0, n));
        }
        else
        {
            WriteByte(0);
        }
    }

    internal void WriteOptUInt64(long? v)
    {
        if (v is { } n)
        {
            WriteByte(1);
            WriteUInt64((ulong)Math.Max(0, n));
        }
        else
        {
            WriteByte(0);
        }
    }

    internal void WriteOptDouble(double? v)
    {
        if (v is { } d)
        {
            WriteByte(1);
            WriteDouble(d);
        }
        else
        {
            WriteByte(0);
        }
    }

    internal void WriteString(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            WriteUInt32(0);
            return;
        }
        var maxBytes = Encoding.UTF8.GetMaxByteCount(s.Length);
        Ensure(4 + maxBytes);
        var actual = Encoding.UTF8.GetBytes(s, _buffer.AsSpan(_written + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_written), (uint)actual);
        _written += 4 + actual;
    }

    internal void WriteShortString(string s)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(s.Length);
        Ensure(2 + maxBytes);
        var actual = Encoding.UTF8.GetBytes(s, _buffer.AsSpan(_written + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_written), (ushort)actual);
        _written += 2 + actual;
    }

    internal void WriteOptString(string? s)
    {
        if (s is null)
        {
            WriteByte(0);
            return;
        }
        WriteByte(1);
        WriteString(s);
    }

    internal void WriteStringList(IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0)
        {
            WriteUInt32(0);
            return;
        }
        WriteUInt32((uint)items.Count);
        for (var i = 0; i < items.Count; i++)
            WriteString(items[i]);
    }

    internal void WriteBytesPrefixed(ReadOnlySpan<byte> bytes)
    {
        WriteUInt32((uint)bytes.Length);
        if (bytes.Length == 0) return;
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_written));
        _written += bytes.Length;
    }

    internal void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_written));
        _written += bytes.Length;
    }

    internal void WriteJsonSubSlice(object? value, JsonSerializerOptions options)
    {
        Ensure(4);
        var lenPos = _written;
        _written += 4;
        var start = _written;
        using (var jw = new Utf8JsonWriter(this))
        {
            if (value is null)
                jw.WriteNullValue();
            else
                JsonSerializer.Serialize(jw, value, value.GetType(), options);
        }
        var len = _written - start;
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(lenPos), (uint)len);
    }

    internal ExoWireOutboundFrame DetachFrame(ExoWireFrameKind kind, ushort methodId, ulong reqId)
    {
        var payloadLen = (uint)(_written - ExoWireCodec.HeaderLen);
        var header = new ExoWireFrameHeader(ExoWireCodec.WireVersion, kind, methodId, reqId, payloadLen);
        header.WriteTo(_buffer.AsSpan(0, ExoWireCodec.HeaderLen));
        var buf = _buffer;
        var len = _written;
        _buffer = [];
        _written = 0;
        return new ExoWireOutboundFrame(buf, len, pooled: true, isBinaryWire: true);
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}

internal static class ExoWireCodec
{
    internal const uint WireMagic = 0x3157_5845u;
    internal const byte WireVersion = 1;
    internal const int HeaderLen = 20;
    internal const int HelloPayloadLen = 16;
    internal const uint MaxFrameBytes = 96u * 1024u * 1024u;

    internal const ushort FlagUseSchemaIds = 1 << 0;
    internal const ushort FlagBinaryHotCodecs = 1 << 1;

    internal const byte HelloStatusOk = 0;
    internal const byte HelloStatusSchemaMismatch = 1;

    internal static void WriteHelloAckFrame(Span<byte> dest, ExoWireHelloAck ack)
    {
        var header = new ExoWireFrameHeader(WireVersion, ExoWireFrameKind.HelloAck, 0, 0, HelloPayloadLen);
        header.WriteTo(dest);
        ack.WriteTo(dest[HeaderLen..]);
    }

    internal static bool TryDecodeRequest(
        ExoWireFrameHeader header,
        byte[]? pooledPayload,
        int payloadLen,
        out ExoWireInboundRequest? request)
    {
        request = null;
        if (header.Kind != ExoWireFrameKind.Request || payloadLen < 1)
            return false;

        var span = pooledPayload is null ? ReadOnlySpan<byte>.Empty : pooledPayload.AsSpan(0, payloadLen);
        var pos = 0;
        string? methodName;
        var methodId = header.MethodId;

        if (methodId == ExoWireSchema.UnknownId)
        {
            if (span.Length < 3) return false;
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(span);
            pos = 2;
            if (nameLen == 0 || nameLen > 128 || pos + nameLen + 1 > span.Length) return false;
            methodName = Encoding.UTF8.GetString(span.Slice(pos, nameLen));
            pos += nameLen;
            methodId = ExoWireSchema.GetMethodId(methodName);
        }
        else
        {
            methodName = ExoWireSchema.GetMethodName(methodId);
            if (methodName is null) return false;
        }

        var encodingByte = span[pos++];
        if (encodingByte > 2) return false;
        var encoding = (ExoWireEncoding)encodingByte;
        var bodyOffset = pos;
        var bodyLen = payloadLen - pos;

        if (encoding == ExoWireEncoding.BinaryTyped)
        {
            var body = span.Slice(bodyOffset, bodyLen);
            string? binaryId = null;
            byte binaryFlags = 0;
            ulong cancelTarget = 0;

            if (methodId is ExoWireSchema.MethodGameGet or ExoWireSchema.MethodGameExtras)
            {
                if (body.Length < 4) return false;
                var strLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (strLen < 0 || 4 + strLen != body.Length) return false;
                binaryId = Encoding.UTF8.GetString(body.Slice(4, strLen));
            }
            else if (methodId is ExoWireSchema.MethodLibraryGet or ExoWireSchema.MethodLibraryRefresh)
            {
                if (body.Length != 1) return false;
                binaryFlags = body[0];
            }
            else if (methodId == ExoWireSchema.MethodRpcCancel)
            {
                if (body.Length != 8) return false;
                cancelTarget = BinaryPrimitives.ReadUInt64LittleEndian(body);
            }
            else
            {
                return false;
            }

            if (pooledPayload is not null)
                ArrayPool<byte>.Shared.Return(pooledPayload);

            request = new ExoWireInboundRequest(null)
            {
                ReqId = header.ReqId,
                MethodId = methodId,
                MethodName = methodName,
                Encoding = encoding,
                PayloadOffset = 0,
                PayloadLength = 0,
                BinaryIdParam = binaryId,
                BinaryFlags = binaryFlags,
                BinaryCancelTargetId = cancelTarget,
            };
            return true;
        }

        if (encoding == ExoWireEncoding.Empty)
        {
            if (pooledPayload is not null)
                ArrayPool<byte>.Shared.Return(pooledPayload);
            request = new ExoWireInboundRequest(null)
            {
                ReqId = header.ReqId,
                MethodId = methodId,
                MethodName = methodName,
                Encoding = encoding,
                PayloadOffset = 0,
                PayloadLength = 0,
            };
            return true;
        }

        request = new ExoWireInboundRequest(pooledPayload)
        {
            ReqId = header.ReqId,
            MethodId = methodId,
            MethodName = methodName,
            Encoding = encoding,
            PayloadOffset = bodyOffset,
            PayloadLength = bodyLen,
        };
        return true;
    }

    internal static ExoWireOutboundFrame EncodeResponseFrame(
        ulong reqId,
        ushort methodId,
        bool ok,
        object? result,
        string? error,
        bool useBinaryHotCodecs)
    {
        using var writer = new ExoWirePooledWriter(ok && result is LibraryResponseDto or ShellBootstrapDto ? 32 * 1024 : 1024);
        writer.ReserveHeader();
        if (!ok)
        {
            var errText = error ?? "error";
            var errBytes = Encoding.UTF8.GetMaxByteCount(errText.Length);
            var span = writer.GetSpan(errBytes);
            var written = Encoding.UTF8.GetBytes(errText, span);
            writer.Advance(written);
            return writer.DetachFrame(ExoWireFrameKind.ResponseErr, methodId, reqId);
        }

        if (result is null)
        {
            writer.WriteByte((byte)ExoWireEncoding.Empty);
            return writer.DetachFrame(ExoWireFrameKind.ResponseOk, methodId, reqId);
        }

        if (useBinaryHotCodecs && TryWriteHotResponse(writer, methodId, result))
            return writer.DetachFrame(ExoWireFrameKind.ResponseOk, methodId, reqId);

        writer.WriteByte((byte)ExoWireEncoding.JsonUtf8);
        using (var jw = new Utf8JsonWriter(writer))
        {
            JsonSerializer.Serialize(jw, result, result.GetType(), ExoBridgeProtocol.JsonOptions);
        }
        return writer.DetachFrame(ExoWireFrameKind.ResponseOk, methodId, reqId);
    }

    internal static ExoWireOutboundFrame EncodeEventFrame(
        string name,
        object? data,
        bool useSchemaIds,
        bool useBinaryHotCodecs)
    {
        var eventId = useSchemaIds ? ExoWireSchema.GetEventId(name) : ExoWireSchema.UnknownId;
        var resolvedId = eventId != ExoWireSchema.UnknownId ? eventId : ExoWireSchema.GetEventId(name);
        using var writer = new ExoWirePooledWriter(data is LibraryResponseDto ? 32 * 1024 : 512);
        writer.ReserveHeader();
        if (eventId == ExoWireSchema.UnknownId)
            writer.WriteShortString(name);

        if (useBinaryHotCodecs && TryWriteHotEvent(writer, resolvedId, data))
            return writer.DetachFrame(ExoWireFrameKind.Event, eventId, 0);

        if (data is null)
        {
            writer.WriteByte((byte)ExoWireEncoding.Empty);
            return writer.DetachFrame(ExoWireFrameKind.Event, eventId, 0);
        }

        writer.WriteByte((byte)ExoWireEncoding.JsonUtf8);
        using (var jw = new Utf8JsonWriter(writer))
        {
            JsonSerializer.Serialize(jw, data, data.GetType(), ExoBridgeProtocol.JsonOptions);
        }
        return writer.DetachFrame(ExoWireFrameKind.Event, eventId, 0);
    }

    internal static ExoWireOutboundFrame EncodeBinaryMediaFrame(string metaJson, byte[] frame)
    {
        var meta = string.IsNullOrWhiteSpace(metaJson) ? "{}" : metaJson;
        var metaLen = Encoding.UTF8.GetByteCount(meta);
        using var writer = new ExoWirePooledWriter(HeaderLen + 4 + metaLen + frame.Length);
        writer.ReserveHeader();
        writer.WriteString(meta);
        writer.WriteRaw(frame);
        return writer.DetachFrame(ExoWireFrameKind.BinaryMedia, 0, 0);
    }

    private static bool TryWriteHotResponse(ExoWirePooledWriter writer, ushort methodId, object result)
    {
        if (methodId is ExoWireSchema.MethodLibraryGet or ExoWireSchema.MethodLibraryRefresh &&
            result is LibraryResponseDto library)
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteLibraryPayload(writer, library);
            return true;
        }
        if (methodId == ExoWireSchema.MethodGameGet && result is GameGetReplyDto gameReply)
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteGameGetReply(writer, gameReply);
            return true;
        }
        if (methodId == ExoWireSchema.MethodGameExtras && result is GameExtrasDto extras)
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteGameExtras(writer, extras);
            return true;
        }
        if (methodId == ExoWireSchema.MethodShellBootstrap &&
            result is ShellBootstrapDto bootstrap &&
            bootstrap.Library is LibraryResponseDto bootLib)
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteShellBootstrap(writer, bootstrap, bootLib);
            return true;
        }
        return false;
    }

    private static bool TryWriteHotEvent(ExoWirePooledWriter writer, ushort eventId, object? data)
    {
        if (eventId == ExoWireSchema.EventAutopilotLive && (data is null || data is AutopilotLiveDto))
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteAutopilotLive(writer, data as AutopilotLiveDto);
            return true;
        }
        if (eventId == ExoWireSchema.EventInstallProgress && data is InstallProgressDto progress)
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteInstallProgress(writer, progress);
            return true;
        }
        if (eventId == ExoWireSchema.EventLibraryUpdated && data is LibraryResponseDto library)
        {
            writer.WriteByte((byte)ExoWireEncoding.BinaryTyped);
            WriteLibraryPayload(writer, library);
            return true;
        }
        return false;
    }

    internal static void WriteLibraryPayload(ExoWirePooledWriter w, LibraryResponseDto dto)
    {
        w.WriteUInt32((uint)Math.Max(0, dto.Count));
        w.WriteOptString(string.IsNullOrEmpty(dto.SortMode) ? null : dto.SortMode);
        w.WriteStringList(dto.Recent);
        if (dto.Progress is { } prog)
        {
            w.WriteByte(1);
            WriteInstallProgress(w, prog);
        }
        else
        {
            w.WriteByte(0);
        }

        var games = dto.Games;
        var count = 0;
        for (var i = 0; i < games.Count; i++)
        {
            if (games[i] is GamePageDto) count++;
        }
        w.WriteUInt32((uint)count);
        for (var i = 0; i < games.Count; i++)
        {
            if (games[i] is GamePageDto g)
                WriteGamePage(w, g);
        }
    }

    internal static void WriteGameGetReply(ExoWirePooledWriter w, GameGetReplyDto dto)
    {
        w.WriteBool(dto.Ok);
        if (dto.Game is { } game)
        {
            w.WriteByte(1);
            WriteGamePage(w, game);
        }
        else
        {
            w.WriteByte(0);
        }
        w.WriteOptString(dto.Message);
    }

    internal static void WriteGameExtras(ExoWirePooledWriter w, GameExtrasDto dto)
    {
        w.WriteBool(dto.Ok);
        w.WriteOptUInt32(dto.AchievementsUnlocked);
        w.WriteOptUInt32(dto.AchievementsTotal);
        if (dto.DriveHeat is { } heat)
        {
            w.WriteByte(1);
            w.WriteDouble(heat.Peak);
            w.WriteOptDouble(heat.Warn);
            w.WriteDouble(heat.SlowedSeconds);
        }
        else
        {
            w.WriteByte(0);
        }
        w.WriteOptString(dto.Message);
    }

    internal static void WriteShellBootstrap(ExoWirePooledWriter w, ShellBootstrapDto dto, LibraryResponseDto library)
    {
        w.WriteBool(dto.Ok);
        w.WriteOptString(dto.BinaryMediaPipe);
        WriteLibraryPayload(w, library);
        w.WriteJsonSubSlice(dto.ArtBase, ExoBridgeProtocol.JsonOptions);
        w.WriteJsonSubSlice(dto.Settings, ExoBridgeProtocol.JsonOptions);
        w.WriteJsonSubSlice(dto.MusicPlayback, ExoBridgeProtocol.JsonOptions);
        w.WriteJsonSubSlice(dto.DiscordStatus, ExoBridgeProtocol.JsonOptions);
    }

    internal static void WriteInstallProgress(ExoWirePooledWriter w, InstallProgressDto dto)
    {
        w.WriteOptString(dto.GameId);
        w.WriteString(dto.Phase);
        w.WriteDouble(dto.Percent ?? 0);
        w.WriteUInt64((ulong)Math.Max(0, dto.BytesDownloaded ?? 0));
        w.WriteUInt64((ulong)Math.Max(0, dto.BytesToDownload ?? 0));
        w.WriteUInt64((ulong)Math.Max(0, dto.BytesPerSecond ?? 0));
        w.WriteOptString(dto.Status);
        byte flags = 0;
        if (dto.CanCancel) flags |= 1 << 0;
        if (dto.IsActive) flags |= 1 << 1;
        w.WriteByte(flags);
    }

    internal static void WriteAutopilotLive(ExoWirePooledWriter w, AutopilotLiveDto? dto)
    {
        if (dto is null)
        {
            w.WriteByte(0);
            return;
        }
        w.WriteByte(1);
        w.WriteString(dto.Game);
        w.WriteString(dto.Title);
        w.WriteDouble(dto.Seconds);
        w.WriteDouble(dto.Fps);
        w.WriteDouble(dto.Low1);
        w.WriteDouble(dto.Low01);
        w.WriteInt32(dto.Stutters);

        ushort mask = 0;
        if (dto.Cpu.HasValue) mask |= 1 << 0;
        if (dto.Clock.HasValue) mask |= 1 << 1;
        if (dto.Gpu.HasValue) mask |= 1 << 2;
        if (dto.GpuTemp.HasValue) mask |= 1 << 3;
        if (dto.VramGb.HasValue) mask |= 1 << 4;
        if (dto.VramTotalGb.HasValue) mask |= 1 << 5;
        if (dto.RamGb.HasValue) mask |= 1 << 6;
        if (dto.RamTotalGb.HasValue) mask |= 1 << 7;
        if (dto.StandbyGb.HasValue) mask |= 1 << 8;
        if (dto.FreeGb.HasValue) mask |= 1 << 9;
        if (dto.Dpc.HasValue) mask |= 1 << 10;
        w.WriteUInt16(mask);

        if (dto.Cpu.HasValue) w.WriteDouble(dto.Cpu.Value);
        if (dto.Clock.HasValue) w.WriteDouble(dto.Clock.Value);
        if (dto.Gpu.HasValue) w.WriteDouble(dto.Gpu.Value);
        if (dto.GpuTemp.HasValue) w.WriteDouble(dto.GpuTemp.Value);
        if (dto.VramGb.HasValue) w.WriteDouble(dto.VramGb.Value);
        if (dto.VramTotalGb.HasValue) w.WriteDouble(dto.VramTotalGb.Value);
        if (dto.RamGb.HasValue) w.WriteDouble(dto.RamGb.Value);
        if (dto.RamTotalGb.HasValue) w.WriteDouble(dto.RamTotalGb.Value);
        if (dto.StandbyGb.HasValue) w.WriteDouble(dto.StandbyGb.Value);
        if (dto.FreeGb.HasValue) w.WriteDouble(dto.FreeGb.Value);
        if (dto.Dpc.HasValue) w.WriteDouble(dto.Dpc.Value);
    }

    internal static void WriteGamePage(ExoWirePooledWriter w, GamePageDto g)
    {
        w.WriteString(g.Id);
        w.WriteString(g.Title);
        w.WriteString(g.Store);
        w.WriteStringList(g.Stores);

        ushort flags = 0;
        if (g.Installed) flags |= 1 << 0;
        if (g.Owned) flags |= 1 << 1;
        if (g.UpdateAvailable) flags |= 1 << 2;
        if (g.CanInstall) flags |= 1 << 3;
        if (g.FamilyShared) flags |= 1 << 4;
        if (g.IsRunning) flags |= 1 << 5;
        if (g.CanStop) flags |= 1 << 6;
        if (g.IsAddPortable) flags |= 1 << 7;
        if (g.SizeApproximate) flags |= 1 << 8;
        w.WriteUInt16(flags);

        w.WriteOptString(EntitlementToWire(g.EntitlementState));
        w.WriteOptString(g.PrimaryAction);
        w.WriteOptString(g.CanonicalTitleKey);
        w.WriteOptString(g.SelectedVariantId);
        w.WriteOptString(g.BuyUrl);
        w.WriteOptString(g.CoverUrl);
        w.WriteOptString(g.CoverSource);
        w.WriteOptString(null);
        w.WriteOptString(g.LaunchTarget);
        w.WriteUInt64((ulong)Math.Max(0, g.ArtRevision));
        w.WriteUInt64((ulong)Math.Max(0, g.PlaytimeMinutes));
        w.WriteOptUInt64(g.SizeBytes);
        w.WriteOptString(string.IsNullOrEmpty(g.Status) ? null : g.Status);
        w.WriteOptString(string.IsNullOrEmpty(g.LaunchNote) ? null : g.LaunchNote);
        w.WriteOptString(g.SharedBy);
        w.WriteOptString(g.LastPlayedUtc);
        w.WriteStringList(g.Tags);

        var includes = g.Includes;
        if (includes is null || includes.Length == 0)
        {
            w.WriteUInt32(0);
        }
        else
        {
            w.WriteUInt32((uint)includes.Length);
            for (var i = 0; i < includes.Length; i++)
            {
                w.WriteOptString(includes[i].AppId);
                w.WriteOptString(includes[i].Title);
            }
        }

        var variants = g.Variants;
        w.WriteUInt32((uint)variants.Length);
        for (var i = 0; i < variants.Length; i++)
        {
            var v = variants[i];
            w.WriteString(v.Id);
            w.WriteString(v.Store);
            byte vFlags = 0;
            if (v.Installed) vFlags |= 1 << 0;
            if (v.Owned) vFlags |= 1 << 1;
            if (v.UpdateAvailable) vFlags |= 1 << 2;
            if (v.CanInstall) vFlags |= 1 << 3;
            if (v.IsRunning) vFlags |= 1 << 4;
            if (v.CanStop) vFlags |= 1 << 5;
            w.WriteByte(vFlags);
            w.WriteOptString(EntitlementToWire(v.EntitlementState));
            w.WriteOptString(v.PrimaryAction);
            w.WriteOptString(v.LaunchTarget);
            w.WriteUInt64((ulong)Math.Max(0, v.PlaytimeMinutes));
            w.WriteOptString(v.LastPlayedUtc);
            w.WriteString(v.Status);
        }
    }

    private static string EntitlementToWire(EntitlementState state) => state switch
    {
        EntitlementState.Owned => "owned",
        EntitlementState.Unverified => "unverified",
        EntitlementState.NotOwned => "notOwned",
        _ => "unknown",
    };
}
