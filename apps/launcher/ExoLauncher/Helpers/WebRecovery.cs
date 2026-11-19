using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>
/// What Exo does when a process of its own page's WebView2 fails (<c>MainWindow.WebRecovery.cs</c>), how it tells whether
/// the page draws, and how often it may recover.
/// </summary>
internal static class WebRecovery
{
    internal enum Action { None, CheckPaint, Reload, Recreate }

    /// <summary>
    /// A GPU process WebView2 starts again by itself, so the page is checked for frames first; a page process that exited
    /// or hangs is reloaded; a browser process that exited takes its environment with it, so the WebView2 is made again.
    /// A subframe's or a utility process changes nothing on the page.
    /// </summary>
    internal static Action For(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.GpuProcessExited => Action.CheckPaint,
        CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => Action.Reload,
        CoreWebView2ProcessFailedKind.BrowserProcessExited => Action.Recreate,
        _ => Action.None,
    };

    internal enum Paint { Drawn, Stuck, Hidden }

    /// <summary>
    /// Runtime.evaluate's request: two animation frames, which come only while Chromium's compositor draws through a
    /// working GPU process. A hidden page draws no frames at all, so it says so instead of waiting.
    /// </summary>
    internal const string PaintProbe =
        """{"expression":"document.visibilityState === 'hidden' ? 'hidden' : new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r('drawn'))))","awaitPromise":true,"returnByValue":true}""";

    /// <summary>The probe's reply: null (no answer in time), an exception or anything unexpected is a page that doesn't draw.</summary>
    internal static Paint ReadPaint(string? reply)
    {
        if (reply is null) return Paint.Stuck;
        try
        {
            using var json = JsonDocument.Parse(reply);
            if (json.RootElement.TryGetProperty("exceptionDetails", out _)
                || !json.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.String) return Paint.Stuck;
            return value.GetString() switch { "drawn" => Paint.Drawn, "hidden" => Paint.Hidden, _ => Paint.Stuck };
        }
        catch (JsonException) { return Paint.Stuck; }
    }
}

/// <summary>
/// At most <paramref name="max"/> recoveries inside any <paramref name="window"/>: a driver that keeps failing can't put the
/// page in a reload loop. UI thread only.
/// </summary>
internal sealed class RecoveryBudget(int max, TimeSpan window)
{
    private readonly Queue<long> _taken = new();

    /// <summary>Counts a recovery at <paramref name="nowMs"/> (Environment.TickCount64) and says whether it may run.</summary>
    internal bool TryTake(long nowMs)
    {
        while (_taken.Count > 0 && nowMs - _taken.Peek() >= window.TotalMilliseconds) _taken.Dequeue();
        if (_taken.Count >= max) return false;
        _taken.Enqueue(nowMs);
        return true;
    }
}
