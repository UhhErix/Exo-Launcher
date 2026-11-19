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
/// - the list grows back towards a filter list (it was 24,921 rules over 315,626 domains);
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

    /// Apple's sign-in comes back as a query (cid, and ct and at on the way out) that its own page hands to MusicKit, and the
    /// page's API calls are queries too. Exo Shield only blocks; nothing of Apple's may be blocked but its usage reports.
    [Theory]
    [InlineData("https://authorize.music.apple.com/woa?cid=1")]
    [InlineData("https://music.apple.com/us/search?term=a")]
    [InlineData("https://amp-api.music.apple.com/v1/catalog/us/search?term=a")]
    [InlineData("https://idmsa.apple.com/appleauth/auth/signin")]
    [InlineData("https://buy.music.apple.com/account/web/info")]
    public void ExoShieldLeavesApplesSignInAndApiAlone(string uri)
    {
        var host = new Uri(uri).Host;
        foreach (var rule in Rules())
        {
            var condition = rule.GetProperty("condition");
            if (Strings(condition, "requestDomains").Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)))
                Assert.Fail("rule " + rule.GetProperty("id").GetInt32() + " blocks " + host);
            if (condition.TryGetProperty("urlFilter", out var filter) && uri.Contains(filter.GetString()!.Trim('|'), StringComparison.Ordinal))
                Assert.Fail("rule " + rule.GetProperty("id").GetInt32() + " blocks " + uri);
        }
    }
}
