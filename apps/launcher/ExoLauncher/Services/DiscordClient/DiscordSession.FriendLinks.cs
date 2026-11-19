using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The friend link for the account signed in: what exists only for other Exo users goes here, sealed, through Exo's relay
/// (<see cref="FriendLinks"/>, <see cref="FriendLinkFormat"/>), and never through Discord. The only thing Discord carries
/// is the account's public key, in its own presence (<see cref="FriendKeyCarrier"/>), and no Discord sign-in ever goes to
/// the relay. Works only while Discord is connected, the account's switch is on and it isn't invisible.
///
/// How it can fail, and what it does then:
/// - Discord strips the key from a presence, so no friend ever finds this Exo: the log line "Share my games: key in
///   presence: yes/no" (from SESSIONS_REPLACE, on change) says so, with the fields that did come back.
/// - Discord refuses the presence and closes the connection: <see cref="KeyRefused"/> takes the key out for the rest of
///   this run, so sharing costs one reconnect at most and the account's connection never loops over it.
/// - The connection to Discord drops: links stay through a short break (a resume is the norm) and end after
///   <see cref="LinkLapseMs"/>, since no presence means no way to know who is still there.
/// - Status, friends or keys change: worked out once, a moment after the last change (<see cref="SyncDelayMs"/>); nothing polls.
/// </summary>
internal sealed partial class DiscordSession
{
    private const int SyncDelayMs = 1000, LinkLapseMs = 60_000;
    private readonly Lock _linkGate = new();
    private FriendLinks? _links;
    private ECDiffieHellman? _linkKey;
    private string? _linkKeyText;
    private Timer? _linkSync, _linkLapse;
    private int _linkSyncPending;
    private volatile bool _gatewayUp, _keyRefused;
    private volatile string? _keyInPresence;
    private long _keySentAt = long.MinValue / 2;
    private FriendKeyCarrier.Echo? _echoLogged;
    private int _peersLogged = -1;
    private int _heardLogged;

    private bool LinksWanted => LinksAllowed(_gatewayUp, _keyRefused, Cache.MyStatus, CallRelayClient.Endpoint is not null);

    /// <summary>Every gate on the link: with any of them shut there is no key in the presence, no connection, and nothing sent or heard.</summary>
    internal static bool LinksAllowed(bool gatewayUp, bool keyRefused, string status, bool hasRelay) =>
        gatewayUp && !keyRefused && status != "invisible" && hasRelay;

    /// <summary>The key to put in a presence now: none unless links are wanted and this account's key is loaded.</summary>
    private string? KeyToPublish()
    {
        lock (_linkGate) return LinksWanted ? _linkKeyText : null;
    }

    /// <summary>Discord closed the connection just after a presence with the key: it may be what it refused.</summary>
    internal static bool KeyRefused(int? closeCode, long msSinceKeySent) => closeCode is 4000 or 4002 && msSinceKeySent is >= 0 and < 5000;

    private void OnGatewayReady(bool freshSession)
    {
        _linkLapse?.Change(Timeout.Infinite, Timeout.Infinite);
        _gatewayUp = true;
        // A new connection's presence starts empty, and its cache has no one's game: everything is said and heard afresh.
        if (freshSession)
        {
            _keyInPresence = null;
            StopFriendLinks();
        }
        SyncLinksSoon();
    }

    private void OnGatewayLost()
    {
        lock (_linkGate)
        {
            if (_links is null) return;
            _linkLapse ??= new Timer(_ =>
            {
                _gatewayUp = false;
                _keyInPresence = null;
                SyncLinks();
            });
            _linkLapse.Change(LinkLapseMs, Timeout.Infinite);
        }
    }

    private void SyncLinksSoon()
    {
        if (Interlocked.CompareExchange(ref _linkSyncPending, 1, 0) != 0) return;
        lock (_linkGate)
        {
            _linkSync ??= new Timer(_ => SyncLinks());
            _linkSync.Change(SyncDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>Brings the links and the key in the presence in line with what is wanted now.</summary>
    private void SyncLinks()
    {
        Interlocked.Exchange(ref _linkSyncPending, 0);
        try
        {
            if (_stop.IsCancellationRequested) return;
            FriendLinks? links;
            lock (_linkGate)
            {
                if (LinksWanted) StartLinksLocked();
                else StopLinksLocked();
                links = _links;
            }
            if (links is not null)
            {
                var peers = Cache.LinkPeers();
                links.SetPeers(peers);
                if (Interlocked.Exchange(ref _peersLogged, peers.Count) != peers.Count)
                    AppLog.Info("Share my games: " + peers.Count + " friends show a key; " + links.Count + " linked.");
            }
            if (_gatewayUp && KeyToPublish() != _keyInPresence) SendPresenceInBackground();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Friend link: not worked out (" + ex.GetType().Name + ")."); }
    }

    private void StartLinksLocked()
    {
        if (_links is not null || Cache.Me?.Id is not { } me || FriendLinkKeys.ForAccount(me) is not { } key) return;
        _linkKey = key;
        _linkKeyText = FriendLinkFormat.EncodeKey(key);
        var links = new FriendLinks(me, key, CallRelayClient.Endpoint);
        links.Heard += OnFriendHeard;
        links.Set(FriendGame.Section, FriendGame.Write(GamesOpened.Current));
        links.Set(FriendPrep.Section, PrepSection());
        _links = links;
    }

    private void StopFriendLinks()
    {
        lock (_linkGate) StopLinksLocked();
    }

    private void StopLinksLocked()
    {
        if (_links is not { } links) return;
        var key = _linkKey;
        _links = null;
        _linkKey = null;
        _peersLogged = -1;
        links.Heard -= OnFriendHeard;
        _ = links.StopAsync().ContinueWith(_ => key?.Dispose(), TaskScheduler.Default);
        foreach (var (name, payload) in Cache.ClearExoGames()) Event?.Invoke(name, payload);
    }

    private void OnFriendHeard(string friend, FriendLinkFormat.Message? message)
    {
        // On a timer's or a socket's thread, where nothing would catch it.
        try
        {
            if (message is not null && Interlocked.Exchange(ref _heardLogged, 1) == 0) AppLog.Info("Share my games: heard from a friend's Exo.");
            var game = FriendGame.Read(message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            foreach (var (name, payload) in Cache.SetExoGame(friend, game)) Event?.Invoke(name, payload);
            foreach (var (name, payload) in Cache.SetExoPrep(friend, FriendPrep.Read(message))) Event?.Invoke(name, payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Friend link: a message not read (" + ex.GetType().Name + ")."); }
    }

    /// <summary>Said once per change: what the owner of an account looks for first when no friend ever sees them.</summary>
    private void LogKeyEcho(JsonElement sessions)
    {
        if (_keyInPresence is not { } key) return;
        var echo = FriendKeyCarrier.Find(sessions, key);
        if (echo == _echoLogged) return;
        _echoLogged = echo;
        AppLog.Info(echo switch
        {
            FriendKeyCarrier.Echo.Shown => "Share my games: key in presence: yes.",
            FriendKeyCarrier.Echo.Hidden => "Share my games: key in presence: yes, among hidden activities.",
            _ => "Share my games: key in presence: no (a custom status came back with: " + FriendKeyCarrier.FieldsKept(sessions) + ").",
        });
    }

    private void SendPresenceInBackground() => _ = Task.Run(async () =>
    {
        try { await SendPresenceAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Discord client: presence not sent (" + ex.GetType().Name + ")."); }
    });
}
