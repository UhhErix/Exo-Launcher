using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

internal sealed partial class MusicEngineService
{
    private DateTime _musicWebLoadingSince;
    private int _musicWebStalls;
    private static readonly TimeSpan MusicWebStall = TimeSpan.FromSeconds(45);

    private bool _musicWebBooted;
    private static readonly TimeSpan MusicWebLoadLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MusicWebBootLimit = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MusicWebSongLimit = TimeSpan.FromSeconds(8);
    private const int MaxMusicPageCharacters = 4 * 1024 * 1024;

    private DateTime _musicSignInUntil;
    private MusicWebHostWindow? _musicSignInWindow;
    private (string Service, string Message)? _musicSignInFailure;
    private bool _youTubeNeedsPremium;

    private const string YouTubeSignInUri =
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    private const string SpotifySignInUri = "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F";

    private const string YouTubeChooseAccountUri =
        "https://accounts.google.com/AccountChooser?service=youtube&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    internal Task<object> StartAsync(string service)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, state = "error", message = "Music engine is unavailable." });
        return OnUiAsync(() => WebMusicStartCoreAsync(service));
    }

    internal Task<object> WebMusicViewAsync(string service, string? view, string? uri, string? query, int offset)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, message = "Music engine is unavailable." });
        return OnUiAsync(() => WebMusicViewCoreAsync(service, view, uri, query, offset));
    }

    internal Task<object> WebMusicActAsync(string service, string? action, string? uri, string? context, int? index, bool? on, string? uid,
        string? query, string? text = null)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, message = "Music engine is unavailable." });
        return OnUiAsync(() => WebMusicActCoreAsync(service, action, uri, context, index, on, uid, query, text));
    }

    internal Task<object> SignInAsync(string? service)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, message = "Music engine is unavailable." });
        return OnUiAsync(() => MusicSignInCoreAsync(service));
    }

    private object MusicWebLoading(string service)
    {
        if (_musicWebLoadingSince == default) _musicWebLoadingSince = DateTime.UtcNow;
        if (DateTime.UtcNow - _musicWebLoadingSince < MusicWebStall) return new { ok = true, state = "loading" };
        AppLog.Warn($"Music: {MusicWebPolicy.Label(service)} did not come up in {(int)MusicWebStall.TotalSeconds} s (attempt {_musicWebStalls + 1}).");
        _musicWebLoadingSince = default;
        ResetMusicWebView();
        if (++_musicWebStalls <= 2) return new { ok = true, state = "loading" };
        _musicWebStalls = 0;
        return new { ok = false, state = "error", message = MusicWebPolicy.Label(service) + " is taking too long to load. Open it again." };
    }

    internal readonly record struct MusicStartStatus(
        bool Ok,
        string State,
        string? Message,
        bool YouTubeNeedsPremium,
        bool SignedIn);

    internal static MusicStartStatus? ClassifyMusicStatus(string service, JsonElement? status, string? signInFailure)
    {
        if (status is not { ValueKind: JsonValueKind.Object } answer ||
            !answer.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
            return null;
        var label = MusicWebPolicy.Label(service);
        var signedIn = answer.TryGetProperty("signedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
        var unsubscribed = signedIn && answer.TryGetProperty("subscribed", out var subscribed) && subscribed.ValueKind == JsonValueKind.False;
        var youTubeNeedsPremium = service == "youtube" && unsubscribed;
        if (service == "spotify" && answer.TryGetProperty("drm", out var drm) && drm.ValueKind == JsonValueKind.False)
            return new MusicStartStatus(false, "error", "Spotify can't play protected audio in Exo yet. Open it again in a minute.", youTubeNeedsPremium, signedIn);
        if (unsubscribed)
        {
            var message = service switch
            {
                "youtube" => "This account has no YouTube Music Premium. Sign in with one that does.",
                "spotify" => "Exo plays Spotify with a Premium account.",
                _ => label + " needs a subscription. Sign in with an Apple ID that has one.",
            };
            return new MusicStartStatus(true, "notPremium", message, youTubeNeedsPremium, signedIn);
        }
        if (signedIn)
            return new MusicStartStatus(true, "running", null, youTubeNeedsPremium, true);
        if (service == "youtube")
            return new MusicStartStatus(true, "signedOut", signInFailure ?? "Sign in with a YouTube Music Premium account.", youTubeNeedsPremium, false);
        return new MusicStartStatus(true, "signedOut", signInFailure, youTubeNeedsPremium, false);
    }

    internal static async Task<MusicStartStatus?> WaitAndPublishReadyAsync(
        string service,
        Func<Task<JsonElement?>> probeStatus,
        Func<bool> gone,
        Func<bool> alreadyBooted,
        Action<MusicStartStatus> onBooted,
        Action<string, string, string?> publish,
        TimeSpan limit,
        TimeSpan step,
        CancellationToken cancellation)
    {
        var started = Environment.TickCount64;
        try
        {
            for (;;)
            {
                if (gone() || alreadyBooted()) return null;
                var status = await probeStatus();
                if (gone() || alreadyBooted()) return null;
                if (ClassifyMusicStatus(service, status, null) is { } resolved)
                {
                    onBooted(resolved);
                    publish(service, resolved.State, resolved.Message);
                    return resolved;
                }
                if (Environment.TickCount64 - started >= limit.TotalMilliseconds) return null;
                await Task.Delay(step, cancellation);
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task PublishMusicReadyWhenBootedAsync(MusicWebHostWindow host, string service)
    {
        try
        {
            await WaitAndPublishReadyAsync(
                service,
                () => WebMusicStatusCallAsync(service, attempts: 1, timeout: TimeSpan.FromSeconds(6)),
                () => _musicWebClosed || _musicWebFailed || !ReferenceEquals(_musicHost, host) || _musicWebService != service,
                () => _musicWebBooted,
                resolved =>
                {
                    _musicWebBooted = true;
                    _musicWebLoadingSince = default;
                    _musicWebStalls = 0;
                    ApplyMusicMemoryTarget();
                    _youTubeNeedsPremium = resolved.YouTubeNeedsPremium;
                    if (resolved.SignedIn)
                    {
                        _musicSignInFailure = null;
                        if (MusicSignInRequested())
                        {
                            AppLog.Info("Music sign-in: " + MusicWebPolicy.Label(service) + " is signed in.");
                            _musicSignInUntil = default;
                        }
                    }
                    else if (resolved.State == "notPremium")
                    {
                        _musicSignInFailure = null;
                    }
                },
                PublishMusicStatus,
                MusicWebBootLimit,
                TimeSpan.FromMilliseconds(250),
                _lifetimeCts.Token);
        }
        catch (Exception) when (_musicWebClosed || !ReferenceEquals(_musicHost, host)) { }
        catch { }
    }

    private async Task<object> WebMusicStartCoreAsync(string service)
    {
        var label = MusicWebPolicy.Label(service);
        if (!MusicEngineDiet.ServiceMatchesSelection(WebClientPreferences.Read().MusicService, service))
            return new { ok = false, state = "error", message = "Choose " + label + " in Settings." };
        if (_musicWebService != service) SelectServiceCore(service);
        if (_musicWebCause is { } last)
        {
            _musicWebCause = null;
            if (last.Service == service && _musicWebFailed) return new { ok = false, state = "error", message = last.Message };
        }
        if (_musicWebFailed) ResetMusicWebView();
        if (_musicHost is null || _musicWebInit is null)
        {
            OpenMusicWeb(service);
            if (_musicHost is null) return new { ok = false, state = "error", message = label + " could not open." };
        }
        if (!_musicWebReady) return MusicWebLoading(service);
        var status = await WebMusicStatusCallAsync(service, attempts: 2);
        if (ClassifyMusicStatus(service, status, null) is not { } result)
            return MusicWebLoading(service);
        var firstBoot = !_musicWebBooted;
        _musicWebBooted = true;
        _musicWebLoadingSince = default;
        _musicWebStalls = 0;
        ApplyMusicMemoryTarget();
        _youTubeNeedsPremium = result.YouTubeNeedsPremium;
        if (!result.Ok && service == "spotify")
            AppLog.Warn("Spotify: this WebView2 has no Widevine module yet.");
        if (result.State == "notPremium")
            _musicSignInFailure = null;
        if (result.SignedIn)
        {
            _musicSignInFailure = null;
            if (MusicSignInRequested())
            {
                AppLog.Info("Music sign-in: " + label + " is signed in.");
                _musicSignInUntil = default;
            }
        }
        if (result.State == "signedOut" && TakeMusicSignInFailure(service) is { } interrupted)
            result = ClassifyMusicStatus(service, status, interrupted) ?? result;
        if (firstBoot)
            PublishMusicStatus(service, result.State, result.Message);
        return result.Message is null
            ? new { ok = result.Ok, state = result.State }
            : new { ok = result.Ok, state = result.State, message = result.Message };
    }

    private Task<JsonElement?> WebMusicStatusCallAsync(string service, int attempts, TimeSpan? timeout = null) => service switch
    {
        "apple" => WebMusicAppleCallAsync(installed => AppleMusicNative.View("status", null, null, 0, installed), attempts: attempts, timeout: timeout),
        "spotify" => WebMusicCallAsync(service, SpotifyNative.Status, attempts: attempts, timeout: timeout),
        _ => WebMusicCallAsync(service, YouTubeMusicNative.View("status", null, null, 0), attempts: attempts, timeout: timeout),
    };

    private async Task<bool> MusicWebUpAsync(string service)
    {
        if (_musicWebReady && _musicWebBooted && MusicCore is not null && _musicWebService == service) return true;
        if (_musicWebClosed) return false;
        if (!MusicEngineDiet.ServiceMatchesSelection(WebClientPreferences.Read().MusicService, service)) return false;
        if (_musicWebService != service) SelectServiceCore(service);
        if (_musicWebFailed) ResetMusicWebView();
        bool Gone() => _musicWebService != service || _musicWebClosed || _musicWebFailed;
        if (Gone()) return false;
        if (_musicHost is null || _musicWebInit is null)
        {
            OpenMusicWeb(service);
            if (_musicHost is null) return false;
        }
        if (!await MusicWebPolicy.WaitUntilAsync(() => _musicWebReady, Gone, MusicWebLoadLimit, _lifetimeCts.Token)) return false;
        var booted = await MusicWebPolicy.WaitUntilAsync(async () =>
        {
            if (_musicWebBooted) return true;
            var status = await WebMusicStatusCallAsync(service, attempts: 1, timeout: TimeSpan.FromSeconds(6));
            return status is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True;
        }, Gone, MusicWebBootLimit, _lifetimeCts.Token);
        if (booted)
        {
            _musicWebBooted = true;
            ApplyMusicMemoryTarget();
        }
        return !Gone();
    }

    private async Task<object> WebMusicViewCoreAsync(string service, string? view, string? uri, string? query, int offset)
    {
        var apple = service == "apple";
        if (view is null || !(apple ? AppleMusicNative.Views : YouTubeMusicNative.Views).Contains(view)) return new { ok = false, message = "Unknown page." };
        var validUri = apple ? AppleMusicNative.IsUri(uri) : YouTubeMusicNative.IsUri(uri);
        if ((apple ? view is "playlist" or "album" or "artist" or "lyrics" or "credits" or "ratings" : YouTubeMusicNative.PageViews.Contains(view)) && !validUri ||
            !apple && view == "search" && uri is not null && !validUri)
            return new { ok = false, message = "Unknown page." };
        if (view == "library" && (!(apple ? AppleMusicNative.IsLibraryQuery(query) : YouTubeMusicNative.IsLibraryQuery(query)) ||
            uri is not null && !validUri))
            return new { ok = false, message = "Unknown page." };
        if (view == "songs" && !MusicSongs.IsQuery(query)) return new { ok = false, message = "Unknown page." };
        query = query is { Length: > 200 } ? query[..200] : query;
        offset = Math.Clamp(offset, 0, 100_000);
        var label = MusicWebPolicy.Label(service);
        if (!await MusicWebUpAsync(service)) return new { ok = false, message = label + " is not answering. Try again." };
        var data = apple
            ? await WebMusicAppleCallAsync(installed => AppleMusicNative.View(view, uri, query, offset, installed))
            : await WebMusicCallAsync(service, YouTubeMusicNative.View(view, uri, query, offset));
        if (data is not { ValueKind: JsonValueKind.Object } page) return new { ok = false, message = label + " is not answering. Try again." };
        if (page.TryGetProperty("error", out var error))
        {
            AppLog.Warn(label + " room " + view + ": " + error.GetString());
            return new { ok = false, message = label + " could not load this page." };
        }
        if (page.GetRawText().Length > MaxMusicPageCharacters)
        {
            AppLog.Warn(label + " room " + view + ": page too large, dropped.");
            return new { ok = false, message = label + " could not load this page." };
        }
        if (!apple && view == "lyrics" && SyncedLyrics.SongOf(page) is { } song &&
            SyncedLyrics.Better(await SyncedLyrics.FindAsync(song), SyncedLyrics.HasLines(page)) is { } lyrics)
            return new { ok = true, data = new { synced = lyrics.Synced, lines = lyrics.Lines.Select(line => new { start = line.Start, text = line.Text }) } };
        return new { ok = true, data = page };
    }

    private async Task<object> WebMusicActCoreAsync(string service, string? action, string? uri, string? context, int? index, bool? on, string? uid,
        string? query, string? text = null)
    {
        var apple = service == "apple";
        if (uid == "") uid = null;
        if (!MusicSongs.IsQuery(query)) return new { ok = false, message = "Nothing to play." };
        if (action is null || !(apple ? AppleMusicNative.Actions : YouTubeMusicNative.Actions).Contains(action)) return new { ok = false, message = "Unknown action." };
        bool Valid(string? id) => id is null || (apple ? AppleMusicNative.IsUri(id) : YouTubeMusicNative.IsUri(id));
        if (!Valid(uri) || !(apple ? Valid(context) : YouTubeMusicNative.IsContext(action, context)) ||
            !(apple ? AppleMusicNative.IsUid(uid) : YouTubeMusicNative.IsUid(uid)) || !AppleMusicNative.IsText(text))
            return new { ok = false, message = "Nothing to play." };
        if (action is "skipTo" or "like" or "enqueue" or "playNext" or "addToPlaylist" or "station" or "library" or "suggestLess" or "dequeue" && uri is null ||
            action is "editPlaylist" or "deletePlaylist" or "movePlaylistTrack" && context is null || action == "play" && uri is null && context is null)
            return new { ok = false, message = "Nothing to play." };
        var label = MusicWebPolicy.Label(service);
        if (!await MusicWebUpAsync(service)) return new { ok = false, message = label + " did not take that. Try again." };
        var reply = apple
            ? await WebMusicAppleCallAsync(installed => AppleMusicNative.Act(action, uri, context, index, on, uid, query, text, installed), attempts: 1, click: true)
            : await WebMusicCallAsync(service, YouTubeMusicNative.Act(action, uri, context, index, on, uid, query, text), attempts: 1, click: false);
        if (reply is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return new { ok = true };
        if (reply is { ValueKind: JsonValueKind.Object } failed && failed.TryGetProperty("error", out var error))
            AppLog.Warn(label + " room " + action + ": " + error.GetString());
        return new { ok = false, message = label + " did not take that. Try again." };
    }

    private void NoteMusicSignInFailed(string service)
    {
        var message = MusicWebPolicy.Label(service) + " could not finish sign-in. Try again.";
        _musicSignInFailure = (service, message);
        AppLog.Warn(message);
    }

    private string? TakeMusicSignInFailure(string service)
    {
        if (_musicSignInFailure is not { } failed || failed.Service != service) return null;
        _musicSignInFailure = null;
        return failed.Message;
    }

    private bool MusicSignInRequested() => DateTime.UtcNow < _musicSignInUntil;

    private async Task<object> MusicSignInCoreAsync(string? service)
    {
        if (service is null || !MusicWebPolicy.IsService(service) || _musicWebService != service || !_musicWebReady)
            return new { ok = false, message = "The player is still opening. Try again." };
        var label = MusicWebPolicy.Label(service);
        if (_musicSignInWindow is { } open && !open.IsClosed)
        {
            ForegroundWatcher.Asked(label + "'s sign-in window, asked for again");
            open.Activate();
            return new { ok = true };
        }
        if (service == "apple")
        {
            _musicSignInUntil = DateTime.UtcNow.AddMinutes(10);
            var reply = await WebMusicCallAsync(service, AppleMusicNative.SignIn, attempts: 3, click: true);
            if (reply is { ValueKind: JsonValueKind.True }) return new { ok = true };
            _musicSignInUntil = default;
            return new { ok = false, message = label + " could not start sign-in. Try again." };
        }
        var generation = _musicWebGeneration;
        var window = MusicWebHostWindow.CreatePopup(label + " · Exo");
        _musicSignInWindow = window;
        _musicPopouts[window] = null;
        var closed = false;
        window.Closed += () =>
        {
            closed = true;
            if (ReferenceEquals(_musicSignInWindow, window)) _musicSignInWindow = null;
            _musicPopouts.Remove(window);
            NotifyStateChanged();
            ApplyMusicMemoryTarget();
            try { if (window.Core is { } c) WebExtensions.Detach(c); } catch { }
        };
        ForegroundWatcher.Asked(label + "'s sign-in window");
        window.Show(activate: true);
        WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? lease = null;
        try
        {
            lease = await WebViewEnvironmentFactory.AcquireAsync();
            var environment = lease.Environment;
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window.Handle),
                WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
            if (closed || _musicWebService != service || generation != _musicWebGeneration)
            {
                try { controller.Close(); } catch { }
                lease.Dispose();
                lease = null;
                window.Close();
                return new { ok = false, message = label + " could not start sign-in. Try again." };
            }
            var ownedLease = lease;
            lease = null;
            window.AttachController(controller, visible: true, environmentLease: ownedLease);
            var core = window.Core!;
            _musicPopouts[window] = core;
            var finished = false;
            core.NavigationStarting += (_, e) => UiGuard.Run(() =>
            {
                if (finished || !MusicWebPolicy.IsPlayerDocument(service, e.Uri)) return;
                finished = true;
                e.Cancel = true;
                PostUi(() =>
                {
                    try { window.Close(); } catch { }
                    if (_musicWebService == service && generation == _musicWebGeneration) ReloadMusic();
                });
            });
            ConfigureMusicCore(core, environment, window, service, popup: true);
            core.WindowCloseRequested += (_, _) => UiGuard.Run(() => window.Close());
            core.Navigate(service == "spotify" ? SpotifySignInUri : _youTubeNeedsPremium ? YouTubeChooseAccountUri : YouTubeSignInUri);
            return new { ok = true };
        }
        catch (Exception error)
        {
            AppLog.Warn(label + " sign-in window could not open: " + error.GetType().Name);
            try { window.Close(); } catch { }
            try { lease?.Dispose(); } catch { }
            return new { ok = false, message = label + " could not start sign-in. Try again." };
        }
    }

    private async Task FinishAppleSignInAsync()
    {
        await Task.Delay(1500);
        if (_musicWebService != "apple" || !MusicSignInRequested()) return;
        var status = await WebMusicAppleCallAsync(installed => AppleMusicNative.View("status", null, null, 0, installed), attempts: 2);
        var signedIn = status is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("signedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
        var trace = await WebMusicCallAsync("apple", AppleMusicNative.SignInTrace, attempts: 1);
        AppLog.Info("Music sign-in: Apple's page " + (signedIn ? "has the session" : "has no session yet; reading it fresh") +
            " (authorize " + (trace is { ValueKind: JsonValueKind.String } how ? how.GetString() : "unknown") + ").");
        if (!signedIn) ReloadMusic();
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (_musicWebService != "apple" || !MusicSignInRequested()) return;
        _musicSignInUntil = default;
        NoteMusicSignInFailed("apple");
    }

    private Task<JsonElement?> WebMusicAppleCallAsync(Func<bool, string> buildExpression, int attempts = 5, bool click = false, TimeSpan? timeout = null) =>
        AppleMusicNative.InvokeCachedAsync(
            buildExpression,
            expression => WebMusicCallAsync("apple", expression, attempts: attempts, click: click, timeout: timeout),
            () => _appleAdapterInstalled,
            installed => _appleAdapterInstalled = installed);

    private async Task<JsonElement?> WebMusicCallAsync(string service, string expression, int attempts = 5, bool click = false, TimeSpan? timeout = null)
    {
        for (var attempt = 0; attempt < attempts && !_lifetimeCts.IsCancellationRequested; attempt++)
        {
            if (_musicWebService != service || !_musicWebReady || MusicCore is not { } core) return null;
            WakeMusicWeb();
            if (attempt > 0)
            {
                try { await Task.Delay(500, _lifetimeCts.Token); } catch (OperationCanceledException) { return null; }
            }
            JsonElement? reply;
            try { reply = await MusicWebEvaluate(core, click)(expression, timeout ?? TimeSpan.FromSeconds(15), _lifetimeCts.Token); }
            catch { reply = null; }
            if (reply is not null) return reply;
        }
        return null;
    }
}
