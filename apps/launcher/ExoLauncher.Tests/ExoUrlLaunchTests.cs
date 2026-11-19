using ExoLauncher.Adapters;
using ExoLauncher.Ui;

namespace ExoLauncher.Tests;

/// <summary>
/// What a page or link may open outside Exo: the web, Steam's own harmless forms, Microsoft Store home/pdp/search and Windows
/// Update's Settings page. A steam link that runs a game with launch arguments, malformed Store home URLs, or anything else stays shut.
/// </summary>
public sealed class ExoUrlLaunchTests
{
    private sealed class FakeUrlLaunchOps(bool installed, Action<string, string?> onStart) : ExoUrlLaunch.IUrlLaunchOps
    {
        public void StartShell(string fileName, string? arguments = null) => onStart(fileName, arguments);
        public bool IsMicrosoftStoreInstalled() => installed;
    }

    [Theory]
    [InlineData("https://store.steampowered.com/app/730")]
    [InlineData("https://tracker.gg/valorant/profile/riot/Player%23EUW/overview")]
    [InlineData("steam://store/730")]
    [InlineData("steam://nav/games/details/730")]
    [InlineData("steam://open/downloads")]
    [InlineData("steam://openurl/https://store.steampowered.com/account/registerkey?key=AAAAA")]
    [InlineData("steam://install/730")]
    [InlineData("steam://rungameid/730")]
    [InlineData("ms-windows-store://home")]
    [InlineData("ms-windows-store://home/")]
    [InlineData("ms-windows-store://pdp/?productid=9NBLGGH4R32N")]
    [InlineData("ms-windows-store://search/?query=halo")]
    [InlineData("ms-settings:windowsupdate")]
    public void OpensTheseAddresses(string url) => Assert.True(ExoUrlLaunch.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("steam://run/730//+exec%20evil.cfg")]
    [InlineData("steam://rungameid/730//-console")]
    [InlineData("steam://openurl/file:///C:/Windows/System32/cmd.exe")]
    [InlineData("steam://exit")]
    [InlineData("steam://openurl")]
    [InlineData("steam://openurl/")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-settings:windowsupdate-action")]
    [InlineData("ms-settings:windowsupdate?x=1")]
    [InlineData("ms-windows-store://review/?productid=1")]
    [InlineData("ms-windows-store://home?q=1")]
    [InlineData("ms-windows-store://home/extra")]
    [InlineData("ms-windows-store://home#frag")]
    public void RefusesTheseAddresses(string url) => Assert.False(ExoUrlLaunch.IsAllowed(new Uri(url)));

    [Fact]
    public void Storefront_XboxDestination_falls_back_to_store_home_and_normalizes_without_trailing_slash()
    {
        Assert.Equal("ms-windows-store://home", Storefront.XboxDestination("", ""));
        Assert.Equal("ms-windows-store://home", ExoUrlLaunch.LaunchUri(new Uri("ms-windows-store://home/")));
    }

    [Fact]
    public void OpenStoreUri_reports_missing_package_when_store_is_not_installed()
    {
        var launched = new List<(string File, string? Args)>();
        var ops = new FakeUrlLaunchOps(installed: false, (f, a) =>
        {
            launched.Add((f, a));
            throw new System.ComponentModel.Win32Exception("No application associated");
        });

        var result = ExoUrlLaunch.OpenStoreUri(new Uri("ms-windows-store://home"), ops);

        Assert.False(result.Ok);
        Assert.Equal("Microsoft Store is not installed on this PC.", result.Message);
        Assert.Single(launched);
        Assert.Equal(("ms-windows-store://home", null), launched[0]);
    }

    [Fact]
    public void OpenStoreUri_falls_back_to_explorer_AppsFolder_when_store_package_is_installed()
    {
        var launched = new List<(string File, string? Args)>();
        var ops = new FakeUrlLaunchOps(installed: true, (f, a) =>
        {
            launched.Add((f, a));
            if (launched.Count == 1)
                throw new System.ComponentModel.Win32Exception("Protocol failed");
        });

        var result = ExoUrlLaunch.OpenStoreUri(new Uri("ms-windows-store://home/"), ops);

        Assert.True(result.Ok);
        Assert.Equal(2, launched.Count);
        Assert.Equal(("ms-windows-store://home", null), launched[0]);
        Assert.Equal(("explorer.exe", ExoUrlLaunch.MicrosoftStoreAppsFolderArg), launched[1]);
    }

    [Fact]
    public void OpenStoreUri_reports_failure_when_store_is_installed_and_apps_folder_fallback_fails()
    {
        var launched = new List<(string File, string? Args)>();
        var ops = new FakeUrlLaunchOps(installed: true, (f, a) =>
        {
            launched.Add((f, a));
            throw new System.ComponentModel.Win32Exception("Launch failed");
        });

        var result = ExoUrlLaunch.OpenStoreUri(new Uri("ms-windows-store://home"), ops);

        Assert.False(result.Ok);
        Assert.Equal("Windows couldn’t open Microsoft Store.", result.Message);
        Assert.Equal(2, launched.Count);
        Assert.Equal(("ms-windows-store://home", null), launched[0]);
        Assert.Equal(("explorer.exe", ExoUrlLaunch.MicrosoftStoreAppsFolderArg), launched[1]);
    }
}
