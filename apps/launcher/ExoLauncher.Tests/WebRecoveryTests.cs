using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's own page could stay white or loop after a WebView2 process fails (a driver install resets the display adapter):
/// - a GPU process exit is ignored (the page stays white when no frame comes back) or reloads at once (WebView2 starts a new
///   GPU process by itself, and a reload on every driver hiccup costs what the page holds);
/// - a page process that exited or hangs isn't reloaded; an exited browser is only reloaded (its CoreWebView2 is dead);
/// - a subframe's or a utility process's failure reloads the whole page;
/// - the paint check reads no answer, an exception or an odd reply as drawn (white stays), or a hidden page as stuck (a
///   minimized window reloads over and over);
/// - the budget lets a fourth recovery through in a minute (a reload loop on a broken driver), never forgets one (three
///   spread over a day leave Exo asking for a restart), or counts a refused recovery against the next minute.
/// </summary>
public sealed class WebRecoveryTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, "CheckPaint")]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, "Reload")]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, "Reload")]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, "Recreate")]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, "None")]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited, "None")]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, "None")]
    public void EachFailureGetsItsRecovery(CoreWebView2ProcessFailedKind kind, string action) =>
        Assert.Equal(action, WebRecovery.For(kind).ToString());

    [Theory]
    [InlineData("""{"result":{"type":"string","value":"drawn"}}""", "Drawn")]
    [InlineData("""{"result":{"type":"string","value":"hidden"}}""", "Hidden")]
    [InlineData(null, "Stuck")]
    [InlineData("""{"result":{"type":"object"},"exceptionDetails":{"text":"Uncaught"}}""", "Stuck")]
    [InlineData("""{"result":{"type":"undefined"}}""", "Stuck")]
    [InlineData("""{"result":{"type":"boolean","value":true}}""", "Stuck")]
    [InlineData("""{"result":{"type":"string","value":"something"}}""", "Stuck")]
    [InlineData("not json", "Stuck")]
    public void PaintIsReadFromTheProbesReplyAlone(string? reply, string paint) =>
        Assert.Equal(paint, WebRecovery.ReadPaint(reply).ToString());

    /// <summary>
    /// Runs the probe's own expression (Node, the same JavaScript the page gets) against a page that is hidden, one that
    /// draws, and one whose compositor never draws a frame: hidden answers at once with no frame asked, a drawing page
    /// answers "drawn" after exactly two frames, a stuck one never answers (CDP's timeout then reads it as Stuck).
    /// </summary>
    [Theory]
    [InlineData("hidden", true, "hidden", 0)]
    [InlineData("visible", true, "drawn", 2)]
    [InlineData("visible", false, "pending", 1)]
    public void ProbeAwaitsTwoFramesAndAnswersHiddenAtOnce(string visibility, bool framesCome, string answer, int framesAsked)
    {
        using var request = JsonDocument.Parse(WebRecovery.PaintProbe);
        var root = request.RootElement;
        // CDP waits for the promise and returns its string, or the reply carries no value and reads as Stuck.
        Assert.True(root.GetProperty("awaitPromise").GetBoolean());
        Assert.True(root.GetProperty("returnByValue").GetBoolean());
        var harness = """
            const [expression, visibility, framesCome] = process.argv.slice(2)
            let asked = 0
            globalThis.document = { visibilityState: visibility }
            globalThis.requestAnimationFrame = cb => { asked++; if (framesCome === 'true') setTimeout(cb, 1) }
            const result = (0, eval)(expression)
            const settled = await Promise.race([Promise.resolve(result), new Promise(r => setTimeout(() => r('pending'), 200))])
            console.log(JSON.stringify({ answer: settled, asked }))
            """;
        var script = Path.Combine(TestDataDirectory.Root, "paint-probe-" + Guid.NewGuid().ToString("N") + ".mjs");
        Directory.CreateDirectory(TestDataDirectory.Root);
        File.WriteAllText(script, harness);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(root.GetProperty("expression").GetString()!);
            start.ArgumentList.Add(visibility);
            start.ArgumentList.Add(framesCome ? "true" : "false");
            using var node = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Node.js (node on PATH) runs this test; the UI build needs it too.");
            var output = node.StandardOutput.ReadToEnd();
            Assert.True(node.WaitForExit(10_000), "node did not finish");
            Assert.True(node.ExitCode == 0, node.StandardError.ReadToEnd());
            using var reply = JsonDocument.Parse(output);
            Assert.Equal(answer, reply.RootElement.GetProperty("answer").GetString());
            Assert.Equal(framesAsked, reply.RootElement.GetProperty("asked").GetInt32());
        }
        finally { DeletionGate.DeleteFile(script); }
    }

    [Fact]
    public void ThreeRecoveriesAMinuteThenNoMore()
    {
        var budget = new RecoveryBudget(3, TimeSpan.FromMinutes(1));
        Assert.True(budget.TryTake(0));
        Assert.True(budget.TryTake(1_000));
        Assert.True(budget.TryTake(2_000));
        Assert.False(budget.TryTake(3_000));
        Assert.False(budget.TryTake(59_999));
    }

    [Fact]
    public void ARecoveryAMinuteOldNoLongerCounts()
    {
        var budget = new RecoveryBudget(3, TimeSpan.FromMinutes(1));
        Assert.True(budget.TryTake(0));
        Assert.True(budget.TryTake(30_000));
        Assert.True(budget.TryTake(40_000));
        Assert.False(budget.TryTake(59_000));
        // The first is a minute old: one more may run, and the refused one at 59 s never counted.
        Assert.True(budget.TryTake(60_000));
        Assert.False(budget.TryTake(60_001));
        Assert.True(budget.TryTake(90_000));
    }

    [Fact]
    public void RecoveriesSpreadOverADayNeverRunOut()
    {
        var budget = new RecoveryBudget(3, TimeSpan.FromMinutes(1));
        for (var hour = 0L; hour < 24; hour++) Assert.True(budget.TryTake(hour * 3_600_000));
    }
}
