using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo's help link in Windows' Installed apps entry: Exo's own help page (getexo.app/help), not a community server. Setup
/// writes the same value (tools/ExoLauncher.nsi), so the two can't drift apart.
/// </summary>
public sealed partial class InstalledAppsEntryTests
{
    [Fact]
    public void HelpLinkIsExoHelpPage()
    {
        Assert.Equal("https://getexo.app/help", InstalledAppsEntry.Help);
    }

    [Fact]
    public void SetupWritesTheSameHelpLink()
    {
        var nsi = File.ReadAllText(Repo.PathOf("tools", "ExoLauncher.nsi"));
        var match = HelpDefine().Match(nsi);
        Assert.True(match.Success, "tools/ExoLauncher.nsi has no PRODUCT_HELP define");
        Assert.Equal(InstalledAppsEntry.Help, match.Groups[1].Value);
    }

    // The script is CRLF, so the line may end in \r before \n.
    [GeneratedRegex("""^!define PRODUCT_HELP "([^"]+)"\r?$""", RegexOptions.Multiline)]
    private static partial Regex HelpDefine();
}
