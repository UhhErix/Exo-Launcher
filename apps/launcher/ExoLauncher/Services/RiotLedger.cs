using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Services;

/// <summary>
/// One game's seed from tracker.gg: its lifetime minutes and the minutes per mode (names from <see cref="RiotModes"/>).
/// Modes may be absent when tracker.gg did not split the total. Partial: only its playlist sum, not its lifetime total.
/// </summary>
internal sealed record GameSeed(int Minutes, IReadOnlyDictionary<string, int>? Modes = null, bool Partial = false);

/// <summary>
/// Riot play time Exo keeps for good: one append-only file per Riot ID, <c>riot-ledger/&lt;account&gt;.jsonl</c>, plain data
/// with one JSON object per line. A seed line is tracker.gg's lifetime total for a game, with its per-mode split,
/// read when a Riot ID is added (or again by hand, which adds a newer seed). A play line is the whole minutes Exo counted
/// of one session chunk, with the mode the client reported for it (<see cref="RiotModes.Unknown"/> when none). Every mode
/// counts: a session is the whole time a game runs. Lifetime is the latest seed plus the play lines that start at or after
/// it; a line's key makes a repeat count once. The file holds no Riot ID, only its account key, so it can sync as it is.
/// </summary>
internal static partial class RiotLedger
{
    internal const int SchemaVersion = 1;
    internal const string TrackerSource = "tracker.gg";
    internal const string ManualSource = "manual";
    internal const string CloudSource = "exo-riot";
    internal const string ExportSource = "riot-export";

    /// <summary>
    /// The ledger for sessions played before any Riot ID is set on this PC. It is local only (never synced) and moves into
    /// the Riot ID's ledger once that ID is set (<see cref="ClaimUnassigned"/>). Not a 20-hex account key, so it never merges.
    /// </summary>
    internal const string Unassigned = "unassigned";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Document> Documents = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    // Set by a restore or import: the ledger on disk is the restored one, so this run appends nothing more to it.
    private static string? _restoredFileKeptFor;

    private sealed record SeedLine(int Minutes, DateTimeOffset ImportedAt, IReadOnlyDictionary<string, int>? Modes, bool Partial, string Source = TrackerSource);

    private sealed record PlayLine(string Game, DateTimeOffset StartUtc, int Minutes, string Mode);

    private sealed class Document
    {
        public required string Path { get; init; }
        public required string Account { get; init; }
        public Dictionary<string, SeedLine> Seeds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PlayLine> Plays { get; } = [];
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Session
    {
        public required string Account { get; set; }
        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset CountedTo { get; set; }
        public string Mode { get; set; } = RiotModes.Unknown;
        public int Sequence { get; set; }
    }

    /// <summary>The account key a Riot ID's ledger is kept under: a hash, so the Riot ID itself never reaches the file.</summary>
    internal static string? AccountKey(string? riotId)
    {
        var normalized = riotId?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..20];
    }

    internal static bool HasSeed(string account)
    {
        lock (Gate) return Open(account).Seeds.Count > 0;
    }

    /// <summary>Writes tracker.gg's seed for every game with minutes as this Riot ID's seed. False when nothing was written.</summary>
    internal static bool Seed(string account, IReadOnlyDictionary<string, GameSeed> seeds, DateTimeOffset importedAt) =>
        Seed(account, seeds, importedAt, TrackerSource);

    /// <summary>Writes a seed line for every Riot game with positive minutes. False when nothing was written.</summary>
    internal static bool Seed(string account, IReadOnlyDictionary<string, GameSeed> seeds, DateTimeOffset importedAt, string source)
    {
        var effectiveSource = string.IsNullOrWhiteSpace(source) ? TrackerSource : source;
        lock (Gate) return WriteSeeds(Open(account), seeds, importedAt, effectiveSource);
    }

    internal static bool SeedIfHigher(string account, IReadOnlyDictionary<string, GameSeed> seeds, DateTimeOffset importedAt, string source)
    {
        var effectiveSource = string.IsNullOrWhiteSpace(source) ? CloudSource : source;
        lock (Gate)
        {
            var doc = Open(account);
            var higher = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
            foreach (var (game, seed) in seeds)
            {
                if (!game.StartsWith("riot:", StringComparison.OrdinalIgnoreCase) || seed.Minutes <= 0 || seed.Minutes > MaxSeedMinutes)
                    continue;
                var current = doc.Seeds.TryGetValue(game, out var existing) ? existing.Minutes : 0;
                if (seed.Minutes > current) higher[game] = seed;
            }
            return higher.Count > 0 && WriteSeeds(doc, higher, importedAt, effectiveSource);
        }
    }

    /// <summary>The latest baseline minutes saved per Riot game for this account (excluding Exo's own session rows).</summary>
    internal static IReadOnlyDictionary<string, int> Baselines(string account)
    {
        lock (Gate)
        {
            var doc = Open(account);
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (game, seed) in doc.Seeds) map[game] = seed.Minutes;
            return map;
        }
    }

    /// <summary>Opens a session. Start is moved up to the last minute already counted for the game, so a restart never counts it twice.</summary>
    internal static void Begin(string account, string game, DateTimeOffset start)
    {
        lock (Gate)
        {
            if (Sessions.ContainsKey(game)) return;
            var doc = Open(account);
            var counted = doc.Plays.Where(play => play.Game == game).Select(play => play.StartUtc.AddMinutes(play.Minutes)).DefaultIfEmpty().Max();
            var from = counted > start ? counted : start;
            Sessions[game] = new Session { Account = account, Start = from, CountedTo = from };
        }
    }

    /// <summary>Sets the mode the client reports for an open session; the chunks written from now on carry it.</summary>
    internal static void SetMode(string game, string mode)
    {
        lock (Gate)
            if (Sessions.TryGetValue(game, out var session)) session.Mode = mode;
    }

    /// <summary>Writes the whole minutes every open session has played up to <paramref name="now"/>; the rest waits for the next.</summary>
    internal static void Checkpoint(DateTimeOffset now)
    {
        lock (Gate)
            foreach (var (game, session) in Sessions.ToArray())
                Flush(game, session, now);
    }

    /// <summary>Writes the session's last minutes through <paramref name="end"/> and closes it. A game with no session is a no-op.</summary>
    internal static void End(string game, DateTimeOffset end)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(game, out var session)) return;
            Flush(game, session, end);
            Sessions.Remove(game);
        }
    }

    /// <summary>Closes a session without counting its unwritten minutes (a helper process that exits at once).</summary>
    internal static void Cancel(string game)
    {
        lock (Gate) Sessions.Remove(game);
    }

    /// <summary>
    /// Moves the sessions played before a Riot ID was set into that ID's ledger, once. Each line is added by its key, so a
    /// repeat claim adds nothing; the unassigned file goes after its lines are in. Sessions still open are re-pointed too.
    /// </summary>
    internal static void ClaimUnassigned(string account)
    {
        lock (Gate)
        {
            foreach (var session in Sessions.Values)
                if (session.Account == Unassigned) session.Account = account;

            var path = System.IO.Path.Combine(PathHelper.AppDataDir, "riot-ledger", Unassigned + ".jsonl");
            if (!File.Exists(path)) return;
            var to = Open(account);
            try
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var row = ParseRow(line);
                    if (row is null || to.Keys.Contains(row["key"]!.GetValue<string>())) continue;
                    if (!Append(to, row)) return; // the file stays; the next claim tries again
                    SyncLine(to, row);
                    Apply(to, line);
                }
                Documents.Remove(Unassigned);
                DeletionGate.DeleteFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Riot play time from before the Riot ID was not moved: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>A ledger line as an object with a string key, or null for a torn or foreign line.</summary>
    private static JsonObject? ParseRow(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject row && row["key"] is JsonValue key && key.TryGetValue(out string? _) ? row : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether a game's latest seed is a playlist sum rather than tracker.gg's lifetime total (see <see cref="GameSeed.Partial"/>).</summary>
    internal static bool IsPartial(string account, string game)
    {
        lock (Gate) return Open(account).Seeds.GetValueOrDefault(game)?.Partial ?? false;
    }

    /// <summary>
    /// Lays a riot-ledger item pulled from the account over this PC's ledger: its line is added once, by its key, and never
    /// written back to the sync. The value carries its account key (a 20-character hash, checked before any file is named).
    /// </summary>
    internal static void Merge(string value)
    {
        var line = JsonNode.Parse(value)?.AsObject() ?? throw new FormatException("Not a ledger line.");
        var account = line["account"]?.GetValue<string>() ?? "";
        line.Remove("account");
        if (!AccountKeyRule().IsMatch(account) || !IsWellFormed(line)) return;
        var key = line["key"]?.GetValue<string>() ?? "";
        if (key.Length == 0) return;
        lock (Gate)
        {
            var doc = Open(account);
            if (doc.Keys.Contains(key)) return;
            if (!Append(doc, line)) return;
            Apply(doc, line.ToJsonString());
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{20}$")]
    private static partial System.Text.RegularExpressions.Regex AccountKeyRule();

    /// <summary>Lifetime minutes for one game on one Riot ID: the latest seed plus the play lines since it (all of them without a seed).</summary>
    internal static int Totals(string account, string game) => (int)Math.Min(int.MaxValue, ModeTotals(account, game).Values.Sum(minutes => (long)minutes));

    /// <summary>The most minutes a seed may hold (a whole game's tracker.gg total), and the most one play line may (a week).</summary>
    internal const int MaxSeedMinutes = 10_000_000;
    internal const int MaxSessionMinutes = 10_080;

    /// <summary>A ledger line as the file holds it (or a backup's copy of one), when it is one this Launcher would write.</summary>
    internal static bool IsWellFormedLine(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject line && IsWellFormed(line); }
        catch (JsonException) { return false; }
    }

    /// <summary>A line is one this Launcher writes: schema 1, a play or seed, a Riot title, and minutes within the cap for its kind.</summary>
    private static bool IsWellFormed(JsonObject line)
    {
        if (!(line["schema"] is JsonValue schema && schema.TryGetValue(out int version) && version == SchemaVersion)) return false;
        if (!(line["kind"] is JsonValue kind && kind.TryGetValue(out string? type) && type is "play" or "seed")) return false;
        if (!(line["game"] is JsonValue game && game.TryGetValue(out string? title) && title.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))) return false;
        return line["minutes"] is JsonValue minutes && minutes.TryGetValue(out int count)
            && count >= 1 && count <= (type == "seed" ? MaxSeedMinutes : MaxSessionMinutes);
    }

    /// <summary>
    /// Lifetime minutes per mode for one game: the seed's split plus the tagged play lines since it. Seed minutes tracker.gg
    /// did not split go under <see cref="RiotModes.Unknown"/>, so the modes add up to <see cref="Totals"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> ModeTotals(string account, string game)
    {
        lock (Gate)
        {
            var doc = Open(account);
            doc.Seeds.TryGetValue(game, out var seed);
            var byMode = new Dictionary<string, int>(StringComparer.Ordinal);
            if (seed is not null)
            {
                var split = CapModes(seed.Minutes, seed.Modes);
                foreach (var (mode, minutes) in split) byMode[mode] = minutes;
                var named = split.Values.Sum(minutes => (long)minutes);
                if (seed.Minutes - named > 0) byMode[RiotModes.Unknown] = (int)(seed.Minutes - named);
            }
            foreach (var play in doc.Plays)
            {
                // Automated lifetime imports already include sessions played before the snapshot; manual starting hours add prior hours without dropping tracked sessions.
                if (play.Game != game || (seed is not null && !string.Equals(seed.Source, ManualSource, StringComparison.OrdinalIgnoreCase) && play.StartUtc < seed.ImportedAt)) continue;
                byMode[play.Mode] = (int)Math.Min(int.MaxValue, (long)byMode.GetValueOrDefault(play.Mode) + play.Minutes);
            }
            return byMode;
        }
    }

    /// <summary>
    /// A seed's per-mode split, scaled down in proportion when it adds up above the seed's total, so the modes never count
    /// more than tracker.gg's total. Modes at or below the total keep their minutes.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> CapModes(int total, IReadOnlyDictionary<string, int>? modes)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (modes is null) return result;
        var named = modes.Values.Where(minutes => minutes > 0).Sum(minutes => (long)minutes);
        foreach (var (mode, minutes) in modes)
        {
            if (minutes <= 0) continue;
            result[mode] = named > total ? (int)(minutes * (long)total / named) : minutes;
        }
        return result;
    }

    /// <summary>Drops what is in memory, as a restart would. The files are read again on the next use.</summary>
    internal static void ForgetLoaded()
    {
        lock (Gate)
        {
            Documents.Clear();
            Sessions.Clear();
            _restoredFileKeptFor = null;
        }
    }

    /// <summary>A restore or import has written the ledger files: this run appends nothing more to them.</summary>
    internal static void KeepRestoredFile()
    {
        lock (Gate) _restoredFileKeptFor = PathHelper.AppDataDir;
    }

    private static void Flush(string game, Session session, DateTimeOffset to)
    {
        var whole = (int)Math.Floor((to - session.CountedTo).TotalMinutes);
        if (whole <= 0) return;
        var doc = Open(session.Account);
        // The key is the session's start and its sequence: a line written twice (a retried write) reads as one.
        var key = game + "|" + session.Start.ToString("O", CultureInfo.InvariantCulture) + "|" + session.Sequence.ToString(CultureInfo.InvariantCulture);
        var row = new JsonObject
        {
            ["schema"] = SchemaVersion,
            ["kind"] = "play",
            ["key"] = key,
            ["game"] = game,
            ["mode"] = session.Mode,
            ["startUtc"] = session.CountedTo.ToString("O", CultureInfo.InvariantCulture),
            ["minutes"] = whole,
            ["source"] = "exo",
        };
        if (!Append(doc, row)) return;
        SyncLine(doc, row);
        doc.Keys.Add(key);
        doc.Plays.Add(new PlayLine(game, session.CountedTo, whole, session.Mode));
        session.CountedTo = session.CountedTo.AddMinutes(whole);
        session.Sequence++;
    }

    private static bool WriteSeeds(Document doc, IReadOnlyDictionary<string, GameSeed> seeds, DateTimeOffset importedAt, string source)
    {
        var written = false;
        var manual = string.Equals(source, ManualSource, StringComparison.OrdinalIgnoreCase);
        foreach (var (game, seed) in seeds.Where(pair => pair.Key.StartsWith("riot:", StringComparison.OrdinalIgnoreCase) && pair.Value.Minutes > 0 && pair.Value.Minutes <= MaxSeedMinutes))
        {
            var effectiveAt = manual && doc.Seeds.TryGetValue(game, out var existing) && importedAt <= existing.ImportedAt
                ? existing.ImportedAt.AddTicks(1)
                : importedAt;
            var key = "seed|" + game + "|" + effectiveAt.ToString("O", CultureInfo.InvariantCulture);
            if (doc.Keys.Contains(key)) continue;
            var row = new JsonObject
            {
                ["schema"] = SchemaVersion,
                ["kind"] = "seed",
                ["key"] = key,
                ["game"] = game,
                ["minutes"] = seed.Minutes,
                ["importedAt"] = effectiveAt.ToString("O", CultureInfo.InvariantCulture),
                ["source"] = source,
            };
            if (seed.Modes is { Count: > 0 } modes)
            {
                var split = new JsonObject();
                foreach (var (mode, minutes) in modes.Where(pair => pair.Value > 0))
                    split[mode] = minutes;
                row["modes"] = split;
            }
            if (seed.Partial) row["partial"] = true;
            if (!Append(doc, row)) continue;
            SyncLine(doc, row);
            doc.Keys.Add(key);
            doc.Seeds[game] = new SeedLine(seed.Minutes, effectiveAt, seed.Modes, seed.Partial, source);
            written = true;
        }
        return written;
    }

    private static bool Append(Document doc, JsonObject row)
    {
        if (_restoredFileKeptFor == PathHelper.AppDataDir) return false;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(doc.Path)!);
            // A torn write leaves a last line with no newline; this row starts a line of its own, or it is lost with the torn one.
            var lead = EndsWithoutNewline(doc.Path) ? "\n" : "";
            File.AppendAllText(doc.Path, lead + row.ToJsonString() + "\n", Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Riot play time was not written: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Sends a line this PC wrote to the account's sync as a riot-ledger item (CLIENT.md 8). The line's id is the same on every PC.</summary>
    private static void SyncLine(Document doc, JsonObject row)
    {
        if (doc.Account == Unassigned) return; // before a Riot ID, the minutes are this PC's until ClaimUnassigned moves them
        var value = JsonNode.Parse(row.ToJsonString())!.AsObject();
        value["account"] = doc.Account;
        AccountSyncHooks.Current?.Write("riot-ledger", AccountSyncRules.RiotLineId(doc.Account, row["key"]!.GetValue<string>()), value.ToJsonString());
    }

    private static bool EndsWithoutNewline(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private static Document Open(string account)
    {
        if (Documents.TryGetValue(account, out var known)) return known;
        var doc = new Document { Path = System.IO.Path.Combine(PathHelper.AppDataDir, "riot-ledger", account + ".jsonl"), Account = account };
        var complete = true;
        try
        {
            if (File.Exists(doc.Path))
                foreach (var line in File.ReadLines(doc.Path, Encoding.UTF8))
                    Apply(doc, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read that stopped part-way is not the ledger: it is used for this call only, and the next read starts again.
            complete = false;
            AppLog.Warn("Riot play time could not be read: " + ex.GetType().Name);
        }
        if (complete)
        {
            if (doc.Seeds.Count == 0) MigrateImport(doc, account);
            Documents[account] = doc;
        }
        return doc;
    }

    private static void Apply(Document doc, string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != SchemaVersion) return;
            var kind = root.GetProperty("kind").GetString();
            var key = root.GetProperty("key").GetString() ?? "";
            var game = root.GetProperty("game").GetString() ?? "";
            var minutes = root.GetProperty("minutes").GetInt32();
            if (!doc.Keys.Add(key) || minutes <= 0) return;
            if (kind == "seed")
            {
                var at = DateTimeOffset.Parse(root.GetProperty("importedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (doc.Seeds.TryGetValue(game, out var latest) && at <= latest.ImportedAt) return;
                var partial = root.TryGetProperty("partial", out var flag) && flag.ValueKind == JsonValueKind.True;
                var source = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
                    ? src.GetString() ?? TrackerSource
                    : TrackerSource;
                doc.Seeds[game] = new SeedLine(minutes, at, ReadModes(root), partial, source);
            }
            else if (kind == "play")
            {
                var at = DateTimeOffset.Parse(root.GetProperty("startUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? RiotModes.Unknown : RiotModes.Unknown;
                doc.Plays.Add(new PlayLine(game, at, minutes, mode));
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            // A torn or foreign line: skipped, the rest of the document still counts.
        }
    }

    private static IReadOnlyDictionary<string, int>? ReadModes(JsonElement root)
    {
        if (!root.TryGetProperty("modes", out var split) || split.ValueKind != JsonValueKind.Object) return null;
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in split.EnumerateObject())
            if (property.Value.TryGetInt32(out var minutes) && minutes > 0) modes[property.Name] = minutes;
        return modes.Count == 0 ? null : modes;
    }

    /// <summary>
    /// The hours file from before the ledger (tracker.gg's total at its observed time, for the Riot ID in effect) becomes
    /// that Riot ID's seed, so an update keeps the hours it already showed. Its file is removed once the seed is written;
    /// another Riot ID's file is left alone for when that ID is used again.
    /// </summary>
    private static void MigrateImport(Document doc, string account)
    {
        var legacy = System.IO.Path.Combine(PathHelper.AppDataDir, "exo-imported-lifetime.json");
        try
        {
            if (!File.Exists(legacy)) return;
            using var json = JsonDocument.Parse(File.ReadAllText(legacy));
            var root = json.RootElement;
            if (!root.TryGetProperty("accountKey", out var key) || key.GetString() != account) return;
            if (!root.TryGetProperty("observedAt", out var observed) ||
                !DateTimeOffset.TryParse(observed.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ||
                !root.TryGetProperty("minutes", out var minutes) || minutes.ValueKind != JsonValueKind.Object)
                return;
            // Two spellings of one game are possible in a hand-edited file: the larger reading is kept, and nothing throws.
            var seeds = new Dictionary<string, GameSeed>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in minutes.EnumerateObject())
            {
                if (!pair.Name.StartsWith("riot:", StringComparison.OrdinalIgnoreCase) || !pair.Value.TryGetInt32(out var value) || value <= 0) continue;
                if (!seeds.TryGetValue(pair.Name, out var prior) || prior.Minutes < value) seeds[pair.Name] = new GameSeed(value);
            }
            if (seeds.Count > 0 && WriteSeeds(doc, seeds, at, TrackerSource))
                DeletionGate.DeleteFile(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            AppLog.Debug("Riot hours from before the ledger were not moved: " + ex.GetType().Name);
        }
    }
}
