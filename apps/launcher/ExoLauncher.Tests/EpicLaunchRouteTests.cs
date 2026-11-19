using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways an Epic launch can show Epic's window (the owner's report: Rocket League opened the Epic Games Launcher):
/// - Epic's 0.113 cold start always reveals its main window, so the launcher handoff shows Epic even when the URI is silent;
/// - Legendary knows the title (its install list, which Epic's own installs join through egl-sync) but the route ignored it
///   whenever Epic had a record of the install, so the handoff was taken;
/// - Legendary must be signed in (its own account), or its launch fails; a title it does not know, or an empty title, never goes to it;
/// - a Legendary launch that exits with an error within 10 s hands the title to the URI once (no wait for a game that won't come).
/// </summary>
public sealed class EpicLaunchRouteTests
{
    [Fact]
    public void AnEpicInstallLegendaryKnowsStartsThroughLegendary()
    {
        // Rocket League (Sugar) is on Epic's 0.113 list and in Legendary's installed.json.
        Assert.True(EpicAdapter.UseLegendaryForLaunch(epicInstalled: true, legendaryKnowsTitle: true, legendarySignedIn: true, appName: "Sugar"));
    }

    [Fact]
    public void AnEpicInstallLegendaryDoesNotKnowKeepsTheLauncherHandoff()
    {
        Assert.False(EpicAdapter.UseLegendaryForLaunch(epicInstalled: true, legendaryKnowsTitle: false, legendarySignedIn: true, appName: "Sugar"));
    }

    [Fact]
    public void WithoutEpicLegendaryLaunchesAnyTitleItKnows()
    {
        Assert.True(EpicAdapter.UseLegendaryForLaunch(epicInstalled: false, legendaryKnowsTitle: true, legendarySignedIn: true, appName: "Sugar"));
    }

    [Fact]
    public void ASignedOutLegendaryKeepsTheUriRoute()
    {
        Assert.False(EpicAdapter.UseLegendaryForLaunch(epicInstalled: true, legendaryKnowsTitle: true, legendarySignedIn: false, appName: "Sugar"));
        Assert.False(EpicAdapter.UseLegendaryForLaunch(epicInstalled: false, legendaryKnowsTitle: true, legendarySignedIn: false, appName: "Sugar"));
    }

    [Fact]
    public void AnEmptyTitleIsNeverSentToLegendary()
    {
        Assert.False(EpicAdapter.UseLegendaryForLaunch(epicInstalled: false, legendaryKnowsTitle: true, legendarySignedIn: true, appName: " "));
        Assert.False(EpicAdapter.UseLegendaryForLaunch(epicInstalled: false, legendaryKnowsTitle: true, legendarySignedIn: true, appName: null));
    }

    [Theory]
    [InlineData("""{"account":"UhhErix","games_installed":3}""", true)]
    [InlineData("""{"account":null,"games_installed":3}""", false)]
    [InlineData("""{"account":"","games_installed":0}""", false)]
    [InlineData("""{"games_installed":0}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void SignedInIsAnAccountNameInLegendaryStatus(string json, bool expected) =>
        Assert.Equal(expected, EpicAdapter.SignedInFromLegendaryStatus(json));

    [Theory]
    [InlineData(1, 4, true)]    // an error within 10 s: the URI, once
    [InlineData(1, 10, true)]   // at the limit still counts
    [InlineData(1, 11, false)]  // later than 10 s: the normal wait for the game
    [InlineData(0, 2, false)]   // a clean exit is a handoff that may still start the game
    public void ALegendaryErrorWithinTenSecondsFallsBackToTheUri(int exitCode, int seconds, bool fallBack) =>
        Assert.Equal(fallBack, EpicAdapter.LegendaryFailedEarly(exitCode, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheLegendaryCommandIsLaunchWithoutAVersionCheckAndNoEpicWindow()
    {
        // The command that starts the game: Legendary's own launch, which never starts Epic's launcher.
        Assert.Equal(["launch", "Sugar", "--skip-version-check"], LegendaryCli.LaunchArgs("Sugar"));
    }
}
