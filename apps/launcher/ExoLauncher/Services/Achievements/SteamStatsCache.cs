using ExoLauncher.Helpers;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Steam's own record of a game's achievements on this PC, read as the Steam client writes it the moment one unlocks
/// (appcache/stats): UserGameStats_{account}_{app}.bin holds each achievement stat's bit field and each unlocked bit's
/// time; UserGameStatsSchema_{app}.bin names every bit (api name, display name and description per language, icons,
/// hidden). No Web API key, no network: what the live watch (<see cref="AchievementWatch"/>) compares while a game runs.
/// </summary>
internal static class SteamStatsCache
{
    /// <summary>One achievement as the schema names it. Icon and IconGray are full CDN URLs (<see cref="IconUrl"/>).</summary>
    internal sealed record Entry(string ApiName, string Name, string Description, bool Hidden, string? Icon, string? IconGray);

    /// <summary>The unlocked achievements' api names with when each unlocked (UTC); null when the file can't be read.</summary>
    internal static Dictionary<string, DateTimeOffset>? Unlocked(byte[] stats, IReadOnlyDictionary<(int Stat, int Bit), Entry> schema)
    {
        if (ValveKv.Read(stats).Child("cache") is not { } cache) return null;
        var unlocked = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (key, value) in cache)
        {
            if (!int.TryParse(key, out var stat) || value is not Dictionary<string, object> node) continue;
            var data = node.Number("data") ?? 0;
            var times = node.Child("AchievementTimes");
            for (var bit = 0; bit < 32; bit++)
            {
                if ((data & (1L << bit)) == 0 || !schema.TryGetValue((stat, bit), out var entry)) continue;
                var at = times.Number(bit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                unlocked[entry.ApiName] = at is > 0 ? DateTimeOffset.FromUnixTimeSeconds(at.Value) : DateTimeOffset.UnixEpoch;
            }
        }
        return unlocked;
    }

    /// <summary>
    /// The schema's achievements by (stat, bit), in <paramref name="language"/> (Steam's names: english, german,
    /// schinese…), English where the game has none in it. Null when the file can't be read.
    /// </summary>
    internal static Dictionary<(int Stat, int Bit), Entry>? Schema(byte[] schema, string appId, string language)
    {
        var root = ValveKv.Read(schema);
        if (root.Child(appId).Child("stats") is not { } stats) return null;
        var entries = new Dictionary<(int, int), Entry>();
        foreach (var (statKey, statValue) in stats)
        {
            if (!int.TryParse(statKey, out var stat) || (statValue as Dictionary<string, object>).Child("bits") is not { } bits) continue;
            foreach (var (bitKey, bitValue) in bits)
            {
                if (!int.TryParse(bitKey, out var bit) || bit is < 0 or > 31 || bitValue is not Dictionary<string, object> node || node.Str("name") is not { } api) continue;
                var display = node.Child("display");
                string Pick(string field) => display.Child(field) is { } text ? text.Str(language) ?? text.Str("english") ?? "" : "";
                string? Icon(string field) => display.Str(field) is { Length: > 0 } file && !file.Contains('/') && !file.Contains('\\')
                    ? IconUrl(appId, file) : null;
                entries[(stat, bit)] = new Entry(api, Pick("name"), Pick("desc"), display.Number("hidden") == 1, Icon("icon"), Icon("icon_gray"));
            }
        }
        return entries;
    }

    /// <summary>Where Steam serves an achievement's icon today, 256 px (the older steamcommunity/public path answers 404 for new games).</summary>
    internal static string IconUrl(string appId, string file) => $"https://shared.steamstatic.com/community_assets/images/apps/{appId}/{file}";

    /// <summary>The Steam language for Windows' display language ("english" when Steam has no name for it).</summary>
    internal static string Language(string? culture) => (culture ?? "").ToLowerInvariant() switch
    {
        var c when c.StartsWith("de", StringComparison.Ordinal) => "german",
        var c when c.StartsWith("fr", StringComparison.Ordinal) => "french",
        var c when c.StartsWith("it", StringComparison.Ordinal) => "italian",
        var c when c.StartsWith("ko", StringComparison.Ordinal) => "koreana",
        var c when c.StartsWith("es", StringComparison.Ordinal) => "spanish",
        var c when c.StartsWith("ru", StringComparison.Ordinal) => "russian",
        var c when c.StartsWith("ja", StringComparison.Ordinal) => "japanese",
        "pt-br" => "brazilian",
        var c when c.StartsWith("pt", StringComparison.Ordinal) => "portuguese",
        var c when c.StartsWith("pl", StringComparison.Ordinal) => "polish",
        var c when c.StartsWith("nl", StringComparison.Ordinal) => "dutch",
        var c when c.StartsWith("tr", StringComparison.Ordinal) => "turkish",
        "zh-tw" or "zh-hk" => "tchinese",
        var c when c.StartsWith("zh", StringComparison.Ordinal) => "schinese",
        _ => "english",
    };
}
