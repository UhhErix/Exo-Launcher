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

    [Fact]
    public async Task CopyToMemoryTimeoutDoesNotDisposeMemoryStreamWhileCopyIsRunning()
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
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = new DelayedUninterruptibleStream(release.Task);

            var ct = TestContext.Current.CancellationToken;
            var result = await ProcessOutput.CopyToMemoryAsync(source, TimeSpan.FromMilliseconds(50), ct);
            Assert.Null(result);

            release.SetResult();
            await source.FirstReadCompleted.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(50, ct);

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
    public async Task DrainAndObserveAsyncObservesLateFaultAfterTimeout()
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
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();

            await ProcessOutput.DrainAndObserveAsync(tcs.Task, Task.CompletedTask, TimeSpan.FromMilliseconds(30), cts);
            Assert.False(cts.IsCancellationRequested);

            tcs.SetException(new ObjectDisposedException("MemoryStream"));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            lock (unobserved) Assert.Empty(unobserved);
            Assert.Throws<ObjectDisposedException>(() => _ = cts.Token);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    private sealed class DelayedUninterruptibleStream(Task gate) : Stream
    {
        private readonly TaskCompletionSource _firstReadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstReadCompleted => _firstReadCompleted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                await gate.ConfigureAwait(false);
                var payload = "late-chunk"u8;
                payload.CopyTo(buffer.Span);
                _firstReadCompleted.TrySetResult();
                return payload.Length;
            }
            return 0;
        }
    }
}
