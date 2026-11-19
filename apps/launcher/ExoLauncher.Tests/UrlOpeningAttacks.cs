using ExoLauncher.Services;
using ExoLauncher.Ui;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 1 — opening URLs. The scheme/host gate that every shell-open path
/// shares is <see cref="ExoUrlLaunch.IsAllowed"/> (shell.openUrl, native
/// Settings/keys) and <see cref="StoreLinks.AppUri"/> (Get-on-store). These tests
/// feed the gate hostile input WITHOUT ever launching anything: they call the
/// pure predicate, not Process.Start, so nothing dangerous can run.
/// Ways it could fail: a dangerous scheme is approved; a steam:// form that runs
/// a game with arguments or a client command is approved; an approved web address
/// reaches the shell with a raw quote or space in it; a store page from a look-alike
/// host, or a Steam page whose app id is not a number, gets a launcher link; a slug
/// leaves its one path segment; a launcher that is not installed gets a link.
/// </summary>
public class UrlOpeningAttacks
{
    // Every payload with the gate's answer. Allowed: the web, Steam's harmless client pages, Windows Update's page.
    public static TheoryData<string, bool> HostileUrls() => new()
    {
        { "javascript:alert(1)", false },
        { "file:///C:/Windows/System32/calc.exe", false },
        { "file://server/share/evil.exe", false },
        { "ms-settings:", false },
        { "ms-settings:windowsupdate", true },
        { "search-ms:query=secret&crumb=location:C:\\", false },
        { "search-ms:displayname=Downloads", false },
        { "\\\\server\\share", false },
        { "\\\\attacker.example.com\\share\\payload.exe", false },
        { "vbscript:msgbox(1)", false },
        { "shell:startup", false },
        { "cmd:/c calc", false },
        { "mailto:x@y.z", false },
        { "ftp://evil/x", false },
        { "data:text/html,<script>1</script>", false },
        // steam:// with odd commands: run with arguments, an install with a shell tail, client commands.
        { "steam://run/1245620//-malicious-arg", false },
        { "steam://install/0; calc", false },
        { "steam://flushconfig", false },
        { "steam://uninstall/440", false },
        // steam://open/<page> only shows a client page (the console window included: it runs nothing by itself).
        { "steam://open/console", true },
        // epic launcher links never go through this gate (StoreLinks builds them).
        { "com.epicgames.launcher://store/../../etc", false },
        { "com.epicgames.launcher://apps/x?action=launch&silent=true", false },
        // quotes and spaces: a web page, handed on escaped (below).
        { "https://example.com/\" & calc & \"", true },
        { "https://example.com/a b c", true },
    };

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public void IsAllowed_answers_each_hostile_url(string url, bool expected)
    {
        var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && ExoUrlLaunch.IsAllowed(uri);
        Assert.Equal(expected, allowed);
        // What TryStart hands to the shell is the escaped absolute form: no quote or space can split it into a command.
        if (allowed) Assert.DoesNotMatch("[\" ]", uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:")]
    [InlineData("search-ms:query=x")]
    [InlineData("\\\\server\\share")]
    [InlineData("vbscript:1")]
    [InlineData("data:text/html,x")]
    public void IsAllowed_denies_dangerous_schemes(string url)
    {
        var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && ExoUrlLaunch.IsAllowed(uri);
        Assert.False(allowed, $"gate approved dangerous URL for shell open: {url}");
    }

    [Fact]
    public void TryStart_refuses_dangerous_without_launching()
    {
        // TryStart parses + gates before Process.Start. A denied URL returns
        // false and never reaches the shell. (It would only ShellExecute an
        // allowed http/steam/store URL, which this test does not pass.)
        foreach (var url in new[]
                 {
                     "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe",
                     "ms-settings:", "search-ms:query=x", "\\\\server\\share",
                 })
        {
            Assert.False(ExoUrlLaunch.TryStart(url), $"TryStart accepted {url}");
        }
    }

    [Fact]
    public void TryStart_handles_very_long_and_malformed_without_throwing()
    {
        var huge = "https://example.com/" + new string('a', 200_000);
        // A clearly invalid scheme gives a definite false without any launch, however long.
        Assert.False(ExoUrlLaunch.TryStart("ht!tp://%%%" + huge));
        Assert.False(ExoUrlLaunch.TryStart(null));
        Assert.False(ExoUrlLaunch.TryStart("   "));
    }

    // ---- Get-on-store launcher links (StoreLinks.AppUri), with the "launcher installed" read given ----

    private static readonly Func<string, bool> AllInstalled = _ => true;
    private static readonly Func<string, bool> NoneInstalled = _ => false;

    [Theory]
    [InlineData("https://store.steampowered.com/app/440/", "steam://store/440")]
    [InlineData("https://store.steampowered.com/app/440/Team_Fortress_2/", "steam://store/440")]
    [InlineData("https://store.epicgames.com/p/fortnite", "com.epicgames.launcher://store/p/fortnite")]
    [InlineData("https://www.epicgames.com/product/fortnite", "com.epicgames.launcher://store/p/fortnite")]
    [InlineData("https://www.gog.com/game/witcher_3", "goggalaxy://openStoreUrl/www.gog.com/game/witcher_3")]
    public void StoreLinks_AppUri_builds_the_launchers_own_link(string page, string expected)
    {
        Assert.Equal(expected, StoreLinks.AppUri(new Uri(page), AllInstalled));
        // The launcher is not installed: the page opens in the browser instead.
        Assert.Null(StoreLinks.AppUri(new Uri(page), NoneInstalled));
    }

    [Theory]
    [InlineData("https://evil.com/app/440/")]
    [InlineData("https://store.steampowered.com.evil.com/app/440/")]
    [InlineData("https://store.steampowered.com/app/notanumber/")]
    [InlineData("https://store.steampowered.com/app/440x/")]
    [InlineData("https://store.steampowered.com/app/")]
    [InlineData("https://epicgames.com.evil.com/p/fortnite")]
    [InlineData("https://notgog.com/game/witcher_3")]
    [InlineData("https://www.ubisoft.com/en-us/game/assassins-creed")]
    public void StoreLinks_AppUri_null_for_untrusted_or_nonnumeric(string page) =>
        Assert.Null(StoreLinks.AppUri(new Uri(page), AllInstalled));

    [Fact]
    public void StoreLinks_AppUri_keeps_an_odd_epic_slug_in_its_one_segment()
    {
        var app = StoreLinks.AppUri(new Uri("https://store.epicgames.com/p/..%2f..%2f"), AllInstalled);
        Assert.NotNull(app);
        var slug = app!["com.epicgames.launcher://store/p/".Length..];
        Assert.DoesNotContain("/", slug);
        Assert.DoesNotContain("..%2f", slug, StringComparison.OrdinalIgnoreCase);
    }
}
