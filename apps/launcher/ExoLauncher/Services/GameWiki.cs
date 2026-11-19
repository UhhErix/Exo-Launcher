using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// What a game's PCGamingWiki page says that Exo acts on, read from one request: whether it outputs real HDR (the "hdr"
/// field of its video settings: true, false, hackable or unknown), which <see cref="GameHdr"/> follows only when the game
/// itself says nothing, and for Fluid Motion Frames (<see cref="FluidMotionFrames"/>) its modes, genres, frame generation,
/// 60 fps lock and graphics APIs. Asked once per game and kept 30 days (7 for a game the wiki has no answer for) in
/// game-wiki.json.
/// </summary>
internal static partial class GameWiki
{
    /// <summary>The page's facts for Fluid Motion Frames, lower case; an empty list where the page says nothing.</summary>
    /// <param name="FrameGen">Its frame generation field: "true", "false", "hackable" or "unknown".</param>
    /// <param name="Over60">Its 120 fps field: true past 60 fps, false when held at 60 (or lifted only by a mod), null when not known.</param>
    /// <param name="Apis">"dx9" to "dx12", "vulkan", "opengl".</param>
    internal sealed record Facts(IReadOnlyList<string> Modes, IReadOnlyList<string> Genres, string FrameGen, bool? Over60, IReadOnlyList<string> Apis);

    /// <param name="Facts">Null in an entry kept before 3.17.2, which read HDR alone.</param>
    private sealed record Entry(string Hdr, DateTimeOffset At, Facts? Facts = null);

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, Task<Entry>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Entry>? _cache;
    private static readonly HttpClient Http = CreateClient();

    // The wiki refuses requests that do not say who is asking.
    private static HttpClient CreateClient()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(12), new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher (+https://getexo.app)");
        return http;
    }

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, "game-wiki.json");

    /// <summary>Where 3.17.1 and older kept the HDR answers, read once and removed.</summary>
    private static string OldFilePath => Path.Combine(PathHelper.AppDataDir, "hdr-games.json");

    /// <summary>The wiki's answer as kept: "true", "false", "hackable", "unknown", or null when not asked yet.</summary>
    internal static string? Cached(string gameId)
    {
        lock (Gate) return Load().TryGetValue(gameId, out var entry) ? entry.Hdr : null;
    }

    /// <summary>The page's facts as kept, or null when not read yet.</summary>
    internal static Facts? CachedFacts(string gameId)
    {
        lock (Gate) return Load().TryGetValue(gameId, out var entry) ? entry.Facts : null;
    }

    /// <summary>The kept HDR answer while fresh, else one wiki request per game at a time.</summary>
    internal static async Task<string> LookupAsync(GameEntry game, CancellationToken ct) =>
        (await EntryAsync(game, needFacts: false, ct).ConfigureAwait(false)).Hdr;

    /// <summary>The page's facts while fresh, else one wiki request per game at a time; null when the wiki can't be reached.</summary>
    internal static async Task<Facts?> FactsAsync(GameEntry game, CancellationToken ct) =>
        (await EntryAsync(game, needFacts: true, ct).ConfigureAwait(false)).Facts;

    private static Task<Entry> EntryAsync(GameEntry game, bool needFacts, CancellationToken ct)
    {
        lock (Gate)
        {
            if (Load().TryGetValue(game.Id, out var kept) && (kept.Facts is not null || !needFacts) &&
                DateTimeOffset.UtcNow - kept.At < TimeSpan.FromDays(kept.Hdr == "unknown" ? 7 : 30))
                return Task.FromResult(kept);
        }
        return Pending.GetOrAdd(game.Id, _ => Task.Run(async () =>
        {
            try { return await AskAsync(game, ct).ConfigureAwait(false); }
            finally { Pending.TryRemove(game.Id, out Task<Entry>? _); }
        }, CancellationToken.None)).WaitAsync(ct);
    }

    private static async Task<Entry> AskAsync(GameEntry game, CancellationToken ct)
    {
        Entry entry;
        try
        {
            var text = await FetchAsync(game, ct).ConfigureAwait(false);
            entry = new Entry(ReadHdrField(text) ?? "unknown", DateTimeOffset.UtcNow, ReadFacts(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Debug("Game wiki lookup: " + ex.GetType().Name);
            lock (Gate) return Load().TryGetValue(game.Id, out var kept) ? kept : new Entry("unknown", default);
        }
        lock (Gate)
        {
            Load()[game.Id] = entry;
            Save();
        }
        return entry;
    }

    /// <summary>The page's wikitext; null when the wiki has no page for the game.</summary>
    private static async Task<string?> FetchAsync(GameEntry game, CancellationToken ct)
    {
        var title = await PageTitleAsync(game, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title)) return null;
        var url = "https://www.pcgamingwiki.com/w/api.php?action=parse&format=json&formatversion=2&redirects=1&prop=wikitext&page=" +
            Uri.EscapeDataString(title);
        using var deadline = ExoHttpTransport.Deadline(Http, ct);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = await ExoHttpTransport.ReadJsonAsync(response.Content, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("parse", out var parse) ||
            !parse.TryGetProperty("wikitext", out var text) || text.ValueKind != JsonValueKind.String)
            return null;
        return text.GetString();
    }

    /// <summary>The page for a Steam app id (the wiki's own lookup), else the game's title.</summary>
    private static async Task<string?> PageTitleAsync(GameEntry game, CancellationToken ct)
    {
        if (CoverArtService.SteamAppId(game) is { } appId)
        {
            using var deadline = ExoHttpTransport.Deadline(Http, ct);
            using var response = await Http.GetAsync("https://www.pcgamingwiki.com/api/appid.php?appid=" + Uri.EscapeDataString(appId),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (PageFromWikiUrl(response.Headers.Location) is { } page) return page;
        }
        return TrademarkMarks().Replace(game.Title, "").Trim();
    }

    /// <summary>"https://www.pcgamingwiki.com/wiki/Hell_is_Us" gives "Hell is Us"; any other host gives null.</summary>
    internal static string? PageFromWikiUrl(Uri? location)
    {
        if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps ||
            !location.Host.Equals("www.pcgamingwiki.com", StringComparison.OrdinalIgnoreCase) ||
            !location.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
            return null;
        var page = Uri.UnescapeDataString(location.AbsolutePath["/wiki/".Length..]).Replace('_', ' ').Trim();
        return page.Length == 0 ? null : page;
    }

    /// <summary>The value of the page's "|hdr = ..." field, lower case; null when the page has none.</summary>
    internal static string? ReadHdrField(string? wikitext)
    {
        if (wikitext is null) return null;
        var match = HdrField().Match(wikitext);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim().ToLowerInvariant();
        return value is "true" or "false" or "hackable" ? value : "unknown";
    }

    /// <summary>
    /// The page's modes and genres (its infobox taxonomy rows), its "framegen" and "120 fps" fields and the APIs its
    /// "direct3d versions", "vulkan versions" and "opengl versions" fields name. A page that names none of them, or no
    /// page, gives empty facts (so it is not asked again before its time).
    /// </summary>
    internal static Facts ReadFacts(string? wikitext)
    {
        wikitext ??= "";
        static IReadOnlyList<string> List(Match match) => match.Success
            ? match.Groups[1].Value.Split(',').Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).ToList()
            : [];
        static string Value(Match match) => match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : "";
        var frameGen = Value(FrameGenField().Match(wikitext));
        bool? over60 = Value(Fps120Field().Match(wikitext)) switch { "true" => true, "false" or "hackable" => false, _ => null };
        var apis = Digits().Matches(Value(Direct3DField().Match(wikitext)))
            .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(version => version is >= 7 and <= 12).Distinct().Select(version => "dx" + version).ToList();
        if (Value(VulkanField().Match(wikitext)) is { Length: > 0 } vulkan && vulkan != "false") apis.Add("vulkan");
        if (Value(OpenGlField().Match(wikitext)) is { Length: > 0 } openGl && openGl != "false") apis.Add("opengl");
        return new Facts(List(ModesRow().Match(wikitext)), List(GenresRow().Match(wikitext)),
            frameGen is "true" or "false" or "hackable" ? frameGen : "unknown", over60, apis);
    }

    [GeneratedRegex(@"^\s*\|\s*hdr\s*=\s*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HdrField();

    [GeneratedRegex(@"^\s*\|\s*framegen\s*=[ \t]*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FrameGenField();

    [GeneratedRegex(@"^\s*\|\s*120 fps\s*=[ \t]*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Fps120Field();

    [GeneratedRegex(@"^\s*\|\s*direct3d versions\s*=[ \t]*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Direct3DField();

    [GeneratedRegex(@"^\s*\|\s*vulkan versions\s*=[ \t]*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex VulkanField();

    [GeneratedRegex(@"^\s*\|\s*opengl versions\s*=[ \t]*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex OpenGlField();

    [GeneratedRegex(@"taxonomy/modes\s*\|([^}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex ModesRow();

    [GeneratedRegex(@"taxonomy/genres\s*\|([^}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex GenresRow();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"[™®©]")]
    private static partial Regex TrademarkMarks();

    private static Dictionary<string, Entry> Load()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var path = File.Exists(FilePath) ? FilePath : OldFilePath;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } read)
                foreach (var pair in read) _cache[pair.Key] = pair.Value;
            if (path == OldFilePath && File.Exists(path))
            {
                Save();
                DeletionGate.DeleteFile(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Debug("Game wiki cache unreadable: " + ex.GetType().Name);
        }
        return _cache;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Game wiki cache not saved: " + ex.GetType().Name);
        }
    }
}
