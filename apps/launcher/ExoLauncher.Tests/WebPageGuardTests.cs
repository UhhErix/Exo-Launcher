using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Tests;

/// <summary>
/// What another site's page in an Exo WebView2 may open outside it, and what it is told when it doesn't load. Ways it
/// could fail, silently: a page's own address (or the browser's own scheme) taken for a link to another app; a scheme
/// used to run code from a page (ms-msdt, search-ms, ms-officecmd, ms-appinstaller, ms-cxh) offered at all, in any case;
/// a drive path, a spaced or quoted address handed to Windows' shell as a link; a page that keeps asking let through, or
/// a refused page let back in before it navigates; a navigation that was only cancelled, handed to another app or to a
/// download shown as a failure; a certificate that stopped a page (reported Unknown) not called out as one.
/// </summary>
public sealed class WebPageGuardTests
{
    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", "spotify")]
    [InlineData("mailto:someone@example.com", "mailto")]
    [InlineData("zoommtg://zoom.us/join?confno=123", "zoommtg")]
    [InlineData("steam://store/730", "steam")]
    [InlineData("ms-settings:display", "ms-settings")]
    [InlineData("Microsoft-Edge:https://example.com", "microsoft-edge")]
    public void OffersAppLinks(string link, string scheme)
    {
        Assert.Equal(scheme, WebPageGuard.SchemeOf(link));
        Assert.True(WebPageGuard.IsAppLink(link));
        Assert.True(WebPageGuard.MayOpen(link));
    }

    [Theory]
    [InlineData("https://music.youtube.com/")]
    [InlineData("http://example.com/")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,hi")]
    [InlineData("blob:https://music.youtube.com/0a1b")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("edge://settings")]
    [InlineData("chrome-extension://abc/options.html")]
    [InlineData("view-source:https://example.com")]
    public void LeavesTheBrowsersOwnSchemesToIt(string address)
    {
        Assert.False(WebPageGuard.IsAppLink(address));
        Assert.False(WebPageGuard.MayOpen(address));
    }

    [Theory]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("ms-msdt:-id%20PCWDiagnostic")]
    [InlineData("MS-MSDT:-id")]
    [InlineData("search-ms:query=x&crumb=location:%5C%5Cevil%5Cshare")]
    [InlineData("search:query=x")]
    [InlineData("ms-officecmd:%7B%7D")]
    [InlineData("ms-appinstaller:?source=https://example.com/a.msix")]
    [InlineData("ms-cxh://setaddlocalonly")]
    [InlineData("ms-cxh-full://x")]
    [InlineData("shell:startup")]
    [InlineData("hcp://services/search")]
    [InlineData("ms-its:file.chm::/a.htm")]
    [InlineData("vbscript:msgbox")]
    public void NeverOpensSchemesThatRunCode(string link)
    {
        Assert.True(WebPageGuard.IsAppLink(link));
        Assert.False(WebPageGuard.MayOpen(link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-scheme-here")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("c:/Windows/System32/calc.exe")]
    [InlineData("1abc:x")]
    [InlineData("sp otify:x")]
    [InlineData(":x")]
    public void IsNoLinkAtAll(string? address)
    {
        Assert.Null(WebPageGuard.SchemeOf(address));
        Assert.False(WebPageGuard.MayOpen(address));
    }

    [Theory]
    [InlineData("spotify:track:1 --flag")]
    [InlineData("spotify:\"track\"")]
    [InlineData("spotify:track:1\n")]
    [InlineData("spotify:track:1\t")]
    public void NeverHandsTheShellAnythingButOnePlainAddress(string link) => Assert.False(WebPageGuard.MayOpen(link));

    [Fact]
    public void NeverOpensAnOverlongLink()
    {
        Assert.True(WebPageGuard.MayOpen("spotify:" + new string('a', 4000)));
        Assert.False(WebPageGuard.MayOpen("spotify:" + new string('a', 4100)));
    }

    private static readonly DateTime Start = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RefusesTheThirdAskWithinTenSecondsUntilThePageNavigates()
    {
        var limit = new AppAskLimit();
        Assert.True(limit.Ask(Start));
        Assert.True(limit.Ask(Start.AddSeconds(4)));
        Assert.False(limit.Ask(Start.AddSeconds(9)));
        Assert.True(limit.Refused);
        // Time alone doesn't let it back in.
        Assert.False(limit.Ask(Start.AddMinutes(5)));
        limit.Navigated();
        Assert.False(limit.Refused);
        Assert.True(limit.Ask(Start.AddMinutes(5)));
    }

    [Fact]
    public void LetsAsksSpreadOverTimeThrough()
    {
        var limit = new AppAskLimit();
        for (var i = 0; i < 10; i++) Assert.True(limit.Ask(Start.AddSeconds(i * 5.5)));
        Assert.False(limit.Refused);
    }

    [Fact]
    public void CountsOnlyAsksWithinTheWindow()
    {
        var limit = new AppAskLimit();
        Assert.True(limit.Ask(Start));
        Assert.True(limit.Ask(Start.AddSeconds(9)));
        // The first is now 10 seconds old: two in the window, not three.
        Assert.True(limit.Ask(Start.AddSeconds(10)));
        Assert.False(limit.Ask(Start.AddSeconds(11)));
    }

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.Disconnected, "You're offline")]
    [InlineData(CoreWebView2WebErrorStatus.HostNameNotResolved, "Can't find music.youtube.com")]
    [InlineData(CoreWebView2WebErrorStatus.Timeout, "music.youtube.com took too long to answer")]
    [InlineData(CoreWebView2WebErrorStatus.CannotConnect, "music.youtube.com won't connect")]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionReset, "The connection to music.youtube.com broke")]
    [InlineData(CoreWebView2WebErrorStatus.CertificateExpired, "The connection to music.youtube.com isn't private")]
    [InlineData(CoreWebView2WebErrorStatus.Unknown, "Can't reach music.youtube.com")]
    public void SaysWhyAPageDidntLoad(CoreWebView2WebErrorStatus status, string cause) =>
        Assert.Equal(cause, WebPageGuard.Cause(status, "music.youtube.com", null));

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.OperationCanceled)]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionAborted)]
    [InlineData(CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired)]
    public void IsNoFailureWhenOnlyCancelledOrHandedOn(CoreWebView2WebErrorStatus status) =>
        Assert.Null(WebPageGuard.Cause(status, "music.youtube.com", null));

    [Fact]
    public void CallsACertificateStopByItsName()
    {
        Assert.Equal("The connection to idmsa.apple.com isn't private",
            WebPageGuard.Cause(CoreWebView2WebErrorStatus.Unknown, "idmsa.apple.com", "idmsa.apple.com"));
        // Another host's certificate (a picture on the last page) isn't this page's.
        Assert.Equal("Can't reach idmsa.apple.com",
            WebPageGuard.Cause(CoreWebView2WebErrorStatus.Unknown, "idmsa.apple.com", "cdn.example.com"));
    }

    [Theory]
    [InlineData("https://music.youtube.com/watch?v=1", "music.youtube.com")]
    [InlineData("http://example.com/", "example.com")]
    [InlineData("https://bücher.example/", "xn--bcher-kva.example")]
    [InlineData("spotify:track:1", null)]
    [InlineData("about:blank", null)]
    [InlineData("null", null)]
    [InlineData(null, null)]
    public void NamesOnlyAWebPagesHost(string? address, string? host)
    {
        Assert.Equal(host, WebPageGuard.HostOf(address));
        if (host is null) Assert.Null(WebPageGuard.Cause(CoreWebView2WebErrorStatus.Disconnected, WebPageGuard.HostOf(address), null));
    }
}
