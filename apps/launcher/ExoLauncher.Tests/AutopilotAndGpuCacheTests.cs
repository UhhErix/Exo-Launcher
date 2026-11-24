using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AutopilotAndGpuCacheTests : IDisposable
{
    private const string FailureModes =
        "Ways this code could fail: " +
        "(1) TtlReadCache serves stale entries past TTL or fails to dedup concurrent in-flight reads; " +
        "(2) an in-flight read finishing after Invalidate() or BeginMutation() overwrites the cache with pre-mutation data (ABA race); " +
        "(3) CompleteMutation caches a result even though another invalidation occurred mid-mutation; " +
        "(4) a display mode, HDR, refresh, or hot-plug fingerprint change fails to invalidate a cached entry or allows a torn mid-read result into cache; " +
        "(5) transient errors or !Ok states get cached; " +
        "(6) cancelling one caller cancels the shared in-flight read for another caller; " +
        "(7) ReadAllDdcAsync runs monitors sequentially instead of in parallel or scrambles monitor order; " +
        "(8) SystemTuning.ReadMemoryIntegrityOnly touches ScheduledTasks, Service, or GPU class keys, or disagrees with Read(); " +
        "(9) AutopilotReports cache serves stale reports after Add/delete/external rewrite or leaks mutable list state; " +
        "(10) AutopilotWatch.LiveChanged misses ticks, spams while alt-tabbed, misses session end, or breaks on a throwing listener.";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-cache-tests-" + Guid.NewGuid().ToString("N"));

    public AutopilotAndGpuCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AutopilotReports.InvalidateCache();
        try { if (Directory.Exists(_dir)) DeletionGate.DeleteDirectory(_dir); } catch { }
    }

    [Fact]
    public async Task TtlReadCache_caches_within_ttl_expires_after_ttl_and_honors_force()
    {
        Assert.NotEmpty(FailureModes);
        var ct = TestContext.Current.CancellationToken;
        long now = 1_000;
        var reads = 0;
        var cache = new TtlReadCache<string>(
            TimeSpan.FromSeconds(15),
            () => Task.FromResult($"v{++reads}"),
            clockMs: () => now);

        Assert.Equal("v1", await cache.GetAsync(ct: ct));
        now += 14_999;
        Assert.Equal("v1", await cache.GetAsync(ct: ct));
        Assert.Equal(1, reads);

        Assert.Equal("v2", await cache.GetAsync(force: true, ct: ct));
        Assert.Equal(2, reads);

        now += 15_000;
        Assert.Equal("v3", await cache.GetAsync(ct: ct));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task TtlReadCache_deduplicates_concurrent_reads_and_isolates_caller_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var cache = new TtlReadCache<string>(
            TimeSpan.FromSeconds(30),
            () =>
            {
                Interlocked.Increment(ref reads);
                started.TrySetResult();
                return gate.Task;
            });

        using var cts = new CancellationTokenSource();
        var waiterA = cache.GetAsync(ct: cts.Token);
        await started.Task;
        var waiterB = cache.GetAsync(ct: ct);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterA);

        gate.SetResult("shared-ok");
        Assert.Equal("shared-ok", await waiterB);
        Assert.Equal(1, reads);
        Assert.Equal("shared-ok", await cache.GetAsync(ct: ct));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task TtlReadCache_rejects_stale_in_flight_result_after_invalidation_and_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var cache = new TtlReadCache<string>(
            TimeSpan.FromSeconds(30),
            () =>
            {
                var n = Interlocked.Increment(ref reads);
                if (n == 1)
                {
                    started.TrySetResult();
                    return firstRead.Task;
                }
                return Task.FromResult($"fresh-{n}");
            });

        var pending = cache.GetAsync(ct: ct);
        await started.Task;

        var mutationGen = cache.BeginMutation();
        firstRead.SetResult("stale-before-mutation");
        Assert.Equal("stale-before-mutation", await pending);

        cache.Invalidate();
        cache.CompleteMutation(mutationGen, "superseded-mutation");

        Assert.Equal("fresh-2", await cache.GetAsync(ct: ct));
        Assert.Equal(2, reads);

        var gen2 = cache.BeginMutation();
        cache.CompleteMutation(gen2, "applied-value");
        Assert.Equal("applied-value", await cache.GetAsync(ct: ct));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task TtlReadCache_invalidates_on_fingerprint_change_and_rejects_torn_mid_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var fp = "disp-a";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var cache = new TtlReadCache<string>(
            TimeSpan.FromSeconds(30),
            () =>
            {
                var n = Interlocked.Increment(ref reads);
                if (n == 1)
                {
                    started.TrySetResult();
                    return firstRead.Task;
                }
                return Task.FromResult($"{fp}-v{n}");
            },
            fingerprint: () => fp);

        var torn = cache.GetAsync(ct: ct);
        await started.Task;
        fp = "disp-b";
        firstRead.SetResult("torn-value");
        Assert.Equal("torn-value", await torn);

        Assert.Equal("disp-b-v2", await cache.GetAsync(ct: ct));
        Assert.Equal(2, reads);
        Assert.Equal("disp-b-v2", await cache.GetAsync(ct: ct));
        Assert.Equal(2, reads);

        fp = "disp-c";
        Assert.Equal("disp-c-v3", await cache.GetAsync(ct: ct));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task TtlReadCache_never_caches_unhealthy_or_faulted_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var step = 0;
        var cache = new TtlReadCache<string>(
            TimeSpan.FromSeconds(30),
            () =>
            {
                step++;
                if (step == 1) return Task.FromException<string>(new InvalidOperationException("driver busy"));
                if (step == 2) return Task.FromResult("ERR");
                return Task.FromResult("OK");
            },
            shouldCache: v => v == "OK");

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(ct: ct));
        Assert.Equal("ERR", await cache.GetAsync(ct: ct));
        Assert.Equal("OK", await cache.GetAsync(ct: ct));
        Assert.Equal("OK", await cache.GetAsync(ct: ct));
        Assert.Equal(3, step);
    }

    [Fact]
    public void BuildDisplaySignature_changes_on_hotplug_mode_refresh_hdr_and_primary()
    {
        var luid = new WindowsHdr.Luid { HighPart = 0, LowPart = 100 };
        var screen1 = new WindowsHdr.Screen(luid, 1, 0, @"\\.\DISPLAY1", "Monitor A", @"\\?\mon1", true, false, true, 500_000_000, "dp", 144.0);
        var screen2 = new WindowsHdr.Screen(luid, 2, 1, @"\\.\DISPLAY2", "Monitor B", @"\\?\mon2", false, false, true, 150_000_000, "hdmi", 60.0);

        var baseline = WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1]);
        Assert.Equal(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1]));
        Assert.NotEqual(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY2", [screen1]));
        Assert.NotEqual(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1, screen2]));
        Assert.NotEqual(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1 with { Enabled = true }]));
        Assert.NotEqual(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1 with { RefreshHz = 240.0 }]));
        Assert.NotEqual(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1 with { PixelRate = 900_000_000 }]));
        Assert.NotEqual(baseline, WebHostBridge.BuildDisplaySignature(@"\\.\DISPLAY1", [screen1 with { Adapter = new WindowsHdr.Luid { LowPart = 101 } }]));
    }

    [Fact]
    public async Task ReadAllDdcAsync_runs_multiple_monitors_in_parallel_and_preserves_order()
    {
        using var barrier = new Barrier(3);
        var gdis = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3" };

        var results = await WebHostBridge.ReadAllDdcAsync(gdis, gdi =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
            var idx = int.Parse(gdi[^1..]);
            return new MonitorDdc.Levels(idx * 10, 0, 100, idx * 5, 0, 100);
        });

        Assert.Equal(3, results.Length);
        Assert.Equal(10, results[0]?.Brightness);
        Assert.Equal(20, results[1]?.Brightness);
        Assert.Equal(30, results[2]?.Brightness);
        Assert.Empty(await WebHostBridge.ReadAllDdcAsync([], _ => null));
    }

    [Fact]
    public void ReadMemoryIntegrityOnly_skips_scheduled_tasks_and_matches_full_read()
    {
        var probe = new StrictIntegritySystem();
        var tuning = new SystemTuning(probe, Path.Combine(_dir, "tuning.json"), () => new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

        probe.HvciEnabled = 1;
        probe.IntegrityActive = true;
        var fast = tuning.ReadMemoryIntegrityOnly();
        Assert.NotNull(fast);
        Assert.Equal(SystemTuning.IntegrityKey, fast!.Key);
        Assert.False(fast.On);
        Assert.Equal(0, probe.ExpensiveCalls);

        probe.ThrowOnExpensive = false;
        var full = tuning.Read().Items.FirstOrDefault(i => i.Key == SystemTuning.IntegrityKey);
        Assert.Equal(full, fast);

        probe.ThrowOnExpensive = true;
        probe.HvciEnabled = 0;
        probe.IntegrityActive = false;
        Assert.Null(tuning.ReadMemoryIntegrityOnly());
        Assert.Equal(0, probe.ExpensiveCalls);
    }

    [Fact]
    public void AutopilotReports_caches_by_file_metadata_and_refreshes_on_add_and_delete()
    {
        var path = Path.Combine(_dir, AutopilotReports.FileName);
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        AutopilotReport MakeReport(int stutters) => new("g1", "Game", now.AddMinutes(-10), now, 600, 60_000, 120, 90, 60, stutters, [], null, [], [], 0, 0);

        Assert.Empty(AutopilotReports.Load(path));
        Assert.True(AutopilotReports.Add(path, MakeReport(3), now));

        var first = AutopilotReports.Load(path);
        Assert.Single(first);
        Assert.Equal(3, first[0].Stutters);
        first.Clear();

        var second = AutopilotReports.Load(path);
        Assert.Single(second);
        Assert.Equal(3, second[0].Stutters);

        Assert.True(AutopilotReports.Add(path, MakeReport(7), now.AddMinutes(1)));
        var third = AutopilotReports.Load(path);
        Assert.Equal(2, third.Count);
        Assert.Equal(7, third[0].Stutters);

        DeletionGate.DeleteFile(path);
        Assert.Empty(AutopilotReports.Load(path));
    }

    [Fact]
    public void AutopilotWatch_LiveChanged_fires_on_front_ticks_and_session_end_and_survives_throwing_listener()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var watch = new AutopilotWatch(() => null, _ => true, () => now);
        var notifications = 0;
        var lastSawLive = false;

        watch.LiveChanged += () => throw new InvalidOperationException("listener boom");
        watch.LiveChanged += () =>
        {
            notifications++;
            lastSawLive = watch.Live is not null;
        };

        watch.Second(null, null, false, [], []);
        Assert.Equal(0, notifications);

        watch.Second("g1", "Game", true, [8.0, 8.0], [1]);
        Assert.Equal(1, notifications);
        Assert.True(lastSawLive);

        watch.Second("g1", "Game", false, [], [1]);
        Assert.Equal(1, notifications);

        watch.Second("g1", "Game", true, [8.0, 8.0], [1]);
        Assert.Equal(2, notifications);
        Assert.True(lastSawLive);

        watch.End();
        Assert.Equal(3, notifications);
        Assert.False(lastSawLive);

        watch.End();
        Assert.Equal(3, notifications);
    }

    private sealed class StrictIntegritySystem : SystemTuning.ISystem
    {
        public bool ThrowOnExpensive { get; set; } = true;
        public int ExpensiveCalls { get; private set; }
        public int? HvciEnabled { get; set; } = 1;
        public bool? IntegrityActive { get; set; } = true;

        private T Fail<T>()
        {
            if (ThrowOnExpensive)
            {
                ExpensiveCalls++;
                throw new InvalidOperationException("Expensive system probe must not be called by ReadMemoryIntegrityOnly.");
            }
            return default!;
        }

        public long Frequency() => ThrowOnExpensive ? Fail<long>() : 10_000_000;
        public int? Dword(string subKey, string valueName) =>
            subKey == SystemTuning.Hvci && valueName == "Enabled" ? HvciEnabled : null;
        public string[]? Strings(string subKey, string valueName) => ThrowOnExpensive ? Fail<string[]?>() : null;
        public bool Exists(string subKey) => false;
        public (int Start, bool Delayed)? Service(string name) => Fail<(int Start, bool Delayed)?>();
        public bool? IntegrityRunning() => IntegrityActive;
        public DateTimeOffset Boot() => new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
        public Task<bool> Admin(IReadOnlyList<string> lines) => Task.FromResult(false);
        public string Answer() => "";
        public string? ReadAnswer(string answer, bool keep = false) => null;
        public bool PointStanding => false;
        public void PointMade(RestorePoint.Outcome outcome) { }
        public Task<LatencyTrace.Answer?> Latency() => Task.FromResult<LatencyTrace.Answer?>(null);
        public bool SupportsScheduledTasks => true;
        public IReadOnlyDictionary<string, bool> ScheduledTasks(IEnumerable<string> taskPaths) =>
            ThrowOnExpensive ? Fail<IReadOnlyDictionary<string, bool>>() : new Dictionary<string, bool>();
        public IReadOnlyList<string> NvidiaDisplayClassKeys() =>
            ThrowOnExpensive ? Fail<IReadOnlyList<string>>() : [];
        public bool SupportsWinFeatures => true;
        public WindowsFeatures.FeatureSnapshot? WinFeature(uint featureId) => Fail<WindowsFeatures.FeatureSnapshot?>();
        public bool SupportsOsOverhead => true;
        public bool IsLaptop() => Fail<bool>();
        public IReadOnlyList<string> HvciRequiringGames() => [];
    }
}
