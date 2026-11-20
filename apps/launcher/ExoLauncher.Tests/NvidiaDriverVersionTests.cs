using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

// After an install Exo reads the driver Windows records (32.0.16.1770), because NvAPI in Exo's process still answers
// for the driver it loaded with: the install looked failed although it worked.
public sealed class NvidiaDriverVersionTests
{
    [Theory]
    [InlineData("32.0.16.1770", "617.70")]
    [InlineData("32.0.16.1742", "617.42")]
    [InlineData("32.0.15.8180", "581.80")]
    [InlineData("31.0.15.5212", "552.12")]
    public void Windows_driver_version_reads_as_NVIDIAs(string windows, string nvidia) =>
        Assert.Equal(nvidia, NvidiaDriverService.FromWindowsVersion(windows));

    [Theory]
    [InlineData("")]
    [InlineData("32.0.16")]
    [InlineData("32.0.x.1770")]
    [InlineData("32.0.16.17700")]
    public void Not_a_driver_version_reads_as_nothing(string windows) =>
        Assert.Null(NvidiaDriverService.FromWindowsVersion(windows));

    [Fact]
    public void OsId_SelectsWindows11AndWindows10()
    {
        Assert.Equal(135, NvidiaDriverService.OsId(new Version(10, 0, 22631, 0)));
        Assert.Equal(135, NvidiaDriverService.OsId(new Version(10, 0, 22000, 0)));
        Assert.Equal(57, NvidiaDriverService.OsId(new Version(10, 0, 19045, 0)));
    }

    [Fact]
    public void PickNvidiaSubkeyMatch_PrefersActiveDxgiDeviceId()
    {
        var entries = new (string?, string?, string?)[]
        {
            ("Intel", @"PCI\VEN_8086&DEV_4680", "31.0.101.5590"),
            ("NVIDIA", @"PCI\VEN_10DE&DEV_1C03", "31.0.15.3623"),
            ("NVIDIA", @"PCI\VEN_10DE&DEV_2484", "32.0.16.1742"),
        };
        Assert.Equal("32.0.16.1742", NvidiaDriverService.PickNvidiaSubkeyMatch(entries, 0x2484));
        Assert.Equal("31.0.15.3623", NvidiaDriverService.PickNvidiaSubkeyMatch(entries, null));
    }

    [Fact]
    public void NvidiaVideo_MatchesVideoHdrNonZeroReadback()
    {
        Assert.True(NvidiaVideo.MatchesReadback(NvidiaVideo.VideoHdr, 5, 1));
        Assert.True(NvidiaVideo.MatchesReadback(NvidiaVideo.VideoHdr, 5, 5));
        Assert.False(NvidiaVideo.MatchesReadback(NvidiaVideo.VideoHdr, 5, 0));
        Assert.True(NvidiaVideo.MatchesReadback(NvidiaVideo.VideoHdr, 0, 0));
        Assert.False(NvidiaVideo.MatchesReadback(NvidiaVideo.VideoHdr, 5, null));
        Assert.True(NvidiaVideo.MatchesReadback(NvidiaVideo.SuperResolution, 4, 4));
        Assert.False(NvidiaVideo.MatchesReadback(NvidiaVideo.SuperResolution, 4, 1));
    }
}
