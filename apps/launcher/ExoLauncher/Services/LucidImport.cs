using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo EQ takes from Lucid's own settings (<c>%APPDATA%\LucidEQ\settings.json</c>, read once, never written) the first
/// time it starts: the headphone Lucid was tuned for and the outputs its block in config.txt named, its switch, Night Shift,
/// and the games Lucid's Game Mode gave their own EQ, as Exo's Footsteps sound for the same game in Exo's library. Lucid's
/// own tunings (its signature profiles, its game EQs and its plugin) are Lucid's and stay out. Pure (LucidImportTests).
/// Ways it could go wrong: a file that isn't Lucid's shape throws or imports half; Lucid IEMs (Lucid's own earphones, with
/// no AutoEq result) name a headphone; a game is matched by a word it only contains ("Rust" in "Rusty Lake"); a game Lucid
/// knows but Exo doesn't have is invented; an output the player set by hand in Exo is overwritten.
/// </summary>
internal static class LucidImport
{
    /// <summary>Lucid's game names to the titles stores give them; a name ending in "*" is a series (every title that starts so).</summary>
    private static readonly Dictionary<string, string[]> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CS2"] = ["Counter-Strike 2"],
        ["R6 Siege"] = ["Tom Clancy's Rainbow Six Siege", "Rainbow Six Siege", "Tom Clancy's Rainbow Six Siege X"],
        ["PUBG"] = ["PUBG: BATTLEGROUNDS", "PUBG"],
        ["DOTA 2"] = ["Dota 2"],
        ["Valorant"] = ["VALORANT"],
        ["Call of Duty"] = ["Call of Duty*"],
        ["Battlefield"] = ["Battlefield*"],
        ["Hunt Showdown"] = ["Hunt: Showdown*"],
        ["The Finals"] = ["THE FINALS"],
        ["Apex Legends"] = ["Apex Legends"],
        ["Overwatch"] = ["Overwatch*"],
    };

    /// <summary>
    /// Lays Lucid's settings over <paramref name="store"/>. <paramref name="config"/> is APO's config.txt (null without it),
    /// <paramref name="playing"/> the output Windows plays on now (its endpoint id), for a Lucid block that named none.
    /// </summary>
    internal static void Into(EqStore store, JsonObject lucid, string? config, IReadOnlyList<(string Id, string Title)> games, string? playing)
    {
        store.LucidImported = true;
        // AudioSourceType 1 is a headphone from Lucid's list; 0 is Lucid's own IEMs, which AutoEq has no result for.
        if (Int(lucid, "AudioSourceType") == 1 && Bool(lucid, "HasCompletedAudioSourceOnboarding", false) && Text(lucid, "SelectedHeadphoneName") is { Length: > 0 } name)
        {
            store.LucidHeadphone = name;
            var devices = config is null ? null : LucidDevices(config);
            store.LucidOutputs = devices is { All: false, Guids.Length: > 0 } named ? [.. named.Guids.Where(ExoEqConfig.IsGuid)]
                : playing is not null && ExoEqConfig.IsGuid(playing) ? [playing] : [];
        }
        store.On = Bool(lucid, "LastEqState", true);
        store.Night.On = Bool(lucid, "NightShiftEnabled", false);
        store.Night.Sync = Bool(lucid, "NightShiftSyncWithWindows", false);
        if (Clock(Text(lucid, "NightShiftStartTime")) is { } start) store.Night.Start = start;
        if (Clock(Text(lucid, "NightShiftEndTime")) is { } end) store.Night.End = end;
        // Game Mode on: each game Lucid gave its own EQ gets Exo's Footsteps sound, unless the player chose one in Exo.
        if (Bool(lucid, "OptimizeForEsports", Bool(lucid, "AutoDetectionEnabled", true)) && lucid["GameSettings"] is JsonObject perGame)
            foreach (var (lucidName, value) in perGame)
            {
                if (value is not JsonObject game || !Bool(game, "UseGameSpecificEq", false) || !Bool(game, "AutoDetectEnabled", true)) continue;
                foreach (var (id, _) in games.Where(g => Same(lucidName, g.Title)))
                    store.Games.TryAdd(id, ExoEqSounds.Footsteps);
            }
    }

    /// <summary>
    /// Lucid's headphone, once AutoEq's list is in: the outputs it was on get it, unless the player already picked one there.
    /// True when it was found (or can never be: the name is then dropped).
    /// </summary>
    internal static bool Resolve(EqStore store, IReadOnlyList<AutoEqCatalog.Headphone> list, Func<AutoEqCatalog.Headphone, EqCorrection?> correction)
    {
        if (store.LucidHeadphone is not { } name) return false;
        store.LucidHeadphone = null;
        if (AutoEqCatalog.Named(name, list) is not { } found || correction(found) is not { } picked) return false;
        foreach (var guid in store.LucidOutputs)
        {
            if (!store.Outputs.TryGetValue(guid, out var output)) store.Outputs[guid] = output = new EqOutput();
            output.Headphone ??= picked;
        }
        store.LucidOutputs = [];
        return true;
    }

    /// <summary>
    /// The outputs Lucid's block ("# Lucid EQ begin" to "# Lucid EQ end") applied its EQ to: endpoint ids from its Device
    /// lines, or all; null when there is no block or it names none before its Include.
    /// </summary>
    internal static (bool All, string[] Guids)? LucidDevices(string config)
    {
        var inside = false;
        var guids = new List<string>();
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("# Lucid EQ begin", StringComparison.OrdinalIgnoreCase)) { inside = true; continue; }
            if (line.StartsWith("# Lucid EQ end", StringComparison.OrdinalIgnoreCase)) break;
            if (line.StartsWith(ExoEqConfig.LucidOffMark, StringComparison.Ordinal)) line = line[ExoEqConfig.LucidOffMark.Length..].Trim();
            if (!inside || line.StartsWith('#')) continue;
            if (line.StartsWith("Include:", StringComparison.OrdinalIgnoreCase)) return guids.Count == 0 ? null : (false, [.. guids]);
            if (!line.StartsWith("Device:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["Device:".Length..].Trim();
            if (value.Equals("all", StringComparison.OrdinalIgnoreCase)) return (true, []);
            // A Device line lists ids or names separated by semicolons; Lucid writes endpoint ids.
            guids.Clear();
            guids.AddRange(value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(one => one.StartsWith('{')));
        }
        return null;
    }

    /// <summary>Whether a store's title is the game Lucid names.</summary>
    internal static bool Same(string lucid, string title)
    {
        var wanted = Titles.TryGetValue(lucid, out var known) ? known : [lucid];
        var plain = Plain(title);
        return wanted.Any(w => w.EndsWith('*') ? plain.StartsWith(Plain(w[..^1]), StringComparison.Ordinal) : plain == Plain(w));
    }

    private static string Plain(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static string? Clock(string text) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time.TotalHours < 24
            ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : null;

    private static bool Bool(JsonObject root, string name, bool fallback) =>
        root[name]?.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };

    private static int Int(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static string Text(JsonObject root, string name) =>
        root[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
}
