using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo Shield (built by shield/build.mjs from shield/rules) could fail:
/// - a rule the browser rejects (a bad filter, a duplicate or missing id, an unknown key): Chromium drops the whole ruleset
///   when it loads the extension, so nothing is blocked and nothing says so;
/// - a rule that reaches past the three provider pages (no initiatorDomains, or another site in it), turning Exo Shield
///   back into a general-purpose blocker for the sign-in windows and every page the profile opens;
/// - a rule that rewrites a query instead of blocking: on apple.com that loses what Apple's sign-in comes back with, so the
///   window closes "signed in" and the player stays signed out;
/// - an ad or tracker address a provider's page calls goes through (a rule dropped, or its pattern no longer matches), or a
///   rule blocks what a player needs to play, keep its history or sign in;
/// - the list grows back towards a filter list (the uBlock Origin Lite build was 24,661 rules over 315,626 domains);
/// - a background page or service worker comes back: its own process, about 56 MB, and seconds at every start;
/// - the shipped folder is not what rules/ builds (someone edited one side), or ships a file the manifest never loads;
/// - the YouTube Music script loses an ad field or stops running before the page's own code (document_start, MAIN).
/// </summary>
public sealed class ExoShieldTests
{
    private static readonly string[] Providers = ["music.youtube.com", "open.spotify.com", "music.apple.com"];
    private const int MaxRules = 100;

    private static string Launcher()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "shield", "rules")) &&
                Directory.Exists(Path.Combine(dir.FullName, "ExoLauncher", "Assets", "Extensions", "exo-shield")))
                return dir.FullName;
        throw new DirectoryNotFoundException("shield/rules is not in the repository.");
    }

    private static string Shipped(string file = "") =>
        Path.Combine(Launcher(), "ExoLauncher", "Assets", "Extensions", "exo-shield", file);

    private static JsonElement Json(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static JsonElement[] Rules() => Json(Shipped("rules.json")).EnumerateArray().ToArray();

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];

    [Fact]
    public void EveryRuleIsAValidBlockOnTheProviderPagesOnly()
    {
        var rules = Rules();
        Assert.InRange(rules.Length, 1, MaxRules);
        Assert.Equal(Enumerable.Range(1, rules.Length), rules.Select(rule => rule.GetProperty("id").GetInt32()));
        var hostLike = new Regex("^[a-z0-9-]+(\\.[a-z0-9-]+)+$");
        foreach (var rule in rules)
        {
            var id = "rule " + rule.GetProperty("id").GetInt32();
            Assert.Equal(["action", "condition", "id", "priority"], rule.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("block", rule.GetProperty("action").GetProperty("type").GetString());
            Assert.Single(rule.GetProperty("action").EnumerateObject());
            var condition = rule.GetProperty("condition");
            foreach (var key in condition.EnumerateObject().Select(p => p.Name))
                Assert.True(key is "initiatorDomains" or "requestDomains" or "urlFilter", id + " has " + key);
            var from = Strings(condition, "initiatorDomains");
            Assert.NotEmpty(from);
            Assert.All(from, site => Assert.Contains(site, Providers));
            var domains = Strings(condition, "requestDomains");
            var hasFilter = condition.TryGetProperty("urlFilter", out var filter);
            Assert.True(domains.Length > 0 ^ hasFilter, id + " needs either requestDomains or a urlFilter");
            Assert.All(domains, domain => Assert.Matches(hostLike, domain));
            if (hasFilter)
            {
                var text = filter.GetString()!;
                // Chromium's urlFilter: ASCII, not starting with ||*, and | only as an anchor at either end.
                Assert.Matches("^(\\|\\|?)?[\\x21-\\x7e]+$", text);
                Assert.DoesNotContain("|", text.TrimStart('|').TrimEnd('|'));
                Assert.False(text.StartsWith("||*", StringComparison.Ordinal), id);
                Assert.True(text.Length >= 8, id + " is too broad: " + text);
            }
        }
    }

    [Fact]
    public void TheExtensionHasNoBackgroundAndLoadsOnlyTheProviderPages()
    {
        var manifest = Json(Shipped("manifest.json"));
        Assert.Equal(3, manifest.GetProperty("manifest_version").GetInt32());
        Assert.False(manifest.TryGetProperty("background", out _), "A background service worker is its own process.");
        Assert.False(manifest.TryGetProperty("host_permissions", out _));
        Assert.Equal(["declarativeNetRequest"], Strings(manifest, "permissions"));
        var loaded = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        foreach (var resource in manifest.GetProperty("declarative_net_request").GetProperty("rule_resources").EnumerateArray())
            loaded.Add(resource.GetProperty("path").GetString()!);
        foreach (var script in manifest.GetProperty("content_scripts").EnumerateArray())
        {
            Assert.All(Strings(script, "matches"), match =>
                Assert.Contains(Providers, site => match == "https://" + site + "/*"));
            foreach (var file in Strings(script, "js").Concat(Strings(script, "css"))) loaded.Add(file);
            if (Strings(script, "js").Length > 0)
            {
                Assert.Equal("MAIN", script.GetProperty("world").GetString());
                Assert.Equal("document_start", script.GetProperty("run_at").GetString());
            }
        }
        var files = Directory.EnumerateFiles(Shipped(), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Shipped(), path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("_metadata/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(loaded.Order(StringComparer.Ordinal), files.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheShippedCopyIsWhatRulesBuilds()
    {
        var source = Path.Combine(Launcher(), "shield", "rules");
        foreach (var file in new[] { "youtube-music.js", "youtube-music.css", "spotify.css" })
            Assert.Equal(File.ReadAllText(Path.Combine(source, file)).ReplaceLineEndings("\n"), File.ReadAllText(Shipped(file)));
        // build.mjs makes one rule per group's domain list and one per url pattern, in order.
        var expected = new List<(string[] From, string[] Domains, string? Url)>();
        foreach (var group in Json(Path.Combine(source, "network.json")).GetProperty("groups").EnumerateArray())
        {
            var from = Strings(group, "from").Order(StringComparer.Ordinal).ToArray();
            if (Strings(group, "domains") is { Length: > 0 } domains) expected.Add((from, domains.Order(StringComparer.Ordinal).ToArray(), null));
            foreach (var url in Strings(group, "urls")) expected.Add((from, [], url));
        }
        var rules = Rules();
        Assert.Equal(expected.Count, rules.Length);
        for (var i = 0; i < rules.Length; i++)
        {
            var condition = rules[i].GetProperty("condition");
            Assert.Equal(expected[i].From, Strings(condition, "initiatorDomains"));
            Assert.Equal(expected[i].Domains, Strings(condition, "requestDomains"));
            Assert.Equal(expected[i].Url, condition.TryGetProperty("urlFilter", out var url) ? url.GetString() : null);
        }
    }

    [Fact]
    public void YouTubeMusicLosesEveryAdField()
    {
        var script = File.ReadAllText(Shipped("youtube-music.js"));
        foreach (var field in new[] { "adPlacements", "adSlots", "playerAds", "adBreakHeartbeatParams" })
            Assert.Contains("'" + field + "'", script);
        Assert.Contains("JSON.parse = new Proxy", script);
        Assert.Contains("Response.prototype.json = new Proxy", script);
    }

    // The ad and tracker addresses each provider's page is known to call, one or more per rule. A rule with no address here,
    // or an address no rule blocks, fails: the list and the rules move together (shield/README.md says how to find a new one).
    public static TheoryData<string, string> KnownAdsAndTrackers => new()
    {
        { "music.youtube.com", "https://googleads.g.doubleclick.net/pagead/id" },
        { "music.youtube.com", "https://static.doubleclick.net/instream/ad_status.js" },
        { "music.youtube.com", "https://pagead2.googlesyndication.com/pagead/managed/js/adsense/m202611180101/show_ads_impl.js" },
        { "music.youtube.com", "https://www.googleadservices.com/pagead/conversion/1001680686/?label=followon_view" },
        { "music.youtube.com", "https://www.googletagmanager.com/gtag/js?id=G-1" },
        { "music.youtube.com", "https://www.google-analytics.com/g/collect?v=2" },
        { "music.youtube.com", "https://www.youtube.com/pagead/adview?ai=x&sigh=y" },
        { "music.youtube.com", "https://www.youtube.com/pagead/viewthroughconversion/962985656/?backend=innertube" },
        { "music.youtube.com", "https://www.youtube.com/api/stats/ads?ver=2&ns=1&event=2" },
        { "music.youtube.com", "https://www.youtube.com/pcs/activeview?xai=x&sig=y" },
        { "music.youtube.com", "https://www.youtube.com/ptracking?ptk=youtube_none&video_id=x" },
        { "music.youtube.com", "https://www.youtube.com/get_midroll_info?ei=x" },
        { "music.youtube.com", "https://music.youtube.com/youtubei/v1/player/ad_break?prettyPrint=false" },
        { "music.youtube.com", "https://music.youtube.com/api/stats/qoe?fmt=251&cpn=x" },
        { "music.youtube.com", "https://music.youtube.com/youtubei/v1/log_event?alt=json" },
        { "music.youtube.com", "https://www.google.com/pagead/1p-user-list/962985656/?backend=innertube" },
        { "open.spotify.com", "https://spclient.wg.spotify.com/ads/v2/config?market=from_token" },
        { "open.spotify.com", "https://spclient.wg.spotify.com/ad-logic/state/config" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/ads/v1/ads/hpto?market=SE" },
        { "open.spotify.com", "https://gue1-spclient.spotify.com/ad-logic/flashpoint" },
        { "open.spotify.com", "https://open.spotifycdn.com/cdn/js/retargeting-pixels.57b4a3b1.js" },
        { "open.spotify.com", "https://pixel.spotify.com/v2/sync?ce=1" },
        { "open.spotify.com", "https://pixel-static.spotify.com/1.gif" },
        { "open.spotify.com", "https://www.googletagmanager.com/gtm.js?id=GTM-1" },
        { "open.spotify.com", "https://stats.g.doubleclick.net/g/collect" },
        { "open.spotify.com", "https://region1.google-analytics.com/g/collect" },
        { "open.spotify.com", "https://connect.facebook.net/en_US/fbevents.js" },
        { "open.spotify.com", "https://analytics.tiktok.com/i18n/pixel/events.js" },
        { "open.spotify.com", "https://sc-static.net/scevent.min.js" },
        { "open.spotify.com", "https://tr.snapchat.com/p" },
        { "open.spotify.com", "https://bat.bing.com/bat.js" },
        { "open.spotify.com", "https://static.ads-twitter.com/uwt.js" },
        { "open.spotify.com", "https://px.ads.linkedin.com/collect" },
        { "open.spotify.com", "https://o22381.ingest.sentry.io/api/5/envelope/" },
        { "open.spotify.com", "https://cdn.cookielaw.org/scripttemplates/otSDKStub.js" },
        { "open.spotify.com", "https://geolocation.onetrust.com/cookieconsentpub/v1/geo/location" },
        { "music.apple.com", "https://xp.apple.com/report/2/xp_amp_web_perf" },
    };

    // What the players need: playback, history, sign-in, the APIs Exo's own player reads, art. None may be blocked, from the
    // provider's page or any other. Apple's sign-in comes back as a query (cid, and ct and at on the way out) that its page
    // hands to MusicKit; YouTube Music's history is watchtime and playback; Spotify's plays and Connect run through spclient.
    public static TheoryData<string, string> NeededByThePlayers => new()
    {
        { "music.youtube.com", "https://music.youtube.com/youtubei/v1/player?prettyPrint=false" },
        { "music.youtube.com", "https://music.youtube.com/youtubei/v1/next?prettyPrint=false" },
        { "music.youtube.com", "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false" },
        { "music.youtube.com", "https://music.youtube.com/api/stats/watchtime?ns=yt&el=detailpage&cpn=x" },
        { "music.youtube.com", "https://music.youtube.com/api/stats/playback?ns=yt&el=detailpage&cpn=x" },
        { "music.youtube.com", "https://rr3---sn-4g5lznlz.googlevideo.com/videoplayback?expire=1&itag=251" },
        { "music.youtube.com", "https://jnn-pa.googleapis.com/$rpc/google.internal.waa.v1.Waa/Create" },
        { "music.youtube.com", "https://www.youtube.com/s/player/6e1dd460/player_ias.vflset/en_US/base.js" },
        { "music.youtube.com", "https://music.youtube.com/sw.js" },
        { "music.youtube.com", "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg" },
        { "music.youtube.com", "https://lh3.googleusercontent.com/a/x=s88" },
        { "music.youtube.com", "https://accounts.google.com/ServiceLogin?service=youtube" },
        { "music.youtube.com", "https://www.google.com/recaptcha/api.js" },
        { "open.spotify.com", "https://apresolve.spotify.com/?type=dealer&type=spclient" },
        { "open.spotify.com", "https://gew4-dealer.spotify.com/?access_token=x" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/connect-state/v1/devices/hobs_x" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/track-playback/v1/devices" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/melody/v1/msg/batch" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/storage-resolve/v2/files/audio/interactive/10/x" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/gabo-receiver-service/v3/events/" },
        { "open.spotify.com", "https://gew4-spclient.spotify.com/playlist/v2/playlist/37i9dQZF1DXcBWIGoYBM5M" },
        { "open.spotify.com", "https://api-partner.spotify.com/pathfinder/v2/query" },
        { "open.spotify.com", "https://api.spotify.com/v1/me/player" },
        { "open.spotify.com", "https://open.spotifycdn.com/cdn/build/web-player/web-player.4f3a1b2c.js" },
        { "open.spotify.com", "https://audio-ak-spotify-com.akamaized.net/audio/x?__token__=y" },
        { "open.spotify.com", "https://i.scdn.co/image/ab67616d0000b273x" },
        { "open.spotify.com", "https://accounts.spotify.com/en/login" },
        { "music.apple.com", "https://authorize.music.apple.com/woa?cid=1" },
        { "music.apple.com", "https://music.apple.com/us/search?term=a" },
        { "music.apple.com", "https://amp-api.music.apple.com/v1/catalog/us/search?term=a" },
        { "music.apple.com", "https://amp-api-edge.music.apple.com/v1/me/library/songs" },
        { "music.apple.com", "https://idmsa.apple.com/appleauth/auth/signin" },
        { "music.apple.com", "https://buy.music.apple.com/account/web/info" },
        { "music.apple.com", "https://play.itunes.apple.com/WebObjects/MZPlay.woa/wa/webPlayback" },
        { "music.apple.com", "https://aod-ssl.itunes.apple.com/itunes-assets/HLSMusic/x/P1.m3u8" },
        { "music.apple.com", "https://js-cdn.music.apple.com/musickit/v3/musickit.js" },
        { "music.apple.com", "https://is1-ssl.mzstatic.com/image/thumb/Music/x/600x600bb.jpg" },
    };

    [Theory]
    [MemberData(nameof(KnownAdsAndTrackers))]
    public void EveryKnownAdAndTrackerOfAProviderIsBlocked(string page, string url) =>
        Assert.True(Blocking(page, url).Length > 0, url + " from " + page + " goes through");

    [Theory]
    [MemberData(nameof(NeededByThePlayers))]
    public void WhatThePlayersNeedGoesThrough(string page, string url)
    {
        foreach (var from in Providers.Append(page).Append("accounts.google.com").Append("idmsa.apple.com"))
        {
            var blocking = Blocking(from, url);
            Assert.True(blocking.Length == 0, "rule " + string.Join(", ", blocking) + " blocks " + url + " from " + from);
        }
    }

    [Fact]
    public void EveryRuleBlocksAKnownAddress()
    {
        var used = KnownAdsAndTrackers.SelectMany(row => Blocking(row.Data.Item1, row.Data.Item2)).ToHashSet();
        var unused = Rules().Select(rule => rule.GetProperty("id").GetInt32()).Where(id => !used.Contains(id)).ToArray();
        Assert.True(unused.Length == 0, "No known address for rule " + string.Join(", ", unused) + ": add one to KnownAdsAndTrackers.");
    }

    [Fact]
    public void PagesOutsideTheThreePlayersAreLeftAlone()
    {
        foreach (var row in KnownAdsAndTrackers)
            foreach (var page in new[] { "www.youtube.com", "accounts.google.com", "accounts.spotify.com", "idmsa.apple.com", "example.com" })
                Assert.Empty(Blocking(page, row.Data.Item2));
    }

    /// The ids of the rules that block a request for `url` made by a page on `initiator`, matched as Chromium's
    /// declarativeNetRequest does for the conditions Exo Shield uses: requestDomains and initiatorDomains take the host and
    /// its subdomains, and urlFilter is case-insensitive, with || anchoring at a host label, | at either end, ^ a separator
    /// (or the end) and * anything.
    private static int[] Blocking(string initiator, string url)
    {
        var host = new Uri(url).Host;
        static bool Under(string name, IEnumerable<string> domains) =>
            domains.Any(domain => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal));
        return Rules().Where(rule =>
        {
            var condition = rule.GetProperty("condition");
            if (!Under(initiator, Strings(condition, "initiatorDomains"))) return false;
            var domains = Strings(condition, "requestDomains");
            if (domains.Length > 0 && !Under(host, domains)) return false;
            return !condition.TryGetProperty("urlFilter", out var filter) || UrlFilter(filter.GetString()!).IsMatch(url);
        }).Select(rule => rule.GetProperty("id").GetInt32()).ToArray();
    }

    private static Regex UrlFilter(string filter)
    {
        var pattern = new System.Text.StringBuilder();
        var body = filter;
        if (body.StartsWith("||", StringComparison.Ordinal)) { pattern.Append(@"^[a-z][a-z0-9+.-]*://([^/?#@]*@)?([^/?#]*\.)?"); body = body[2..]; }
        else if (body.StartsWith('|')) { pattern.Append('^'); body = body[1..]; }
        var end = body.EndsWith('|');
        if (end) body = body[..^1];
        foreach (var c in body)
            pattern.Append(c switch { '*' => ".*", '^' => @"([^a-z0-9_\-.%]|$)", _ => Regex.Escape(c.ToString()) });
        if (end) pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
