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
    private Timer? _linkSync, _linkLapse;
    private int _linkSyncPending;
    private volatile bool _gatewayUp;
    private int _peersLogged = -1;
    private int _heardLogged;

    private bool LinksWanted => LinksAllowed(_gatewayUp, Cache.MyStatus, CallRelayClient.Endpoint is not null);

    internal static bool LinksAllowed(bool gatewayUp, string status, bool hasRelay) =>
        gatewayUp && status != "invisible" && hasRelay;

    private void OnGatewayReady(bool freshSession)
    {
        _linkLapse?.Change(Timeout.Infinite, Timeout.Infinite);
        _gatewayUp = true;
        if (freshSession)
        {
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
                    AppLog.Info("Share my games: " + peers.Count + " friends paired; " + links.Count + " linked.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Friend link: not worked out (" + ex.GetType().Name + ")."); }
    }

    private void StartLinksLocked()
    {
        if (_links is not null || Cache.Me?.Id is not { } me || FriendLinkKeys.ForAccount(me) is not { } key) return;
        _linkKey = key;
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
        try
        {
            if (message is not null && Interlocked.Exchange(ref _heardLogged, 1) == 0) AppLog.Info("Share my games: heard from a friend's Exo.");
            var game = FriendGame.Read(message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            foreach (var (name, payload) in Cache.SetExoGame(friend, game)) Event?.Invoke(name, payload);
            foreach (var (name, payload) in Cache.SetExoPrep(friend, FriendPrep.Read(message))) Event?.Invoke(name, payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Friend link: a message not read (" + ex.GetType().Name + ")."); }
    }

    private void SendPresenceInBackground() => _ = Task.Run(async () =>
    {
        try { await SendPresenceAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Discord client: presence not sent (" + ex.GetType().Name + ")."); }
    });
}
