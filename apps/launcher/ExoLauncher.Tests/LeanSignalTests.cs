using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Streaming software found by its executable, with or without ".exe" and in any case; a prefix entry covers a
/// product's renamed builds; a name that only contains one (an OBS plug-in host, a clip recorder) does not count.
/// </summary>
public sealed class LeanSignalTests
{
    [Theory]
    [InlineData("obs64.exe")]
    [InlineData("OBS64.EXE")]
    [InlineData("obs32")]
    [InlineData("Streamlabs OBS.exe")]
    [InlineData("Streamlabs Desktop.exe")]
    [InlineData("XSplit.Core.exe")]
    [InlineData("PRISMLiveStudio.exe")]
    [InlineData("vMix64.exe")]
    public void StreamingSoftwareCounts(string name) =>
        Assert.True(LeanSignal.IsStreamingApp(["explorer.exe", name]));

    [Theory]
    [InlineData("obs-browser-page.exe")]
    [InlineData("medal.exe")]
    [InlineData("ExoCapture.exe")]
    [InlineData("nvcontainer.exe")]
    [InlineData("jobs64.exe")]
    public void OtherProcessesDoNot(string name) =>
        Assert.False(LeanSignal.IsStreamingApp(["explorer.exe", name]));
}
