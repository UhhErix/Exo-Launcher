using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Epic's installer is trusted twice: when it is downloaded (its address and signature) and again before Exo runs it, since the
/// file could change in between. Ways this could fail: an installer that is no longer trusted at launch is started anyway; the
/// refused file is left in the temp folder. Fixture file is unsigned bytes.
/// </summary>
public sealed class EpicInstallerTrustTests
{
    [Fact]
    public void AnInstallerNoLongerTrustedIsNotStartedAndIsDeleted()
    {
        var setup = Path.Combine(Path.GetTempPath(), "exo-epic-trust-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(setup, [0x4D, 0x5A]);
        var started = false;

        var message = EpicNewLauncherInstaller.Open(setup, () => false, _ => { started = true; return null; });

        Assert.Equal("That download wasn't signed by Epic Games, so Exo deleted it.", message);
        Assert.False(started);
        Assert.False(File.Exists(setup));
    }
}
