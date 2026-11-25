using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

public sealed class FailFastHardeningTests
{
    [Fact]
    public async Task EchoCancellerNeverCallsNativeAfterDisposeAndSerializesConcurrentRender()
    {
        var ct = TestContext.Current.CancellationToken;
        var activeRender = 0;
        var activeTotal = 0;
        var overlapDetected = 0;
        var useAfterFreeDetected = 0;
        var destroyed = 0;
        var inRender = new ManualResetEventSlim(false);
        var releaseRender = new ManualResetEventSlim(false);

        var canceller = new EchoCanceller(
            apm: 0x1234,
            renderNative: (handle, _) =>
            {
                if (handle == 0 || Volatile.Read(ref destroyed) != 0)
                    Interlocked.Increment(ref useAfterFreeDetected);
                if (Interlocked.Increment(ref activeRender) > 1)
                    Interlocked.Increment(ref overlapDetected);
                Interlocked.Increment(ref activeTotal);
                try
                {
                    inRender.Set();
                    releaseRender.Wait(TimeSpan.FromSeconds(2));
                    return 0;
                }
                finally
                {
                    Interlocked.Decrement(ref activeTotal);
                    Interlocked.Decrement(ref activeRender);
                }
            },
            captureNative: (handle, _) =>
            {
                if (handle == 0 || Volatile.Read(ref destroyed) != 0)
                    Interlocked.Increment(ref useAfterFreeDetected);
                Interlocked.Increment(ref activeTotal);
                try
                {
                    Thread.SpinWait(200);
                    return 0;
                }
                finally
                {
                    Interlocked.Decrement(ref activeTotal);
                }
            },
            destroyNative: _ =>
            {
                if (Volatile.Read(ref activeTotal) != 0)
                    Interlocked.Increment(ref useAfterFreeDetected);
                Interlocked.Increment(ref destroyed);
            });

        var frame = new float[VoiceAudio.FrameFloats];
        var t1 = Task.Run(() => canceller.Render(frame, silent: false), ct);
        Assert.True(inRender.Wait(TimeSpan.FromSeconds(5), ct));

        var t2 = Task.Run(() => canceller.Render(frame, silent: false), ct);
        var t3 = Task.Run(() => canceller.Capture(frame), ct);

        canceller.Dispose();
        canceller.Dispose();
        Assert.Equal(0, Volatile.Read(ref destroyed));

        releaseRender.Set();
        await Task.WhenAll(t1, t2, t3);

        Assert.Equal(1, Volatile.Read(ref destroyed));
        Assert.Equal(0, Volatile.Read(ref overlapDetected));
        Assert.Equal(0, Volatile.Read(ref useAfterFreeDetected));

        canceller.Render(frame, silent: false);
        canceller.Capture(frame);
        Assert.Equal(0, Volatile.Read(ref useAfterFreeDetected));
    }

    [Fact]
    public void CrashBreadcrumbScrubsPersonalDataToSingleLineAndFeedsPipelineWithoutThrowing()
    {
        var previousLog = CrashBreadcrumb.LogWriter;
        var previousReport = CrashBreadcrumb.ReportRecorder;
        var logs = new List<string>();
        var reports = new List<Exception>();
        try
        {
            CrashBreadcrumb.LogWriter = line => logs.Add(line);
            CrashBreadcrumb.ReportRecorder = ex => reports.Add(ex);

            var secretEx = new InvalidOperationException(
                "Failed reading C:\\Users\\Alice\\secret.txt\nwith Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc123def456 for alice@example.com on https://private.example.com/tok?k=1");
            var formatted = CrashBreadcrumb.FormatLine(
                "ui-callback",
                secretEx,
                context: null,
                threadId: 7,
                userName: "Alice",
                profile: @"C:\Users\Alice",
                machineName: "ALICE-PC");

            Assert.DoesNotContain('\n', formatted);
            Assert.DoesNotContain('\r', formatted);
            Assert.DoesNotContain("Alice", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice@example.com", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain("private.example.com", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source=ui-callback", formatted, StringComparison.Ordinal);
            Assert.Contains("type=InvalidOperationException", formatted, StringComparison.Ordinal);
            Assert.True(formatted.Length <= 512);

            CrashBreadcrumb.Record("ui-callback", secretEx);
            Assert.Single(logs);
            Assert.Single(reports);
            Assert.Same(secretEx, reports[0]);

            var nativeLine = CrashBreadcrumb.FormatNativeLine(
                0xC0000005,
                @"C:\Users\Alice\AppData\Local\ExoLauncher\app\exo_apm.dll",
                0x1A2B,
                42,
                "Alice",
                @"C:\Users\Alice",
                "ALICE-PC");
            Assert.DoesNotContain("Alice", nativeLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AppData", nativeLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("code=0xC0000005", nativeLine, StringComparison.Ordinal);
            Assert.Contains("mod=exo_apm.dll", nativeLine, StringComparison.Ordinal);
            Assert.Contains("off=0x1A2B", nativeLine, StringComparison.Ordinal);

            CrashBreadcrumb.LogWriter = _ => throw new IOException("disk full");
            CrashBreadcrumb.ReportRecorder = _ => throw new UnauthorizedAccessException("denied");
            CrashBreadcrumb.Record("ui-callback", secretEx);
        }
        finally
        {
            CrashBreadcrumb.LogWriter = previousLog;
            CrashBreadcrumb.ReportRecorder = previousReport;
        }
    }

    [Fact]
    public void EveryUnmanagedCallersOnlyMethodHasTopLevelTryCatch()
    {
        var unguarded = new List<string>();
        var root = Repo.PathOf("ExoLauncher");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"\[UnmanagedCallersOnly[^\]]*\]"))
            {
                var brace = text.IndexOf('{', match.Index + match.Length);
                if (brace < 0)
                {
                    unguarded.Add($"ExoLauncher/{rel}: missing method body");
                    continue;
                }
                var afterBrace = text[(brace + 1)..].TrimStart();
                if (!afterBrace.StartsWith("try", StringComparison.Ordinal))
                    unguarded.Add($"ExoLauncher/{rel}: UnmanagedCallersOnly method must open with try/catch");
            }
        }

        Assert.Empty(unguarded);
    }
}
