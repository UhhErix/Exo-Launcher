using System.Text.RegularExpressions;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the network registry and PRIVACY.md can drift apart:
/// - a registry entry names a PRIVACY.md row that was renamed or removed;
/// - a PRIVACY.md row has no entry, so nobody knows which code it describes;
/// - new code contacts a host that is in neither list, so users are never told.
/// The mapping is by exact row text, never by fuzzy matching.
/// </summary>
public sealed partial class NetworkDestinationsTests
{
    /// <summary>
    /// https:// hosts written in the host's source that its own code does not
    /// request: pages opened in the user's browser, pages hosted in WebView2
    /// (out of the registry's scope, see NetworkDestinations), Exo's virtual
    /// hosts, and project links in comments.
    /// </summary>
    private static readonly Dictionary<string, string> NotNativeRequests = new(StringComparer.OrdinalIgnoreCase)
    {
        // Store pages and searches opened in the user's browser (Storefront, StoreService.Game, GameKeyActivation).
        ["www.gog.com"] = "browser",
        ["gaming.amazon.com"] = "browser",
        ["store.rockstargames.com"] = "browser",
        ["www.minecraft.net"] = "browser",
        ["www.roblox.com"] = "browser",
        ["www.paradoxinteractive.com"] = "browser",
        // GitHub links kept in comments as licence credits or upstream pages; no code requests them (see NoRuntimeRequestGoesToGitHub).
        ["github.com"] = "project link in a comment",
        ["raw.githubusercontent.com"] = "upstream path named in a comment",
        ["wargaming.com"] = "browser",
        ["itch.io"] = "browser",
        ["playruneterra.com"] = "browser",
        ["tracker.gg"] = "browser (profile link; also the Origin header of api.tracker.gg requests)",
        // A keyboard's own app, or its download page, opened in the user's browser (Peripherals.Apps).
        ["wootility.io"] = "browser",
        ["launcher.keychron.com"] = "browser",
        ["usevia.app"] = "browser",
        ["vial.rocks"] = "browser",
        ["www.razer.com"] = "browser",
        ["www.logitechg.com"] = "browser",
        ["www.logitech.com"] = "browser",
        ["www.corsair.com"] = "browser",
        ["steelseries.com"] = "browser",
        // A drive maker's firmware tool page, opened in the user's browser from PC › Drives (Drives.Vendor; Exo never flashes).
        ["semiconductor.samsung.com"] = "browser",
        ["www.sandisk.com"] = "browser",
        ["www.crucial.com"] = "browser",
        ["www.kingston.com"] = "browser",
        ["www.solidigm.com"] = "browser",
        ["help.corsair.com"] = "browser",
        ["www.seagate.com"] = "browser",
        // The cover cache's Worker (cloud/covers) asks this store search itself; the PC never does.
        ["catalog.gog.com"] = "asked by the cover cache's Worker",
        // GIF hosts Discord's messages and picker point to: Exo's page loads them (Discord's row says so); native code requests none.
        ["klipy.com"] = "page loads GIFs",
        ["tenor.com"] = "page loads GIFs",
        ["giphy.com"] = "page loads GIFs",
        ["i.imgur.com"] = "page loads GIFs",
        // Cloudflare's DoH template, registered with Windows by Best settings › Network: Windows' own DNS client asks it, never Exo's code.
        ["cloudflare-dns.com"] = "Windows' DNS client (CloudflareDns)",
        // Exo's page in Windows' Installed apps entry: written to the registry, opened by Windows, never requested (InstalledAppsEntry).
        ["getexo.app"] = "Installed apps link",
        // Store sign-in pages hosted in WebView2.
        ["auth.gog.com"] = "WebView sign-in",
        // Xbox token issuers named by old sign-in requests; Exo no longer contacts them.
        ["auth.xboxlive.com"] = "relying party name",
        ["xboxlive.com"] = "relying party name",
        ["accounts.google.com"] = "WebView sign-in",
        ["accounts.spotify.com"] = "WebView sign-in",
        // Steam's website in the hidden gifts page and its sign-in window (StoreGifts).
        ["login.steampowered.com"] = "WebView page",
        ["help.steampowered.com"] = "WebView page",
        // Web music players hosted in WebView2, and scripts that run inside Spotify's own page.
        ["music.youtube.com"] = "WebView page",
        ["music.apple.com"] = "WebView page",
        ["spclient.wg.spotify.com"] = "Spotify's page",
        // Exo's own WebView virtual hosts.
        ["app.exo-launcher.local"] = "virtual host",
        ["covers.exo-launcher.local"] = "virtual host",
        ["thumbs.exo-launcher.local"] = "virtual host",
        ["devices.exo-launcher.local"] = "virtual host",
    };

    private static IReadOnlyList<string> PrivacyRows()
    {
        var lines = File.ReadAllLines(Repo.PathOf("PRIVACY.md"));
        var start = Array.FindIndex(lines, line => line.Trim() == "## Other network requests");
        Assert.True(start >= 0, "PRIVACY.md has no \"## Other network requests\" section");
        var rows = new List<string>();
        foreach (var line in lines.Skip(start + 1).SkipWhile(line => !line.StartsWith('|')).TakeWhile(line => line.StartsWith('|')))
        {
            var cell = line.Split('|', StringSplitOptions.TrimEntries)[1];
            if (cell is "Destination" || cell.StartsWith("---", StringComparison.Ordinal)) continue;
            rows.Add(cell);
        }
        Assert.NotEmpty(rows);
        return rows;
    }

    /// <summary>The whole line of each row of the table, by its Destination cell.</summary>
    private static IReadOnlyDictionary<string, string> PrivacyRowLines()
    {
        var lines = File.ReadAllLines(Repo.PathOf("PRIVACY.md"));
        var start = Array.FindIndex(lines, line => line.Trim() == "## Other network requests");
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(start + 1).SkipWhile(line => !line.StartsWith('|')).TakeWhile(line => line.StartsWith('|')))
        {
            var cell = line.Split('|', StringSplitOptions.TrimEntries)[1];
            if (cell is "Destination" || cell.StartsWith("---", StringComparison.Ordinal)) continue;
            rows[cell] = line;
        }
        return rows;
    }

    /// <summary>
    /// Rows of the table that name a service or a kind of server and not each host: the registry lists the hosts behind them,
    /// and a host added to one of these is told to the player by what the row says it sends and when, not by its address.
    /// Every other row must name each of its hosts, so a host added to it without a word in PRIVACY.md fails the build.
    /// </summary>
    private static readonly HashSet<string> RowsThatNameServicesNotHosts = new(StringComparer.Ordinal)
    {
        NetworkDestinations.CoverArt,         // "the image servers a store's own data points to": a class of hosts
        NetworkDestinations.MusicArtwork,     // the services' image servers
        NetworkDestinations.StoreRoom,        // Steam store, Steam Web API and CheapShark by name
        NetworkDestinations.StoreTags,        // IStoreService/GetTagList on the Steam Web API
        NetworkDestinations.SteamAccount,     // the Steam Web API: its host is in the row's own title
        NetworkDestinations.SpotifyShareLinks,
        NetworkDestinations.ExoRelay,         // "Cloudflare Workers": the Worker's address is not the player's to read
        NetworkDestinations.Installers,       // the makers by name; their hosts are in the row where they differ
    };

    /// <summary>Host names written in a row's text (the TLD list is the ones the table uses; file names such as galaxy-2.0.db are not hosts).</summary>
    private static IEnumerable<string> HostsNamedIn(string text) =>
        NamedHost().Matches(text.Replace("`", "")).Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct();

    /// <summary>A host PRIVACY.md writes in a row is one Exo contacts: it is in the registry (or a subdomain of a listed host's parent), or classed as not requested by native code.</summary>
    [Fact]
    public void EveryHostPrivacyNamesIsInTheRegistry()
    {
        var unknown = PrivacyRowLines()
            .SelectMany(row => HostsNamedIn(row.Value).Select(host => (row: row.Key, host)))
            .Where(entry => !NetworkDestinations.IsListed(entry.host) &&
                            !NotNativeRequests.ContainsKey(entry.host) &&
                            !NetworkDestinations.Hosts.Any(listed => listed.Host.EndsWith("." + entry.host, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => $"{entry.host} (row: {entry.row[..Math.Min(40, entry.row.Length)]}...)")
            .Distinct()
            .ToArray();
        Assert.True(unknown.Length == 0, "Hosts PRIVACY.md names that NetworkDestinations does not list: " + string.Join(" | ", unknown));
    }

    /// <summary>The reverse: a host in the registry, on a row that names its hosts, is written in that row.</summary>
    [Fact]
    public void EveryRegistryHostIsNamedInItsPrivacyRow()
    {
        var lines = PrivacyRowLines();
        var silent = NetworkDestinations.Hosts
            .Where(destination => !RowsThatNameServicesNotHosts.Contains(destination.PrivacyRow))
            .Where(destination => lines.TryGetValue(destination.PrivacyRow, out var line) &&
                                  !line.Replace("`", "").Contains(destination.Host.TrimStart('*').TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            .Select(destination => $"{destination.Host} (row: {destination.PrivacyRow[..Math.Min(40, destination.PrivacyRow.Length)]}...)")
            .ToArray();
        Assert.True(silent.Length == 0, "Registry hosts their PRIVACY.md row does not name: " + string.Join(" | ", silent));
    }

    [Fact]
    public void EveryRegistryRowIsInPrivacy()
    {
        var rows = PrivacyRows().ToHashSet(StringComparer.Ordinal);
        var named = NetworkDestinations.Hosts.Select(host => host.PrivacyRow).Concat(NetworkDestinations.RowsWithoutNativeHosts);
        var missing = named.Distinct().Where(row => !rows.Contains(row)).ToArray();
        Assert.True(missing.Length == 0, "Rows not in PRIVACY.md's table: " + string.Join(" | ", missing));
    }

    [Fact]
    public void EveryPrivacyRowIsInTheRegistry()
    {
        var named = NetworkDestinations.Hosts.Select(host => host.PrivacyRow)
            .Concat(NetworkDestinations.RowsWithoutNativeHosts).ToHashSet(StringComparer.Ordinal);
        var missing = PrivacyRows().Where(row => !named.Contains(row)).ToArray();
        Assert.True(missing.Length == 0, "PRIVACY.md rows with no NetworkDestinations entry: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// GitHub is contacted only for the vendors' upscaler files and the frame generation mods (UpstreamSource), each with
    /// Exo's cache behind it, and no code line writes a GitHub URL out (UpstreamSource builds them from its host names).
    /// Ways it could fail: a GitHub host listed for another row (store tools, Lucid, the voice model), or a request string
    /// naming api.github.com, github.com, a raw or release file host, or GitHub Pages. Comment lines are skipped: they name
    /// upstream pages as credits.
    /// </summary>
    [Fact]
    public void GitHubIsAskedOnlyForUpscalersAndFrameGeneration()
    {
        Assert.All(NetworkDestinations.Hosts.Where(host => host.Host.Contains("github", StringComparison.OrdinalIgnoreCase)),
            host => Assert.Contains(host.PrivacyRow, new[] { NetworkDestinations.Upscalers, NetworkDestinations.FrameGenMod }));
        var github = new Regex(@"https?://[A-Za-z0-9.-]*(github\.com|githubusercontent\.com|github\.io)", RegexOptions.IgnoreCase);
        var offenders = Repo.HostSources()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                            !entry.line.TrimStart().StartsWith("/*", StringComparison.Ordinal) &&
                            !entry.line.TrimStart().StartsWith('*') &&
                            github.IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(Repo.Root, entry.path)}:{entry.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Code that requests GitHub: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RowsWithoutNativeHostsReallyHaveNone() =>
        Assert.DoesNotContain(NetworkDestinations.Hosts, host => NetworkDestinations.RowsWithoutNativeHosts.Contains(host.PrivacyRow));

    /// <summary>Exo Shaders' build is local-only: nothing Exo lists may be SCSKiller's servers or Patreon.</summary>
    [Fact]
    public void NothingListedIsSCSKillerOrPatreon() =>
        Assert.DoesNotContain(NetworkDestinations.Hosts, host => host.Host.Contains("scskiller", StringComparison.OrdinalIgnoreCase) ||
                                                                 host.Host.Contains("patreon", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EveryHostInTheSourceIsAccountedFor()
    {
        var unlisted = Repo.HostSources()
            .SelectMany(path => UrlHost().Matches(File.ReadAllText(path)).Select(match => (path, host: match.Groups[1].Value)))
            .Where(entry => entry.host is not ("127.0.0.1" or "localhost") &&
                            !NetworkDestinations.IsListed(entry.host) &&
                            !NotNativeRequests.ContainsKey(entry.host))
            .Select(entry => $"{entry.host} ({Path.GetRelativePath(Repo.Root, entry.path)})")
            .Distinct()
            .ToArray();
        Assert.True(unlisted.Length == 0,
            "Hosts in the source that are neither in NetworkDestinations nor classed as not requested by native code: " +
            string.Join(", ", unlisted));
    }

    /// <summary>
    /// Hosts the host code builds without writing an https:// address (new UriBuilder("https", "host", ...), "https://" + host,
    /// a host kept in a constant) still show up as a quoted host name. Ways it could miss one: a host spelled in pieces, or
    /// kept in a file that is not C#. Comment lines are skipped.
    /// </summary>
    [Fact]
    public void EveryQuotedHostNameInTheSourceIsAccountedFor()
    {
        var unlisted = Repo.HostSources()
            .Where(path => !HostMatcherFiles.ContainsKey(Path.GetFileName(path)))
            .SelectMany(path => File.ReadLines(path).Select(line => (path, line)))
            .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                            !entry.line.TrimStart().StartsWith('*'))
            .SelectMany(entry => QuotedHost().Matches(entry.line).Select(match => (entry.path, host: match.Groups[1].Value.ToLowerInvariant())))
            .Where(entry => entry.host is not ("127.0.0.1" or "localhost") &&
                            !NetworkDestinations.IsListed(entry.host) &&
                            !NotNativeRequests.ContainsKey(entry.host) &&
                            !QuotedHostsThatAreNotRequests.Contains(entry.host))
            .Select(entry => $"{entry.host} ({Path.GetRelativePath(Repo.Root, entry.path)})")
            .Distinct()
            .ToArray();
        Assert.True(unlisted.Length == 0,
            "Quoted host names in the host code that are neither in NetworkDestinations nor classed as not requested: " + string.Join(", ", unlisted));
    }

    /// <summary>Quoted names that look like hosts and are not requested by native code.</summary>
    private static readonly HashSet<string> QuotedHostsThatAreNotRequests = new(StringComparer.OrdinalIgnoreCase)
    {
        "wargaming.net", "booking.com",     // names matched against installed programs and store links, never requested
        "gog.com", "cheapshark.com", "steampowered.com", // suffixes a link or a redirect is matched against (StoreLinks, GogAdapter, PlaytimeService...)
        "embed.gog.com",                    // gogdl's sign-in redirect, which the sign-in window only recognises (GogdlCli)
        "epicgames.dev", "fortnite.com",    // hosts Epic's sign-in window may navigate through (EpicAuthService)
        "play.spotify.com",                 // a link shape SpotifyLinks recognises
        "microsoft.windowsdesktop.app",     // a .NET runtime's name, not a host
    };

    /// <summary>
    /// Files that list hosts to compare a link with or to let a sign-in or music page navigate to, never to request:
    /// look-alike detection, the music pages' navigation rules and the store sign-in window's.
    /// </summary>
    private static readonly Dictionary<string, string> HostMatcherFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LinkLookalike.cs"] = "brand names a link is compared with",
        ["MusicWebPolicy.cs"] = "where the music pages may navigate (WebView pages are out of scope)",
        ["StoreAuthWindow.cs"] = "where the store sign-in windows may navigate (WebView pages are out of scope)",
    };

    [GeneratedRegex(@"""((?:[a-z0-9][a-z0-9-]*\.)+(?:com|net|org|app|gg|gl|io|de|cc|co|ms|media|tv|us|ca|dev))(?=[""/:?])", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedHost();

    [Theory]
    [InlineData("*.steamstatic.com", "cdn.cloudflare.steamstatic.com", true)]
    [InlineData("*.steamstatic.com", "steamstatic.com", true)]
    [InlineData("*.steamstatic.com", "evilsteamstatic.com", false)]
    [InlineData("api.gog.com", "API.GOG.COM", true)]
    [InlineData("api.gog.com", "x.api.gog.com", false)]
    public void WildcardsCoverSubdomainsOnly(string pattern, string host, bool expected) =>
        Assert.Equal(expected, NetworkDestinations.Covers(pattern, host));

    [Theory]
    [InlineData("https://aka.ms/vs/17/release/vc_redist.x64.exe", true)]
    [InlineData("https://download.visualstudio.microsoft.com/x/vc_redist.x64.exe", true)]
    [InlineData("http://download.visualstudio.microsoft.com/x.exe", false)]
    [InlineData("https://download.visualstudio.microsoft.com:8443/x.exe", false)]
    [InlineData("https://cdn.cloudflare.steamstatic.com/x.exe", false)] // listed, but not on the installers row
    [InlineData("https://evil.example/x.exe", false)]
    public void InstallerRedirectsStayOnTheInstallersRow(string url, bool expected) =>
        Assert.Equal(expected, ExoLauncher.Services.DependencyService.IsInstallerHost(new Uri(url)));

    [Theory]
    [InlineData("https://bacon.secure.dyn.riotcdn.net/channels/public/rccontent/theme/x.png", true)]
    [InlineData("http://bacon.secure.dyn.riotcdn.net/x.png", false)]
    [InlineData("https://riotcdn.net.evil.example/x.png", false)]
    public void RiotThemeArtComesOnlyFromItsCdn(string url, bool expected) =>
        Assert.Equal(expected, ExoLauncher.Services.CoverArtService.IsApprovedArtworkDownloadUrl(url));

    [GeneratedRegex(@"https?://([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)")]
    private static partial Regex UrlHost();

    [GeneratedRegex(@"(?<![\w.@-])((?:[a-z0-9][a-z0-9-]*\.)+(?:com|net|org|app|gg|gl|io|de|cc|co|ms|media|dev|tv|us|ca))(?![\w-]|\.[a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NamedHost();
}
