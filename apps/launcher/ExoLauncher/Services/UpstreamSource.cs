namespace ExoLauncher.Services;

/// <summary>
/// Where a file in Exo's cache came from. Exo's cache (<see cref="VerifiedGitHubReleaseDownloader.CacheHost"/>) keeps each
/// upstream path at the same path under <c>/v1/cache/</c>; upscaler and frame generation downloads ask the upstream copy first
/// (GitHub, DLSS Swapper's download host) so a new release reaches the player the day it ships, and use the cache only when the
/// upstream answer fails (offline, rate limited, moved). Both copies pass the same checks after the download (size, SHA-256 or
/// git blob hash, the vendor's signature), so where the bytes came from never decides whether they are used.
/// </summary>
internal static class UpstreamSource
{
    internal const string GitHubApi = "api.github.com";
    internal const string GitHubRaw = "raw.githubusercontent.com";
    internal const string GitHub = "github.com";
    internal const string SwapperHost = "dlss-swapper-downloads.beeradmoore.com";

    /// <summary>The hosts a GitHub release download redirects to. A redirect anywhere else is refused.</summary>
    internal static readonly string[] ReleaseAssetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    private static readonly string CachePrefix = $"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/";

    /// <summary>
    /// The upstream URL an Exo cache URL is a copy of, or null when the path is Exo's own (the DLSS Swapper catalog Exo builds,
    /// Exo's own releases) or not one Exo maps.
    /// </summary>
    internal static Uri? UpstreamOf(string cacheUrl)
    {
        if (!cacheUrl.StartsWith(CachePrefix, StringComparison.Ordinal)) return null;
        var rest = cacheUrl[CachePrefix.Length..];
        var query = "";
        var q = rest.IndexOf('?');
        if (q >= 0) { query = rest[q..]; rest = rest[..q]; }
        var parts = rest.Split('/');
        if (parts.Length < 3 || parts.Any(part => part.Length == 0 || part is "." or "..")) return null;

        if (parts[0] == SwapperHost)
            return query.Length == 0 ? Build(SwapperHost, string.Join('/', parts[1..])) : null;
        if (parts[0].Contains('.') || parts[0].Equals("exo", StringComparison.OrdinalIgnoreCase)) return null;

        var repo = parts[0] + "/" + parts[1];
        var tail = parts[2..];
        return tail switch
        {
            ["releases", "latest"] when query.Length == 0 => Build(GitHubApi, $"repos/{repo}/releases/latest"),
            ["commits", _] when query.Length == 0 => Build(GitHubApi, $"repos/{repo}/commits/{tail[1]}"),
            ["contents", ..] when tail.Length > 1 && query.Length == 45 && query.StartsWith("?ref=", StringComparison.Ordinal) && query[5..].All(char.IsAsciiHexDigit) =>
                Build(GitHubApi, $"repos/{repo}/{string.Join('/', tail)}", query),
            ["releases", "download", _, _] when query.Length == 0 => Build(GitHub, $"{repo}/{string.Join('/', tail)}"),
            [var commit, ..] when tail.Length > 1 && query.Length == 0 && commit.Length == 40 && commit.All(char.IsAsciiHexDigit) =>
                Build(GitHubRaw, $"{repo}/{string.Join('/', tail)}"),
            _ => null,
        };
    }

    /// <summary>
    /// A hop an upstream download may take: an upstream URL itself, or GitHub's release asset hosts it redirects to. HTTPS on
    /// the default port only, no credentials in the URL.
    /// </summary>
    internal static bool IsUpstreamHop(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals(GitHubApi, StringComparison.OrdinalIgnoreCase) || uri.Host.Equals(GitHubRaw, StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals(GitHub, StringComparison.OrdinalIgnoreCase) || uri.Host.Equals(SwapperHost, StringComparison.OrdinalIgnoreCase) ||
         ReleaseAssetHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The sources to try for a cache URL, upstream first. The cache is always last, so a download that fails upstream (or
    /// whose bytes fail a check) is tried once more from Exo's copy.
    /// </summary>
    internal static IEnumerable<string> SourcesFor(string cacheUrl)
    {
        if (UpstreamOf(cacheUrl) is { } upstream) yield return upstream.AbsoluteUri;
        yield return cacheUrl;
    }

    /// <summary>
    /// GET with redirects followed by hand (at most three), each hop checked by <paramref name="allowed"/> before it is asked.
    /// The client must have AllowAutoRedirect off. GitHub's API asks for a JSON accept header and a user agent; the client
    /// sets the agent.
    /// </summary>
    internal static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, Func<Uri, bool> allowed, CancellationToken ct)
    {
        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            if (!allowed(current)) throw new InvalidDataException("Exo only downloads the upscaler files it has pinned.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (current.Host.Equals(GitHubApi, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is null) return response;
            var next = new Uri(current, response.Headers.Location);
            response.Dispose();
            if (hop >= 3) throw new InvalidDataException("The download was redirected too often.");
            current = next;
        }
    }

    private static Uri Build(string host, string path, string query = "") => new($"https://{host}/{path}{query}", UriKind.Absolute);
}
