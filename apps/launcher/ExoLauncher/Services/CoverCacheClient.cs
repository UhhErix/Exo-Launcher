using System.Net;
using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Exo's shared cover cache (cloud/covers): one lookup for everyone, so a game's portrait is found once instead of by
/// every PC. The Launcher asks it first for a title it has no store id for; a hit is a store CDN URL (allowlisted, then
/// downloaded and checked as a portrait like any other art). A miss from the cache is never final for the PC: the Launcher
/// goes on to its own store searches, and its own miss memory (CoverMissMemory) is what stops it repeating them. Only an outage (a timeout, a network error, a 429 or a 5xx) switches the cache
/// off, for two minutes (or the time the cache asks for, at most fifteen), so a warm never waits on it twice; a 400 for one
/// odd title, a 404 that is not final, or art on an address Exo does not download from leaves the cache on and the
/// Launcher's own lookups running. Only the title and the store id go out; no account, path, or PC detail.
/// </summary>
internal static class CoverCacheClient
{
    internal static readonly Uri Endpoint = new("https://covers.getexo.app/v1/cover");
    /// <summary>Longer than the Worker's own budget (cloud/covers/src/limits.json, which the Worker reads and a test checks this against), so a slow store is answered, not mistaken for an unreachable cache.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    internal static readonly Backoff Shared = new();

    internal enum Outcome
    {
        /// <summary>The cache named an allowlisted art URL.</summary>
        Hit,
        /// <summary>The cache has no art for it (it may be partial: a store could not be asked). Look elsewhere.</summary>
        Miss,
        /// <summary>No answer from the cache, but no outage either (a 400, art on an address Exo does not use, an odd reply): look elsewhere, ask the cache again later.</summary>
        Unknown,
        /// <summary>An outage: unreachable, timed out, rate limited, a 5xx, or a body Exo cannot read. The cache is left alone for a while.</summary>
        Unreachable,
    }

    internal readonly record struct Answer(Outcome Outcome, string? Url);

    /// <summary>After an outage the cache is not asked again until this has passed.</summary>
    internal sealed class Backoff
    {
        internal static readonly TimeSpan Length = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan Longest = TimeSpan.FromMinutes(15);
        private readonly object _gate = new();
        private DateTimeOffset _until = DateTimeOffset.MinValue;

        internal void Reset()
        {
            lock (_gate) _until = DateTimeOffset.MinValue;
        }

        internal bool Active(DateTimeOffset now)
        {
            lock (_gate) return now < _until;
        }

        /// <summary>Starts (or extends) the backoff; <paramref name="asked"/> is the cache's own Retry-After, if it sent one.</summary>
        internal void Note(DateTimeOffset now, TimeSpan? asked = null)
        {
            var length = asked is { } a && a > TimeSpan.Zero ? (a > Longest ? Longest : a) : Length;
            lock (_gate)
            {
                var until = now + length;
                if (until > _until) _until = until;
            }
        }
    }

    /// <summary>The store id the cache can look up directly: a Steam app id or a GOG product id, else none (title only).</summary>
    internal static (string? Store, string? Id) StoreAndId(GameEntry g)
    {
        var steam = CoverArtService.SteamAppId(g);
        if (steam is not null) return ("steam", steam);
        var gog = CoverArtService.GogProductId(g);
        return gog is not null ? ("gog", gog) : (null, null);
    }

    internal static Uri BuildUri(string title, string? store, string? id)
    {
        var query = new List<string> { "title=" + Uri.EscapeDataString(title) };
        if (store is not null) query.Add("store=" + store);
        if (id is not null) query.Add("id=" + Uri.EscapeDataString(id));
        return new Uri(Endpoint, "?" + string.Join('&', query));
    }

    /// <summary>
    /// Reads the cache's answer body: <c>{"ok":true,"url":...}</c> or <c>{"ok":false,"miss":true}</c> (with
    /// <c>"partial":true</c> when a store could not be asked). A body that is not that shape is unreachable, never
    /// "no art", so a broken cache can't hide a game's art.
    /// </summary>
    internal static Answer ParseBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new Answer(Outcome.Unreachable, null);
            if (!ok.GetBoolean())
                return new Answer(root.TryGetProperty("miss", out var m) && m.ValueKind == JsonValueKind.True ? Outcome.Miss : Outcome.Unknown, null);
            if (!root.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                return new Answer(Outcome.Unreachable, null);
            var url = urlEl.GetString();
            // An address Exo does not download from (another host, another port) says nothing about the game's art.
            return CoverArtService.IsApprovedArtworkDownloadUrl(url)
                ? new Answer(Outcome.Hit, url)
                : new Answer(Outcome.Unknown, null);
        }
        catch (JsonException)
        {
            return new Answer(Outcome.Unreachable, null);
        }
    }

    internal static async Task<Answer> LookupAsync(
        HttpClient http,
        GameEntry g,
        CancellationToken cancellationToken,
        DateTimeOffset now,
        Backoff? backoff = null,
        TimeSpan? requestTimeout = null)
    {
        backoff ??= Shared;
        var title = g.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return new Answer(Outcome.Miss, null);
        if (backoff.Active(now)) return new Answer(Outcome.Unreachable, null);

        var (store, id) = StoreAndId(g);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout ?? RequestTimeout);
            using var response = await http.GetAsync(BuildUri(title, store, id), timeout.Token).ConfigureAwait(false);
            var status = response.StatusCode;
            // One odd title (all symbols, too long) is refused by the cache with a 400: no art from the cache for it, but never an outage.
            if (status == HttpStatusCode.BadRequest) return new Answer(Outcome.Unknown, null);
            if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
            {
                backoff.Note(now, response.Headers.RetryAfter?.Delta);
                return new Answer(Outcome.Unreachable, null);
            }
            // Any other refusal (a 403 from a firewall, an unknown path): not the cache's answer, not an outage either.
            if (!response.IsSuccessStatusCode && status != HttpStatusCode.NotFound)
                return new Answer(Outcome.Unknown, null);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var answer = ParseBody(body);
            // A 404 that is not the cache's own JSON (an edge page, a wrong route) is no final miss and no outage.
            if (answer.Outcome == Outcome.Unreachable && status == HttpStatusCode.NotFound) return new Answer(Outcome.Unknown, null);
            if (answer.Outcome == Outcome.Unreachable) backoff.Note(now);
            return answer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Cover cache unreachable: " + ex.Message);
            backoff.Note(now);
            return new Answer(Outcome.Unreachable, null);
        }
    }
}
