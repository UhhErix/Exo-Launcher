using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

internal sealed partial class MusicEngineService
{
    private sealed class EmbeddedActivityState
    {
        internal bool? Active;
        internal long Version;
        internal CoreWebView2MemoryUsageTargetLevel? MemoryTarget;
    }

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConditionalWeakTable<CoreWebView2, EmbeddedActivityState> _embeddedActivity = new();
    private readonly Dictionary<MusicWebHostWindow, CoreWebView2?> _musicPopouts = new();

    private DispatcherQueue? _dispatcher;
    private Action<string, object?>? _publishEvent;
    private Func<bool>? _isHostWindowHidden;
    private Action? _onStateChanged;
    private Action? _onRenderersChanged;

    private MusicWebHostWindow? _musicHost;
    private Task? _musicWebInit;
    private TaskCompletionSource? _musicWebLoad;
    private string? _musicWebService;
    private bool _musicWebClosed;
    private bool _musicWebReady;
    private bool _appleAdapterInstalled;
    private bool _musicWebFailed;
    private int _musicWebFailures;
    private DateTimeOffset _musicWebRetryAfter = DateTimeOffset.MinValue;
    private (string Service, string Message)? _musicWebCause;
    private int _musicWebGeneration;
    private MediaSessionService? _mediaSessions;
    private MusicNowPlaying? _musicNowPlaying;
    private bool _musicWebAudio;

    private Timer? _otherMusicTimer;
    private bool _sharingOtherMusic;
    private Action? _otherMusicCallHandler;
    private Action<MediaSessionService.Snapshot>? _playbackChangedHandler;
    private int _otherMusicRunning;

    private bool _musicVolumeLanding;
    private double? _musicVolumeWanted;
    private (string? Service, double Level)? _musicVolumeYours;
    private (string? Service, double Level)? _musicAppLevelLast;
    private MusicAppVolume? _musicAppVolume;

    private CancellationTokenSource? _musicIdleCancellation;
    private bool _musicWebIdle;
    private bool _musicSuspendFailureLogged;
    private bool _musicProcessesTrimmed;
    private long _musicTrimGeneration;
    private static readonly TimeSpan MusicSuspendDelay = MusicEngineDiet.SuspendDelay;
    private static readonly TimeSpan MusicReleaseDelay = MusicEngineDiet.ReleaseDelay;

    private CoreWebView2? MusicCore => _musicHost?.Core;
    private MediaSessionService MediaSessions => _mediaSessions ??= CreateMediaSessions();

    internal bool HasWebView => _musicHost is not null;
    internal bool IsActive => _musicHost is not null || _musicWebService is not null;
    internal string? ActiveService => _musicWebService;
    internal bool HasPopouts => _musicPopouts.Count > 0;
    internal bool IsPlayingAudio => Volatile.Read(ref _musicWebAudio);
    internal bool IsPlaying => Volatile.Read(ref _musicWebAudio) || _mediaSessions?.Current is { Status: "playing" };

    internal uint BrowserProcessId
    {
        get
        {
            try { return MusicCore?.BrowserProcessId ?? 0; }
            catch { return 0; }
        }
    }

    internal void Attach(DispatcherQueue dispatcher, Action<string, object?> publishEvent)
    {
        _dispatcher = dispatcher;
        _publishEvent = publishEvent;
    }

    internal void SetWindowHooks(Func<bool>? isHostWindowHidden, Action? onStateChanged, Action? onRenderersChanged)
    {
        _isHostWindowHidden = isHostWindowHidden;
        _onStateChanged = onStateChanged;
        _onRenderersChanged = onRenderersChanged;
    }

    private bool HostHiddenOrMinimized()
    {
        try { return _isHostWindowHidden?.Invoke() == true; }
        catch { return false; }
    }

    private void NotifyStateChanged()
    {
        try { _onStateChanged?.Invoke(); }
        catch { }
    }

    private void NotifyRenderersChanged()
    {
        try { _onRenderersChanged?.Invoke(); }
        catch { }
    }

    private static void EnsureSyncContext(DispatcherQueue dispatcher)
    {
        if (SynchronizationContext.Current is not DispatcherQueueSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _musicWebClosed)
            throw new InvalidOperationException("Music engine dispatcher is not attached.");
        if (dispatcher.HasThreadAccess)
        {
            EnsureSyncContext(dispatcher);
            return work();
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.EnqueueAsync(async () =>
        {
            EnsureSyncContext(dispatcher);
            try { tcs.TrySetResult(await work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, "Music engine UI work"))
        {
            tcs.TrySetException(new InvalidOperationException("Music engine dispatcher is shutting down."));
        }
        return tcs.Task;
    }

    private Task<T> OnUiAsync<T>(Func<T> work) => OnUiAsync(() => Task.FromResult(work()));

    private void PostUi(Action work)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess)
        {
            EnsureSyncContext(dispatcher);
            UiGuard.Run(work);
            return;
        }
        dispatcher.TryEnqueue(() => UiGuard.Run(() =>
        {
            EnsureSyncContext(dispatcher);
            work();
        }));
    }

    private void PostUiAsync(Func<Task> work, string reason)
    {
        _ = reason;
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(() => UiGuard.Run(() =>
        {
            EnsureSyncContext(dispatcher);
            _ = UiGuard.RunAsync(work);
        }));
    }

    internal void SelectService(string service)
    {
        if (_musicWebClosed || _dispatcher is null) return;
        PostUi(() => SelectServiceCore(service));
    }

    internal Task ResetPermissionGrantsAsync()
    {
        if (_musicWebClosed || _dispatcher is null) return Task.CompletedTask;
        return OnUiAsync(async () =>
        {
            if (MusicCore is { } music) await ResetProfilePermissionsAsync(music.Profile);
            return true;
        });
    }

    internal void ApplyMemoryTarget()
    {
        if (_musicWebClosed || _dispatcher is null) return;
        PostUi(ApplyMusicMemoryTarget);
    }

    internal Task<object> PlaybackAsync()
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = true, playback = (object?)null });
        return OnUiAsync<object>(() => new { ok = true, playback = PlaybackDto(MediaSessions.Current) });
    }

    internal Task<object> ControlAsync(string? action, double? positionMs)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, message = "Music engine is unavailable." });
        return OnUiAsync(() => ControlMusicCoreAsync(action, positionMs));
    }

    internal Task<object> VolumeAsync(double? level, bool? muted)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = true, level = (double?)null, muted = false, pending = true, source = "app" });
        return OnUiAsync(() => MusicVolumeCoreAsync(level, muted));
    }

    internal Task<object> WarmAsync()
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false });
        return OnUiAsync<object>(() =>
        {
            if (MusicWebPolicy.IsService(_musicWebService)) WakeMusicWeb();
            return new { ok = true };
        });
    }

    internal void Close()
    {
        if (_musicWebClosed) return;
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            _musicWebClosed = true;
            try { _lifetimeCts.Cancel(); } catch { }
            return;
        }
        if (dispatcher.HasThreadAccess)
        {
            CloseCore();
            return;
        }
        var done = new ManualResetEventSlim(false);
        if (dispatcher.TryEnqueue(() => UiGuard.Run(() =>
        {
            try { CloseCore(); }
            finally { done.Set(); }
        })))
        {
            done.Wait(TimeSpan.FromSeconds(2));
        }
        else
        {
            _musicWebClosed = true;
            try { _lifetimeCts.Cancel(); } catch { }
        }
        done.Dispose();
    }

    private void StartOtherMusic()
    {
        if (_otherMusicTimer is not null) return;
        _otherMusicTimer = new Timer(_ => _ = ShareOtherMusicAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _otherMusicCallHandler = () => ReevaluateOtherMusicTimer();
        DiscordClient.DiscordSession.CallStateChanged += _otherMusicCallHandler;
        ReevaluateOtherMusicTimer();
    }

    private void ReevaluateOtherMusicTimer()
    {
        var timer = _otherMusicTimer;
        if (timer is null || _musicWebClosed) return;
        var shouldPoll = DiscordClient.DiscordSession.Current is { InCall: true }
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
            if (DiscordClient.DiscordSession.Current is not { InCall: true } session)
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
                session.SetCallSong(DiscordClient.DiscordSession.CallSongOf(other.Provider, other.Status, other.Title, other.Artist, other.PositionMs, other.DurationMs, null), other.CapturedAt);
            }
            else if (_sharingOtherMusic)
            {
                _sharingOtherMusic = false;
                session.SetCallSong(room is null ? null : DiscordClient.DiscordSession.CallSongOf(room.Provider, room.Status, room.Title, room.Artist, room.PositionMs, room.DurationMs, room.TrackId),
                    room?.CapturedAt ?? Environment.TickCount64);
            }
        }
        catch (Exception ex) { AppLog.Warn("Call song from another app: " + ex.GetType().Name + ": " + ex.Message); }
    }

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
            PostUi(() =>
            {
                if (_musicWebClosed) return;
                NotifyStateChanged();
                ReevaluateOtherMusicTimer();
                if (snapshot.Provider != _musicWebService) return;
                if (snapshot.Status == "playing") ApplyPendingMusicVolume();
                else ApplyMusicMemoryTarget();
                if (_sharingOtherMusic && snapshot.Status != "playing") return;
                DiscordClient.DiscordSession.Current?.SetCallSong(DiscordClient.DiscordSession.CallSongOf(snapshot.Provider,
                    snapshot.Status, snapshot.Title, snapshot.Artist, snapshot.PositionMs, snapshot.DurationMs, snapshot.TrackId), snapshot.CapturedAt);
                PublishMusicPlayback(snapshot);
            });
        sessions.PlaybackChanged += _playbackChangedHandler;
        return sessions;
    }

    internal static TimeSpan InitRetryBackoff(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(60),
    };

    private void SelectServiceCore(string service)
    {
        if (!MusicWebPolicy.IsService(service) || _musicWebService == service) return;
        _musicWebFailures = 0;
        _musicWebRetryAfter = DateTimeOffset.MinValue;
        ResetMusicWebView();
        _musicWebService = service;
        MediaSessions.SetProvider(service);
        PublishMusicStatus(service, "loading");
    }

    private void OpenMusicWeb(string service)
    {
        if (_musicWebClosed || !MusicEngineDiet.ServiceMatchesSelection(WebClientPreferences.Read().MusicService, service)) return;
        if (_musicWebService != service) SelectServiceCore(service);
        if (_musicWebFailed)
        {
            if (DateTimeOffset.UtcNow < _musicWebRetryAfter) return;
            ResetMusicWebView();
        }
        if (service == "spotify") _ = SpotifyApp.CloseRunningAsync(_lifetimeCts.Token);
        if (_musicHost is null)
        {
            var (width, height) = MusicEngineDiet.HiddenWindowSize(service);
            _musicHost = MusicWebHostWindow.CreateHidden(width, height);
        }
        var host = _musicHost;
        NotifyStateChanged();
        _musicWebInit ??= StartMusicWebAsync(host, service);
    }

    private async Task StartMusicWebAsync(MusicWebHostWindow host, string service)
    {
        await Task.Yield();
        if (_musicWebClosed || !ReferenceEquals(_musicHost, host) || _musicWebService != service) return;
        try { await InitializeMusicWebAsync(host, service); }
        catch (Exception) when (_musicWebClosed || !ReferenceEquals(_musicHost, host)) { }
        catch (Exception ex)
        {
            var reported = _musicWebFailed;
            AppLog.Warn("Music web player initialization failed: " + ex.GetType().Name + ": " + ex.Message);
            ResetMusicWebView();
            _musicWebFailed = true;
            _musicWebFailures++;
            _musicWebRetryAfter = DateTimeOffset.UtcNow + InitRetryBackoff(_musicWebFailures);
            if (!reported)
                PublishMusicStatus(service, "error", MusicWebPolicy.Label(service) + " could not load. Choose Retry to reconnect.");
        }
    }

    private async Task<object> ControlMusicCoreAsync(string? action, double? positionMs)
    {
        if (MusicWebPolicy.IsService(_musicWebService))
        {
            var released = !_musicWebReady;
            WakeMusicWeb();
            if (released && await MusicWebUpAsync(_musicWebService!))
                await MusicWebPolicy.WaitUntilAsync(() => MediaSessions.Current is { HasTrack: true }, () => false, MusicWebSongLimit, _lifetimeCts.Token);
        }
        var reply = await MediaSessions.ControlAsync(action, positionMs);
        if (!reply.Refused || _musicWebService != "spotify") return new { ok = reply.Ok, message = reply.Message };
        var cause = MusicControlRefusal.Classify(await SpotifyDevicesAsync());
        AppLog.Info($"Music: Spotify refused {action ?? "the control"}; cause {cause.Name}.");
        if (cause.Name != "another-device") return new { ok = false, message = reply.Message, cause = cause.Name };
        var where = cause.DeviceName is { Length: > 0 } name ? name : "another device";
        return new { ok = false, message = $"Spotify is playing on {where}.", cause = cause.Name, deviceId = cause.DeviceId };
    }

    private async Task<object> MusicVolumeCoreAsync(double? level, bool? muted)
    {
        var change = level is not null || muted is not null;
        if (level is { } yours && muted is null) _musicVolumeYours = (_musicWebService, yours);
        if (change && MusicWebPolicy.IsService(_musicWebService)) WakeMusicWeb();
        var target = muted == true ? 0 : level;
        if (target is null && MusicCore is { } core && (MusicWebSuspended(core) || _musicWebIdle) &&
            _musicAppLevelLast is { } cached && cached.Service == _musicWebService)
            return new { ok = true, level = cached.Level, muted = cached.Level <= 0, source = "app" };
        if (MusicAppLevel() is { } app)
        {
            var appLevel = target is { } wanted ? await app.WriteAsync(wanted) : await app.ReadAsync();
            if (appLevel is { } value && ReferenceEquals(app, _musicAppVolume))
            {
                _musicVolumeWanted = null;
                value = await FoldSessionVolumeAsync(app, value);
                _musicAppLevelLast = (_musicWebService, value);
                if (_musicVolumeYours?.Service != _musicWebService) _musicVolumeYours = (_musicWebService, value);
                return new { ok = true, level = value, muted = value <= 0, source = "app" };
            }
        }
        var chosen = muted == true ? 0 : level;
        if (chosen is { } later)
        {
            _musicVolumeWanted = later;
            _ = LandMusicVolumeAsync();
        }
        if (chosen is null && _musicAppLevelLast is { } last && last.Service == _musicWebService)
            return new { ok = true, level = last.Level, muted = last.Level <= 0, source = "app" };
        return new { ok = true, level = chosen, muted = chosen is <= 0, pending = true, source = "app" };
    }

    private async Task LandMusicVolumeAsync()
    {
        if (_musicVolumeLanding) return;
        _musicVolumeLanding = true;
        try
        {
            for (var attempt = 0; attempt < 30 && _musicVolumeWanted is not null && !_musicWebClosed; attempt++)
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

    private MusicAppVolume? MusicAppLevel()
    {
        if (_musicWebClosed) return null;
        if (_musicAppVolume is { } current)
        {
            if (current.Service == _musicWebService) return current;
            _musicAppVolume = null;
            current.Dispose();
        }
        if (!MusicWebPolicy.IsService(_musicWebService) || !_musicWebReady || MusicCore is not { } core) return null;
        var link = MusicAppVolume.ForWebPage(
            _musicWebService!,
            MusicWebEvaluate(core),
            paused: () => MusicWebSuspended(core),
            idle: () => (_musicWebIdle || HostHiddenOrMinimized() || _isHostWindowHidden is null) && !Volatile.Read(ref _musicWebAudio));
        _musicAppVolume = link;
        link.Changed += changed => PostUiAsync(async () =>
        {
            if (!ReferenceEquals(_musicAppVolume, link)) return;
            var wanted = _musicVolumeWanted;
            _musicVolumeWanted = null;
            if (wanted is { } level) { _ = MusicVolumeCoreAsync(level, null); return; }
            if (link.Service == "spotify" && _musicVolumeYours is { Service: "spotify" } yours && Math.Abs(changed - yours.Level) > 0.01
                && await SpotifyJamGuestAsync() && ReferenceEquals(_musicAppVolume, link))
            {
                AppLog.Info($"Music volume: Spotify's Jam set {changed:0.00}; your {yours.Level:0.00} kept.");
                _ = MusicVolumeCoreAsync(yours.Level, null);
                return;
            }
            _musicVolumeYours = (link.Service, changed);
            PublishMusicVolume(changed);
        }, "Music volume update");
        link.Watch();
        return link;
    }

    private MusicAppVolume.Evaluate MusicWebEvaluate(CoreWebView2 core, bool click = false) => async (expression, timeout, cancellation) =>
    {
        if (!ReferenceEquals(MusicCore, core)) return null;
        var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true, userGesture = click });
        var reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request).AsTask().WaitAsync(timeout, cancellation);
        using var json = JsonDocument.Parse(reply);
        if (json.RootElement.TryGetProperty("exceptionDetails", out _) ||
            !json.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value))
            return null;
        return value.Clone();
    };

    private Func<double, double, CancellationToken, Task<bool>> MusicWebPress(CoreWebView2 core) => async (x, y, cancellation) =>
    {
        if (!ReferenceEquals(MusicCore, core) || MusicPress.ClickCommands(x, y) is not { } commands) return false;
        var host = _musicHost;
        var restoredHidden = false;
        if (host?.Controller is { IsVisible: false })
        {
            host.SetControllerVisible(true);
            restoredHidden = true;
        }
        try
        {
            foreach (var (method, json) in commands)
                await core.CallDevToolsProtocolMethodAsync(method, json).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            return true;
        }
        finally
        {
            if (restoredHidden && host is not null && ReferenceEquals(_musicHost, host)) host.SetControllerVisible(false);
        }
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

    private async Task<double> FoldSessionVolumeAsync(MusicAppVolume app, double level)
    {
        if (app.SessionFolded) return level;
        var pids = MusicVolumePids();
        if (pids.Count == 0) return level;
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
        if (_musicWebClosed) return;
        try { _publishEvent?.Invoke("music.volume", new { level, source = "app" }); }
        catch { }
    }

    private void ApplyPendingMusicVolume()
    {
        if (_musicVolumeWanted is { } wanted) _ = MusicVolumeCoreAsync(wanted, null);
        else if (_musicAppVolume is { SessionFolded: false } link) _ = FoldOncePlayingAsync(link);
    }

    private async Task FoldOncePlayingAsync(MusicAppVolume link)
    {
        await Task.Delay(1500);
        if (ReferenceEquals(_musicAppVolume, link) && !link.SessionFolded) await MusicVolumeCoreAsync(null, null);
    }

    private HashSet<int> MusicVolumePids()
    {
        var pids = new HashSet<int>();
        if (MusicCore is { } core)
        {
            try
            {
                foreach (var process in core.Environment.GetProcessInfos()) pids.Add(process.ProcessId);
            }
            catch { }
        }
        return pids;
    }

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
        if (_musicWebClosed) return;
        try { _publishEvent?.Invoke("music.playback", PlaybackDto(snapshot)); }
        catch { }
    }

    private void WakeMusicWeb()
    {
        _musicIdleCancellation?.Cancel();
        _musicIdleCancellation = null;
        _musicWebIdle = false;
        try
        {
            if (_musicHost is not { } host || host.Core is not { } core) return;
            host.SetControllerVisible(true);
            if (core.IsSuspended) core.Resume();
            SetWebViewMemoryTarget(core, CoreWebView2MemoryUsageTargetLevel.Normal);
            if (_musicProcessesTrimmed) TrimMusicProcesses(core, low: false, trim: false);
            _musicNowPlaying?.Wake();
            _musicAppVolume?.Wake();
            ApplyMusicMemoryTarget();
        }
        catch { }
    }

    private void ReloadMusic()
    {
        _musicWebFailures = 0;
        _musicWebRetryAfter = DateTimeOffset.MinValue;
        if (_musicWebFailed) { ResetMusicWebView(); return; }
        if (MusicCore is not { } core) return;
        _musicWebReady = false;
        PublishMusicStatus(_musicWebService!, "loading");
        core.Reload();
    }

    private void ResetMusicWebView()
    {
        DropMusicAppVolume();
        DropSpotifyBridge();
        SetMusicNowPlaying(null);
        _musicIdleCancellation?.Cancel();
        _musicIdleCancellation = null;
        _musicWebIdle = false;
        _musicProcessesTrimmed = false;
        Interlocked.Increment(ref _musicTrimGeneration);
        _musicWebGeneration++;
        foreach (var popup in _musicPopouts.Keys.ToArray())
            try { popup.Close(); } catch { }
        _musicPopouts.Clear();
        var previous = _musicHost;
        _musicHost = null;
        _musicWebInit = null;
        _musicWebLoad?.TrySetCanceled();
        _musicWebLoad = null;
        _musicWebReady = false;
        _appleAdapterInstalled = false;
        _musicWebFailed = false;
        Volatile.Write(ref _musicWebAudio, false);
        NotifyStateChanged();
        if (previous is null) return;
        try
        {
            if (previous.Core is { } core) WebExtensions.Detach(core);
            previous.Close();
        }
        catch { }
        NotifyRenderersChanged();
    }

    private void CloseCore()
    {
        _musicWebClosed = true;
        try { _lifetimeCts.Cancel(); } catch { }
        ResetMusicWebView();
        _musicNowPlaying?.Dispose();
        _musicNowPlaying = null;
        if (_otherMusicCallHandler is not null)
        {
            DiscordClient.DiscordSession.CallStateChanged -= _otherMusicCallHandler;
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
            if (_musicHost is not { } host || host.Core is not { } core) return;
            SetWebViewMemoryTarget(core, core.IsSuspended || _musicWebIdle ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
            SetEmbeddedActivity(core, false);
            if (core.IsDocumentPlayingAudio || core.IsSuspended || _musicIdleCancellation is not null) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _musicIdleCancellation = cancellation;
            _ = SuspendIdleMusicAsync(host, cancellation);
        }
        catch { }
    }

    private async Task SuspendIdleMusicAsync(MusicWebHostWindow host, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(MusicSuspendDelay, cancellation.Token);
            while (!cancellation.IsCancellationRequested)
            {
                var hasHost = ReferenceEquals(host, _musicHost) && host.Core is not null;
                if (!hasHost) return;
                var candidate = host.Core!;
                var sessionPlaying = MediaSessions.Current is { Status: "playing" } now && now.Provider == _musicWebService;
                var hasPopouts = _musicPopouts.Count > 0;
                var signInActive = MusicSignInRequested();
                if (MusicEngineDiet.CanSuspend(hasHost, candidate.IsDocumentPlayingAudio, sessionPlaying, hasPopouts, signInActive))
                    break;
                if (!MusicEngineDiet.ShouldDeferIdleRetry(hasHost, candidate.IsDocumentPlayingAudio, sessionPlaying, hasPopouts, signInActive))
                    return;
                await Task.Delay(MusicEngineDiet.DeferredRetryDelay, cancellation.Token);
            }
            if (cancellation.IsCancellationRequested || !ReferenceEquals(host, _musicHost) || host.Core is not { } core) return;
            host.SetControllerVisible(false);
            var suspended = false;
            if (!core.IsSuspended && ReferenceEquals(host, _musicHost))
            {
                string? failure = null;
                var deadline = Environment.TickCount64 + 1600;
                while (!suspended && !core.IsSuspended && !cancellation.IsCancellationRequested &&
                       Environment.TickCount64 < deadline)
                {
                    await NextLowPriorityTurnAsync();
                    try { suspended = await core.TrySuspendAsync(); failure = null; }
                    catch (Exception error) { failure = $"{error.GetType().Name} 0x{error.HResult:X8}"; }
                    if (!suspended && !core.IsSuspended) await Task.Delay(120, cancellation.Token);
                }
                if (failure is not null) LogMusicSuspendFailure(failure);
                else if (!suspended && !core.IsSuspended) LogMusicSuspendFailure("declined");
            }
            var stillIdle = !cancellation.IsCancellationRequested && ReferenceEquals(host, _musicHost) && !core.IsDocumentPlayingAudio;
            if (!stillIdle)
            {
                host.SetControllerVisible(true);
                if (core.IsSuspended) core.Resume();
            }
            _musicWebIdle = stillIdle && !core.IsSuspended;
            SetWebViewMemoryTarget(core, core.IsSuspended || _musicWebIdle ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
            if (!stillIdle) return;
            if (App.MainAppWindow is null) TrimMusicProcesses(core, low: true, trim: true);
            await Task.Delay(MusicReleaseDelay, cancellation.Token);
            var releaseSessionPlaying = MediaSessions.Current is { Status: "playing" } playing && playing.Provider == _musicWebService;
            if (!MusicEngineDiet.CanRelease(
                    ReferenceEquals(host, _musicHost),
                    core.IsSuspended || _musicWebIdle,
                    core.IsDocumentPlayingAudio,
                    releaseSessionPlaying,
                    _musicPopouts.Count > 0,
                    _musicSignInWindow is not null))
                return;
            AppLog.Info(MusicWebPolicy.Label(_musicWebService ?? "") + " web player released after " + MusicReleaseDelay.TotalMinutes + " idle minutes.");
            ResetMusicWebView();
        }
        catch { if (ReferenceEquals(host, _musicHost)) host.SetControllerVisible(true); }
        finally
        {
            if (ReferenceEquals(_musicIdleCancellation, cancellation)) _musicIdleCancellation = null;
            cancellation.Dispose();
        }
    }

    private void TrimMusicProcesses(CoreWebView2 core, bool low, bool trim)
    {
        var browserPid = 0;
        var extraPids = new List<int>();
        try
        {
            browserPid = (int)core.BrowserProcessId;
            foreach (var info in core.Environment.GetProcessInfos())
                if (info.ProcessId > 0) extraPids.Add(info.ProcessId);
        }
        catch { }
        if (browserPid <= 0 && extraPids.Count == 0) return;
        _musicProcessesTrimmed = low;
        var generation = Interlocked.Increment(ref _musicTrimGeneration);
        _ = Task.Run(() =>
        {
            var self = Environment.ProcessId;
            var pids = new HashSet<int>(extraPids);
            if (browserPid > 0)
            {
                foreach (var pid in BackgroundTrim.TreePids(browserPid))
                    pids.Add(pid);
            }
            foreach (var pid in pids)
            {
                if (Volatile.Read(ref _musicTrimGeneration) != generation) return;
                if (pid <= 0 || pid == self) continue;
                if (ManagedProcessResources.Identify(pid) is { } identity)
                    BackgroundTrim.ApplyManaged(identity, low, trim);
            }
        });
    }

    private Task NextLowPriorityTurnAsync()
    {
        var turn = new TaskCompletionSource();
        if (_dispatcher is null || !_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => UiGuard.Run(() => turn.TrySetResult())))
            turn.TrySetResult();
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
            _publishEvent?.Invoke("music.status", new
            {
                ok = state != "error",
                ready = state == "running",
                service,
                state,
                message,
            });
        }
        catch { }
    }

    private async Task InitializeMusicWebAsync(MusicWebHostWindow host, string service)
    {
        var initTimer = Stopwatch.StartNew();
        WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? lease = null;
        CoreWebView2Environment environment;
        long envMs;
        long coreMs;
        try
        {
            lease = await WebViewEnvironmentFactory.AcquireAsync();
            environment = lease.Environment;
            envMs = initTimer.ElapsedMilliseconds;
            if (_musicWebClosed || !ReferenceEquals(_musicHost, host))
            {
                lease.Dispose();
                return;
            }
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)host.Handle),
                WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
            coreMs = initTimer.ElapsedMilliseconds;
            if (_musicWebClosed || !ReferenceEquals(_musicHost, host))
            {
                try { controller.Close(); } catch { }
                lease.Dispose();
                return;
            }
            var ownedLease = lease;
            lease = null;
            host.AttachController(controller, visible: true, environmentLease: ownedLease);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
        var core = host.Core!;
        WebViewEnvironmentFactory.LogEmbeddedProfileOnce(core);
        WebViewWindowHider.Hide(core.BrowserProcessId);
        _ = WebExtensions.AttachAsync(core, MusicWebPolicy.Label(service), host.Handle, service);
        await ResetProfilePermissionsAsync(core.Profile);
        var permissionsMs = initTimer.ElapsedMilliseconds;
        if (_musicWebClosed || !ReferenceEquals(_musicHost, host)) return;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        await SetMusicMotionPreferenceAsync(core);
        if (_musicWebClosed || !ReferenceEquals(_musicHost, host)) return;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _musicWebLoad = loaded;
        var guard = ConfigureMusicCore(core, environment, host, service);
        NotifyStateChanged();
        Volatile.Write(ref _musicWebAudio, core.IsDocumentPlayingAudio);
        core.IsDocumentPlayingAudioChanged += (_, _) => UiGuard.Run(() =>
        {
            if (!ReferenceEquals(_musicHost, host) || _musicWebClosed) return;
            Volatile.Write(ref _musicWebAudio, core.IsDocumentPlayingAudio);
            NotifyStateChanged();
            if (core.IsDocumentPlayingAudio)
            {
                _musicIdleCancellation?.Cancel();
                _musicIdleCancellation = null;
                _musicWebIdle = false;
                if (_musicProcessesTrimmed) TrimMusicProcesses(core, low: false, trim: false);
            }
            ApplyMusicMemoryTarget();
        });
        core.NavigationCompleted += (sender, e) => UiGuard.Run(() =>
        {
            if (!ReferenceEquals(_musicHost, host) || _musicWebClosed) return;
            if (!e.IsSuccess)
            {
                if (guard.Failure(e) is not { } message) return;
                _musicWebReady = false;
                _appleAdapterInstalled = false;
                _musicNowPlaying?.MarkNavigationStarting();
                _musicWebFailed = true;
                _musicWebCause = (service, message);
                host.SetControllerVisible(false);
                NotifyStateChanged();
                PublishMusicStatus(service, "error", message);
                loaded.TrySetException(new InvalidOperationException(message));
                return;
            }
            _musicWebReady = true;
            _musicWebFailures = 0;
            _musicWebRetryAfter = DateTimeOffset.MinValue;
            _appleAdapterInstalled = service == "apple";
            _musicWebBooted = false;
            _musicWebFailed = false;
            SetEmbeddedActivity(core, false, force: true);
            if (_musicNowPlaying?.Service != service)
                SetMusicNowPlaying(MusicNowPlaying.ForWebPage(service, MusicWebEvaluate(core), () => MusicWebSuspended(core) || _musicWebIdle,
                    control: MusicWebEvaluate(core, click: true), press: MusicWebPress(core), preinstalled: true));
            else
                _musicNowPlaying.MarkInstalled();
            loaded.TrySetResult();
            NotifyStateChanged();
            _ = PublishMusicReadyWhenBootedAsync(host, service);
            ApplyMusicMemoryTarget();
            NotifyRenderersChanged();
        });
        core.ProcessFailed += (_, e) => UiGuard.Run(() =>
        {
            if (!ReferenceEquals(_musicHost, host) || _musicWebClosed) return;
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
                return;
            _musicWebFailed = true;
            _musicWebReady = false;
            _appleAdapterInstalled = false;
            _musicNowPlaying?.MarkNavigationStarting();
            host.SetControllerVisible(false);
            NotifyStateChanged();
            PublishMusicStatus(service, "error", "The player stopped responding. Choose Retry to reconnect.");
            loaded.TrySetException(new InvalidOperationException("Music web process failed."));
            AppLog.Warn("Music web player process stopped; reload is available.");
        });
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.Script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(MusicNowPlaying.InstallScriptFor(service));
        if (service == "apple")
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AppleMusicNative.InstallScript);
        if (service == "youtube")
            await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.OptimizeScript);
        var domLogged = false;
        core.DOMContentLoaded += (_, _) => UiGuard.Run(() =>
        {
            if (domLogged || _musicWebClosed || !ReferenceEquals(_musicHost, host)) return;
            domLogged = true;
            AppLog.Info($"PERF music-init service={service} domContentLoadedMs={initTimer.ElapsedMilliseconds}");
        });
        core.Navigate(MusicWebPolicy.StartUri(service));
        AppLog.Info($"PERF music-init service={service} envMs={envMs} coreMs={coreMs} permissionsMs={permissionsMs} navigateMs={initTimer.ElapsedMilliseconds}");
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), _lifetimeCts.Token);
        AppLog.Info(MusicWebPolicy.Label(service) + " official web player started on the embedded profile.");
    }

    private WebPageGuard ConfigureMusicCore(CoreWebView2 core, CoreWebView2Environment environment,
        MusicWebHostWindow host, string service, bool popup = false)
    {
        var generation = _musicWebGeneration;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = popup;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        core.PermissionRequested += (_, args) => UiGuard.Run(() => OnMusicPermissionRequested(args, service));
        var userAgent = core.Settings.UserAgent ?? "";
        var cleaned = StoreAuthWindow.CleanUserAgent(userAgent);
        if (!string.Equals(cleaned, userAgent, StringComparison.Ordinal))
            core.Settings.UserAgent = cleaned;
        var guard = WebPageGuard.Attach(core, MusicWebPolicy.Label(service));
        if (!popup) AttachMusicResourceDiet(core, environment, service);
        core.NavigationStarting += (_, e) => UiGuard.Run(() =>
        {
            if (MusicWebPolicy.IsServiceDocument(service, e.Uri) || (popup && (e.Uri == "about:blank" || MusicWebPolicy.IsSignInHop(service, e.Uri))) || WebPageGuard.IsAppLink(e.Uri))
            {
                if (!popup && !WebPageGuard.IsAppLink(e.Uri) && ReferenceEquals(_musicHost, host))
                {
                    _appleAdapterInstalled = false;
                    _musicNowPlaying?.MarkNavigationStarting();
                }
                return;
            }
            e.Cancel = true;
            if (popup)
            {
                if (e.IsUserInitiated && MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
                if (_musicPopouts.ContainsKey(host))
                {
                    if (MusicSignInRequested() || ReferenceEquals(_musicSignInWindow, host))
                        NoteMusicSignInFailed(service);
                    try { host.Close(); } catch { }
                }
                return;
            }
            if (e.IsUserInitiated && MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
        });
        core.NewWindowRequested += (sender, e) => _ = UiGuard.RunAsync(async () =>
        {
            _ = sender;
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
            MusicWebHostWindow? child = null;
            WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? popupLease = null;
            var popupDismissed = false;
            try
            {
                child = MusicWebHostWindow.CreatePopup(MusicWebPolicy.Label(service) + " · Exo", ExoConnectedTheme.AskedSize(e.WindowFeatures));
                var window = child;
                _musicPopouts[window] = null;
                NotifyStateChanged();
                window.Closed += () =>
                {
                    popupDismissed = true;
                    _musicPopouts.Remove(window);
                    if (service == "apple" && MusicSignInRequested())
                    {
                        AppLog.Info("Music sign-in: Apple's window closed.");
                        _ = FinishAppleSignInAsync();
                    }
                    NotifyStateChanged();
                    ApplyMusicMemoryTarget();
                    try { if (window.Core is { } c) WebExtensions.Detach(c); } catch { }
                };
                if (e.IsUserInitiated || MusicSignInRequested())
                {
                    ForegroundWatcher.Asked(MusicWebPolicy.Label(service) + " opened a window of its own");
                    window.Show(activate: true);
                }
                else
                {
                    window.Show(activate: false);
                }
                popupLease = WebViewEnvironmentFactory.TryAcquire(environment) ?? await WebViewEnvironmentFactory.AcquireAsync();
                var popupEnv = popupLease.Environment;
                var controller = await popupEnv.CreateCoreWebView2ControllerAsync(
                    CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window.Handle),
                    WebViewEnvironmentFactory.EmbeddedControllerOptions(popupEnv));
                var ensureMs = popupTimer.ElapsedMilliseconds;
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration)
                {
                    try { controller.Close(); } catch { }
                    popupLease.Dispose();
                    popupLease = null;
                    window.Close();
                    return;
                }
                var ownedPopupLease = popupLease;
                popupLease = null;
                window.AttachController(controller, visible: true, environmentLease: ownedPopupLease);
                var popupCore = window.Core!;
                _musicPopouts[window] = popupCore;
                _ = WebExtensions.AttachAsync(popupCore, MusicWebPolicy.Label(service), window.Handle, service);
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                await SetMusicMotionPreferenceAsync(popupCore);
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                ConfigureMusicCore(popupCore, popupEnv, window, service, popup: true);
                await popupCore.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.Script);
                void UpdatePopupActivity(bool force = false)
                {
                    var minimized = window.IsMinimized;
                    SetEmbeddedActivity(popupCore, !minimized, force);
                    SetWebViewMemoryTarget(popupCore, minimized ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
                }
                window.MinimizedChanged += _ => { try { UpdatePopupActivity(); } catch { } };
                popupCore.NavigationCompleted += (_, _) => UiGuard.Run(() => { try { UpdatePopupActivity(force: true); } catch { } });
                popupCore.WindowCloseRequested += (_, _) => UiGuard.Run(() => window.Close());
                popupCore.NavigationStarting += (_, step) => UiGuard.Run(() =>
                {
                    if (MusicSignInRequested() && Uri.TryCreate(step.Uri, UriKind.Absolute, out var at))
                        AppLog.Info($"Music sign-in: {at.Host}{(step.Cancel ? " refused" : "")}");
                });
                var popupDomLogged = false;
                popupCore.DOMContentLoaded += (_, _) => UiGuard.Run(() =>
                {
                    if (popupDomLogged) return;
                    popupDomLogged = true;
                    AppLog.Info($"PERF music-popup service={service} domContentLoadedMs={popupTimer.ElapsedMilliseconds}");
                });
                e.NewWindow = popupCore;
                AppLog.Info($"PERF music-popup service={service} ensureMs={ensureMs} readyMs={popupTimer.ElapsedMilliseconds}");
            }
            catch
            {
                var dismissed = popupDismissed;
                try { child?.Close(); } catch { }
                try { popupLease?.Dispose(); } catch { }
                if (!dismissed && generation == _musicWebGeneration)
                    PublishMusicStatus(service, "error", "The player's window could not open. Try opening it in your browser.");
            }
            finally { try { deferral.Complete(); } catch { } }
        });
        return guard;
    }

    private void AttachMusicResourceDiet(CoreWebView2 core, CoreWebView2Environment environment, string service)
    {
        foreach (var (pattern, context) in MusicEngineDiet.FilterPatternsForService(service))
            core.AddWebResourceRequestedFilter(pattern, context);
        core.WebResourceRequested += (_, e) => UiGuard.Run(() =>
        {
            if (_musicWebClosed || !MusicEngineDiet.ShouldBlockResource(service, e.Request.Uri, e.ResourceContext, popup: false))
                return;
            try { e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", ""); }
            catch { }
        });
    }

    private void OnMusicPermissionRequested(CoreWebView2PermissionRequestedEventArgs e, string service)
    {
        e.SavesInProfile = false;
        e.Handled = true;
        if (_musicWebClosed || !MusicWebPolicy.IsPlayerDocument(service, e.Uri))
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }
        var current = WebClientPreferences.Read();
        var policy = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Microphone => current.Microphone,
            CoreWebView2PermissionKind.Camera => current.Camera,
            CoreWebView2PermissionKind.ClipboardRead => current.Clipboard,
            CoreWebView2PermissionKind.Notifications => current.Notifications,
            _ => "block",
        };
        e.State = policy == "allow" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
    }

    private static async Task ResetProfilePermissionsAsync(CoreWebView2Profile profile)
    {
        foreach (var grant in await profile.GetNonDefaultPermissionSettingsAsync())
            await profile.SetPermissionStateAsync(grant.PermissionKind, grant.PermissionOrigin, CoreWebView2PermissionState.Default);
    }

    private void SetWebViewMemoryTarget(CoreWebView2 core, CoreWebView2MemoryUsageTargetLevel target)
    {
        if (core is null) return;
        var state = _embeddedActivity.GetOrCreateValue(core);
        if (state.MemoryTarget == target) return;
        core.MemoryUsageTargetLevel = target;
        state.MemoryTarget = target;
    }

    private void SetEmbeddedActivity(CoreWebView2 core, bool active, bool force = false) =>
        _ = SetEmbeddedActivityAsync(core, active, force);

    private async Task SetEmbeddedActivityAsync(CoreWebView2 core, bool active, bool force = false)
    {
        if (core is null) return;
        var state = _embeddedActivity.GetOrCreateValue(core);
        if (!force && state.Active == active) return;
        state.Active = active;
        var version = ++state.Version;
        try
        {
            var result = await core.ExecuteScriptAsync($"window.__exoClientPresentation?.setActive({(active ? "true" : "false")}); !!window.__exoClientPresentation")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
            if (result != "true" && version == state.Version) state.Active = null;
        }
        catch { if (version == state.Version) state.Active = null; }
    }

    private async Task SetMusicMotionPreferenceAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia",
                "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
        }
        catch { }
    }

    private static void OpenMusicExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { AppLog.Warn("Music external link could not open."); }
    }
}
