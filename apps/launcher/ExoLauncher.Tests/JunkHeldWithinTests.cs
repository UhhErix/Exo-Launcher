using System.Diagnostics;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The "in use" holder lookup after a junk clean. Ways it fails: it runs on the UI thread and freezes the page while
/// Restart Manager is slow; it waits past its cap, so the clean never answers; a quick lookup loses its names.
/// </summary>
public sealed class JunkHeldWithinTests
{
    [Fact]
    public async Task AQuickAnswerComesBack()
    {
        Assert.Equal("Those files are in use by Foo.", await WebHostBridge.HeldWithinAsync(() => "Those files are in use by Foo.", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ASlowLookupIsCutOffAtItsCap()
    {
        var clock = Stopwatch.StartNew();
        var answer = await WebHostBridge.HeldWithinAsync(() => { Thread.Sleep(3000); return "late"; }, TimeSpan.FromMilliseconds(150));
        Assert.Null(answer);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"waited {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void TheCapIsTwoSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2), WebHostBridge.JunkHeldCap);
}
