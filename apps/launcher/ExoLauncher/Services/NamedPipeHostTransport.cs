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

    private readonly int _maxMessageBytes;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _reliable = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Channel<byte[]> _stream = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(StreamQueueCapacity)
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
    private int _outboundSignaled;
    private int _mediaConnected;
    private long _sessionGeneration;
    private bool _detached;

    public string PipeName { get; }
    public string MediaPipeName => $"{PipeName}-media";
    public string FullPipePath => $@"\\.\pipe\{PipeName}";
    public string FullMediaPipePath => $@"\\.\pipe\{MediaPipeName}";

    public event Action<string>? MessageReceived;
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
        if (_reliable.Writer.TryWrite(utf8Json)) SignalOutbound();
    }

    public void PostBinaryFrame(string metaJson, byte[] frame)
    {
        if (Volatile.Read(ref _detached) || frame.Length == 0 || frame.Length > MaxVideoFrameBytes) return;

        if (Volatile.Read(ref _mediaConnected) == 1)
        {
            _mediaFrames.Writer.TryWrite((string.IsNullOrWhiteSpace(metaJson) ? "{}" : metaJson, frame));
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
        if (_stream.Writer.TryWrite(buffer.WrittenSpan.ToArray())) SignalOutbound();
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
                writerTask = WriterLoopAsync(pipe, clientCts.Token);
                await ReadLoopAsync(pipe, sessionGen, clientCts.Token).ConfigureAwait(false);
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
                while (_reliable.Reader.TryRead(out _)) { }
                while (_stream.Reader.TryRead(out _)) { }
                Volatile.Write(ref _outboundSignaled, 0);
                while (_outboundSignal.Wait(0)) { }
                if (!Volatile.Read(ref _detached))
                {
                    try { SessionReset?.Invoke(); } catch { }
                }
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, long sessionGen, CancellationToken ct)
    {
        var chunk = new byte[16 * 1024];
        using var line = new MemoryStream();
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var read = await pipe.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;

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

    private async Task WriterLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var batch = ArrayPool<byte>.Shared.Rent(WriteBatchBytes);
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                Interlocked.Exchange(ref _outboundSignaled, 0);
                var sent = false;
                var offset = 0;

                while (_reliable.Reader.TryRead(out var message))
                {
                    if (!pipe.IsConnected || ct.IsCancellationRequested) return;
                    var byteCount = message.Length + 1;
                    if (byteCount > batch.Length)
                    {
                        if (offset > 0)
                        {
                            await pipe.WriteAsync(batch.AsMemory(0, offset), ct).ConfigureAwait(false);
                            offset = 0;
                        }
                        await WriteLargeLineAsync(pipe, message, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        if (offset + byteCount > batch.Length)
                        {
                            await pipe.WriteAsync(batch.AsMemory(0, offset), ct).ConfigureAwait(false);
                            offset = 0;
                        }
                        message.CopyTo(batch.AsSpan(offset));
                        offset += message.Length;
                        batch[offset++] = (byte)'\n';
                    }
                    sent = true;
                }

                if (offset > 0)
                {
                    await pipe.WriteAsync(batch.AsMemory(0, offset), ct).ConfigureAwait(false);
                }

                if (_stream.Reader.TryRead(out var frameMessage))
                {
                    if (!pipe.IsConnected || ct.IsCancellationRequested) return;
                    var byteCount = frameMessage.Length + 1;
                    if (byteCount <= batch.Length)
                    {
                        frameMessage.CopyTo(batch.AsSpan(0));
                        batch[frameMessage.Length] = (byte)'\n';
                        await pipe.WriteAsync(batch.AsMemory(0, byteCount), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteLargeLineAsync(pipe, frameMessage, ct).ConfigureAwait(false);
                    }
                    sent = true;
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

    private static async Task WriteLargeLineAsync(NamedPipeServerStream pipe, byte[] message, CancellationToken ct)
    {
        var total = message.Length + 1;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            message.CopyTo(rented.AsSpan(0));
            rented[message.Length] = (byte)'\n';
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
