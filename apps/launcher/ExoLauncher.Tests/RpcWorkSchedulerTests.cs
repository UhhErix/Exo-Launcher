using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class RpcWorkSchedulerTests
{
    [Fact]
    public async Task SlowHandlerFloodDoesNotStarveFastCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = new RpcWorkScheduler(maxTotalHeavy: 6, maxPerLane: 2);
        var releaseSlow = new ManualResetEventSlim(false);
        var slowStarted = new CountdownEvent(6);
        string[] heavyMethods =
        [
            "devices.list",
            "gpu.profile",
            "system.drives",
            "apps.list",
            "startup.list",
            "shaders.prepare.games",
        ];

        var slowTasks = new List<Task<int>>();
        for (var i = 0; i < 30; i++)
        {
            var method = heavyMethods[i % heavyMethods.Length];
            slowTasks.Add(scheduler.RunAsync(method, () =>
            {
                if (slowStarted.CurrentCount > 0)
                {
                    try { slowStarted.Signal(); } catch (InvalidOperationException) { }
                }
                releaseSlow.Wait(TimeSpan.FromSeconds(10), ct);
                return 1;
            }, ct));
        }

        Assert.True(slowStarted.Wait(TimeSpan.FromSeconds(5), ct));

        var fastWatch = Stopwatch.StartNew();
        string[] fastMethods =
        [
            "shell.heartbeat",
            "settings.get",
            "app.log",
            "telemetry.track",
            "keep.status",
            "store.home",
            "store.wishlist",
            "discord.friends",
        ];

        var fastTasks = fastMethods.Select(m => scheduler.RunAsync(m, () => m, ct)).ToArray();
        var fastResults = await Task.WhenAll(fastTasks).WaitAsync(TimeSpan.FromSeconds(2), ct);
        fastWatch.Stop();

        Assert.Equal(fastMethods, fastResults);
        Assert.True(fastWatch.Elapsed < TimeSpan.FromSeconds(2), $"Fast calls took {fastWatch.ElapsedMilliseconds} ms");
        Assert.True(scheduler.ActiveHeavyCount <= 6);

        releaseSlow.Set();
        await Task.WhenAll(slowTasks).WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task JammedGpuLaneDoesNotBlockDevicesOrAppsLanes()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = new RpcWorkScheduler(maxTotalHeavy: 6, maxPerLane: 2);
        var releaseGpu = new ManualResetEventSlim(false);
        var gpuFilled = new CountdownEvent(2);

        var gpuTasks = Enumerable.Range(0, 8).Select(_ => scheduler.RunAsync("gpu.profile", () =>
        {
            if (gpuFilled.CurrentCount > 0)
            {
                try { gpuFilled.Signal(); } catch (InvalidOperationException) { }
            }
            releaseGpu.Wait(TimeSpan.FromSeconds(10), ct);
            return "gpu";
        }, ct)).ToArray();

        Assert.True(gpuFilled.Wait(TimeSpan.FromSeconds(5), ct));

        var devicesTask = scheduler.RunAsync("devices.list", () => "devices-ok", ct);
        var appsTask = scheduler.RunAsync("apps.list", () => "apps-ok", ct);

        Assert.Equal("devices-ok", await devicesTask.WaitAsync(TimeSpan.FromSeconds(2), ct));
        Assert.Equal("apps-ok", await appsTask.WaitAsync(TimeSpan.FromSeconds(2), ct));

        releaseGpu.Set();
        await Task.WhenAll(gpuTasks).WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task QueuedHeavyCallCancelsWithoutRunningHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = new RpcWorkScheduler(maxTotalHeavy: 2, maxPerLane: 1);
        var releaseFirst = new ManualResetEventSlim(false);
        var firstEntered = new ManualResetEventSlim(false);
        var secondRan = 0;

        var first = scheduler.RunAsync("devices.list", () =>
        {
            firstEntered.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(10), ct);
            return "first";
        }, ct);

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5), ct));

        using var cts = new CancellationTokenSource();
        var second = scheduler.RunAsync("devices.list", () =>
        {
            Interlocked.Increment(ref secondRan);
            return "second";
        }, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(2), ct));

        releaseFirst.Set();
        Assert.Equal("first", await first.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(0, Volatile.Read(ref secondRan));
    }

    [Fact]
    public async Task CoalescedReadsRunOnceAndDoNotCancelOtherWaiters()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = new RpcWorkScheduler(maxTotalHeavy: 4, maxPerLane: 2);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;

        Task<string> Call(CancellationToken token) => scheduler.RunAsync("devices.list", async _ =>
        {
            Interlocked.Increment(ref invocations);
            return await gate.Task.ConfigureAwait(false);
        }, token, coalesceKey: "devices.list");

        using var cancelledCts = new CancellationTokenSource();
        var liveCalls = Enumerable.Range(0, 6).Select(_ => Call(ct)).ToArray();
        var cancelledCall = Call(cancelledCts.Token);

        cancelledCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCall.WaitAsync(TimeSpan.FromSeconds(2), ct));

        gate.SetResult("snapshot-1");
        var results = await Task.WhenAll(liveCalls).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.All(results, value => Assert.Equal("snapshot-1", value));

        var fresh = await scheduler.RunAsync("devices.list", () =>
        {
            Interlocked.Increment(ref invocations);
            return "snapshot-2";
        }, ct, coalesceKey: "devices.list").WaitAsync(TimeSpan.FromSeconds(2), ct);

        Assert.Equal("snapshot-2", fresh);
        Assert.Equal(2, Volatile.Read(ref invocations));
    }

    [Fact]
    public void CrashReportSubmitReturnsWithoutBlockingCaller()
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            CrashReport.Submit("UiError", $"RpcWorkSchedulerTests burst {i}", "ui");
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), $"Submit burst took {watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("gpu.profile", RpcLane.Gpu)]
    [InlineData("gpu.display", RpcLane.Gpu)]
    [InlineData("devices.list", RpcLane.Devices)]
    [InlineData("hid.list", RpcLane.Devices)]
    [InlineData("system.drives", RpcLane.System)]
    [InlineData("exoAi.state", RpcLane.System)]
    [InlineData("autopilot.state", RpcLane.System)]
    [InlineData("apps.list", RpcLane.Apps)]
    [InlineData("startup.list", RpcLane.Apps)]
    [InlineData("runtimes.list", RpcLane.Apps)]
    [InlineData("library.get", RpcLane.Library)]
    [InlineData("stores.matrix", RpcLane.Library)]
    [InlineData("shaders.prepare.games", RpcLane.Shaders)]
    [InlineData("dlss.status", RpcLane.Shaders)]
    [InlineData("shell.heartbeat", RpcLane.Fast)]
    [InlineData("store.home", RpcLane.Fast)]
    [InlineData("store.wishlist", RpcLane.Fast)]
    [InlineData("app.log", RpcLane.Fast)]
    [InlineData("keep.status", RpcLane.Fast)]
    public void LaneClassificationMatchesRpcFamilies(string method, RpcLane expected) =>
        Assert.Equal(expected, RpcWorkScheduler.LaneFor(method));
}
