using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Exo.Identity;

/// <summary>
/// A WebSocket to the mailbox Worker that carries whole binary frames. Each app opens it itself, through its own egress
/// rules (Launcher's NetworkDestinations), so this library has no network of its
/// own; a test copy's app opens only a mailbox on loopback (<see cref="Mailbox.EndpointAllowed"/>).
/// </summary>
public interface IMailboxSocket : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    /// <summary>The next whole binary frame, or null once the connection is closed (by either side).</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
}

/// <summary>Opens a socket to one of the mailbox's paths (<see cref="MailboxPaths"/>). A refused path (no such
/// nameplate, too many joins) throws; the app says why.</summary>
public delegate Task<IMailboxSocket> MailboxConnect(string path, CancellationToken ct);

/// <summary>The mailbox's three WebSocket paths (cloud/mailbox/README.md).</summary>
public static class MailboxPaths
{
    public const string NewLink = "/v1/link";

    public static string Box(string boxId) =>
        boxId.Length == 32 && boxId.All(char.IsAsciiHexDigitLower) ? "/v1/box/" + boxId : throw new ArgumentException("A box id is 32 lowercase hex.", nameof(boxId));

    public static string Join(string nameplate) =>
        LinkHandshake.IsNameplate(nameplate) ? "/v1/link/" + nameplate : throw new ArgumentException("A nameplate is 8 digits.", nameof(nameplate));
}

/// <summary>The mailbox ended the conversation: closed, the other PC gone, or a nameplate crowded.</summary>
public sealed class MailboxClosedException(string message) : Exception(message);

/// <summary>A message kept in a box: its seq, when it was stored (whole seconds) and its bytes (an envelope).</summary>
public sealed record MailboxMessage(uint Seq, DateTimeOffset At, byte[] Blob);

/// <summary>
/// The Worker's token buckets per connection (cloud/mailbox/src/limits.js): any frame, and puts (each a row written, the
/// free plan's scarcest thing). The Worker closes a connection that goes past either, so every client keeps the same
/// buckets itself and waits for a token instead: a snapshot bigger than the burst goes on over time on the same
/// connection. Its refill runs a tenth and 50 ms slower than the Worker's, so a frame on its way never lands before the
/// Worker's own token does.
/// </summary>
public sealed record MailboxPace(int Frames, TimeSpan FrameEvery, int Puts, TimeSpan PutEvery)
{
    /// <summary>The deployed Worker's: 60 frames then 1 every 2 s, 20 puts then 1 every 30 s.</summary>
    public static readonly MailboxPace Worker = new(60, TimeSpan.FromSeconds(2), 20, TimeSpan.FromSeconds(30));

    /// <summary>How long to wait before connecting again after a connection ended: the put bucket's refill (30 s),
    /// doubling with each failure in a row, at most 5 minutes.</summary>
    public static TimeSpan Retry(int failures) =>
        TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Clamp(failures, 0, 8))));
}

/// <summary>The Worker's token bucket (worker.js' take), on this PC's clock and a little slower.</summary>
internal sealed class TokenBucket(int size, TimeSpan every)
{
    private readonly long _every = (long)(every.TotalMilliseconds * 1.1) + 50;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _tokens = size;
    private long _at = Environment.TickCount64;

    public async Task TakeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = Environment.TickCount64;
                var steps = (now - _at) / _every;
                var tokens = Math.Min(size, _tokens + steps);
                _at = tokens == size ? now : _at + steps * _every;
                _tokens = tokens;
                if (tokens >= 1)
                {
                    _tokens--;
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(_at + _every - now + 1), ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// One logged-in connection to a box (plan §2.6): <see cref="PutAsync"/> stores an envelope, <see cref="SinceAsync"/>
/// reads every row after a seq, <see cref="NextAsync"/> waits for the next one another connection stores, and
/// <see cref="TrimAsync"/> deletes rows after a snapshot. One reader at a time: SinceAsync and NextAsync share the
/// incoming frames. Puts may overlap them; the Worker answers puts in order.
/// </summary>
public sealed class MailboxBox : IAsyncDisposable
{
    private readonly IMailboxSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _read = new(1, 1);
    private readonly ConcurrentQueue<TaskCompletionSource<uint>> _puts = new();
    // 0x12 messages and 0x14 caught-up frames, in the order they came.
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly TokenBucket _frames, _putTokens;
    private Task _loop = Task.CompletedTask;

    private MailboxBox(IMailboxSocket socket, string boxId, uint first, uint last, TokenBucket frames, TokenBucket puts)
    {
        _socket = socket;
        BoxId = boxId;
        First = first;
        Last = last;
        _frames = frames;
        _putTokens = puts;
    }

    /// <summary>32 lowercase hex, as the URL names it.</summary>
    public string BoxId { get; }

    /// <summary>The lowest seq the box kept when this connection logged in (Last + 1 for an empty box). A reader whose cursor
    /// is below First - 1 missed rows and resyncs from a snapshot.</summary>
    public uint First { get; }

    /// <summary>The highest seq when this connection logged in. A cursor above it means the box emptied and started again.</summary>
    public uint Last { get; }

    /// <summary>Connects to the box named by <paramref name="boxKey"/>'s public key and logs in with it. Every frame then
    /// keeps to <paramref name="pace"/> (the deployed Worker's unless a test runs its own).</summary>
    public static async Task<MailboxBox> OpenAsync(MailboxConnect connect, IDeviceSigner boxKey, CancellationToken ct, MailboxPace? pace = null)
    {
        pace ??= MailboxPace.Worker;
        var boxId = Mailbox.BoxId(boxKey.PublicKey);
        var socket = await connect(MailboxPaths.Box(boxId), ct).ConfigureAwait(false);
        try
        {
            var challenge = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            if (challenge is not { Length: 1 + Mailbox.ChallengeBytes } || challenge[0] != Mailbox.ChallengeFrame)
                throw new MailboxClosedException("The mailbox didn't ask this box's key to log in.");
            // The Worker fills both buckets as it sends the challenge: counting from here never runs ahead of it.
            var frames = new TokenBucket(pace.Frames, pace.FrameEvery);
            var puts = new TokenBucket(pace.Puts, pace.PutEvery);
            await frames.TakeAsync(ct).ConfigureAwait(false);
            await socket.SendAsync(Mailbox.Auth(boxKey, challenge.AsSpan(1)), ct).ConfigureAwait(false);
            var ready = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            if (ready is not { Length: 9 } || ready[0] != Mailbox.ReadyFrame)
                throw new MailboxClosedException("The mailbox refused this box's key.");
            var box = new MailboxBox(socket, boxId, BinaryPrimitives.ReadUInt32BigEndian(ready.AsSpan(1)), BinaryPrimitives.ReadUInt32BigEndian(ready.AsSpan(5)), frames, puts);
            box._loop = Task.Run(box.ReadLoopAsync, CancellationToken.None);
            return box;
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stores <paramref name="blob"/> (1 byte to 64 KiB) and answers the seq the box gave it. Past the Worker's
    /// burst it waits for its bucket (<see cref="MailboxPace"/>), so a long run of puts goes on over time.</summary>
    public async Task<uint> PutAsync(ReadOnlyMemory<byte> blob, CancellationToken ct)
    {
        if (blob.Length is 0 or > Envelope.MaxBytes) throw new ArgumentException("A message is 1 byte to 64 KiB.", nameof(blob));
        var stored = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new byte[blob.Length + 1];
        frame[0] = Mailbox.PutFrame;
        blob.Span.CopyTo(frame.AsSpan(1));
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            await _putTokens.TakeAsync(ct).ConfigureAwait(false);
            await _frames.TakeAsync(ct).ConfigureAwait(false);
            // Queued with the send, under one lock, so the queue's order is the order the Worker answers in.
            _puts.Enqueue(stored);
            await _socket.SendAsync(frame, ct).ConfigureAwait(false);
        }
        finally { _send.Release(); }
        if (_loop.IsCompleted) Fail(new MailboxClosedException("The mailbox closed the connection."));
        return await stored.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every row after <paramref name="after"/>, page by page, in seq order, each once (a live message that arrived
    /// while the replay was asked for is folded in). Messages that come after the last page stay for <see cref="NextAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<MailboxMessage>> SinceAsync(uint after, CancellationToken ct)
    {
        await _read.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var got = new SortedDictionary<uint, MailboxMessage>();
            var from = after;
            while (true)
            {
                await SendFrameAsync([Mailbox.SinceFrame, .. U32(from)], ct).ConfigureAwait(false);
                while (true)
                {
                    var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                    if (frame[0] == Mailbox.MessageFrame)
                    {
                        if (Message(frame) is { } message && message.Seq > after) got.TryAdd(message.Seq, message);
                        continue;
                    }
                    var last = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1));
                    if (frame[5] == 0) return [.. got.Values];
                    from = Math.Max(from, last);
                    break;
                }
            }
        }
        finally { _read.Release(); }
    }

    /// <summary>The next message another connection stores in this box.</summary>
    public async Task<MailboxMessage> NextAsync(CancellationToken ct)
    {
        await _read.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (frame[0] == Mailbox.MessageFrame && Message(frame) is { } message) return message;
            }
        }
        finally { _read.Release(); }
    }

    /// <summary>Deletes rows up to <paramref name="upTo"/> (never the newest), after a full snapshot. In a box several apps
    /// share, only once every app's items are in snapshots after it.</summary>
    public Task TrimAsync(uint upTo, CancellationToken ct) => SendFrameAsync([Mailbox.TrimFrame, .. U32(upTo)], ct);

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        await _socket.DisposeAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { /* it ends with the socket */ }
        Fail(new MailboxClosedException("The connection to the mailbox was closed."));
        _send.Dispose();
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _frames.TakeAsync(ct).ConfigureAwait(false);
            await _socket.SendAsync(frame, ct).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        try { return await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new MailboxClosedException("The mailbox closed the connection."); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _socket.ReceiveAsync(_stop.Token).ConfigureAwait(false) is { } frame)
            {
                switch (frame)
                {
                    case [Mailbox.StoredFrame, _, _, _, _]:
                        if (_puts.TryDequeue(out var stored)) stored.TrySetResult(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1)));
                        break;
                    case [Mailbox.MessageFrame, ..] when frame.Length >= 9:
                    case [Mailbox.CaughtUpFrame, _, _, _, _, _]:
                        _incoming.Writer.TryWrite(frame);
                        break;
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { /* closing */ }
        catch (Exception) { /* the connection broke: everyone waiting hears it below */ }
        finally
        {
            _incoming.Writer.TryComplete();
            Fail(new MailboxClosedException("The mailbox closed the connection."));
        }
    }

    private void Fail(Exception error)
    {
        while (_puts.TryDequeue(out var put)) put.TrySetException(error);
    }

    private static MailboxMessage? Message(byte[] frame) => frame.Length < 9 ? null : new(
        BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1)),
        DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(5))),
        frame[9..]);

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}

/// <summary>
/// The link rendezvous (plan §2.4, §2.6): the new PC asks for a nameplate (<see cref="CreateAsync"/>), the linked PC
/// joins it (<see cref="JoinAsync"/>), and from <c>0x31</c> on each frame one sends reaches the other as it is. The
/// handshake on top is <see cref="LinkHandshake"/>; the whole exchange, <see cref="LinkFlow"/>.
/// </summary>
public sealed class MailboxLink : IAsyncDisposable
{
    private readonly IMailboxSocket _socket;
    private bool _joined;

    private MailboxLink(IMailboxSocket socket, string nameplate, bool joined)
    {
        _socket = socket;
        Nameplate = nameplate;
        _joined = joined;
    }

    /// <summary>8 ASCII digits, shown as two groups of four.</summary>
    public string Nameplate { get; }

    /// <summary>The new PC's side: a nameplate of its own, valid 10 minutes.</summary>
    public static async Task<MailboxLink> CreateAsync(MailboxConnect connect, CancellationToken ct)
    {
        var socket = await connect(MailboxPaths.NewLink, ct).ConfigureAwait(false);
        try
        {
            var frame = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            if (frame is not { Length: 1 + LinkHandshake.NameplateDigits } || frame[0] != Mailbox.NameplateFrame ||
                Encoding.ASCII.GetString(frame, 1, LinkHandshake.NameplateDigits) is not { } nameplate || !LinkHandshake.IsNameplate(nameplate))
                throw new MailboxClosedException("The mailbox didn't hand out a nameplate.");
            return new(socket, nameplate, joined: false);
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The linked PC's side: joins the nameplate the new PC shows, once both are there.</summary>
    public static async Task<MailboxLink> JoinAsync(MailboxConnect connect, string nameplate, CancellationToken ct)
    {
        var socket = await connect(MailboxPaths.Join(nameplate), ct).ConfigureAwait(false);
        var link = new MailboxLink(socket, nameplate, joined: false);
        try
        {
            await link.WaitJoinedAsync(ct).ConfigureAwait(false);
            return link;
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Waits until the other PC is there (0x31).</summary>
    public async Task WaitJoinedAsync(CancellationToken ct)
    {
        if (_joined) return;
        var frame = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
        Ended(frame);
        if (frame![0] != Mailbox.JoinedFrame) throw new MailboxClosedException("The mailbox sent something else before the other PC joined.");
        _joined = true;
    }

    public Task SendAsync(byte[] frame, CancellationToken ct)
    {
        if (frame.Length == 0 || frame[0] >= Mailbox.NameplateFrame) throw new ArgumentException("The rendezvous' own frames are 0x30 and up.", nameof(frame));
        return _socket.SendAsync(frame, ct);
    }

    /// <summary>The other PC's next frame. Throws <see cref="MailboxClosedException"/> when it left (0x32), someone else
    /// came for the nameplate (0x33: start again) or the connection closed.</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var frame = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
        Ended(frame);
        return frame!;
    }

    public ValueTask DisposeAsync() => _socket.DisposeAsync();

    private static void Ended(byte[]? frame)
    {
        switch (frame)
        {
            case null or []: throw new MailboxClosedException("The link was closed.");
            case [Mailbox.GoneFrame, ..]: throw new MailboxClosedException("The other PC left.");
            case [Mailbox.CrowdedFrame, ..]: throw new MailboxClosedException("Someone else used this code. Start again.");
        }
    }
}
