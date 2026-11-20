using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

/// <summary>
/// The Music room's engine. Spotify, YouTube Music and Apple Music are their own web players
/// (open.spotify.com, music.youtube.com, music.apple.com), hidden on the embedded WebView2 profile. The
/// mini player's song, clock and transport come from the page (<see cref="MusicNowPlaying"/>,
/// through <see cref="MediaSessionService"/>), and its volume is the page's own (<see cref="MusicAppVolume"/>).
/// Exo never starts a music app of the service's own. A running Spotify app is closed whenever Exo starts Spotify's player (<see cref="SpotifyApp"/>); the other services have none.
/// </summary>
public sealed partial class MainWindow
{
    private WebView2? _musicWeb;
    private Task? _musicWebInit;
    private TaskCompletionSource? _musicWebLoad;
    private string? _musicWebService;
    private bool _musicWebClosed;
    private bool _musicWebReady;
    private bool _musicWebFailed;
    // Why the hidden page last didn't load (WebPageGuard), until the room has said so once.
    private (string Service, string Message)? _musicWebCause;
    private int _musicWebGeneration;
    private readonly Dictionary<WebView2, Window> _musicPopouts = new();
    private MediaSessionService? _mediaSessions;
    private MusicNowPlaying? _musicNowPlaying;
    private bool _musicWebAudio;

    private MediaSessionService MediaSessions => _mediaSessions ??= CreateMediaSessions();

    // While you're in a call and Exo's Music room plays nothing: another app's song, read every 3 s, for your call.
    private Timer? _otherMusicTimer;
    private bool _sharingOtherMusic;
    private Action? _otherMusicCallHandler;
    private Action<MediaSessionService.Snapshot>? _playbackChangedHandler;
    private int _otherMusicRunning;

    /// <summary>
    /// The song your call's Exo apps see when it comes from another player on this PC (the Spotify app, Apple Music, a
    /// browser's YouTube Music): your Music room's song wins while it plays; another app's is read while you're in a call,
    /// and goes when it stops (your Music room's own, paused or none, is said again).
    /// </summary>
    private void StartOtherMusic()
    {
        if (_otherMusicTimer is not null) return;
        _otherMusicTimer = new Timer(_ => _ = ShareOtherMusicAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _otherMusicCallHandler = () => ReevaluateOtherMusicTimer();
        Services.DiscordClient.DiscordSession.CallStateChanged += _otherMusicCallHandler;
        ReevaluateOtherMusicTimer();
    }

    private void ReevaluateOtherMusicTimer()
    {
        var timer = _otherMusicTimer;
        if (timer is null || _musicWebClosed || _closingWindow) return;
        var shouldPoll = Services.DiscordClient.DiscordSession.Current is { InCall: true }
            && _mediaSessions?.Current is not { Status: "playing" };
        if (shouldPoll)
        {
            if (Interlocked.Exchange(ref _otherMusicRunning, 1) == 0)
            {
                try { timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(3)); }
                catch (ObjectDisposedException) { }
            }
        }
        else if (Interlocked.Exchange(ref _otherMusicRunning, 0) == 1 || _sharingOtherMusic)
        {
            try { timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
            if (_sharingOtherMusic) _ = ShareOtherMusicAsync();
        }
    }

    private async Task ShareOtherMusicAsync()
    {
        try
        {
            if (Services.DiscordClient.DiscordSession.Current is not { InCall: true } session)
            {
                _sharingOtherMusic = false;
                ReevaluateOtherMusicTimer();
                return;
            }
            var room = _mediaSessions?.Current;
            if (room is { Status: "playing" })
            {
                _sharingOtherMusic = false;
                ReevaluateOtherMusicTimer();
                return;
            }
            var other = await MediaSessions.OtherMusicAsync().ConfigureAwait(false);
            if (other is not null)
            {
                if (!_sharingOtherMusic) AppLog.Info($"Call song: from another app on this PC ({other.Provider}).");
                _sharingOtherMusic = true;
                session.SetCallSong(Services.DiscordClient.DiscordSession.CallSongOf(other.Provider, other.Status, other.Title, other.Artist, other.PositionMs, other.DurationMs, null), other.CapturedAt);
            }
            else if (_sharingOtherMusic)
            {
                _sharingOtherMusic = false;
                session.SetCallSong(room is null ? null : Services.DiscordClient.DiscordSession.CallSongOf(room.Provider, room.Status, room.Title, room.Artist, room.PositionMs, room.DurationMs, room.TrackId),
                    room?.CapturedAt ?? Environment.TickCount64);
            }
        }
        catch (Exception ex) { AppLog.Warn("Call song from another app: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// The provider page's now-playing reader: one at a time, replaced on a
    /// provider switch, a new YouTube Music view or a new Spotify host.
    private void SetMusicNowPlaying(MusicNowPlaying? next)
    {
        var previous = _musicNowPlaying;
        if (ReferenceEquals(previous, next)) return;
        _musicNowPlaying = next;
        MediaSessions.SetPage(next);
        previous?.Dispose();
        next?.Watch();
    }

    private MediaSessionService CreateMediaSessions()
    {
        var sessions = new MediaSessionService();
        StartOtherMusic();
        _playbackChangedHandler = snapshot =>
            DispatcherQueue.Enqueue(() =>
            {
                if (_closingWindow) return;
                ReevaluateShellSleep();
                ReevaluateOtherMusicTimer();
                if (snapshot.Provider != _musicWebService) return;
                if (snapshot.Status == "playing") ApplyPendingMusicVolume();
                // Your song for the Exo apps in your Discord call (through the call relay, end-to-end encrypted), never for Discord.
                // Another app's song stays while the Music room plays nothing.
                if (_sharingOtherMusic && snapshot.Status != "playing") return;
                Services.DiscordClient.DiscordSession.Current?.SetCallSong(Services.DiscordClient.DiscordSession.CallSongOf(snapshot.Provider,
                    snapshot.Status, snapshot.Title, snapshot.Artist, snapshot.PositionMs, snapshot.DurationMs, snapshot.TrackId), snapshot.CapturedAt);
                PublishMusicPlayback(snapshot);
            });
        sessions.PlaybackChanged += _playbackChangedHandler;
        return sessions;
    }

    internal void SelectMusicService(string service)
    {
        if (!MusicWebPolicy.IsService(service) || _musicWebService == service) return;
        // One active provider: the web surface releases its renderer.
        ResetMusicWebView();
        _musicWebService = service;
        MediaSessions.SetProvider(service);
        PublishMusicStatus(service, "loading");
    }

    /// The hidden player, loading if it isn't yet: it lies behind the room with no input, as large as the window below
    /// the Bar. The launch runs behind this call and reports as `music.status` (running on load, error on failure).
    private void OpenMusicWeb(string service)
    {
        if (_musicWebClosed || WebClientPreferences.Read().MusicService is { } selected && selected != service) return;
        double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight - 56;
        if (!EmbeddedClientFrame.ValidBounds(0, 56, width, height)) return;
        if (_musicWebService != service) SelectMusicService(service);
        if (_musicWebFailed) ResetMusicWebView();
        // A Spotify desktop app started since the last open takes the Connect device: close it each time Spotify's player opens.
        if (service == "spotify") _ = SpotifyApp.CloseRunningAsync(_lifetimeCts.Token);
        if (_musicWeb is null)
        {
            _musicWeb = new WebView2
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0),
            };
            ContentHost.Children.Add(_musicWeb);
        }
        var view = _musicWeb;
        EmbeddedClientFrame.SetBounds(view, 0, 56, width, height);
        EmbeddedClientFrame.SetInputVisible(view, false);
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        _musicWebInit ??= StartMusicWebAsync(view, service);
    }

    /// The launch behind <see cref="OpenMusicWeb"/>. Its outcome reaches the room as `music.status`.
    private async Task StartMusicWebAsync(WebView2 view, string service)
    {
        await Task.Yield();
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view) || _musicWebService != service) return;
        try { await InitializeMusicWebAsync(view, service); }
        catch (Exception) when (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) { }
        catch (Exception ex)
        {
            // A failed navigation or crashed renderer has already said why.
            var reported = _musicWebFailed;
            AppLog.Warn("Music web player initialization failed: " + ex.GetType().Name);
            ResetMusicWebView();
            _musicWebFailed = true;
            if (!reported)
                PublishMusicStatus(service, "error", MusicWebPolicy.Label(service) + " could not load. Choose Retry to reconnect.");
        }
    }

    /// Transport goes to the page of the song the mini player shows. A suspended
    /// page wakes first; the call retries while it does.
    internal async Task<object> ControlMusicAsync(string? action, double? positionMs)
    {
        if (MusicWebPolicy.IsService(_musicWebService))
        {
            // The room's player stays on screen through an idle release: its press loads the page, and goes to the song the
            // page comes back with.
            var released = !_musicWebReady;
            WakeMusicWeb();
            if (released && await MusicWebUpAsync(_musicWebService!))
                await MusicWebPolicy.WaitUntilAsync(() => MediaSessions.Current is { HasTrack: true }, () => false, MusicWebSongLimit, _lifetimeCts.Token);
        }
        var reply = await MediaSessions.ControlAsync(action, positionMs);
        if (!reply.Refused || _musicWebService != "spotify") return new { ok = reply.Ok, message = reply.Message };
        // The page refused: say which cause it was. Another active Connect device is the one the room can fix (Play here).
        var cause = MusicControlRefusal.Classify(await SpotifyDevicesAsync());
        AppLog.Info($"Music: Spotify refused {action ?? "the control"}; cause {cause.Name}.");
        if (cause.Name != "another-device") return new { ok = false, message = reply.Message, cause = cause.Name };
        var where = cause.DeviceName is { Length: > 0 } name ? name : "another device";
        return new { ok = false, message = $"Spotify is playing on {where}.", cause = cause.Name, deviceId = cause.DeviceId };
    }

    internal object MusicPlayback() => new { ok = true, playback = PlaybackDto(MediaSessions.Current) };

    /// One volume: the service's own, read and written inside its page
    /// (<see cref="MusicAppVolume"/>), so the mini player and the page's own slider
    /// are the same control, and a change made there reaches the shell as a
    /// <c>music.volume</c> event. The call always answers: before the page can,
    /// the chosen level is remembered and applied the moment it answers, so
    /// the control never greys out or snaps back because of the page's state.
    internal async Task<object> MusicVolumeAsync(double? level, bool? muted)
    {
        var change = level is not null || muted is not null;
        if (level is { } yours && muted is null) _musicVolumeYours = (_musicWebService, yours);
        if (change && MusicWebPolicy.IsService(_musicWebService)) WakeMusicWeb();
        if (MusicAppLevel() is { } app)
        {
            var target = muted == true ? 0 : level;
            var appLevel = target is { } wanted ? await app.WriteAsync(wanted) : await app.ReadAsync();
            if (appLevel is { } value && ReferenceEquals(app, _musicAppVolume))
            {
                _musicVolumeWanted = null;
                value = await FoldSessionVolumeAsync(app, value);
                _musicAppLevelLast = (_musicWebService, value);
                // The first level read is the user's until they choose another (what a Jam's change goes back to).
                if (_musicVolumeYours?.Service != _musicWebService) _musicVolumeYours = (_musicWebService, value);
                return new { ok = true, level = value, muted = value <= 0, source = "app" };
            }
        }
        // The page did not answer (not started, loading, restarting): its
        // level is unknown. The Windows session is not the page's volume (it
        // sits at 100% once folded), so it is neither shown nor written in
        // its place; a chosen level waits for the page, the slider answered
        // at once, and the shell asks again until the page answers.
        var chosen = muted == true ? 0 : level;
        if (chosen is { } later)
        {
            _musicVolumeWanted = later;
            _ = LandMusicVolumeAsync();
        }
        // A read of a suspended page shows the level it last reported, so the
        // slider stays live instead of greying out at 0 until the music plays again.
        if (chosen is null && _musicAppLevelLast is { } last && last.Service == _musicWebService)
            return new { ok = true, level = last.Level, muted = last.Level <= 0, source = "app" };
        return new { ok = true, level = chosen, muted = chosen is <= 0, pending = true, source = "app" };
    }

    private bool _musicVolumeLanding;

    /// A level the page didn't take in time (a page waking): tried every second behind the slider,
    /// the newest level winning, for half a minute. No page at all: the level lands when one answers (MusicAppLevel's Changed).
    private async Task LandMusicVolumeAsync()
    {
        if (_musicVolumeLanding) return;
        _musicVolumeLanding = true;
        try
        {
            for (var attempt = 0; attempt < 30 && _musicVolumeWanted is not null && !_closingWindow; attempt++)
            {
                await Task.Delay(1000, _lifetimeCts.Token);
                if (_musicVolumeWanted is not { } latest || MusicAppLevel() is not { } app) return;
                WakeMusicWeb();
                if (await app.WriteAsync(latest) is not { } value || !ReferenceEquals(app, _musicAppVolume)) continue;
                if (_musicVolumeWanted == latest) _musicVolumeWanted = null;
                _musicAppLevelLast = (_musicWebService, value);
            }
        }
        catch (OperationCanceledException) { }
        finally { _musicVolumeLanding = false; }
    }

    private double? _musicVolumeWanted;
    /// <summary>The user's own music level, by service: the last they set in Exo, else the last the app had outside a Jam.</summary>
    private (string? Service, double Level)? _musicVolumeYours;
    /// <summary>The level the page last reported, by service: shown while it is suspended.</summary>
    private (string? Service, double Level)? _musicAppLevelLast;
    private MusicAppVolume? _musicAppVolume;

    /// The selected provider's own volume, when it has one and its page is up.
    private MusicAppVolume? MusicAppLevel()
    {
        if (_closingWindow) return null;
        if (_musicAppVolume is { } current)
        {
            if (current.Service == _musicWebService) return current;
            _musicAppVolume = null;
            current.Dispose();
        }
        if (!MusicWebPolicy.IsService(_musicWebService) || !_musicWebReady || _musicWeb?.CoreWebView2 is not { } core) return null;
        var link = MusicAppVolume.ForWebPage(
            _musicWebService!,
            MusicWebEvaluate(core),
            paused: () => MusicWebSuspended(core),
            idle: () => WindowIsHiddenOrMinimized() && !Volatile.Read(ref _musicWebAudio));
        _musicAppVolume = link;
        link.Changed += changed => DispatcherQueue.EnqueueAsync(async () =>
        {
            if (!ReferenceEquals(_musicAppVolume, link)) return;
            // The page answers again: a level chosen while it could not lands now.
            var wanted = _musicVolumeWanted;
            _musicVolumeWanted = null;
            if (wanted is { } level) { _ = MusicVolumeAsync(level, null); return; }
            // Spotify changed the volume on its own: as a guest in someone's Jam it took the host's level, so the user's
            // own goes back (joining a friend's Jam set their music to the friend's volume). Anywhere else the app's
            // change is the user's level from now on.
            if (link.Service == "spotify" && _musicVolumeYours is { Service: "spotify" } yours && Math.Abs(changed - yours.Level) > 0.01
                && await SpotifyJamGuestAsync() && ReferenceEquals(_musicAppVolume, link))
            {
                AppLog.Info($"Music volume: Spotify's Jam set {changed:0.00}; your {yours.Level:0.00} kept.");
                _ = MusicVolumeAsync(yours.Level, null);
                return;
            }
            _musicVolumeYours = (link.Service, changed);
            PublishMusicVolume(changed);
        }, "Music volume update");
        link.Watch();
        return link;
    }

    /// Runtime.evaluate in Exo's own music web document, through its DevTools protocol: no host object or web message
    /// ever reaches the embedded profile. UI thread only. <paramref name="click"/>
    /// marks the call as the user's own click: only for what the user just
    /// pressed in Exo (play, transport), which Apple's page, like any page,
    /// only starts from a click.
    private MusicAppVolume.Evaluate MusicWebEvaluate(CoreWebView2 core, bool click = false) => async (expression, timeout, cancellation) =>
    {
        if (!ReferenceEquals(_musicWeb?.CoreWebView2, core)) return null;
        var request = System.Text.Json.JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true, userGesture = click });
        var reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request).AsTask().WaitAsync(timeout, cancellation);
        using var json = System.Text.Json.JsonDocument.Parse(reply);
        if (json.RootElement.TryGetProperty("exceptionDetails", out _) ||
            !json.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value))
            return null;
        return value.Clone();
    };

    /// A trusted press at (x, y) in the music page: DevTools mouse events, which the page takes as the user's own gesture.
    private Func<double, double, CancellationToken, Task<bool>> MusicWebPress(CoreWebView2 core) => async (x, y, cancellation) =>
    {
        if (!ReferenceEquals(_musicWeb?.CoreWebView2, core) || MusicPress.ClickCommands(x, y) is not { } commands) return false;
        foreach (var (method, json) in commands)
            await core.CallDevToolsProtocolMethodAsync(method, json).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellation);
        return true;
    };

    private static bool MusicWebSuspended(CoreWebView2 core)
    {
        try { return core.IsSuspended; }
        catch { return true; }
    }

    private void DropMusicAppVolume()
    {
        if (_musicAppVolume is not { } link) return;
        _musicAppVolume = null;
        link.Dispose();
    }

    /// Exo used to set the Windows session volume, and left below 100% it
    /// would quietly scale the app's own slider. The first time the app's own
    /// volume answers while an audio session exists, the session goes back to
    /// 100% and its level is folded into the app's, so loudness does not jump.
    private async Task<double> FoldSessionVolumeAsync(MusicAppVolume app, double level)
    {
        if (app.SessionFolded) return level;
        var pids = MusicVolumePids();
        if (pids.Count == 0) return level;
        // Only a playing session is the app's: an idle one (another WebView in
        // Exo's environment, a session left from an earlier run) never decides
        // the fold. It is tried again when the song starts.
        if (await AppVolume.ApplyAsync(pids) is not { Active: true } session) return level;
        app.SessionFolded = true;
        if (session.Level >= 0.995 && !session.Muted) return level;
        var folded = session.Muted ? 0 : Math.Round(level * session.Level, 3);
        await AppVolume.ApplyAsync(pids, 1.0, false);
        AppLog.Info($"Music volume: {app.Service} session volume reset to 100%; its own volume {level:0.00} -> {folded:0.00}.");
        return await app.WriteAsync(folded) ?? folded;
    }

    private void PublishMusicVolume(double level)
    {
        if (_closingWindow) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "music.volume",
                data = new { level, source = "app" },
            }));
        }
        catch { }
    }

    /// Playback started: a level chosen before the session existed lands now,
    /// and the app-level link folds a session volume that only now exists.
    private void ApplyPendingMusicVolume()
    {
        if (_musicVolumeWanted is { } wanted) _ = MusicVolumeAsync(wanted, null);
        else if (_musicAppVolume is { SessionFolded: false } link) _ = FoldOncePlayingAsync(link);
    }

    /// The audio session turns active a moment after the app reports playing.
    private async Task FoldOncePlayingAsync(MusicAppVolume link)
    {
        await Task.Delay(1500);
        if (ReferenceEquals(_musicAppVolume, link) && !link.SessionFolded) await MusicVolumeAsync(null, null);
    }

    private HashSet<int> MusicVolumePids()
    {
        var pids = new HashSet<int>();
        if (_musicWeb?.CoreWebView2 is { } core)
        {
            try
            {
                foreach (var process in core.Environment.GetProcessInfos()) pids.Add(process.ProcessId);
            }
            catch { }
        }
        return pids;
    }

    /// positionAgeMs is measured on the host's monotonic clock at send time;
    /// the shell anchors it to its own clock on receipt, so wall-clock skew or
    /// a clock change never moves the time.
    private static object? PlaybackDto(MediaSessionService.Snapshot? snapshot) => snapshot is null ? null : new
    {
        seq = snapshot.Seq,
        provider = snapshot.Provider,
        status = snapshot.Status,
        title = snapshot.Title,
        artist = snapshot.Artist,
        artworkDataUrl = snapshot.ArtworkDataUrl,
        artworkUrl = snapshot.ArtworkUrl,
        positionMs = snapshot.PositionMs,
        durationMs = snapshot.DurationMs,
        positionAgeMs = Math.Max(0, Environment.TickCount64 - snapshot.CapturedAt),
        rate = snapshot.Rate,
        canPlay = snapshot.CanPlay,
        canPause = snapshot.CanPause,
        canPrev = snapshot.CanPrev,
        canNext = snapshot.CanNext,
        canSeek = snapshot.CanSeek,
    };

    private void PublishMusicPlayback(MediaSessionService.Snapshot snapshot)
    {
        if (_closingWindow) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "music.playback",
                data = PlaybackDto(snapshot),
            }));
        }
        catch { }
    }

    private CancellationTokenSource? _musicIdleCancellation;
    /// Silent and hidden past the idle wait without a suspend: kept at low memory until something wakes it.
    private bool _musicWebIdle;

    private void WakeMusicWeb()
    {
        _musicIdleCancellation?.Cancel();
        _musicIdleCancellation = null;
        _musicWebIdle = false;
        try
        {
            if (_musicWeb is not { } view || view.CoreWebView2 is not { } core) return;
            view.Visibility = Visibility.Visible;
            if (core.IsSuspended) core.Resume();
            SetWebViewMemoryTarget(core, CoreWebView2MemoryUsageTargetLevel.Normal);
            _musicNowPlaying?.Wake();
            _musicAppVolume?.Wake();
            // Always re-arm the idle-suspend countdown after waking so hovers, room switches, or paused transport
            // controls do not leave the background Music WebView rendering indefinitely when nothing plays.
            ApplyMusicMemoryTarget();
        }
        catch { /* Navigation/recreation reports its own recovery state. */ }
    }

    /// <summary>
    /// The pointer reached the mini player or the Music tab: wake a suspended page
    /// now, so the click that follows lands on a running player. Cheap when nothing sleeps.
    /// </summary>
    internal object WarmMusic()
    {
        if (MusicWebPolicy.IsService(_musicWebService)) WakeMusicWeb();
        return new { ok = true };
    }

    private void ReloadMusic()
    {
        if (_musicWebFailed) { ResetMusicWebView(); return; }
        if (_musicWeb?.CoreWebView2 is not { } core) return;
        _musicWebReady = false;
        PublishMusicStatus(_musicWebService!, "loading");
        core.Reload();
    }

    private void ResetMusicWebView()
    {
        DropMusicAppVolume();
        DropSpotifyBridge();
        SetMusicNowPlaying(null);
        CancelConnectedPermissions(_musicWeb);
        _musicIdleCancellation?.Cancel();
        _musicIdleCancellation = null;
        _musicWebIdle = false;
        _musicWebGeneration++;
        foreach (var popup in _musicPopouts.Values.ToArray())
            try { popup.Close(); } catch { }
        _musicPopouts.Clear();
        var previous = _musicWeb;
        // Invalidate callbacks before closing the old view.
        _musicWeb = null;
        _musicWebInit = null;
        _musicWebLoad?.TrySetCanceled();
        _musicWebLoad = null;
        _musicWebReady = false;
        _musicWebFailed = false;
        Volatile.Write(ref _musicWebAudio, false);
        ReevaluateShellSleep();
        _embeddedRenderers = Array.Empty<EmbeddedRendererInfo>();
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        if (previous is null) return;
        try { WebExtensions.Detach(previous.CoreWebView2); previous.Close(); } catch { }
        ContentHost.Children.Remove(previous);
    }

    private void CloseMusicWeb()
    {
        _musicWebClosed = true;
        ResetMusicWebView();
        _musicNowPlaying?.Dispose();
        _musicNowPlaying = null;
        if (_otherMusicCallHandler is not null)
        {
            Services.DiscordClient.DiscordSession.CallStateChanged -= _otherMusicCallHandler;
            _otherMusicCallHandler = null;
        }
        _otherMusicTimer?.Dispose();
        _otherMusicTimer = null;
        if (_mediaSessions is not null && _playbackChangedHandler is not null)
            _mediaSessions.PlaybackChanged -= _playbackChangedHandler;
        _playbackChangedHandler = null;
        _mediaSessions?.Dispose();
        _mediaSessions = null;
    }

    private void ApplyMusicMemoryTarget()
    {
        try
        {
            if (_musicWeb?.CoreWebView2 is not { } core) return;
            // A hidden player still needs its decoder and next-track buffers.
            // Only a confirmed idle document (suspended, or silent a minute) may release that memory.
            SetWebViewMemoryTarget(core, core.IsSuspended || _musicWebIdle ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
            SetEmbeddedActivity(core, false);
            // Paused or empty players may sleep after a final idle wait.
            if (core.IsDocumentPlayingAudio || core.IsSuspended || _musicIdleCancellation is not null) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _musicIdleCancellation = cancellation;
            _ = SuspendIdleMusicAsync(_musicWeb, cancellation);
        }
        catch { }
    }

    private async Task SuspendIdleMusicAsync(WebView2 view, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellation.Token);
            // A session the provider still reports as playing (buffering, a
            // silent stretch) is not idle even while no audio is audible.
            if (cancellation.IsCancellationRequested || !ReferenceEquals(view, _musicWeb) ||
                // A sign-in answers this page from its own window: a suspended page never hears it.
                _musicPopouts.Count > 0 || MusicSignInRequested() ||
                view.CoreWebView2 is not { } core || core.IsDocumentPlayingAudio ||
                MediaSessions.Current is { Status: "playing" } now && now.Provider == _musicWebService) return;
            // A player that has been silent and hidden for a minute is paused;
            // let the native host own idle scheduling independently. Suspend
            // is only valid on a hidden controller, so the view collapses first.
            view.Visibility = Visibility.Collapsed;
            var suspended = false;
            if (!core.IsSuspended && ReferenceEquals(view, _musicWeb))
            {
                // The controller is told the view hid on a later turn: ~200ms later while the window is up
                // (WebView2 waits that long against a flash of its background), at once while the window is
                // hidden or minimized. Suspend is refused until then, so it is retried across that window
                // rather than in a couple of turns.
                string? failure = null;
                var deadline = Environment.TickCount64 + 1600;
                while (!suspended && !core.IsSuspended && !cancellation.IsCancellationRequested &&
                       Environment.TickCount64 < deadline)
                {
                    await NextLowPriorityTurnAsync();
                    try { suspended = await core.TrySuspendAsync(); failure = null; }
                    catch (Exception error) { failure = $"{error.GetType().Name} 0x{error.HResult:X8}"; }
                    if (!suspended && !core.IsSuspended) await Task.Delay(120);
                }
                if (failure is not null) LogMusicSuspendFailure(failure);
                else if (!suspended && !core.IsSuspended) LogMusicSuspendFailure("declined");
            }
            var stillIdle = !cancellation.IsCancellationRequested && ReferenceEquals(view, _musicWeb) && !core.IsDocumentPlayingAudio;
            if (!suspended || !stillIdle)
            {
                view.Visibility = Visibility.Visible;
                if (core.IsSuspended) core.Resume();
            }
            // A page the controller won't suspend (a COMException every time in app.log) rests at low memory instead and
            // is released the same way: a refusal used to keep it loaded, renderer and all, for the rest of the session.
            _musicWebIdle = stillIdle && !core.IsSuspended;
            SetWebViewMemoryTarget(core, core.IsSuspended || _musicWebIdle ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
            if (!stillIdle) return;
            // Still asleep after a long while: close the player and its renderer.
            // Any wake (room, mini player, transport, sound) cancels this; Music loads it again.
            await Task.Delay(MusicReleaseDelay, cancellation.Token);
            if (!ReferenceEquals(view, _musicWeb) || !(core.IsSuspended || _musicWebIdle) || core.IsDocumentPlayingAudio ||
                _musicPopouts.Count > 0 || _musicSignInWindow is not null ||
                MediaSessions.Current is { Status: "playing" } playing && playing.Provider == _musicWebService) return;
            AppLog.Info(MusicWebPolicy.Label(_musicWebService ?? "") + " web player released after " + MusicReleaseDelay.TotalMinutes + " idle minutes.");
            ResetMusicWebView();
        }
        catch { if (ReferenceEquals(view, _musicWeb)) view.Visibility = Visibility.Visible; }
        finally
        {
            if (ReferenceEquals(_musicIdleCancellation, cancellation)) _musicIdleCancellation = null;
            cancellation.Dispose();
        }
    }

    /// How long an idle music player stays asleep before Exo closes its web view.
    private static readonly TimeSpan MusicReleaseDelay = TimeSpan.FromMinutes(15);

    private bool _musicSuspendFailureLogged;

    /// Resumes after the dispatcher has run everything more urgent (layout included).
    private Task NextLowPriorityTurnAsync()
    {
        var turn = new TaskCompletionSource();
        if (!DispatcherQueue.Enqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => turn.TrySetResult())) turn.TrySetResult();
        return turn.Task;
    }

    private void LogMusicSuspendFailure(string reason)
    {
        if (_musicSuspendFailureLogged) return;
        _musicSuspendFailureLogged = true;
        AppLog.Warn("Music web player could not suspend while hidden (" + reason + "); it rests at low memory until it closes.");
    }

    private void PublishMusicStatus(string service, string state, string? message = null)
    {
        if (_musicWebClosed || _musicWebService != service) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "music.status", data = new { ok = state != "error", ready = state == "running", service, state, message },
            }));
        }
        catch { }
    }

    private async Task InitializeMusicWebAsync(WebView2 view, string service)
    {
        var initTimer = Stopwatch.StartNew();
        var environment = await WebViewEnvironmentFactory.GetAsync();
        var envMs = initTimer.ElapsedMilliseconds;
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        await view.EnsureCoreWebView2Async(environment, WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
        var coreMs = initTimer.ElapsedMilliseconds;
        if (!_musicWebClosed && ReferenceEquals(_musicWeb, view))
            WebViewEnvironmentFactory.LogEmbeddedProfileOnce(view.CoreWebView2);
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        EmbeddedClientFrame.Apply(view);
        var core = view.CoreWebView2;
        HideWebViewWindows();
        // Installed extensions apply to the profile regardless of bookkeeping;
        // attach in parallel so the first navigation is never blocked by it.
        _ = WebExtensions.AttachAsync(core, MusicWebPolicy.Label(service), WinRT.Interop.WindowNative.GetWindowHandle(this));
        await ResetPermissionGrantsAsync(core.Profile);
        var permissionsMs = initTimer.ElapsedMilliseconds;
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        await SetMusicMotionPreferenceAsync(core);
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _musicWebLoad = loaded;
        var guard = ConfigureMusicCore(core, environment, view, service);
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        Volatile.Write(ref _musicWebAudio, core.IsDocumentPlayingAudio);
        core.IsDocumentPlayingAudioChanged += (_, _) => UiGuard.Run(() =>
        {
            if (!ReferenceEquals(_musicWeb, view) || _musicWebClosed) return;
            Volatile.Write(ref _musicWebAudio, core.IsDocumentPlayingAudio);
            ReevaluateShellSleep();
            // Sound started without a wake (Windows' media keys reach the page directly): the idle wait starts over.
            if (core.IsDocumentPlayingAudio)
            {
                _musicIdleCancellation?.Cancel();
                _musicIdleCancellation = null;
                _musicWebIdle = false;
            }
            ApplyMusicMemoryTarget();
        });
        core.NavigationCompleted += (_, e) => UiGuard.Run(() =>
        {
            if (!ReferenceEquals(_musicWeb, view) || _musicWebClosed) return;
            if (!e.IsSuccess)
            {
                // Why, in plain words (WebPageGuard): the room says it with Open again. None for a page that was only
                // cancelled or handed elsewhere.
                if (guard.Failure(e) is not { } message) return;
                _musicWebReady = false;
                _musicWebFailed = true;
                _musicWebCause = (service, message);
                view.Visibility = Visibility.Collapsed;
                ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                PublishMusicStatus(service, "error", message);
                loaded.TrySetException(new InvalidOperationException(message));
                return;
            }
            _musicWebReady = true;
            _musicWebBooted = false;
            _musicWebFailed = false;
            SetEmbeddedActivity(core, false, force: true);
            // The reader outlives reloads: its page helper is reinstalled in
            // each new document on the next call.
            if (_musicNowPlaying?.Service != service)
                SetMusicNowPlaying(MusicNowPlaying.ForWebPage(service, MusicWebEvaluate(core), () => MusicWebSuspended(core),
                    control: MusicWebEvaluate(core, click: true), press: MusicWebPress(core)));
            loaded.TrySetResult();
            // Rendered but transparent, behind the room.
            view.Visibility = Visibility.Visible;
            EmbeddedClientFrame.SetInputVisible(view, false);
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            PublishMusicStatus(service, "running");
            RefreshEmbeddedRendererMapAsync().ContinueWith(static task => task.Exception?.Handle(static _ => true),
                TaskContinuationOptions.OnlyOnFaulted);
        });
        core.ProcessFailed += (_, e) => UiGuard.Run(() =>
        {
            if (!ReferenceEquals(_musicWeb, view) || _musicWebClosed) return;
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
                return;
            _musicWebFailed = true;
            _musicWebReady = false;
            view.Visibility = Visibility.Collapsed;
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            PublishMusicStatus(service, "error", "The player stopped responding. Choose Retry to reconnect.");
            loaded.TrySetException(new InvalidOperationException("Music web process failed."));
            AppLog.Warn("Music web player process stopped; reload is available.");
        });
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.Script);
        if (service == "youtube")
            await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.OptimizeScript);
        var domLogged = false;
        core.DOMContentLoaded += (_, _) => UiGuard.Run(() =>
        {
            if (domLogged || _musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
            domLogged = true;
            AppLog.Info($"PERF music-init service={service} domContentLoadedMs={initTimer.ElapsedMilliseconds}");
        });
        // The provider owns its document and sign-in colors. Forcing CSS
        // color-scheme into an embedded account form can hide typed text.
        core.Navigate(MusicWebPolicy.StartUri(service));
        AppLog.Info($"PERF music-init service={service} envMs={envMs} coreMs={coreMs} permissionsMs={permissionsMs} navigateMs={initTimer.ElapsedMilliseconds}");
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), _lifetimeCts.Token);
        AppLog.Info(MusicWebPolicy.Label(service) + " official web player started on the embedded profile.");
    }

    private WebPageGuard ConfigureMusicCore(CoreWebView2 core, CoreWebView2Environment environment,
        WebView2 surface, string service, bool popup = false)
    {
        var generation = _musicWebGeneration;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.PermissionRequested += (_, args) => UiGuard.Run(() => OnConnectedPermissionRequested(args, surface, popup, service));
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        // The same cleaning store sign-in does: an embedded token in the agent makes Google and Apple refuse the page.
        var userAgent = core.Settings.UserAgent ?? "";
        var cleaned = StoreAuthWindow.CleanUserAgent(userAgent);
        if (!string.Equals(cleaned, userAgent, StringComparison.Ordinal))
            core.Settings.UserAgent = cleaned;
        // The player itself runs hidden behind Exo's room; only its windows (sign-in, a page's own) are seen.
        var guard = ConfigureConnectedChrome(core, surface, MusicWebPolicy.Label(service),
            uri => MusicWebPolicy.IsPlayerDocument(service, uri), shown: popup);
        core.NavigationStarting += (sender, e) => UiGuard.Run(() =>
        {
            // A link to another app goes on to the guard: asked in Exo's card in a window, refused in the hidden player.
            if (MusicWebPolicy.IsServiceDocument(service, e.Uri) || (popup && (e.Uri == "about:blank" || MusicWebPolicy.IsSignInHop(service, e.Uri))) || WebPageGuard.IsAppLink(e.Uri)) return;
            e.Cancel = true;
            if (popup) {
                // A popup that leaves its allowed hosts has finished its job:
                // hand the destination to the system browser and close it so
                // the user never lands on a blank window. Only a click opens the browser,
                // so a page's own redirect cannot launch one.
                if (e.IsUserInitiated && MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
                if (_musicPopouts.TryGetValue(surface, out var popupWindow)) {
                    if (MusicSignInRequested() || ReferenceEquals(_musicSignInWindow, popupWindow))
                        NoteMusicSignInFailed(service);
                    try { popupWindow.Close(); } catch { }
                }
                return;
            }
            if (e.IsUserInitiated && MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
        });
        core.NewWindowRequested += async (sender, e) => await UiGuard.RunAsync(async () =>
        {
            e.Handled = true;
            if (_musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) return;
            if (WebPageGuard.IsAppLink(e.Uri))
            {
                guard.Offer(e.Uri);
                return;
            }
            var authContinuation = MusicWebPolicy.IsServiceDocument(service, core.Source) &&
                !MusicWebPolicy.IsPlayerDocument(service, core.Source) &&
                (MusicWebPolicy.IsServiceDocument(service, e.Uri) || e.Uri == "about:blank");
            if (!e.IsUserInitiated && !authContinuation && !(MusicSignInRequested() && MusicWebPolicy.IsServiceDocument(service, e.Uri))) return;
            if (!MusicWebPolicy.IsServiceDocument(service, e.Uri) && e.Uri != "about:blank")
            {
                if (MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
                return;
            }
            var deferral = e.GetDeferral();
            var popupTimer = Stopwatch.StartNew();
            Window? child = null;
            var popupDismissed = false;
            try
            {
                var view = new WebView2 { DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0) };
                child = CreateExoWebPopup(service, view, ExoConnectedTheme.AskedSize(e.WindowFeatures));
                var window = child;
                _musicPopouts[view] = window;
                ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                window.Closed += (_, _) =>
                {
                    CancelConnectedPermissions(view);
                    popupDismissed = true;
                    _musicPopouts.Remove(view);
                    if (service == "apple" && MusicSignInRequested())
                    {
                        AppLog.Info("Music sign-in: Apple's window closed.");
                        _ = FinishAppleSignInAsync();
                    }
                    ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                    try { WebExtensions.Detach(view.CoreWebView2); view.Close(); } catch { }
                };
                // A window the page opens on its own (a sign-in's next step) takes the front only while Exo has it.
                if (e.IsUserInitiated || ForegroundWatcher.ExoInFront(WinRT.Interop.WindowNative.GetWindowHandle(this)))
                {
                    ForegroundWatcher.Asked(MusicWebPolicy.Label(service) + " opened a window of its own");
                    window.Activate();
                }
                else window.AppWindow.Show(activateWindow: false);
                await view.EnsureCoreWebView2Async(environment, WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
                var ensureMs = popupTimer.ElapsedMilliseconds;
                if (_musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                _ = WebExtensions.AttachAsync(view.CoreWebView2, MusicWebPolicy.Label(service), WinRT.Interop.WindowNative.GetWindowHandle(window));
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                await SetMusicMotionPreferenceAsync(view.CoreWebView2);
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                ConfigureMusicCore(view.CoreWebView2, environment, view, service, popup: true);
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.Script);
                void UpdatePopupActivity(bool force = false)
                {
                    var minimized = window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                        presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized;
                    SetEmbeddedActivity(view.CoreWebView2, !minimized, force);
                    SetWebViewMemoryTarget(view.CoreWebView2, minimized ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
                }
                window.AppWindow.Changed += (_, _) => { try { UpdatePopupActivity(); } catch { } };
                view.CoreWebView2.NavigationCompleted += (_, _) => UiGuard.Run(() => { try { UpdatePopupActivity(force: true); } catch { } });

                view.CoreWebView2.WindowCloseRequested += (_, _) => UiGuard.Run(() => window.Close());
                // How far a sign-in got, by host only (never a path or query: they carry tokens).
                view.CoreWebView2.NavigationStarting += (_, step) => UiGuard.Run(() =>
                {
                    if (MusicSignInRequested() && Uri.TryCreate(step.Uri, UriKind.Absolute, out var at)) AppLog.Info($"Music sign-in: {at.Host}{(step.Cancel ? " refused" : "")}");
                });
                var popupDomLogged = false;
                view.CoreWebView2.DOMContentLoaded += (_, _) => UiGuard.Run(() =>
                {
                    if (popupDomLogged) return;
                    popupDomLogged = true;
                    AppLog.Info($"PERF music-popup service={service} domContentLoadedMs={popupTimer.ElapsedMilliseconds}");
                });
                e.NewWindow = view.CoreWebView2;
                AppLog.Info($"PERF music-popup service={service} ensureMs={ensureMs} readyMs={popupTimer.ElapsedMilliseconds}");
            }
            catch
            {
                var dismissed = popupDismissed;
                try { child?.Close(); } catch { }
                if (!dismissed && generation == _musicWebGeneration)
                    PublishMusicStatus(service, "error", "The player's window could not open. Try opening it in your browser.");
            }
            finally { try { deferral.Complete(); } catch { } }
        });
        // Provider permissions and DRM prompts keep WebView2's ordinary user
        // decision flow. No auto-grants, license interception or request blocking.
        return guard;
    }

    private async Task SetMusicMotionPreferenceAsync(CoreWebView2 core)
    {
        try
        {
            // Use the provider's reduced-motion path, including shadow DOM and
            // script-driven effects. This does not enable a debugging endpoint.
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia",
                "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
        }
        catch { /* Motion support must not prevent the player from opening. */ }
    }

    private static void OpenMusicExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { AppLog.Warn("Music external link could not open."); }
    }
}
