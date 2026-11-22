using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

internal static class RiotDataExportImporter
{
    private const long MaxSingleEntryBytes = 64L * 1024 * 1024;
    private const long MaxTotalReadBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 500;
    private const int MaxMatchSeconds = 12 * 3600;

    internal sealed record Result(
        bool Ok,
        IReadOnlyDictionary<string, GameSeed> Seeds,
        IReadOnlyDictionary<string, int> MatchCounts,
        DateTimeOffset? LatestMatchUtc,
        string Message);

    private sealed class Accumulator
    {
        public Dictionary<string, HashSet<string>> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> SecondsByGame { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> MatchesByGame { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, long>> ModeSecondsByGame { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? LatestMatchUtc { get; set; }

        public void Add(string gameId, string? matchId, int seconds, string? mode, DateTimeOffset? timestamp)
        {
            if (seconds <= 0 || seconds > MaxMatchSeconds) return;
            if (!string.IsNullOrWhiteSpace(matchId))
            {
                if (!SeenIds.TryGetValue(gameId, out var seen))
                {
                    seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    SeenIds[gameId] = seen;
                }
                if (!seen.Add(matchId.Trim())) return;
            }

            SecondsByGame[gameId] = SecondsByGame.GetValueOrDefault(gameId) + seconds;
            MatchesByGame[gameId] = MatchesByGame.GetValueOrDefault(gameId) + 1;

            if (!string.IsNullOrWhiteSpace(mode) && !string.Equals(mode, RiotModes.Unknown, StringComparison.Ordinal))
            {
                if (!ModeSecondsByGame.TryGetValue(gameId, out var modes))
                {
                    modes = new Dictionary<string, long>(StringComparer.Ordinal);
                    ModeSecondsByGame[gameId] = modes;
                }
                modes[mode] = modes.GetValueOrDefault(mode) + seconds;
            }

            if (timestamp is { } ts && (LatestMatchUtc is null || ts > LatestMatchUtc))
                LatestMatchUtc = ts;
        }
    }

    internal static Task<Result> ImportFromFileAsync(string path, CancellationToken ct) =>
        Task.Run(() => ImportFromFile(path, ct), ct);

    internal static Result ImportFromFile(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Fail("Choose a Riot data export (.zip or .json) file.");

        var acc = new Accumulator();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".zip")
            {
                using var stream = File.OpenRead(path);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                long totalBytes = 0;
                var inspected = 0;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (inspected >= MaxEntries || totalBytes >= MaxTotalReadBytes) break;
                    if (entry.Length <= 0 || !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length > MaxSingleEntryBytes) continue;
                    inspected++;
                    using var entryStream = entry.Open();
                    var bytes = ReadBounded(entryStream, Math.Min(MaxSingleEntryBytes, MaxTotalReadBytes - totalBytes));
                    if (bytes is null) continue;
                    totalBytes += bytes.Length;
                    ParseJsonDocument(bytes, entry.FullName, acc, ct);
                }
            }
            else if (ext == ".json")
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxSingleEntryBytes)
                    return Fail("That JSON file is empty or too large to import.");
                var bytes = File.ReadAllBytes(path);
                ParseJsonDocument(bytes, Path.GetFileName(path), acc, ct);
            }
            else
            {
                return Fail("Choose a Riot data export .zip archive or match history .json file.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Fail("That file is not a valid .zip archive.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Exo could not read that file.");
        }

        return BuildResult(acc);
    }

    internal static Result ImportFromJsonText(string json, string? fileNameHint = null)
    {
        var acc = new Accumulator();
        ParseJsonDocument(System.Text.Encoding.UTF8.GetBytes(json), fileNameHint ?? "", acc, CancellationToken.None);
        return BuildResult(acc);
    }

    private static Result BuildResult(Accumulator acc)
    {
        var seeds = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, totalSeconds) in acc.SecondsByGame)
        {
            var minutes = (int)Math.Clamp(Math.Round(totalSeconds / 60d), 1, RiotLedger.MaxSeedMinutes);
            Dictionary<string, int>? modeMins = null;
            if (acc.ModeSecondsByGame.TryGetValue(game, out var rawModes) && rawModes.Count > 0)
            {
                modeMins = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (mode, secs) in rawModes)
                {
                    var m = (int)Math.Round(secs / 60d);
                    if (m > 0) modeMins[mode] = m;
                }
                if (modeMins.Count == 0) modeMins = null;
            }
            seeds[game] = new GameSeed(minutes, modeMins, Partial: false);
            counts[game] = acc.MatchesByGame.GetValueOrDefault(game);
        }

        if (seeds.Count == 0)
            return Fail("No Riot match history durations were found in that export.");

        var totalMatches = counts.Values.Sum();
        var gameLabel = seeds.Count == 1 ? "1 game" : $"{seeds.Count} games";
        var matchLabel = totalMatches == 1 ? "1 match" : $"{totalMatches:N0} matches";
        return new Result(true, seeds, counts, acc.LatestMatchUtc, $"Imported {gameLabel} ({matchLabel}) from Riot data export.");
    }

    private static void ParseJsonDocument(byte[] bytes, string entryName, Accumulator acc, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var hint = GameHintFromPath(entryName);
            InspectElement(doc.RootElement, hint, acc, ct, depth: 0);
        }
        catch (JsonException)
        {
        }
    }

    private static void InspectElement(JsonElement el, string? gameHint, Accumulator acc, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 8) return;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                InspectElement(item, gameHint, acc, ct, depth + 1);
            return;
        }

        if (el.ValueKind != JsonValueKind.Object) return;

        if (TryExtractMatch(el, gameHint, out var gameId, out var matchId, out var seconds, out var mode, out var ts))
        {
            acc.Add(gameId, matchId, seconds, mode, ts);
            return;
        }

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                var nextHint = gameHint ?? GameHintFromPath(prop.Name);
                InspectElement(prop.Value, nextHint, acc, ct, depth + 1);
            }
        }
    }

    private static bool TryExtractMatch(
        JsonElement obj,
        string? gameHint,
        out string gameId,
        out string? matchId,
        out int seconds,
        out string? mode,
        out DateTimeOffset? timestamp)
    {
        gameId = "";
        matchId = null;
        seconds = 0;
        mode = null;
        timestamp = null;

        if (TryGetObject(obj, "matchInfo", out var matchInfo))
        {
            var secs = ReadDurationSeconds(matchInfo, "gameLengthMillis", isMillis: true)
                ?? ReadDurationSeconds(matchInfo, "gameLength", isMillis: false);
            if (secs is > 0)
            {
                gameId = gameHint ?? "riot:valorant";
                matchId = FirstString(matchInfo, "matchId", "match_id", "id") ?? FirstString(obj, "matchId", "id");
                seconds = secs.Value;
                mode = MapMode(gameId, FirstString(matchInfo, "queueId", "gameMode", "mode"), null);
                timestamp = ReadTimestamp(matchInfo, "gameStartMillis", "gameStartTime", "started_at");
                return true;
            }
        }

        if (TryGetObject(obj, "info", out var info))
        {
            var isTft = gameHint == "riot:teamfighttactics" ||
                        info.TryGetProperty("tft_set_number", out _) ||
                        info.TryGetProperty("tft_game_type", out _) ||
                        info.TryGetProperty("game_length", out _);
            if (isTft)
            {
                var secs = ReadDurationSeconds(info, "game_length", isMillis: false)
                    ?? ReadDurationSeconds(info, "gameDuration", isMillis: false);
                if (secs is > 0)
                {
                    gameId = "riot:teamfighttactics";
                    matchId = TryGetObject(obj, "metadata", out var meta)
                        ? FirstString(meta, "match_id", "matchId")
                        : FirstString(obj, "match_id", "matchId");
                    seconds = secs.Value;
                    timestamp = ReadTimestamp(info, "game_datetime", "gameStartTimestamp");
                    return true;
                }
            }

            var lolSecs = ReadLeagueDurationSeconds(info);
            if (lolSecs is > 0)
            {
                gameId = gameHint ?? "riot:league_of_legends";
                matchId = TryGetObject(obj, "metadata", out var meta)
                    ? FirstString(meta, "matchId", "match_id")
                    : FirstString(info, "gameId") ?? FirstString(obj, "matchId", "gameId");
                seconds = lolSecs.Value;
                mode = MapMode(gameId, FirstString(info, "gameMode"), ReadInt(info, "queueId"));
                timestamp = ReadTimestamp(info, "gameEndTimestamp", "gameStartTimestamp", "gameCreation");
                return true;
            }
        }

        if (TryGetObject(obj, "meta", out var hMeta) || TryGetObject(obj, "metadata", out hMeta))
        {
            var secs = ReadDurationSeconds(hMeta, "game_length", isMillis: false)
                ?? ReadDurationSeconds(hMeta, "gameLengthMillis", isMillis: true);
            if (secs is > 0)
            {
                gameId = gameHint ?? (hMeta.TryGetProperty("map", out _) || hMeta.TryGetProperty("mode", out _) ? "riot:valorant" : "");
                if (gameId.Length > 0)
                {
                    matchId = FirstString(hMeta, "id", "matchid", "matchId", "match_id");
                    seconds = secs.Value;
                    mode = MapMode(gameId, FirstString(hMeta, "mode", "queue", "queueId"), ReadInt(hMeta, "queueId"));
                    timestamp = ReadTimestamp(hMeta, "started_at", "game_start", "gameStartMillis");
                    return true;
                }
            }
        }

        var directMillis = ReadDurationSeconds(obj, "gameLengthMillis", isMillis: true);
        var directLength = ReadDurationSeconds(obj, "game_length", isMillis: false);
        var directLol = ReadLeagueDurationSeconds(obj);
        var directSeconds = directMillis ?? directLength ?? directLol ?? ReadDurationSeconds(obj, "durationSeconds", isMillis: false);
        if (directSeconds is > 0)
        {
            var inferred = gameHint ?? InferGameFromFlatObject(obj, directMillis.HasValue, directLength.HasValue, directLol.HasValue);
            if (inferred is not null)
            {
                gameId = inferred;
                matchId = FirstString(obj, "matchId", "match_id", "gameId", "id");
                seconds = directSeconds.Value;
                mode = MapMode(gameId, FirstString(obj, "queueId", "queue", "mode", "gameMode"), ReadInt(obj, "queueId"));
                timestamp = ReadTimestamp(obj, "gameEndTimestamp", "gameStartMillis", "gameStartTime", "game_datetime", "started_at", "gameCreation");
                return true;
            }
        }

        return false;
    }

    private static string? InferGameFromFlatObject(JsonElement obj, bool hasValMillis, bool hasTftLength, bool hasLolDuration)
    {
        if (obj.TryGetProperty("tft_set_number", out _) || obj.TryGetProperty("tft_game_type", out _))
            return "riot:teamfighttactics";
        if (hasValMillis) return "riot:valorant";
        if (hasLolDuration) return "riot:league_of_legends";
        if (hasTftLength)
        {
            if (FirstString(obj, "queueId", "mode") is { } mode &&
                !string.Equals(RiotModes.FromValorantQueue(mode), RiotModes.Other, StringComparison.Ordinal))
                return "riot:valorant";
            return "riot:teamfighttactics";
        }
        return null;
    }

    private static string? GameHintFromPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var lower = name.ToLowerInvariant();
        if (lower.Contains("valorant") || lower.Contains("/val_") || lower.StartsWith("val_", StringComparison.Ordinal))
            return "riot:valorant";
        if (lower.Contains("tft") || lower.Contains("teamfight"))
            return "riot:teamfighttactics";
        if (lower.Contains("runeterra") || lower.Contains("bacon") || lower.Contains("/lor_") || lower.StartsWith("lor_", StringComparison.Ordinal))
            return "riot:bacon";
        if (lower.Contains("2xko") || lower.Contains("lion"))
            return "riot:lion";
        if (lower.Contains("league") || lower.Contains("/lol_") || lower.StartsWith("lol_", StringComparison.Ordinal) || lower.Contains("lol-"))
            return "riot:league_of_legends";
        return null;
    }

    private static string? MapMode(string gameId, string? modeName, int? queueId)
    {
        if (gameId.Equals("riot:league_of_legends", StringComparison.OrdinalIgnoreCase))
        {
            if (queueId is > 0) return RiotModes.FromLeagueQueue(queueId.Value);
            return string.IsNullOrWhiteSpace(modeName) ? null : RiotModes.FromTrackerPlaylist(gameId, modeName);
        }
        if (gameId.Equals("riot:valorant", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(modeName)) return null;
            var direct = RiotModes.FromValorantQueue(modeName);
            return direct != RiotModes.Other ? direct : RiotModes.FromTrackerPlaylist(gameId, modeName);
        }
        return null;
    }

    private static int? ReadLeagueDurationSeconds(JsonElement obj)
    {
        if (!obj.TryGetProperty("gameDuration", out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var raw) || raw <= 0)
            return null;
        var hasEnd = obj.TryGetProperty("gameEndTimestamp", out _);
        var seconds = raw > 86400 || (!hasEnd && raw > 20000) ? (int)Math.Round(raw / 1000d) : (int)Math.Round(raw);
        return seconds is > 0 and <= MaxMatchSeconds ? seconds : null;
    }

    private static int? ReadDurationSeconds(JsonElement obj, string prop, bool isMillis)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var raw) || raw <= 0)
            return null;
        var seconds = isMillis || raw > 86400 ? (int)Math.Round(raw / 1000d) : (int)Math.Round(raw);
        return seconds is > 0 and <= MaxMatchSeconds ? seconds : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var num) && num > 0)
            {
                try
                {
                    var ms = num < 10_000_000_000L ? num * 1000L : num;
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }
                catch (ArgumentOutOfRangeException) { }
            }
            if (el.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        return null;
    }

    private static bool TryGetObject(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el)) continue;
            if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                return el.GetString();
            if (el.ValueKind == JsonValueKind.Number)
                return el.GetRawText();
        }
        return null;
    }

    private static byte[]? ReadBounded(Stream stream, long limit)
    {
        if (limit <= 0) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        long readTotal = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            readTotal += n;
            if (readTotal > limit) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static Result Fail(string message) =>
        new(false, new Dictionary<string, GameSeed>(), new Dictionary<string, int>(), null, message);
}
