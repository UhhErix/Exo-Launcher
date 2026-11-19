using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The shared WebView2 folder (L1), where people's sign-ins live: anything joining it passes the spec's argument string
/// byte for byte, and WebView2 refuses an app whose options differ from the running group's. Ways it could fail:
/// Launcher's own arguments drift from the spec (a flag added to the builder only), so it can never join; a test port or
/// a person's own WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS changes the string and Launcher joins anyway (WebView2 then
/// refuses the whole group); a port in the wrong place or of the wrong size is taken as the spec's test form.
/// </summary>
public sealed class SharedWebViewTests
{
    [Fact]
    public void LaunchersOwnArgumentsWithNoSwitchJoinTheSharedFolder()
    {
        var own = WebViewHostProfile.AdditionalBrowserArguments(null, null, null);
        Assert.Equal(WebViewHostProfile.SharedArguments, own);
        Assert.True(WebViewHostProfile.MayJoinShared(own));
    }

    [Fact]
    public void AJointTestRunPutsThePortFirstAndStillJoins()
    {
        var withPort = WebViewHostProfile.AdditionalBrowserArguments("--remote-debugging-port=9333", null, null);
        Assert.Equal("--remote-debugging-port=9333 " + WebViewHostProfile.SharedArguments, withPort);
        Assert.True(WebViewHostProfile.MayJoinShared(withPort));
    }

    [Theory]
    [InlineData("--disable-gpu")]
    [InlineData("--lang=de")]
    [InlineData("--remote-debugging-port=9333 --disable-gpu")]
    public void AnyOtherSwitchKeepsLauncherInItsOwnFolder(string extra) =>
        Assert.False(WebViewHostProfile.MayJoinShared(WebViewHostProfile.AdditionalBrowserArguments(extra, null, null)));

    [Theory]
    [InlineData("--remote-debugging-port=93 ")]
    [InlineData("--remote-debugging-port=933333 ")]
    [InlineData("--remote-debugging-port=abcd ")]
    [InlineData(" ")]
    public void OnlyTheSpecsTestFormJoins(string prefix) =>
        Assert.False(WebViewHostProfile.MayJoinShared(prefix + WebViewHostProfile.SharedArguments));

    [Fact]
    public void TheSpecStringAloneWithAnythingAfterItDoesNotJoin() =>
        Assert.False(WebViewHostProfile.MayJoinShared(WebViewHostProfile.SharedArguments + " --disable-gpu"));
}
