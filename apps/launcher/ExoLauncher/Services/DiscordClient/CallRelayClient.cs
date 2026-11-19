using System.Net.WebSockets;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// One topic on Exo's relay (relay/README.md), for a call or a friend link: a WebSocket that sends this Exo's sealed messages and hands what
/// the others on the topic say (<see cref="CallRelayFormat.Frame"/>) to <see cref="Received"/>. It never sees a key.
///
/// How it can fail, and what it does then:
/// - The relay can't be reached, refuses (a full topic, 429) or drops the connection (a deploy, a network change):
///   it tries again after 1, 2, 4 … 60 s, back to 1 s after a connection that lasted a minute; each try is
///   <see cref="Connected"/> again, after which the relay replays what is current, so the owner forgets what it heard
///   before and sends its own message afresh (an old one would carry an old position).
/// - The relay closes it for breaking a limit (size 1009, rate 1008, text 1003): Exo's own bug; logged, then the same.
/// - A silent dead connection: WebSocket pings every 25 s, and no answer in 15 s ends it.
/// - A frame over 4 KB or not the relay's: skipped.
/// Sends wait for the one before (one at a time on the socket); while not connected they are dropped.
/// </summary>
internal sealed class CallRelayClient : IAsyncDisposable
{
    // The deployed relay (relay/README.md, Deploy). Null would turn the call relay off.
    private static readonly Uri? Deployed = new("wss://exo-relay.exo-erix.workers.dev");

    /// <summary>
    /// Where the relay is: the deployed one, or EXO_CALL_RELAY when it names a relay on this PC (ws://127.0.0.1:port,
    /// ws://localhost:port), for a local relay (relay/local/server.mjs). Null: no relay.
    /// </summary>
    internal static Uri? Endpoint
    {
        get
        {
            if (Environment.GetEnvironmentVariable("EXO_CALL_RELAY") is { Length: > 0 } local
                && Uri.TryCreate(local, UriKind.Absolute, out var uri) && uri.Scheme == "ws" && uri.IsLoopback)
                return uri;
            return Deployed;
        }
    }

    private const int MaxFrame = 4096;
    private readonly Uri _uri;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _ws;
    private Task? _run;

    /// <summary>A frame from the relay, on the receive thread.</summary>
    internal event Action<CallRelayFormat.Frame>? Received;

    /// <summary>Connected (again): what was heard before may be stale, and this Exo's message is wanted afresh.</summary>
    internal event Action? Connected;

    /// <param name="name">What it is called in the log.</param>
    internal CallRelayClient(Uri endpoint, string topic, string name = "Discord call relay")
    {
        _uri = new Uri($"{endpoint.GetLeftPart(UriPartial.Authority)}/v1/{topic}");
        _name = name;
    }

    internal bool Open => _ws?.State == WebSocketState.Open;

    internal void Start() => _run ??= Task.Run(RunAsync);

    internal Task SendAsync(byte[] message) => _ws is { State: WebSocketState.Open } ws ? SendAsync(ws, message) : Task.CompletedTask;

    private async Task SendAsync(ClientWebSocket ws, byte[] message)
    {
        try
        {
            await _sendGate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try { await ws.SendAsync(message, WebSocketMessageType.Binary, true, _stop.Token).ConfigureAwait(false); }
            finally { _sendGate.Release(); }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { /* the receive loop sees the drop */ }
    }

    private async Task RunAsync()
    {
        var wait = 1;
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            var began = Environment.TickCount64;
            try
            {
                var closed = await ConnectAsync(_stop.Token).ConfigureAwait(false);
                failures = 0;
                if (closed is WebSocketCloseStatus.MessageTooBig or WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.InvalidMessageType)
                    AppLog.Info($"{_name}: the relay closed the connection ({(int)closed}).");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
            {
                // One line per run of failures: the relay down for an hour is one line, not sixty.
                if (failures++ == 0) AppLog.Info($"{_name}: can't connect ({e.GetType().Name}); trying again.");
            }
            if (Environment.TickCount64 - began > 60_000) wait = 1;
            try { await Task.Delay(TimeSpan.FromSeconds(wait), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            wait = Math.Min(wait * 2, 60);
        }
    }

    // One connection, until it closes: the relay's close status, if it sent one.
    private async Task<WebSocketCloseStatus?> ConnectAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(_uri, connect.Token).ConfigureAwait(false);
        }
        _ws = ws;
        try
        {
            Connected?.Invoke();
            // One byte more than a frame may be, so a bigger one shows; the rest of it goes to scratch and is dropped.
            var buffer = new byte[MaxFrame + 1];
            var scratch = new byte[MaxFrame];
            while (ws.State == WebSocketState.Open)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(count < buffer.Length ? buffer.AsMemory(count) : scratch, ct).ConfigureAwait(false);
                    count += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
                if (result.MessageType == WebSocketMessageType.Close) return ws.CloseStatus;
                if (count > MaxFrame || result.MessageType != WebSocketMessageType.Binary) continue;
                if (CallRelayFormat.ParseFrame(buffer.AsSpan(0, count)) is { } frame) Received?.Invoke(frame);
            }
            return ws.CloseStatus;
        }
        finally { _ws = null; }
    }

    /// <summary>Sends <paramref name="last"/> (when connected), then closes: the relay tells the others this one left.</summary>
    internal async Task CloseAsync(byte[]? last)
    {
        if (_ws is { State: WebSocketState.Open } ws)
        {
            if (last is not null) await SendAsync(ws, last).ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _ws?.Abort();
        if (_run is { } run) try { await run.ConfigureAwait(false); } catch { /* ended */ }
        _stop.Dispose();
    }
}
