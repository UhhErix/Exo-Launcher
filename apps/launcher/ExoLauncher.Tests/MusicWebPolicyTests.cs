using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Apple Music and YouTube Music sign-in can fail closed, or let the wrong page in:
/// - Apple's catalog script still names a stand-in storefront, so every catalog page is refused;
/// - a Google or Apple sign-in hop (the device check, the account cookie, a step under /signin) is not a service
///   document, so the sign-in window closes before the account page finishes;
/// - YouTube's own site, or another host that only looks like one, is treated as sign-in.
/// </summary>
public sealed class MusicWebPolicyTests
{
    [Theory]
    [InlineData("https://gds.google.com/")]
    [InlineData("https://accounts.google.com/v3/signin/identifier")]
    [InlineData("https://www.youtube.com/signin")]
    [InlineData("https://www.youtube.com/signin/identifier")]
    [InlineData("https://youtube.com/signin")]
    [InlineData("https://www.google.com/accounts/SetSID")]
    [InlineData("https://music.youtube.com/")]
    public void YouTubeSignInKeepsGooglesOwnHops(string uri) =>
        Assert.True(MusicWebPolicy.IsServiceDocument("youtube", uri));

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://evil.youtube.com/signin")]
    [InlineData("https://www.google.com/search")]
    [InlineData("https://music.apple.com/")]
    public void YouTubeSignInRefusesTheRestOfTheWeb(string uri) =>
        Assert.False(MusicWebPolicy.IsServiceDocument("youtube", uri));

    [Theory]
    [InlineData("https://gsa.apple.com/")]
    [InlineData("https://setup.icloud.com/")]
    [InlineData("https://idmsa.apple.com/appleauth/auth/authorize/signin")]
    [InlineData("https://authorize.music.apple.com/")]
    [InlineData("https://account.apple.com/")]
    public void AppleSignInKeepsApplesOwnHops(string uri) =>
        Assert.True(MusicWebPolicy.IsServiceDocument("apple", uri));

    [Fact]
    public void AppleSignInWindowKeepsTheRestOfApplesAccountHosts()
    {
        Assert.True(MusicWebPolicy.IsSignInHop("apple", "https://id.apple.com/"));
        Assert.True(MusicWebPolicy.IsSignInHop("apple", "https://www.icloud.com/"));
        Assert.False(MusicWebPolicy.IsSignInHop("apple", "https://accounts.google.com/"));
        Assert.False(MusicWebPolicy.IsServiceDocument("apple", "https://id.apple.com/"));
    }

    [Fact]
    public void AppleSignInDoesNotFollowGoogle() =>
        Assert.False(MusicWebPolicy.IsServiceDocument("apple", "https://accounts.google.com/"));
}
