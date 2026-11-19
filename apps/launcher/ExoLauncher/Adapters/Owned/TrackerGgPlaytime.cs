using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Public tracker.gg profile JSON. Hours are match-derived on their side.
/// A private profile is reported as private, not as zero hours.
/// </summary>
internal static class TrackerGgPlaytime
{
    internal const string ValorantProfileUrl = "https://api.tracker.gg/api/v2/valorant/standard/profile/riot/";
    internal const string LeagueProfileUrl = "https://api.tracker.gg/api/v2/lol/standard/profile/riot/";
    // A profile is tens of kilobytes; anything past this is not one.
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    private static readonly Regex Hours = new(
        @"([\d,.]+)\s*h",
        RegexOptions.IgnoreCase);
    private static readonly Regex MinutesPart = new(
        @"([\d,.]+)\s*m",
        RegexOptions.IgnoreCase);

    private static readonly CookieContainer Cookies = new();
    private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = true,
        CookieContainer = Cookies,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = false,
    };

    internal sealed record Result(
        bool Ok,
        bool ProfilePrivate,
        IReadOnlyDictionary<string, int> Minutes,
        string Message,
        /// <summary>Games whose figure is tracker.gg's lifetime total (its overview), which replaces an earlier reading; the rest are only ever raised.</summary>
        IReadOnlySet<string>? Lifetime = null,
        /// <summary>Per game: its figure with the per-mode split tracker.gg showed (the one-time seed).</summary>
        IReadOnlyDictionary<string, RiotGameSeedReading>? Readings = null);

    /// <summary>What one game's profile read gave: the figure, its per-mode split, and whether the figure is lifetime.</summary>
    internal sealed record RiotGameSeedReading(int Minutes, bool Lifetime, IReadOnlyDictionary<string, int>? Modes);

    internal static string ProfilePageUrl(string riotId) =>
        "https://tracker.gg/valorant/profile/riot/" + EncodeRiotId(riotId) + "/overview";

    internal static string EncodeRiotId(string riotId)
    {
        var normalized = RiotIdStore.Normalize(riotId) ?? riotId.Trim();
        var hash = normalized.IndexOf('#');
        if (hash <= 0) return Uri.EscapeDataString(normalized);
        return Uri.EscapeDataString(normalized[..hash]) + "%23" + Uri.EscapeDataString(normalized[(hash + 1)..]);
    }

    internal static bool IsPrivateProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object) continue;
                var code = ReadString(error, "code") ?? "";
                var message = ReadString(error, "message") ?? "";
                if (code.Contains("Private", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("private", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static IReadOnlyDictionary<string, int> ParseMinutes(string? json, string gameId)
    {
        var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var value = ParseBestMinutes(json, gameId);
        if (value is int parsed && parsed > 0)
            minutes[gameId] = parsed;
        return minutes;
    }

    /// <summary>
    /// The ledger seeds a load's readings become. A profile with no overview lifetime (only playlist sums) still seeds, as
    /// before, marked partial so it is not taken for tracker.gg's whole total.
    /// </summary>
    internal static Dictionary<string, GameSeed> ToSeeds(IReadOnlyDictionary<string, RiotGameSeedReading> readings)
    {
        var seeds = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, reading) in readings)
            seeds[game] = new GameSeed(reading.Minutes, reading.Modes, Partial: !reading.Lifetime);
        return seeds;
    }

    internal static int? ParseBestMinutes(string? json, string? gameId = null) => ParseReading(json, gameId)?.Minutes;

    /// <summary>
    /// Hours per mode from a profile's playlist segments, each playlist's larger reading once, named by
    /// <see cref="RiotModes.FromTrackerPlaylist"/>. Null when the profile has no playlists.
    /// </summary>
    internal static IReadOnlyDictionary<string, int>? ParseModes(string? json, string gameId)
    {
        var segments = Segments(json);
        if (segments is null) return null;
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            if (ReadString(segment, "type")?.Equals("playlist", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (!segment.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object ||
                !stats.TryGetProperty("timePlayed", out var stat)) continue;
            if (MinutesFromStat(stat) is not int value || value <= 0) continue;
            var name = SegmentName(segment) ?? byName.Count.ToString(CultureInfo.InvariantCulture);
            if (!byName.TryGetValue(name, out var current) || value > current) byName[name] = value;
        }
        if (byName.Count == 0) return null;
        return ModesOf(gameId, byName);
    }

    /// <summary>
    /// Playlists by mode name. Aliases of one queue (Valorant's Ranked and Competitive) count once: the larger reading.
    /// Playlists with no known name are their own queues, so they add up.
    /// </summary>
    internal static Dictionary<string, int> ModesOf(string gameId, IReadOnlyDictionary<string, int> byName)
    {
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in byName)
        {
            var mode = RiotModes.FromTrackerPlaylist(gameId, name);
            modes[mode] = mode == RiotModes.Other
                ? modes.GetValueOrDefault(mode) + value
                : Math.Max(modes.GetValueOrDefault(mode), value);
        }
        return modes;
    }

    private static List<JsonElement>? Segments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || IsPrivateProfile(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) || !TryEnumerateSegments(data, out var segments))
                return null;
            // Cloned: the elements belong to the document, which closes here.
            return segments.Select(segment => segment.Clone()).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The best figure in a profile, and whether it is tracker.gg's lifetime total (overview) rather than a sum of queues or one season.</summary>
    internal static (int Minutes, bool Lifetime)? ParseReading(string? json, string? gameId = null)
    {
        if (string.IsNullOrWhiteSpace(json) || IsPrivateProfile(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data))
                return null;
            if (!TryEnumerateSegments(data, out var segments))
                return null;

            int? overview = null;
            var playlists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seasons = 0;
            foreach (var segment in segments)
            {
                if (segment.ValueKind != JsonValueKind.Object) continue;
                if (!segment.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object ||
                    !stats.TryGetProperty("timePlayed", out var stat))
                    continue;
                var minutes = MinutesFromStat(stat);
                if (minutes is not int value || value <= 0) continue;

                var type = ReadString(segment, "type") ?? "";
                if (type.Equals("overview", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (overview is null || value > overview) overview = value;
                    continue;
                }

                if (type.Equals("playlist", StringComparison.OrdinalIgnoreCase))
                {
                    var name = SegmentName(segment) ?? playlists.Count.ToString(CultureInfo.InvariantCulture);
                    if (!playlists.TryGetValue(name, out var current) || value > current)
                        playlists[name] = value;
                    continue;
                }

                if (type.Equals("season", StringComparison.OrdinalIgnoreCase) && value > seasons)
                    seasons = value;
            }

            // Overview/profile is tracker.gg lifetime. Playlist sums are only
            // a fallback: they can be current-act or overlapping queues.
            if (overview is > 0) return (overview.Value, true);
            if (playlists.Count > 0)
                return (gameId is null ? playlists.Values.Sum() : ModesOf(gameId, playlists).Values.Sum(), false);
            return seasons > 0 ? (seasons, false) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task<Result> LoadAsync(string riotId, HttpMessageHandler handler, CancellationToken ct)
    {
        var id = RiotIdStore.Normalize(riotId);
        if (id is null)
            return new Result(false, false, new Dictionary<string, int>(), "Riot ID must look like Name#TAG.");

        var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lifetime = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readings = new Dictionary<string, RiotGameSeedReading>(StringComparer.OrdinalIgnoreCase);
        var anyPrivate = false;
        var anyUnanswered = false;
        Answer? blockedBy = null;
        var encoded = EncodeRiotId(id);
        foreach (var (urls, gameId) in new[]
                 {
                     (new[]
                     {
                         ValorantProfileUrl + encoded + "?season=all",
                         ValorantProfileUrl + encoded + "/segments/playlist?season=all",
                     }, "riot:valorant"),
                     (new[]
                     {
                         LeagueProfileUrl + encoded + "?season=all",
                         LeagueProfileUrl + encoded + "/segments/playlist?season=all",
                     }, "riot:league_of_legends"),
                 })
        {
            // A block answers every request alike: one is enough to know, and Exo asks no more.
            if (blockedBy is not null) break;
            string? json = null;
            var unanswered = false;
            var priv = false;
            foreach (var url in urls)
            {
                var answer = await HttpGetJsonAsync(url, handler, ct).ConfigureAwait(false);
                if (answer.Kind == AnswerKind.Blocked)
                {
                    blockedBy = answer;
                    break;
                }
                var body = answer.Body;
                if (answer.Kind != AnswerKind.Json || body is null)
                {
                    unanswered = true;
                    continue;
                }

                if (IsPrivateProfile(body))
                {
                    priv = true;
                    continue;
                }

                if (ParseBestMinutes(body, gameId) is > 0)
                {
                    json = body;
                    break;
                }
            }

            if (json is null)
            {
                if (priv) anyPrivate = true;
                else if (unanswered) anyUnanswered = true;
                continue;
            }

            var parsed = ParseMinutes(json, gameId);
            foreach (var pair in parsed)
            {
                if (!minutes.TryGetValue(pair.Key, out var current) || pair.Value > current)
                    minutes[pair.Key] = pair.Value;
            }
            if (ParseReading(json, gameId) is { } reading)
            {
                if (reading.Lifetime) lifetime.Add(gameId);
                readings[gameId] = new RiotGameSeedReading(reading.Minutes, reading.Lifetime, ParseModes(json, gameId));
            }
        }

        if (minutes.Count > 0)
        {
            AppLog.Info("tracker.gg import: hours read for " + minutes.Count + " game(s).");
            return new Result(true, false, minutes, "Imported hours from tracker.gg.", lifetime, readings);
        }
        if (blockedBy is { } block)
        {
            // Exo does not get past a block (no browser, no spoofed headers, no challenge solved): the seed stays empty.
            AppLog.Info("tracker.gg import: refused by tracker.gg (HTTP " + block.Status + (block.Challenge ? ", browser check" : "") + "); no hours imported.");
            return new Result(false, false, minutes, BlockedMessage);
        }
        if (anyPrivate)
        {
            AppLog.Info("tracker.gg import: the profile is private.");
            return new Result(
                false,
                true,
                minutes,
                "This tracker.gg profile is private. Sign in with Riot on tracker.gg once so the profile is public.");
        }
        if (anyUnanswered)
        {
            AppLog.Info("tracker.gg import: no answer from tracker.gg.");
            return new Result(false, false, minutes, UnansweredMessage);
        }
        AppLog.Info("tracker.gg import: the profile had no hours.");
        return new Result(false, false, minutes, "tracker.gg did not return hours for that Riot ID.");
    }

    /// <summary>
    /// tracker.gg's API now answers apps with a browser check (Cloudflare's "Just a moment…") or its "You've been blocked"
    /// page, HTTP 403, whatever the request says it is (seen November 2026). Exo will not pretend to be a browser or solve
    /// the check, so it says so plainly.
    /// </summary>
    internal const string BlockedMessage =
        "tracker.gg now blocks apps from reading its profiles, so Exo can't import your Riot hours from it. Exo still counts every session you play.";

    internal const string UnansweredMessage = "tracker.gg isn't answering right now. Try again later.";

    internal enum AnswerKind { Json, Blocked, Unanswered }

    /// <summary>One request's answer: JSON to read, a block (its status, and whether it was a browser check), or nothing usable.</summary>
    internal sealed record Answer(AnswerKind Kind, int Status, string? Body = null, bool Challenge = false);

    /// <summary>
    /// What one answer is. A refusal status (401, 403, 429, 503) or a page that is a challenge or a block page is tracker.gg
    /// refusing the request; JSON is a profile (or its error); anything else is no answer.
    /// </summary>
    internal static Answer Classify(int status, string? body)
    {
        var challenge = body is not null && (
            body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("_cf_chl_opt", StringComparison.Ordinal) ||
            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase));
        var blockPage = body is not null && body.Contains("You've Been Blocked", StringComparison.OrdinalIgnoreCase);
        if (challenge || blockPage || (status is 401 or 403 or 429 or 503 && !IsJsonBody(body)))
            return new Answer(AnswerKind.Blocked, status, Challenge: challenge);
        return IsJsonBody(body) ? new Answer(AnswerKind.Json, status, body) : new Answer(AnswerKind.Unanswered, status);
    }

    internal static Task<Result> LoadAsync(string riotId, CancellationToken ct) =>
        LoadAsync(riotId, SharedHandler, ct);

    private static async Task<Answer> HttpGetJsonAsync(string url, HttpMessageHandler handler, CancellationToken ct)
    {
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(20), handler, disposeHandler: false);
        http.DefaultRequestVersion = HttpVersion.Version11;
        http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Honest: Exo says what it is. No browser user agent, no Origin or Referer of tracker.gg's own site.
        request.Headers.UserAgent.ParseAdd("ExoLauncher/" + AppServices.RunningVersion);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            // A profile over the cap is no answer: no seed.
            var body = await ExoHttp.ReadBodyLimitedAsync(response.Content, MaxBodyBytes, ct).ConfigureAwait(false);
            return Classify((int)response.StatusCode, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Info("tracker.gg playtime query unavailable: " + ex.GetType().Name);
            return new Answer(AnswerKind.Unanswered, 0);
        }
    }

    private static bool IsJsonBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static bool TryEnumerateSegments(JsonElement data, out List<JsonElement> segments)
    {
        segments = [];
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    segments.Add(item);
            }

            return segments.Count > 0;
        }

        if (data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("segments", out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nested.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    segments.Add(item);
            }

            return segments.Count > 0;
        }

        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("stats", out _))
                segments.Add(property.Value);
        }

        return segments.Count > 0;
    }

    private static string? SegmentName(JsonElement segment)
    {
        if (!segment.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(metadata, "name") ?? ReadString(metadata, "shortName");
    }

    internal static int? MinutesFromStat(JsonElement stat)
    {
        if (stat.ValueKind == JsonValueKind.Number && stat.TryGetDouble(out var bare) && bare > 0)
            return (int)Math.Clamp(bare / 60d, 1, int.MaxValue);
        if (stat.ValueKind != JsonValueKind.Object) return null;
        // The raw value in its declared unit; the display text is rounded, so it is the last resort.
        if (StatMinutes(stat) is { } minutes) return minutes;
        return stat.TryGetProperty("displayValue", out var display) &&
               display.ValueKind == JsonValueKind.String &&
               TryParseDisplay(display.GetString(), out var fromDisplay)
            ? fromDisplay
            : null;
    }

    /// <summary>
    /// A stat's minutes from its raw value and declared unit (TimeSeconds,
    /// TimeMilliseconds, TimeMinutes, TotalHoursFromSeconds…); null for an unknown unit.
    /// </summary>
    internal static int? StatMinutes(JsonElement stat)
    {
        if (stat.ValueKind != JsonValueKind.Object || !stat.TryGetProperty("value", out var node) ||
            node.ValueKind != JsonValueKind.Number || !node.TryGetDouble(out var value) || value <= 0)
            return null;
        var unit = ReadString(stat, "displayType") ?? "";
        double minutes;
        if (unit.Contains("Millis", StringComparison.OrdinalIgnoreCase)) minutes = value / 60_000d;
        else if (unit.Contains("Seconds", StringComparison.OrdinalIgnoreCase)) minutes = value / 60d;
        else if (unit.Contains("Minutes", StringComparison.OrdinalIgnoreCase)) minutes = value;
        else if (unit.Contains("Hours", StringComparison.OrdinalIgnoreCase)) minutes = value * 60d;
        else return null;
        return minutes >= 1 && minutes < 100_000_000 ? (int)minutes : null;
    }

    internal static bool TryParseDisplay(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hoursMatch = Hours.Match(text);
        var minuteMatch = MinutesPart.Match(text);
        var hours = hoursMatch.Success &&
                    double.TryParse(hoursMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var h)
            ? h
            : 0;
        var extra = minuteMatch.Success &&
                    double.TryParse(minuteMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)
            ? m
            : 0;
        var total = (int)Math.Clamp((hours * 60d) + extra, 0, int.MaxValue);
        if (total <= 0) return false;
        minutes = total;
        return true;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
