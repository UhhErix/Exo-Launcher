using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

internal static class RiotHoursClient
{
    internal static readonly Uri DefaultEndpoint = new("https://riot.getexo.app/v1/hours");
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(24);
    internal static readonly CoverCacheClient.Backoff SharedBackoff = new();
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    internal sealed record GameReading(
        int Minutes,
        int Matches,
        bool Partial,
        string Source,
        IReadOnlyDictionary<string, int>? Modes,
        DateTimeOffset? LatestMatchUtc);

    internal sealed record Result(
        bool Ok,
        bool Unconfigured,
        bool NotFound,
        bool TransientFailure,
        IReadOnlyDictionary<string, GameReading> Readings,
        string Message)
    {
        internal Dictionary<string, GameSeed> ToSeeds()
        {
            var seeds = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
            foreach (var (game, reading) in Readings)
            {
                if (reading.Minutes > 0)
                    seeds[game] = new GameSeed(reading.Minutes, reading.Modes, reading.Partial);
            }
            return seeds;
        }

        internal DateTimeOffset? LatestMatchUtc()
        {
            DateTimeOffset? latest = null;
            foreach (var reading in Readings.Values)
            {
                if (reading.LatestMatchUtc is { } ts && (latest is null || ts > latest))
                    latest = ts;
            }
            return latest;
        }
    }

    internal static Uri ResolveEndpoint(string? envOverride = null)
    {
        var raw = envOverride ?? Environment.GetEnvironmentVariable("EXO_RIOT_HOURS_URL");
        if (string.IsNullOrWhiteSpace(raw)) return DefaultEndpoint;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return DefaultEndpoint;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return uri;
        return DefaultEndpoint;
    }

    internal static Uri BuildUri(string riotId, string? region = null, Uri? endpoint = null)
    {
        var baseUri = endpoint ?? ResolveEndpoint();
        var query = "riotId=" + Uri.EscapeDataString(riotId);
        if (!string.IsNullOrWhiteSpace(region))
            query += "&region=" + Uri.EscapeDataString(region.Trim().ToLowerInvariant());
        var builder = new UriBuilder(baseUri) { Query = query };
        return builder.Uri;
    }

    internal static Task<Result> LoadAsync(string riotId, CancellationToken ct, string? region = null) =>
        LoadAsync(riotId, SharedHandler, ct, region, endpoint: null, backoff: SharedBackoff);

    internal static async Task<Result> LoadAsync(
        string riotId,
        HttpMessageHandler handler,
        CancellationToken ct,
        string? region = null,
        Uri? endpoint = null,
        CoverCacheClient.Backoff? backoff = null,
        DateTimeOffset? nowUtc = null)
    {
        var normalized = RiotIdStore.Normalize(riotId);
        if (normalized is null)
            return Empty(false, false, false, "Riot ID must look like Name#TAG.");

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var activeBackoff = backoff ?? SharedBackoff;
        if (activeBackoff.Active(now))
            return Empty(false, false, true, "Riot hours sync is temporarily unavailable. Try again in a couple of minutes.");

        using var http = ExoHttp.Create(RequestTimeout, handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(normalized, region, endpoint));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("ExoLauncher/" + AppServices.RunningVersion);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await ExoHttp.ReadBodyLimitedAsync(response.Content, MaxBodyBytes, ct).ConfigureAwait(false);
            var status = response.StatusCode;

            if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
            {
                var parsed = ParseErrorFlags(body);
                if (parsed.Unconfigured)
                {
                    activeBackoff.Note(now, TimeSpan.FromMinutes(15));
                    return Empty(true, false, false, parsed.Message ?? "Automatic Riot sync isn't configured on Exo's server yet. You can import a Riot data export or enter starting hours.");
                }
                activeBackoff.Note(now, response.Headers.RetryAfter?.Delta);
                return Empty(false, false, true, parsed.Message ?? "Riot hours sync is temporarily unavailable. Try again later.");
            }

            if (status == HttpStatusCode.NotFound)
            {
                var parsed = ParseErrorFlags(body);
                if (!parsed.KnownJson)
                {
                    activeBackoff.Note(now, TimeSpan.FromMinutes(15));
                    return Empty(true, false, false, "Automatic Riot sync isn't live yet. You can import a Riot data export or enter starting hours.");
                }
                return Empty(false, true, false, parsed.Message ?? "No Riot match history was found for that Riot ID.");
            }

            if (!response.IsSuccessStatusCode)
                return Empty(false, false, false, ParseErrorFlags(body).Message ?? "Riot hours sync failed. Try again.");

            return ParseSuccess(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot hours service unreachable: " + ex.GetType().Name);
            activeBackoff.Note(now);
            return Empty(false, false, true, "Exo's Riot hours service could not be reached. You can import a Riot data export or enter starting hours.");
        }
    }

    internal static Result ParseSuccess(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Empty(false, false, true, "Riot hours service returned an empty reply.");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var okEl) ||
                okEl.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("games", out var gamesEl) ||
                gamesEl.ValueKind != JsonValueKind.Object)
            {
                return Empty(false, false, true, "Riot hours service returned an unexpected reply.");
            }

            var readings = new Dictionary<string, GameReading>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in gamesEl.EnumerateObject())
            {
                if (!prop.Name.StartsWith("riot:", StringComparison.OrdinalIgnoreCase) ||
                    prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!prop.Value.TryGetProperty("minutes", out var minsEl) ||
                    !minsEl.TryGetInt32(out var minutes) ||
                    minutes <= 0 ||
                    minutes > RiotLedger.MaxSeedMinutes)
                    continue;

                var matches = prop.Value.TryGetProperty("matches", out var mEl) && mEl.TryGetInt32(out var mc) && mc > 0 ? mc : 0;
                var partial = prop.Value.TryGetProperty("partial", out var pEl) && pEl.ValueKind == JsonValueKind.True;
                var source = prop.Value.TryGetProperty("source", out var sEl) && sEl.ValueKind == JsonValueKind.String
                    ? sEl.GetString() ?? RiotLedger.CloudSource
                    : RiotLedger.CloudSource;
                var modes = ReadModes(prop.Value);
                DateTimeOffset? latest = null;
                if (prop.Value.TryGetProperty("latestMatchAt", out var tsEl) &&
                    tsEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTs))
                {
                    latest = parsedTs.ToUniversalTime();
                }

                readings[prop.Name.ToLowerInvariant()] = new GameReading(minutes, matches, partial, source, modes, latest);
            }

            if (readings.Count == 0)
                return Empty(false, true, false, "No Riot match history was found for that Riot ID.");

            var suffix = readings.Count == 1 ? "1 Riot game." : $"{readings.Count} Riot games.";
            return new Result(true, false, false, false, readings, "Synced Riot hours for " + suffix);
        }
        catch (JsonException)
        {
            return Empty(false, false, true, "Riot hours service returned unreadable data.");
        }
    }

    private static Dictionary<string, int>? ReadModes(JsonElement gameObj)
    {
        if (!gameObj.TryGetProperty("modes", out var modesEl) || modesEl.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in modesEl.EnumerateObject())
        {
            if (entry.Value.TryGetInt32(out var mins) && mins > 0)
                map[entry.Name.Trim().ToLowerInvariant()] = mins;
        }
        return map.Count > 0 ? map : null;
    }

    private static (bool KnownJson, bool Unconfigured, string? Message) ParseErrorFlags(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (false, false, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("ok", out _))
                return (false, false, null);
            var unconfigured = doc.RootElement.TryGetProperty("unconfigured", out var u) && u.ValueKind == JsonValueKind.True;
            var message = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            return (true, unconfigured, message);
        }
        catch (JsonException)
        {
            return (false, false, null);
        }
    }

    private static Result Empty(bool unconfigured, bool notFound, bool transientFailure, string message) =>
        new(false, unconfigured, notFound, transientFailure, new Dictionary<string, GameReading>(), message);
}
