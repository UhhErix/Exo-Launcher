using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

internal sealed class MousePanelService
{
    private readonly AppServices _services;
    private readonly ConcurrentDictionary<string, byte> _openedHidKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _runStateGate = new(1, 1);
    private Dictionary<string, string> _cardOf = new(StringComparer.Ordinal);
    private readonly Lock _cardLock = new();

    private DispatcherQueue? _queue;
    private WebHostBridge? _bridge;
    private MousePanelWindow? _window;
    private WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? _environmentLease;
    private LocalFileHost? _files;
    private CancellationTokenSource? _panelCts;
    private long _generation;
    private bool _iframeReady;
    private bool _eventsWired;
    private int _lastRailWidth;

    internal MousePanelService(AppServices services)
    {
        _services = services;
    }

    internal bool IsOpen => _window is { IsClosed: false };

    internal void Attach(DispatcherQueue queue, WebHostBridge bridge)
    {
        _queue = queue;
        _bridge = bridge;
    }

    internal Task<object> OpenAsync(
        nint ownerHwnd,
        double x,
        double y,
        double width,
        double height,
        bool visible,
        CancellationToken ct)
    {
        if (_queue is not { } queue || _bridge is null)
            return Task.FromResult<object>(new { ok = false, message = "Mouse panel host is not attached." });

        return RunOnQueueAsync(queue, () => OpenOnUiThreadAsync(ownerHwnd, x, y, width, height, visible, ct));
    }

    internal Task<object> UpdateBoundsAsync(
        nint ownerHwnd,
        double x,
        double y,
        double width,
        double height,
        bool visible)
    {
        if (_queue is not { } queue)
            return Task.FromResult<object>(new { ok = true });

        return RunOnQueueAsync(queue, () =>
        {
            if (_window is { IsClosed: false } window
                && (ownerHwnd == 0 || window.OwnerHandle == ownerHwnd))
            {
                window.UpdateBounds(x, y, width, height, visible);
                PushRailIfChanged(window);
            }
            return Task.FromResult<object>(new { ok = true });
        });
    }

    internal Task<object> CloseAsync()
    {
        if (_queue is not { } queue)
        {
            CloseCoreOnUiThread();
            return Task.FromResult<object>(new { ok = true });
        }
        return RunOnQueueAsync(queue, () =>
        {
            CloseCoreOnUiThread();
            return Task.FromResult<object>(new { ok = true });
        });
    }

    internal void Close()
    {
        var queue = _queue;
        if (queue is null || queue.HasThreadAccess)
        {
            CloseCoreOnUiThread();
            return;
        }
        if (!queue.TryEnqueue(() => UiGuard.Run(CloseCoreOnUiThread)))
        {
            CloseCoreOnUiThread();
        }
    }

    internal void NotifySettingsChanged()
    {
        var queue = _queue;
        if (queue is null) return;
        _ = queue.TryEnqueue(() => UiGuard.Run(() =>
        {
            if (_window is not { IsClosed: false } window || !_iframeReady) return;
            var settings = _services.Settings.Current;
            var scale = MousePanelRelay.TextScaleFor(settings.TextSize);
            PostEventOnUiThread(MousePanelRelay.SerializeEvent("exo.textScale", new { scale }));
            PostEventOnUiThread(MousePanelRelay.SerializeEvent("exo.motion", new { motion = MousePanelRelay.MotionFor(settings.ReduceMotion) }));
            PushRailIfChanged(window, force: true);
        }));
    }

    private async Task<object> OpenOnUiThreadAsync(
        nint ownerHwnd,
        double x,
        double y,
        double width,
        double height,
        bool visible,
        CancellationToken ct)
    {
        if (_window is { IsClosed: false } existing && existing.OwnerHandle == ownerHwnd)
        {
            existing.UpdateBounds(x, y, width, height, visible);
            if (_iframeReady)
            {
                PushRailIfChanged(existing);
                PostEventOnUiThread(MousePanelRelay.SerializeEvent("room.visible", existing.IsShown));
            }
            return new { ok = true };
        }

        CloseCoreOnUiThread();

        var mouseDir = ResolveMouseDir();
        if (mouseDir is null)
            return new { ok = false, message = "Exo Mouse assets were not found." };

        var gen = Interlocked.Increment(ref _generation);
        var panelCts = new CancellationTokenSource();
        _panelCts = panelCts;
        _iframeReady = false;
        _lastRailWidth = 0;

        MousePanelWindow window;
        try
        {
            window = MousePanelWindow.Create(ownerHwnd, x, y, width, height, visible);
        }
        catch (Exception ex)
        {
            AppLog.Warn("MousePanel: create window failed: " + ex.Message);
            return new { ok = false, message = "Exo could not open the mouse panel." };
        }

        _window = window;
        window.OwnerDestroyed += () => Close();
        window.VisibilityChanged += shown =>
        {
            if (Volatile.Read(ref _generation) != gen || !_iframeReady) return;
            PostEventOnUiThread(MousePanelRelay.SerializeEvent("room.visible", shown));
        };

        WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? lease = null;
        try
        {
            lease = await WebViewEnvironmentFactory.AcquireAsync();
            if (Volatile.Read(ref _generation) != gen || window.IsClosed || ct.IsCancellationRequested)
            {
                lease.Dispose();
                if (!window.IsClosed) window.Close();
                return new { ok = false, message = "Mouse panel closed before ready." };
            }

            var environment = lease.Environment;
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window.Handle));
            if (Volatile.Read(ref _generation) != gen || window.IsClosed || ct.IsCancellationRequested)
            {
                WebViewEnvironmentLifetime.CloseAndRelease(controller);
                lease.Dispose();
                if (!window.IsClosed) window.Close();
                return new { ok = false, message = "Mouse panel closed before ready." };
            }

            _environmentLease = lease;
            lease = null;
            window.AttachController(controller);
            var core = controller.CoreWebView2;
            WebViewEnvironmentLifetime.ApplyLowMemoryTarget(core);
            WebViewWindowHider.Hide(core.BrowserProcessId);

            ConfigureCore(core, environment, mouseDir, gen);
            WireHostEvents();
            core.Navigate(MousePanelRelay.WrapperUrl);
            return new { ok = true };
        }
        catch (Exception ex)
        {
            lease?.Dispose();
            AppLog.Warn("MousePanel: WebView2 init failed: " + ex.Message);
            if (Volatile.Read(ref _generation) == gen)
                CloseCoreOnUiThread();
            return new { ok = false, message = "Exo Mouse could not start." };
        }
    }

    private void ConfigureCore(
        CoreWebView2 core,
        CoreWebView2Environment environment,
        string mouseDir,
        long gen)
    {
        try { core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; } catch { }
        try { core.Settings.AreHostObjectsAllowed = false; } catch { }
        try { core.Settings.IsWebMessageEnabled = true; } catch { }
        try { core.Settings.AreDefaultScriptDialogsEnabled = false; } catch { }
        try { core.Settings.IsStatusBarEnabled = false; } catch { }
        try { core.Settings.IsSwipeNavigationEnabled = false; } catch { }
        try { core.Settings.IsPinchZoomEnabled = false; } catch { }
#if DEBUG
        try { core.Settings.AreDevToolsEnabled = true; } catch { }
#else
        try { core.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
        try { core.Settings.AreDevToolsEnabled = false; } catch { }
#endif

        core.SetVirtualHostNameToFolderMapping(
            WebViewTrustPolicy.MouseHost,
            mouseDir,
            CoreWebView2HostResourceAccessKind.DenyCors);

        if (_queue is { } queue)
        {
            try
            {
                var files = new LocalFileHost(core, queue);
                files.ServeMade(CoverArtService.ThumbnailHost, ReadThumbnailAsync, LocalFileHost.KeptForRun);
                _files = files;
            }
            catch (Exception ex)
            {
                AppLog.Warn("MousePanel: thumbnail host unavailable: " + ex.Message);
            }
        }

        core.AddWebResourceRequestedFilter(MousePanelRelay.WrapperUrl, CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) => UiGuard.Run(() =>
        {
            if (!MousePanelRelay.IsTrustedWrapperSource(e.Request.Uri)) return;
            var bytes = Encoding.UTF8.GetBytes(MousePanelRelay.WrapperHtml);
            var stream = new MemoryStream(bytes, writable: false);
            e.Response = environment.CreateWebResourceResponse(
                stream.AsRandomAccessStream(),
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        });

        core.NavigationStarting += (_, e) => UiGuard.Run(() =>
        {
            if (!MousePanelRelay.IsTrustedWrapperSource(e.Uri))
                e.Cancel = true;
        });

        core.FrameNavigationStarting += (_, e) => UiGuard.Run(() =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.IdnHost, WebViewTrustPolicy.MouseHost, StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort)
            {
                e.Cancel = true;
            }
        });

        core.NewWindowRequested += (_, e) => UiGuard.Run(() => e.Handled = true);
        core.PermissionRequested += (_, e) => UiGuard.Run(() => e.State = CoreWebView2PermissionState.Deny);
        core.ProcessFailed += (_, e) => UiGuard.Run(() =>
        {
            AppLog.Warn($"MousePanel: WebView2 process failed ({e.ProcessFailedKind}).");
            if (Volatile.Read(ref _generation) == gen) CloseCoreOnUiThread();
        });
        core.WebMessageReceived += (_, e) => UiGuard.Run(() => OnWebMessage(e, gen));
    }

    private void OnWebMessage(CoreWebView2WebMessageReceivedEventArgs e, long gen)
    {
        if (Volatile.Read(ref _generation) != gen) return;
        if (!MousePanelRelay.IsTrustedWrapperSource(e.Source)) return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("exo", out var exoEl) || exoEl.ValueKind != JsonValueKind.String) return;
            var exo = exoEl.GetString();

            if (string.Equals(exo, "mouse-host", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("ready", out var readyEl) && readyEl.ValueKind == JsonValueKind.True
                    && _window is { IsClosed: false } window)
                {
                    _iframeReady = true;
                    PostEventOnUiThread(MousePanelRelay.SerializeEvent("room.visible", window.IsShown));
                    PushRailIfChanged(window, force: true);
                }
                return;
            }

            if (!string.Equals(exo, "mouse", StringComparison.Ordinal)) return;

            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                HandleOneWayModuleMessage(root);
                return;
            }

            if (idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out var id)) return;
            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String) return;
            var method = methodEl.GetString();
            if (!MousePanelRelay.IsAllowedMethod(method)) return;

            var hasParams = root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object;
            var paramsClone = hasParams ? paramsEl.Clone() : default;
            var ct = _panelCts?.Token ?? CancellationToken.None;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await DispatchMethodAsync(method!, paramsClone, hasParams, ct).ConfigureAwait(false);
                    PostIfCurrent(gen, MousePanelRelay.SerializeReply(id, result));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLog.Warn($"MousePanel.{method}: {ex.Message}");
                    var message = ex is InvalidOperationException && WebHostBridge.ThrownByExo(ex)
                        ? ex.Message
                        : "Exo could not reach the device.";
                    PostIfCurrent(gen, MousePanelRelay.SerializeError(id, message));
                }
            });
        }
    }

    private static void HandleOneWayModuleMessage(JsonElement root)
    {
        if (MousePanelRelay.ValidateNotice(root) is { } notice)
            AppLog.Info("MousePanel notice: " + notice);
        _ = MousePanelRelay.TryParseMouseState(root, out _);
    }

    private async Task<object> DispatchMethodAsync(string method, JsonElement p, bool hasParams, CancellationToken ct)
    {
        var bridge = _bridge ?? throw new InvalidOperationException("Host bridge unavailable.");
        switch (method)
        {
            case "hid.list":
                return bridge.HidList(p, hasParams);
            case "hid.open":
            {
                var result = await bridge.HidCallAsync("hid.open", p, hasParams, ct).ConfigureAwait(false);
                if (ReadDeviceKey(p, hasParams) is { } key) _openedHidKeys[key] = 0;
                return result;
            }
            case "hid.close":
            {
                if (ReadDeviceKey(p, hasParams) is { } key) _openedHidKeys.TryRemove(key, out _);
                return await bridge.HidCallAsync("hid.close", p, hasParams, ct).ConfigureAwait(false);
            }
            case "hid.sendReport":
            case "hid.sendFeatureReport":
            case "hid.receiveFeatureReport":
            case "hid.receiveInputReport":
                return await bridge.HidCallAsync(method, p, hasParams, ct).ConfigureAwait(false);
            case "exo.games":
                return await ReadInstalledGamesAsync().ConfigureAwait(false);
            case "exo.profiles.read":
                return bridge.MouseProfilesGet();
            case "exo.profiles.write":
                return bridge.MouseProfilesSet(p, hasParams);
            case "exo.presets.read":
                return bridge.MousePresetsGet();
            case "exo.presets.write":
                return bridge.MousePresetsSet(p, hasParams);
            case "exo.textScale":
                return new { ok = true, data = MousePanelRelay.TextScaleString(_services.Settings.Current.TextSize) };
            case "exo.motion":
                return new { ok = true, data = MousePanelRelay.MotionFor(_services.Settings.Current.ReduceMotion) };
            case "exo.theme":
                return new { ok = true, data = "void", look = MousePanelRelay.BuildLookTokens() };
            case "exo.mouseCore":
            {
                var action = MousePanelRelay.ValidateCoreAction(p, hasParams);
                using var doc = action is null
                    ? JsonDocument.Parse("{}")
                    : JsonDocument.Parse(JsonSerializer.Serialize(new { action }));
                return await WebHostBridge.SystemMouseCoreAsync(doc.RootElement, true, ct).ConfigureAwait(false);
            }
            default:
                throw new InvalidOperationException("Unsupported mouse panel method.");
        }
    }

    private async Task<object> ReadInstalledGamesAsync()
    {
        var library = await _services.Library.GetLibraryAsync().ConfigureAwait(false);
        var (games, cardMap) = MousePanelRelay.BuildInstalledGames(
            library,
            file => LocalFileHost.Url(CoverArtService.ThumbnailHost, $"{file}?w=240"));
        lock (_cardLock) _cardOf = cardMap;
        return new { ok = true, games };
    }

    private async Task<string> ResolveCardIdAsync(string sourceId)
    {
        lock (_cardLock)
        {
            if (_cardOf.TryGetValue(sourceId, out var found))
                return found;
        }
        try
        {
            var library = await _services.Library.GetLibraryAsync().ConfigureAwait(false);
            var (_, cardMap) = MousePanelRelay.BuildInstalledGames(
                library,
                file => LocalFileHost.Url(CoverArtService.ThumbnailHost, $"{file}?w=240"));
            lock (_cardLock)
            {
                _cardOf = cardMap;
                return _cardOf.TryGetValue(sourceId, out var resolved) ? resolved : sourceId;
            }
        }
        catch
        {
            return sourceId;
        }
    }

    private void WireHostEvents()
    {
        if (_eventsWired) return;
        _eventsWired = true;
        var hid = _services.Hid;
        hid.Input += OnHidInput;
        hid.Changed += OnHidChanged;
        hid.Start();
        _services.Launcher.GameRunStateChanged += OnGameRunStateChanged;
    }

    private void UnwireHostEvents()
    {
        if (!_eventsWired) return;
        _eventsWired = false;
        try
        {
            _services.Hid.Input -= OnHidInput;
            _services.Hid.Changed -= OnHidChanged;
        }
        catch { }
        try
        {
            _services.Launcher.GameRunStateChanged -= OnGameRunStateChanged;
        }
        catch { }
    }

    private void OnHidInput(string key, int reportId, byte[] data)
    {
        var gen = Volatile.Read(ref _generation);
        PostIfCurrent(gen, MousePanelRelay.SerializeEvent("hid.input", new
        {
            device = key,
            reportId,
            data = Convert.ToBase64String(data),
        }));
    }

    private void OnHidChanged()
    {
        var gen = Volatile.Read(ref _generation);
        PostIfCurrent(gen, MousePanelRelay.SerializeEvent("hid.changed", new { }));
    }

    private void OnGameRunStateChanged(string gameId, GameRunState state)
    {
        var gen = Volatile.Read(ref _generation);
        _ = Task.Run(async () =>
        {
            await _runStateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _generation) != gen) return;
                var cardId = await ResolveCardIdAsync(gameId).ConfigureAwait(false);
                PostIfCurrent(gen, MousePanelRelay.SerializeGameRunState(cardId, state.IsRunning));
            }
            finally
            {
                _runStateGate.Release();
            }
        });
    }

    private void PushRailIfChanged(MousePanelWindow window, bool force = false)
    {
        if (!_iframeReady || window.IsClosed) return;
        var scale = MousePanelRelay.TextScaleFor(_services.Settings.Current.TextSize);
        var rail = MousePanelRelay.RailWidth(window.LogicalWidth - 20.0, scale);
        if (!force && rail == _lastRailWidth) return;
        _lastRailWidth = rail;
        PostEventOnUiThread(MousePanelRelay.SerializeEvent("room.rail", rail));
    }

    private void PostIfCurrent(long gen, string json)
    {
        if (Volatile.Read(ref _generation) != gen) return;
        var queue = _queue;
        if (queue is null) return;
        _ = queue.TryEnqueue(() => UiGuard.Run(() =>
        {
            if (Volatile.Read(ref _generation) != gen) return;
            PostEventOnUiThread(json);
        }));
    }

    private void PostEventOnUiThread(string json)
    {
        if (_window is not { IsClosed: false, Core: { } core }) return;
        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private void CloseCoreOnUiThread()
    {
        Interlocked.Increment(ref _generation);
        _iframeReady = false;
        _lastRailWidth = 0;
        UnwireHostEvents();

        if (Interlocked.Exchange(ref _panelCts, null) is { } cts)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        foreach (var key in _openedHidKeys.Keys.ToArray())
        {
            if (_openedHidKeys.TryRemove(key, out _))
            {
                try { _services.Hid.Close(key); } catch { }
            }
        }

        _files = null;

        if (_window is { } window)
        {
            _window = null;
            try { window.Close(); } catch { }
        }

        if (Interlocked.Exchange(ref _environmentLease, null) is { } lease)
        {
            try { lease.Dispose(); } catch { }
        }
    }

    private static string? ReadDeviceKey(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        return p.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } key
            ? key
            : null;
    }

    private static string? ResolveMouseDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot", "mouse"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot", "mouse")),
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task<(byte[] Bytes, string Type)?> ReadThumbnailAsync(string name, string query)
    {
        var width = 0;
        var dim = false;
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("w=", StringComparison.Ordinal) && int.TryParse(pair.AsSpan(2), out var w)) width = w;
            else if (pair is "dim=1" or "dim") dim = true;
        }
        if (!CoverArtService.IsThumbnailWidth(width)) return null;
        return await CoverArtService.ReadThumbnailAsync(name, width, dim).ConfigureAwait(false) is { } body
            ? (body.Bytes, body.ContentType)
            : null;
    }

    private static Task<T> RunOnQueueAsync<T>(DispatcherQueue queue, Func<Task<T>> work)
    {
        if (queue.HasThreadAccess) return work();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.EnqueueAsync(async () =>
        {
            try
            {
                var result = await work().ConfigureAwait(true);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, "Mouse panel UI work"))
        {
            tcs.TrySetException(new InvalidOperationException("Host UI queue is shut down."));
        }
        return tcs.Task;
    }
}
