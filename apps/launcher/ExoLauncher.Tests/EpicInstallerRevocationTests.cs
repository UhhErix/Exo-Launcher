using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Opening a downloaded Epic installer checks its revocation again, as the download did. Ways this could fail: a download whose
/// revocation could not be checked is run from the stored result (the file may have changed or the CA may be unreachable now).
/// Fixture file is unsigned bytes, so its revocation cannot be checked.
/// </summary>
public sealed class EpicInstallerRevocationTests
{
    [Fact]
    public void AnInstallerWhoseRevocationCannotBeCheckedIsNotRunAndIsDeleted()
    {
        var setup = Path.Combine(Path.GetTempPath(), "exo-epic-revoke-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(setup, [0x4D, 0x5A]);
        var started = false;
        var download = new EpicNewLauncherInstaller.Download(setup, null, new Uri("https://launcher-public-service-prod06.ol.epicgames.com/"));

        var message = EpicNewLauncherInstaller.Open(download, _ => { started = true; return null; });

        Assert.Equal("Couldn't verify Epic's installer right now. Try again.", message);
        Assert.False(started);
        Assert.False(File.Exists(setup));
    }
}
