using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// The Steam overlay's per-game switch, where Steam keeps it: the account's
/// <c>userdata/&lt;id&gt;/config/localconfig.vdf</c> ›
/// <c>UserLocalConfigStore/apps/&lt;app&gt;/OverlayAppEnable</c> ("0" off, "1" on,
/// absent = on), at the store's top level: where Steam's own Properties toggle
/// (<c>SteamClient.Apps.ToggleEnableSteamOverlayForApp</c> in steamui) saves it, and
/// the only place it reads. The same key under <c>Software/Valve/Steam/apps</c> (where
/// play time lives) is ignored: a game set off there still got the overlay.
/// Steam holds the file in memory and writes it as it exits, so a change is
/// written only while Steam is closed. The rest of the file is kept byte for byte.
/// </summary>
internal static class SteamOverlayConfig
{
    internal const string Key = "OverlayAppEnable";
    private static readonly string[] AppsPath = ["UserLocalConfigStore", "apps"];

    /// <summary>The <c>apps</c> section of a localconfig.vdf, parsed once for <see cref="Read"/>.</summary>
    internal static SteamKeyValues Apps(string? text) =>
        string.IsNullOrEmpty(text) ? SteamKeyValues.Empty : SteamKeyValues.ParseText(text).Section(string.Join('/', AppsPath));

    /// <summary>The game's overlay switch; null when Steam has none written (Steam's default, on).</summary>
    internal static bool? Read(SteamKeyValues apps, string appId) =>
        apps.Section(appId).Value(Key) switch { "0" => false, "1" => true, _ => null };

    /// <summary>
    /// Steam's store categories whose features run in the overlay: 35 In-App Purchases (Steam's purchase window),
    /// 36 Online PvP and 38 Online Co-op (Steam's invite window and joining a friend), 44 Remote Play Together.
    /// </summary>
    private static readonly string[] OverlayCategories = ["category_35", "category_36", "category_38", "category_44"];

    /// <summary>
    /// Whether a game needs the overlay: it sells through Steam, plays online with friends or by Remote Play Together, or
    /// Steam Input maps the controller for it (<paramref name="steamInput"/>; Steam Input works through the overlay).
    /// Everything else runs without it: one hook fewer in the game and the frames it costs.
    /// </summary>
    internal static bool Needed(SteamKeyValues app, bool steamInput) =>
        steamInput || OverlayCategories.Any(category => app.Section("common").Section("category").Value(category) is not null);

    /// <summary>
    /// What Exo writes, and its record afterwards. <paramref name="wanted"/>: each installed game's switch as
    /// <see cref="Needed"/> decides it. The record holds each game's switch as Exo set it ("on"/"off", with "?" while
    /// Steam has not taken it yet), or as the player chose it in Properties ("on="/"off=", which wins over the rule); a
    /// game whose switch was changed in Steam itself after Exo set it is the player's from then on ("!"), so Exo never
    /// fights them.
    /// </summary>
    internal static (Dictionary<string, bool> Writes, Dictionary<string, string> Record) Plan(
        SteamKeyValues apps,
        IReadOnlyDictionary<string, bool> wanted,
        IReadOnlyDictionary<string, string> record)
    {
        var writes = new Dictionary<string, bool>(StringComparer.Ordinal);
        var next = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase);
        foreach (var (appId, want) in wanted)
        {
            var current = Read(apps, appId);
            var kept = record.GetValueOrDefault(appId);
            if (kept is not null && (kept.EndsWith('!') ||
                kept is "on" or "off" or "on=" or "off=" && current is { } changed && changed != kept.StartsWith("on", StringComparison.Ordinal)))
            {
                next[appId] = ((current ?? true) ? "on" : "off") + "!";
                continue;
            }
            var chosen = kept?.TrimEnd('?') is "on=" or "off=";
            var goal = chosen ? kept!.StartsWith("on", StringComparison.Ordinal) : want;
            var word = (goal ? "on" : "off") + (chosen ? "=" : "");
            if ((current ?? true) != goal)
            {
                writes[appId] = goal;
                word += "?";
            }
            next[appId] = word;
        }
        return (writes, next);
    }

    /// <summary>Steam's global in-game overlay switch in localconfig.vdf; true unless explicitly <c>"0"</c>.</summary>
    internal static bool GlobalEnabled(string? localConfigText)
    {
        if (string.IsNullOrEmpty(localConfigText)) return true;
        var root = SteamKeyValues.ParseText(localConfigText).Section("UserLocalConfigStore");
        var value = root.Section("system").Value("EnableGameOverlay")
            ?? root.Section("Software/Valve/Steam").Value("EnableGameOverlay");
        return value != "0";
    }

    internal static string PriorKey(string accountId, string appId) => $"{accountId}/{appId}";

    /// <summary>Records the pre-Exo VDF value (<c>"1"</c>, <c>"0"</c>, or <c>""</c> for absent) once per account/app before editing.</summary>
    internal static void RecordPriors(
        string accountId,
        SteamKeyValues apps,
        IEnumerable<string> appIds,
        IDictionary<string, string> prior)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        foreach (var appId in appIds)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) continue;
            var key = PriorKey(accountId, appId);
            if (prior.ContainsKey(key)) continue;
            prior[key] = Read(apps, appId) switch { true => "1", false => "0", null => "" };
        }
    }

    /// <summary>Reverses Exo's per-app <c>OverlayAppEnable</c> changes for <paramref name="accountId"/> back to pre-Exo VDF state without touching <c>EnableGameOverlay</c>.</summary>
    internal static string Restore(string text, string accountId, IReadOnlyDictionary<string, string> prior)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return text;
        var prefix = accountId + "/";
        foreach (var (key, original) in prior)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var appId = key[prefix.Length..];
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) continue;
            string[] path = [.. AppsPath, appId];
            text = original.Length == 0 ? RemoveValue(text, path, Key) : SetValue(text, path, Key, original);
        }
        return text;
    }

    /// <summary>Target per-app overlay states after applying explicit player opt-ins and Steam overrides over <paramref name="wanted"/>.</summary>
    internal static Dictionary<string, bool> EffectiveGoals(
        IReadOnlyDictionary<string, bool> wanted,
        IReadOnlyDictionary<string, string> record)
    {
        var goals = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (appId, want) in wanted)
        {
            var entry = record.GetValueOrDefault(appId)?.TrimEnd('?');
            if (entry is "on!" or "off!") continue;
            goals[appId] = entry is "on=" or "off=" ? entry == "on=" : want;
        }
        return goals;
    }

    /// <summary>Per-app writes needed for an account's VDF to match <paramref name="goals"/>.</summary>
    internal static Dictionary<string, bool> WritesForAccount(
        SteamKeyValues apps,
        IReadOnlyDictionary<string, bool> goals)
    {
        var writes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (appId, goal) in goals)
        {
            if ((Read(apps, appId) ?? true) != goal)
                writes[appId] = goal;
        }
        return writes;
    }

    /// <summary>
    /// The file with each game's switch set. A missing game or section is added in
    /// Steam's own layout (tabs, one key per line); nothing else changes.
    /// </summary>
    internal static string Write(string text, IReadOnlyDictionary<string, bool> apps)
    {
        foreach (var (appId, on) in apps)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("Not a Steam app id.", nameof(apps));
            text = SetValue(text, [.. AppsPath, appId], Key, on ? "1" : "0");
        }
        return text;
    }

    /// <summary>The VDF text with <paramref name="key"/> under <paramref name="path"/> set to <paramref name="value"/> (escaped as Steam writes it).</summary>
    internal static string SetValue(string text, string[] path, string key, string value)
    {
        // Each pass adds one missing section; the path is at most six deep.
        for (var pass = 0; pass <= path.Length; pass++)
        {
            var scan = Scan(text, path, key);
            if (scan.ValueStart >= 0) return string.Concat(text.AsSpan(0, scan.ValueStart), value, text.AsSpan(scan.ValueEnd));
            var depth = scan.Depth;
            var indent = new string('\t', depth);
            if (depth == path.Length)
                return text.Insert(scan.InsertAt, $"{indent}\"{key}\"\t\t\"{value}\"\n");
            text = text.Insert(scan.InsertAt, $"{indent}\"{path[depth]}\"\n{indent}{{\n{indent}}}\n");
        }
        throw new InvalidDataException("localconfig.vdf did not take the section.");
    }

    /// <summary>The VDF text without <paramref name="key"/>'s line under <paramref name="path"/> (unchanged when it isn't there).</summary>
    internal static string RemoveValue(string text, string[] path, string key)
    {
        var scan = Scan(text, path, key);
        if (scan.ValueStart < 0) return text;
        var start = text.LastIndexOf('\n', scan.ValueStart) + 1;
        var end = text.IndexOf('\n', scan.ValueEnd);
        end = end < 0 ? text.Length : end + 1;
        return text.Remove(start, end - start);
    }

    /// <summary>
    /// What the file holds for <paramref name="key"/> under <paramref name="path"/>, as written (the text between the quotes,
    /// escapes kept), or null when the key is absent; and how many of the path's sections the file has (all of them when the
    /// key is there). What a put back needs to return the file to exactly what it was.
    /// </summary>
    internal static (string? Raw, int Depth) Probe(string text, string[] path, string key)
    {
        var scan = Scan(text, path, key);
        return scan.ValueStart >= 0 ? (text[scan.ValueStart..scan.ValueEnd], path.Length) : (null, scan.Depth);
    }

    /// <summary>
    /// The VDF text without the section at <paramref name="path"/> when it is empty (the block <see cref="SetValue"/> adds,
    /// its two braces and nothing between); unchanged when it holds anything or isn't there.
    /// </summary>
    internal static string RemoveEmptySection(string text, string[] path)
    {
        var stack = new List<string>();
        string? pending = null;
        var pendingStart = -1;
        var sectionStart = -1;
        var openEnd = -1;
        var position = 0;
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c)) { position++; continue; }
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            if (c == '{')
            {
                stack.Add(pending ?? "");
                if (sectionStart < 0 && stack.Count == path.Length && OnPath(stack, path, path.Length) && pendingStart >= 0)
                {
                    sectionStart = pendingStart;
                    openEnd = position + 1;
                }
                pending = null;
                position++;
                continue;
            }
            if (c == '}')
            {
                if (sectionStart >= 0 && stack.Count == path.Length && OnPath(stack, path, path.Length))
                {
                    if (!text.AsSpan(openEnd, position - openEnd).ToString().All(char.IsWhiteSpace)) return text;
                    var start = text.LastIndexOf('\n', sectionStart) + 1;
                    var end = text.IndexOf('\n', position);
                    end = end < 0 ? text.Length : end + 1;
                    return text.Remove(start, end - start);
                }
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                pending = null;
                position++;
                continue;
            }
            var tokenStart = position;
            var tokenEnd = TokenEnd(text, ref position);
            if (pending is null)
            {
                pending = text[(c == '"' ? tokenStart + 1 : tokenStart)..tokenEnd];
                pendingStart = tokenStart;
            }
            else pending = null;
        }
        return text;
    }

    /// <summary>
    /// Where <paramref name="key"/> sits under <paramref name="path"/>: its value's
    /// span, or how deep the path exists and where the next line goes in it (just
    /// before the deepest section's closing brace, at the start of its line).
    /// </summary>
    private static (int ValueStart, int ValueEnd, int Depth, int InsertAt) Scan(string text, string[] path, string key)
    {
        var stack = new List<string>();
        var matched = 0;
        var deepestClose = -1;
        string? pending = null;
        var position = 0;
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c)) { position++; continue; }
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            if (c == '{')
            {
                stack.Add(pending ?? "");
                pending = null;
                position++;
                continue;
            }
            if (c == '}')
            {
                // The deepest matched section is closing: the key or the next section goes here.
                if (stack.Count == matched && matched > 0 && OnPath(stack, path, matched))
                {
                    deepestClose = position;
                    break;
                }
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                pending = null;
                position++;
                continue;
            }
            var start = c == '"' ? position + 1 : position;
            var end = TokenEnd(text, ref position);
            var token = text[start..end];
            if (pending is null)
            {
                pending = token;
                // Entering the next section on the path once its brace opens.
                if (stack.Count == matched && matched < path.Length && OnPath(stack, path, matched) &&
                    token.Equals(path[matched], StringComparison.OrdinalIgnoreCase) && NextIsBrace(text, position))
                    matched++;
            }
            else
            {
                if (stack.Count == path.Length && matched == path.Length && OnPath(stack, path, matched) &&
                    pending.Equals(key, StringComparison.OrdinalIgnoreCase) && text[start - 1] == '"')
                    return (start, end, matched, -1);
                pending = null;
            }
        }
        if (matched == 0) return (-1, -1, 0, InsertPoint(text, text.Length));
        if (deepestClose < 0) throw new InvalidDataException("localconfig.vdf ends inside a section.");
        return (-1, -1, matched, InsertPoint(text, deepestClose));
    }

    private static bool OnPath(List<string> stack, string[] path, int count)
    {
        if (stack.Count < count) return false;
        for (var i = 0; i < count; i++)
            if (!stack[i].Equals(path[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool NextIsBrace(string text, int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        return position < text.Length && text[position] == '{';
    }

    /// <summary>The start of the line that holds <paramref name="at"/> (a closing brace), so a new line goes above it.</summary>
    private static int InsertPoint(string text, int at)
    {
        var lineStart = at;
        while (lineStart > 0 && text[lineStart - 1] is '\t' or ' ') lineStart--;
        if (lineStart > 0 && text[lineStart - 1] == '\n') return lineStart;
        // A brace that shares its line: break the line first by inserting at the brace itself.
        return at;
    }

    /// <summary>Moves past one token and returns the end of its content (before a closing quote).</summary>
    private static int TokenEnd(string text, ref int position)
    {
        if (text[position] != '"')
        {
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"')) position++;
            return position;
        }
        position++;
        while (position < text.Length && text[position] != '"')
            position += text[position] == '\\' && position + 1 < text.Length ? 2 : 1;
        var end = position;
        if (position < text.Length) position++;
        return end;
    }

    private static int _skipLogged;

    /// <summary>
    /// Writes the file next to itself and swaps it in, so Steam never reads half of it. The one writer of localconfig.vdf:
    /// the overlay, Steam Input and the quiet settings all come here, so a test instance (<see cref="HostMode"/>) leaves it.
    /// </summary>
    internal static void WriteFile(string path, string text)
    {
        if (HostMode.IsDisposable)
        {
            if (System.Threading.Interlocked.Exchange(ref _skipLogged, 1) == 0)
                AppLog.Info("Steam: a test instance leaves localconfig.vdf and its other Steam files alone.");
            return;
        }
        var tmp = path + ".exo-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
