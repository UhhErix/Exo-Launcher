using System.Text;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Unreal Engine 3's ini files, as Rocket League's TASystemSettings.ini: "[Section]" headers, "Key=Value" lines, ";"
/// comments, CRLF, ANSI without a BOM, and [IniVersion] last, whose timestamps make the engine rebuild the whole file
/// from its defaults when they no longer match. In play the engine reads one section ([SystemSettings]); the rest are
/// profiles it bases on that one (Screenshot, SplitScreen2, Mobile...). So a setting line here is a key the profile
/// names inside the live section only, with the values Exo knows the engine takes; any other section, [IniVersion], an
/// unknown key, a comment or a merge command ("+Key=", "-Key=") is never one, so it is never written. The bytes are read
/// one to one (Latin-1), so a character outside ASCII anywhere in the file goes back exactly as it was; a UTF-16 file
/// (the engine writes one only for a value outside ANSI) has no setting lines. Unreal Engine 5's GameUserSettings.ini
/// (Phantom Blade Zero's) is the same format, its live section the game's settings class
/// ("[/Script/ProjectPBZ.PBZSettingsLocal]"); its first line, ";METADATA=(Diff=true, UseCommands=true)", is a comment.
/// A game whose Video menu is that file has two (Hell is Us: its class and [ScalabilityGroups], with dotted keys, "sg.ShadowQuality").
/// </summary>
internal static partial class GameSettings
{
    /// <summary>A key the engine reads as true or false; written in the casing the line already has ("false", "True", "FALSE").</summary>
    internal const string Toggle = "true or false";

    [GeneratedRegex(@"^[ \t]*(?<name>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*=[ \t]*(?<value>.*?)[ \t]*$")]
    private static partial Regex IniLine();

    /// <summary>
    /// The file's lines, its setting lines being <paramref name="keys"/> in <paramref name="section"/>, each allowed what
    /// its entry says: <see cref="Toggle"/> or "low to high".
    /// </summary>
    internal static SettingsFile ParseIni(byte[] bytes, string section, IReadOnlyDictionary<string, string> keys) => ParseIni(bytes, [section], keys);

    /// <summary>The same, with live lines in each of these sections.</summary>
    internal static SettingsFile ParseIni(byte[] bytes, IReadOnlyCollection<string> sections, IReadOnlyDictionary<string, string> keys)
    {
        var latin1 = Encoding.Latin1;
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) return new SettingsFile([latin1.GetString(bytes)], "\r\n", false, [], latin1);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = latin1.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine);
        var settings = new List<Line>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var live = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                live = sections.Contains(trimmed[1..^1].Trim(), StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (!live || IniLine().Match(lines[i]) is not { Success: true } m || !keys.TryGetValue(m.Groups["name"].Value, out var allowed)) continue;
            var name = m.Groups["name"].Value;
            var again = seen.GetValueOrDefault(name);
            seen[name] = again + 1;
            var value = m.Groups["value"];
            settings.Add(new Line(i, name, again == 0 ? name : $"{name}#{again}", value.Value,
                allowed == Toggle ? ToggleChoices(value.Value) : allowed, value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings, latin1);
    }

    /// <summary>
    /// The file's [IniVersion] as it stands ("0=1792002878.000000|1=1792002878.000000"): the write times of the
    /// Default*.ini the engine built it from, from which it rebuilds the whole file once they no longer match (UE3's
    /// appCheckIniForOutdatedness). Null without one.
    /// </summary>
    internal static string? IniVersion(SettingsFile file)
    {
        var at = Array.FindIndex(file.Lines, line => line.Trim().Equals("[IniVersion]", StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null
            : string.Join('|', file.Lines.Skip(at + 1).Select(line => line.Trim()).TakeWhile(line => !line.StartsWith('[')).Where(line => line.Length > 0));
    }

    /// <summary>True and False as the line spells them, so a write keeps its casing.</summary>
    private static string ToggleChoices(string value) =>
        value.Length > 0 && value.All(char.IsLower) ? "one of true, false"
        : value.Length > 1 && value.All(char.IsUpper) ? "one of TRUE, FALSE"
        : "one of True, False";
}
