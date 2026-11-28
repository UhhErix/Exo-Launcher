using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

public sealed class VersionLabelTests
{
    [Theory]
    [InlineData("4.0.0", "0.1 beta")]
    [InlineData("v4.0.0+16da8f0b", "0.1 beta")]
    [InlineData("4.0.2", "0.1.2 beta")]
    [InlineData("4.1.0-dev", "0.2 beta")]
    [InlineData("3.31.4", "3.31.4")]
    [InlineData("5.0.0", "5.0.0")]
    [InlineData("", "")]
    public void The_4_line_is_named_the_beta_and_other_versions_are_left_alone(string version, string label) =>
        Assert.Equal(label, VersionLabel.Of(version));
}
