using System.Diagnostics;
using System.Text;

namespace ExoLauncher.Helpers;

internal static class ProcessOutput
{
    private enum DrainState
    {
        Succeeded,
        Faulted,
        TimedOut,
    }

    internal static (bool Completed, int ExitCode, byte[] Stdout) RunCaptureBytes(
        ProcessStartInfo info, TimeSpan timeout, bool killTree = false) =>
        RunCore(info, timeout, killTree, static (bytes, _) => bytes.ToArray(), []);

    internal static (bool Completed, int ExitCode, string Stdout) RunCaptureText(
        ProcessStartInfo info, TimeSpan timeout, bool killTree = false) =>
        RunCore(info, timeout, killTree, static (bytes, encoding) =>
        {
            bytes.Position = 0;
            using var reader = new StreamReader(bytes, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return reader.ReadToEnd();
        }, "");

    private static (bool Completed, int ExitCode, T Stdout) RunCore<T>(
        ProcessStartInfo info, TimeSpan timeout, bool killTree,
        Func<MemoryStream, Encoding, T> materialize, T empty)
    {
        using var process = Process.Start(info);
        if (process is null) return (false, -1, empty);

        var encoding = info.RedirectStandardOutput
            ? info.StandardOutputEncoding ?? process.StandardOutput.CurrentEncoding
            : Encoding.UTF8;

        var cts = new CancellationTokenSource();
        var bytes = new MemoryStream();
        var copyOut = Task.CompletedTask;
        var copyErr = Task.CompletedTask;

        try
        {
            copyOut = info.RedirectStandardOutput
                ? process.StandardOutput.BaseStream.CopyToAsync(bytes, cts.Token)
                : Task.CompletedTask;
            copyErr = info.RedirectStandardError
                ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, cts.Token)
                : Task.CompletedTask;

            var waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            if (!process.WaitForExit(waitMs))
            {
                CancelAndKill(process, cts, killTree);
                WaitDrain(copyOut, copyErr, TimeSpan.FromSeconds(2));
                return (false, -1, empty);
            }

            var drained = WaitDrain(copyOut, copyErr, TimeSpan.FromSeconds(2));
            if (drained != DrainState.Succeeded)
            {
                if (drained == DrainState.TimedOut)
                {
                    TryCancel(cts);
                    WaitDrain(copyOut, copyErr, TimeSpan.FromSeconds(1));
                }
                return (false, -1, empty);
            }

            return (true, process.ExitCode, materialize(bytes, encoding));
        }
        finally
        {
            if (copyOut.IsCompleted && copyErr.IsCompleted)
            {
                ObserveCompleted(copyOut);
                ObserveCompleted(copyErr);
                bytes.Dispose();
                cts.Dispose();
            }
            else
            {
                TryCancel(cts);
                AttachCleanup(copyOut, copyErr, bytes, cts);
            }
        }
    }

    internal static async Task DrainAndObserveAsync(
        Task first, Task second, TimeSpan wait, CancellationTokenSource? ownedCts = null)
    {
        var all = Task.WhenAll(first, second);
        try
        {
            await all.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AttachFaultObserver(first);
            AttachFaultObserver(second);
            AttachFaultObserver(all);
            if (ownedCts is not null)
                AttachDispose(all, ownedCts);
            return;
        }
        catch
        {
            ObserveCompleted(first);
            ObserveCompleted(second);
            ObserveCompleted(all);
        }

        ownedCts?.Dispose();
    }

    internal static async Task<byte[]?> CopyToMemoryAsync(
        Stream source, TimeSpan timeout, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var bytes = new MemoryStream();
        var copy = Task.CompletedTask;
        try
        {
            copy = source.CopyToAsync(bytes, cts.Token);
            await copy.WaitAsync(cts.Token).ConfigureAwait(false);
            return bytes.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (copy.IsCompleted)
            {
                ObserveCompleted(copy);
                bytes.Dispose();
                cts.Dispose();
            }
            else
            {
                TryCancel(cts);
                AttachCleanup(copy, Task.CompletedTask, bytes, cts);
            }
        }
    }

    internal static void AttachFaultObserver(Task task)
    {
        if (task.IsCompleted)
        {
            ObserveCompleted(task);
            return;
        }
        _ = task.ContinueWith(static t => ObserveCompleted(t),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void CancelAndKill(Process process, CancellationTokenSource cts, bool killTree)
    {
        TryCancel(cts);
        TryKill(process, killTree);
        TryWaitExit(process, 2000);
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch { }
    }

    private static void TryKill(Process process, bool killTree)
    {
        try
        {
            if (!process.HasExited) ProcessTermination.Kill(process, entireProcessTree: killTree);
        }
        catch { }
    }

    private static void TryWaitExit(Process process, int ms)
    {
        try { process.WaitForExit(ms); }
        catch { }
    }

    private static DrainState WaitDrain(Task first, Task second, TimeSpan wait)
    {
        var all = Task.WhenAll(first, second);
        try
        {
            if (all.Wait(wait))
                return first.Status == TaskStatus.RanToCompletion && second.Status == TaskStatus.RanToCompletion
                    ? DrainState.Succeeded
                    : DrainState.Faulted;
        }
        catch
        {
            ObserveCompleted(first);
            ObserveCompleted(second);
            ObserveCompleted(all);
            return DrainState.Faulted;
        }
        AttachFaultObserver(first);
        AttachFaultObserver(second);
        AttachFaultObserver(all);
        return DrainState.TimedOut;
    }

    private static void AttachCleanup(Task first, Task second, MemoryStream bytes, CancellationTokenSource cts)
    {
        AttachFaultObserver(first);
        AttachFaultObserver(second);
        var all = Task.WhenAll(first, second);
        _ = all.ContinueWith(static (t, state) =>
        {
            ObserveCompleted(t);
            var (stream, tokenSource) = ((MemoryStream, CancellationTokenSource))state!;
            stream.Dispose();
            tokenSource.Dispose();
        }, (bytes, cts), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void AttachDispose(Task task, IDisposable disposable)
    {
        _ = task.ContinueWith(static (t, state) =>
        {
            ObserveCompleted(t);
            ((IDisposable)state!).Dispose();
        }, disposable, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void ObserveCompleted(Task task)
    {
        if (!task.IsFaulted) return;
        var ex = task.Exception?.GetBaseException();
        if (ex is not null && ex is not ObjectDisposedException and not IOException and not OperationCanceledException)
            AppLog.Warn($"Process output copy faulted ({ex.GetType().Name}).");
    }
}
