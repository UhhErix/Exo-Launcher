using System.Text.Json;
using ExoLauncher.Helpers;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ExoLauncher.Services;

/// <summary>
/// The mini player's source of truth: the selected music provider's own page
/// (<see cref="MusicNowPlaying"/>). Publishes coalesced snapshots to the shell
/// and sends transport to the same page. Windows' media sessions are read only
/// for <see cref="OtherMusicAsync"/>: what another app plays, for your call.
/// </summary>
/// <summary>A transport answer; Refused means the page answered false (not that it could not be reached).</summary>
internal sealed record MusicControlReply(bool Ok, string? Message, bool Refused = false);

internal sealed class MediaSessionService : IDisposable
{
    /// <param name="Source">"page", "session" or "none": where controls go.</param>
    /// <param name="Rate">How fast <paramref name="PositionMs"/> advances; 0 while paused, buffering or in an ad.</param>
    /// <param name="CapturedAt"><see cref="Environment.TickCount64"/> when the position was true.</param>
    internal sealed record Snapshot(
        string Provider, string Source, string Status, string Title, string Artist,
        string? ArtworkDataUrl, long? PositionMs, long? DurationMs, double Rate, long CapturedAt,
        bool CanPlay, bool CanPause, bool CanPrev, bool CanNext, bool CanSeek)
    {
        /// Publish order, so the shell can drop an answer older than an event.
        internal long Seq { get; init; }
        /// The page's own id for the song (Spotify's URI, YouTube Music's video id, Apple Music's id); null from the media session.
        internal string? TrackId { get; init; }
        internal bool HasTrack => Status != "none" && Title.Length > 0;
    }

    internal event Action<Snapshot>? PlaybackChanged;

    private static readonly TimeSpan PageCoalesce = TimeSpan.FromMilliseconds(30);
    // A track that disappears is kept this long before the mini player hides:
    // a page reloading must not make it flap.
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(4);
    // A page that stopped answering (and is not frozen on purpose) stops
    // being believed after this long.
    private const long PageStaleMs = 10_000;
    private const int MaxArtworkDataUrlBytes = 768 * 1024;
    // Covers kept as data URLs (up to 1.5 MB of text each): the shown song's, and a few it may go back to.
    private const int KeptCovers = 4;
    private static readonly HttpClient ArtworkHttp = CreateArtworkHttp();

    private static HttpClient CreateArtworkHttp()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(10));
        http.MaxResponseContentBufferSize = 4 << 20;
        return http;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _pageArtwork = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pageArtworkPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _spotifyCovers = new(StringComparer.Ordinal);
    // A cover that could not be fetched, and when: asked again after a minute, never on every update.
    private readonly Dictionary<string, long> _pageArtworkFailed = new(StringComparer.Ordinal);
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _managerTask;
    private MusicNowPlaying? _page;
    private string? _provider;
    private Snapshot? _last;
    private long _seq;
    private long _hideAt;
    private long _generation;
    private Timer? _publishTimer;
    private Timer? _hideTimer;
    private bool _publishing;
    private bool _publishAgain;
    private bool _disposed;

    /// The music provider's latest snapshot: what the mini player shows and music's own upkeep reads.
    internal Snapshot? Current { get { lock (_gate) return _last; } }

    /// A provider switch clears the old song at once (no hide delay).
    internal void SetProvider(string? provider)
    {
        lock (_gate)
        {
            if (_provider == provider) return;
            _provider = provider;
            _generation++;
            _hideAt = 0;
            _last = null;
        }
        SchedulePublish(PageCoalesce);
    }

    /// The provider's own page reader, or null when it closed. The owner disposes it.
    internal void SetPage(MusicNowPlaying? page)
    {
        MusicNowPlaying? previous;
        lock (_gate)
        {
            previous = _page;
            if (ReferenceEquals(previous, page)) return;
            _page = page;
        }
        if (previous is not null) previous.Changed -= OnPage;
        if (page is not null) page.Changed += OnPage;
        SchedulePublish(PageCoalesce);
    }

    private void OnPage() => SchedulePublish(PageCoalesce);

    private Task<GlobalSystemMediaTransportControlsSessionManager?> EnsureManagerAsync()
    {
        lock (_gate)
            return _managerTask ??= CreateManagerAsync();
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> CreateManagerAsync()
    {
        try
        {
            return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            AppLog.Warn("Media session manager could not start: " + error.Message);
            return null;
        }
    }

    /// <summary>
    /// Music another app plays on this PC, for the song your call's Exo apps see when Exo's Music room plays none: the
    /// Spotify app, Apple Music or iTunes, or a browser tab Windows' media controls call music (YouTube Music, mostly),
    /// named by the service the call relay knows it as (spotify, apple, youtube). Never Exo itself. Null while none plays.
    /// </summary>
    internal async Task<Snapshot?> OtherMusicAsync()
    {
        var manager = await EnsureManagerAsync().ConfigureAwait(false);
        if (manager is null) return null;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = manager.GetSessions(); } catch { return null; }
        foreach (var session in sessions)
        {
            var id = session.SourceAppUserModelId;
            if (string.IsNullOrEmpty(id) || id.Contains("ExoLauncher", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var info = session.GetPlaybackInfo();
                if (info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                var service = OtherMusicService(id) ?? (IsBrowser(id) && info.PlaybackType == MediaPlaybackType.Music ? "youtube" : null);
                if (service is null) continue;
                var properties = await session.TryGetMediaPropertiesAsync();
                if (properties is null || string.IsNullOrWhiteSpace(properties.Title)) continue;
                var timeline = session.GetTimelineProperties();
                var length = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
                return new Snapshot(service, "session", "playing", properties.Title, properties.Artist ?? "", null,
                    (long)timeline.Position.TotalMilliseconds, length > 0 ? length : null, 1, Environment.TickCount64, false, false, false, false, false);
            }
            catch { }
        }
        return null;
    }

    /// <summary>A music app's service, by its media session's app id: the Spotify app, Apple Music or iTunes.</summary>
    internal static string? OtherMusicService(string sourceAppId) =>
        sourceAppId.Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase) ||
        sourceAppId.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase) ? "spotify"
        : sourceAppId.StartsWith("AppleInc.AppleMusicWin_", StringComparison.OrdinalIgnoreCase) || sourceAppId.StartsWith("AppleInc.iTunes_", StringComparison.OrdinalIgnoreCase) ||
          sourceAppId.Equals("iTunes.exe", StringComparison.OrdinalIgnoreCase) ? "apple"
        : null;

    /// <summary>A browser's media session (Chrome, Edge, Firefox, Brave, Opera, Vivaldi).</summary>
    internal static bool IsBrowser(string sourceAppId)
    {
        string[] browsers = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "Chrome", "MSEdge"];
        var name = sourceAppId.Split('!')[0].Split('_')[0];
        return browsers.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private void SchedulePublish(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_publishing) { _publishAgain = true; return; }
            _publishTimer ??= new Timer(_ => Publish(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _publishTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Publish()
    {
        MusicNowPlaying? page;
        string? provider;
        long generation;
        lock (_gate)
        {
            if (_disposed || _publishing) return;
            _publishing = true;
            _publishAgain = false;
            page = _page;
            provider = _provider;
            generation = _generation;
        }
        Snapshot? publish = null;
        try
        {
            var next = provider is null ? null : Compose(provider, page);
            lock (_gate)
            {
                if (next is null || _disposed || _generation != generation || _provider != provider) return;
                var now = Environment.TickCount64;
                if (!next.HasTrack && _last is { HasTrack: true })
                {
                    if (_hideAt == 0)
                    {
                        _hideAt = now + (long)HideDelay.TotalMilliseconds;
                        _hideTimer ??= new Timer(_ => SchedulePublish(TimeSpan.Zero), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                        _hideTimer.Change(HideDelay, Timeout.InfiniteTimeSpan);
                    }
                    if (now < _hideAt) return;
                }
                if (next.HasTrack || _last is not { HasTrack: true }) _hideAt = 0;
                if (_last is { } last && Same(last, next)) return;
                _last = publish = next with { Seq = ++_seq };
            }
        }
        catch { }
        finally
        {
            lock (_gate)
            {
                _publishing = false;
                if (_publishAgain)
                {
                    _publishAgain = false;
                    _publishTimer?.Change(PageCoalesce, Timeout.InfiniteTimeSpan);
                }
            }
        }
        if (publish is not null && PlaybackChanged is { } changed)
            foreach (var handler in changed.GetInvocationList())
                try { ((Action<Snapshot>)handler)(publish); } catch { }
    }

    /// Equal for the shell: same song, state and controls, and a position
    /// within 0.4 s of where the shown one has advanced to.
    private static bool Same(Snapshot a, Snapshot b)
    {
        if (a.Provider != b.Provider || a.Source != b.Source || a.Status != b.Status || a.Title != b.Title ||
            a.Artist != b.Artist ||
            !string.Equals(a.ArtworkDataUrl, b.ArtworkDataUrl, StringComparison.Ordinal) ||
            a.DurationMs != b.DurationMs || Math.Abs(a.Rate - b.Rate) > 0.001 || a.CanPlay != b.CanPlay ||
            a.CanPause != b.CanPause || a.CanPrev != b.CanPrev || a.CanNext != b.CanNext || a.CanSeek != b.CanSeek)
            return false;
        if (a.PositionMs is not { } from || b.PositionMs is not { } to) return a.PositionMs == b.PositionMs;
        var expected = from + (b.CapturedAt - a.CapturedAt) * a.Rate;
        return Math.Abs(expected - to) <= 400;
    }

    private Snapshot Compose(string provider, MusicNowPlaying? page)
    {
        if (page is not null && page.Service == provider)
        {
            var state = page.Current;
            var frozen = page.Frozen;
            var answering = state.FailingSince == 0 || frozen || Environment.TickCount64 - state.FailingSince < PageStaleMs;
            if (answering && state.Track is { } track) return FromPage(provider, track, frozen);
        }
        return None(provider);
    }

    private static Snapshot None(string provider) =>
        new(provider, "none", "none", "", "", null, null, null, 0, Environment.TickCount64,
            CanPlay: false, CanPause: false, CanPrev: false, CanNext: false, CanSeek: false);

    /// A frozen provider only froze after it fell silent: its song is paused.
    private Snapshot FromPage(string provider, MusicNowPlaying.Track track, bool frozen)
    {
        var playing = track.Playing && !frozen;
        return new Snapshot(provider, "page", playing ? "playing" : "paused", track.Title, track.Artist,
            PageArtwork(track.ArtworkUrl ?? (provider == "spotify" ? SpotifyCoverUrl(track.Id) : null)), track.PositionMs, track.DurationMs, playing ? track.Rate : 0, track.CapturedAt,
            CanPlay: true, CanPause: true, CanPrev: track.CanPrev, CanNext: track.CanNext, CanSeek: track.CanSeek)
        { TrackId = track.Id };
    }

    private static readonly string[] ArtworkHosts = ["googleusercontent.com", "ggpht.com", "ytimg.com", "scdn.co", "spotifycdn.com", "mzstatic.com"];

    /// The page's artwork URL, fetched once and re-encoded as a bounded data
    /// URL. Keyed by URL, which arrives with its title, so a track change can
    /// never show the previous cover; until it is ready the snapshot has none.
    private string? PageArtwork(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !ArtworkHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            return null;
        lock (_gate)
        {
            if (_pageArtwork.TryGetValue(url, out var cached)) return cached;
            if (_pageArtworkFailed.TryGetValue(url, out var failed) && Environment.TickCount64 - failed < 60_000) return null;
            if (!_pageArtworkPending.Add(url)) return null;
        }
        _ = FetchArtworkAsync(url, uri);
        return null;
    }

    /// <summary>
    /// A Spotify song the player names without a cover (restored paused, then suspended while idle, so it
    /// never fills in): Spotify's public oEmbed for the song names it, asked once per song, and the
    /// 640 px size of the same picture is used.
    /// </summary>
    private string? SpotifyCoverUrl(string id)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] != "spotify" || parts[1] is not ("track" or "episode")) return null;
        lock (_gate)
        {
            if (_spotifyCovers.TryGetValue(id, out var known)) return known;
            // A failed ask (offline, Spotify busy) is asked again after a minute, like a page cover.
            if (_pageArtworkFailed.TryGetValue(id, out var failed) && Environment.TickCount64 - failed < 60_000) return null;
            _spotifyCovers[id] = null;
        }
        _ = FetchSpotifyCoverAsync(id, parts[1], parts[2]);
        return null;
    }

    private async Task FetchSpotifyCoverAsync(string id, string kind, string key)
    {
        string? url = null;
        try
        {
            var page = Uri.EscapeDataString($"https://open.spotify.com/{kind}/{key}");
            using var doc = JsonDocument.Parse(await ArtworkHttp.GetStringAsync($"https://open.spotify.com/oembed?url={page}").ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("thumbnail_url", out var thumb) && thumb.GetString() is { Length: > 0 } small)
                url = small.Replace("ab67616d00001e02", "ab67616d0000b273", StringComparison.Ordinal);
        }
        catch { }
        lock (_gate)
        {
            if (url is null)
            {
                _spotifyCovers.Remove(id);
                FailedLocked(id);
                return;
            }
            _pageArtworkFailed.Remove(id);
            if (_spotifyCovers.Count >= 64) _spotifyCovers.Clear();
            _spotifyCovers[id] = url;
        }
        if (url is not null) SchedulePublish(PageCoalesce);
    }

    private async Task FetchArtworkAsync(string url, Uri uri)
    {
        string? artwork = null;
        try
        {
            artwork = await ArtworkDataUrlAsync(await ArtworkHttp.GetByteArrayAsync(uri).ConfigureAwait(false));
        }
        catch { }
        lock (_gate)
        {
            _pageArtworkPending.Remove(url);
            if (artwork is null) { FailedLocked(url); return; }
            _pageArtworkFailed.Remove(url);
            if (_pageArtwork.Count >= KeptCovers) _pageArtwork.Clear();
            _pageArtwork[url] = artwork;
        }
        if (artwork is not null) SchedulePublish(PageCoalesce);
    }

    /// A cover that didn't come, asked again after a minute; the list keeps only the last minute's.
    private void FailedLocked(string key)
    {
        var now = Environment.TickCount64;
        if (_pageArtworkFailed.Count >= 32)
            foreach (var (stale, at) in _pageArtworkFailed.ToArray())
                if (now - at >= 60_000) _pageArtworkFailed.Remove(stale);
        _pageArtworkFailed[key] = now;
    }

    /// Transport goes to the page of the song the shell shows. Must be called
    /// on the UI thread: YouTube Music's page is reached from there.
    internal async Task<MusicControlReply> ControlAsync(string? action, double? positionMs)
    {
        Snapshot? shown;
        MusicNowPlaying? page;
        lock (_gate)
        {
            shown = _last;
            page = _page;
        }
        if (action is not ("play" or "pause" or "toggle" or "next" or "prev" or "previous" or "seek"))
            return new MusicControlReply(false, "This control is unavailable.");
        if (action == "seek" && (positionMs is not { } target || !double.IsFinite(target) || target < 0))
            return new MusicControlReply(false, "This control is unavailable.");
        if (shown is { Source: "page" } && page is not null && page.Service == shown.Provider)
        {
            var done = await page.ControlAsync(action == "prev" ? "previous" : action, action == "seek" ? positionMs : null);
            return done switch
            {
                true => new MusicControlReply(true, null),
                false => new MusicControlReply(false, "The player did not accept the control.", Refused: true),
                null => new MusicControlReply(false, "The player could not respond."),
            };
        }
        return new MusicControlReply(false, "Nothing is playing.");
    }

    /// A downloaded cover: a JPEG or PNG within the budget goes out exactly as it came (nothing to
    /// decode); anything else is decoded and re-encoded below.
    private static async Task<string?> ArtworkDataUrlAsync(byte[] raw)
    {
        var mime = raw is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg" : raw is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : null;
        if (mime is not null && raw.Length < MaxArtworkDataUrlBytes / 4 * 3) return "data:" + mime + ";base64," + Convert.ToBase64String(raw);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(raw);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return await ArtworkDataUrlAsync(stream);
    }

    /// Artwork as a data URL at full quality: the service's own JPEG or PNG exactly as it came when
    /// it fits the budget (never compressed a second time), else re-encoded at up to 640 px, finely.
    private static async Task<string?> ArtworkDataUrlAsync(IRandomAccessStream stream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var codec = decoder.DecoderInformation?.CodecId;
            var mime = codec == BitmapDecoder.PngDecoderId ? "image/png" : codec == BitmapDecoder.JpegDecoderId ? "image/jpeg" : null;
            if (mime is not null && stream.Size is > 0 and < MaxArtworkDataUrlBytes / 4 * 3)
            {
                using var original = new DataReader(stream.GetInputStreamAt(0));
                await original.LoadAsync((uint)stream.Size);
                var raw = new byte[stream.Size];
                original.ReadBytes(raw);
                return "data:" + mime + ";base64," + Convert.ToBase64String(raw);
            }
            var largest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            foreach (var size in new[] { 640u, 480u, 320u })
            {
                var ratio = Math.Min(1.0, size / (double)largest);
                foreach (var quality in new[] { 0.95f, 0.85f })
                {
                    using var output = new InMemoryRandomAccessStream();
                    var options = new BitmapPropertySet
                    {
                        { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) },
                    };
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * ratio)),
                        ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * ratio)),
                    };
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                        ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                    using var reader = new DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    var bytes = new byte[output.Size];
                    reader.ReadBytes(bytes);
                    var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
                    if (dataUrl.Length <= MaxArtworkDataUrlBytes) return dataUrl;
                }
            }
            return null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        MusicNowPlaying? page;
        lock (_gate)
        {
            _disposed = true;
            page = _page;
            _page = null;
        }
        if (page is not null) page.Changed -= OnPage;
        try { _publishTimer?.Dispose(); } catch { }
        try { _hideTimer?.Dispose(); } catch { }
        PlaybackChanged = null;
    }
}
