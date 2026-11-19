using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's private file hosts (captures, profile media, note photos, device pictures, the cover cache and its
/// thumbnails) could fail:
/// - a frame that isn't Exo's names a file without the run's token, a near-miss of it, or the token in another place;
/// - a path climbs out of the root: dot-dot plain or percent-encoded, an encoded slash or backslash, a drive or UNC
///   path, a stream's colon, a device name, Windows' trailing-dot and trailing-space aliases;
/// - a file type the host doesn't serve is answered;
/// - a page that says it is another site (Origin or Referer) is answered;
/// - a byte range is read wrong: a seek answered from 0, an open end or a suffix miscounted, an end past the file not
///   clamped, a start past the end answered instead of refused, several ranges or junk treated as one.
/// </summary>
public sealed class LocalFileHostTests
{
    private const string Token = "012345" + "6789ab" + "cdef01" + "234567" + "89abcd" + "ef";
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "exo-file-host-root");
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase) { [".mp4"] = "video/mp4", [".png"] = "image/png" };

    [Theory]
    [InlineData("/" + Token + "/clip.mp4", "clip.mp4", "video/mp4")]
    [InlineData("/" + Token + "/Elden%20Ring/Elden%20Ring%202026-10-30%2012-00-00.mp4", @"Elden Ring\Elden Ring 2026-10-30 12-00-00.mp4", "video/mp4")]
    [InlineData("/" + Token + "/Snip.PNG", "Snip.PNG", "image/png")]
    [InlineData("/" + Token + "/caf%C3%A9.png", "café.png", "image/png")]
    public void NamedFileUnderTheRootIsServed(string path, string relative, string type)
    {
        var found = LocalFileHost.ResolveFile(Root, path, Token, Types);
        Assert.NotNull(found);
        Assert.Equal(Path.Combine(Root, relative), found.Value.Path);
        Assert.Equal(type, found.Value.Type);
    }

    [Theory]
    [InlineData("/clip.mp4")]
    [InlineData("/" + Token)]
    [InlineData("/" + Token + "/")]
    [InlineData("/0123456789abcdef0123456789abcdee/clip.mp4")]
    [InlineData("/0123456789ABCDEF0123456789ABCDEF/clip.mp4")]
    [InlineData("/x/" + Token + "/clip.mp4")]
    [InlineData(Token + "/clip.mp4")]
    [InlineData("/" + Token + "/../clip.mp4")]
    [InlineData("/" + Token + "/%2e%2e/clip.mp4")]
    [InlineData("/" + Token + "/a/%2E%2E/%2E%2E/clip.mp4")]
    [InlineData("/" + Token + "/./clip.mp4")]
    [InlineData("/" + Token + "/..%2fclip.mp4")]
    [InlineData("/" + Token + "/..%5Cclip.mp4")]
    [InlineData("/" + Token + "/a%5C..%5C..%5Cclip.mp4")]
    [InlineData("/" + Token + "/C:%5CWindows%5Cclip.mp4")]
    [InlineData("/" + Token + "/%5C%5Cserver%5Cshare%5Cclip.mp4")]
    [InlineData("/" + Token + "/clip.mp4:secret.mp4")]
    [InlineData("/" + Token + "/CON.mp4")]
    [InlineData("/" + Token + "/nul.png")]
    [InlineData("/" + Token + "/COM1.mp4")]
    [InlineData("/" + Token + "/clip.mp4.")]
    [InlineData("/" + Token + "/clip.mp4%20")]
    [InlineData("/" + Token + "/a//clip.mp4")]
    [InlineData("/" + Token + "/clip%00.mp4")]
    [InlineData("/" + Token + "/settings.json")]
    [InlineData("/" + Token + "/clip.mp4.exe")]
    [InlineData("/" + Token + "/clip")]
    public void AnythingElseIsNotFound(string path) =>
        Assert.Null(LocalFileHost.ResolveFile(Root, path, Token, Types));

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://app.exo-launcher.local", null)]
    [InlineData(null, "https://app.exo-launcher.local/index.html")]
    public void ShellRequestsAreAnswered(string? origin, string? referer) =>
        Assert.True(LocalFileHost.IsExoRequest(origin, referer));

    [Theory]
    [InlineData("https://www.youtube.com", null)]
    [InlineData(null, "https://www.youtube.com/embed/x")]
    [InlineData("null", null)]
    [InlineData("https://mouse.exo-launcher.local", null)]
    [InlineData("https://pay.exo-launcher.local", null)]
    [InlineData("https://shaders.exo-launcher.local", null)]
    [InlineData("http://app.exo-launcher.local", null)]
    [InlineData("https://app.exo-launcher.local.evil.test", null)]
    [InlineData("https://app.exo-launcher.local", "https://open.spotify.com/")]
    public void OtherPagesAreRefused(string? origin, string? referer) =>
        Assert.False(LocalFileHost.IsExoRequest(origin, referer));

    [Theory]
    [InlineData(null, 0, 999)]
    [InlineData("", 0, 999)]
    [InlineData("bytes=0-0,5-9", 0, 999)]
    [InlineData("items=0-9", 0, 999)]
    [InlineData("bytes=9-5", 0, 999)]
    [InlineData("bytes=a-9", 0, 999)]
    [InlineData("bytes=-", 0, 999)]
    [InlineData("bytes=+5-9", 0, 999)]
    [InlineData("bytes=5", 0, 999)]
    public void NoOrInvalidRangeIsTheWholeFile(string? header, long first, long last)
    {
        Assert.Equal(LocalFileHost.RangeKind.Whole, LocalFileHost.ParseRange(header, 1000, out var a, out var b));
        Assert.Equal((first, last), (a, b));
    }

    [Theory]
    [InlineData("bytes=0-", 0, 999)]
    [InlineData("bytes=500-", 500, 999)]
    [InlineData("bytes=500-599", 500, 599)]
    [InlineData("bytes=999-999", 999, 999)]
    [InlineData("bytes=900-5000", 900, 999)]
    [InlineData("bytes=-100", 900, 999)]
    [InlineData("bytes=-5000", 0, 999)]
    [InlineData("BYTES= 10 - 19 ", 10, 19)]
    public void OneRangeIsThatPart(string header, long first, long last)
    {
        Assert.Equal(LocalFileHost.RangeKind.Part, LocalFileHost.ParseRange(header, 1000, out var a, out var b));
        Assert.Equal((first, last), (a, b));
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=1000-1001")]
    [InlineData("bytes=-0")]
    public void RangePastTheEndIsUnsatisfiable(string header) =>
        Assert.Equal(LocalFileHost.RangeKind.Unsatisfiable, LocalFileHost.ParseRange(header, 1000, out _, out _));

    [Fact]
    public void EmptyFileIsWhole() =>
        Assert.Equal(LocalFileHost.RangeKind.Whole, LocalFileHost.ParseRange("bytes=0-", 0, out _, out _));

    [Theory]
    [InlineData("/" + Token + "/hero_steam_1245620.jpg", "hero_steam_1245620.jpg")]
    [InlineData("/" + Token + "/caf%C3%A9.jpg", "café.jpg")]
    public void MadeHostNamesOneFile(string path, string name) =>
        Assert.Equal(name, LocalFileHost.ResolveName(path, Token));

    [Theory]
    [InlineData("/hero.jpg")]
    [InlineData("/0123456789abcdef0123456789abcdee/hero.jpg")]
    [InlineData("/" + Token + "/")]
    [InlineData("/" + Token + "/sub/hero.jpg")]
    [InlineData("/" + Token + "/..")]
    [InlineData("/" + Token + "/..%5Chero.jpg")]
    [InlineData("/" + Token + "/CON.jpg")]
    public void MadeHostRefusesAnythingElse(string path) =>
        Assert.Null(LocalFileHost.ResolveName(path, Token));

    private const string Covers = "covers.exo-launcher.local";

    [Fact]
    public void AddressesGainTheTokenOnTheWayToThePage()
    {
        const string sent = """{"coverUrl":"https://covers.exo-launcher.local/a.jpg","heroUrl":"https://covers.exo-launcher.local/sub/b.jpg?v=2","other":"https://covers.exo-launcher.local.evil.test/c.jpg","x":"https://www.covers.exo-launcher.local/"}""";
        var page = LocalFileHost.WithToken(sent, Covers, Token);
        Assert.Contains($"\"https://covers.exo-launcher.local/{Token}/a.jpg\"", page);
        Assert.Contains($"\"https://covers.exo-launcher.local/{Token}/sub/b.jpg?v=2\"", page);
        Assert.Contains("\"https://covers.exo-launcher.local.evil.test/c.jpg\"", page);
        // Already tokened: left as it is; the way back gives exactly what was sent.
        Assert.Equal(page, LocalFileHost.WithToken(page, Covers, Token));
        Assert.Equal(sent, LocalFileHost.WithoutToken(page, Covers, Token));
        Assert.Same(sent, LocalFileHost.WithToken(sent, "thumbs.exo-launcher.local", Token));
    }

    [Fact]
    public void BridgeSendsCoverAndThumbnailAddressesWithTheToken()
    {
        var json = ExoBridgeProtocol.SerializeEvent("library.updated", new { coverUrl = "https://covers.exo-launcher.local/a b+c.jpg", thumb = "https://thumbs.exo-launcher.local/a.jpg?w=240&dim=1" });
        var page = WebHostBridge.ToPage(json);
        Assert.Contains($"https://covers.exo-launcher.local/{LocalFileHost.Token}/a b", page);
        Assert.Contains($"https://thumbs.exo-launcher.local/{LocalFileHost.Token}/a.jpg?w=240", page);
        Assert.Equal(json, WebHostBridge.FromPage(page));
    }

    [Fact]
    public void UrlsCarryTheRunsToken()
    {
        var url = new Uri(LocalFileHost.Url("captures.exo-launcher.local", "Elden%20Ring/clip.mp4") + "?v=1");
        Assert.NotNull(LocalFileHost.ResolveFile(Root, url.AbsolutePath, LocalFileHost.Token, Types));
        Assert.Matches("^[0-9a-f]{32}$", LocalFileHost.Token);
    }
}
