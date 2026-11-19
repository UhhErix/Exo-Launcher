using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>The app's own check of the Windows build: the same line Exo Setup draws (setup/src/main.cpp).</summary>
public sealed class WindowsRequirementTests
{
    [Theory]
    [InlineData(19045, false)] // Windows 10 22H2, the last Windows 10
    [InlineData(21999, false)]
    [InlineData(22000, true)]  // Windows 11 21H2
    [InlineData(26100, true)]  // Windows 11 24H2
    [InlineData(0, false)]
    public void Only_Windows_11_builds_are_supported(int build, bool supported) =>
        Assert.Equal(supported, WindowsRequirement.Supports(build));

    [Fact]
    public void The_minimum_is_Windows_11_21H2() => Assert.Equal(22000, WindowsRequirement.MinimumBuild);
}
