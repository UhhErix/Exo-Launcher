using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters.Riot;

internal static class RiotLocalMatchHistory
{
    internal const string ClientPlatformBase64 =
        "ew0KCSJwbGF0Zm9ybVR5cGUiOiAiUEMiLA0KCSJwbGF0Zm9ybU9TIjogIldpbmRvd3MiLA0KCSJwbGF0Zm9ybU9TVmVyc2lvbiI6ICIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwNCgkicGxhdGZvcm1DaGlwc2V0IjogIlVua25vd24iDQp9";

    private const string CacheFileName = "riot-matches.json";
    private const int MaxHistoryCount = 200;
    private const int HistoryPageSize = 20;
    private const int MaxValorantDetailsPerRun = 40;
    private const int DetailDelayMs = 250;
    private const int MaxMatchSeconds = 12 * 3600;
    private static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(10);
    private static readonly object CacheGate = new();

    internal sealed class CachedMatch
    {
        [JsonPropertyName("s")]
        public int Seconds { get; set; }

        [JsonPropertyName("m")]
        public string? Mode { get; set; }

        [JsonPropertyName("t")]
        public long TimestampMs { get; set; }
    }

    internal sealed record SyncResult(
        bool Ok,
        bool ClientAvailable,
        bool AuthorizationRejected,
        int NewMatchesAdded,
        bool SeedUpdated,
        IReadOnlyDictionary<string, GameSeed> Seeds,
        DateTimeOffset? LatestMatchUtc,
        string Message);

    internal static async Task<SyncResult> SyncAsync(
        string accountKey,
        HttpMessageHandler? pdHandler = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
            return Empty(false, false, "Save a Riot ID first (Name#TAG).");

        var cache = LoadCache();
        if (!cache.TryGetValue(accountKey, out var accountMatches))
        {
            accountMatches = new Dictionary<string, CachedMatch>(StringComparer.Ordinal);
            cache[accountKey] = accountMatches;
        }

        var clientAvailable = false;
        var authRejected = false;
        var added = 0;

        using var riotApi = RiotClientApi.TryConnect();
        if (riotApi is not null)
        {
            var signedIn = await riotApi.IsSignedInAsync(ct).ConfigureAwait(false);
            if (signedIn == true)
            {
                clientAvailable = true;
                var val = await SyncValorantAsync(riotApi, accountMatches, pdHandler, ct).ConfigureAwait(false);
                added += val.Added;
                if (val.Rejected) authRejected = true;
            }
        }

        var lolInstall = RiotInstallProbe.FindInstalledProduct("league_of_legends");
        if (lolInstall is not null)
        {
            using var lcu = RiotClientApi.TryConnect(Path.Combine(lolInstall, "lockfile"));
            if (lcu is not null)
            {
                clientAvailable = true;
                var lolJson = await lcu.GetJsonAsync(
                    "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=199",
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(lolJson))
                    added += MergeMatches(accountMatches, ParseLcuLeagueMatches(lolJson));

                var summonerJson = await lcu.GetJsonAsync("/lol-summoner/v1/current-summoner", ct).ConfigureAwait(false);
                if (ParseSummonerPuuid(summonerJson) is { } puuid)
                {
                    var tftJson = await lcu.GetJsonAsync(
                        $"/lol-match-history/v1/products/tft/{Uri.EscapeDataString(puuid)}/matches?begin=0&count=200",
                        ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(tftJson))
                        added += MergeMatches(accountMatches, ParseLcuTftMatches(tftJson));
                }
            }
        }

        if (added > 0)
            SaveCache(cache);

        var (seeds, latest) = Summarize(accountMatches);
        if (seeds.Count == 0)
        {
            var msg = authRejected
                ? "Riot denied local match history lookup."
                : clientAvailable
                    ? "No Riot match history was found for this account yet."
                    : "Open Riot Client (signed in) to sync local Riot hours.";
            return Empty(clientAvailable, authRejected, msg);
        }

        var now = DateTimeOffset.UtcNow;
        var effectiveAt = latest is { } ts && ts <= now ? ts : now;
        var updated = RiotLedger.SeedIfHigher(accountKey, seeds, effectiveAt, RiotLedger.LocalSource);

        return new SyncResult(
            true,
            clientAvailable,
            authRejected,
            added,
            updated,
            seeds,
            latest,
            $"Synced Riot hours from local client history ({seeds.Count} game(s)).");
    }

    internal static string MapValorantShard(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return "na";
        var key = region.Trim().ToLowerInvariant();
        if (key is "na" or "latam" or "br" or "na1" or "br1" or "la1" or "la2" or "americas") return "na";
        if (key is "eu" or "euw" or "euw1" or "eune" or "eun1" or "tr" or "tr1" or "ru" or "europe") return "eu";
        if (key is "kr" or "kr1") return "kr";
        if (key is "ap" or "oc" or "oc1" or "jp" or "jp1" or "ph" or "sg" or "th" or "tw" or "vn" or "sea" or "asia") return "ap";
        if (key.StartsWith("eu", StringComparison.Ordinal)) return "eu";
        if (key.StartsWith("kr", StringComparison.Ordinal)) return "kr";
        if (key.StartsWith("ap", StringComparison.Ordinal) || key.StartsWith("oc", StringComparison.Ordinal) || key.StartsWith("jp", StringComparison.Ordinal))
            return "ap";
        return "na";
    }

    internal static (string AccessToken, string EntitlementsToken, string Puuid)? ParseEntitlements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var access = ReadString(root, "accessToken");
            var entitlements = ReadString(root, "token");
            var puuid = ReadString(root, "subject");
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(entitlements) || string.IsNullOrWhiteSpace(puuid))
                return null;
            return (access, entitlements, puuid);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseRegionFromLocale(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return ReadString(root, "region") ?? ReadString(root, "webRegion");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseValorantVersionFromExternalSessions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var entry in root.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var productId = ReadString(entry.Value, "productId");
                if (!string.Equals(productId, "valorant", StringComparison.OrdinalIgnoreCase)) continue;
                var version = ReadString(entry.Value, "version");
                if (!string.IsNullOrWhiteSpace(version)) return version;
                if (entry.Value.TryGetProperty("launchConfiguration", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                {
                    var cfgVersion = ReadString(cfg, "version");
                    if (!string.IsNullOrWhiteSpace(cfgVersion)) return cfgVersion;
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseValorantVersionApi(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                return ReadString(data, "riotClientVersion");
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static (int Total, List<string> MatchIds) ParsePdHistory(string? json)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return (0, ids);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (0, ids);
            var total = root.TryGetProperty("Total", out var tEl) && tEl.TryGetInt32(out var t) ? t : 0;
            if (root.TryGetProperty("History", out var historyEl) && historyEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in historyEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = ReadString(item, "MatchID") ?? ReadString(item, "matchId");
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                }
            }
            return (total, ids);
        }
        catch (JsonException)
        {
            return (0, ids);
        }
    }

    internal static (string Game, string MatchId, CachedMatch Match)? ParsePdMatchDetail(string? json, string fallbackMatchId)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("matchInfo", out var info) || info.ValueKind != JsonValueKind.Object) return null;
            var matchId = ReadString(info, "matchId") ?? ReadString(info, "MatchID") ?? fallbackMatchId;
            if (string.IsNullOrWhiteSpace(matchId)) return null;
            if (!TryReadNumber(info, "gameLengthMillis", out var millis) || millis <= 0) return null;
            var seconds = (int)Math.Round(millis / 1000d);
            if (seconds <= 0 || seconds > MaxMatchSeconds) return null;
            var queue = ReadString(info, "queueID") ?? ReadString(info, "queueId");
            var mode = RiotModes.FromValorantQueue(queue);
            var startMs = ReadUnixMs(info, "gameStartMillis");
            return ("riot:valorant", matchId, new CachedMatch { Seconds = seconds, Mode = mode, TimestampMs = startMs });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<(string Game, string MatchId, CachedMatch Match)> ParseLcuLeagueMatches(string? json)
    {
        var list = new List<(string, string, CachedMatch)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return list;
            if (!root.TryGetProperty("games", out var gamesObj) || gamesObj.ValueKind != JsonValueKind.Object) return list;
            if (!gamesObj.TryGetProperty("games", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadIdString(item, "gameId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!TryReadNumber(item, "gameDuration", out var rawDuration) || rawDuration <= 0) continue;
                var seconds = rawDuration > 86400 ? (int)Math.Round(rawDuration / 1000d) : (int)Math.Round(rawDuration);
                if (seconds <= 0 || seconds > MaxMatchSeconds) continue;
                var queueId = item.TryGetProperty("queueId", out var qEl) && qEl.TryGetInt32(out var q) ? q : 0;
                var mode = RiotModes.FromLeagueQueue(queueId);
                var ts = ReadUnixMs(item, "gameCreation");
                list.Add(("riot:league_of_legends", id, new CachedMatch { Seconds = seconds, Mode = mode, TimestampMs = ts }));
            }
            return list;
        }
        catch (JsonException)
        {
            return list;
        }
    }

    internal static string? ParseSummonerPuuid(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadString(doc.RootElement, "puuid") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<(string Game, string MatchId, CachedMatch Match)> ParseLcuTftMatches(string? json)
    {
        var list = new List<(string, string, CachedMatch)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return list;
            if (!root.TryGetProperty("games", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("json", out var body) || body.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadNumber(body, "game_length", out var rawLen) || rawLen <= 0) continue;
                var seconds = rawLen > 86400 ? (int)Math.Round(rawLen / 1000d) : (int)Math.Round(rawLen);
                if (seconds <= 0 || seconds > MaxMatchSeconds) continue;
                string? id = null;
                if (item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    id = ReadString(meta, "match_id") ?? ReadString(meta, "matchId");
                id ??= ReadIdString(body, "game_id") ?? ReadIdString(body, "gameId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var ts = ReadUnixMs(body, "game_datetime");
                list.Add(("riot:teamfighttactics", id, new CachedMatch { Seconds = seconds, Mode = null, TimestampMs = ts }));
            }
            return list;
        }
        catch (JsonException)
        {
            return list;
        }
    }

    internal static (Dictionary<string, GameSeed> Seeds, DateTimeOffset? LatestMatchUtc) Summarize(
        IReadOnlyDictionary<string, CachedMatch> matches)
    {
        var totalSeconds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var modeSeconds = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        long latestMs = 0;

        foreach (var (compositeKey, entry) in matches)
        {
            var bar = compositeKey.IndexOf('|');
            if (bar <= 0 || entry.Seconds <= 0 || entry.Seconds > MaxMatchSeconds) continue;
            var game = compositeKey[..bar];
            totalSeconds[game] = totalSeconds.GetValueOrDefault(game) + entry.Seconds;
            if (!string.IsNullOrWhiteSpace(entry.Mode) &&
                !string.Equals(entry.Mode, RiotModes.Unknown, StringComparison.Ordinal))
            {
                if (!modeSeconds.TryGetValue(game, out var byMode))
                {
                    byMode = new Dictionary<string, long>(StringComparer.Ordinal);
                    modeSeconds[game] = byMode;
                }
                byMode[entry.Mode] = byMode.GetValueOrDefault(entry.Mode) + entry.Seconds;
            }
            if (entry.TimestampMs > latestMs) latestMs = entry.TimestampMs;
        }

        var seeds = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, secs) in totalSeconds)
        {
            var mins = (int)Math.Clamp(Math.Round(secs / 60d), 1, RiotLedger.MaxSeedMinutes);
            Dictionary<string, int>? modes = null;
            if (modeSeconds.TryGetValue(game, out var rawModes) && rawModes.Count > 0)
            {
                modes = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (mode, mSecs) in rawModes)
                {
                    var mMins = (int)Math.Round(mSecs / 60d);
                    if (mMins > 0) modes[mode] = mMins;
                }
                if (modes.Count == 0) modes = null;
            }
            seeds[game] = new GameSeed(mins, modes, Partial: true);
        }

        DateTimeOffset? latest = null;
        if (latestMs > 0)
        {
            try { latest = DateTimeOffset.FromUnixTimeMilliseconds(latestMs); }
            catch (ArgumentOutOfRangeException) { }
        }
        return (seeds, latest);
    }

    internal static int MergeMatches(
        IDictionary<string, CachedMatch> target,
        IEnumerable<(string Game, string MatchId, CachedMatch Match)> items)
    {
        var added = 0;
        foreach (var (game, matchId, match) in items)
        {
            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(matchId) || match.Seconds <= 0 || match.Seconds > MaxMatchSeconds)
                continue;
            var key = game.ToLowerInvariant() + "|" + matchId.Trim();
            if (target.ContainsKey(key)) continue;
            target[key] = match;
            added++;
        }
        return added;
    }

    private static async Task<(int Added, bool Rejected)> SyncValorantAsync(
        RiotClientApi riotApi,
        IDictionary<string, CachedMatch> accountMatches,
        HttpMessageHandler? pdHandler,
        CancellationToken ct)
    {
        var entJson = await riotApi.GetJsonAsync("/entitlements/v1/token", ct).ConfigureAwait(false);
        var tokens = ParseEntitlements(entJson);
        if (tokens is not { } auth) return (0, false);

        var localeJson = await riotApi.GetJsonAsync("/riotclient/region-locale", ct).ConfigureAwait(false);
        var shard = MapValorantShard(ParseRegionFromLocale(localeJson));

        var sessionsJson = await riotApi.GetJsonAsync("/product-session/v1/external-sessions", ct).ConfigureAwait(false);
        var version = ParseValorantVersionFromExternalSessions(sessionsJson);

        using var http = pdHandler is null
            ? ExoHttp.Create(ExternalTimeout)
            : ExoHttp.Create(ExternalTimeout, pdHandler, disposeHandler: false);

        if (string.IsNullOrWhiteSpace(version))
            version = await FetchFallbackValorantVersionAsync(http, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(version)) return (0, false);

        var matchIds = new List<string>();
        var total = MaxHistoryCount;
        for (var start = 0; start < total && start < MaxHistoryCount; start += HistoryPageSize)
        {
            var end = Math.Min(start + HistoryPageSize, MaxHistoryCount);
            var url = $"https://pd.{shard}.a.pvp.net/match-history/v1/history/{Uri.EscapeDataString(auth.Puuid)}?startIndex={start}&endIndex={end}";
            var res = await SendPdGetAsync(http, url, auth.AccessToken, auth.EntitlementsToken, version, ct).ConfigureAwait(false);
            if (res.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (0, true);
            if (res.Status == HttpStatusCode.TooManyRequests || !res.Ok || res.Body is null) break;

            var page = ParsePdHistory(res.Body);
            if (page.Total > 0) total = Math.Min(page.Total, MaxHistoryCount);
            if (page.MatchIds.Count == 0) break;
            matchIds.AddRange(page.MatchIds);
            if (page.MatchIds.Count < HistoryPageSize) break;
        }

        if (matchIds.Count == 0) return (0, false);

        var fetched = 0;
        var parsedDetails = new List<(string Game, string MatchId, CachedMatch Match)>();
        foreach (var matchId in matchIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (fetched >= MaxValorantDetailsPerRun) break;
            if (accountMatches.ContainsKey("riot:valorant|" + matchId.Trim())) continue;

            if (fetched > 0)
                await Task.Delay(DetailDelayMs, ct).ConfigureAwait(false);
            fetched++;

            var detailUrl = $"https://pd.{shard}.a.pvp.net/match-details/v1/matches/{Uri.EscapeDataString(matchId)}";
            var detailRes = await SendPdGetAsync(http, detailUrl, auth.AccessToken, auth.EntitlementsToken, version, ct).ConfigureAwait(false);
            if (detailRes.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (MergeMatches(accountMatches, parsedDetails), true);
            if (detailRes.Status == HttpStatusCode.TooManyRequests) break;
            if (!detailRes.Ok || detailRes.Body is null) continue;

            if (ParsePdMatchDetail(detailRes.Body, matchId) is { } parsed)
                parsedDetails.Add(parsed);
        }

        return (MergeMatches(accountMatches, parsedDetails), false);
    }

    private static async Task<string?> FetchFallbackValorantVersionAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync("https://valorant-api.com/v1/version", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await ExoHttp.ReadBodyLimitedAsync(resp.Content, 1024 * 1024, ct).ConfigureAwait(false);
            return ParseValorantVersionApi(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool Ok, HttpStatusCode Status, string? Body)> SendPdGetAsync(
        HttpClient http,
        string url,
        string accessToken,
        string entitlementsToken,
        string clientVersion,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        req.Headers.TryAddWithoutValidation("X-Riot-Entitlements-JWT", entitlementsToken);
        req.Headers.TryAddWithoutValidation("X-Riot-ClientVersion", clientVersion);
        req.Headers.TryAddWithoutValidation("X-Riot-ClientPlatform", ClientPlatformBase64);
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, resp.StatusCode, null);
            var body = await ExoHttp.ReadBodyLimitedAsync(resp.Content, 8 * 1024 * 1024, ct).ConfigureAwait(false);
            return (body is not null, resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (false, 0, null);
        }
    }

    private static Dictionary<string, Dictionary<string, CachedMatch>> LoadCache()
    {
        lock (CacheGate)
        {
            var path = Path.Combine(PathHelper.AppDataDir, CacheFileName);
            var result = new Dictionary<string, Dictionary<string, CachedMatch>>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return result;
                var json = File.ReadAllText(path, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CachedMatch>>>(json);
                if (parsed is null) return result;
                foreach (var (account, entries) in parsed)
                {
                    if (string.IsNullOrWhiteSpace(account) || entries is null) continue;
                    result[account] = new Dictionary<string, CachedMatch>(entries, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Debug("Riot match cache read failed: " + ex.GetType().Name);
            }
            return result;
        }
    }

    private static void SaveCache(Dictionary<string, Dictionary<string, CachedMatch>> cache)
    {
        lock (CacheGate)
        {
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var path = Path.Combine(PathHelper.AppDataDir, CacheFileName);
                var root = new JsonObject();
                foreach (var (account, entries) in cache)
                {
                    var obj = new JsonObject();
                    foreach (var (key, match) in entries)
                    {
                        var item = new JsonObject
                        {
                            ["s"] = match.Seconds,
                            ["t"] = match.TimestampMs,
                        };
                        if (!string.IsNullOrWhiteSpace(match.Mode))
                            item["m"] = match.Mode;
                        obj[key] = item;
                    }
                    root[account] = obj;
                }
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToJsonString(), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Riot match cache save failed: " + ex.GetType().Name);
            }
        }
    }

    private static bool TryReadNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetDouble(out value);
    }

    private static long ReadUnixMs(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var raw) && raw > 0)
                return raw < 10_000_000_000L ? raw * 1000L : raw;
            if (el.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUnixTimeMilliseconds();
        }
        return 0;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()
            : null;

    private static string? ReadIdString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())) return el.GetString();
        if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
        return null;
    }

    private static SyncResult Empty(bool clientAvailable, bool authRejected, string message) =>
        new(false, clientAvailable, authRejected, 0, false, new Dictionary<string, GameSeed>(), null, message);
}
