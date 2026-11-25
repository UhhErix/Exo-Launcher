using System.Buffers;
using System.Text;
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
    private static readonly HashSet<string> ProtectedProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Exo", "Exo Launcher", "ExoLauncher", "Exo Mouse", "Exo Shaders",
        "Exo Capture", "Exo Shield", "Exo EQ", "Exo Cinema", "Exo AI",
    };
    private static readonly string[] DotNetRoots =
    [
        "System.", "Microsoft.", "Windows.", "ExoLauncher.", "WinRT.", "ABI.", "Internal.",
        "CommunityToolkit.", "Newtonsoft.", "Xunit.",
    ];
    private static volatile Matcher? _matcher;

    private sealed class TrieNode
    {
        public Dictionary<char, TrieNode>? Children;
        public string? Name;
    }

    private sealed class Matcher(SearchValues<string> values, TrieNode root)
    {
        public SearchValues<string> Values { get; } = values;
        public TrieNode Root { get; } = root;
    }

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
                    if (name is null || name.Length < MinLength || ProtectedProductNames.Contains(name)) continue;
                    added |= Known.Add(name);
                }
                if (added) _matcher = null;
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
            _matcher = null;
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
        if (string.IsNullOrEmpty(text)) return text;
        var matcher = _matcher;
        if (matcher is null)
        {
            lock (Gate) matcher = _matcher ??= Build();
            if (matcher is null) return text;
        }
        return RedactWith(text, matcher);
    }

    private static string RedactWith(string text, Matcher matcher)
    {
        var span = text.AsSpan();
        StringBuilder? sb = null;
        var copied = 0;
        var pos = 0;
        while (pos < span.Length)
        {
            var rel = span[pos..].IndexOfAny(matcher.Values);
            if (rel < 0) break;
            var hit = pos + rel;
            var matched = FindLongestValidMatch(text, span, hit, matcher.Root);
            if (matched is not null)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(span[copied..hit]).Append("<name>");
                copied = hit + matched.Length;
                pos = copied;
                continue;
            }
            pos = hit + 1;
        }
        if (sb is null) return text;
        sb.Append(span[copied..]);
        return sb.ToString();
    }

    private static string? FindLongestValidMatch(string text, ReadOnlySpan<char> span, int hit, TrieNode root)
    {
        var node = root;
        string? best = null;
        for (var i = hit; i < span.Length; i++)
        {
            var c = char.ToUpperInvariant(span[i]);
            if (node.Children is null || !node.Children.TryGetValue(c, out node)) break;
            if (node.Name is { } candidate)
            {
                if (char.IsLetterOrDigit(candidate[0]) && hit > 0 && IsWordChar(span[hit - 1])) continue;
                if (char.IsLetterOrDigit(candidate[^1]) && hit + candidate.Length < span.Length && IsWordChar(span[hit + candidate.Length])) continue;
                if (IsInStackFrame(text, hit, candidate.Length) || IsInsideDotNetToken(text, hit, candidate.Length)) continue;
                best = candidate;
            }
        }
        return best;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    internal static bool IsInStackFrame(string text, int start, int length)
    {
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var p = lineStart;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p + 3 > text.Length || !text.AsSpan(p, 3).Equals("at ".AsSpan(), StringComparison.Ordinal)) return false;
        var frameStart = p + 3;
        if (start < frameStart) return false;
        var lineEnd = text.IndexOf('\n', frameStart);
        if (lineEnd < 0) lineEnd = text.Length;
        var openParen = text.IndexOf('(', frameStart, lineEnd - frameStart);
        return openParen > frameStart && start + length <= openParen;
    }

    internal static bool IsDotNetNamespaceToken(string text, int start, int length)
    {
        var span = text.AsSpan(start, length);
        foreach (var root in DotNetRoots)
            if (span.StartsWith(root.AsSpan(), StringComparison.Ordinal)) return true;
        if (length > 0 && char.IsAsciiLetterUpper(span[0]) && start + length + 1 < text.Length
            && text[start + length] == '.' && char.IsAsciiLetterUpper(text[start + length + 1]))
            return true;
        return false;
    }

    private static bool IsInsideDotNetToken(string text, int start, int length)
    {
        var tokenStart = start;
        while (tokenStart > 0 && (char.IsLetterOrDigit(text[tokenStart - 1]) || text[tokenStart - 1] is '.' or '_')) tokenStart--;
        var tokenEnd = start + length;
        while (tokenEnd < text.Length && (char.IsLetterOrDigit(text[tokenEnd]) || text[tokenEnd] is '.' or '_')) tokenEnd++;
        var fullLen = tokenEnd - tokenStart;
        return fullLen > length && IsDotNetNamespaceToken(text, tokenStart, fullLen);
    }

    /// <summary>
    /// One alternation of every name, longest first. A name that starts or ends with a letter or digit only matches as a
    /// whole word; any other name matches as plain text. Called under <see cref="Gate"/>.
    /// </summary>
    private static Matcher? Build()
    {
        if (Known.Count == 0) return null;
        var ordered = Known
            .OrderByDescending(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var root = new TrieNode();
        foreach (var name in ordered)
        {
            var node = root;
            foreach (var ch in name)
            {
                var key = char.ToUpperInvariant(ch);
                node.Children ??= new Dictionary<char, TrieNode>();
                if (!node.Children.TryGetValue(key, out var next)) node.Children[key] = next = new TrieNode();
                node = next;
            }
            node.Name ??= name;
        }
        return new Matcher(SearchValues.Create(ordered, StringComparison.OrdinalIgnoreCase), root);
    }
}
