using System.Diagnostics;
using System.Text;

namespace ExoLauncher.Helpers;

internal static class ProcessOutput
{
    internal static (bool Completed, int ExitCode, byte[] Stdout) RunCaptureBytes(
        ProcessStartInfo info, TimeSpan timeout, bool killTree = false)
    {
        using var process = Process.Start(info);
        if (process is null) return (false, -1, []);

        using var cts = new CancellationTokenSource();
        using var bytes = new MemoryStream();
        var copyOut = info.RedirectStandardOutput
            ? process.StandardOutput.BaseStream.CopyToAsync(bytes, cts.Token)
            : Task.CompletedTask;
        var copyErr = info.RedirectStandardError
            ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, cts.Token)
            : Task.CompletedTask;

        var waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        if (!process.WaitForExit(waitMs))
        {
            cts.Cancel();
            TryKill(process, killTree);
            TryWaitExit(process, 2000);
            Observe(copyOut, copyErr, TimeSpan.FromSeconds(2));
            return (false, -1, []);
        }

        if (!Observe(copyOut, copyErr, TimeSpan.FromSeconds(2)))
        {
            cts.Cancel();
            Observe(copyOut, copyErr, TimeSpan.FromSeconds(1));
            return (false, -1, []);
        }

        return (true, process.ExitCode, bytes.ToArray());
    }

    internal static (bool Completed, int ExitCode, string Stdout) RunCaptureText(
        ProcessStartInfo info, TimeSpan timeout, bool killTree = false)
    {
        using var process = Process.Start(info);
        if (process is null) return (false, -1, "");

        var encoding = info.RedirectStandardOutput
            ? info.StandardOutputEncoding ?? process.StandardOutput.CurrentEncoding
            : Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        using var bytes = new MemoryStream();
        var copyOut = info.RedirectStandardOutput
            ? process.StandardOutput.BaseStream.CopyToAsync(bytes, cts.Token)
            : Task.CompletedTask;
        var copyErr = info.RedirectStandardError
            ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, cts.Token)
            : Task.CompletedTask;

        var waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        if (!process.WaitForExit(waitMs))
        {
            cts.Cancel();
            TryKill(process, killTree);
            TryWaitExit(process, 2000);
            Observe(copyOut, copyErr, TimeSpan.FromSeconds(2));
            return (false, -1, "");
        }

        if (!Observe(copyOut, copyErr, TimeSpan.FromSeconds(2)))
        {
            cts.Cancel();
            Observe(copyOut, copyErr, TimeSpan.FromSeconds(1));
            return (false, -1, "");
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return (true, process.ExitCode, reader.ReadToEnd());
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

    private static bool Observe(Task first, Task second, TimeSpan wait)
    {
        var all = Task.WhenAll(first, second);
        try
        {
            if (all.Wait(wait)) return true;
        }
        catch
        {
            return true;
        }
        AttachFaultObserver(first);
        AttachFaultObserver(second);
        AttachFaultObserver(all);
        return false;
    }

    private static void AttachFaultObserver(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
