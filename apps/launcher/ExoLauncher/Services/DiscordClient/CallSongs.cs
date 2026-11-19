using ExoLauncher.Helpers;
using Song = ExoLauncher.Services.DiscordClient.CallRelayFormat.Song;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// One call's songs: this Exo's song sent to the call relay and the other members' songs heard from it
/// (<see cref="CallRelayClient"/>, <see cref="CallRelayFormat"/>, relay/README.md), each epoch on its own topic under
/// its own key, from the call's DAVE exporter (<see cref="UseSecret"/>). <see cref="DiscordSession"/> holds one per call.
///
/// How it can fail, and what it does then:
/// - The call is not end-to-end encrypted (no secret) or no relay is set: nothing is sent or heard.
/// - Someone joins or leaves (a new epoch): a new topic; songs heard on the old one stay <see cref="SweepMs"/> for the
///   members' own Exo to say them again on the new one, then go.
/// - The relay drops or replays: on each (re)connect what was heard is marked old the same way, the relay's replay
///   brings back what is current, and this Exo sends its song again with its position now.
/// - A message from another epoch, changed on the way or malformed: it doesn't open and is dropped. One whose sender
///   isn't in the call, is this user, or reuses another sender's connection or an old sequence number: dropped.
/// - A member leaves the call: <see cref="Forget"/> drops their song at once.
/// - A seek or song change in a burst: sent at most once a second, 0.4 s after the last change.
/// </summary>
internal sealed class CallSongs
{
    /// <param name="At">The tick at which <see cref="Song.PositionMs"/> was where they were.</param>
    internal sealed record Heard(Song Song, long At);

    private sealed record Entry(Heard Heard, int Epoch, uint Conn);

    private readonly string _me;
    private readonly Uri? _endpoint;
    private readonly Func<string, bool> _inCall;
    private readonly Lock _gate = new();
    private CallRelayClient? _relay;
    private CallRelayFormat.Keys? _keys;
    // Counts topics and reconnects: what was heard under an older one is waiting to be said again.
    private int _epoch;
    private long _seq;
    private readonly Dictionary<uint, (string User, long Seq)> _conns = [];
    private readonly Dictionary<string, Entry> _heard = [];
    private readonly Timer _sweep;
    private readonly Timer _send;
    private (Song Song, long At)? _mine, _sentFor;
    private long _sentAt;
    private bool _stopped, _began;

    /// <param name="me">This user's Discord id: the sender of this Exo's messages, never taken from the relay.</param>
    /// <param name="endpoint">The relay; null: none set, nothing is sent.</param>
    /// <param name="inCall">Whether a user id is in the call now (the voice gateway's word).</param>
    internal CallSongs(string me, Uri? endpoint, Func<string, bool> inCall)
    {
        _me = me;
        _endpoint = endpoint;
        _inCall = inCall;
        _sweep = new Timer(_ => Sweep());
        _send = new Timer(_ => SendMine(again: false));
    }

    /// <summary>A member's song changed (null: it went), on whichever thread heard it.</summary>
    internal event Action<string, Heard?>? Changed;

    /// <summary>How long a song heard on the topic before stays for its sender to say it again.</summary>
    internal int SweepMs { get; init; } = 10_000;

    internal bool Connected => _relay?.Open == true;

    /// <summary>The call's secret for its current epoch (a new topic and key), or null: not end-to-end encrypted.</summary>
    internal void UseSecret(byte[]? secret)
    {
        CallRelayClient? old;
        byte[]? goodbye = null;
        bool first;
        lock (_gate)
        {
            if (_stopped) return;
            var keys = secret is null ? null : CallRelayFormat.Derive(secret);
            if (keys is not null && keys.Topic == _keys?.Topic) return;
            first = secret is not null && !_began;
            _began |= secret is not null;
            old = _relay;
            if (keys is null && _keys is { } was) goodbye = CallRelayFormat.Seal(was, new(_me, ++_seq, null));
            _relay = null;
            _keys = keys;
            NewEpoch();
            if (keys is not null && _endpoint is { } endpoint)
            {
                var client = _relay = new CallRelayClient(endpoint, keys.Topic);
                client.Connected += () => OnConnected(client);
                client.Received += frame => OnFrame(client, frame);
                client.Start();
            }
        }
        if (first) AppLog.Info(_endpoint is null ? "Discord call relay: off (no relay set)." : "Discord call relay: on for this call.");
        if (old is not null && secret is null) AppLog.Info("Discord call relay: off; the call isn't end-to-end encrypted.");
        if (old is not null) _ = old.CloseAsync(goodbye);
    }

    /// <summary>
    /// What this Exo plays (null: nothing), with the tick its position was read: sent when it is another song, it plays
    /// or pauses, or its position is more than 3 s from where it should be.
    /// </summary>
    internal void SetMine(Song? song, long at)
    {
        lock (_gate)
        {
            var before = _mine;
            _mine = song is null ? null : (song, at);
            if (_stopped || !Differs(before, song, at)) return;
            _send.Change(Math.Max(400, 1000 - (Environment.TickCount64 - _sentAt)), Timeout.Infinite);
        }
    }

    private static bool Differs((Song Song, long At)? before, Song? song, long at)
    {
        if (before is not { } was || song is null) return before is not null || song is not null;
        var old = was.Song;
        if (old.Title != song.Title || old.Service != song.Service || old.Id != song.Id || old.Playing != song.Playing
            || !old.Artists.SequenceEqual(song.Artists)) return true;
        var expected = old.PositionMs + (old.Playing ? at - was.At : 0);
        return Math.Abs(expected - song.PositionMs) > 3000;
    }

    /// <summary>A member left the call: their song goes now, not when the relay or the next epoch says so.</summary>
    internal void Forget(string user)
    {
        lock (_gate)
        {
            if (!_heard.Remove(user)) return;
            foreach (var conn in _conns.Where(c => c.Value.User == user).Select(c => c.Key).ToList()) _conns.Remove(conn);
        }
        Changed?.Invoke(user, null);
    }

    internal IReadOnlyList<(string User, Heard Heard)> Current()
    {
        lock (_gate) return [.. _heard.Select(h => (h.Key, h.Value.Heard))];
    }

    /// <summary>The call ended: this Exo's song is cleared on the relay, everyone's here.</summary>
    internal async Task StopAsync()
    {
        CallRelayClient? relay;
        byte[]? goodbye = null;
        List<string> cleared;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            relay = _relay;
            if (_keys is { } keys) goodbye = CallRelayFormat.Seal(keys, new(_me, ++_seq, null));
            _relay = null;
            _keys = null;
            _conns.Clear();
            cleared = [.. _heard.Keys];
            _heard.Clear();
        }
        _sweep.Dispose();
        _send.Dispose();
        foreach (var user in cleared) Changed?.Invoke(user, null);
        if (relay is not null) await relay.CloseAsync(goodbye).ConfigureAwait(false);
    }

    // Under _gate: what was heard is now old; after SweepMs whatever wasn't said again goes.
    private void NewEpoch()
    {
        _epoch++;
        _conns.Clear();
        _sweep.Change(SweepMs, Timeout.Infinite);
    }

    private void Sweep()
    {
        List<string> gone;
        lock (_gate)
        {
            if (_stopped) return;
            gone = [.. _heard.Where(h => h.Value.Epoch != _epoch).Select(h => h.Key)];
            foreach (var user in gone) _heard.Remove(user);
        }
        foreach (var user in gone) Changed?.Invoke(user, null);
    }

    private void OnConnected(CallRelayClient client)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(client, _relay)) return;
            NewEpoch();
        }
        SendMine(again: true);
    }

    private void OnFrame(CallRelayClient client, CallRelayFormat.Frame frame)
    {
        string? user;
        Heard? now = null;
        lock (_gate)
        {
            if (!ReferenceEquals(client, _relay) || _keys is not { } keys) return;
            if (frame.Gone)
            {
                if (!_conns.Remove(frame.Conn, out var left) || !_heard.TryGetValue(left.User, out var theirs) || theirs.Conn != frame.Conn) return;
                _heard.Remove(user = left.User);
            }
            else
            {
                if (CallRelayFormat.Open(keys, frame.Payload) is not { } message || message.From == _me || !_inCall(message.From)) return;
                if (_conns.TryGetValue(frame.Conn, out var known) && (known.User != message.From || message.Seq <= known.Seq)) return;
                _conns[frame.Conn] = (message.From, message.Seq);
                user = message.From;
                if (message.Song is { } song) _heard[user] = new(now = new(song, Environment.TickCount64 - frame.AgeMs), _epoch, frame.Conn);
                else if (!_heard.Remove(user)) return;
            }
        }
        Changed?.Invoke(user, now);
    }

    // again: on a (re)connect, where the topic wants it afresh; otherwise only what wasn't sent yet goes.
    private void SendMine(bool again)
    {
        byte[] message;
        CallRelayClient relay;
        lock (_gate)
        {
            if (_stopped || _relay is not { Open: true } open || _keys is not { } keys || !again && Equals(_sentFor, _mine)) return;
            relay = open;
            _sentFor = _mine;
            var song = _mine is { } mine ? mine.Song with { PositionMs = mine.Song.PositionMs + (mine.Song.Playing ? Environment.TickCount64 - mine.At : 0) } : null;
            message = CallRelayFormat.Seal(keys, new(_me, ++_seq, song));
            _sentAt = Environment.TickCount64;
        }
        _ = relay.SendAsync(message);
    }
}
