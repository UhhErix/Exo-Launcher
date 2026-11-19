using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExoLauncher;

/// <summary>
/// Exo's own Music rooms. The service's web player (open.spotify.com,
/// music.youtube.com, music.apple.com) loads on the embedded profile and
/// stays transparent behind the room; its pages, queue and actions go through
/// <see cref="SpotifyNative"/>, <see cref="YouTubeMusicNative"/> and <see cref="AppleMusicNative"/>. The
/// page itself is never the room: signing in happens in the provider's own
/// sign-in window (<see cref="MusicSignInAsync"/>).
/// </summary>
public sealed partial class MainWindow
{
    /// Loads the hidden page if needed and says where the room stands:
    /// loading, signedOut, notPremium (signed in without the subscription) or running.
    /// When the current page began loading, and how many times a page that never came up has been reloaded: a big account's player
    /// can take longer than a small one's, but never forever. The room reloads it twice, then says so.
    private DateTime _musicWebLoadingSince;
    private int _musicWebStalls;
    private static readonly TimeSpan MusicWebStall = TimeSpan.FromSeconds(45);

    /// <summary>The room's "loading", unless this page has been loading too long: then it is reloaded, and after two reloads the room says why.</summary>
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

    internal async Task<object> WebMusicStartAsync(string service)
    {
        var label = MusicWebPolicy.Label(service);
        if (WebClientPreferences.Read().MusicService is { } selected && selected != service)
            return new { ok = false, state = "error", message = "Choose " + label + " in Settings." };
        if (_musicWebService != service) SelectMusicService(service);
        // The page didn't load: the room says why, once, and its Open again loads it afresh (rather than a new page every
        // second while offline).
        if (_musicWebCause is { } last)
        {
            _musicWebCause = null;
            if (last.Service == service && _musicWebFailed) return new { ok = false, state = "error", message = last.Message };
        }
        if (_musicWebFailed) ResetMusicWebView();
        if (_musicWeb is null || _musicWebInit is null)
        {
            OpenMusicWeb(service);
            if (_musicWeb is null) return new { ok = false, state = "error", message = label + " could not open." };
        }
        if (!_musicWebReady) return MusicWebLoading(service);
        var status = await WebMusicCallAsync(service, MusicWebStatus(service), attempts: 2);
        if (status is not { ValueKind: JsonValueKind.Object } answer ||
            !answer.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
            return MusicWebLoading(service);
        _musicWebLoadingSince = default;
        _musicWebStalls = 0;
        var signedIn = answer.TryGetProperty("signedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
        // Apple Music and YouTube Music play only for a subscriber (Premium): signed in without one is the
        // screen every music service shows for that, saying why; its Sign in picks another account (YouTube:
        // Google's account chooser; Apple: the page signs the Apple ID out first).
        var unsubscribed = signedIn && answer.TryGetProperty("subscribed", out var subscribed) && subscribed.ValueKind == JsonValueKind.False;
        _youTubeNeedsPremium = service == "youtube" && unsubscribed;
        // Spotify's page plays protected audio through the browser's Widevine module, and it fails silently behind Exo's room.
        if (service == "spotify" && answer.TryGetProperty("drm", out var drm) && drm.ValueKind == JsonValueKind.False)
        {
            AppLog.Warn("Spotify: this WebView2 has no Widevine module yet.");
            return new { ok = false, state = "error", message = "Spotify can't play protected audio in Exo yet. Open it again in a minute." };
        }
        if (unsubscribed)
        {
            _musicSignInFailure = null;
            return new { ok = true, state = "notPremium", message = service switch
            {
                "youtube" => "This account has no YouTube Music Premium. Sign in with one that does.",
                "spotify" => "Exo plays Spotify with a Premium account.",
                _ => label + " needs a subscription. Sign in with an Apple ID that has one.",
            } };
        }
        if (signedIn)
        {
            _musicSignInFailure = null;
            if (MusicSignInRequested())
            {
                AppLog.Info("Music sign-in: " + label + " is signed in.");
                _musicSignInUntil = default;
            }
            return new { ok = true, state = "running" };
        }
        var interrupted = TakeMusicSignInFailure(service);
        if (service == "youtube")
            return new { ok = true, state = "signedOut", message = interrupted ?? "Sign in with a YouTube Music Premium account." };
        if (interrupted is null) return new { ok = true, state = "signedOut" };
        return new { ok = true, state = "signedOut", message = interrupted };
    }

    private static string MusicWebStatus(string service) => service switch
    {
        "apple" => AppleMusicNative.View("status", null, null, 0),
        "spotify" => SpotifyNative.Status,
        _ => YouTubeMusicNative.View("status", null, null, 0),
    };

    /// The page's own app has booted in this document (its status says ready): a page read before that answers "signed out".
    private bool _musicWebBooted;
    /// How long a press waits on a page loading, on its app booting, and then on the song it comes back with.
    private static readonly TimeSpan MusicWebLoadLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MusicWebBootLimit = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MusicWebSongLimit = TimeSpan.FromSeconds(8);

    /// What a page or an action needs first: the web player up. The idle release closes it, and the room's player stays on
    /// screen meanwhile, so the next read or press finds no page: it loads it and waits, as a press on a running page would.
    /// False only when it can't come (another service chosen, the window closing, a load that failed: the room says why).
    private async Task<bool> MusicWebUpAsync(string service)
    {
        if (_musicWebReady && _musicWebBooted && _musicWeb?.CoreWebView2 is not null) return true;
        bool Gone() => _musicWebService != service || _musicWebClosed || _musicWebFailed;
        if (Gone()) return false;
        if (_musicWeb is null || _musicWebInit is null)
        {
            OpenMusicWeb(service);
            if (_musicWeb is null) return false;
        }
        if (!await MusicWebPolicy.WaitUntilAsync(() => _musicWebReady, Gone, MusicWebLoadLimit, _lifetimeCts.Token)) return false;
        var booted = await MusicWebPolicy.WaitUntilAsync(async () =>
        {
            if (_musicWebBooted) return true;
            var status = await WebMusicCallAsync(service, MusicWebStatus(service), attempts: 1, timeout: TimeSpan.FromSeconds(6));
            return status is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True;
        }, Gone, MusicWebBootLimit, _lifetimeCts.Token);
        // Not booted in time: the call goes on and says what it finds.
        if (booted) _musicWebBooted = true;
        return !Gone();
    }

    /// A music page is rows of tracks; past this it is a page script gone wrong, not data for the room.
    private const int MaxMusicPageCharacters = 4 * 1024 * 1024;

    internal async Task<object> WebMusicViewAsync(string service, string? view, string? uri, string? query, int offset)
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
        var data = await WebMusicCallAsync(service, apple
            ? AppleMusicNative.View(view, uri, query, offset) : YouTubeMusicNative.View(view, uri, query, offset));
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

    /// context is the list or page a song plays in (ytm:list:…, am:albums:…);
    /// query is the Songs page's order and filter a song plays in.
    internal async Task<object> WebMusicActAsync(string service, string? action, string? uri, string? context, int? index, bool? on, string? uid,
        string? query, string? text = null)
    {
        var apple = service == "apple";
        // A queue or catalog row has no uid of its own: the room sends it empty.
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
        // Every action is a click the user just made in Exo's room.
        // Woken first by a read; the action then goes once, never repeated: a call that timed out may still have run, and sent
        // again a like, a shuffle or a new playlist would be done twice.
        if (await WebMusicCallAsync(service, "1", timeout: TimeSpan.FromSeconds(2)) is null)
            return new { ok = false, message = label + " did not take that. Try again." };
        var reply = await WebMusicCallAsync(service, apple
            ? AppleMusicNative.Act(action, uri, context, index, on, uid, query, text) : YouTubeMusicNative.Act(action, uri, context, index, on, uid, query, text), attempts: 1, click: apple);
        if (reply is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return new { ok = true };
        if (reply is { ValueKind: JsonValueKind.Object } failed && failed.TryGetProperty("error", out var error))
            AppLog.Warn(label + " room " + action + ": " + error.GetString());
        return new { ok = false, message = label + " did not take that. Try again." };
    }

    /// Until when the hidden page may open a sign-in window without a click
    /// inside it: Exo's own Sign in button was the click.
    private DateTime _musicSignInUntil;
    private Window? _musicSignInWindow;

    /// Set when a sign-in window leaves its page, shown once on the room's next signed-out read.
    private (string Service, string Message)? _musicSignInFailure;

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

    /// Google's own sign-in for YouTube, continuing to music.youtube.com.
    private const string YouTubeSignInUri =
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    /// Spotify's own sign-in, continuing to its web player. Its Log in page offers Google, Apple and Facebook too.
    private const string SpotifySignInUri = "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F";

    private bool MusicSignInRequested() => DateTime.UtcNow < _musicSignInUntil;

    /// Signed in to YouTube without Premium (the last status read): Sign in opens Google's account chooser.
    private bool _youTubeNeedsPremium;

    /// Google's account chooser for YouTube, to pick or add a Premium account, continuing to music.youtube.com.
    private const string YouTubeChooseAccountUri =
        "https://accounts.google.com/AccountChooser?service=youtube&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    /// The room's Sign in button. Apple Music: the hidden page's own MusicKit
    /// authorize(), which opens Apple's sign-in window. YouTube Music: Google's
    /// sign-in page in Exo's window; when it lands back on music.youtube.com
    /// the window closes and the hidden page reloads signed in; Spotify's web
    /// player does the same from accounts.spotify.com. The user signs in inside
    /// the provider's own page; Exo reads nothing from it.
    internal async Task<object> MusicSignInAsync(string? service)
    {
        if (service is null || !MusicWebPolicy.IsService(service) || _musicWebService != service || !_musicWebReady)
            return new { ok = false, message = "The player is still opening. Try again." };
        var label = MusicWebPolicy.Label(service);
        if (_musicSignInWindow is { } open)
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
        var view = new WebView2 { DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0) };
        var window = CreateExoWebPopup(service, view);
        _musicSignInWindow = window;
        _musicPopouts[view] = window;
        var closed = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            if (ReferenceEquals(_musicSignInWindow, window)) _musicSignInWindow = null;
            CancelConnectedPermissions(view);
            _musicPopouts.Remove(view);
            try { view.Close(); } catch { }
        };
        ForegroundWatcher.Asked(label + "'s sign-in window");
        window.Activate();
        try
        {
            var environment = await WebViewEnvironmentFactory.GetAsync();
            await view.EnsureCoreWebView2Async(environment, WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
            if (closed || _musicWebService != service || generation != _musicWebGeneration)
            {
                window.Close();
                return new { ok = false, message = label + " could not start sign-in. Try again." };
            }
            var core = view.CoreWebView2;
            var finished = false;
            // Back on the player: signed in. The hidden page reloads to pick it up.
            core.NavigationStarting += (_, e) => UiGuard.Run(() =>
            {
                if (finished || !MusicWebPolicy.IsPlayerDocument(service, e.Uri)) return;
                finished = true;
                e.Cancel = true;
                DispatcherQueue.Enqueue(() =>
                {
                    try { window.Close(); } catch { }
                    if (_musicWebService == service && generation == _musicWebGeneration) ReloadMusic();
                });
            });
            ConfigureMusicCore(core, environment, view, service, popup: true);
            core.WindowCloseRequested += (_, _) => UiGuard.Run(() => window.Close());
            core.Navigate(service == "spotify" ? SpotifySignInUri : _youTubeNeedsPremium ? YouTubeChooseAccountUri : YouTubeSignInUri);
            return new { ok = true };
        }
        catch (Exception error)
        {
            AppLog.Warn(label + " sign-in window could not open: " + error.GetType().Name);
            try { window.Close(); } catch { }
            return new { ok = false, message = label + " could not start sign-in. Try again." };
        }
    }

    /// Apple's window hands the player its session as it closes: a moment for that, then the page is asked. Landed, the room
    /// opens on its next look, with no reload's wait; not yet, the page reads it fresh (about 6 s). Still signed out well after
    /// that, the room says so instead of sitting on its sign-in screen. A sign-in the room saw land clears MusicSignInRequested.
    private async Task FinishAppleSignInAsync()
    {
        await Task.Delay(1500);
        if (_musicWebService != "apple" || !MusicSignInRequested()) return;
        var status = await WebMusicCallAsync("apple", AppleMusicNative.View("status", null, null, 0), attempts: 2);
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

    /// A suspended page answers nothing until it resumes: wake it and retry.
    private async Task<JsonElement?> WebMusicCallAsync(string service, string expression, int attempts = 5, bool click = false, TimeSpan? timeout = null)
    {
        for (var attempt = 0; attempt < attempts && !_lifetimeCts.IsCancellationRequested; attempt++)
        {
            if (_musicWebService != service || !_musicWebReady || _musicWeb?.CoreWebView2 is not { } core) return null;
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
