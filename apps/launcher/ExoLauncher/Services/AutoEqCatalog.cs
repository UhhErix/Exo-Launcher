using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The headphones Exo EQ can correct: AutoEq's list of recommended results (github.com/jaakkopasanen/AutoEq, MIT; one
/// result per model, from the most accurate measurement of it), and one model's parametric EQ, both fetched from AutoEq's
/// own repository when the player opens the picker or picks a model, never shipped inside Exo: the measurements behind
/// them are oratory1990's, crinacle's, Rtings' and others', whose terms are their own (PRIVACY.md, THIRD_PARTY_NOTICES.md).
/// The list is kept on this PC for a week (autoeq-index.txt), a picked model's filters with the player's choice
/// (exo-eq.json). Ways it could go wrong: a name with brackets or an unescaped bracket in its link is cut short; a link that
/// climbs out of results/ or names another host is followed; a huge or endless answer fills memory; a list that fails to
/// load wipes the one kept; a jack's plain "Headphones" or a name two models fit guesses a headset.
/// </summary>
internal static class AutoEqCatalog
{
    /// <param name="Id">The result's folder under results/ ("oratory1990/over-ear/Sennheiser HD 600"): unique, and what picks it again.</param>
    /// <param name="Source">Who measured it ("oratory1990").</param>
    /// <param name="Form">"in-ear", "earbud" or "over-ear".</param>
    internal sealed record Headphone(string Id, string Name, string Source, string Form);

    internal const string Root = "https://" + UpstreamSource.GitHubRaw + "/jaakkopasanen/AutoEq/master/results/";
    private const string IndexName = "autoeq-index.txt";
    private const int IndexLimit = 4 * 1024 * 1024, PresetLimit = 64 * 1024;
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(20));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTime Written, IReadOnlyList<Headphone> List)? _cache;

    /// <summary>The list as last read, without asking for it (null before the picker first opened this run).</summary>
    internal static IReadOnlyList<Headphone>? Cached => _cache?.List;

    private static string IndexFile => Path.Combine(PathHelper.AppDataDir, IndexName);

    /// <summary>
    /// The list: the copy kept on this PC while it is under a week old, else fetched again (the kept one when that fails).
    /// Null when there is neither.
    /// </summary>
    internal static async Task<IReadOnlyList<Headphone>?> ListAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var written = File.Exists(IndexFile) ? File.GetLastWriteTimeUtc(IndexFile) : DateTime.MinValue;
            if (_cache is { } hit && hit.Written == written && DateTime.UtcNow - written < Fresh) return hit.List;
            if (DateTime.UtcNow - written >= Fresh && await FetchAsync(Root + "README.md", IndexLimit, ct).ConfigureAwait(false) is { } fetched
                && Parse(fetched) is { Count: > 100 } fresh)
            {
                if (!HostMode.IsDisposable)
                {
                    var tmp = IndexFile + ".tmp";
                    await File.WriteAllTextAsync(tmp, fetched, ct).ConfigureAwait(false);
                    File.Move(tmp, IndexFile, overwrite: true);
                }
                _cache = (File.Exists(IndexFile) ? File.GetLastWriteTimeUtc(IndexFile) : DateTime.UtcNow, fresh);
                return fresh;
            }
            if (!File.Exists(IndexFile)) return null;
            var kept = Parse(await File.ReadAllTextAsync(IndexFile, ct).ConfigureAwait(false));
            _cache = (written, kept);
            return kept.Count > 0 ? kept : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Exo EQ: AutoEq's list couldn't be kept: " + ex.GetType().Name);
            return _cache?.List;
        }
        finally { Gate.Release(); }
    }

    /// <summary>One model's parametric EQ (its ParametricEQ.txt); null when it couldn't be fetched or holds no filter.</summary>
    internal static async Task<List<EqFilter>?> FiltersAsync(Headphone headphone, CancellationToken ct = default)
    {
        if (PresetAddress(headphone.Id) is not { } address) return null;
        var text = await FetchAsync(address, PresetLimit, ct).ConfigureAwait(false);
        return text is not null && ExoEqConfig.Parse(text) is { Count: > 0 } filters ? filters : null;
    }

    private static async Task<string?> FetchAsync(string address, int limit, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Host != UpstreamSource.GitHubRaw) return null;
            return await ExoHttp.ReadBodyLimitedAsync(response.Content, limit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            AppLog.Info("Exo EQ: AutoEq didn't answer: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The address of a result's ParametricEQ.txt; null for an id that isn't a plain path under results/.</summary>
    internal static string? PresetAddress(string id)
    {
        var parts = id.Split('/');
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains('\\') || part.Contains(':'))) return null;
        return Root + string.Join('/', parts.Select(Uri.EscapeDataString)) + "/" + Uri.EscapeDataString(parts[^1] + " ParametricEQ.txt");
    }

    /// <summary>AutoEq's recommended list (results/README.md): its "- [Name](./Source/Rig/Name)" lines, one per model.</summary>
    internal static List<Headphone> Parse(string markdown)
    {
        var list = new List<Headphone>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("- [", StringComparison.Ordinal) || !line.EndsWith(')')) continue;
            // A name may hold brackets ("1MORE Aero (ANC Off)") and its link may too, unescaped: the link starts at "](./".
            var split = line.IndexOf("](./", StringComparison.Ordinal);
            if (split < 4) continue;
            var name = line[3..split].Trim();
            string id;
            try { id = Uri.UnescapeDataString(line[(split + 4)..^1]); }
            catch (UriFormatException) { continue; }
            var parts = id.Split('/');
            if (name.Length == 0 || parts.Length != 3 || PresetAddress(id) is null || !seen.Add(id)) continue;
            var rig = parts[1].ToLowerInvariant();
            var form = rig.Contains("in-ear", StringComparison.Ordinal) ? "in-ear" : rig.Contains("earbud", StringComparison.Ordinal) ? "earbud" : "over-ear";
            list.Add(new Headphone(id, name, parts[0], form));
        }
        return list;
    }

    // Words in a device's name that say nothing about which model it is.
    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "headset", "headphones", "headphone", "earphone", "earphones", "speakers", "speaker", "wireless", "gaming", "game", "chat",
        "stereo", "usb", "audio", "pro", "x", "the", "and", "with", "hands", "free", "bluetooth", "le", "high", "definition", "device",
        "digital", "output", "realtek", "r", "anc", "on", "off", "mode", "dongle", "dac", "amp", "receiver", "lightspeed",
        "hyperspeed", "slipstream", "sonar", "dts", "spatial", "sound", "24g", "24ghz", "usbaudio",
    };

    /// <summary>
    /// The headset Windows plays on, by the name it gives itself (a USB or wireless headset does: "Speakers (Arctis Nova
    /// 7)"): the model whose every word is in that name (its maker's may be missing) and that shares the most telling words
    /// with it. Null for a jack's plain "Headphones", for a name that fits nothing, and for two models that fit it as well.
    /// </summary>
    internal static Headphone? Guess(string? output, IReadOnlyList<Headphone> list)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var heard = Tokens(output);
        Headphone? best = null;
        var bestScore = 0;
        var tie = false;
        foreach (var headphone in list)
        {
            var words = Words(headphone.Name);
            if (words.Count == 0) continue;
            // The maker's word may be missing from what Windows says ("Arctis Nova 7" for SteelSeries'); it then counts less.
            var whole = words.All(heard.Contains);
            if (!whole && (words.Count < 2 || !words.Skip(1).All(heard.Contains))) continue;
            var model = whole ? words : words.Skip(1).ToList();
            var telling = model.Count(word => !Plain.Contains(word));
            if (telling == 0) continue;
            var score = telling * 10 + model.Count * 2 + (whole ? 1 : 0);
            if (score > bestScore) (best, bestScore, tie) = (headphone, score, false);
            else if (score == bestScore) tie = true;
        }
        return tie ? null : best;
    }

    /// <summary>The model a name says exactly (Lucid's "ZiiGaat Lush"), its words compared without case or punctuation.</summary>
    internal static Headphone? Named(string? name, IReadOnlyList<Headphone> list)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var wanted = string.Join(' ', Tokens(name).Order(StringComparer.Ordinal));
        var hits = list.Where(h => string.Join(' ', Tokens(h.Name).Order(StringComparer.Ordinal)) == wanted).ToList();
        return hits.Count >= 1 ? hits[0] : null;
    }

    private static HashSet<string> Tokens(string text) => [.. Words(text)];

    // The name's words in order, each once.
    private static List<string> Words(string text) =>
        [.. text.ToLowerInvariant().Split((char[])[' ', '(', ')', '-', '_', '/', ',', '.', '+', '®', '™', ':'], StringSplitOptions.RemoveEmptyEntries).Distinct()];
}
