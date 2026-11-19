using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// Synced lyrics for YouTube Music, which has only unsynced lyrics on the web: Exo looks the song up on LRCLIB's open
/// lyrics API (lrclib.net) by its title, artists, album and length (LRCLIB matches the length within 2 s) and draws the
/// timed lines in its own Lyrics panel. One GET per song, without cookies, cached for the run; the request carries those
/// four facts and nothing else, and nothing about the song is logged. No match, an instrumental, or no answer in time:
/// the page's own unsynced lyrics stay.
/// </summary>
internal static partial class SyncedLyrics
{
    internal sealed record Song(string Title, string Artist, string Album, int Seconds);

    /// <summary>Start: milliseconds into the song (0 for every line of unsynced lyrics).</summary>
    internal sealed record Line(int Start, string Text);

    internal sealed record Lyrics(IReadOnlyList<Line> Lines, bool Synced);

    /// <summary>As many lines and characters per line as the page's own lyrics carry.</summary>
    private const int MaxLines = 400, MaxText = 300;

    private static readonly Lyrics None = new([], false);
    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, Task<Lyrics?>> Found = new(StringComparer.Ordinal);
    /// <summary>After LRCLIB fails to answer, Lyrics opens without waiting on it for a minute.</summary>
    private static long s_quietUntil;

    private static HttpClient CreateHttp()
    {
        // A miss takes LRCLIB about 1.3 s (it asks other sources); past 4 s the unsynced lyrics show.
        var http = ExoHttp.Create(TimeSpan.FromSeconds(4),
            new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
        http.MaxResponseContentBufferSize = 1 << 20;
        // LRCLIB asks every client to name itself, its version and its home page.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ExoLauncher/{AppServices.RunningVersion} (+https://getexo.app)");
        return http;
    }

    /// <summary>The song the YouTube Music page's lyrics answer names (`song`: title, artists, album, durationMs).</summary>
    internal static Song? SongOf(JsonElement page)
    {
        if (!page.TryGetProperty("song", out var song) || song.ValueKind != JsonValueKind.Object) return null;
        static string Text(JsonElement o, string name, int max) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!.Trim(), max) : "";
        var title = Text(song, "title", 200);
        var artists = song.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join(", ", list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String)
                .Select(a => a.GetString()!.Trim()).Where(a => a.Length > 0).Take(4))
            : "";
        var ms = song.TryGetProperty("durationMs", out var d) && d.TryGetDouble(out var value) ? value : 0;
        // A song of a second or less, or longer than a day, is a page that didn't read its length.
        if (title.Length == 0 || artists.Length == 0 || ms < 1000 || ms > 86_400_000) return null;
        return new Song(title, Clip(artists, 200), Text(song, "album", 200), (int)Math.Round(ms / 1000));
    }

    internal static bool HasLines(JsonElement page) =>
        page.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array &&
        lines.EnumerateArray().Any(line => line.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } s && s.Trim().Length > 0);

    /// <summary>
    /// What Exo's panel shows instead of the page's lyrics: LRCLIB's when they're synced, or when the page has none;
    /// null keeps the page's.
    /// </summary>
    internal static Lyrics? Better(Lyrics? found, bool pageHasLines) =>
        found is { Lines.Count: > 0 } && (found.Synced || !pageHasLines) ? found : null;

    internal static string RequestUri(Song song) =>
        "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(song.Title) +
        "&artist_name=" + Uri.EscapeDataString(song.Artist) +
        (song.Album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(song.Album) : "") +
        "&duration=" + song.Seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// LRCLIB's lyrics for the song: synced when it has them, else its plain text; no lines for an instrumental or a song
    /// it doesn't know; null when it didn't answer (asked again on the next open, after a minute).
    /// </summary>
    internal static Task<Lyrics?> FindAsync(Song song)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref s_quietUntil)) return Task.FromResult<Lyrics?>(null);
        var key = string.Join('\n', song.Title.ToLowerInvariant(), song.Artist.ToLowerInvariant(), song.Album.ToLowerInvariant(),
            song.Seconds.ToString(CultureInfo.InvariantCulture));
        if (Found.Count > 500) Found.Clear();
        var task = Found.GetOrAdd(key, _ => AskAsync(song));
        _ = task.ContinueWith(t => { if (!t.IsCompletedSuccessfully || t.Result is null) Found.TryRemove(new(key, task)); }, TaskScheduler.Default);
        return task;
    }

    private static async Task<Lyrics?> AskAsync(Song song)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await Http.GetAsync(RequestUri(song)).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                AppLog.Debug($"Lyrics: LRCLIB has none for a YouTube Music song ({watch.ElapsedMilliseconds} ms).");
                return None;
            }
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var found = FromAnswer(doc.RootElement);
            AppLog.Debug($"Lyrics: LRCLIB answered for a YouTube Music song: {(found.Lines.Count == 0 ? "none" : found.Synced ? "synced" : "unsynced")} ({watch.ElapsedMilliseconds} ms).");
            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Interlocked.Exchange(ref s_quietUntil, Environment.TickCount64 + 60_000);
            AppLog.Warn($"Lyrics: LRCLIB didn't answer ({ex.GetType().Name}, {watch.ElapsedMilliseconds} ms); YouTube Music's own lyrics show.");
            return null;
        }
    }

    /// <summary>One answer of LRCLIB's /api/get: instrumental, syncedLyrics (LRC), plainLyrics.</summary>
    internal static Lyrics FromAnswer(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return None;
        if (answer.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True) return None;
        static string? Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Text(answer, "syncedLyrics") is { } lrc && ParseLrc(lrc) is { Count: > 0 } timed) return new Lyrics(timed, true);
        if (Text(answer, "plainLyrics") is { } plain)
        {
            var lines = plain.Replace("\r\n", "\n").Split('\n').Take(MaxLines).Select(l => new Line(0, Clip(l.Trim(), MaxText))).ToList();
            while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Any(l => l.Text.Length > 0)) return new Lyrics(lines, false);
        }
        return None;
    }

    // [mm:ss], [mm:ss.xx], [mm:ss.xxx] (some files write [mm:ss:xx]); several may lead one line.
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    /// <summary>
    /// LRC to lines in time order. Tag lines ([ar:…], [length:…]) are skipped; [offset:+n] moves every line n ms earlier,
    /// as LRC defines it. A line that is only ♪ is a wait with nothing sung (empty text). Never more than 400 lines.
    /// </summary>
    internal static List<Line> ParseLrc(string lrc)
    {
        var lines = new List<Line>();
        var offset = 0;
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            if (Offset().Match(raw) is { Success: true } shift)
            {
                offset = int.TryParse(shift.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
                continue;
            }
            var at = 0;
            var starts = new List<int>();
            for (var m = Stamp().Match(raw); m.Success && m.Index == at; m = m.NextMatch())
            {
                var fraction = m.Groups[3].Value;
                var millis = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (seconds < 60) starts.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60_000 + seconds * 1000 + millis);
                at = m.Index + m.Length;
            }
            if (starts.Count == 0) continue;
            var text = raw[at..].Trim();
            if (text.Trim('♪', '♫', ' ').Length == 0) text = "";
            foreach (var start in starts) lines.Add(new Line(Math.Max(0, start - offset), Clip(text, MaxText)));
        }
        // Stable: lines that share a time keep the file's order.
        return lines.OrderBy(l => l.Start).Take(MaxLines).ToList();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
