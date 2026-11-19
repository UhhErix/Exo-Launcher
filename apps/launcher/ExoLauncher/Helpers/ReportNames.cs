using System.Text.RegularExpressions;
namespace ExoLauncher.Helpers;

/// <summary>
/// Names a crash report must never carry: Discord names, game titles from the library, store account names. The
/// service cannot know them, so the client registers them where they are read and <see cref="CrashReport"/> replaces
/// each one with "&lt;name&gt;". Thread-safe; <see cref="Add"/> never throws.
/// </summary>
public static class ReportNames
{
    private const int MinLength = 3;
    private static readonly object Gate = new();
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static volatile Regex? _pattern;

    /// <summary>Registers names. Blank and names shorter than three characters are ignored; repeats are fine.</summary>
    public static void Add(params string?[] names) => Add((IEnumerable<string?>)names);

    public static void Add(IEnumerable<string?>? names)
    {
        try
        {
            if (names is null) return;
            lock (Gate)
            {
                var added = false;
                foreach (var raw in names)
                {
                    var name = raw?.Trim();
                    if (name is null || name.Length < MinLength) continue;
                    added |= Known.Add(name);
                }
                if (added) _pattern = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Report names: not registered ({ex.GetType().Name}).");
        }
    }

    /// <summary>Forgets every name. For tests.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Known.Clear();
            _pattern = null;
        }
    }

    /// <summary>The registered names, longest first, so a longer title wins over a shorter one it starts with.</summary>
    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return [.. Known.OrderByDescending(name => name.Length).ThenBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>The text with every registered name replaced by "&lt;name&gt;", case-insensitively.</summary>
    internal static string Redact(string text)
    {
        var pattern = _pattern;
        if (pattern is null)
        {
            lock (Gate) pattern = _pattern ??= Build();
            if (pattern is null) return text;
        }
        return pattern.Replace(text, "<name>");
    }

    /// <summary>
    /// One alternation of every name, longest first. A name that starts or ends with a letter or digit only matches as a
    /// whole word; any other name matches as plain text. Called under <see cref="Gate"/>.
    /// </summary>
    private static Regex? Build()
    {
        if (Known.Count == 0) return null;
        var parts = Known
            .OrderByDescending(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                var escaped = Regex.Escape(name);
                var wordStart = char.IsLetterOrDigit(name[0]);
                var wordEnd = char.IsLetterOrDigit(name[^1]);
                return (wordStart ? @"(?<![\p{L}\p{N}_])" : "") + escaped + (wordEnd ? @"(?![\p{L}\p{N}_])" : "");
            });
        return new Regex(string.Join('|', parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
