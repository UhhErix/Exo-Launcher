using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// This Exo's links to its friends' Exo apps: one relay topic per friend who shows a key (<see cref="FriendLinkFormat"/>),
/// carrying this Exo's sections to all of them and handing each friend's to <see cref="Heard"/>. Reacts only to what
/// happens: its one running cost is each socket's WebSocket ping (<see cref="CallRelayClient"/>), every 25 s.
///
/// How it can fail, and what it does then:
/// - More friends show a key than <see cref="MaxLinks"/>: links already up stay, the rest fill in the given order, and
///   whoever is left out gets the next place that opens.
/// - The relay can't be reached or drops: each link's <see cref="CallRelayClient"/> tries again after 1, 2, 4 … 60 s, so
///   it never busy-loops; meanwhile what was heard stays for the grace period, then goes.
/// - A friend's connection ends: the same grace, so a blip doesn't flicker; their reconnect says it again.
/// - Someone joins hours after a message: the relay's held copy is too old to take, so a <c>hello</c> asks for a fresh
///   one, answered only when the last one sent is older than <see cref="AnswerAfterMs"/>, so hellos can't loop.
/// - The relay replays or reflects messages: <see cref="FriendLinkFormat.Receipt"/> and the direction refuse them.
/// - A section changes in a burst: messages are coalesced (<see cref="SendDelayMs"/>), far under the relay's 20 per 10 s.
/// - A friend's key can't be used: they are skipped, once, until their key changes.
/// </summary>
internal sealed class FriendLinks
{
    internal const int MaxLinks = 16;
    internal const long AnswerAfterMs = FriendLinkFormat.FreshMs / 2;
    private const int SendDelayMs = 250;

    private sealed class Link(string friend, string friendKey, FriendLinkFormat.Keys keys, CallRelayClient relay)
    {
        public readonly string Friend = friend, FriendKey = friendKey;
        public readonly FriendLinkFormat.Keys Keys = keys;
        public readonly CallRelayClient Relay = relay;
        public readonly FriendLinkFormat.Receipt Receipt = new();
        public long SentAt;
        public uint? Conn;
        public bool Heard;
        public Timer? Grace;
    }

    private readonly string _me;
    private readonly ECDiffieHellman _key;
    private readonly Uri? _endpoint;
    private readonly int _graceMs;
    private readonly long _answerAfterMs;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Link> _links = [];
    private readonly Dictionary<string, JsonNode?> _sections = new(StringComparer.Ordinal);
    private readonly HashSet<(string Friend, string Key)> _unusable = [];
    private readonly Timer _send;
    private long _lastAt;
    private bool _stopped;

    /// <param name="key">This account's key; the caller owns it.</param>
    /// <param name="endpoint">The relay; null: no relay set, nothing is sent or heard.</param>
    /// <param name="graceMs">How long what was heard stays after a connection ends.</param>
    /// <param name="answerAfterMs">How old this side's last message must be before a hello gets a fresh one.</param>
    internal FriendLinks(string me, ECDiffieHellman key, Uri? endpoint, int graceMs = 30_000, long answerAfterMs = AnswerAfterMs)
    {
        _me = me;
        _key = key;
        _endpoint = endpoint;
        _graceMs = graceMs;
        _answerAfterMs = answerAfterMs;
        _send = new Timer(_ => SendAll());
    }

    internal int Count { get { lock (_gate) return _links.Count; } }

    /// <summary>A friend's whole message (null: they went), on whichever thread heard it.</summary>
    internal event Action<string, FriendLinkFormat.Message?>? Heard;

    /// <summary>Sets one section of what this Exo says (null: says nothing of it). Friends hear it within a moment.</summary>
    internal void Set(string type, JsonNode? value)
    {
        lock (_gate)
        {
            if (_stopped || (_sections.TryGetValue(type, out var had) ? JsonNode.DeepEquals(had, value) : value is null)) return;
            _sections[type] = value?.DeepClone();
            _send.Change(SendDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>The friends who should be linked, by id and key, the ones who win a place first. Everyone else's link ends.</summary>
    internal void SetPeers(IReadOnlyList<(string Id, string Key)> peers)
    {
        List<Link> ended = [];
        List<CallRelayClient> started = [];
        lock (_gate)
        {
            if (_stopped || _endpoint is null) return;
            var wanted = peers.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Key);
            foreach (var link in _links.Values.Where(l => wanted.GetValueOrDefault(l.Friend) != l.FriendKey).ToList())
            {
                _links.Remove(link.Friend);
                link.Grace?.Dispose();
                ended.Add(link);
            }
            foreach (var (id, key) in peers)
            {
                if (_links.Count >= MaxLinks) break;
                if (_links.ContainsKey(id) || _unusable.Contains((id, key))) continue;
                if (FriendLinkFormat.Derive(_key, _me, id, key) is not { } keys)
                {
                    _unusable.Add((id, key));
                    AppLog.Info("Friend link: a friend's key can't be used.");
                    continue;
                }
                var relay = new CallRelayClient(_endpoint, keys.Topic, "Friend link");
                var link = new Link(id, key, keys, relay);
                relay.Connected += () => OnConnected(link);
                relay.Received += frame => OnFrame(link, frame);
                _links[id] = link;
                started.Add(relay);
            }
        }
        foreach (var relay in started) relay.Start();
        foreach (var link in ended)
        {
            _ = link.Relay.CloseAsync(null);
            if (link.Heard) Heard?.Invoke(link.Friend, null);
        }
    }

    /// <summary>Ends every link: the relay tells each friend this one left.</summary>
    internal async Task StopAsync()
    {
        List<Link> links;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            links = [.. _links.Values];
            _links.Clear();
        }
        await _send.DisposeAsync().ConfigureAwait(false);
        foreach (var link in links) link.Grace?.Dispose();
        await Task.WhenAll(links.Select(link => link.Relay.CloseAsync(null))).ConfigureAwait(false);
    }

    private void OnConnected(Link link)
    {
        // What was heard before may be old: it stays for the grace, and the friend's replay or answer renews it.
        ArmGrace(link);
        Send(link, hello: true);
    }

    private void OnFrame(Link link, CallRelayFormat.Frame frame)
    {
        if (frame.Gone)
        {
            lock (_gate) { if (frame.Conn != link.Conn) return; }
            ArmGrace(link);
            return;
        }
        if (FriendLinkFormat.Open(link.Keys, frame.Payload) is not { } message) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool answer;
        lock (_gate)
        {
            if (_stopped || !_links.ContainsValue(link) || !link.Receipt.Accept(message.AtMs, now)) return;
            link.Conn = frame.Conn;
            link.Heard = true;
            link.Grace?.Change(Timeout.Infinite, Timeout.Infinite);
            answer = message.Hello && now - link.SentAt > _answerAfterMs;
        }
        Heard?.Invoke(link.Friend, message);
        if (answer) Send(link, hello: false);
    }

    private void ArmGrace(Link link)
    {
        lock (_gate)
        {
            if (_stopped || !link.Heard || !_links.ContainsValue(link)) return;
            link.Grace ??= new Timer(_ => GraceEnded(link));
            link.Grace.Change(_graceMs, Timeout.Infinite);
        }
    }

    private void GraceEnded(Link link)
    {
        lock (_gate)
        {
            if (_stopped || !link.Heard || !_links.ContainsValue(link)) return;
            link.Heard = false;
            link.Conn = null;
        }
        Heard?.Invoke(link.Friend, null);
    }

    private void SendAll()
    {
        List<Link> links;
        lock (_gate)
        {
            if (_stopped) return;
            links = [.. _links.Values];
        }
        foreach (var link in links) Send(link, hello: false);
    }

    private void Send(Link link, bool hello)
    {
        byte[]? message;
        lock (_gate)
        {
            if (_stopped) return;
            // Strictly later each time, so two within a millisecond both count as newer.
            var at = _lastAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastAt + 1);
            message = FriendLinkFormat.Seal(link.Keys, at, hello, _sections);
            link.SentAt = at;
        }
        if (message is not null) _ = link.Relay.SendAsync(message);
    }
}
