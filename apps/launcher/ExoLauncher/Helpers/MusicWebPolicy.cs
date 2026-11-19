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
}
