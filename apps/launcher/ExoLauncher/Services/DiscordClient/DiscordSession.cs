using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Exo's own Discord client (the Discord room): one gateway
/// connection (gateway.discord.gg, v9, JSON) that identifies as Discord's web client
/// (<see cref="DiscordRest.Properties"/>), keeps <see cref="DiscordCache"/> current and passes
/// what the user may see to the UI as discord.* events. Heartbeats as the gateway asks,
/// resumes after a drop, and starts over when Discord says the session is gone; a rejected
/// sign-in (close 4004) signs out. It only ever acts on what the user does in the room:
/// no automation, nothing on anyone else's behalf.
/// </summary>
internal sealed partial class DiscordSession
{
    private const string GatewayUrl = "wss://gateway.discord.gg";

    internal static event Action<string, object?>? Event;

    /// <summary>A video frame for the page: its note (discord.video.frame's fields but the picture) and the frame itself.</summary>
    internal static event Action<object, byte[]>? VideoFrame;

    private static readonly Lock StartGate = new();
    private static DiscordSession? _current;
    private static CancellationTokenSource? _signIn;
    private static string _state = "signedOut";
    private static string? _message;

    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly GatewaySendLimit _sendLimit = new();
    private ClientWebSocket? _ws;
    private string? _sessionId;
    private string? _resumeUrl;
    private long? _seq;
    private bool _acked = true;

    internal DiscordCache Cache { get; } = new();
    internal DiscordRest Rest { get; }

    private DiscordSession(string token)
    {
        _token = token;
        Rest = new DiscordRest(token);
    }

    internal static DiscordSession? Current => _current;

    internal static object BuildStatusResponse(string state, string? message, bool signedIn) => new { state, message, signedIn };

    internal static object Status() => BuildStatusResponse(_state, _message, DiscordToken.HasToken());

    private static bool NoiseSuppressionWanted =>
        Voice.VoiceProcessing.Current.NoiseSuppression || App.Services.Settings.Shared.DiscordOthersNoiseSuppression;

    /// <summary>Connects with the saved sign-in; does nothing when it already runs.</summary>
    internal static void Start()
    {
        if (DiscordParityMock.IsEnabled) return;
        var started = false;
        lock (StartGate)
        {
            if (_current is not null) return;
            if (DiscordToken.Read() is not { } token) { SetState("signedOut"); return; }
            var session = new DiscordSession(token);
            _current = session;
            _state = "connecting";
            _message = null;
            started = true;
            if (NoiseSuppressionWanted) Voice.NoiseSuppressor.KeepOne = true;
            _ = Task.Run(() => session.RunAsync()).ContinueWith(run => session.Ended(run.Exception), TaskScheduler.Default);
        }
        if (started) Event?.Invoke("discord.state", new { state = "connecting", message = (string?)null });
    }

    // The session's loop is over. A stop or a fatal answer already said why; anything else that ended it is logged and
    // lets the next Start run again, instead of staying "connecting" until Exo restarts.
    private void Ended(AggregateException? failure)
    {
        if (failure is null || _stop.IsCancellationRequested) return;
        AppLog.Warn("Discord client: the connection stopped (" + failure.InnerException?.GetType().Name + ").");
        StopTimers();
        Rest.Dispose();
        Drop(this);
        SetState("error", "The connection to Discord stopped. Try again.");
    }

    // What a session armed dies with it: after a sign-out a saved-message reminder must not still pop up with that
    // account's text, nor a custom status clear send a presence for a connection that is gone; and the warm noise
    // suppression model may go a minute after its last use.
    private void StopTimers()
    {
        Voice.NoiseSuppressor.KeepOne = false;
        lock (_reminderGate)
        {
            _reminderTimer?.Dispose();
            _reminderTimer = null;
            _reminders.Clear();
        }
        Interlocked.Exchange(ref _customExpiry, null)?.Cancel();
        Interlocked.Exchange(ref _statusExpiry, null)?.Cancel();
        StopKeybinds();
        StopFriendLinks();
        lock (_linkGate)
        {
            _linkSync?.Dispose();
            _linkSync = null;
            _linkLapse?.Dispose();
            _linkLapse = null;
        }
    }

    /// <summary>Disconnects; the sign-in stays saved until <see cref="SignOutAsync"/> clears it.</summary>
    internal static void Stop()
    {
        lock (StartGate)
        {
            _signIn?.Cancel();
            if (_current is not { } session) return;
            _current = null;
            RaiseCallStateChanged();
            session.LeaveVoice();
            session._stop.Cancel();
            session._ws?.Abort();
            session.StopTimers();
            // Its REST client (an HTTP/2 connection kept warm and pinged) goes with it, on sign-out and account switch.
            session.Rest.Dispose();
        }
        SetState(DiscordToken.HasToken() ? "off" : "signedOut");
        Volatile.Write(ref _lastBadge, 0);
        BadgeChanged?.Invoke(0);
    }

    /// <summary>Starts a QR sign-in; its steps arrive as discord.signin events.</summary>
    internal static void SignIn()
    {
        CancellationTokenSource cts;
        lock (StartGate)
        {
            _signIn?.Cancel();
            _signIn = cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        }
        _ = Task.Run(async () =>
        {
            await DiscordRest.LoadBuildAsync(cts.Token).ConfigureAwait(false);
            var token = await DiscordRemoteAuth.RunAsync(step => Event?.Invoke("discord.signin", step), cts.Token).ConfigureAwait(false);
            if (token is null) return;
            if (!DiscordToken.Save(token))
            {
                Event?.Invoke("discord.signin", new DiscordRemoteAuth.Step("error", Message: "Exo couldn't save the sign-in."));
                return;
            }
            AppLog.Info("Discord client: signed in by QR code.");
            Event?.Invoke("discord.signin", new DiscordRemoteAuth.Step("done"));
            // Added beside an account already connected: that one stays remembered, the new one connects.
            Stop();
            Start();
        });
    }

    internal static void CancelSignIn()
    {
        lock (StartGate) _signIn?.Cancel();
    }

    /// <summary>The accounts signed in on this PC, the connected one marked; never their sign-ins.</summary>
    internal static object Accounts()
    {
        var current = _current?.Cache.Me?.Id;
        return DiscordAccounts.ListSummaries().Select(a => new { id = a.Id, name = a.Name, avatar = a.Avatar, current = a.Id == current }).ToList();
    }

    /// <summary>Connects as another account signed in on this PC, as Discord's Switch accounts does; null when it did.</summary>
    internal static string? SwitchTo(string id)
    {
        if (_current?.Cache.Me?.Id == id) return null;
        if (DiscordAccounts.TokenOf(id) is not { } token) return "That account isn't signed in on this PC any more.";
        Stop();
        if (!DiscordToken.Save(token)) return "Exo couldn't switch accounts.";
        AppLog.Info("Discord client: switched account.");
        Start();
        return null;
    }

    /// <summary>Signs out: tells Discord to end this sign-in, then forgets it.</summary>
    internal static async Task SignOutAsync()
    {
        var token = DiscordToken.Read();
        var account = _current?.Cache.Me?.Id;
        Stop();
        DiscordToken.Clear();
        if (account is not null) FriendLinkKeys.Forget(account);
        if (token is not null) DiscordAccounts.ForgetToken(token);
        SetState("signedOut");
        if (token is null) return;
        try
        {
            using var rest = new DiscordRest(token);
            await rest.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLog.Info("Discord client: sign-out call failed (" + ex.GetType().Name + ")."); }
        AppLog.Info("Discord client: signed out.");
    }

    /// <summary>Asks the gateway for live messages and typing in a channel of a large server, as Discord's client does on opening it.</summary>
    internal void Subscribe(string guildId, string channelId)
    {
        lock (_listChannel)
        {
            if (_listChannel.GetValueOrDefault(guildId) == channelId) return;
            _listChannel[guildId] = channelId;
        }
        Subscribe(guildId, channelId, [(0, 99)]);
    }

    /// <summary>
    /// The people column has no list to show. The channel open if there is one, else the one already asked for, else the first this
    /// account can see (<see cref="DiscordCache.MemberListChannel"/>): asked again even if it was asked before, since an ask sent
    /// around a reconnect is lost and nothing else would repeat it.
    /// </summary>
    internal void AskMembers(string guildId, string? openChannelId)
    {
        string? channelId;
        lock (_listChannel) channelId = openChannelId ?? _listChannel.GetValueOrDefault(guildId);
        channelId = Cache.MemberListChannel(guildId, channelId);
        if (channelId is null) return;
        lock (_listChannel) _listChannel[guildId] = channelId;
        Subscribe(guildId, channelId, [(0, 99)]);
    }

    /// <summary>
    /// The member list scrolled to row <paramref name="at"/>: the hundred rows there and the next hundred, with the first
    /// hundred kept, as Discord's client asks for the ranges in view.
    /// </summary>
    internal void MembersAt(string guildId, int at)
    {
        string? channelId;
        lock (_listChannel) channelId = _listChannel.GetValueOrDefault(guildId);
        if (channelId is null) return;
        var from = Math.Max(0, at) / 100 * 100;
        Subscribe(guildId, channelId, from == 0 ? [(0, 99), (100, 199)] : [(0, 99), (from, from + 99), (from + 100, from + 199)]);
    }

    // The channel whose member list each server shows (the one last opened there).
    private readonly Dictionary<string, string> _listChannel = [];

    // A new connection: the servers' open channels are asked for again, so the member lists and live messages the
    // room still shows keep coming (the room stays on screen through a reconnect and doesn't open them again).
    // Servers whose voice channel statuses were asked of Discord this connection (op 43), as Discord's client asks once
    // when it first shows a server's channels.
    private readonly HashSet<string> _statusAsked = [];

    /// <summary>Asks Discord for a server's voice channel statuses, once a connection (they arrive as CHANNEL_INFO).</summary>
    internal void AskVoiceStatuses(string guildId)
    {
        lock (_statusAsked) if (!_statusAsked.Add(guildId)) return;
        _ = SendAsync(43, new JsonObject { ["guild_id"] = guildId, ["fields"] = new JsonArray("status", "voice_start_time") });
    }

    private void Resubscribe()
    {
        lock (_statusAsked) _statusAsked.Clear();
        KeyValuePair<string, string>[] open;
        lock (_listChannel) open = [.. _listChannel];
        foreach (var (guildId, channelId) in open) Subscribe(guildId, channelId, [(0, 99)]);
    }

    private void Subscribe(string guildId, string channelId, (int From, int To)[] ranges)
    {
        if (_noSubscribe) return;
        _subscribedAt = Environment.TickCount64;
        _ = SendAsync(37, new JsonObject
        {
            ["subscriptions"] = new JsonObject
            {
                [guildId] = new JsonObject
                {
                    ["typing"] = true,
                    // The server's open threads and which you're in (THREAD_LIST_SYNC), as Discord's client asks: the
                    // sidebar lists the joined ones under their channel.
                    ["threads"] = true,
                    ["activities"] = false,
                    ["member_updates"] = false,
                    ["channels"] = new JsonObject { [channelId] = new JsonArray([.. ranges.Select(r => (JsonNode)new JsonArray(r.From, r.To))]) },
                },
            },
        });
    }

    // A gateway that refuses the subscription (closes with a decode error right after) is not asked again this run.
    private bool _noSubscribe;
    private long _subscribedAt = long.MinValue / 2;

    // Connections that never reached READY in a row; one report per stall (CrashReport kind DiscordGateway).
    private readonly FailureStreak _gatewayStreak = new();
    private int? _lastGatewayClose;
    private bool _lastGatewayNetworkFailure;

    private static void SetState(string state, string? message = null)
    {
        _state = state;
        _message = message;
        Event?.Invoke("discord.state", new { state, message });
    }

    private Task RunAsync() => LoopAsync();

    private async Task LoopAsync()
    {
        var backoff = GatewayBackoff.Start;
        if (_state != "connecting") SetState("connecting");
        await DiscordRest.LoadBuildAsync(_stop.Token).ConfigureAwait(false);
        while (!_stop.IsCancellationRequested)
        {
            var began = Environment.TickCount64;
            var (fatal, ready, limited) = await ConnectOnceAsync().ConfigureAwait(false);
            if (_stop.IsCancellationRequested) return;
            OnGatewayLost();
            if (fatal is not null)
            {
                StopTimers();
                Rest.Dispose();
                Drop(this);
                if (fatal == "signedOut") { DiscordToken.Clear(); DiscordAccounts.ForgetToken(_token); if (Cache.Me?.Id is { } gone) FriendLinkKeys.Forget(gone); }
                SetState(fatal == "signedOut" ? "signedOut" : "error",
                    fatal == "signedOut" ? "Discord signed you out. Sign in again." : fatal);
                return;
            }
            if (ready) _gatewayStreak.Record(true, DateTime.UtcNow);
            else if (_gatewayStreak.Record(false, DateTime.UtcNow) && FailureReportRules.IsDiscordGatewayReportable(_lastGatewayClose, _lastGatewayNetworkFailure))
                CrashReport.Submit("DiscordGateway", $"Discord gateway failed {FailureStreak.DefaultThreshold} times in a row, last close code {_lastGatewayClose?.ToString() ?? "none"}", "discord");
            // Rate limited (4008) waits a minute; the backoff starts over only after a connection that stayed up a minute (GatewayBackoff).
            var stable = ready && Environment.TickCount64 - began >= GatewayBackoff.Stable.TotalMilliseconds;
            var (wait, next) = GatewayBackoff.Plan(backoff, limited, stable, Random.Shared.Next(1000));
            SetState("connecting");
            try { await Task.Delay(wait, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = next;
        }
    }

    // Returns (fatal reason or null, whether this connection got ready, whether Discord closed it for sending too much).
    private async Task<(string? Fatal, bool Ready, bool Limited)> ConnectOnceAsync()
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://discord.com");
        ws.Options.SetRequestHeader("User-Agent", DiscordRest.UserAgent);
        _ws = ws;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        using var inflater = new GatewayZlibInflater();
        var ready = false;
        var sw = Stopwatch.StartNew();
        var connectMs = 0.0;
        var networkFailure = false;
        try
        {
            await ws.ConnectAsync(new Uri((_resumeUrl ?? GatewayUrl) + "/?encoding=json&v=9&compress=zlib-stream"), connection.Token).ConfigureAwait(false);
            connectMs = sw.Elapsed.TotalMilliseconds;
            while (ws.State == WebSocketState.Open)
            {
                var (doc, wireBytes, jsonBytes) = await ReceiveFrameAsync(ws, 256 << 20, inflater, connection.Token).ConfigureAwait(false);
                using var owned = doc;
                if (owned is null) break;
                var root = owned.RootElement;
                var op = root.GetProperty("op").GetInt32();
                switch (op)
                {
                    case 10:
                        var interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        _acked = true;
                        _sendLimit.Reset();
                        _ = HeartbeatAsync(ws, interval, connection.Token);
                        if (_sessionId is not null && _seq is not null)
                            await SendAsync(6, new JsonObject { ["token"] = _token, ["session_id"] = _sessionId, ["seq"] = _seq }).ConfigureAwait(false);
                        else
                            await SendAsync(2, Identify()).ConfigureAwait(false);
                        break;
                    case 11:
                        _acked = true;
                        break;
                    case 1:
                        await SendAsync(1, _seq is { } s ? JsonValue.Create(s) : null).ConfigureAwait(false);
                        break;
                    case 7:
                        return (null, ready, false);
                    case 9:
                        if (root.GetProperty("d").ValueKind != JsonValueKind.True) { _sessionId = null; _seq = null; _resumeUrl = null; }
                        await Task.Delay(Random.Shared.Next(1000, 5000), connection.Token).ConfigureAwait(false);
                        return (null, ready, false);
                    case 0:
                        if (root.TryGetProperty("s", out var seq) && seq.ValueKind == JsonValueKind.Number) _seq = seq.GetInt64();
                        var type = root.GetProperty("t").GetString() ?? "";
                        var data = root.GetProperty("d");
                        if (type is "READY" or "RESUMED") ready = true;
                        // One event Exo can't read (a shape Discord changed) is logged and skipped, never the end of the session.
                        try
                        {
                            var loadStart = type == "READY" ? sw.Elapsed.TotalMilliseconds : 0.0;
                            Dispatch(type, data);
                            if (type == "READY")
                            {
                                var nowMs = sw.Elapsed.TotalMilliseconds;
                                AppLog.Info($"PERF discord gateway ready connectMs={connectMs:F1} readyMs={nowMs:F1} loadReadyMs={nowMs - loadStart:F1} wireBytes={wireBytes} jsonBytes={jsonBytes}");
                            }
                            if (Cache.TakeLinkPeersChanged()) SyncLinksSoon();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                        {
                            AppLog.Warn($"Discord client: {type} not read ({ex.GetType().Name}: {ex.Message}) at {ex.StackTrace?.Split('\n').FirstOrDefault(line => line.Contains("ExoLauncher", StringComparison.Ordinal))?.Trim()}.");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return (null, ready, false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or IOException)
        {
            networkFailure = ex is WebSocketException or IOException or OperationCanceledException;
            AppLog.Info("Discord client: connection dropped (" + ex.GetType().Name + ").");
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            _ws = null;
        }
        var code = (int?)ws.CloseStatus;
        _lastGatewayClose = code;
        _lastGatewayNetworkFailure = networkFailure && code is null;
        if (code is not null) AppLog.Info($"Discord client: gateway closed {code}.");
        switch (code)
        {
            case 4004: return ("signedOut", ready, false);
            case 4008: return (null, ready, true);
            case 4002 when Environment.TickCount64 - _subscribedAt < 5000:
                _noSubscribe = true;
                AppLog.Info("Discord client: the gateway refused a channel subscription; not sending more.");
                break;
            case 4007 or 4009: _sessionId = null; _seq = null; _resumeUrl = null; break;
            case 4010 or 4011 or 4012 or 4013 or 4014:
                // Refused: the web build Exo identifies with is read from Discord again next time, not kept for the day.
                DiscordRest.ForgetBuild();
                return ("Discord refused the connection (" + code + ").", ready, false);
        }
        return (null, ready, false);
    }

    private void Dispatch(string type, JsonElement data)
    {
        OnSettingsDispatch(type, data);
        if (type == "READY")
        {
            _sessionId = data.GetProperty("session_id").GetString();
            _resumeUrl = data.TryGetProperty("resume_gateway_url", out var url) ? url.GetString() : null;
            Cache.LoadReady(data);
            AskForMyRoles();
            AskVoiceMembers();
            AppLog.Info("Discord client: ready.");
            if (Cache.MeCard is { } me) DiscordAccounts.Remember(me.Id, me.Name, me.Avatar, _token);
            SetState("ready");
            Event?.Invoke("discord.changed", new { guildId = (string?)null });
            PublishBadge();
            Resubscribe();
            _ = Task.Run(RunPostReadyMaintenanceAsync);
            OnGatewayReady(freshSession: true);
            UpdateKeyTick();
            return;
        }
        if (type == "RESUMED")
        {
            // A resume comes every few hours: what the cache holds by then, so its growth can be read from app.log.
            AppLog.Info("Discord client: resumed; the cache holds " + Cache.Counts() + ".");
            Resubscribe();
            OnGatewayReady(freshSession: false);
            SetState("ready");
            return;
        }
        if (type == "VOICE_STATE_UPDATE") AskVoiceMembers();
        if (OnVoiceDispatch(type, data) || OnAppsDispatch(type, data)) return;
        // A message saved or let go (here or in another app): its reminders are timed again.
        if (type is "SAVED_MESSAGE_CREATE" or "SAVED_MESSAGE_DELETE") _ = Task.Run(LoadRemindersAsync);
        var unread = false;
        if (type is "GUILD_CREATE" or "READY_SUPPLEMENTAL") AskVoiceMembers();
        foreach (var (name, payload) in Cache.Apply(type, data))
        {
            if (payload is DiscordCache.Notification notice) { Notify(notice); continue; }
            if (payload is DiscordCache.NotifyClear clear) { ClearNotices(clear.ChannelId, clear.MessageId); continue; }
            if (payload is DiscordCache.GuildChanged changed) { TellInBursts(name, changed); continue; }
            unread |= name == "discord.unread";
            Event?.Invoke(name, payload);
        }
        if (unread || type is "MESSAGE_ACK" or "CHANNEL_DELETE" or "GUILD_DELETE") PublishBadge();
        if (type == "GUILD_CREATE") AskForMyRoles();
    }

    // A server's member list and its members' colors change in bursts (a big server's list moves with every status in
    // view, also for servers no longer shown; a member chunk brings a thousand): the first reaches the page at once, then
    // one at most every quarter second while the burst lasts, the last always told. Each makes the page read the whole
    // list or the colors on screen again, which covers every change before it.
    private const int BurstMs = 250;
    private readonly Dictionary<(string Name, string Guild), long> _toldAt = [];
    private readonly HashSet<(string Name, string Guild)> _telling = [];

    private void TellInBursts(string name, DiscordCache.GuildChanged changed)
    {
        var key = (name, changed.GuildId);
        var now = Environment.TickCount64;
        long wait;
        lock (_toldAt)
        {
            if (_telling.Contains(key)) return; // one is on its way, and covers this
            var since = now - _toldAt.GetValueOrDefault(key, long.MinValue / 2);
            if (since >= BurstMs) { _toldAt[key] = now; wait = 0; }
            else { _telling.Add(key); wait = BurstMs - since; }
        }
        if (wait == 0) { Event?.Invoke(name, changed); return; }
        _ = Task.Delay((int)wait, _stop.Token).ContinueWith(_ =>
        {
            lock (_toldAt) { _telling.Remove(key); _toldAt[key] = Environment.TickCount64; }
            Event?.Invoke(name, changed);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    // A server that came without the user's own member would show role-only channels late (once the member list loaded)
    // or drop them after a reconnect: their member is asked for at once, as Discord's client does.
    private void AskForMyRoles()
    {
        if (Cache.Me?.Id is not { } me || Cache.GuildsWithoutMyRoles().Count == 0 || Interlocked.Exchange(ref _askingRoles, 1) == 1) return;
        // Not in the burst right after connecting, where the send limit drops member requests; once more if any are still missing.
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var after in new[] { 1500, 10_000 })
                {
                    await Task.Delay(after, _stop.Token).ConfigureAwait(false);
                    var missing = Cache.GuildsWithoutMyRoles();
                    if (missing.Count == 0) return;
                    AppLog.Info($"Discord client: asking for your roles in {missing.Count} server(s).");
                    // One request for every server (op 8 takes a list).
                    await SendAsync(8, new JsonObject
                    {
                        ["guild_id"] = new JsonArray([.. missing.Select(id => (JsonNode)id)]),
                        ["user_ids"] = new JsonArray(me),
                        ["presences"] = false,
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _askingRoles, 0); }
        });
    }

    private int _askingRoles;

    /// <summary>The conversation the room shows right now (null: none, or the room is hidden), so it doesn't notify.</summary>
    internal static string? Viewing { get; set; }

    private static int _lastBadge;

    internal static int CurrentBadge => Volatile.Read(ref _lastBadge);

    internal static event Action<int>? BadgeChanged;

    internal void PublishBadge()
    {
        var count = Cache.Badge();
        Volatile.Write(ref _lastBadge, count);
        BadgeChanged?.Invoke(count);
    }

    // Channels read here that Discord hasn't been told of yet.
    private readonly HashSet<string> _outgoingAcks = [];

    /// <summary>
    /// Marks a channel read up to a message: here at once, and to Discord as its client does, three seconds after the
    /// first read with what is read by then (a busy channel sends one ack, not one a message, and never an older one).
    /// </summary>
    internal void Ack(string channelId, string messageId)
    {
        // The channel list's dot and mention count go at once, as Discord's do when you reach the bottom.
        Event?.Invoke("discord.unread", Cache.MarkRead(channelId, messageId));
        ClearNotices(channelId);
        PublishBadge();
        lock (_outgoingAcks) if (!_outgoingAcks.Add(channelId)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, _stop.Token).ConfigureAwait(false);
                lock (_outgoingAcks) _outgoingAcks.Remove(channelId);
                if (Cache.LastRead(channelId) is { } newest) await Rest.AckAsync(channelId, newest, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Mark Unread: read up to just before a message, here (the channel lights up again, as in Discord) and on Discord.</summary>
    internal async Task<string?> MarkUnreadAsync(string channelId, string beforeId, CancellationToken ct)
    {
        Cache.MarkUnread(channelId, beforeId);
        Event?.Invoke("discord.unread", Cache.UnreadOf(channelId));
        PublishBadge();
        return (await Rest.MarkUnreadAsync(channelId, beforeId, ct).ConfigureAwait(false)).Error;
    }

    /// <summary>A server's Mark As Read: every unread channel read here at once (marks, mentions, notifications), then one bulk ack to Discord.</summary>
    internal async Task<string?> MarkServerReadAsync(string guildId, CancellationToken ct)
    {
        var reads = Cache.UnreadIn(guildId);
        foreach (var (channel, _) in reads)
        {
            Event?.Invoke("discord.unread", Cache.UnreadOf(channel));
            ClearNotices(channel);
        }
        PublishBadge();
        return reads.Count == 0 ? null : (await Rest.AckBulkAsync(reads, ct).ConfigureAwait(false)).Error;
    }

    /// <summary>
    /// A message that notifies (<see cref="DiscordCache"/> decided by Discord's rules), shown where
    /// <see cref="DiscordNotifier.Route"/> says: Discord's message sound, Exo's banner (discord.notify) or Windows',
    /// and the taskbar flash while Exo is in the background, as the user chose.
    /// </summary>
    private static void Notify(DiscordCache.Notification notice)
    {
        var settings = App.Services.Settings.Shared;
        var inFront = HostWindow.IsActive;
        // Game focus: a game is running, so messages wait in the unread badge instead of popping up.
        var route = DiscordNotifier.Route(inFront, Viewing, notice.ChannelId,
            settings.DiscordQuietWhilePlaying && App.Services.Launcher.AnyGameRunning, StreamerQuiet, settings.DiscordDesktopNotifications);
        if (route == NotifyRoute.None) return;
        Sound("message1");
        if (route == NotifyRoute.InApp) Event?.Invoke("discord.notify", notice);
        else if (route == NotifyRoute.Windows) DiscordNotifier.Show(notice);
        if (!StreamerQuiet && !inFront && settings.DiscordTaskbarFlash) FlashRequested?.Invoke();
    }

    /// <summary>
    /// Takes away a conversation's notifications, Exo's banners and Windows' (or one message's, deleted): it was read
    /// here or in another Discord app, or opened, as Discord clears them on MESSAGE_ACK, CHANNEL_SELECT and focus.
    /// </summary>
    internal static void ClearNotices(string channelId, string? messageId = null)
    {
        DiscordNotifier.Clear(channelId, messageId);
        Event?.Invoke("discord.notifyClear", new { channelId, messageId });
    }

    /// <summary>Exo came to the front: the conversation on screen is seen, so its notifications go.</summary>
    internal static void ExoFocused()
    {
        if (Viewing is { } viewing) ClearNotices(viewing);
    }

    /// <summary>
    /// Online, idle, do not disturb or invisible: this session's presence and the account setting, until a time (ms since
    /// 1970) or for good. The time is kept on the account as Discord's apps keep it, so every device goes back to Online.
    /// </summary>
    internal async Task<string?> SetStatusAsync(string status, CancellationToken ct, long? until = null)
    {
        if (status is not ("online" or "idle" or "dnd" or "invisible")) return "Not a status.";
        if (status == "online") until = null;
        Cache.SetMyStatus(status);
        await SendPresenceAsync().ConfigureAwait(false);
        SyncLinks();
        var reply = await Rest.SetStatusAsync(status, ct).ConfigureAwait(false);
        Event?.Invoke("discord.presence", new { userId = Cache.Me?.Id, status, activity = (string?)null, until });
        if (reply.Error is not null) return reply.Error;
        var (_, error) = await UserSettingsAsync(new Dictionary<string, JsonElement> { ["statusExpiresAtMs"] = JsonSerializer.SerializeToElement(until) }, ct).ConfigureAwait(false);
        ExpireStatusAt(until);
        return error;
    }

    private CancellationTokenSource? _statusExpiry;

    /// <summary>Back to Online when a status chosen for a while runs out.</summary>
    private void ExpireStatusAt(long? until)
    {
        var next = until is null ? null : new CancellationTokenSource();
        Interlocked.Exchange(ref _statusExpiry, next)?.Cancel();
        if (next is null || until is not { } at) return;
        var wait = Math.Max(0, at - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(wait, int.MaxValue)), next.Token).ContinueWith(_ =>
        {
            AppLog.Info("Discord client: the status chosen for a while ran out; back to Online.");
            return SetStatusAsync("online", _stop.Token);
        }, next.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private int _voiceWarmed;

    /// <summary>On connect: a status chosen for a while that ran out while Exo was closed ends now; one still running ends on time.</summary>
    private async Task RunPostReadyMaintenanceAsync()
    {
        try
        {
            await Task.Delay(1500, _stop.Token).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _voiceWarmed, 1) == 0)
            {
                _ = Task.Run(async () =>
                {
                    // Exo Voice is ready when Discord is: noise suppression fetched, its model loaded and kept warm while Discord is
                    // connected (NoiseSuppressor.KeepOne), and Only My Voice's voice model loaded, so a call's first words are
                    // cleaned and judged at once, with a game running too.
                    if (NoiseSuppressionWanted)
                    {
                        await Voice.NoiseSuppressorDownload.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
                        if (Voice.NoiseSuppressorDownload.Ready()) Voice.NoiseSuppressor.Create()?.Dispose();
                        Voice.SpeakerModel.Warm();
                    }
                    // NVIDIA's noise removal is gone from Exo: its runtime, fetched by earlier versions, goes too (800 MB and more).
                    var nvafx = Path.Combine(PathHelper.AppDataDir, "voice", "nvafx");
                    try { if (Directory.Exists(nvafx)) DeletionGate.DeleteDirectory(nvafx, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Discord voice: NVIDIA's old runtime stays for now: " + ex.Message); }
                });
            }
            var reminders = LoadRemindersAsync();
            var (values, _) = await UserSettingsAsync(null, _stop.Token).ConfigureAwait(false);
            if (values is not null)
            {
                if (values["statusExpiresAtMs"] is JsonValue at && at.TryGetValue(out long until) && Cache.MyStatus != "online")
                    ExpireStatusAt(until);
            }
            await reminders.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    // Sends the presence again when the custom status clears itself (its Clear after), as Discord's client drops it then.
    private CancellationTokenSource? _customExpiry;
    private readonly SemaphoreSlim _presenceGate = new(1, 1);

    private async Task SendPresenceAsync()
    {
        await _presenceGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var activities = new JsonArray();
            var settings = _userSettings ?? (await UserSettingsAsync(null, _stop.Token).ConfigureAwait(false)).Values;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var custom = CustomActivity(settings?["customStatus"], now);
            if (custom is not null) activities.Add(custom);
            await SendAsync(3, new JsonObject { ["status"] = Cache.MyStatus, ["since"] = 0, ["activities"] = activities.DeepClone(), ["afk"] = false }).ConfigureAwait(false);
            foreach (var (name, payload) in Cache.SetMyActivities(activities)) Event?.Invoke(name, payload);
            var expiry = new CancellationTokenSource();
            Interlocked.Exchange(ref _customExpiry, expiry)?.Cancel();
            if (settings?["customStatus"]?["expiresAtMs"] is JsonValue end && end.TryGetValue(out long until) && until > now)
                _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(until - now + 1000, int.MaxValue)), expiry.Token)
                    .ContinueWith(_ => SendPresenceAsync(), expiry.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        finally { _presenceGate.Release(); }
    }

    /// <summary>The custom status as Discord's client sends it in a presence (an activity of type 4), or null when there is none or it has cleared.</summary>
    internal static JsonObject? CustomActivity(JsonNode? custom, long now)
    {
        if (custom is not JsonObject c) return null;
        var expires = c["expiresAtMs"] is JsonValue e && e.TryGetValue(out long at) ? at : 0;
        if (expires != 0 && expires <= now) return null;
        var activity = new JsonObject { ["name"] = "Custom Status", ["type"] = 4 };
        if (c["text"]?.GetValue<string>() is { Length: > 0 } text) activity["state"] = text;
        var emojiName = c["emojiName"]?.GetValue<string>();
        if (c["emojiId"]?.GetValue<string>() is { } id) activity["emoji"] = new JsonObject { ["id"] = id, ["name"] = emojiName, ["animated"] = false };
        else if (emojiName is { Length: > 0 }) activity["emoji"] = new JsonObject { ["name"] = emojiName };
        if (expires != 0) activity["timestamps"] = new JsonObject { ["end"] = expires };
        return activity;
    }

    // The voice lists' members asked once each per session: a member Discord never answers for isn't asked again and again.
    private readonly HashSet<string> _voiceAsked = [];

    /// <summary>Asks for the members of everyone in voice whose nickname Exo doesn't know yet (after the event applies).</summary>
    private void AskVoiceMembers() => _ = Task.Run(async () =>
    {
        await Task.Delay(500).ConfigureAwait(false);
        foreach (var (guild, users) in Cache.VoiceMembersUnknown())
        {
            List<string> fresh;
            lock (_voiceAsked) fresh = [.. users.Where(id => _voiceAsked.Add(guild + ":" + id))];
            RequestMembers(guild, fresh);
        }
    });

    /// <summary>Asks Discord for members by id (op 8), as its client does for message authors it hasn't listed yet.</summary>
    internal void RequestMembers(string guildId, IReadOnlyList<string> userIds)
    {
        // Discord takes 100 ids a request; a full voice list past that was marked asked and never sent.
        foreach (var batch in userIds.Chunk(100))
            _ = SendAsync(8, new JsonObject
            {
                ["guild_id"] = new JsonArray(guildId),
                ["user_ids"] = new JsonArray([.. batch.Select(id => (JsonNode)id)]),
                ["presences"] = false,
            });
    }

    /// <summary>Asks Discord for members whose name starts with the text (op 8), as its client does while you type an @.</summary>
    internal void SearchMembers(string guildId, string query) =>
        _ = SendAsync(8, new JsonObject
        {
            ["guild_id"] = new JsonArray(guildId),
            ["query"] = query,
            ["limit"] = 10,
            ["presences"] = false,
        });

    private JsonObject Identify() => new()
    {
        ["token"] = _token,
        // DEDUPE_USER_OBJECTS: READY carries users[], recipient_ids and merged_members (the user's own roles per server).
        // AUTO_CALL_CONNECT: a DM call already on as Exo connects (one joined from a phone, one that outlived Exo's
        // restart) comes as its CALL_CREATE, as in Discord's own client; without it Exo never knew of it, so no Join.
        ["capabilities"] = (1 << 4) | (1 << 12),
        ["properties"] = DiscordRest.Properties(),
        ["presence"] = new JsonObject { ["status"] = "unknown", ["since"] = 0, ["activities"] = new JsonArray(), ["afk"] = false },
        ["compress"] = false,
    };

    private async Task HeartbeatAsync(ClientWebSocket ws, int interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay((int)(interval * Random.Shared.NextDouble()), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (!_acked) { ws.Abort(); return; }
                _acked = false;
                await SendAsync(1, _seq is { } s ? JsonValue.Create(s) : null).ConfigureAwait(false);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private async Task SendAsync(int op, JsonNode? d)
    {
        if (_ws is not { State: WebSocketState.Open } ws) return;
        // Heartbeats, identify and resume always go; the rest keep under Discord's limit. A member request
        // over it is dropped (the room asks again later); anything else waits its turn.
        if (op is not (1 or 2 or 6))
            for (var wait = _sendLimit.Take(Environment.TickCount64); wait > 0; wait = _sendLimit.Take(Environment.TickCount64))
            {
                if (op == 8) { AppLog.Debug("Discord client: member request held back (gateway send limit)."); return; }
                try { await Task.Delay(TimeSpan.FromMilliseconds(wait), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (_ws != ws || ws.State != WebSocketState.Open) return;
            }
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString());
        await _sendGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _sendGate.Release(); }
    }

    /// <summary>
    /// One whole text message off a socket, or null when it closed. It is read into a pooled buffer (a bigger one when it
    /// outgrows it) and parsed from there, the document taking a pooled copy of its own: a stream per message, grown and
    /// copied as it went (READY's on the large object heap) and read again to parse, was garbage at once.
    /// </summary>
    internal static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, int maxBytes, CancellationToken ct) =>
        (await ReceiveFrameAsync(ws, maxBytes, null, ct).ConfigureAwait(false)).Doc;

    internal static async Task<(JsonDocument? Doc, int WireBytes, int JsonBytes)> ReceiveFrameAsync(
        ClientWebSocket ws, int maxBytes, GatewayZlibInflater? inflater, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var count = 0;
            var binary = false;
            while (true)
            {
                if (count == buffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, count).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                var result = await ws.ReceiveAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return (null, 0, 0);
                if (result.MessageType == WebSocketMessageType.Binary) binary = true;
                count += result.Count;
                if (count > maxBytes) throw new IOException("Gateway message too large.");
                if (binary)
                {
                    if (result.EndOfMessage && GatewayZlibInflater.HasFlushSuffix(buffer.AsSpan(0, count))) break;
                }
                else if (result.EndOfMessage)
                {
                    break;
                }
            }
            if (binary && inflater is not null)
            {
                var doc = inflater.ParseCompressed(buffer, count, maxBytes, out var jsonBytes);
                return (doc, count, jsonBytes);
            }
            return (Parse(buffer.AsSpan(0, count)), count, count);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        static JsonDocument Parse(ReadOnlySpan<byte> json)
        {
            var reader = new Utf8JsonReader(json);
            return JsonDocument.ParseValue(ref reader);
        }
    }
}
