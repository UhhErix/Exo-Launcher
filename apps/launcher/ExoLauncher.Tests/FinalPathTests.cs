using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The final path Windows opens, without the device prefix. Ways this could fail: the prefix is not stripped (the kept-folder
/// check then compares a prefixed path with a plain one and never matches); a drive root comes back with its prefix. Nothing is written.
/// </summary>
public sealed class FinalPathTests
{
    [Fact]
    public void ADriveRootOpensWithoutThePrefix() =>
        Assert.Equal(@"C:\", FinalPath.Of(@"C:\"));

    [Fact]
    public void AnExistingFolderOpensAsItsPlainPath()
    {
        var folder = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

        var final = FinalPath.Of(folder);

        Assert.NotNull(final);
        Assert.False(final!.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.Equal(folder, final.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
    }
}
