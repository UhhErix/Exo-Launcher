using ExoLauncher.Services;
using static ExoLauncher.Services.ShaderPrepareWatch;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a Prepare run could end wrongly, each of which either wastes the user's wait or closes a game mid-compile:
/// - a pause between compile batches, or the driver writing in bursts, reads as finished;
/// - a game loading (busy, no cache yet) reads as nothing to compile;
/// - a game sitting at a menu, or one whose processor use can't be read, never ends;
/// - driver housekeeping (a few KB) counts as a compile;
/// - a compile that never settles runs forever.
/// </summary>
public sealed class ShaderPrepareWatchTests
{
    private const long Mb = 1L << 20;
    private const long Start = 5000 * Mb;

    [Fact]
    public void AGrownCacheThatGoesQuietHasSettled()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(30, Start + 200 * Mb, 0.9));
        Assert.Equal(Verdict.Working, watch.Next(60, Start + 400 * Mb, 0.9));
        Assert.Equal(Verdict.Working, watch.Next(120, Start + 400 * Mb, 0.1));
        Assert.Equal(Verdict.Settled, watch.Next(180, Start + 400 * Mb, 0.1));
    }

    [Fact]
    public void APauseBetweenBatchesIsNotTheEnd()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(30, Start + 100 * Mb, 0.1));
        Assert.Equal(Verdict.Working, watch.Next(140, Start + 100 * Mb, 0.1));
        Assert.Equal(Verdict.Working, watch.Next(145, Start + 300 * Mb, 0.1));
        Assert.Equal(Verdict.Working, watch.Next(250, Start + 300 * Mb, 0.1));
        Assert.Equal(Verdict.Settled, watch.Next(265, Start + 300 * Mb, 0.1));
    }

    [Fact]
    public void ABusyGameWithAQuietCacheIsStillWorking()
    {
        var watch = new ShaderPrepareWatch(Start);
        watch.Next(30, Start + 100 * Mb, 0.8);
        Assert.Equal(Verdict.Working, watch.Next(400, Start + 100 * Mb, 0.8));
        Assert.Equal(Verdict.Settled, watch.Next(520, Start + 100 * Mb, 0.1));
    }

    [Fact]
    public void AGameLoadingForAWhileIsNotNothing()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(200, Start, 0.8));
        Assert.Equal(Verdict.Working, watch.Next(310, Start, 0.8));
    }

    [Fact]
    public void AMenuBusierThanTheFloorStillSettlesAfterACompile()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(60, Start + 500 * Mb, 0.95));
        Assert.Equal(Verdict.Working, watch.Next(120, Start + 500 * Mb, 0.4));
        Assert.Equal(Verdict.Settled, watch.Next(180, Start + 500 * Mb, 0.4));
    }

    [Fact]
    public void AGameAtAMenuCompiledNothing()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(120, Start, 0.05));
        Assert.Equal(Verdict.NothingCompiled, watch.Next(300, Start, 0.05));
    }

    [Fact]
    public void HousekeepingIsNotACompile()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(100, Start + 32 * 1024, 0.05));
        Assert.Equal(Verdict.NothingCompiled, watch.Next(300, Start + 48 * 1024, 0.05));
    }

    [Fact]
    public void AnUnreadableProcessorStillSettles()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.Working, watch.Next(60, Start + 50 * Mb, null));
        Assert.Equal(Verdict.Settled, watch.Next(180, Start + 50 * Mb, null));
    }

    [Theory]
    [InlineData("ready", null, true)]
    [InlineData("ready", 0, true)]
    [InlineData("ready", 90, false)]
    [InlineData("stale", 90, true)]
    [InlineData("prepared", 0, false)]
    [InlineData("none", 0, false)]
    [InlineData("failed", 0, false)]
    [InlineData("running", 0, false)]
    public void AutomaticRunsOnlyWhatItCanHelp(string state, int? playtime, bool runs) =>
        Assert.Equal(runs, ShaderPrepare.WantsAuto(state, playtime));

    [Fact]
    public void ACompileThatNeverSettlesEnds()
    {
        var watch = new ShaderPrepareWatch(Start);
        Assert.Equal(Verdict.TooLong, watch.Next(MaxSeconds, Start + 900 * Mb, 0.9));
    }
}
