using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// No test may write the user's real %LOCALAPPDATA%\ExoLauncher. Ways this could fail: a test run starts without the data
/// folder variable and PathHelper falls back to the real folder; a test restores the variable to the real folder instead of
/// the one the run set; the run's folder is not under the temp folder. The first test proves the run's app data is the temp
/// folder; the second proves the real folder is never the answer, whatever the variable says before the run.
/// </summary>
public sealed class TestDataIsolationTests
{
    private static string RealAppData =>
        Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher"));

    [Fact]
    public void TheRunsAppDataIsATempFolderNotTheUsersRealOne()
    {
        var data = Path.GetFullPath(PathHelper.AppDataDir);

        Assert.Equal(Path.GetFullPath(TestDataDirectory.Root), data);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), data, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(RealAppData, data);
    }

    [Fact]
    public void TheVariableNamesTheRunsTempFolderWhileTheRunsTestsAreRunning()
    {
        var variable = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

        Assert.False(string.IsNullOrWhiteSpace(variable));
        Assert.NotEqual(RealAppData, Path.GetFullPath(variable!));
    }
}
