using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class ElevatedScriptsTests
{
    [Fact]
    public void TheWindowsUpdateInstallKeepsWhatEachFailureSaid()
    {
        var script = WindowsUpdateDrivers.InstallScript(["windows:a"], @"C:\k", @"C:\r.json", @"C:\s.json");
        Assert.Contains("$ok = $false; $err = $_.Exception.Message", script);
        Assert.Contains("RolledBack = $back; Error = $err", script);
    }

    [Fact]
    public void TheWindowsUpdateResultReadsAFailureAsNotInstalledWithItsDriverUnchanged()
    {
        var item = new DriverItem("windows:a", "driver", "Chipset", null, null, null, null, null, default, null, false);
        var json = """{"Reboot":false,"Results":[{"Id":"a","Ok":false,"RolledBack":false,"Error":"Access is denied"}]}""";
        var result = Assert.Single(WindowsUpdateDrivers.Results([item], json));
        Assert.False(result.Result.Ok);
        Assert.Equal("Didn't install. Your driver is unchanged.", result.Result.Message);
    }
}
