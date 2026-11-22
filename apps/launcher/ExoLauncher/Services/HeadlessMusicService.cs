using Windows.Media.Control;

namespace ExoLauncher.Services;

internal sealed class HeadlessMusicService
{
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _managerTask;
    private double _lastVolume = 0.75;
    private bool _lastMuted;

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

    private async Task<GlobalSystemMediaTransportControlsSession?> ActiveSessionAsync()
    {
        var mgr = await GetManagerAsync().ConfigureAwait(false);
        if (mgr is null) return null;
        var current = mgr.GetCurrentSession();
        if (current is not null) return current;
        var sessions = mgr.GetSessions();
        return sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? sessions.FirstOrDefault();
    }

    internal async Task<object> PlaybackAsync()
    {
        try
        {
            var session = await ActiveSessionAsync().ConfigureAwait(false);
            if (session is null) return new { ok = true, playback = (object?)null };
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
            if (title.Length == 0) return new { ok = true, playback = (object?)null };
            var controls = info?.Controls;
            var durationMs = timeline?.EndTime.TotalMilliseconds is > 0 ? (long)timeline.EndTime.TotalMilliseconds : (long?)null;
            var posMs = timeline?.Position.TotalMilliseconds is >= 0 ? (long)timeline.Position.TotalMilliseconds : (long?)null;
            return new
            {
                ok = true,
                playback = new
                {
                    provider = DetectProvider(session.SourceAppUserModelId),
                    source = "session",
                    status,
                    title,
                    artist = media?.Artist?.Trim() ?? "",
                    album = media?.AlbumTitle?.Trim() ?? "",
                    positionMs = posMs,
                    durationMs,
                    canPlay = controls?.IsPlayEnabled == true,
                    canPause = controls?.IsPauseEnabled == true,
                    canPrev = controls?.IsPreviousEnabled == true,
                    canNext = controls?.IsNextEnabled == true,
                    canSeek = controls?.IsPlaybackPositionEnabled == true,
                },
            };
        }
        catch
        {
            return new { ok = true, playback = (object?)null };
        }
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

    private static string DetectProvider(string? appId)
    {
        var lower = (appId ?? "").ToLowerInvariant();
        if (lower.Contains("spotify")) return "spotify";
        if (lower.Contains("apple")) return "apple";
        if (lower.Contains("youtube") || lower.Contains("ytmusic")) return "youtube";
        return "system";
    }
}
