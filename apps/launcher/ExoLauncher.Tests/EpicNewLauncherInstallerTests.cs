using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Epic installer download can be unsafe:
/// - an installer served over plain HTTP, or from a host that only looks like Epic's (epicgames.com.evil.example);
/// - a download that redirects off Epic's hosts is run anyway (the final address is what counts);
/// - an unsigned, or differently signed, file is run because only the address was checked.
/// </summary>
public sealed class EpicNewLauncherInstallerTests
{
    [Theory]
    [InlineData("https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherSetup-x86_64.exe", true)]
    [InlineData("https://content-download-egs.distro.on.epicgames.com/Builds/EGLv2/EpicGamesLauncherSetup.exe", true)]
    [InlineData("https://epicgames.com/setup.exe", true)]
    [InlineData("http://launcher-public-service-prod06.ol.epicgames.com/setup.exe", false)] // not HTTPS
    [InlineData("https://epicgames.com.evil.example/setup.exe", false)] // a lookalike host
    [InlineData("https://evilepicgames.com/setup.exe", false)] // no dot before the name
    [InlineData("https://user@launcher.epicgames.com/setup.exe", false)] // user info in the address
    [InlineData("https://launcher.epicgames.com:8443/setup.exe", false)] // another port
    [InlineData("https://github.com/UhhErix/setup.exe", false)]
    public void OnlyEpicsHttpsAddressesAreFromEpic(string url, bool expected) =>
        Assert.Equal(expected, EpicNewLauncherInstaller.FromEpic(new Uri(url)));

    [Fact]
    public void ANullAddressIsNotFromEpic() => Assert.False(EpicNewLauncherInstaller.FromEpic(null));

    [Fact]
    public void AFileIsTrustedOnlyWhenItsFinalAddressIsEpicsAndEpicSignedIt()
    {
        var epic = new Uri("https://content-download-egs.distro.on.epicgames.com/setup.exe");
        var elsewhere = new Uri("https://cdn.example.net/setup.exe");

        Assert.True(EpicNewLauncherInstaller.Trusted(epic, () => true));
        Assert.False(EpicNewLauncherInstaller.Trusted(epic, () => false));
        Assert.False(EpicNewLauncherInstaller.Trusted(elsewhere, () => true));
        Assert.False(EpicNewLauncherInstaller.Trusted(null, () => true));
    }

    [Fact]
    public void ASignerWithOnlyPartOfThePublisherNameIsRefused()
    {
        // .NET's runtime library is Microsoft-signed, not Epic: an installer must carry Epic's exact publisher name.
        var signed = typeof(object).Assembly.Location;

        Assert.True(AuthenticodeVerifier.IsMicrosoft(signed));
        Assert.False(EpicNewLauncherInstaller.SignedByEpic(signed));
    }

    [Fact]
    public void TheEpicPublisherIsTheOneEpicsInstallerCarries()
    {
        // Read from Epic's signed EpicGamesLauncherSetup.exe (its signer's simple name is "Epic Games Inc.", CN=Epic Games Inc., O=Epic Games Inc.).
        Assert.Equal("Epic Games Inc.", EpicNewLauncherInstaller.EpicPublisher);
    }

    [Fact]
    public void AMissingOrUnsignedFileIsNotEpics()
    {
        Assert.False(EpicNewLauncherInstaller.SignedByEpic(Path.Combine(Path.GetTempPath(), "exo-no-such-" + Guid.NewGuid().ToString("N") + ".exe")));
    }

    [Fact]
    public void ACancelledElevationSaysSoAndDeletesTheInstaller()
    {
        var setup = Path.Combine(Path.GetTempPath(), "exo-epic-cancel-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(setup, [0x4D, 0x5A]);

        var message = EpicNewLauncherInstaller.Open(setup, () => true, _ => throw new System.ComponentModel.Win32Exception(1223));

        Assert.Equal("The installer was cancelled.", message);
        Assert.False(File.Exists(setup));
    }

    [Fact]
    public void AnInstallerThatWontStartIsDeletedToo()
    {
        var setup = Path.Combine(Path.GetTempPath(), "exo-epic-nostart-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(setup, [0x4D, 0x5A]);

        var message = EpicNewLauncherInstaller.Open(setup, () => true, _ => throw new System.ComponentModel.Win32Exception(193));

        Assert.Equal("Epic's installer couldn't open. Try again.", message);
        Assert.False(File.Exists(setup));
    }

    [Fact]
    public void TheSignatureIsNotAskedAboutWhenTheAddressFails()
    {
        // A redirect off Epic is refused before anyone asks whether the file is signed.
        var asked = false;
        Assert.False(EpicNewLauncherInstaller.Trusted(new Uri("https://cdn.example.net/setup.exe"), () => { asked = true; return true; }));
        Assert.False(asked);
    }
}
