using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Out-of-process <see cref="IHostTransport"/> over an ACL-hardened Windows named pipe
/// using newline-delimited UTF-8 JSON frames for native UI clients such as Rune.
/// </summary>
public sealed class NamedPipeHostTransport : IHostTransport
{
    internal const int MaxMessageBytes = ExoBridgeProtocol.MaxImageRequestCharacters;
    internal const int MaxVideoFrameBytes = 8 * 1024 * 1024;
    internal const int MaxVideoMetaBytes = 64 * 1024;
    private const int StreamQueueCapacity = 64;
    private const int MediaQueueCapacity = 16;
    private const int WriteBatchBytes = 64 * 1024;
    private const int WireModePending = 0;
    private const int WireModeNdjsonV1 = 1;
    private const int WireModeExoWireV1 = 2;

    private readonly int _maxMessageBytes;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ExoWireOutboundFrame> _reliable = Channel.CreateUnbounded<ExoWireOutboundFrame>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Channel<ExoWireOutboundFrame> _stream = Channel.CreateBounded<ExoWireOutboundFrame>(new BoundedChannelOptions(StreamQueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Channel<(string MetaJson, byte[] Frame)> _mediaFrames = Channel.CreateBounded<(string, byte[])>(new BoundedChannelOptions(MediaQueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly SemaphoreSlim _outboundSignal = new(0);
    private readonly Lock _pipeGate = new();
    private NamedPipeServerStream? _activePipe;
    private NamedPipeServerStream? _activeMediaPipe;
    private Task? _acceptTask;
    private Task? _mediaAcceptTask;
    private TaskCompletionSource<int>? _wireModeReady;
    private int _outboundSignaled;
    private int _mediaConnected;
    private int _wireMode;
    private int _useSchemaIds;
    private int _useBinaryHotCodecs;
    private long _sessionGeneration;
    private bool _detached;

    public string PipeName { get; }
    public string MediaPipeName => $"{PipeName}-media";
    public string FullPipePath => $@"\\.\pipe\{PipeName}";
    public string FullMediaPipePath => $@"\\.\pipe\{MediaPipeName}";
    internal bool IsBinaryWireActive => Volatile.Read(ref _wireMode) == WireModeExoWireV1;
    internal bool UseSchemaIds => Volatile.Read(ref _useSchemaIds) == 1;
    internal bool UseBinaryHotCodecs => Volatile.Read(ref _useBinaryHotCodecs) == 1;

    public event Action<string>? MessageReceived;
    internal event Action<ExoWireInboundRequest>? WireRequestReceived;
    public event Action? SessionReset;

    public NamedPipeHostTransport(string? pipeName = null) : this(pipeName, MaxMessageBytes) { }

    internal NamedPipeHostTransport(string? pipeName, int maxMessageBytes)
    {
        PipeName = string.IsNullOrWhiteSpace(pipeName) ? $"exo-launcher-rpc-{Environment.ProcessId}" : pipeName;
        _maxMessageBytes = maxMessageBytes > 0 ? maxMessageBytes : MaxMessageBytes;
    }

    public void Start()
    {
        lock (_pipeGate)
        {
            if (Volatile.Read(ref _detached) || _acceptTask is not null) return;
            _acceptTask = Task.Run(AcceptLoopAsync);
            _mediaAcceptTask = Task.Run(MediaAcceptLoopAsync);
        }
    }

    internal static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows user SID could not be resolved.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public void PostJson(string json)
    {
        if (Volatile.Read(ref _detached) || string.IsNullOrEmpty(json)) return;
        PostJsonUtf8(Encoding.UTF8.GetBytes(json));
    }

    public void PostJsonUtf8(byte[] utf8Json)
    {
        if (Volatile.Read(ref _detached) || utf8Json.Length == 0) return;
        if (_reliable.Writer.TryWrite(new ExoWireOutboundFrame(utf8Json, utf8Json.Length, pooled: false, isBinaryWire: false)))
            SignalOutbound();
    }

    internal void PostWireFrame(ExoWireOutboundFrame frame, bool reliable = true)
    {
        if (Volatile.Read(ref _detached) || frame.Length == 0)
        {
            frame.Release();
            return;
        }
        var written = reliable ? _reliable.Writer.TryWrite(frame) : _stream.Writer.TryWrite(frame);
        if (written) SignalOutbound();
        else frame.Release();
    }

    internal void PostWireResponse(ulong reqId, ushort methodId, bool ok, object? result, string? error)
    {
        if (Volatile.Read(ref _detached)) return;
        var frame = ExoWireCodec.EncodeResponseFrame(reqId, methodId, ok, result, error, UseBinaryHotCodecs);
        PostWireFrame(frame, reliable: true);
    }

    internal void PostWireEvent(string name, object? data)
    {
        if (Volatile.Read(ref _detached)) return;
        var frame = ExoWireCodec.EncodeEventFrame(name, data, UseSchemaIds, UseBinaryHotCodecs);
        PostWireFrame(frame, reliable: true);
    }

    public void PostBinaryFrame(string metaJson, byte[] frame)
    {
        if (Volatile.Read(ref _detached) || frame.Length == 0 || frame.Length > MaxVideoFrameBytes) return;

        if (Volatile.Read(ref _mediaConnected) == 1)
        {
            _mediaFrames.Writer.TryWrite((string.IsNullOrWhiteSpace(metaJson) ? "{}" : metaJson, frame));
            return;
        }

        if (IsBinaryWireActive)
        {
            PostWireFrame(ExoWireCodec.EncodeBinaryMediaFrame(metaJson, frame), reliable: false);
            return;
        }

        var buffer = new ArrayBufferWriter<byte>(metaJson.Length + frame.Length * 4 / 3 + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", "binaryFrame");
            writer.WritePropertyName("meta");
            writer.WriteRawValue(string.IsNullOrWhiteSpace(metaJson) ? "{}" : metaJson);
            writer.WriteBase64String("data", frame);
            writer.WriteEndObject();
        }
        var bytes = buffer.WrittenSpan.ToArray();
        if (_stream.Writer.TryWrite(new ExoWireOutboundFrame(bytes, bytes.Length, pooled: false, isBinaryWire: false)))
            SignalOutbound();
    }

    private void SignalOutbound()
    {
        if (Interlocked.Exchange(ref _outboundSignaled, 1) != 0) return;
        try { _outboundSignal.Release(); } catch (ObjectDisposedException) { }
    }

    private async Task AcceptLoopAsync()
    {
        var security = CreateCurrentUserPipeSecurity();
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            Task? writerTask = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 64 * 1024,
                    outBufferSize: 64 * 1024,
                    security);

                lock (_pipeGate)
                {
                    if (Volatile.Read(ref _detached))
                    {
                        pipe.Dispose();
                        break;
                    }
                    _activePipe = pipe;
                }

                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var sessionGen = Interlocked.Increment(ref _sessionGeneration);
                Volatile.Write(ref _wireMode, WireModePending);
                Volatile.Write(ref _useSchemaIds, 0);
                Volatile.Write(ref _useBinaryHotCodecs, 0);
                var modeReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _wireModeReady, modeReady);
                writerTask = WriterLoopAsync(pipe, modeReady, clientCts.Token);
                await ReadSessionAsync(pipe, modeReady, sessionGen, clientCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Debug("NamedPipeHostTransport: client session ended: " + ex.GetType().Name);
            }
            finally
            {
                Interlocked.Increment(ref _sessionGeneration);
                Volatile.Write(ref _wireMode, WireModePending);
                Volatile.Write(ref _useSchemaIds, 0);
                Volatile.Write(ref _useBinaryHotCodecs, 0);
                Interlocked.Exchange(ref _wireModeReady, null)?.TrySetCanceled();
                try { clientCts.Cancel(); } catch { }
                if (writerTask is not null)
                {
                    try { await writerTask.ConfigureAwait(false); } catch { }
                }
                lock (_pipeGate)
                {
                    if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
                }
                if (pipe is not null)
                {
                    try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                while (_reliable.Reader.TryRead(out var dropped)) dropped.Release();
                while (_stream.Reader.TryRead(out var droppedStream)) droppedStream.Release();
                Volatile.Write(ref _outboundSignaled, 0);
                while (_outboundSignal.Wait(0)) { }
                if (!Volatile.Read(ref _detached))
                {
                    try { SessionReset?.Invoke(); } catch { }
                }
            }
        }
    }

    private async Task ReadSessionAsync(
        NamedPipeServerStream pipe,
        TaskCompletionSource<int> modeReady,
        long sessionGen,
        CancellationToken ct)
    {
        var chunk = new byte[16 * 1024];
        var firstRead = await pipe.ReadAsync(chunk, ct).ConfigureAwait(false);
        if (firstRead == 0) return;

        var initialCount = firstRead;
        if (chunk[0] == (byte)'E')
        {
            while (initialCount < 4)
            {
                var more = await pipe.ReadAsync(chunk.AsMemory(initialCount, 4 - initialCount), ct).ConfigureAwait(false);
                if (more == 0) return;
                initialCount += more;
            }

            if (chunk[0] == (byte)'E' && chunk[1] == (byte)'X' && chunk[2] == (byte)'W' && chunk[3] == (byte)'1' &&
                Interlocked.CompareExchange(ref _wireMode, WireModeExoWireV1, WireModePending) == WireModePending)
            {
                var helloTotal = ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen;
                while (initialCount < helloTotal)
                {
                    var more = await pipe.ReadAsync(chunk.AsMemory(initialCount, chunk.Length - initialCount), ct).ConfigureAwait(false);
                    if (more == 0) return;
                    initialCount += more;
                }

                if (!ExoWireFrameHeader.TryRead(chunk.AsSpan(0, ExoWireCodec.HeaderLen), out var helloHeader) ||
                    helloHeader.Kind != ExoWireFrameKind.Hello ||
                    helloHeader.PayloadLen != ExoWireCodec.HelloPayloadLen ||
                    !ExoWireHello.TryRead(chunk.AsSpan(ExoWireCodec.HeaderLen, ExoWireCodec.HelloPayloadLen), out var hello) ||
                    hello.MinVersion > ExoWireCodec.WireVersion ||
                    hello.MaxVersion < ExoWireCodec.WireVersion)
                {
                    throw new InvalidDataException("Invalid exo-wire Hello frame.");
                }

                var schemaMatch = hello.SchemaHash == ExoWireSchema.SchemaHash;
                var useIds = schemaMatch && (hello.Flags & ExoWireCodec.FlagUseSchemaIds) != 0;
                var useHot = (hello.Flags & ExoWireCodec.FlagBinaryHotCodecs) != 0;
                Volatile.Write(ref _useSchemaIds, useIds ? 1 : 0);
                Volatile.Write(ref _useBinaryHotCodecs, useHot ? 1 : 0);

                var ackFlags = (ushort)((useIds ? ExoWireCodec.FlagUseSchemaIds : 0) | (useHot ? ExoWireCodec.FlagBinaryHotCodecs : 0));
                var ack = new ExoWireHelloAck(
                    ExoWireCodec.WireVersion,
                    schemaMatch ? ExoWireCodec.HelloStatusOk : ExoWireCodec.HelloStatusSchemaMismatch,
                    ackFlags,
                    ExoWireCodec.MaxFrameBytes,
                    ExoWireSchema.SchemaHash);
                var ackBuf = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
                ExoWireCodec.WriteHelloAckFrame(ackBuf, ack);
                await pipe.WriteAsync(ackBuf, ct).ConfigureAwait(false);
                modeReady.TrySetResult(WireModeExoWireV1);

                var leftoverOffset = helloTotal;
                var leftoverLen = initialCount - helloTotal;
                await ReadWireLoopAsync(pipe, chunk, leftoverOffset, leftoverLen, sessionGen, ct).ConfigureAwait(false);
                return;
            }
        }

        Interlocked.CompareExchange(ref _wireMode, WireModeNdjsonV1, WireModePending);
        modeReady.TrySetResult(WireModeNdjsonV1);
        await ReadNdjsonLoopAsync(pipe, chunk, initialCount, sessionGen, ct).ConfigureAwait(false);
    }

    private sealed class BufferedPipeReader(NamedPipeServerStream pipe, byte[] chunk, int offset, int len)
    {
        private int _offset = offset;
        private int _len = len;

        internal async ValueTask<bool> ReadExactAsync(byte[] dest, int count, CancellationToken ct)
        {
            var filled = 0;
            if (_len > 0)
            {
                var take = Math.Min(_len, count);
                Buffer.BlockCopy(chunk, _offset, dest, 0, take);
                _offset += take;
                _len -= take;
                filled = take;
            }
            while (filled < count)
            {
                var remaining = count - filled;
                if (remaining < chunk.Length && _len == 0)
                {
                    var read = await pipe.ReadAsync(chunk, ct).ConfigureAwait(false);
                    if (read == 0) return false;
                    var take = Math.Min(read, remaining);
                    Buffer.BlockCopy(chunk, 0, dest, filled, take);
                    filled += take;
                    if (read > take)
                    {
                        _offset = take;
                        _len = read - take;
                    }
                }
                else
                {
                    var read = await pipe.ReadAsync(dest.AsMemory(filled, remaining), ct).ConfigureAwait(false);
                    if (read == 0) return false;
                    filled += read;
                }
            }
            return true;
        }
    }

    private async Task ReadWireLoopAsync(
        NamedPipeServerStream pipe,
        byte[] chunk,
        int bufferedOffset,
        int bufferedLen,
        long sessionGen,
        CancellationToken ct)
    {
        var reader = new BufferedPipeReader(pipe, chunk, bufferedOffset, bufferedLen);
        var headerBuf = new byte[ExoWireCodec.HeaderLen];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            if (!await reader.ReadExactAsync(headerBuf, ExoWireCodec.HeaderLen, ct).ConfigureAwait(false))
                break;

            if (!ExoWireFrameHeader.TryRead(headerBuf, out var header))
                throw new InvalidDataException("Malformed exo-wire frame header.");

            var payloadLen = (int)header.PayloadLen;
            byte[]? payloadBuf = null;
            if (payloadLen > 0)
            {
                payloadBuf = ArrayPool<byte>.Shared.Rent(payloadLen);
                if (!await reader.ReadExactAsync(payloadBuf, payloadLen, ct).ConfigureAwait(false))
                {
                    ArrayPool<byte>.Shared.Return(payloadBuf);
                    break;
                }
            }

            if (ExoWireCodec.TryDecodeRequest(header, payloadBuf, payloadLen, out var request) && request is not null)
            {
                EmitWireRequest(request, sessionGen);
            }
            else if (payloadBuf is not null)
            {
                ArrayPool<byte>.Shared.Return(payloadBuf);
            }
        }
    }

    private void EmitWireRequest(ExoWireInboundRequest request, long sessionGen)
    {
        var handler = WireRequestReceived;
        if (handler is null)
        {
            request.Dispose();
            return;
        }
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                var (self, callback, req, gen) = state;
                if (Volatile.Read(ref self._sessionGeneration) != gen || Volatile.Read(ref self._detached))
                {
                    req.Dispose();
                    return;
                }
                try { callback(req); }
                catch { req.Dispose(); }
            },
            (this, handler, request, sessionGen),
            preferLocal: false);
    }

    private async Task ReadNdjsonLoopAsync(
        NamedPipeServerStream pipe,
        byte[] chunk,
        int initialCount,
        long sessionGen,
        CancellationToken ct)
    {
        using var line = new MemoryStream();
        var read = initialCount;
        while (true)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != (byte)'\n') continue;

                var segLen = i - start;
                if (line.Length + segLen > _maxMessageBytes)
                    throw new InvalidDataException("Named pipe inbound message exceeded MaxMessageBytes.");

                if (segLen > 0) line.Write(chunk, start, segLen);
                EmitLine(line, sessionGen);
                line.SetLength(0);
                start = i + 1;
            }

            var tail = read - start;
            if (tail > 0)
            {
                if (line.Length + tail > _maxMessageBytes)
                    throw new InvalidDataException("Named pipe inbound message exceeded MaxMessageBytes.");
                line.Write(chunk, start, tail);
            }

            if (ct.IsCancellationRequested || !pipe.IsConnected) break;
            read = await pipe.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
        }
    }

    private void EmitLine(MemoryStream line, long sessionGen)
    {
        if (line.Length == 0) return;
        var len = (int)line.Length;
        var buf = line.GetBuffer();
        if (buf[len - 1] == (byte)'\r') len--;
        if (len <= 0) return;
        var text = Encoding.UTF8.GetString(buf, 0, len);
        if (string.IsNullOrWhiteSpace(text)) return;
        var handler = MessageReceived;
        if (handler is null) return;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                var (self, callback, payload, gen) = state;
                if (Volatile.Read(ref self._sessionGeneration) != gen || Volatile.Read(ref self._detached)) return;
                try { callback(payload); } catch { }
            },
            (this, handler, text, sessionGen),
            preferLocal: false);
    }

    private async Task WriterLoopAsync(NamedPipeServerStream pipe, TaskCompletionSource<int> modeReady, CancellationToken ct)
    {
        var batch = ArrayPool<byte>.Shared.Rent(WriteBatchBytes);
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                Interlocked.Exchange(ref _outboundSignaled, 0);
                if (Volatile.Read(ref _wireMode) == WireModePending &&
                    (_reliable.Reader.TryPeek(out _) || _stream.Reader.TryPeek(out _)))
                {
                    try
                    {
                        await modeReady.Task.WaitAsync(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        if (Interlocked.CompareExchange(ref _wireMode, WireModeNdjsonV1, WireModePending) == WireModePending)
                            modeReady.TrySetResult(WireModeNdjsonV1);
                    }
                }

                var sent = false;
                var offset = 0;

                while (_reliable.Reader.TryRead(out var message))
                {
                    try
                    {
                        if (!pipe.IsConnected || ct.IsCancellationRequested) return;
                        var trailing = message.IsBinaryWire ? 0 : 1;
                        var byteCount = message.Length + trailing;
                        if (byteCount > batch.Length)
                        {
                            if (offset > 0)
                            {
                                await pipe.WriteAsync(batch.AsMemory(0, offset), ct).ConfigureAwait(false);
                                offset = 0;
                            }
                            if (message.IsBinaryWire)
                                await pipe.WriteAsync(message.Buffer.AsMemory(0, message.Length), ct).ConfigureAwait(false);
                            else
                                await WriteLargeLineAsync(pipe, message.Buffer, message.Length, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            if (offset + byteCount > batch.Length)
                            {
                                await pipe.WriteAsync(batch.AsMemory(0, offset), ct).ConfigureAwait(false);
                                offset = 0;
                            }
                            message.Buffer.AsSpan(0, message.Length).CopyTo(batch.AsSpan(offset));
                            offset += message.Length;
                            if (!message.IsBinaryWire)
                                batch[offset++] = (byte)'\n';
                        }
                        sent = true;
                    }
                    finally
                    {
                        message.Release();
                    }
                }

                if (offset > 0)
                {
                    await pipe.WriteAsync(batch.AsMemory(0, offset), ct).ConfigureAwait(false);
                }

                if (_stream.Reader.TryRead(out var frameMessage))
                {
                    try
                    {
                        if (!pipe.IsConnected || ct.IsCancellationRequested) return;
                        if (frameMessage.IsBinaryWire)
                        {
                            await pipe.WriteAsync(frameMessage.Buffer.AsMemory(0, frameMessage.Length), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            var byteCount = frameMessage.Length + 1;
                            if (byteCount <= batch.Length)
                            {
                                frameMessage.Buffer.AsSpan(0, frameMessage.Length).CopyTo(batch.AsSpan(0));
                                batch[frameMessage.Length] = (byte)'\n';
                                await pipe.WriteAsync(batch.AsMemory(0, byteCount), ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await WriteLargeLineAsync(pipe, frameMessage.Buffer, frameMessage.Length, ct).ConfigureAwait(false);
                            }
                        }
                        sent = true;
                    }
                    finally
                    {
                        frameMessage.Release();
                    }
                }

                if (!sent)
                    await _outboundSignal.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batch);
        }
    }

    private static async Task WriteLargeLineAsync(NamedPipeServerStream pipe, byte[] message, int length, CancellationToken ct)
    {
        var total = length + 1;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            message.AsSpan(0, length).CopyTo(rented.AsSpan(0));
            rented[length] = (byte)'\n';
            await pipe.WriteAsync(rented.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task MediaAcceptLoopAsync()
    {
        var security = CreateCurrentUserPipeSecurity();
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    MediaPipeName,
                    PipeDirection.Out,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 256 * 1024,
                    security);

                lock (_pipeGate)
                {
                    if (Volatile.Read(ref _detached))
                    {
                        pipe.Dispose();
                        break;
                    }
                    _activeMediaPipe = pipe;
                }

                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                while (_mediaFrames.Reader.TryRead(out _)) { }
                Volatile.Write(ref _mediaConnected, 1);

                var headerBuf = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    while (!_cts.IsCancellationRequested && pipe.IsConnected)
                    {
                        if (!await _mediaFrames.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                            break;
                        while (_mediaFrames.Reader.TryRead(out var item))
                        {
                            if (!pipe.IsConnected || _cts.IsCancellationRequested) break;
                            var metaLen = Encoding.UTF8.GetByteCount(item.MetaJson);
                            var frameLen = item.Frame.Length;
                            if (metaLen > MaxVideoMetaBytes || frameLen == 0 || frameLen > MaxVideoFrameBytes)
                                continue;
                            var prefixLen = 8 + metaLen;
                            var totalLen = prefixLen + frameLen;
                            if (totalLen > headerBuf.Length)
                            {
                                ArrayPool<byte>.Shared.Return(headerBuf);
                                headerBuf = ArrayPool<byte>.Shared.Rent(totalLen);
                            }
                            BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(0, 4), (uint)metaLen);
                            BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(4, 4), (uint)frameLen);
                            Encoding.UTF8.GetBytes(item.MetaJson, headerBuf.AsSpan(8, metaLen));
                            item.Frame.AsSpan(0, frameLen).CopyTo(headerBuf.AsSpan(prefixLen, frameLen));
                            await pipe.WriteAsync(headerBuf.AsMemory(0, totalLen), _cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(headerBuf);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Debug("NamedPipeHostTransport: media session ended: " + ex.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref _mediaConnected, 0);
                while (_mediaFrames.Reader.TryRead(out _)) { }
                lock (_pipeGate)
                {
                    if (ReferenceEquals(_activeMediaPipe, pipe)) _activeMediaPipe = null;
                }
                if (pipe is not null)
                {
                    try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref _detached, true)) return;
        Volatile.Write(ref _mediaConnected, 0);
        _reliable.Writer.TryComplete();
        _stream.Writer.TryComplete();
        _mediaFrames.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        NamedPipeServerStream? pipe;
        NamedPipeServerStream? mediaPipe;
        Task? acceptTask;
        Task? mediaAcceptTask;
        lock (_pipeGate)
        {
            pipe = _activePipe;
            _activePipe = null;
            mediaPipe = _activeMediaPipe;
            _activeMediaPipe = null;
            acceptTask = _acceptTask;
            mediaAcceptTask = _mediaAcceptTask;
        }
        if (pipe is not null)
        {
            try { pipe.Dispose(); } catch { }
        }
        if (mediaPipe is not null)
        {
            try { mediaPipe.Dispose(); } catch { }
        }
        if (acceptTask is not null)
        {
            try { acceptTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        if (mediaAcceptTask is not null)
        {
            try { mediaAcceptTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        _outboundSignal.Dispose();
        _cts.Dispose();
    }

    public void Dispose() => Detach();
}
