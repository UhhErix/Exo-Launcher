using System.Globalization;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>
/// The shell WebView's private files (captures and their thumbnails, profile media, note photos, device pictures, the game
/// art cache and its thumbnails, which tell what you own), each on
/// its own host, served here rather than by a folder mapping. The shell page holds content Exo doesn't control: the
/// players its CSP frames (YouTube, Twitch, Vimeo, TikTok, SoundCloud, Spotify, Apple Music) and whatever those frames
/// nest, Stripe's script on the pay host and the shader add-on's page. A mapping answers any of them that asks once the
/// shell has loaded the host, and with Allow a frame could read a clip's bytes or a snip's pixels;
/// a request carries no frame of its own to check, WebView2 hands this handler no Sec-Fetch-Site, and the shell sends no
/// Referer (its no-referrer meta), so an absent header proves nothing. So every URL carries <see cref="Token"/>, a secret made each run that only the shell is given
/// (through the bridge, which answers the shell document alone): a frame can't ask for a file it can't name. A request
/// that names another page as its Origin or Referer is refused too. Refusals and misses are the same 404, so nothing
/// tells a page which files exist. Ranges are answered from the file as it is read (seeking and looping a video never
/// holds it in memory), a video at most <see cref="MaxVideoAnswer"/> bytes per answer. Private files are never kept
/// (<see cref="NoStore"/>); the cover cache and its thumbnails, drawn everywhere, are kept for the run
/// (<see cref="KeptForRun"/>): the token changes every run, so a kept answer can't be named again after it.
/// </summary>
internal sealed class LocalFileHost
{
    internal static readonly string Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    internal const long MaxVideoAnswer = 8L << 20;
    private const string Refused = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";
    /// <summary>Never kept: the user's own clips, snips, profile media, note photos and device pictures.</summary>
    internal const string NoStore = "no-store";
    /// <summary>Kept by this run's browser cache; the next run's token is another address.</summary>
    internal const string KeptForRun = "private, max-age=86400";

    internal static readonly IReadOnlyDictionary<string, string> Pictures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
    };

    /// <summary>A picture made for a request (a cover's thumbnail): the one plain name after the token, and the query.</summary>
    internal delegate Task<(byte[] Bytes, string Type)?> Maker(string name, string query);

    internal sealed record Site(Func<string?> Root, IReadOnlyDictionary<string, string> Types, string Cache, Maker? Make = null);

    internal enum RangeKind { Whole, Part, Unsatisfiable }

    private readonly CoreWebView2 _core;
    private readonly DispatcherQueue _queue;
    private readonly Dictionary<string, Site> _sites = new(StringComparer.OrdinalIgnoreCase);

    internal LocalFileHost(CoreWebView2 core, DispatcherQueue queue)
    {
        _core = core;
        _queue = queue;
        core.WebResourceRequested += (sender, e) => UiGuard.Run(() => OnRequested(sender, e));
    }

    /// <summary>A file's address: <paramref name="path"/> is already escaped, segment by segment; empty gives the host's base.</summary>
    internal static string Url(string host, string path) => $"https://{host}/{Token}/{path}";

    /// <summary>Every request to the host comes here, from every frame and worker: one not answered here would go to the network.</summary>
    internal void Serve(string host, Func<string?> root, IReadOnlyDictionary<string, string> types, string cache = NoStore) =>
        Add(host, new Site(root, types, cache));

    /// <summary>A host whose answers <paramref name="make"/> works out (null: not found), with the same token and page checks.</summary>
    internal void ServeMade(string host, Maker make, string cache = NoStore) =>
        Add(host, new Site(() => null, Pictures, cache, make));

    private void Add(string host, Site site)
    {
        _sites[host] = site;
        _core.AddWebResourceRequestedFilter($"https://{host}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
    }

    private void OnRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !_sites.TryGetValue(uri.IdnHost, out var site)) return;
        var environment = sender.Environment;
        var headers = e.Request.Headers;
        string? Header(string name) => headers.Contains(name) ? headers.GetHeader(name) : null;
        var origin = Header("Origin");
        var method = e.Request.Method;
        if (method is not ("GET" or "HEAD") || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || !IsExoRequest(origin, Header("Referer")))
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", Refused);
            return;
        }
        var range = Header("Range");
        var deferral = e.GetDeferral();
        var head = method == "HEAD";
        _ = Task.Run(() => site.Make is { } make ? AnswerMadeAsync(make, uri, site.Cache, origin, head) : Task.FromResult(Answer(site, uri.AbsolutePath, origin, range, head))).ContinueWith(task =>
        {
            var answer = task.IsCompletedSuccessfully ? task.Result : (404, "Not Found", Refused, null);
            if (_queue.Enqueue(DispatcherQueuePriority.High, () =>
            {
                try { e.Response = environment.CreateWebResourceResponse(answer.Body?.AsRandomAccessStream(), answer.Status, answer.Reason, answer.Headers); }
                catch { answer.Body?.Dispose(); }
                finally { deferral.Complete(); }
            })) return;
            answer.Body?.Dispose();
            deferral.Complete();
        }, TaskScheduler.Default);
    }

    private static async Task<(int Status, string Reason, string Headers, Stream? Body)> AnswerMadeAsync(Maker make, Uri uri, string cache, string? origin, bool head)
    {
        if (ResolveName(uri.AbsolutePath, Token) is not { } name || await make(name, uri.Query).ConfigureAwait(false) is not { } made)
            return (404, "Not Found", Refused, null);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Content-Type: {made.Type}\r\nContent-Length: {made.Bytes.Length}\r\n{Kept(cache, origin)}");
        return (200, "OK", text, head ? null : new MemoryStream(made.Bytes, writable: false));
    }

    /// <summary>
    /// The headers every answer carries. Vary: Origin always, so a kept answer to a plain img (no Origin, no
    /// Access-Control-Allow-Origin) is never handed to the shell's CORS read of the same address, nor the other way round.
    /// </summary>
    private static string Kept(string cache, string? origin) =>
        $"Cache-Control: {cache}\r\nX-Content-Type-Options: nosniff\r\nCross-Origin-Resource-Policy: same-site\r\nVary: Origin\r\n" +
        // The shell's own CORS reads (the snip editor's canvas, a profile video's still, Liquid Glass) name it as their Origin.
        (origin is null ? "" : $"Access-Control-Allow-Origin: {origin}\r\n");

    private static (int Status, string Reason, string Headers, Stream? Body) Answer(Site site, string absolutePath, string? origin, string? range, bool head)
    {
        if (site.Root() is not { } root || ResolveFile(root, absolutePath, Token, site.Types) is not { } found)
            return (404, "Not Found", Refused, null);
        FileStream file;
        try { file = new FileStream(found.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (404, "Not Found", Refused, null); }
        var total = file.Length;
        var kind = ParseRange(range, total, out var first, out var last);
        var video = found.Type.StartsWith("video/", StringComparison.Ordinal);
        var cors = origin is null ? "" : $"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\n";
        if (kind == RangeKind.Unsatisfiable)
        {
            file.Dispose();
            return (416, "Range Not Satisfiable", string.Create(CultureInfo.InvariantCulture, $"Content-Range: bytes */{total}\r\n{cors}{Refused}"), null);
        }
        // A video's first ask carries a range (bytes=0-); one without is cut to its start the same way.
        if (kind == RangeKind.Whole && video && total > MaxVideoAnswer) kind = RangeKind.Part;
        if (video) last = Math.Min(last, first + MaxVideoAnswer - 1);
        var length = total == 0 ? 0 : last - first + 1;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Content-Type: {found.Type}\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\n{Kept(site.Cache, origin)}");
        if (kind == RangeKind.Part) text += string.Create(CultureInfo.InvariantCulture, $"Content-Range: bytes {first}-{last}/{total}\r\n");
        if (head)
        {
            file.Dispose();
            return (kind == RangeKind.Part ? 206 : 200, kind == RangeKind.Part ? "Partial Content" : "OK", text, null);
        }
        return (kind == RangeKind.Part ? 206 : 200, kind == RangeKind.Part ? "Partial Content" : "OK", text, new Slice(file, found.Path, first, length));
    }

    /// <summary>
    /// A request Exo's shell may have made: no Origin or Referer, or the shell's own. Absence is all the shell sends to
    /// these hosts (no-referrer, and an img or video without crossorigin sends no Origin), so it can't be required; the
    /// token is what keeps other frames out, this only refuses a page that says who it is.
    /// </summary>
    internal static bool IsExoRequest(string? origin, string? referer) =>
        (origin is null || WebViewTrustPolicy.IsTrustedAppUri(origin)) &&
        (referer is null || WebViewTrustPolicy.IsTrustedAppUri(referer));

    /// <summary>
    /// The file a request's path names under <paramref name="root"/>: "/token/segment/…/name.ext", each segment
    /// unescaped on its own, with an extension the host serves. Null for a wrong or missing token, an empty, dot, dot-dot
    /// or Windows-aliased segment (trailing dot or space, a device name, a stream's colon), a separator or a control
    /// character hidden in a segment, and anything that resolves outside the root.
    /// </summary>
    internal static (string Path, string Type)? ResolveFile(string root, string absolutePath, string token, IReadOnlyDictionary<string, string> types)
    {
        if (Names(absolutePath, token) is not { } names) return null;
        if (!types.TryGetValue(Path.GetExtension(names[^1]), out var type)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var path = Path.GetFullPath(Path.Combine([full, .. names]));
            return path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? (path, type) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The one plain name a made host's path holds after the token ("/token/name"); null for anything else.</summary>
    internal static string? ResolveName(string absolutePath, string token) =>
        Names(absolutePath, token) is [var name] ? name : null;

    /// <summary>The unescaped segments after the token, each a plain name; null for a wrong token or any other segment.</summary>
    private static string[]? Names(string absolutePath, string token)
    {
        var segments = absolutePath.Split('/');
        if (segments.Length < 3 || segments[0].Length != 0 ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(segments[1]), System.Text.Encoding.ASCII.GetBytes(token)))
            return null;
        var names = new string[segments.Length - 2];
        for (var i = 2; i < segments.Length; i++)
        {
            string name;
            try { name = Uri.UnescapeDataString(segments[i]); }
            catch (UriFormatException) { return null; }
            if (!IsPlainName(name)) return null;
            names[i - 2] = name;
        }
        return names;
    }

    /// <summary>
    /// What the shell is sent, with this run's token after every "https://host/" in it that lacks one. Kept addresses
    /// (the cover cache's, in the library's saved state and settings) stay token-free on disk and in Exo's own code;
    /// they gain the token only on their way to the page.
    /// </summary>
    internal static string WithToken(string text, string host) => WithToken(text, host, Token);

    internal static string WithToken(string text, string host, string token)
    {
        var start = "https://" + host + "/";
        var at = text.IndexOf(start, StringComparison.Ordinal);
        if (at < 0) return text;
        var tokened = token + "/";
        var built = new System.Text.StringBuilder(text.Length + 64);
        var from = 0;
        for (; at >= 0; at = text.IndexOf(start, from, StringComparison.Ordinal))
        {
            var after = at + start.Length;
            built.Append(text, from, after - from);
            if (string.CompareOrdinal(text, after, tokened, 0, tokened.Length) != 0) built.Append(tokened);
            from = after;
        }
        return built.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>What the shell sends back, with this run's token taken out of the host's addresses: what Exo keeps is token-free.</summary>
    internal static string WithoutToken(string text, string host) => WithoutToken(text, host, Token);

    internal static string WithoutToken(string text, string host, string token)
    {
        var tokened = "https://" + host + "/" + token + "/";
        return text.Contains(tokened, StringComparison.Ordinal) ? text.Replace(tokened, "https://" + host + "/", StringComparison.Ordinal) : text;
    }

    private static readonly char[] Unsafe = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':'];
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && name[^1] is not ('.' or ' ') &&
        name.IndexOfAny(Unsafe) < 0 && !DeviceNames.Contains(name.Split('.')[0].TrimEnd(' '));

    /// <summary>
    /// One byte range ("bytes=a-b", "bytes=a-" or "bytes=-n") of a file of <paramref name="length"/> bytes, the end
    /// clamped to the file. A header that isn't one valid range (several ranges, another unit, junk) is ignored, as HTTP
    /// allows: the whole file. A start past the end, or an empty suffix, is unsatisfiable.
    /// </summary>
    internal static RangeKind ParseRange(string? header, long length, out long first, out long last)
    {
        first = 0;
        last = length - 1;
        if (string.IsNullOrWhiteSpace(header) || length <= 0) return RangeKind.Whole;
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return RangeKind.Whole;
        var spec = value[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0 || spec.Contains(',')) return RangeKind.Whole;
        var from = spec[..dash].Trim();
        var to = spec[(dash + 1)..].Trim();
        const NumberStyles digits = NumberStyles.None;
        if (from.Length == 0)
        {
            if (!long.TryParse(to, digits, CultureInfo.InvariantCulture, out var suffix)) return RangeKind.Whole;
            if (suffix == 0) return RangeKind.Unsatisfiable;
            first = Math.Max(0, length - suffix);
            return RangeKind.Part;
        }
        if (!long.TryParse(from, digits, CultureInfo.InvariantCulture, out var start)) return RangeKind.Whole;
        long end = length - 1;
        if (to.Length > 0 && (!long.TryParse(to, digits, CultureInfo.InvariantCulture, out end) || end < start)) return RangeKind.Whole;
        if (start >= length) return RangeKind.Unsatisfiable;
        first = start;
        last = Math.Min(end, length - 1);
        return RangeKind.Part;
    }

    /// <summary>
    /// <paramref name="length"/> bytes of a file from <paramref name="start"/>, read as WebView2 asks. The file closes
    /// once the slice is read, so a capture can be trimmed, moved or deleted without waiting for the stream's finalizer;
    /// a later seek back opens it again.
    /// </summary>
    private sealed class Slice(FileStream file, string path, long start, long length) : Stream
    {
        private FileStream? _file = file;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var want = (int)Math.Min(buffer.Length, length - _position);
            if (want <= 0) { Release(); return 0; }
            _file ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            _file.Position = start + _position;
            var read = _file.Read(buffer[..want]);
            _position += read;
            if (read == 0 || _position >= length) Release();
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Current => _position + offset, SeekOrigin.End => length + offset, _ => offset };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Release()
        {
            _file?.Dispose();
            _file = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
            base.Dispose(disposing);
        }
    }
}
