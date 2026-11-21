using System.Globalization;
using System.Numerics;
using System.Text;

namespace ExoLauncher.Services;

/// <summary>One of Equalizer APO's biquads that Exo EQ writes: a peak, a low shelf or a high shelf.</summary>
internal enum EqKind { Peak, LowShelf, HighShelf }

/// <summary>One filter: its kind, centre or corner in Hz, gain in dB and Q.</summary>
internal sealed record EqFilter(EqKind Kind, double Hz, double Gain, double Q);

/// <summary>What one output plays: its endpoint id (<c>{…}</c>), the lines that say why, and its filters in order.</summary>
internal sealed record EqChain(string Guid, IReadOnlyList<string> Notes, IReadOnlyList<EqFilter> Filters);

/// <summary>
/// The text Exo EQ puts in Equalizer APO's config folder, and the one line it adds to the player's config.txt. Pure, so
/// every byte is tested (ExoEqConfigTests). Ways it could go wrong: a filter written in a culture with a decimal comma (APO
/// reads a dot); a curve that boosts past 0 dB clips (the preamp guard reads the whole curve, not one filter's gain, since
/// two filters side by side add up); the Include line lands twice, or removing it takes a line of the player's with it, or
/// turns CRLF into LF, or drops a last line without a newline; a block meant for one output plays on all of them because
/// the Device selection was never put back; Lucid's own block keeps playing under Exo's, so the sound is corrected twice.
/// </summary>
internal static class ExoEqConfig
{
    /// <summary>Exo's own file, beside config.txt.</summary>
    internal const string FileName = "exo.txt";

    /// <summary>The one line Exo adds to config.txt.</summary>
    internal const string IncludeLine = "Include: " + FileName;

    internal const string Header = "# Exo EQ: Exo Launcher writes this file (Devices > your audio). Change the EQ there; edits here are replaced.";

    /// <summary>The limits Exo keeps every filter inside: what a picked preset or a slider can ask for.</summary>
    internal static EqFilter Clamp(EqFilter f) => f with
    {
        Hz = double.IsFinite(f.Hz) ? Math.Clamp(f.Hz, 20, 20000) : 1000,
        Gain = double.IsFinite(f.Gain) ? Math.Clamp(f.Gain, -20, 20) : 0,
        Q = double.IsFinite(f.Q) ? Math.Clamp(f.Q, 0.1, 10) : 0.71,
    };

    // ------------------------------------------------------------------ The preamp guard

    private const double Rate = 48000;

    /// <summary>The chain's gain in dB at <paramref name="hz"/>, as Equalizer APO's biquads (the Audio EQ Cookbook's) give it.</summary>
    internal static double Response(IEnumerable<EqFilter> filters, double hz)
    {
        var w = 2 * Math.PI * hz / Rate;
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var total = 0.0;
        foreach (var f in filters)
        {
            var (b0, b1, b2, a0, a1, a2) = Coefficients(f);
            var h = (b0 + b1 * z1 + b2 * z2) / (a0 + a1 * z1 + a2 * z2);
            total += 20 * Math.Log10(Math.Max(h.Magnitude, 1e-9));
        }
        return total;
    }

    private static (double, double, double, double, double, double) Coefficients(EqFilter f)
    {
        var a = Math.Pow(10, f.Gain / 40);
        var w0 = 2 * Math.PI * f.Hz / Rate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * f.Q);
        var root = 2 * Math.Sqrt(a) * alpha;
        return f.Kind switch
        {
            EqKind.LowShelf => (
                a * (a + 1 - (a - 1) * cos + root), 2 * a * (a - 1 - (a + 1) * cos), a * (a + 1 - (a - 1) * cos - root),
                a + 1 + (a - 1) * cos + root, -2 * (a - 1 + (a + 1) * cos), a + 1 + (a - 1) * cos - root),
            EqKind.HighShelf => (
                a * (a + 1 + (a - 1) * cos + root), -2 * a * (a - 1 + (a + 1) * cos), a * (a + 1 + (a - 1) * cos - root),
                a + 1 - (a - 1) * cos + root, 2 * (a - 1 - (a + 1) * cos), a + 1 - (a - 1) * cos - root),
            _ => (1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a),
        };
    }

    /// <summary>The highest the chain reaches over 20 Hz to 20 kHz (241 points, a 1/24 octave apart).</summary>
    internal static double Peak(IReadOnlyList<EqFilter> filters)
    {
        if (filters.Count == 0) return 0;
        var peak = double.NegativeInfinity;
        for (var i = 0; i <= 240; i++) peak = Math.Max(peak, Response(filters, 20 * Math.Pow(1000, i / 240.0)));
        return peak;
    }

    /// <summary>
    /// The preamp that keeps the chain from clipping: minus its highest boost, rounded away from zero to 0.1 dB (0 when the
    /// chain only cuts).
    /// </summary>
    internal const double HeadroomMarginDb = 0.5;

    internal static double Preamp(IReadOnlyList<EqFilter> filters)
    {
        var peak = Peak(filters);
        return peak <= 0.005 ? 0 : -(Math.Ceiling(Math.Round(peak, 6) * 10) / 10 + HeadroomMarginDb);
    }

    // ------------------------------------------------------------------ exo.txt

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>"Filter: ON PK Fc 105 Hz Gain -0.7 dB Q 0.70", as Equalizer APO reads it.</summary>
    internal static string Line(EqFilter f)
    {
        var kind = f.Kind switch { EqKind.LowShelf => "LSC", EqKind.HighShelf => "HSC", _ => "PK" };
        var gain = Math.Round(f.Gain, 1, MidpointRounding.AwayFromZero);
        return $"Filter: ON {kind} Fc {Number(f.Hz, "0.##")} Hz Gain {Number(gain == 0 ? 0 : gain, "0.0")} dB Q {Number(f.Q, "0.00")}";
    }

    /// <summary>
    /// exo.txt: a block per output with a chain, each opening with its Device line and closing with <c>Device: all</c>
    /// and <c>Channel: all</c>, so nothing of it reaches another output or what follows. <paramref name="off"/> (the
    /// switch off, or Compare) writes the header and that line only: APO plays every output flat.
    /// </summary>
    internal static string Text(IReadOnlyList<EqChain> chains, string? off = null)
    {
        var text = new StringBuilder();
        void Add(string line) => text.Append(line).Append("\r\n");
        Add(Header);
        if (off is not null) { Add("# " + Clean(off)); return text.ToString(); }
        foreach (var chain in chains)
        {
            if (chain.Filters.Count == 0 || !IsGuid(chain.Guid)) continue;
            var filters = chain.Filters.Select(Clamp).ToList();
            Add("");
            Add("Device: " + chain.Guid);
            Add("Channel: all");
            foreach (var note in chain.Notes) Add("# " + Clean(note));
            var preamp = Preamp(filters);
            if (preamp != 0) Add($"Preamp: {Number(preamp, "0.0")} dB");
            foreach (var f in filters) Add(Line(f));
            Add("Device: all");
            Add("Channel: all");
        }
        return text.ToString();
    }

    /// <summary>An endpoint id as Device lines name it: <c>{8-4-4-4-12}</c>.</summary>
    internal static bool IsGuid(string value) =>
        value.Length == 38 && value[0] == '{' && value[^1] == '}' && System.Guid.TryParse(value, out _);

    // A name in a comment line: one line, no control characters, at most 120 characters.
    private static string Clean(string text)
    {
        var one = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return one.Length > 120 ? one[..120] : one;
    }

    /// <summary>
    /// The filters of a file in Equalizer APO's format (AutoEq's ParametricEQ.txt, Lucid's lucid_temp.txt): ON lines of
    /// kinds Exo writes (PK, PEQ, LS, LSC, HS, HSC with a Q), in order. Its Preamp is not kept: Exo works out its own.
    /// </summary>
    internal static List<EqFilter> Parse(string text)
    {
        var list = new List<EqFilter>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (!line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase) || colon < 0) continue;
            var words = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || !words[0].Equals("ON", StringComparison.OrdinalIgnoreCase)) continue;
            EqKind? kind = words[1].ToUpperInvariant() switch
            {
                "PK" or "PEQ" => EqKind.Peak,
                "LS" or "LSC" => EqKind.LowShelf,
                "HS" or "HSC" => EqKind.HighShelf,
                _ => null,
            };
            if (kind is null) continue;
            double? After(string word)
            {
                var at = Array.FindIndex(words, w => w.Equals(word, StringComparison.OrdinalIgnoreCase));
                return at >= 0 && at + 1 < words.Length && double.TryParse(words[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            }
            if (After("Fc") is not { } hz || After("Gain") is not { } gain || After("Q") is not { } q) continue;
            list.Add(Clamp(new EqFilter(kind.Value, hz, gain, q)));
        }
        return list;
    }

    // ------------------------------------------------------------------ config.txt

    // config.txt is read and written as Latin-1, which gives back every byte as it was (any encoding, any BOM): Exo's own
    // line is plain ASCII, and the lines it matches are too.

    /// <summary>The lines with their own line breaks, so a rewrite keeps CRLF, LF and a last line without one as they were.</summary>
    private static IEnumerable<(string Text, string Break)> Lines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) { yield return (text[start..], ""); yield break; }
            var cr = end > start && text[end - 1] == '\r';
            yield return (text[start..(cr ? end - 1 : end)], cr ? "\r\n" : "\n");
            start = end + 1;
        }
    }

    private static bool IsOurInclude(string line)
    {
        var trimmed = line.Trim();
        var colon = trimmed.IndexOf(':');
        return colon > 0 && trimmed[..colon].Trim().Equals("Include", StringComparison.OrdinalIgnoreCase)
            && trimmed[(colon + 1)..].Trim().Trim('"').Equals(FileName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasInclude(string config) => Lines(config).Any(line => IsOurInclude(line.Text));

    /// <summary>
    /// config.txt with Exo's Include line at its end, once (unchanged when it is there). It goes last so the player's own
    /// lines run first, and exo.txt chooses its own outputs. The file's own line break is used.
    /// </summary>
    internal static string WithInclude(string config)
    {
        if (HasInclude(config)) return config;
        var lineBreak = config.Contains("\r\n", StringComparison.Ordinal) || !config.Contains('\n') ? "\r\n" : "\n";
        var body = config.Length == 0 || config.EndsWith('\n') ? config : config + lineBreak;
        return body + IncludeLine + lineBreak;
    }

    /// <summary>config.txt without Exo's Include line; every other byte stays.</summary>
    internal static string WithoutInclude(string config) =>
        string.Concat(Lines(config).Where(line => !IsOurInclude(line.Text)).Select(line => line.Text + line.Break));

    private const string LucidBegin = "# Lucid EQ begin", LucidEnd = "# Lucid EQ end";

    /// <summary>The prefix that turns one of Lucid's lines into a comment once Exo EQ took over from it.</summary>
    internal const string LucidOffMark = "# Exo EQ took over: ";

    /// <summary>Whether Lucid's EQ block ("# Lucid EQ begin" to "# Lucid EQ end") still does anything.</summary>
    internal static bool LucidActive(string config)
    {
        var inside = false;
        foreach (var (raw, _) in Lines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith(LucidBegin, StringComparison.OrdinalIgnoreCase)) inside = true;
            else if (line.StartsWith(LucidEnd, StringComparison.OrdinalIgnoreCase)) inside = false;
            else if (inside && line.Length > 0 && !line.StartsWith('#')) return true;
        }
        return false;
    }

    /// <summary>
    /// config.txt with Lucid's EQ block turned into comments (each line kept after <see cref="LucidOffMark"/>), so the sound
    /// is not corrected twice. Lucid Voice's block, and everything outside Lucid's, stays.
    /// </summary>
    internal static string LucidOff(string config)
    {
        var inside = false;
        var text = new StringBuilder(config.Length + 128);
        foreach (var (raw, lineBreak) in Lines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith(LucidBegin, StringComparison.OrdinalIgnoreCase)) inside = true;
            else if (line.StartsWith(LucidEnd, StringComparison.OrdinalIgnoreCase)) inside = false;
            else if (inside && line.Length > 0 && !line.StartsWith('#')) { text.Append(LucidOffMark).Append(raw).Append(lineBreak); continue; }
            text.Append(raw).Append(lineBreak);
        }
        return text.ToString();
    }

    private const string LegacyBegin = "# Exo EQ begin", LegacyEnd = "# Exo EQ end";

    /// <summary>
    /// config.txt without the block an earlier Exo wrote ("# Exo EQ begin" to "# Exo EQ end", which said Exo writes only
    /// between those lines): its lines go, except a Lucid block that ended up inside it, which stays where it was.
    /// </summary>
    internal static string WithoutLegacyBlock(string config)
    {
        bool inLegacy = false, inLucid = false;
        var text = new StringBuilder(config.Length);
        foreach (var (raw, lineBreak) in Lines(config))
        {
            var line = raw.Trim();
            if (!inLegacy && line.StartsWith(LegacyBegin, StringComparison.OrdinalIgnoreCase)) { inLegacy = true; continue; }
            if (inLegacy && !inLucid && line.StartsWith(LegacyEnd, StringComparison.OrdinalIgnoreCase)) { inLegacy = false; continue; }
            if (inLegacy)
            {
                if (line.StartsWith(LucidBegin, StringComparison.OrdinalIgnoreCase)) inLucid = true;
                if (!inLucid) continue;
                if (line.StartsWith(LucidEnd, StringComparison.OrdinalIgnoreCase)) inLucid = false;
            }
            text.Append(raw).Append(lineBreak);
        }
        return text.ToString();
    }

    /// <summary>What Exo writes to config.txt when it turns its EQ on: an earlier Exo's block gone, its Include line, and Lucid's EQ off when asked.</summary>
    internal static string Prepared(string config, bool lucidOff)
    {
        var next = WithInclude(WithoutLegacyBlock(config));
        return lucidOff ? LucidOff(next) : next;
    }
}
