using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ExoLauncher.Helpers;

/// <summary>
/// The daily send record behind crash report limits: at most <see cref="MaxPerDay"/> reports per UTC day, and one per
/// errorType+signature. The record holds the UTC day, the count and "errorType|hash" keys, where the hash is the signature's
/// SHA-256 cut to 16 hex characters, so no excerpt text is kept. No install id, account or name is ever stored.
/// </summary>
internal sealed class CrashReportLimit
{
    public const int MaxPerDay = 5;
    private const int MaxVersionKeys = 64;

    private readonly HashSet<string> _keys;
    private readonly HashSet<string> _versionKeys;

    private CrashReportLimit(string day, int count, IEnumerable<string> keys, string version = "", IEnumerable<string>? versionKeys = null)
    {
        Day = day;
        Count = count;
        _keys = new HashSet<string>(keys, StringComparer.Ordinal);
        Version = version;
        _versionKeys = new HashSet<string>(versionKeys ?? [], StringComparer.Ordinal);
    }

    public string Day { get; private set; }
    public int Count { get; private set; }
    public string Version { get; private set; }
    public IReadOnlyCollection<string> Keys => _keys;
    public IReadOnlyCollection<string> VersionKeys => _versionKeys;

    public static CrashReportLimit Empty(DateTime utcNow) => new(DayOf(utcNow), 0, []);

    /// <summary>A day already full: what an unreadable state file becomes, so that UTC day sends nothing.</summary>
    public static CrashReportLimit Exhausted(DateTime utcNow) => new(DayOf(utcNow), MaxPerDay, []);

    public static string DayOf(DateTime utcNow) => utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string KeyOf(string errorType, string signature) =>
        $"{errorType}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16]}";

    /// <summary>
    /// The signature of an excerpt: its first line that is neither a timestamp line nor the header (the header carries the
    /// minute, so it would change every send). That line is the error's own line, which repeats for the same error.
    /// </summary>
    public static string SignatureOf(string excerpt)
    {
        foreach (var raw in excerpt.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Exo Launcher ", StringComparison.Ordinal)) continue;
            if (line.Length > 1 && line[0] == '[' && char.IsAsciiDigit(line[1])) continue;
            return line;
        }
        return "";
    }

    /// <summary>Reserves a send for this errorType+signature on <paramref name="utcNow"/>'s UTC day. False when refused.</summary>
    public bool TryReserve(string errorType, string signature, DateTime utcNow, out string key, string? version = null)
    {
        key = KeyOf(errorType, signature);
        var day = DayOf(utcNow);
        if (day != Day)
        {
            Day = day;
            Count = 0;
            _keys.Clear();
        }
        if (!string.IsNullOrEmpty(version) && !string.Equals(version, Version, StringComparison.Ordinal))
        {
            Version = version;
            _versionKeys.Clear();
        }
        if (!string.IsNullOrEmpty(version) && _versionKeys.Contains(key)) return false;
        if (Count >= MaxPerDay || !_keys.Add(key)) return false;
        if (!string.IsNullOrEmpty(version) && _versionKeys.Count < MaxVersionKeys) _versionKeys.Add(key);
        Count++;
        return true;
    }

    /// <summary>Gives a reservation back, after a POST that did not succeed. A key from an earlier day is already gone.</summary>
    public void Release(string key)
    {
        if (_keys.Remove(key)) Count--;
        _versionKeys.Remove(key);
    }

    public string ToJson()
    {
        var keys = new JsonArray();
        foreach (var key in _keys) keys.Add(JsonValue.Create(key));
        var node = new JsonObject { ["day"] = Day, ["count"] = Count, ["keys"] = keys };
        if (!string.IsNullOrEmpty(Version))
        {
            var vKeys = new JsonArray();
            foreach (var key in _versionKeys) vKeys.Add(JsonValue.Create(key));
            node["version"] = Version;
            node["versionKeys"] = vKeys;
        }
        return node.ToJsonString();
    }

    /// <summary>Reads the record <see cref="ToJson"/> writes. Throws on anything else.</summary>
    public static CrashReportLimit Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Not an object.");
        var day = root["day"]!.GetValue<string>();
        var count = root["count"]!.GetValue<int>();
        var keys = (root["keys"] as JsonArray ?? throw new FormatException("No keys.")).Select(node => node!.GetValue<string>()).ToArray();
        if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || count < 0 || count > MaxPerDay || keys.Length > count)
            throw new FormatException("Out of range.");
        var version = root["version"]?.GetValue<string>() ?? "";
        var versionKeys = root["versionKeys"] is JsonArray vk ? vk.Select(node => node!.GetValue<string>()).Take(MaxVersionKeys).ToArray() : [];
        return new CrashReportLimit(day, count, keys, version, versionKeys);
    }
}

/// <summary>
/// The limit's state file, kept under Exo's data folder and written atomically (temp file, then move). Every failure is
/// absorbed: a read that fails for any reason but a missing file, or a corrupt file, counts as a full day, so no report is
/// sent that day; a write that fails refuses the send.
/// </summary>
public static class CrashReportLimiter
{
    private static readonly object Gate = new();

    public static string DefaultPath => Path.Combine(PathHelper.AppDataDir, "crash-report-limit.json");

    /// <summary>Reserves one send in the state file. False when refused; the caller then logs one line and sends nothing.</summary>
    public static bool TryReserve(string errorType, string signature, DateTime utcNow, out string key, string? version = null) =>
        TryReserve(errorType, signature, utcNow, DefaultPath, out key, version);

    /// <summary>Gives back a reservation whose POST did not succeed.</summary>
    public static void Release(string key, DateTime utcNow) => Release(key, utcNow, DefaultPath);

    internal static bool TryReserve(string errorType, string signature, DateTime utcNow, string path, out string key, string? version = null)
    {
        lock (Gate)
        {
            var state = Load(utcNow, path);
            var reserved = state.TryReserve(errorType, signature, utcNow, out key, version);
            // A reservation that is not saved would not survive a restart, so it is not sent.
            return reserved && Save(state, path);
        }
    }

    internal static void Release(string key, DateTime utcNow, string path)
    {
        lock (Gate)
        {
            var state = Load(utcNow, path);
            state.Release(key);
            Save(state, path);
        }
    }

    private static CrashReportLimit Load(DateTime utcNow, string path)
    {
        try
        {
            return File.Exists(path)
                ? CrashReportLimit.Parse(File.ReadAllText(path, Encoding.UTF8))
                : CrashReportLimit.Empty(utcNow);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Crash report limit file unreadable ({ex.GetType().Name}); no reports today.");
            var full = CrashReportLimit.Exhausted(utcNow);
            // Written back, so tomorrow's UTC day starts clean instead of reading the same corrupt file again.
            Save(full, path);
            return full;
        }
    }

    private static bool Save(CrashReportLimit state, string path)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, state.ToJson(), Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Crash report limit file not saved ({ex.GetType().Name}).");
            try { DeletionGate.DeleteFile(temp); } catch { /* best-effort */ }
            return false;
        }
    }
}
