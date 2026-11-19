using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Voice for Exo's own Discord client: joining a server voice channel or a DM call is the main
/// gateway's op 4 (voice state update); Discord answers with this session's voice session id
/// (VOICE_STATE_UPDATE) and a voice server (VOICE_SERVER_UPDATE), and <see cref="VoiceConnection"/>
/// takes it from there. Mute and deafen are local and told to Discord the same way. A DM call that
/// rings this user (CALL_CREATE or CALL_UPDATE naming them) notifies and shows in the room until it
/// stops ringing them, ends, or they join or decline it; joining it is the user's click.
/// </summary>
internal sealed partial class DiscordSession
{
    private readonly Lock _voiceGate = new();
    private VoiceConnection? _voice;

    /// <summary>A new microphone or speakers chosen: the call in progress switches now (a watched stream's sound too).</summary>
    internal void ReopenAudio(bool capture)
    {
        _voice?.ReopenAudio(capture);
        if (capture) return;
        List<VoiceConnection> watched;
        lock (_voiceGate) watched = [.. WatchedConnections()];
        foreach (var stream in watched) stream.ReopenAudio(false);
    }
    private string? _voiceGuild, _voiceChannel, _voiceSession, _voiceState, _voiceMessage;
    private bool _muted, _deafened, _e2ee, _camera, _micSilent;
    // The others in this call who record it now (their client flags CLIPS_ENABLED): the voice panel marks the call.
    private readonly HashSet<string> _recordedBy = [];

    /// <summary>
    /// Exo Capture keeps a replay or records with the call's voices in it (Capture settings › Record the call's voices):
    /// Exo flags itself in calls as recording (CLIPS_ENABLED), as Discord's client does with clips on, so everyone in the
    /// call is told. Set by the capture's state (<see cref="MainWindow.CaptureHoldsCall"/>); never while it leaves the call out.
    /// </summary>
    internal static bool Recording
    {
        get => Volatile.Read(ref _recording) == 1;
        set
        {
            if (Interlocked.Exchange(ref _recording, value ? 1 : 0) == (value ? 1 : 0)) return;
            if (Current is { } session && session._voiceChannel is not null) session.SendVoiceState();
        }
    }
    private static int _recording;
    private Timer? _voiceTick;
    // Each join counts up; the join timeout ends only its own join, and only before it ever connected.
    private int _joinId, _connectedJoin;
    // DM calls ringing this user now.
    private readonly HashSet<string> _ringingMe = [];

    internal object VoiceStatus()
    {
        lock (_voiceGate)
            // ringing: a DM call ringing this user now, for a page that opens after the ring began (its notification says to).
            return new { state = _voiceState ?? "off", guildId = _voiceGuild, channelId = _voiceChannel, muted = _muted, deafened = _deafened, e2ee = _e2ee, micSilent = _micSilent, message = _voiceMessage, ringing = _ringingMe.FirstOrDefault(), recorded = _recordedBy.Count > 0 && _voiceChannel is not null };
    }

    /// <summary>Joins a voice channel (or a DM's call when <paramref name="guildId"/> is null), leaving any other first.</summary>
    internal string? JoinVoice(string? guildId, string channelId)
    {
        if (DaveSession.MaxVersion == 0) return "Voice isn't installed with this copy of Exo.";
        if (Cache.JoinRefusal(channelId) is { } refusal) return refusal;
        // Noise suppression's model loads (half a second) while the call connects, so its first words are cleaned too.
        if (NoiseSuppressionWanted && Voice.NoiseSuppressorDownload.Ready()) _ = Task.Run(() => Voice.NoiseSuppressor.Create()?.Dispose());
        int join;
        bool wasRinging;
        lock (_voiceGate)
        {
            _ = TearDownVoice();
            _recordedBy.Clear();
            _voiceGuild = guildId;
            _voiceChannel = channelId;
            _voiceSession = null;
            _voiceState = "connecting";
            _voiceMessage = null;
            _e2ee = false;
            _micSilent = false;
            join = ++_joinId;
            wasRinging = _ringingMe.Remove(channelId);
        }
        if (wasRinging) Event?.Invoke("discord.call", new { channelId, ringing = false });
        // Everyone's User Volume and Mute are the account's settings: read before anyone is heard.
        if (_userSettings is null) _ = UserSettingsAsync(null, _stop.Token);
        SendVoiceState();
        PublishVoice();
        AppLog.Info("Discord voice: joining.");
        _ = JoinTimeoutAsync(join);
        return null;
    }

    // A join Discord never answers (a full channel it turns away, a voice server that never comes) ends after 20 s with why.
    private async Task JoinTimeoutAsync(int join)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        lock (_voiceGate)
        {
            if (join != _joinId || _connectedJoin == join || _voiceChannel is null) return;
            _ = TearDownVoice();
            _voiceChannel = null;
            _voiceState = null;
            _voiceMessage = "Discord didn't connect the call. Try again.";
        }
        AppLog.Info("Discord voice: the join timed out.");
        SendVoiceState();
        PublishVoice();
    }

    internal void LeaveVoice()
    {
        lock (_voiceGate)
        {
            if (_voiceChannel is null) return;
            _ = TearDownVoice();
            _voiceChannel = null;
            _voiceState = null;
            _voiceMessage = null;
        }
        Sound("disconnect");
        SendVoiceState();
        PublishVoice();
    }

    internal void SetVoice(bool? muted, bool? deafened)
    {
        bool wasMuted, wasDeafened;
        lock (_voiceGate)
        {
            (wasMuted, wasDeafened) = (_muted, _deafened);
            if (deafened is { } deaf) { _deafened = deaf; if (deaf) _muted = true; else if (muted is null) _muted = false; }
            if (muted is { } mute) { _muted = mute; if (!mute) _deafened = false; }
            if (_voice is not null) { _voice.Muted = _muted; _voice.Deafened = _deafened; }
            // Deafened hears nothing, a Go Live being watched included, as in Discord.
            foreach (var stream in WatchedConnections()) stream.Deafened = _deafened;
        }
        ToggleSound(wasMuted, wasDeafened);
        if (_voiceChannel is not null) SendVoiceState();
        PublishVoice();
    }

    /// <summary>Rings the other people in a DM after joining its call, as Discord's own Call button does.</summary>
    internal Task<DiscordRest.Reply> RingAsync(string channelId, CancellationToken ct)
    {
        Sound("call_calling");
        return Rest.RingAsync(channelId, ct);
    }

    /// <summary>Decline: the call stops ringing here and Discord stops ringing this user (their other apps too).</summary>
    internal Task<DiscordRest.Reply> DeclineCallAsync(string channelId, CancellationToken ct)
    {
        lock (_voiceGate) _ringingMe.Remove(channelId);
        Event?.Invoke("discord.call", new { channelId, ringing = false });
        return Rest.StopRingingAsync(channelId, ct);
    }

    // Main gateway dispatches that concern this user's voice; true when handled here.
    /// <summary>
    /// A soundboard sound in the call this client is in, into the call's own sound (<see cref="Soundboard"/>): the user's
    /// own as they press it, at its own volume; someone else's (their client tells everyone's) at the sender's volume times
    /// Soundboard Volume, unless the soundboard is muted in Voice settings. Never while deafened.
    /// </summary>
    internal void PlaySound(string soundId, double volume)
    {
        VoiceConnection? voice;
        lock (_voiceGate) voice = _deafened ? null : _voice;
        voice?.PlaySound(soundId, (float)volume);
    }

    private void OnSoundboard(JsonElement d)
    {
        var s = App.Services.Settings.Shared;
        string? channel;
        lock (_voiceGate) channel = _voiceChannel;
        if (SoundboardEffect(d, Cache.Me?.Id, channel, s.DiscordSoundboardMuted, s.DiscordSoundboardVolume) is var (sound, volume)) PlaySound(sound, volume);
    }

    /// <summary>
    /// The soundboard sound a VOICE_CHANNEL_EFFECT_SEND has this client play, and how loud: someone else's, in the channel
    /// it is in, at the sender's volume times Soundboard Volume; null for the user's own (it played as they pressed it),
    /// another channel's, an effect with no sound (an emoji), or with the soundboard muted.
    /// </summary>
    internal static (string Sound, double Volume)? SoundboardEffect(JsonElement d, string? me, string? channel, bool muted, int soundboardVolume)
    {
        if (muted || Text(d, "sound_id") is not { } sound || Text(d, "channel_id") is not { } at || at != channel || Text(d, "user_id") == me) return null;
        var volume = d.TryGetProperty("sound_volume", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 1.0;
        return (sound, volume * soundboardVolume / 100.0);

        // Discord sends a default sound's id as a number, a server's as a string.
        static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind switch { JsonValueKind.String => p.GetString(), JsonValueKind.Number => p.GetRawText(), _ => null } : null;
    }

    private bool OnVoiceDispatch(string type, JsonElement d)
    {
        if (OnStreamDispatch(type, d)) return true;
        switch (type)
        {
            case "VOICE_STATE_UPDATE" when d.TryGetProperty("user_id", out var u) && u.GetString() == Cache.Me?.Id:
                lock (_voiceGate)
                {
                    var channel = d.TryGetProperty("channel_id", out var c) ? c.GetString() : null;
                    var session = d.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                    // You on another device (your phone) is not this client's call: it showed here as this client joining,
                    // and waited for a connection that never came. Joining there moves your voice there, as Discord does,
                    // so a call this client had ends; the channel list shows you there either way (the cache records it).
                    if (session is not null && _sessionId is not null && session != _sessionId)
                    {
                        if (channel is not null && _voiceChannel is not null) { _ = TearDownVoice(); _voiceChannel = null; _voiceState = null; }
                    }
                    else if (channel is null) { if (_voiceChannel is not null && _voiceState == "connected") { _ = TearDownVoice(); _voiceChannel = null; _voiceState = null; } }
                    else { _voiceSession = session; _voiceChannel = channel; }
                }
                PublishVoice();
                return false; // the cache still records it for the channel list
            case "VOICE_SERVER_UPDATE":
                OnVoiceServer(d);
                return true;
            case "CALL_CREATE" or "CALL_UPDATE":
                Cache.ApplyCall(type, d);
                OnRinging(d);
                Event?.Invoke("discord.changed", new { guildId = (string?)null, voice = true });
                return true;
            // Someone played a soundboard sound in a voice channel: each client plays it itself, as Discord's does.
            case "VOICE_CHANNEL_EFFECT_SEND":
                OnSoundboard(d);
                return true;
            case "CALL_DELETE":
                Cache.ApplyCall(type, d);
                if (d.GetProperty("channel_id").GetString() is { } ended)
                {
                    lock (_voiceGate) _ringingMe.Remove(ended);
                    Event?.Invoke("discord.call", new { channelId = ended, ringing = false });
                }
                Event?.Invoke("discord.changed", new { guildId = (string?)null, voice = true });
                return true;
        }
        return false;
    }

    // A call's ringing list (CALL_CREATE and CALL_UPDATE alike, as Discord's own client reads them): newly naming this
    // user rings here; no longer naming them (they declined in another app, or the caller stopped) stops it.
    private void OnRinging(JsonElement d)
    {
        if (d.GetProperty("channel_id").GetString() is not { } dm) return;
        var rung = d.TryGetProperty("ringing", out var ringing) && ringing.ValueKind == JsonValueKind.Array
            && ringing.EnumerateArray().Any(r => r.GetString() == Cache.Me?.Id);
        bool changed;
        lock (_voiceGate) changed = rung ? _voiceChannel != dm && _ringingMe.Add(dm) : _ringingMe.Remove(dm);
        if (!changed) return;
        Event?.Invoke("discord.call", new { channelId = dm, ringing = rung });
        if (!rung) return;
        Sound("call_ringing");
        DiscordNotifier.ShowCall("Incoming call", "Open Discord in Exo to join.", dm);
    }

    private void OnVoiceServer(JsonElement d)
    {
        var endpoint = d.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
        var serverId = (d.TryGetProperty("guild_id", out var g) ? g.GetString() : null) ?? (d.TryGetProperty("channel_id", out var c) ? c.GetString() : null);
        VoiceConnection connection;
        lock (_voiceGate)
        {
            if (endpoint is null || serverId is null || _voiceChannel is null || _voiceSession is null || Cache.Me is null) return;
            // Discord can move the call to another voice server: the old connection goes, and what it says as it goes is ignored.
            // In the same channel only the call's own connection is replaced: your stream and the streams you watch have their
            // own servers and go on. The whole call's teardown here ended your Go Live without telling Discord, so it froze for
            // your viewers, and going live again waited on a stream Discord still had open.
            _ = _voiceFor == _voiceChannel ? ReplaceCallConnection() : TearDownVoice();
            _voiceFor = _voiceChannel;
            // The channel's own bitrate, as Discord's client sends (a DM call uses Discord's 64 kbps).
            connection = new VoiceConnection(endpoint, serverId, _voiceChannel, Cache.Me.Id, _voiceSession, d.GetProperty("token").GetString() ?? "", bitrate: Cache.VoiceBitrate(_voiceChannel))
            {
                Muted = _muted,
                Deafened = _deafened,
                UserGain = UserGain,
                FramesWanted = static () => _framesWanted,
            };
            connection.Changed += (kind, data) => OnVoiceChanged(connection, kind, data);
            connection.CallSecret += secret => OnCallSecret(connection, secret);
            WatchCallVideo(connection);
            _voice = connection;
            _voiceTick ??= new Timer(_ => _voice?.Tick(), null, 200, 200);
        }
        connection.Start();
    }

    private void OnVoiceChanged(VoiceConnection from, string kind, object data)
    {
        // A connection that was replaced (a new voice server) or closed no longer speaks for the call.
        if (!ReferenceEquals(from, _voice)) return;
        if (kind == "speaking") { OnSpeaking(data); Event?.Invoke("discord.speaking", data); return; }
        if (kind == "left")
        {
            var gone = JsonSerializer.SerializeToElement(data).GetProperty("userId").GetString() ?? "";
            ForgetCallSong(gone);
            OnClips(gone, false);
            return;
        }
        if (kind == "ptt") { Sound(JsonSerializer.SerializeToElement(data).GetProperty("on").GetBoolean() ? "ptt_start" : "ptt_stop"); return; }
        if (kind == "clips")
        {
            var clips = JsonSerializer.SerializeToElement(data);
            OnClips(clips.GetProperty("userId").GetString() ?? "", clips.GetProperty("on").GetBoolean());
            return;
        }
        if (kind == "micSilent")
        {
            lock (_voiceGate) _micSilent = JsonSerializer.SerializeToElement(data).GetProperty("silent").GetBoolean();
            AppLog.Info(_micSilent ? "Discord voice: the microphone has sent nothing for 15 s; the voice panel says so." : "Discord voice: the microphone is heard again.");
            PublishVoice();
            return;
        }
        if (kind == "video")
        {
            var video = JsonSerializer.SerializeToElement(data);
            Event?.Invoke("discord.video", new { source = "call", userId = video.GetProperty("userId").GetString(), active = video.GetProperty("active").GetBoolean() });
            return;
        }
        var state = JsonSerializer.SerializeToElement(data);
        var gaveUp = false;
        lock (_voiceGate)
        {
            if (!ReferenceEquals(from, _voice)) return;
            _voiceState = state.GetProperty("state").GetString();
            if (state.TryGetProperty("e2ee", out var e2ee)) _e2ee = e2ee.GetBoolean();
            if (_voiceState == "connected") _connectedJoin = _joinId;
            if (_voiceState == "disconnected")
            {
                // The connection gave up (Discord ended it, or five tries failed): leave the channel on Discord too.
                gaveUp = true;
                _ = TearDownVoice();
                _voiceChannel = null;
                _voiceState = null;
                _voiceMessage = "The call disconnected.";
            }
        }
        if (gaveUp)
        {
            AppLog.Info("Discord voice: the call's connection gave up; leaving.");
            Sound("disconnect");
            SendVoiceState();
        }
        PublishVoice();
    }

    // Someone's clips on or off, or they left: the voice panel's mark follows whether anyone in the call records it.
    private void OnClips(string user, bool on)
    {
        bool was, now;
        lock (_voiceGate)
        {
            was = _recordedBy.Count > 0;
            if (on) _recordedBy.Add(user); else _recordedBy.Remove(user);
            now = _recordedBy.Count > 0;
        }
        if (was == now) return;
        AppLog.Info(now ? "Discord voice: someone in this call records it (clips on); the voice panel marks it." : "Discord voice: no one in this call records it now.");
        PublishVoice();
    }

    /// <summary>
    /// Discord's voice state flags for this client: 2 lets Discord's clips record this voice (the account allows it); 1
    /// (CLIPS_ENABLED) tells everyone in the call this client records it, only in a call while Exo Capture holds its voices.
    /// </summary>
    internal static int VoiceFlags(bool inCall, bool recording) => 2 | (inCall && recording ? 1 : 0);

    private void SendVoiceState() =>
        _ = SendAsync(4, new JsonObject
        {
            ["guild_id"] = _voiceGuild,
            ["channel_id"] = _voiceChannel,
            ["self_mute"] = _muted,
            ["self_deaf"] = _deafened,
            ["self_video"] = _camera,
            ["flags"] = VoiceFlags(_voiceChannel is not null, Recording),
        });

    private void PublishVoice()
    {
        Event?.Invoke("discord.voice", VoiceStatus());
        CallChanged(_voiceChannel is not null);
    }

    // Under _voiceGate: takes the call's parts at once, so nothing a new call sets up (its connection, the speaking
    // timer) is caught by this one's teardown, then closes them in the background: on the pool, never on the caller's
    // thread under the lock (closing waits for the sound's threads to stop).
    // The channel the call's connection is for: a voice server for the same channel is a move, not a new call.
    private string? _voiceFor;

    // Under _voiceGate: the call's own connection alone, for a move to another voice server. Go Live, the streams watched
    // and the speaking timer stay; the camera rode on the connection and goes off, as the whole call's teardown turns it off.
    private Task ReplaceCallConnection()
    {
        var voice = _voice;
        _voice = null;
        if (_camera) { _camera = false; Event?.Invoke("discord.camera", new { state = "off" }); }
        if (voice is null) return Task.CompletedTask;
        foreach (var user in voice.VideoUsers) Event?.Invoke("discord.video", new { source = "call", userId = user, active = false });
        AppLog.Info("Discord voice: the call moved to another voice server" + (_liveKey is null ? "." : "; your stream stays up."));
        return Task.Run(async () => await voice.DisposeAsync().ConfigureAwait(false));
    }

    private Task TearDownVoice()
    {
        var (voice, live, capture) = (_voice, _live, _liveCapture);
        _voiceFor = null;
        List<Watch> watched = [.. _watches.Values];
        _watches.Clear();
        _voice = null;
        _live = null;
        _liveCapture = null;
        _voiceTick?.Dispose();
        _voiceTick = null;
        StopCallSongs();
        if (_camera) { _camera = false; Event?.Invoke("discord.camera", new { state = "off" }); }
        if (_liveKey is not null)
        {
            // Discord is told, as Stop Streaming tells it: a stream it still had open made going live again wait on it.
            var key = _liveKey;
            _liveKey = null;
            _ = SendAsync(19, new JsonObject { ["stream_key"] = key });
            AppLog.Info("Discord video: your stream stopped with the call.");
            Event?.Invoke("discord.golive", new { state = "off" });
        }
        // The page closes the decoder of each camera and stream that was on.
        if (voice is not null) foreach (var user in voice.VideoUsers) Event?.Invoke("discord.video", new { source = "call", userId = user, active = false });
        foreach (var watch in watched) Event?.Invoke("discord.video", new { source = "stream", userId = UserOfKey(watch.Key), active = false });
        List<VoiceConnection> streams = [.. watched.Select(w => w.Connection).OfType<VoiceConnection>()];
        return Task.Run(() => CloseAsync(voice, streams, live, capture));

        static async Task CloseAsync(VoiceConnection? voice, List<VoiceConnection> streams, VoiceConnection? live, GoLiveCapture? capture)
        {
            foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
            if (capture is not null) await capture.DisposeAsync().ConfigureAwait(false);
            if (live is not null) await live.DisposeAsync().ConfigureAwait(false);
            if (voice is not null) await voice.DisposeAsync().ConfigureAwait(false);
        }
    }
}
