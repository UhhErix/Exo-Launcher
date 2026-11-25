using System.Diagnostics;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

public sealed class ProcessOutputTests
{
    [Fact]
    public void SlowChildProcessTimeoutRaisesNoUnobservedTaskException()
    {
        var unobserved = new List<Exception>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved) unobserved.Add(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("echo starting & ping -n 30 127.0.0.1 >nul");

            var bytesResult = ProcessOutput.RunCaptureBytes(info, TimeSpan.FromMilliseconds(200), killTree: true);
            Assert.False(bytesResult.Completed);
            Assert.Equal(-1, bytesResult.ExitCode);
            Assert.Empty(bytesResult.Stdout);

            var textResult = ProcessOutput.RunCaptureText(info, TimeSpan.FromMilliseconds(200), killTree: true);
            Assert.False(textResult.Completed);
            Assert.Equal(-1, textResult.ExitCode);
            Assert.Equal("", textResult.Stdout);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            lock (unobserved) Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public void FastChildProcessCapturesStdoutAndExitCode()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("echo hello-exo");

        var textResult = ProcessOutput.RunCaptureText(info, TimeSpan.FromSeconds(5));
        Assert.True(textResult.Completed);
        Assert.Equal(0, textResult.ExitCode);
        Assert.Contains("hello-exo", textResult.Stdout);

        var bytesResult = ProcessOutput.RunCaptureBytes(info, TimeSpan.FromSeconds(5));
        Assert.True(bytesResult.Completed);
        Assert.Equal(0, bytesResult.ExitCode);
        Assert.Contains("hello-exo", Encoding.UTF8.GetString(bytesResult.Stdout));
    }
}
