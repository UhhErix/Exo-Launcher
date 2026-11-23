namespace ExoLauncher.Helpers;

/// <summary>
/// Allow-lists for the music web players hosted on WebView2: YouTube Music
/// (music.youtube.com), Apple Music (music.apple.com) and Spotify
/// (open.spotify.com). They run hidden as the engine
/// behind Exo's own player; only their own sign-in pages ever show,
/// in Exo's sign-in window. The view runs the bundled
/// extensions (WebExtensions: Exo Shield, SponsorBlock); Exo
/// always optimizes YouTube Music, which also hides its Premium upsells
/// (EmbeddedClientPresentation).
/// </summary>
internal static class MusicWebPolicy
{
    internal static bool IsService(string? service) =>
        service is "spotify" or "apple" or "youtube";

    /// Provider display label for statuses and errors.
    internal static string Label(string service) => service switch
    {
        "spotify" => "Spotify",
        "apple" => "Apple Music",
        "youtube" => "YouTube Music",
        _ => "Music",
    };

    /// Where a web player starts. Apple Music opens on its search page, the
    /// lightest page of its web player (about 90 MB less than its New page);
    /// it redirects to the user's own storefront.
    internal static string StartUri(string service) => service switch
    {
        "apple" => "https://music.apple.com/search",
        "spotify" => "https://open.spotify.com/",
        _ => "https://music.youtube.com",
    };

    /// Documents that may load inside the player's own surface. Sign-in
    /// continues through the provider's own account hosts inside the player
    /// or its popup.
    internal static bool IsServiceDocument(string? service, string? uri)
    {
        if (!IsService(service) || uri is null) return false;
        if (!IsHttps(uri)) return uri == "about:blank";
        var host = Host(uri);
        if (service == "apple")
            return host is "music.apple.com" or "authorize.music.apple.com" or "idmsa.apple.com" or "appleid.apple.com"
                or "account.apple.com" or "gsa.apple.com" or "setup.icloud.com" or "www.icloud.com";
        if (service == "spotify")
        {
            // Spotify's own account pages and the three companies its sign-in page offers.
            if (host is "open.spotify.com" or "accounts.spotify.com" or "challenge.spotify.com" or
                "accounts.google.com" or "appleid.apple.com" or "www.facebook.com" or "m.facebook.com") return true;
        }
        else
        {
            if (host == "music.youtube.com") return true;
            // The same Google hosts a store sign-in already keeps: a hop onto one of them used to
            // close YouTube Music's window before the account page finished.
            if (host is "accounts.google.com" or "myaccount.google.com" or "consent.google.com" or
                "consent.youtube.com" or "accounts.youtube.com" or "gds.google.com") return true;
            if (YouTubeSignIn(host, PathOf(uri))) return true;
        }
        if (host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = PathOf(uri);
            return path.StartsWith("/accounts", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/recaptcha", StringComparison.OrdinalIgnoreCase);
        }
        return host is "hcaptcha.com" or "recaptcha.net" or "challenges.cloudflare.com" or "arkoselabs.com" or "funcaptcha.com" ||
            host.EndsWith(".hcaptcha.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".recaptcha.net", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".arkoselabs.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".funcaptcha.com", StringComparison.OrdinalIgnoreCase);
    }

    /// YouTube's own sign-in page, including the steps under it. The rest of YouTube stays out.
    private static bool YouTubeSignIn(string host, string path)
    {
        if (host is not ("www.youtube.com" or "youtube.com" or "m.youtube.com")) return false;
        path = path.TrimEnd('/');
        return path.Equals("/signin", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/signin/", StringComparison.OrdinalIgnoreCase);
    }

    private static string PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : "";

    /// A sign-in window may follow the rest of that company's own account hosts. The hidden player stays on
    /// <see cref="IsServiceDocument"/>.
    internal static bool IsSignInHop(string? service, string? uri)
    {
        if (IsServiceDocument(service, uri)) return true;
        if (!IsHttps(uri)) return false;
        var host = Host(uri!);
        if (service == "apple")
            return host.Equals("apple.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".cdn-apple.com", StringComparison.OrdinalIgnoreCase);
        if (service == "spotify")
            return host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// True once the player shows its own app surface.
    internal static bool IsPlayerDocument(string? service, string? uri) =>
        IsService(service) && uri is not null && IsHttps(uri) &&
        Host(uri) == service switch { "apple" => "music.apple.com", "spotify" => "open.spotify.com", _ => "music.youtube.com" };

    /// Waits for what a call needs of a page: true when <paramref name="up"/> says so, false at once when
    /// <paramref name="gone"/> says it can't come, at the limit, or cancelled.
    internal static async Task<bool> WaitUntilAsync(Func<Task<bool>> up, Func<bool> gone, TimeSpan limit, CancellationToken cancellation)
    {
        var started = Environment.TickCount64;
        try
        {
            for (;;)
            {
                if (gone()) return false;
                if (await up()) return true;
                if (Environment.TickCount64 - started >= limit.TotalMilliseconds) return false;
                await Task.Delay(WaitStep, cancellation);
            }
        }
        catch (OperationCanceledException) { return false; }
    }

    internal static Task<bool> WaitUntilAsync(Func<bool> up, Func<bool> gone, TimeSpan limit, CancellationToken cancellation) =>
        WaitUntilAsync(() => Task.FromResult(up()), gone, limit, cancellation);

    private static readonly TimeSpan WaitStep = TimeSpan.FromMilliseconds(100);

    /// Strict https: the default port only, and never a URI carrying
    /// credentials — a userinfo host could smuggle a lookalike past the check.
    internal static bool IsHttps(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps && parsed.Port == 443 &&
        string.IsNullOrEmpty(parsed.UserInfo);

    private static string Host(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : "";

    private static readonly string[] YouTubeAdTrackerDomains =
    [
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "googletagmanager.com",
        "google-analytics.com",
    ];

    private static readonly string[] YouTubeAdPathPrefixes =
    [
        "/pagead/",
        "/api/stats/ads",
        "/pcs/activeview",
        "/ptracking",
        "/get_midroll_",
        "/youtubei/v1/player/ad_break",
        "/api/stats/qoe",
        "/youtubei/v1/log_event",
    ];

    private static readonly string[] SpotifyAdTrackerDomains =
    [
        "pixel.spotify.com",
        "pixel-static.spotify.com",
        "doubleclick.net",
        "googletagmanager.com",
        "google-analytics.com",
        "connect.facebook.net",
        "analytics.tiktok.com",
        "sc-static.net",
        "tr.snapchat.com",
        "bat.bing.com",
        "static.ads-twitter.com",
        "ads.linkedin.com",
        "sentry.io",
        "cookielaw.org",
        "onetrust.com",
    ];

    private static readonly string[] SpotifyVideoCanvasDomains =
    [
        "canvaz.scdn.co",
        "video-fa.scdn.co",
    ];

    private static readonly string[] AppleTrackerDomains =
    [
        "xp.apple.com",
        "metrics.apple.com",
        "metrics.icloud.com",
        "securemetrics.apple.com",
    ];

    private static readonly string[] AppleVideoDomains =
    [
        "mvod.itunes.apple.com",
        "mvod-ssl.itunes.apple.com",
    ];

    internal static IReadOnlyList<(string Pattern, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext Context)> ResourceDietFilterPatterns(string service)
    {
        var list = new List<(string, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext)>
        {
            ("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image),
            ("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Font),
        };
        if (service == "youtube")
        {
            foreach (var domain in YouTubeAdTrackerDomains) AddDomainPatterns(list, domain);
            list.Add(("*://*.youtube.com/pagead/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.youtube.com/api/stats/ads*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.youtube.com/pcs/activeview*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.youtube.com/ptracking*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.youtube.com/get_midroll_*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://music.youtube.com/youtubei/v1/player/ad_break*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://music.youtube.com/api/stats/qoe*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://music.youtube.com/youtubei/v1/log_event*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.google.com/pagead/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
        }
        else if (service == "spotify")
        {
            foreach (var domain in SpotifyAdTrackerDomains) AddDomainPatterns(list, domain);
            foreach (var domain in SpotifyVideoCanvasDomains) AddDomainPatterns(list, domain);
            list.Add(("*://*.spotify.com/ads/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.spotify.com/ad-logic/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://open.spotifycdn.com/cdn/js/retargeting-pixels*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
            list.Add(("*://*.scdn.co/*canvas*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Media));
            list.Add(("*://video*.scdn.co/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
        }
        else if (service == "apple")
        {
            foreach (var domain in AppleTrackerDomains) AddDomainPatterns(list, domain);
            foreach (var domain in AppleVideoDomains) AddDomainPatterns(list, domain);
            list.Add(("*://*.itunes.apple.com/*EditorialVideo*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
        }
        return list;
    }

    internal static bool ShouldBlockHiddenResource(string service, string? uri, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext context, bool popup)
    {
        if (popup || string.IsNullOrEmpty(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("https" or "http")) return false;
        var host = parsed.Host;
        var path = parsed.AbsolutePath;

        if (IsPlaybackCriticalResource(service, host, path)) return false;

        if (context == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Font) return true;

        if (context == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image) return true;

        if (service == "youtube")
        {
            if (MatchesAnyDomain(host, YouTubeAdTrackerDomains)) return true;
            if ((MatchesDomain(host, "youtube.com") || MatchesDomain(host, "google.com"))
                && StartsWithAny(path, YouTubeAdPathPrefixes))
                return true;
            return false;
        }

        if (service == "spotify")
        {
            if (MatchesAnyDomain(host, SpotifyAdTrackerDomains)) return true;
            if (MatchesAnyDomain(host, SpotifyVideoCanvasDomains)) return true;
            if (MatchesDomain(host, "spotify.com")
                && (path.StartsWith("/ads/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/ad-logic/", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (host.Equals("open.spotifycdn.com", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith("/cdn/js/retargeting-pixels", StringComparison.OrdinalIgnoreCase))
                return true;
            if (MatchesDomain(host, "scdn.co")
                && host.StartsWith("video", StringComparison.OrdinalIgnoreCase))
                return true;
            if (context == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Media
                && MatchesDomain(host, "scdn.co")
                && !host.StartsWith("audio", StringComparison.OrdinalIgnoreCase)
                && (path.Contains("/canvas/", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        if (service == "apple")
        {
            if (MatchesAnyDomain(host, AppleTrackerDomains)) return true;
            if (MatchesAnyDomain(host, AppleVideoDomains)) return true;
            if (MatchesDomain(host, "itunes.apple.com")
                && path.Contains("/EditorialVideo/", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        return false;
    }

    private static bool IsPlaybackCriticalResource(string service, string host, string path)
    {
        if (service == "youtube")
        {
            if (MatchesDomain(host, "googlevideo.com")) return true;
            if (host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)
                && (path.StartsWith("/api/stats/watchtime", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/stats/playback", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        else if (service == "spotify")
        {
            if (host.StartsWith("audio", StringComparison.OrdinalIgnoreCase)
                && (MatchesDomain(host, "scdn.co") || MatchesDomain(host, "spotifycdn.com")))
                return true;
        }
        else if (service == "apple")
        {
            if (host.StartsWith("aod", StringComparison.OrdinalIgnoreCase)
                && MatchesDomain(host, "itunes.apple.com"))
                return true;
            if (host.Equals("play.itunes.apple.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("amp-api.music.apple.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("api.music.apple.com", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void AddDomainPatterns(List<(string, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext)> list, string domain)
    {
        list.Add(("*://" + domain + "/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
        list.Add(("*://*." + domain + "/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All));
    }

    private static bool MatchesAnyDomain(string host, string[] domains)
    {
        for (var i = 0; i < domains.Length; i++)
            if (MatchesDomain(host, domains[i])) return true;
        return false;
    }

    private static bool MatchesDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || (host.Length > domain.Length
            && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
            && host[host.Length - domain.Length - 1] == '.');

    private static bool StartsWithAny(string path, string[] prefixes)
    {
        for (var i = 0; i < prefixes.Length; i++)
            if (path.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
