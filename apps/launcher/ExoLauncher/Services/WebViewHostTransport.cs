using System.IO;
using ExoLauncher.Helpers;
using ExoLauncher.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// Shipping <see cref="IHostTransport"/> backed by WebView2's message channel and shared buffers.
/// Keeps UI-thread guards and origin checks aligned with the shell trust boundary.
/// </summary>
public sealed class WebViewHostTransport : IHostTransport
{
    private readonly DispatcherQueue _queue;
    private CoreWebView2? _web;
    private bool _detached;

    public WebViewHostTransport(CoreWebView2 web, DispatcherQueue queue)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        web.WebMessageReceived += OnWebMessageReceived;
        web.NavigationStarting += OnNavigationStarting;
    }

    public event Action<string>? MessageReceived;
    public event Action? SessionReset;

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e) => UiGuard.Run(() =>
    {
        if (Volatile.Read(ref _detached)) return;
        if (!WebViewTrustPolicy.IsTrustedBridgeSource(e.Source))
        {
            AppLog.Warn("Blocked a privileged WebView message from an untrusted origin.");
            return;
        }

        string? raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = e.WebMessageAsJson; } catch { return; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return;
        MessageReceived?.Invoke(raw);
    });

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e) => UiGuard.Run(() =>
    {
        if (e.Cancel) return;
        SessionReset?.Invoke();
    });

    public void PostJson(string json)
    {
        try
        {
            var web = _web;
            if (web is null || Volatile.Read(ref _detached)) return;

            void Send()
            {
                try { web.PostWebMessageAsJson(json); } catch { }
            }

            if (!_queue.HasThreadAccess) _queue.Enqueue(Send);
            else Send();
        }
        catch { }
    }

    public void PostBinaryFrame(string metaJson, byte[] frame)
    {
        var web = _web;
        if (web is null || Volatile.Read(ref _detached) || frame.Length == 0) return;

        void Send()
        {
            try
            {
                using var buffer = web.Environment.CreateSharedBuffer((ulong)frame.Length);
                using (var stream = WindowsRuntimeStreamExtensions.AsStreamForWrite(buffer.OpenStream(), 0))
                    stream.Write(frame);
                web.PostSharedBufferToScript(buffer, CoreWebView2SharedBufferAccess.ReadOnly, metaJson);
            }
            catch { }
        }

        if (!_queue.HasThreadAccess) _queue.Enqueue(Send);
        else Send();
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref _detached, true)) return;
        var web = Interlocked.Exchange(ref _web, null);
        if (web is not null)
        {
            try { web.WebMessageReceived -= OnWebMessageReceived; } catch { }
            try { web.NavigationStarting -= OnNavigationStarting; } catch { }
        }
    }

    public void Dispose() => Detach();
}
