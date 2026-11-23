using Windows.Media.Control;

namespace ExoLauncher.Services;

internal sealed class HeadlessMusicService : IDisposable
{
    private const int PushDebounceMs = 150;
    private readonly Lock _gate = new();
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _managerTask;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _hookedSession;
    private Action<string, object?>? _publishEvent;
    private Func<bool>? _suppressed;
    private Timer? _debounceTimer;
    private int _pushRunning;
    private int _pushPending;
    private bool _lastHadPlayback;
    private bool _attached;
    private double _lastVolume = 0.75;
    private bool _lastMuted;

    internal void Attach(Action<string, object?> publishEvent, Func<bool> suppressed)
    {
        lock (_gate)
        {
            _publishEvent = publishEvent;
            _suppressed = suppressed;
            if (_attached) return;
            _attached = true;
            _debounceTimer ??= new Timer(_ => _ = FlushPushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }
        _ = WireGsmtcAsync();
    }

    internal void Detach()
    {
        GlobalSystemMediaTransportControlsSessionManager? mgr;
        GlobalSystemMediaTransportControlsSession? session;
        Timer? timer;
        lock (_gate)
        {
            _attached = false;
            _publishEvent = null;
            _suppressed = null;
            mgr = _manager;
            _manager = null;
            session = _hookedSession;
            _hookedSession = null;
            timer = _debounceTimer;
            _debounceTimer = null;
        }
        if (session is not null) UnhookSession(session);
        if (mgr is not null)
        {
            try { mgr.CurrentSessionChanged -= OnCurrentSessionChanged; } catch { }
        }
        try { timer?.Dispose(); } catch { }
    }

    private async Task WireGsmtcAsync()
    {
        var mgr = await GetManagerAsync().ConfigureAwait(false);
        if (mgr is null) return;
        GlobalSystemMediaTransportControlsSession? initial;
        lock (_gate)
        {
            if (!_attached) return;
            if (!ReferenceEquals(_manager, mgr))
            {
                _manager = mgr;
                try { mgr.CurrentSessionChanged += OnCurrentSessionChanged; } catch { }
            }
            initial = SelectSession(mgr);
            ReplaceHookedSessionLocked(initial);
        }
        SchedulePush();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        lock (_gate)
        {
            if (!_attached) return;
            ReplaceHookedSessionLocked(SelectSession(sender));
        }
        SchedulePush();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => SchedulePush();

    private void ReplaceHookedSessionLocked(GlobalSystemMediaTransportControlsSession? next)
    {
        if (ReferenceEquals(_hookedSession, next)) return;
        if (_hookedSession is { } prev) UnhookSession(prev);
        _hookedSession = next;
        if (next is not null)
        {
            try
            {
                next.MediaPropertiesChanged += OnSessionChanged;
                next.PlaybackInfoChanged += OnSessionChanged;
                next.TimelinePropertiesChanged += OnSessionChanged;
            }
            catch { }
        }
    }

    private void UnhookSession(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= OnSessionChanged;
            session.PlaybackInfoChanged -= OnSessionChanged;
            session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        catch { }
    }

    private void SchedulePush()
    {
        lock (_gate)
        {
            if (!_attached || _debounceTimer is null) return;
            try { _debounceTimer.Change(PushDebounceMs, Timeout.Infinite); } catch { }
        }
    }

    private async Task FlushPushAsync()
    {
        Volatile.Write(ref _pushPending, 1);
        if (Interlocked.Exchange(ref _pushRunning, 1) != 0) return;
        try
        {
            while (Interlocked.Exchange(ref _pushPending, 0) != 0)
            {
                Action<string, object?>? publish;
                Func<bool>? suppressed;
                lock (_gate)
                {
                    if (!_attached) return;
                    publish = _publishEvent;
                    suppressed = _suppressed;
                }
                if (publish is null || suppressed?.Invoke() == true) continue;
                var dto = await BuildPlaybackDtoAsync().ConfigureAwait(false);
                var hasPlayback = dto is not null;
                if (!ShouldPublishPlaybackTransition(hasPlayback, ref _lastHadPlayback)) continue;
                try { publish("music.playback", dto); } catch { }
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _pushRunning, 0);
            if (Volatile.Read(ref _pushPending) != 0)
                _ = FlushPushAsync();
        }
    }

    internal static bool ShouldPublishPlaybackTransition(bool hasPlayback, ref bool lastHadPlayback)
    {
        if (!hasPlayback && !lastHadPlayback) return false;
        lastHadPlayback = hasPlayback;
        return true;
    }

    private Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync() =>
        _managerTask ??= LoadManagerAsync();

    private static async Task<GlobalSystemMediaTransportControlsSessionManager?> LoadManagerAsync()
    {
        try
        {
            return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            return null;
        }
    }

    private static GlobalSystemMediaTransportControlsSession? SelectSession(GlobalSystemMediaTransportControlsSessionManager mgr)
    {
        var current = mgr.GetCurrentSession();
        if (current is not null) return current;
        var sessions = mgr.GetSessions();
        return sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? sessions.FirstOrDefault();
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> ActiveSessionAsync()
    {
        var mgr = await GetManagerAsync().ConfigureAwait(false);
        return mgr is null ? null : SelectSession(mgr);
    }

    private async Task<object?> BuildPlaybackDtoAsync()
    {
        try
        {
            var session = await ActiveSessionAsync().ConfigureAwait(false);
            if (session is null) return null;
            var media = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var status = info?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                _ => "stopped",
            };
            var title = media?.Title?.Trim() ?? "";
            if (title.Length == 0) return null;
            var controls = info?.Controls;
            var durationMs = timeline?.EndTime.TotalMilliseconds is > 0 ? (long)timeline.EndTime.TotalMilliseconds : (long?)null;
            var posMs = timeline?.Position.TotalMilliseconds is >= 0 ? (long)timeline.Position.TotalMilliseconds : (long?)null;
            return BuildPlaybackDto(
                DetectProvider(session.SourceAppUserModelId),
                status,
                title,
                media?.Artist?.Trim() ?? "",
                media?.AlbumTitle?.Trim() ?? "",
                posMs,
                durationMs,
                controls?.IsPlayEnabled == true,
                controls?.IsPauseEnabled == true,
                controls?.IsPreviousEnabled == true,
                controls?.IsNextEnabled == true,
                controls?.IsPlaybackPositionEnabled == true);
        }
        catch
        {
            return null;
        }
    }

    internal static object BuildPlaybackDto(
        string provider,
        string status,
        string title,
        string artist,
        string album,
        long? positionMs,
        long? durationMs,
        bool canPlay,
        bool canPause,
        bool canPrev,
        bool canNext,
        bool canSeek) =>
        new
        {
            provider,
            source = "session",
            status,
            title,
            artist,
            album,
            positionMs,
            durationMs,
            canPlay,
            canPause,
            canPrev,
            canNext,
            canSeek,
        };

    internal static object BuildPlaybackResponse(object? playback) =>
        new { ok = true, playback };

    internal async Task<object> PlaybackAsync()
    {
        var playback = await BuildPlaybackDtoAsync().ConfigureAwait(false);
        return BuildPlaybackResponse(playback);
    }

    internal async Task<object> ControlAsync(string? action, double? positionMs)
    {
        try
        {
            var session = await ActiveSessionAsync().ConfigureAwait(false);
            if (session is null)
                return new { ok = false, message = "No active music session found on this PC." };
            var ok = action switch
            {
                "play" => await session.TryPlayAsync(),
                "pause" => await session.TryPauseAsync(),
                "toggle" => await session.TryTogglePlayPauseAsync(),
                "next" => await session.TrySkipNextAsync(),
                "previous" or "prev" => await session.TrySkipPreviousAsync(),
                "seek" when positionMs is >= 0 => await session.TryChangePlaybackPositionAsync((long)(positionMs.Value * 10_000)),
                _ => false,
            };
            if (ok) SchedulePush();
            return new { ok, message = ok ? null : "The active media player declined that control." };
        }
        catch
        {
            return new { ok = false, message = "Could not control active music session." };
        }
    }

    internal Task<object> VolumeAsync(double? level, bool? muted)
    {
        if (level is { } l && double.IsFinite(l)) _lastVolume = Math.Clamp(l, 0.0, 1.0);
        if (muted is { } m) _lastMuted = m;
        return Task.FromResult<object>(new
        {
            ok = true,
            level = _lastVolume,
            muted = _lastMuted || _lastVolume <= 0.0,
            source = "session",
        });
    }

    internal static string DetectProvider(string? appId)
    {
        var lower = (appId ?? "").ToLowerInvariant();
        if (lower.Contains("spotify")) return "spotify";
        if (lower.Contains("apple")) return "apple";
        if (lower.Contains("youtube") || lower.Contains("ytmusic")) return "youtube";
        return "system";
    }

    public void Dispose() => Detach();
}
