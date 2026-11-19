using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Which paths Exo will look at on this PC: a fully qualified path on a fixed or removable drive. A network share, a device or
/// an extended path would make Exo probe another machine (an NTLM hash leak from a planted backup); a mapped network drive
/// is a network drive too. Nothing is written.
/// </summary>
public sealed class LocalPathTests
{
    [Theory]
    [InlineData("\\\\attacker\\share\\Lucid.exe", false)]
    [InlineData("\\\\?\\UNC\\attacker\\share\\x.exe", false)]
    [InlineData("\\\\.\\C:\\x.exe", false)]
    [InlineData("\\\\?\\C:\\x.exe", false)]
    [InlineData("relative\\Lucid.exe", false)]
    public void ANonLocalPathIsNeverLocal(string path, bool local) =>
        Assert.Equal(local, LocalPath.IsLocalDrivePath(path));

    [Fact]
    public void ASystemDrivePathIsLocal() =>
        Assert.True(LocalPath.IsLocalDrivePath(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Games")));

    [Fact]
    public void ANonLocalFolderIsNeverProbed() =>
        Assert.False(LocalPath.ExistsLocally("\\\\attacker\\share"));
}
