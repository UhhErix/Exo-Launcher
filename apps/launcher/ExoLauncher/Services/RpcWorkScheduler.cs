using System.Collections.Concurrent;

namespace ExoLauncher.Services;

public enum RpcLane
{
    Fast,
    Gpu,
    System,
    Devices,
    Apps,
    Library,
    Shaders,
}

internal sealed class RpcWorkScheduler
{
    internal static int DefaultMaxTotalHeavy => Math.Clamp(Environment.ProcessorCount, 6, 10);
    internal const int DefaultMaxPerLane = 3;

    private readonly SemaphoreSlim _globalHeavy;
    private readonly Dictionary<RpcLane, SemaphoreSlim> _lanes;
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationToken _lifetime;
    private int _activeHeavy;
    private int _queuedHeavy;

    internal RpcWorkScheduler(int? maxTotalHeavy = null, int? maxPerLane = null, CancellationToken lifetime = default)
    {
        var total = Math.Max(1, maxTotalHeavy ?? DefaultMaxTotalHeavy);
        var perLane = Math.Clamp(maxPerLane ?? DefaultMaxPerLane, 1, total);
        _globalHeavy = new SemaphoreSlim(total, total);
        _lanes = new Dictionary<RpcLane, SemaphoreSlim>
        {
            [RpcLane.Gpu] = new(perLane, perLane),
            [RpcLane.System] = new(perLane, perLane),
            [RpcLane.Devices] = new(perLane, perLane),
            [RpcLane.Apps] = new(perLane, perLane),
            [RpcLane.Library] = new(perLane, perLane),
            [RpcLane.Shaders] = new(perLane, perLane),
        };
        _lifetime = lifetime;
    }

    internal int ActiveHeavyCount => Volatile.Read(ref _activeHeavy);
    internal int QueuedHeavyCount => Volatile.Read(ref _queuedHeavy);

    internal static RpcLane LaneFor(string method)
    {
        if (method.StartsWith("gpu.", StringComparison.Ordinal)) return RpcLane.Gpu;
        if (method.StartsWith("devices.", StringComparison.Ordinal)
            || method.StartsWith("hid.", StringComparison.Ordinal)
            || method.StartsWith("keyboard.", StringComparison.Ordinal)
            || method.StartsWith("audio.", StringComparison.Ordinal)
            || method.StartsWith("controller.", StringComparison.Ordinal)
            || method is "eq" or "eq.headphones" or "steamInput.state")
            return RpcLane.Devices;
        if (method.StartsWith("system.", StringComparison.Ordinal)
            || method.StartsWith("autopilot.", StringComparison.Ordinal)
            || method.StartsWith("exoAi.", StringComparison.Ordinal))
            return RpcLane.System;
        if (method.StartsWith("apps.", StringComparison.Ordinal)
            || method.StartsWith("startup.", StringComparison.Ordinal)
            || method.StartsWith("runtimes.", StringComparison.Ordinal)
            || method.StartsWith("deps.", StringComparison.Ordinal)
            || method.StartsWith("compress.", StringComparison.Ordinal)
            || method.StartsWith("junk.", StringComparison.Ordinal)
            || method.StartsWith("replaced.", StringComparison.Ordinal)
            || method.StartsWith("backup.", StringComparison.Ordinal))
            return RpcLane.Apps;
        if (method.StartsWith("library.", StringComparison.Ordinal)
            || method is "stores.matrix" or "accounts.list" or "clients.status")
            return RpcLane.Library;
        if (method.StartsWith("shaders.", StringComparison.Ordinal)
            || method.StartsWith("dlss.", StringComparison.Ordinal)
            || method.StartsWith("hdr.", StringComparison.Ordinal))
            return RpcLane.Shaders;
        return RpcLane.Fast;
    }

    internal Task<T> RunAsync<T>(string method, Func<T> work, CancellationToken ct, string? coalesceKey = null)
    {
        var lane = LaneFor(method);
        if (lane == RpcLane.Fast) return Task.Run(work, ct);
        return RunHeavyAsync(lane, token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(work());
        }, ct, coalesceKey);
    }

    internal Task<T> RunAsync<T>(string method, Func<CancellationToken, Task<T>> work, CancellationToken ct, string? coalesceKey = null)
    {
        var lane = LaneFor(method);
        if (lane == RpcLane.Fast) return Task.Run(() => work(ct), ct);
        return RunHeavyAsync(lane, work, ct, coalesceKey);
    }

    private Task<T> RunHeavyAsync<T>(RpcLane lane, Func<CancellationToken, Task<T>> work, CancellationToken ct, string? coalesceKey)
    {
        if (string.IsNullOrEmpty(coalesceKey))
            return ExecuteHeavyAsync(lane, work, ct);

        return RunCoalescedAsync(lane, work, ct, coalesceKey);
    }

    private async Task<T> RunCoalescedAsync<T>(RpcLane lane, Func<CancellationToken, Task<T>> work, CancellationToken ct, string coalesceKey)
    {
        ct.ThrowIfCancellationRequested();
        Lazy<Task<object?>>? created = null;
        var entry = _inflight.GetOrAdd(coalesceKey, _ =>
        {
            created = new Lazy<Task<object?>>(
                () => RunSharedAsync(lane, work, coalesceKey),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return created;
        });

        var shared = entry.Value;
        var boxed = await shared.WaitAsync(ct).ConfigureAwait(false);
        return (T)boxed!;
    }

    private async Task<object?> RunSharedAsync<T>(RpcLane lane, Func<CancellationToken, Task<T>> work, string coalesceKey)
    {
        try
        {
            return await ExecuteHeavyAsync(lane, work, _lifetime).ConfigureAwait(false);
        }
        finally
        {
            if (_inflight.TryGetValue(coalesceKey, out var current) && current.IsValueCreated)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(coalesceKey, current));
        }
    }

    private async Task<T> ExecuteHeavyAsync<T>(RpcLane lane, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var laneGate = _lanes[lane];
        var laneAcquired = false;
        var globalAcquired = false;
        var active = false;
        var queued = true;
        Interlocked.Increment(ref _queuedHeavy);
        try
        {
            await laneGate.WaitAsync(ct).ConfigureAwait(false);
            laneAcquired = true;
            await _globalHeavy.WaitAsync(ct).ConfigureAwait(false);
            globalAcquired = true;
            Interlocked.Decrement(ref _queuedHeavy);
            queued = false;
            Interlocked.Increment(ref _activeHeavy);
            active = true;
            return await Task.Run(() => work(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            if (active) Interlocked.Decrement(ref _activeHeavy);
            if (queued) Interlocked.Decrement(ref _queuedHeavy);
            if (globalAcquired) _globalHeavy.Release();
            if (laneAcquired) laneGate.Release();
        }
    }
}
