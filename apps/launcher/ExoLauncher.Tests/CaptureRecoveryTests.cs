using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

// A dropped or malformed reply must not strand calls on a dead pipe or poison later replies;
// the last saved event must arrive before the old run goes away, and the new run must be isolated.
public sealed class CaptureRecoveryTests
{
    [Fact]
    public async Task EofFailsOutstandingCallsAfterDeliveringSavedAndRestarts()
    {
        var savedAndExited = new ConcurrentQueue<string>();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = 0;
        using var addon = new CaptureAddon(() => Fake(++launch == 1 ? """
            while ($line = [Console]::ReadLine()) {
                $request = ConvertFrom-Json $line
                if ($request.method -eq 'finish') {
                    [Console]::WriteLine('{"event":"saved","data":{"kind":"record"}}')
                    break
                }
            }
            """ : Echo));
        addon.Event += (name, _) =>
        {
            if (name is "saved" or "exited") savedAndExited.Enqueue(name);
            if (name == "ready" && launch == 2) restarted.TrySetResult();
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = addon.CallAsync("finish", null, deadline.Token);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Exo Capture stopped.", failure.Message);
        await restarted.Task.WaitAsync(deadline.Token);
        Assert.Equal(new[] { "saved", "exited" }, savedAndExited.ToArray());
        Assert.Equal("status", (await addon.CallAsync("status", null, deadline.Token))?.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => addon.CallAsync("status", null, deadline.Token, generation: 1));
    }

    [Fact]
    public async Task BrokenMessagesAndDroppedRepliesDoNotPoisonParallelCalls()
    {
        using var addon = new CaptureAddon(() => Fake("""
            [Console]::WriteLine('{"event":42}')
            [Console]::WriteLine('{"event":"bad","event":"duplicate"}')
            [Console]::WriteLine('{"bad":')
            """ + Environment.NewLine + Echo));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var dropped = addon.CallAsync("drop", null, deadline.Token);
        var goodCalls = await Task.WhenAll(addon.CallAsync("first", null, deadline.Token),
            addon.CallAsync("second", null, deadline.Token));
        Assert.Equal(new[] { "first", "second" }, goodCalls.Select(g => g?.GetValue<string>()));
        var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() => dropped);
        Assert.Contains("didn't respond", timeout.Message);
        Assert.Equal("after", (await addon.CallAsync("after", null, deadline.Token))?.GetValue<string>());
    }

    [Fact]
    public async Task ABlockedPipeDoesNotBlockTheCallerThread()
    {
        using var addon = new CaptureAddon(() => Fake("Start-Sleep -Seconds 5"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var stopwatch = Stopwatch.StartNew();
        var request = addon.CallAsync("test", new JsonObject { ["data"] = new string('x', 1_000_000) }, deadline.Token);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "The call waited for the OS pipe write.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public void AnUnacknowledgedSwitchIsCorrectedEvenWhenTheDesiredGameReverts()
    {
        var target = new CaptureGameTarget();
        Assert.False(target.Begin(1, null));
        Assert.True(target.Begin(1, "game-a"));
        target.Acknowledge(1, "game-a");
        Assert.False(target.Begin(1, "GAME-A"));
        Assert.True(target.Begin(1, "game-b"));
        Assert.True(target.Begin(1, "game-a"));
        target.Acknowledge(1, "game-a");
        Assert.True(target.Begin(1, null));
        target.Acknowledge(1, null);
        Assert.False(target.Begin(1, null));
    }

    [Fact]
    public void AReplacementAddOnGetsTheCurrentGameAgain()
    {
        var target = new CaptureGameTarget();
        Assert.True(target.Begin(1, "game-a"));
        target.Acknowledge(1, "game-a");
        Assert.True(target.Begin(2, "game-a"));
    }

    private const string Echo = """
        while ($line = [Console]::ReadLine()) {
            $request = ConvertFrom-Json $line
            if ($request.method -eq 'shutdown') { break }
            if ($request.method -ne 'drop') {
                [Console]::WriteLine((ConvertTo-Json -Compress @{id=$request.id; result=$request.method}))
            }
        }
        """;

    private static ProcessStartInfo Fake(string script)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var ready = """[Console]::WriteLine('{"event":"ready","data":{}}')""" + Environment.NewLine;
        var arguments = Convert.ToBase64String(Encoding.Unicode.GetBytes(ready + script));
        return new ProcessStartInfo(path, "-NoProfile -NonInteractive -EncodedCommand " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }
}
