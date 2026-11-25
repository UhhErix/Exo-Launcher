using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

internal sealed class WebViewEnvironmentRefPool<T> where T : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private T? _current;
    private int _refs;
    private Func<T, Task?>? _onReleased;
    private Task? _lastReleasedWait;

    internal int ActiveCount
    {
        get
        {
            lock (_stateLock) return _refs;
        }
    }

    internal T? TryGetCurrent()
    {
        lock (_stateLock) return _refs > 0 ? _current : null;
    }

    internal Lease? TryAcquire(T? expected)
    {
        if (expected is null) return null;
        lock (_stateLock)
        {
            if (_current is null || !ReferenceEquals(_current, expected)) return null;
            _refs++;
            return new Lease(this, _current);
        }
    }

    internal async Task<Lease> AcquireAsync(
        Func<CancellationToken, Task<T>> factory,
        Func<T, Task?>? onReleased = null,
        CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_current is not null)
            {
                _refs++;
                return new Lease(this, _current);
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            Task? previousWait;
            lock (_stateLock)
            {
                if (_current is not null)
                {
                    _refs++;
                    return new Lease(this, _current);
                }
                previousWait = _lastReleasedWait;
                _lastReleasedWait = null;
            }

            if (previousWait is not null)
            {
                try { await previousWait.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }

            var created = await factory(ct);
            lock (_stateLock)
            {
                _current = created;
                _refs = 1;
                _onReleased = onReleased;
            }
            return new Lease(this, created);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void Reset(T? failed)
    {
        if (failed is null) return;
        Func<T, Task?>? onReleased = null;
        var matched = false;
        lock (_stateLock)
        {
            if (ReferenceEquals(_current, failed))
            {
                _current = null;
                _refs = 0;
                onReleased = _onReleased;
                _onReleased = null;
                matched = true;
            }
        }

        if (!matched) return;
        var wait = onReleased?.Invoke(failed);
        if (wait is not null)
        {
            lock (_stateLock) _lastReleasedWait = wait;
        }
    }

    private void Release(T target)
    {
        Func<T, Task?>? onReleased;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_current, target)) return;
            _refs = Math.Max(0, _refs - 1);
            if (_refs > 0) return;
            _current = null;
            onReleased = _onReleased;
            _onReleased = null;
        }

        var wait = onReleased?.Invoke(target);
        if (wait is not null)
        {
            lock (_stateLock) _lastReleasedWait = wait;
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly WebViewEnvironmentRefPool<T> _pool;
        private T? _value;
        private int _disposed;

        internal Lease(WebViewEnvironmentRefPool<T> pool, T value)
        {
            _pool = pool;
            _value = value;
        }

        internal T Environment => _value ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var target = Interlocked.Exchange(ref _value, null);
            if (target is not null) _pool.Release(target);
        }
    }
}

internal static class WebViewEnvironmentLifetime
{
    private static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, Task> PendingFolderExits = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task WaitForFolderExitAsync(string? userDataFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder)) return;
        if (!PendingFolderExits.TryGetValue(userDataFolder, out var pending)) return;
        try { await pending.WaitAsync(BrowserExitTimeout, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    internal static void ApplyLowMemoryTarget(CoreWebView2? core)
    {
        if (core is null) return;
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
    }

    internal static void CloseAndRelease(CoreWebView2Controller? controller)
    {
        if (controller is null) return;
        try
        {
            if (controller.CoreWebView2 is { IsSuspended: true } core)
                core.Resume();
        }
        catch { }
        try { controller.Close(); } catch { }
    }

    internal static void CloseAndRelease(CoreWebView2CompositionController? controller)
    {
        if (controller is null) return;
        try
        {
            if (controller.CoreWebView2 is { IsSuspended: true } core)
                core.Resume();
        }
        catch { }
        try { controller.RootVisualTarget = null; } catch { }
        try { controller.Close(); } catch { }
    }

    internal static Task? Release(CoreWebView2Environment? environment, uint browserProcessId = 0, string? userDataFolder = null)
    {
        if (environment is null) return null;
        var pid = browserProcessId;
        var folder = userDataFolder;
        var task = Task.Run(() => DrainAndWaitAsync(pid, folder));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            PendingFolderExits[folder] = task;
            _ = task.ContinueWith(
                _ => PendingFolderExits.TryRemove(new KeyValuePair<string, Task>(folder, task)),
                TaskScheduler.Default);
        }
        return task;
    }

    private static async Task DrainAndWaitAsync(uint browserProcessId, string? userDataFolder)
    {
        await Task.Delay(50).ConfigureAwait(false);
        CollectFinalizers();

        var deadline = Environment.TickCount64 + (long)BrowserExitTimeout.TotalMilliseconds;
        var pid = browserProcessId != 0 ? browserProcessId : FindBrowserProcessId(userDataFolder);
        if (pid != 0)
        {
            await WaitForPidExitAsync(pid, deadline).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(userDataFolder)) return;

        var delayMs = 250;
        while (Environment.TickCount64 < deadline && HasWebViewProcessesForFolder(userDataFolder))
        {
            CollectFinalizers();
            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            await Task.Delay(Math.Min(delayMs, remaining)).ConfigureAwait(false);
            delayMs = Math.Min(delayMs + 250, 1000);
        }
    }

    private static async Task WaitForPidExitAsync(uint pid, long deadline)
    {
        Process? process = null;
        try { process = Process.GetProcessById((int)pid); }
        catch { return; }

        using (process)
        {
            if (IsExited(process)) return;
            var delayMs = 200;
            while (!IsExited(process) && Environment.TickCount64 < deadline)
            {
                var remainingMs = (int)Math.Max(1, deadline - Environment.TickCount64);
                var stepMs = Math.Min(delayMs, remainingMs);
                try
                {
                    using var cts = new CancellationTokenSource(stepMs);
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    if (IsExited(process)) return;
                    CollectFinalizers();
                    delayMs = Math.Min(delayMs + 200, 1000);
                }
                catch
                {
                    return;
                }
            }
        }
    }

    private static uint FindBrowserProcessId(string? userDataFolder)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder)) return 0;
        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            using (process)
            {
                var line = HostedProcessClass.QueryCommandLine(process.Id);
                if (line is not null
                    && line.Contains(userDataFolder, StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                {
                    return (uint)process.Id;
                }
            }
        }
        return 0;
    }

    private static bool HasWebViewProcessesForFolder(string userDataFolder)
    {
        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            using (process)
            {
                var line = HostedProcessClass.QueryCommandLine(process.Id);
                if (line is not null
                    && line.Contains(userDataFolder, StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("--type=crashpad-handler", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void CollectFinalizers()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
        catch { }
    }
}
