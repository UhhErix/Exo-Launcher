using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Video for Exo's own Discord client (docs/architecture/discord-video.md). Cameras arrive on the
/// call's own connection. A Go Live stream is watched on its own connection: op 20 STREAM_WATCH with
/// the stream key, then Discord's STREAM_CREATE (the stream's rtc_server_id) and STREAM_SERVER_UPDATE
/// (its voice server and token) open a listening <see cref="VoiceConnection"/> on that server, with
/// the call's session and DAVE group rtc_server_id - 1. Frames go to the page as discord.video.frame.
/// This client's own Go Live is the same in reverse: Exo Capture encodes the window or screen
/// (<see cref="GoLiveCapture"/>) and its frames go straight onto the stream's connection.
/// </summary>
internal sealed partial class DiscordSession
{
    // The streams this client watches, by stream key: as in Discord, several at once.
    private readonly Dictionary<string, Watch> _watches = [];
    private VoiceConnection? _live;
    private GoLiveCapture? _liveCapture;
    // Your stream's tile is on screen: it is sent the stream's own frames.
    private volatile bool _livePreview;
    private string? _liveKey, _liveServer, _liveChannel, _liveEndpoint, _liveToken, _liveTitle;
    // Who watches your stream now, as Discord last said (its viewer_ids); under _voiceGate.
    private string[] _liveViewers = [];
    private uint? _liveSound; // null: no sound; 0: this PC's but Exo's; else the shared app's process
    private int _liveMost; // the plan's rate: the most the stream goes up to when the server takes it
    private (CaptureAddon Addon, GoLiveSources.Source Source, GoLivePlan Plan)? _liveStart; // to start Exo Capture again in another codec
    private readonly SemaphoreSlim _liveSwapGate = new(1, 1);
    private int _expectedStreamDeletes;
    private const int StreamStart = 8_000_000;

    /// <summary>
    /// Go Live: this client's own stream of a window or screen in the call it is in. When already live on the same
    /// stream key ("Change what you share"), starts and validates the new capture first and hot-swaps the feed on
    /// the active stream connection without an op 19 STREAM_DELETE + op 18 STREAM_CREATE round-trip.
    /// </summary>
    internal async Task<string?> StartGoLiveAsync(GoLiveSources.Source source, int height, int fps, bool sound, CaptureAddon addon, CancellationToken ct)
    {
        string? initialKey;
        lock (_voiceGate) initialKey = Cache.Me is { } me ? StreamKeyOf(me.Id) : null;
        if (initialKey is null) return "Join a voice channel to go live.";

        await _liveSwapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            VoiceConnection? existingLive;
            GoLiveCapture? oldCapture;
            string? keyNow;
            lock (_voiceGate)
            {
                keyNow = Cache.Me is { } me ? StreamKeyOf(me.Id) : null;
                existingLive = _liveKey == keyNow ? _live : null;
                oldCapture = _liveCapture;
            }
            if (keyNow is null) return "Join a voice channel to go live.";

            var plan = GoLivePlan.For(source.Width, source.Height, height, fps);
            // The stream starts at most at 8 Mbit/s (Discord's server takes 9 at most) and then follows what the server says
            // it takes, up to the plan's rate (VoiceConnection.FollowServer).
            var start = plan with { Bitrate = Math.Min(plan.Bitrate, StreamStart) };
            var wantHevc = existingLive is not null && oldCapture?.Hevc == true;

            // Hot-swap path: start and validate new capture BEFORE tearing down old capture or touching the live connection.
            if (existingLive is not null && oldCapture is not null)
            {
                var (nextCapture, hotErr) = await GoLiveCapture.StartAsync(addon, source, start, ct, hevc: wantHevc).ConfigureAwait(false);
                if (nextCapture is null) return hotErr;
                WireCapture(nextCapture);
                uint? newSound = !sound ? null : source.Kind == "window" ? source.ProcessId : 0;
                bool swapped;
                lock (_voiceGate)
                {
                    var currentKey = Cache.Me is { } me ? StreamKeyOf(me.Id) : null;
                    swapped = ReferenceEquals(_live, existingLive) && ReferenceEquals(_liveCapture, oldCapture) && currentKey == _liveKey;
                    if (swapped)
                    {
                        oldCapture.Retiring = true;
                        _liveCapture = nextCapture;
                        _liveMost = plan.Bitrate;
                        _liveStart = (addon, source, start);
                        _liveTitle = source.Title;
                        _liveSound = newSound;
                    }
                }
                if (!swapped)
                {
                    nextCapture.Retiring = true;
                    await nextCapture.DisposeAsync().ConfigureAwait(false);
                    return "Join a voice channel to go live.";
                }
                await oldCapture.DisposeAsync().ConfigureAwait(false);
                existingLive.ReplaceSoundshare(newSound);
                existingLive.FollowServer(nextCapture.Bitrate, Math.Max(nextCapture.Bitrate, _liveMost));
                _ = existingLive.SetVideoAsync(nextCapture.Width, nextCapture.Height, nextCapture.Fps, Math.Max(nextCapture.Bitrate, _liveMost));
                if (!nextCapture.Hevc) existingLive.EncodesH265 = nextCapture.HevcSupported;
                nextCapture.Keyframe();
                Event?.Invoke("discord.golive", new { state = "live", title = source.Title });
                AppLog.Info($"Discord video: changed shared source to {source.Title} ({nextCapture.Width}x{nextCapture.Height}, {nextCapture.Fps} fps).");
                return null;
            }

            // Fresh stream path (not live yet, or replacing a half-open connection cleanly).
            StopGoLive();
            var (capture, error) = await GoLiveCapture.StartAsync(addon, source, start, ct).ConfigureAwait(false);
            if (capture is null) return error;
            _liveMost = plan.Bitrate;
            _liveStart = (addon, source, start);
            WireCapture(capture);
            string? key, guild, channel;
            lock (_voiceGate)
            {
                key = Cache.Me is { } me ? StreamKeyOf(me.Id) : null;
                if (key is not null)
                {
                    _liveKey = key;
                    _liveViewers = [];
                    _liveServer = _liveChannel = _liveEndpoint = _liveToken = null;
                    _liveCapture = capture;
                    _liveTitle = source.Title;
                    _liveSound = !sound ? null : source.Kind == "window" ? source.ProcessId : 0;
                }
                guild = _voiceGuild;
                channel = _voiceChannel;
            }
            if (key is null)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
                return "Join a voice channel to go live.";
            }
            _ = SendAsync(18, new JsonObject { ["type"] = guild is null ? "call" : "guild", ["guild_id"] = guild, ["channel_id"] = channel, ["preferred_region"] = null });
            _ = SendAsync(22, new JsonObject { ["stream_key"] = key, ["paused"] = false });
            Event?.Invoke("discord.golive", new { state = "connecting", title = source.Title });
            AppLog.Info($"Discord video: going live ({capture.Width}x{capture.Height}, {capture.Fps} fps, {capture.Bitrate / 1000} kbit/s, {source.Kind}, sound {(sound ? "on" : "off")}).");
            return null;
        }
        finally
        {
            _liveSwapGate.Release();
        }
    }

    /// <summary>Ends this client's Go Live; why reaches the page when it ended on its own.</summary>
    internal void StopGoLive(string? why = null)
    {
        string? key;
        VoiceConnection? live;
        GoLiveCapture? capture;
        lock (_voiceGate)
        {
            key = _liveKey;
            live = _live;
            capture = _liveCapture;
            _liveKey = null;
            _live = null;
            _liveCapture = null;
            if (key is not null) _expectedStreamDeletes++;
        }
        _liveStart = null;
        if (capture is not null)
        {
            capture.Retiring = true;
            _ = capture.DisposeAsync().AsTask();
        }
        if (live is not null) _ = live.DisposeAsync().AsTask();
        if (key is null) return;
        AppLog.Info("Discord video: your stream stopped" + (why is null ? "." : " (" + why.TrimEnd('.') + ")."));
        _ = SendAsync(19, new JsonObject { ["stream_key"] = key });
        Event?.Invoke("discord.golive", new { state = "off", message = why });
    }

    /// <summary>Your stream's tile shows or leaves: it plays the very frames viewers get (from a fresh key frame), or stops.</summary>
    internal void WatchOwnStream(bool on)
    {
        _livePreview = on;
        if (on) _liveCapture?.Keyframe();
        else if (Cache.Me is { } me) Event?.Invoke("discord.video", new { source = "self", userId = me.Id, active = false });
    }

    internal Task<string?> GoLivePreviewAsync(int width) => _liveCapture?.PreviewAsync(width) ?? Task.FromResult<string?>(null);

    // A Go Live capture's frames go to the stream, and to your own tile while it shows; its end ends the stream, unless
    // it is being replaced by one in another codec.
    private void WireCapture(GoLiveCapture capture)
    {
        capture.Frame += (frame, timestamp) =>
        {
            _live?.SendVideoFrame(frame, timestamp, capture.Hevc);
            if (_livePreview && Cache.Me is { } me)
                PublishFrame("self", me.Id, timestamp, capture.Hevc ? H265Rtp.IsKeyframe(frame) : H264Rtp.IsKeyframe(frame), frame, hevc: capture.Hevc);
        };
        // sound: an app the stream's sound leaves out while it plays the call or your mic, said on the stream's status line.
        capture.Stats += (sent, bits) => Event?.Invoke("discord.golive.stats", new { width = capture.Width, height = capture.Height, fps = sent, bitrate = bits, max = Math.Max(capture.Bitrate, _liveMost), codec = capture.Hevc ? "H.265" : "H.264", sound = _live?.SoundNote });
        capture.Ended += why => { if (ReferenceEquals(_liveCapture, capture) && !capture.Retiring) StopGoLive("Your stream ended: " + why + "."); };
    }

    // The server picked the stream's codec (H.265 when every viewer can play it, H.264 again when one who can't joins):
    // release the previous encoder first so there are never two concurrent encoders, then start the chosen codec.
    // If H.265 fails to start, keep streaming H.264 on the live connection rather than tearing down the stream.
    private async Task SwitchCodecAsync(VoiceConnection live, bool hevc)
    {
        await _liveSwapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            GoLiveCapture? old;
            (CaptureAddon Addon, GoLiveSources.Source Source, GoLivePlan Plan)? start;
            lock (_voiceGate)
            {
                if (!ReferenceEquals(_live, live)) return;
                (old, start) = (_liveCapture, _liveStart);
                if (old is null || start is not { } || old.Hevc == hevc || (hevc && !old.HevcSupported)) return;
                old.Retiring = true;
                _liveCapture = null;
            }
            var s = start!.Value;
            var bitrate = old.Bitrate;
            await old.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_live, live)) return;

            var (next, error) = await GoLiveCapture.StartAsync(s.Addon, s.Source, s.Plan with { Bitrate = bitrate }, CancellationToken.None, hevc, allowFallback: !hevc).ConfigureAwait(false);
            if (next is null || next.Hevc != hevc)
            {
                if (next is not null)
                {
                    next.Retiring = true;
                    await next.DisposeAsync().ConfigureAwait(false);
                }
                if (hevc && ReferenceEquals(_live, live))
                {
                    live.FallbackToH264();
                    AppLog.Info($"Discord video: H.265 did not start ({error ?? "unsupported"}); continuing in H.264.");
                    var (h264, h264Err) = await GoLiveCapture.StartAsync(s.Addon, s.Source, s.Plan with { Bitrate = bitrate }, CancellationToken.None, hevc: false, allowFallback: true).ConfigureAwait(false);
                    if (h264 is not null)
                    {
                        WireCapture(h264);
                        bool active;
                        lock (_voiceGate)
                        {
                            active = ReferenceEquals(_live, live);
                            if (active) _liveCapture = h264;
                        }
                        if (active) { h264.Keyframe(); return; }
                        h264.Retiring = true;
                        await h264.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    error = h264Err ?? error;
                }
                if (ReferenceEquals(_live, live)) StopGoLive($"Your stream ended: it couldn't change to {(hevc ? "H.265" : "H.264")} ({error ?? "Exo Capture failed"}).");
                return;
            }

            WireCapture(next);
            bool current;
            lock (_voiceGate)
            {
                current = ReferenceEquals(_live, live);
                if (current) _liveCapture = next;
            }
            if (!current)
            {
                next.Retiring = true;
                await next.DisposeAsync().ConfigureAwait(false);
                return;
            }
            AppLog.Info($"Discord video: your stream now sends {(hevc ? "H.265" : "H.264")}.");
            next.Keyframe();
        }
        finally
        {
            _liveSwapGate.Release();
        }
    }

    private void OpenLive()
    {
        VoiceConnection live;
        string? title;
        uint? sound;
        lock (_voiceGate)
        {
            if (_live is not null || _liveKey is null || _liveServer is null || _liveEndpoint is null || _liveToken is null
                || _voiceSession is null || Cache.Me is null)
            {
                if (_live is null && _liveKey is not null && _liveServer is not null && _liveEndpoint is not null)
                    AppLog.Info($"Discord video: your stream can't connect yet (token {(_liveToken is null ? "missing" : "given")}, voice session {(_voiceSession is null ? "missing" : "given")}).");
                return;
            }
            if (StreamChannel(_liveServer, _liveChannel) is not { } channel)
            {
                AppLog.Info("Discord video: your stream has no channel to connect to.");
                return;
            }
            AppLog.Info("Discord video: connecting your stream.");
            live = new VoiceConnection(_liveEndpoint, _liveServer, channel, Cache.Me.Id, _voiceSession, _liveToken, listening: true, screen: true)
            {
                EncodesH265 = _liveCapture?.HevcSupported == true,
            };
            _live = live;
            (title, sound) = (_liveTitle, _liveSound);
        }
        // The capture of the moment: it is replaced when the server changes the stream's codec.
        live.KeyframeRequested += () => _liveCapture?.Keyframe();
        live.RateWanted += rate => _liveCapture?.SetBitrate(rate);
        live.CodecChosen += hevc => _ = SwitchCodecAsync(live, hevc);
        live.Changed += (kind, data) =>
        {
            // A stream stopped (or replaced by a new one) says "disconnected" as it closes: that one no longer speaks for Go Live.
            if (kind != "state" || !ReferenceEquals(live, _live)) return;
            var state = JsonSerializer.SerializeToElement(data).GetProperty("state").GetString();
            if (state == "connected" && _liveCapture is { } capture)
            {
                live.FollowServer(capture.Bitrate, Math.Max(capture.Bitrate, _liveMost));
                _ = live.SetVideoAsync(capture.Width, capture.Height, capture.Fps, Math.Max(capture.Bitrate, _liveMost));
                _ = live.SpeakingAsync(2);
                if (sound is { } app) live.StartSoundshare(app);
                capture.Keyframe();
                Event?.Invoke("discord.golive", new { state = "live", title });
            }
            else if (state == "disconnected") StopGoLive("Your stream ended: Discord closed it.");
        };
        live.Start();
    }

    /// <summary>
    /// A stream's own channel, which its connection identifies with and is its DAVE group: STREAM_CREATE's rtc_channel_id,
    /// else rtc_server_id - 1 (Discord's RTCConnection.trueChannelId); null when the server id isn't a number.
    /// </summary>
    internal static string? StreamChannel(string server, string? channel) =>
        !string.IsNullOrEmpty(channel) ? channel
        : ulong.TryParse(server, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? (id - 1).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>A user's Go Live stream key in the call this client is in.</summary>
    private string? StreamKeyOf(string userId)
    {
        lock (_voiceGate)
        {
            if (_voiceChannel is null) return null;
            return _voiceGuild is { } guild ? $"guild:{guild}:{_voiceChannel}:{userId}" : $"call:{_voiceChannel}:{userId}";
        }
    }

    // One watched stream: what Discord said about it, and its own connection once it has them.
    private sealed class Watch(string key)
    {
        internal readonly string Key = key;
        internal string? Server, Channel, Endpoint, Token;
        internal VoiceConnection? Connection;
        internal long AskedAt = Environment.TickCount64;
        internal bool Retried;
    }

    /// <summary>Watches someone's Go Live in the call this client is in, beside any other watched; one watched already starts over (Try Again).</summary>
    internal string? WatchStream(string userId)
    {
        if (StreamKeyOf(userId) is not { } key) return "Join the voice channel to watch.";
        StopWatching(userId);
        lock (_voiceGate) _watches[key] = new Watch(key);
        _ = SendAsync(20, new JsonObject { ["stream_key"] = key });
        AppLog.Info("Discord video: watching a stream.");
        return null;
    }

    /// <summary>Stops watching one person's stream, or every stream (null).</summary>
    internal void StopWatching(string? userId = null)
    {
        List<Watch> stopped;
        lock (_voiceGate)
        {
            stopped = [.. _watches.Values.Where(w => userId is null || UserOfKey(w.Key) == userId)];
            foreach (var watch in stopped) _watches.Remove(watch.Key);
        }
        foreach (var watch in stopped)
        {
            if (watch.Connection is { } connection) _ = connection.DisposeAsync().AsTask();
            _ = SendAsync(19, new JsonObject { ["stream_key"] = watch.Key });
            Event?.Invoke("discord.video", new { source = "stream", userId = UserOfKey(watch.Key), active = false });
        }
    }

    // Under _voiceGate: every watched stream's connection.
    private IEnumerable<VoiceConnection> WatchedConnections() => _watches.Values.Select(w => w.Connection).OfType<VoiceConnection>();

    private static string UserOfKey(string key) => key[(key.LastIndexOf(':') + 1)..];

    // Main gateway Go Live dispatches; true when handled here.
    private bool OnStreamDispatch(string type, JsonElement d)
    {
        if (type is not ("STREAM_CREATE" or "STREAM_SERVER_UPDATE" or "STREAM_DELETE" or "STREAM_UPDATE")) return false;
        var key = d.TryGetProperty("stream_key", out var k) ? k.GetString() : null;
        bool watched;
        lock (_voiceGate) watched = key is not null && _watches.ContainsKey(key);
        // Each Go Live message for a stream this client watches or sends (never its token): a stream that won't open says why.
        if (key is not null && (key == _liveKey || watched))
            AppLog.Info($"Discord video: {type} for {(key == _liveKey ? "your stream" : "the watched stream")}" + type switch
            {
                "STREAM_CREATE" => $" (server {(d.TryGetProperty("rtc_server_id", out var rs) ? "given" : "missing")}, channel {(d.TryGetProperty("rtc_channel_id", out var rc) && rc.ValueKind == JsonValueKind.String ? "given" : "none")}, paused {(d.TryGetProperty("paused", out var pz) ? pz.GetRawText() : "?")}).",
                "STREAM_SERVER_UPDATE" => $" (endpoint {(d.TryGetProperty("endpoint", out var ep) && ep.GetString() is { } host ? host.Split(':')[0] : "missing")}).",
                "STREAM_DELETE" => $" (reason {(d.TryGetProperty("reason", out var why) ? why.GetRawText() : "none")}).",
                _ => ".",
            });
        if (key is not null && key == _liveKey) { OnLiveDispatch(type, d); return true; }
        Watch? watch;
        var again = false;
        lock (_voiceGate)
        {
            if (key is null || !_watches.TryGetValue(key, out watch)) return true;
            switch (type)
            {
                case "STREAM_CREATE":
                    watch.Server = d.TryGetProperty("rtc_server_id", out var s) ? s.GetString() : null;
                    watch.Channel = d.TryGetProperty("rtc_channel_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    break;
                case "STREAM_SERVER_UPDATE":
                    watch.Endpoint = d.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                    watch.Token = d.TryGetProperty("token", out var t) ? t.GetString() : null;
                    break;
                case "STREAM_DELETE":
                    // Discord at times ends a watch the moment it's asked for (the stream between two of its states), and
                    // Try Again then works: it is asked once more by itself, a second later.
                    again = !watch.Retried && watch.Connection is null && Environment.TickCount64 - watch.AskedAt < 5000;
                    watch.Retried |= again;
                    break;
                default:
                    return true;
            }
        }
        if (type == "STREAM_DELETE")
        {
            if (again) _ = AskAgainAsync(watch);
            else StopWatching(UserOfKey(watch.Key));
            return true;
        }
        OpenStream(watch);
        return true;
    }

    private async Task AskAgainAsync(Watch watch)
    {
        AppLog.Info("Discord video: the watched stream closed as it was asked for: asking again.");
        await Task.Delay(1000).ConfigureAwait(false);
        lock (_voiceGate)
        {
            if (!_watches.TryGetValue(watch.Key, out var now) || now != watch) return;
            watch.AskedAt = Environment.TickCount64;
        }
        _ = SendAsync(20, new JsonObject { ["stream_key"] = watch.Key });
    }

    private void OpenStream(Watch watch)
    {
        VoiceConnection stream;
        string user;
        lock (_voiceGate)
        {
            if (!_watches.TryGetValue(watch.Key, out var current) || current != watch || watch.Connection is not null
                || watch.Server is null || watch.Endpoint is null || watch.Token is null || _voiceSession is null || Cache.Me is null) return;
            if (StreamChannel(watch.Server, watch.Channel) is not { } channel)
            {
                AppLog.Info("Discord video: the watched stream has no channel to connect to.");
                return;
            }
            AppLog.Info("Discord video: connecting to the watched stream.");
            user = UserOfKey(watch.Key);
            stream = new VoiceConnection(watch.Endpoint, watch.Server, channel, Cache.Me.Id, _voiceSession, watch.Token, listening: true)
            {
                Deafened = _deafened,
                // The stream's sound at its Stream Volume, or silent when muted for me.
                UserGain = StreamGain,
                FramesWanted = static () => _framesWanted,
            };
            watch.Connection = stream;
        }
        stream.Frame += (from, timestamp, key, data, orientation, hevc) => PublishFrame("stream", from, timestamp, key, data, orientation, hevc);
        stream.Changed += (kind, data) =>
        {
            // One stopped watching (StopWatching said so itself) closing late must not mark the stream watched now as ended.
            if (kind != "state") return;
            lock (_voiceGate)
                if (!_watches.TryGetValue(watch.Key, out var current) || current != watch || !ReferenceEquals(stream, watch.Connection)) return;
            var state = JsonSerializer.SerializeToElement(data).GetProperty("state").GetString();
            Event?.Invoke("discord.video", new { source = "stream", userId = user, active = state == "connected", state });
        };
        stream.Start();
    }

    private void OnLiveDispatch(string type, JsonElement d)
    {
        // Whether Discord holds your stream paused and how many watch it: the first thing to read when viewers can't see it.
        if (type is "STREAM_CREATE" or "STREAM_UPDATE")
        {
            AppLog.Info($"Discord video: {type} for your stream (paused {(d.TryGetProperty("paused", out var paused) ? paused.GetRawText() : "unset")}, " +
                $"{(d.TryGetProperty("viewer_ids", out var viewers) && viewers.ValueKind == JsonValueKind.Array ? viewers.GetArrayLength() : 0)} watching).");
            LiveViewers(d);
        }
        var ignoreStaleDelete = false;
        lock (_voiceGate)
        {
            switch (type)
            {
                case "STREAM_CREATE":
                    _liveServer = d.TryGetProperty("rtc_server_id", out var s) ? s.GetString() : null;
                    _liveChannel = d.TryGetProperty("rtc_channel_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    _expectedStreamDeletes = 0;
                    break;
                case "STREAM_SERVER_UPDATE":
                    _liveEndpoint = d.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                    _liveToken = d.TryGetProperty("token", out var t) ? t.GetString() : null;
                    break;
                case "STREAM_DELETE":
                    if (_expectedStreamDeletes > 0 && _liveServer is null)
                    {
                        _expectedStreamDeletes--;
                        ignoreStaleDelete = true;
                    }
                    break;
                default: return;
            }
        }
        if (ignoreStaleDelete) return;
        if (type == "STREAM_DELETE") { StopGoLive(); return; }
        OpenLive();
    }

    // Who watches your stream: Discord sends the whole list again each time someone starts or stops watching, so your
    // stream's tile shows their pictures and each goes when they leave. You are never one of them.
    private void LiveViewers(JsonElement d)
    {
        if (!d.TryGetProperty("viewer_ids", out var list) || list.ValueKind != JsonValueKind.Array) return;
        var me = Cache.Me?.Id;
        var ids = list.EnumerateArray()
            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null)
            .OfType<string>().Where(id => id != me).Distinct().ToArray();
        lock (_voiceGate)
        {
            if (_liveKey is null || ids.SequenceEqual(_liveViewers)) return;
            _liveViewers = ids;
        }
        Event?.Invoke("discord.golive.viewers", new { ids });
    }

    /// <summary>The camera on in the call this client is in: voice state self_video, then op 12 with its size.</summary>
    internal string? StartCamera(int width, int height, int fps)
    {
        VoiceConnection? voice;
        lock (_voiceGate)
        {
            voice = _voiceState == "connected" ? _voice : null;
            if (voice is null) return "Join a voice channel to turn on your camera.";
            _camera = true;
        }
        SendVoiceState();
        _ = voice.SetVideoAsync(width, height, fps);
        Event?.Invoke("discord.camera", new { state = "live" });
        return null;
    }

    internal void StopCamera()
    {
        VoiceConnection? voice;
        lock (_voiceGate)
        {
            if (!_camera) return;
            _camera = false;
            voice = _voice;
        }
        if (voice is not null) _ = voice.SetVideoAsync(0, 0, 0);
        SendVoiceState();
        Event?.Invoke("discord.camera", new { state = "off" });
    }

    /// <summary>One encoded camera frame (Annex B H.264), timed in milliseconds.</summary>
    internal void SendCameraFrame(byte[] frame, double milliseconds)
    {
        if (_camera) _voice?.SendVideoFrame(frame, unchecked((uint)(long)(milliseconds * 90)));
    }

    /// <summary>The call's connection reports cameras turning on and off, and their frames.</summary>
    private void WatchCallVideo(VoiceConnection connection)
    {
        connection.Frame += (from, timestamp, key, data, orientation, hevc) => PublishFrame("call", from, timestamp, key, data, orientation, hevc);
        connection.KeyframeRequested += () => Event?.Invoke("discord.video.keyframe", new { source = "camera" });
    }

    // Whether the page shows a camera or a watched stream right now (on screen, with somewhere to draw it): only then
    // do their frames cross to it. Your own stream's tile asks for its frames separately (WatchOwnStream). The page's, not
    // a session's: it can say so before Discord has signed in (a restarted Exo putting a watched stream back) or across a
    // new session, and it was refused then and never said again, so the stream played its sound with no picture.
    private static volatile bool _framesWanted;

    /// <summary>The page started or stopped showing call video; starting again asks every sender for a keyframe to begin from.</summary>
    internal static void WantFrames(bool on)
    {
        var was = _framesWanted;
        _framesWanted = on;
        // Said again while already on (a video newly showing a feed: the mini player taking the stage's stream, a watch
        // begun again): its picture starts at a keyframe, so every sender is asked for one rather than leaving it frozen.
        if (on && was) AskKeyframes();
        else if (on) Current?.RequestKeyframes();
    }

    private static long _keyframesAskedAt;

    /// <summary>A feed on the page is waiting for a keyframe (a watch begun again, the window back, a decoder reset):
    /// every sender is asked for one now, at most twice a second, rather than the feed staying frozen until the sender's
    /// own next one (20 to 35 s).</summary>
    internal static void AskKeyframes()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _keyframesAskedAt) < 500) return;
        Interlocked.Exchange(ref _keyframesAskedAt, now);
        if (_framesWanted) Current?.RequestKeyframes();
    }

    private void RequestKeyframes()
    {
        _voice?.RequestKeyframes();
        List<VoiceConnection> watched;
        lock (_voiceGate) watched = [.. WatchedConnections()];
        foreach (var stream in watched) stream.RequestKeyframes();
    }

    // rotation: the sender's quarter turns clockwise (0-3) to show it upright; flip: mirrored first (urn:3gpp:video-orientation).
    private int _framesHeldBack;

    private void PublishFrame(string source, string userId, uint timestamp, bool keyframe, byte[] data, int orientation = 0, bool hevc = false)
    {
        if (source != "self" && !_framesWanted)
        {
            // The page hasn't asked for call video: said once in a while, so a stream that never shows says so.
            if (Interlocked.Increment(ref _framesHeldBack) % 600 == 1) AppLog.Info("Discord video: frames held back (the page hasn't asked for call video).");
            return;
        }
        VideoFrame?.Invoke(new { @event = "discord.video.frame", source, userId, timestamp, keyframe, rotation = orientation & 3, flip = (orientation & 4) != 0, codec = hevc ? "h265" : "h264" }, data);
    }
}
