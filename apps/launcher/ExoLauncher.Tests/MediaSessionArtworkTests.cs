using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Instant music cover forwarding and bounded LRU retention: CSP-whitelisted HTTPS artwork URLs reach the UI
/// immediately while data URLs download in the background, and 64 recent covers stay cached with LRU eviction.
/// </summary>
public sealed class MediaSessionArtworkTests
{
    [Theory]
    [InlineData("https://i.scdn.co/image/ab67616d0000b2731234")]
    [InlineData("https://mosaic.scdn.co/640/abcd")]
    [InlineData("https://image-cdn-ak.spotifycdn.com/image/cover")]
    [InlineData("https://lh3.googleusercontent.com/cover=w544-h544")]
    [InlineData("https://i.ytimg.com/vi/abc/hqdefault.jpg")]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/cover.jpg")]
    [InlineData("https://music.youtube.com/cover.png")]
    [InlineData("https://www.gstatic.com/music/cover.png")]
    public void AllowedArtworkUrlAcceptsCspWhitelistedHttpsHosts(string url) =>
        Assert.Equal(url, MediaSessionService.AllowedArtworkUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://i.scdn.co/image/insecure")]
    [InlineData("https://evil.example/cover.png")]
    [InlineData("https://scdn.co/bare-apex.png")]
    [InlineData("https://notscdn.co/lookalike.png")]
    [InlineData("https://yt3.ggpht.com/not-in-csp-img-src.png")]
    [InlineData("https://user:pass@i.scdn.co/image/cred")]
    [InlineData("javascript:alert(1)")]
    public void AllowedArtworkUrlRejectsNonCspAndInsecureUrls(string? url) =>
        Assert.Null(MediaSessionService.AllowedArtworkUrl(url));

    [Fact]
    public void CoverCacheKeeps64CoversWithTrueLruEviction()
    {
        Assert.Equal(64, MediaSessionService.KeptCovers);
        var cache = new MediaSessionService.CoverLru(MediaSessionService.KeptCovers);
        for (var i = 0; i < 64; i++) cache.Set($"url-{i}", $"data-{i}");
        Assert.Equal(64, cache.Count);

        // Touch url-0 so url-1 becomes the least recently used entry.
        Assert.True(cache.TryGet("url-0", out var hit));
        Assert.Equal("data-0", hit);

        cache.Set("url-64", "data-64");
        Assert.Equal(64, cache.Count);
        Assert.True(cache.TryGet("url-0", out _));
        Assert.False(cache.TryGet("url-1", out _));
        Assert.True(cache.TryGet("url-64", out _));
    }
}
