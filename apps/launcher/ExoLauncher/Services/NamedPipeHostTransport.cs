using System.Buffers;
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
    internal const int MaxMessageBytes = 4 * 1024 * 1024;
    private const int OutboundQueueCapacity = 4096;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboundQueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Lock _pipeGate = new();
    private NamedPipeServerStream? _activePipe;
    private readonly Task _acceptTask;
    private bool _detached;

    public string PipeName { get; }
    public string FullPipePath => $@"\\.\pipe\{PipeName}";

    public event Action<string>? MessageReceived;
    public event Action? SessionReset;

    public NamedPipeHostTransport(string? pipeName = null)
    {
        PipeName = string.IsNullOrWhiteSpace(pipeName) ? $"exo-launcher-rpc-{Environment.ProcessId}" : pipeName;
        _acceptTask = Task.Run(AcceptLoopAsync);
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
        _outbound.Writer.TryWrite(json);
    }

    public void PostBinaryFrame(string metaJson, byte[] frame)
    {
        if (Volatile.Read(ref _detached) || frame.Length == 0) return;

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
        _outbound.Writer.TryWrite(Encoding.UTF8.GetString(buffer.WrittenSpan));
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
                writerTask = WriterLoopAsync(pipe, clientCts.Token);
                await ReadLoopAsync(pipe, clientCts.Token).ConfigureAwait(false);
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
                while (_outbound.Reader.TryRead(out _)) { }
                if (!Volatile.Read(ref _detached))
                {
                    try { SessionReset?.Invoke(); } catch { }
                }
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
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
                if (line.Length + segLen > MaxMessageBytes)
                    throw new InvalidDataException("Named pipe inbound message exceeded MaxMessageBytes.");

                if (segLen > 0) line.Write(chunk, start, segLen);
                EmitLine(line);
                line.SetLength(0);
                start = i + 1;
            }

            var tail = read - start;
            if (tail > 0)
            {
                if (line.Length + tail > MaxMessageBytes)
                    throw new InvalidDataException("Named pipe inbound message exceeded MaxMessageBytes.");
                line.Write(chunk, start, tail);
            }
        }
    }

    private void EmitLine(MemoryStream line)
    {
        if (line.Length == 0) return;
        var len = (int)line.Length;
        var buf = line.GetBuffer();
        if (buf[len - 1] == (byte)'\r') len--;
        if (len <= 0) return;
        var text = Encoding.UTF8.GetString(buf, 0, len);
        if (!string.IsNullOrWhiteSpace(text))
            MessageReceived?.Invoke(text);
    }

    private async Task WriterLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            if (!await _outbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
            while (_outbound.Reader.TryRead(out var message))
            {
                if (!pipe.IsConnected || ct.IsCancellationRequested) return;
                var byteCount = Encoding.UTF8.GetByteCount(message) + 1;
                var rented = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    var written = Encoding.UTF8.GetBytes(message, rented);
                    rented[written] = (byte)'\n';
                    await pipe.WriteAsync(rented.AsMemory(0, written + 1), ct).ConfigureAwait(false);
                    await pipe.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref _detached, true)) return;
        _outbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        NamedPipeServerStream? pipe;
        lock (_pipeGate)
        {
            pipe = _activePipe;
            _activePipe = null;
        }
        if (pipe is not null)
        {
            try { pipe.Dispose(); } catch { }
        }
        try { _acceptTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    public void Dispose() => Detach();
}
