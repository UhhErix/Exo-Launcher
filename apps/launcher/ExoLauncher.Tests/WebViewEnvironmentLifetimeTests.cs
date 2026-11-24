using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Tests;

public sealed class WebViewEnvironmentLifetimeTests
{
    private sealed class FakeEnvironment(int id)
    {
        public int Id { get; } = id;
    }

    [Fact]
    public async Task SingleAcquireAndDisposeReleasesEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new WebViewEnvironmentRefPool<FakeEnvironment>();
        var created = 0;
        var released = new List<int>();

        var lease = await pool.AcquireAsync(
            _ => Task.FromResult(new FakeEnvironment(++created)),
            env => { released.Add(env.Id); return null; },
            ct);

        Assert.Equal(1, pool.ActiveCount);
        Assert.Same(lease.Environment, pool.TryGetCurrent());
        Assert.Empty(released);

        lease.Dispose();

        Assert.Equal(0, pool.ActiveCount);
        Assert.Null(pool.TryGetCurrent());
        Assert.Equal([1], released);
    }

    [Fact]
    public async Task SharedConsumersKeepEnvironmentAliveUntilLastLeaseDisposes()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new WebViewEnvironmentRefPool<FakeEnvironment>();
        var created = 0;
        var released = new List<int>();

        var first = await pool.AcquireAsync(
            _ => Task.FromResult(new FakeEnvironment(++created)),
            env => { released.Add(env.Id); return null; },
            ct);
        var second = await pool.AcquireAsync(
            _ => Task.FromResult(new FakeEnvironment(++created)),
            env => { released.Add(env.Id); return null; },
            ct);
        var third = pool.TryAcquire(first.Environment);

        Assert.NotNull(third);
        Assert.Equal(1, created);
        Assert.Same(first.Environment, second.Environment);
        Assert.Same(first.Environment, third.Environment);
        Assert.Equal(3, pool.ActiveCount);

        first.Dispose();
        first.Dispose();
        Assert.Equal(2, pool.ActiveCount);
        Assert.Empty(released);

        second.Dispose();
        Assert.Equal(1, pool.ActiveCount);
        Assert.Empty(released);

        third.Dispose();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Null(pool.TryGetCurrent());
        Assert.Equal([1], released);
    }

    [Fact]
    public async Task ConcurrentAcquiresInvokeFactoryOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new WebViewEnvironmentRefPool<FakeEnvironment>();
        var gate = new TaskCompletionSource<FakeEnvironment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => pool.AcquireAsync(_ =>
            {
                Interlocked.Increment(ref created);
                return gate.Task;
            }, ct: ct))
            .ToArray();

        var env = new FakeEnvironment(42);
        gate.SetResult(env);
        var leases = await Task.WhenAll(tasks);

        Assert.Equal(1, created);
        Assert.Equal(5, pool.ActiveCount);
        Assert.All(leases, lease => Assert.Same(env, lease.Environment));

        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public async Task ThreeOpenCloseCyclesAwaitPreviousExitAndReturnToZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new WebViewEnvironmentRefPool<FakeEnvironment>();
        var created = 0;
        var released = new List<int>();
        var previousExitFinished = 0;

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var exitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lease = await pool.AcquireAsync(
                _ => Task.FromResult(new FakeEnvironment(++created)),
                env =>
                {
                    released.Add(env.Id);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(25);
                        Interlocked.Increment(ref previousExitFinished);
                        exitTcs.TrySetResult();
                    });
                    return exitTcs.Task;
                },
                ct);

            Assert.Equal(cycle, lease.Environment.Id);
            Assert.Equal(cycle - 1, Volatile.Read(ref previousExitFinished));
            Assert.Equal(1, pool.ActiveCount);

            lease.Dispose();
            Assert.Equal(0, pool.ActiveCount);
        }

        Assert.Equal(3, created);
        Assert.Equal([1, 2, 3], released);
    }

    [Fact]
    public async Task ResetDropsFailedInstanceAndIgnoresLateDisposeOfOldLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new WebViewEnvironmentRefPool<FakeEnvironment>();
        var created = 0;
        var released = new List<int>();

        var oldLease = await pool.AcquireAsync(
            _ => Task.FromResult(new FakeEnvironment(++created)),
            env => { released.Add(env.Id); return null; },
            ct);

        pool.Reset(oldLease.Environment);

        Assert.Equal(0, pool.ActiveCount);
        Assert.Null(pool.TryGetCurrent());
        Assert.Equal([1], released);

        var freshLease = await pool.AcquireAsync(
            _ => Task.FromResult(new FakeEnvironment(++created)),
            env => { released.Add(env.Id); return null; },
            ct);

        oldLease.Dispose();

        Assert.Equal(1, pool.ActiveCount);
        Assert.Equal(2, freshLease.Environment.Id);
        Assert.Equal([1], released);

        freshLease.Dispose();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal([1, 2], released);
    }

    [Fact]
    public async Task TryAcquireReturnsNullForUnknownOrReleasedEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new WebViewEnvironmentRefPool<FakeEnvironment>();
        var other = new FakeEnvironment(99);

        Assert.Null(pool.TryAcquire(null));
        Assert.Null(pool.TryAcquire(other));

        var lease = await pool.AcquireAsync(_ => Task.FromResult(new FakeEnvironment(1)), ct: ct);
        Assert.Null(pool.TryAcquire(other));
        lease.Dispose();
        Assert.Null(pool.TryAcquire(new FakeEnvironment(1)));
    }

    [Fact]
    public async Task SharedEnvironmentExitsAllWebViewProcessesWithinFiveSecondsAcrossThreeCycles()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EXO_WEBVIEW_LIVE_TEST") == "1", "EXO_WEBVIEW_LIVE_TEST isn't set");
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "exo-wv-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, tempRoot);

        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = controller.DispatcherQueue;
        try
        {
            await RunOnQueueAsync(queue, () =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                return true;
            });
            var hwnd = await RunOnQueueAsync(queue, () =>
                CreateWindowExW(0, "STATIC", "", 0, 0, 0, 64, 64, new nint(-3), 0, 0, 0));
            Assert.NotEqual(0, hwnd);
            try
            {
                var baselineCount = CountWebViewProcesses(tempRoot);
                Assert.Equal(0, baselineCount);

                await RunOnQueueAsync(queue, async () =>
                {
                    using var warm = await WebViewEnvironmentFactory.AcquireAsync(ct: ct);
                    CoreWebView2Controller? wv = null;
                    Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? onNav = null;
                    try
                    {
                        wv = await warm.Environment.CreateCoreWebView2ControllerAsync(
                            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
                        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        onNav = (_, _) => navigated.TrySetResult();
                        wv.CoreWebView2.NavigationCompleted += onNav;
                        wv.CoreWebView2.NavigateToString("<!doctype html><title>warm</title>");
                        await navigated.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                        await Task.Delay(500, ct);
                    }
                    finally
                    {
                        if (wv is not null && onNav is not null)
                        {
                            try { wv.CoreWebView2.NavigationCompleted -= onNav; } catch { }
                        }
                        WebViewEnvironmentLifetime.CloseAndRelease(wv);
                        wv = null;
                    }
                });
                var warmRemaining = await WaitForWebViewExitAsync(tempRoot, TimeSpan.FromSeconds(15), includeCrashpad: false, ct);
                Assert.True(warmRemaining.Count == 0, $"Warmup left {warmRemaining.Count} process(es): {string.Join(", ", warmRemaining)}");

                var baselineWsMb = CurrentProcessWorkingSetMb();

                for (var cycle = 1; cycle <= 3; cycle++)
                {
                    var (spawned, openWvMb) = await RunOnQueueAsync(queue, async () =>
                    {
                        using var lease = await WebViewEnvironmentFactory.AcquireAsync(ct: ct);
                        CoreWebView2Controller? wv = null;
                        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? onNav = null;
                        try
                        {
                            wv = await lease.Environment.CreateCoreWebView2ControllerAsync(
                                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
                            WebViewEnvironmentLifetime.ApplyLowMemoryTarget(wv.CoreWebView2);
                            var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            onNav = (_, _) => navigated.TrySetResult();
                            wv.CoreWebView2.NavigationCompleted += onNav;
                            wv.CoreWebView2.NavigateToString($"<!doctype html><title>cycle-{cycle}</title>");
                            await navigated.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                            await Task.Delay(500, ct);
                            var procs = CountWebViewProcesses(tempRoot, includeCrashpad: true);
                            var wvMb = SumWebViewWorkingSetMb(tempRoot);
                            wv.IsVisible = false;
                            try { await wv.CoreWebView2.TrySuspendAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { }
                            return (procs, wvMb);
                        }
                        finally
                        {
                            if (wv is not null && onNav is not null)
                            {
                                try { wv.CoreWebView2.NavigationCompleted -= onNav; } catch { }
                            }
                            WebViewEnvironmentLifetime.CloseAndRelease(wv);
                            wv = null;
                        }
                    });

                    var exitStart = Environment.TickCount64;
                    Assert.True(spawned >= 3, $"Cycle {cycle} expected >=3 msedgewebview2 processes, got {spawned}");
                    Assert.Equal(0, WebViewEnvironmentFactory.ActiveLeaseCount);
                    var remaining = await WaitForWebViewExitAsync(tempRoot, TimeSpan.FromSeconds(5), includeCrashpad: false, ct);
                    var exitMs = Environment.TickCount64 - exitStart;
                    Assert.True(remaining.Count == 0, $"Cycle {cycle} left {remaining.Count} process(es): {string.Join(", ", remaining)}");
                    Console.WriteLine($"WEBVIEW_CYCLE={cycle} open_procs={spawned} open_wv_mb={openWvMb:F1} after_procs={remaining.Count} after_wv_mb={SumWebViewWorkingSetMb(tempRoot):F1} exit_ms={exitMs}");
                }

                await RunOnQueueAsync(queue, async () =>
                {
                    var first = await WebViewEnvironmentFactory.AcquireAsync(ct: ct);
                    var second = await WebViewEnvironmentFactory.AcquireAsync(ct: ct);
                    CoreWebView2Controller? c1 = null;
                    CoreWebView2Controller? c2 = null;
                    try
                    {
                        c1 = await first.Environment.CreateCoreWebView2ControllerAsync(
                            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
                        c2 = await second.Environment.CreateCoreWebView2ControllerAsync(
                            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
                        Assert.Equal(2, WebViewEnvironmentFactory.ActiveLeaseCount);
                        await Task.Delay(500, ct);

                        WebViewEnvironmentLifetime.CloseAndRelease(c1);
                        c1 = null;
                        first.Dispose();
                        Assert.Equal(1, WebViewEnvironmentFactory.ActiveLeaseCount);
                        Assert.True(CountWebViewProcesses(tempRoot, includeCrashpad: false) > 0);
                    }
                    finally
                    {
                        WebViewEnvironmentLifetime.CloseAndRelease(c1);
                        WebViewEnvironmentLifetime.CloseAndRelease(c2);
                        c1 = null;
                        c2 = null;
                        first.Dispose();
                        second.Dispose();
                    }
                });

                Assert.Equal(0, WebViewEnvironmentFactory.ActiveLeaseCount);
                var finalRemaining = await WaitForWebViewExitAsync(tempRoot, TimeSpan.FromSeconds(5), includeCrashpad: false, ct);
                Assert.True(finalRemaining.Count == 0, $"Final left {finalRemaining.Count} process(es): {string.Join(", ", finalRemaining)}");
                Assert.Equal(0, CountWebViewProcesses(tempRoot, includeCrashpad: false));
                var afterWsMb = CurrentProcessWorkingSetMb();
                Console.WriteLine($"WEBVIEW_SUMMARY host_baseline_mb={baselineWsMb:F1} host_after_mb={afterWsMb:F1} host_delta_mb={afterWsMb - baselineWsMb:F1} remaining_wv_mb={SumWebViewWorkingSetMb(tempRoot):F1}");
                Assert.True(afterWsMb - baselineWsMb <= 10.0, $"Expected process WS within +10 MB of baseline ({baselineWsMb:F1} MB -> {afterWsMb:F1} MB)");
            }
            finally
            {
                await RunOnQueueAsync(queue, () => DestroyWindow(hwnd));
            }
        }
        finally
        {
            await controller.ShutdownQueueAsync();
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previousDataDir);
            try { DeletionGate.DeleteDirectory(tempRoot); } catch { }
        }
    }

    private static Task<T> RunOnQueueAsync<T>(DispatcherQueue queue, Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("Queue unavailable."));
        }
        return tcs.Task;
    }

    private static Task<T> RunOnQueueAsync<T>(DispatcherQueue queue, Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try { tcs.TrySetResult(await work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("Queue unavailable."));
        }
        return tcs.Task;
    }

    private static Task RunOnQueueAsync(DispatcherQueue queue, Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try { await work(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("Queue unavailable."));
        }
        return tcs.Task;
    }

    private static List<string> ListWebViewProcesses(string folderMarker, bool includeCrashpad)
    {
        var list = new List<string>();
        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            using (process)
            {
                var line = HostedProcessClass.QueryCommandLine(process.Id);
                if (line is not null && line.Contains(folderMarker, StringComparison.OrdinalIgnoreCase))
                {
                    var typeIdx = line.IndexOf("--type=", StringComparison.OrdinalIgnoreCase);
                    var kind = "browser";
                    if (typeIdx >= 0)
                    {
                        var end = line.IndexOf(' ', typeIdx);
                        kind = end > typeIdx ? line[(typeIdx + 7)..end] : line[(typeIdx + 7)..];
                    }
                    if (!includeCrashpad && kind.Equals("crashpad-handler", StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add($"{process.Id}:{kind}");
                }
            }
        }
        return list;
    }

    private static int CountWebViewProcesses(string folderMarker, bool includeCrashpad = true) =>
        ListWebViewProcesses(folderMarker, includeCrashpad).Count;

    private static async Task<List<string>> WaitForWebViewExitAsync(string folderMarker, TimeSpan timeout, bool includeCrashpad, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var list = ListWebViewProcesses(folderMarker, includeCrashpad);
            if (list.Count == 0 || Environment.TickCount64 >= deadline) return list;
            await Task.Delay(150, ct);
        }
    }

    private static double SumWebViewWorkingSetMb(string folderMarker)
    {
        long totalBytes = 0;
        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            using (process)
            {
                try
                {
                    var line = HostedProcessClass.QueryCommandLine(process.Id);
                    if (line is not null && line.Contains(folderMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Refresh();
                        totalBytes += process.WorkingSet64;
                    }
                }
                catch { }
            }
        }
        return totalBytes / (1024.0 * 1024.0);
    }

    private static double CurrentProcessWorkingSetMb()
    {
        using var current = Process.GetCurrentProcess();
        current.Refresh();
        return current.WorkingSet64 / (1024.0 * 1024.0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
}
