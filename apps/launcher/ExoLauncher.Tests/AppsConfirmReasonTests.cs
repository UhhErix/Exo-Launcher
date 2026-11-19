using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// apps.confirm used to drop an app with no word: the page said "None of those can be uninstalled here" and wrote nothing to
/// the log. Each app now gets its reason, and each check matches what the removal itself would refuse, so a confirm never
/// says an app can go that the removal would then turn away.
/// </summary>
public sealed class AppsConfirmReasonTests
{
    private static InstalledProgram Desktop(string name) =>
        new("HKLM:{x}", name, null, null, null, null, null, null, Store: false, Machine: true, "MsiExec.exe /X{x}", null, null, true, null, null, null);

    private static InstalledProgram Package(string family) =>
        new("store:" + family, family, null, null, null, null, null, null, Store: true, Machine: false, null, null, family, true, null, null, null);

    private static readonly AppsKeep.Needs Nothing = new(new Dictionary<StoreKind, int>(), new Dictionary<string, string>(), Eq: false);

    [Fact]
    public void AnAppWithNoUninstallerCanOnlyBeClearedNotUninstalled()
    {
        var gone = Desktop("Old Tool") with { Uninstall = @"C:\Nope\Missing\uninstall.exe" };

        Assert.Equal("Its uninstaller is gone: clear what it left instead.", WebHostBridge.ConfirmReason(gone, Nothing));
    }

    [Fact]
    public void AnAppWhoseUninstallerIsGoneWithNoCommandAtAllSaysTheSameThing()
    {
        Assert.Equal("Its uninstaller is gone: clear what it left instead.", WebHostBridge.ConfirmReason(Desktop("Old Tool") with { Uninstall = null }, Nothing));
    }

    [Fact]
    public void AnAppWhatGamesNeedStaysWithTheReasonNamed()
    {
        var needs = new AppsKeep.Needs(new Dictionary<StoreKind, int> { [StoreKind.Steam] = 3 }, new Dictionary<string, string>(), Eq: false);

        Assert.Equal("Your 3 Steam games need it: it stays.", WebHostBridge.ConfirmReason(Desktop("Steam"), needs));
    }

    [Fact]
    public void AReplacedAppIsRemovedInCleanUpNotByAnUninstall()
    {
        Assert.Equal("Exo replaces it: Clean up removes it, not an uninstall.", WebHostBridge.ConfirmReason(Desktop("Some Replaced App") with { Replaces = "Replaced" }, Nothing));
    }

    [Fact]
    public void ANotRemovableAppKeepsTheWordsItAlwaysHad()
    {
        var exo = Desktop("Exo Launcher") with { Removable = false, Care = InstalledPrograms.Cares.Exo };

        Assert.Equal("To remove Exo, use Windows' Installed apps.", WebHostBridge.ConfirmReason(exo, Nothing));
    }

    [Fact]
    public void AnAppNamedInTheLogIsScrubbedFromCrashReports()
    {
        try
        {
            WebHostBridge.NoteProgramName(Desktop("Acme Backup Pro"));

            var scrubbed = CrashReport.Scrub("Uninstall failed in Acme Backup Pro", "Erix", @"C:\Users\Erix", "DESKTOP-K3J9");

            Assert.DoesNotContain("Acme Backup Pro", scrubbed);
            Assert.Contains("<name>", scrubbed);
        }
        finally
        {
            ReportNames.Clear();
        }
    }

    [Fact]
    public void AnUninstallableDesktopAppAndAStoreAppCanGo()
    {
        Assert.Null(WebHostBridge.ConfirmReason(Desktop("Discord"), Nothing));
        Assert.Null(WebHostBridge.ConfirmReason(Package("SpotifyAB.SpotifyMusic_zpdnekdrzrea0"), Nothing));
    }
}
