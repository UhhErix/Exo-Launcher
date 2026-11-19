using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Picks the fastest of the CDN hosts Epic lists for a game, by measurement, for Legendary's own --preferred-cdn.
/// Legendary takes the first host Epic lists, and Epic's hosts differ by 2-4x on the same line. Exo reads the game's
/// manifest list the way Legendary does before every install (Epic's assets API, Legendary's sign-in), then times the
/// first 256 KB of that manifest from each listed host at once, with no sign-in, within 2 seconds. The ranking holds
/// for 6 hours, so most installs measure nothing. Any failure returns null and Legendary picks as it always has.
/// </summary>
internal static class EpicCdn
{
    private const string ManifestApi = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows";
    // Legendary 0.21.1's own User-Agent (legendary/api/egs.py), so Epic sees the same request Legendary makes.
    private const string LegendaryUserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";
    private const int ProbeBytes = 256 * 1024;
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(2), ListBudget = TimeSpan.FromSeconds(5);
    private static readonly CdnRankingCache Ranking = new(TimeSpan.FromHours(6), () => DateTimeOffset.UtcNow);
    // Neither client follows a redirect: the sign-in never leaves Epic's API host, and a probe never reaches a host
    // NetworkDestinations does not list.
    private static readonly HttpClient Api = Create();
    private static readonly HttpClient Probe = Create();

    private static HttpClient Create()
    {
        var http = ExoHttp.Create(ListBudget, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        });
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LegendaryUserAgent);
        return http;
    }

    /// <summary>Legendary's install or update arguments with the fastest host, workers and shared memory for this PC.</summary>
    internal static async Task<string[]> TuneAsync(string[] args, string appName, CancellationToken ct) =>
        LegendaryCli.WithDownloadOptions(args, await FastestHostAsync(appName, ct).ConfigureAwait(false),
            DownloadTuning.Workers(), DownloadTuning.LegendarySharedMemoryMiB());

    internal static async Task<string?> FastestHostAsync(string appName, CancellationToken ct)
    {
        if (Ranking.Best() is { } cached) return cached;
        try
        {
            var urls = await ManifestUrlsAsync(appName, ct).ConfigureAwait(false);
            if (urls.Count == 0) return null;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ProbeBudget);
            var probes = await Task.WhenAll(urls.Select(url => TimeAsync(url, budget.Token))).ConfigureAwait(false);
            var ranking = CdnRanking.Rank(probes);
            Ranking.Store(ranking);
            AppLog.Info(ranking.Count == 0
                ? "Epic CDN: no host answered in time; Legendary picks."
                : $"Epic CDN: {string.Join(", ", probes.Where(p => p.Elapsed is not null).OrderBy(p => p.Elapsed).Select(p => $"{p.Host} {p.Elapsed!.Value.TotalMilliseconds:0} ms"))}.");
            return ranking.FirstOrDefault();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Debug("Epic CDN: not measured: " + ex.GetType().Name);
            return null;
        }
    }

    private static async Task<IReadOnlyList<Uri>> ManifestUrlsAsync(string appName, CancellationToken ct)
    {
        if (CatalogIds(appName) is not { } ids) return [];
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ListBudget);
        if (await EpicPlaytime.ResolveSessionAsync(budget.Token).ConfigureAwait(false) is not { } session) return [];
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ManifestApi}/namespace/{Uri.EscapeDataString(ids.Namespace)}/catalogItem/{Uri.EscapeDataString(ids.Item)}/app/{Uri.EscapeDataString(appName)}/label/Live");
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", session.AccessToken);
        using var response = await Api.SendAsync(request, budget.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];
        return ParseManifestUrls(await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false));
    }

    /// <summary>
    /// The manifest URL per listed host from Epic's assets answer, with its query parameters, as Legendary builds them
    /// (core.get_cdn_urls); only https hosts NetworkDestinations lists for Epic's downloads, one URL per host.
    /// </summary>
    internal static IReadOnlyList<Uri> ParseManifestUrls(string json)
    {
        var urls = new List<Uri>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array ||
                elements.GetArrayLength() != 1 || !elements[0].TryGetProperty("manifests", out var manifests) ||
                manifests.ValueKind != JsonValueKind.Array) return urls;
            foreach (var manifest in manifests.EnumerateArray())
            {
                if (!manifest.TryGetProperty("uri", out var uri) || uri.GetString() is not { } text) continue;
                if (manifest.TryGetProperty("queryParams", out var query) && query.ValueKind == JsonValueKind.Array)
                    text += "?" + string.Join('&', query.EnumerateArray().Select(p =>
                        $"{p.GetProperty("name").GetString()}={p.GetProperty("value").GetString()}"));
                if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort &&
                    IsDownloadHost(url.Host) && !urls.Any(seen => seen.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase)))
                    urls.Add(url);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { urls.Clear(); }
        return urls;
    }

    internal static bool IsDownloadHost(string host) =>
        NetworkDestinations.Hosts.Any(d => d.PrivacyRow == NetworkDestinations.EpicDownloads && NetworkDestinations.Covers(d.Host, host));

    private static async Task<(string Host, TimeSpan? Elapsed)> TimeAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);
            var clock = Stopwatch.StartNew();
            using var response = await Probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (url.Host, null);
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            for (var read = 0; read < ProbeBytes;)
            {
                var n = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ProbeBytes - read)), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            return (url.Host, clock.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return (url.Host, null);
        }
    }

    /// <summary>The game's namespace and catalog item id from Legendary's own metadata, as Legendary reads them.</summary>
    private static (string Namespace, string Item)? CatalogIds(string appName)
    {
        if (EpicPlaytime.LegendaryUserPath() is not { } user || appName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(user)!, "metadata", appName + ".json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
            var ns = meta.TryGetProperty("namespace", out var n) ? n.GetString() : null;
            var item = meta.TryGetProperty("id", out var i) ? i.GetString() : null;
            return string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(item) ? null : (ns!, item!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }
}
