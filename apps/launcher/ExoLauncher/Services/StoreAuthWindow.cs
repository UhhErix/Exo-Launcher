using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// The sign-in browser for store accounts (Epic via Legendary, GOG via gogdl).
/// It is a plain browser on purpose: no ad blocker or extensions (Epic and GOG
/// sign-in depend on bot-protection and captcha scripts that blockers break),
/// the stock Edge user agent, and the store plus the identity providers it
/// federates to ("Sign in with Google/Apple/Xbox/…"). Anything else stays out; a link to another app is asked in Exo's
/// card over the page (WebPageGuard).
/// Its windows wear Exo's chrome with the store's mark (ExoConnectedTheme); the page is the store's own.
/// Provider popups open in a second window on the same browser environment so
/// <c>window.opener</c> hand-backs work, and close when the provider is done.
/// </summary>
internal sealed class StoreAuthWindow
{
    // Service: the window's service (ServiceMarks), its mark and name in the title band.
    internal sealed record Options(
        string LogName,
        string Service,
        string Title,
        string UserDataFolder,
        string StartUrl,
        Func<Uri, bool> IsAllowedNavigation,
        Func<string, bool>? InterceptNavigation = null,
        Func<WebView2, Task>? PageLoaded = null);

    private readonly Options _options;
    private readonly List<(Window Window, WebView2 Web)> _popups = new();
    private CoreWebView2Environment? _environment;
    private uint _browserPid;
    private Window? _window;
    private WebView2? _web;
    private bool _closing;
    private bool _closed;
    private string? _lastLoggedHost;

    internal StoreAuthWindow(Options options) => _options = options;

    /// <summary>Raised on the UI thread when the user closes the main sign-in window.</summary>
    internal event Action? Closed;

    /// <summary>Must run on the UI thread.</summary>
    internal async Task StartAsync()
    {
        var web = new WebView2();
        var window = new Window { Title = _options.Title };
        ExoConnectedTheme.WindowContent(window, _options.Service, web);
        _window = window;
        _web = web;
        window.Closed += (_, _) => OnMainClosed();
        Show(window);
        AppLog.Info($"{_options.LogName}: sign-in window shown.");

        CoreWebView2Environment? createdEnvironment = null;
        try
        {
            await WebViewEnvironmentLifetime.WaitForFolderExitAsync(_options.UserDataFolder);
            if (_closing) return;
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                WebViewRuntimeLocator.ResolveBrowserExecutableFolder(),
                _options.UserDataFolder,
                new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
            createdEnvironment = environment;
            if (_closing)
            {
                _ = WebViewEnvironmentLifetime.Release(environment, userDataFolder: _options.UserDataFolder);
                return;
            }
            _environment = environment;
            await web.EnsureCoreWebView2Async(environment);
            if (_closing)
            {
                uint pid = 0;
                try { pid = web.CoreWebView2?.BrowserProcessId ?? 0; } catch { }
                try { (web.Parent as Panel)?.Children.Remove(web); } catch { }
                try { web.Close(); } catch { }
                _ = WebViewEnvironmentLifetime.Release(environment, pid, _options.UserDataFolder);
                return;
            }
            var core = web.CoreWebView2 ?? throw new InvalidOperationException("The sign-in browser did not start.");
            _browserPid = core.BrowserProcessId;
            Configure(core, web, popupWindow: null);
            AppLog.Info($"{_options.LogName}: browser ready (WebView2 {environment.BrowserVersionString}); UA \"{core.Settings.UserAgent}\".");
            core.Navigate(_options.StartUrl);
        }
        catch
        {
            uint pid = 0;
            try { pid = web.CoreWebView2?.BrowserProcessId ?? 0; } catch { }
            try { (web.Parent as Panel)?.Children.Remove(web); } catch { }
            try { web.Close(); } catch { }
            Close();
            if (createdEnvironment is not null)
                _ = WebViewEnvironmentLifetime.Release(createdEnvironment, pid, _options.UserDataFolder);
            throw;
        }
    }

    /// <summary>Close every sign-in window. Safe to call more than once; UI thread only.</summary>
    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        _closing = true;
        foreach (var (popupWindow, popupWeb) in _popups.ToArray())
        {
            try { MainWindow.ClearPageCard(popupWeb); } catch { }
            try { (popupWeb.Parent as Panel)?.Children.Remove(popupWeb); } catch { }
            try { popupWindow.Content = null; } catch { }
            try { popupWeb.Close(); } catch { }
            try { popupWindow.Close(); } catch { }
        }
        _popups.Clear();
        var web = _web;
        var window = _window;
        var environment = _environment;
        var browserPid = _browserPid;
        _web = null;
        _window = null;
        _environment = null;
        _browserPid = 0;
        if (web is not null)
        {
            try { MainWindow.ClearPageCard(web); } catch { }
            try { (web.Parent as Panel)?.Children.Remove(web); } catch { }
        }
        if (window is not null)
        {
            try { window.Content = null; } catch { }
        }
        if (web is not null)
        {
            try { if (web.CoreWebView2 is { IsSuspended: true } core) core.Resume(); } catch { }
            try { web.Close(); } catch { }
        }
        if (window is not null)
        {
            try { window.Close(); } catch { }
        }
        _ = WebViewEnvironmentLifetime.Release(environment, browserPid, _options.UserDataFolder);
    }

    private void OnMainClosed()
    {
        var userClosed = !_closing;
        Close();
        if (!userClosed) return;
        AppLog.Info($"{_options.LogName}: sign-in window closed by the user.");
        try { Closed?.Invoke(); } catch { /* listener failure must not escape the UI thread */ }
    }

    private void Configure(CoreWebView2 core, WebView2 web, Window? popupWindow)
    {
        WebViewEnvironmentLifetime.ApplyLowMemoryTarget(core);
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        // WebView2's default UA already matches Edge. Strip any embedding token
        // a future runtime might add; identity providers refuse "embedded" UAs.
        var userAgent = settings.UserAgent ?? "";
        var cleaned = CleanUserAgent(userAgent);
        if (!string.Equals(cleaned, userAgent, StringComparison.Ordinal))
            settings.UserAgent = cleaned;
        // A page that didn't load, or one asking to open another app, is Exo's card over this window's page, not Edge's.
        var guard = WebPageGuard.Attach(core, _options.LogName, web);

        var where = popupWindow is null ? "" : " (popup)";
        core.NavigationStarting += (_, e) => UiGuard.Run(() =>
        {
            if (_options.InterceptNavigation?.Invoke(e.Uri) == true)
            {
                e.Cancel = true;
                return;
            }
            // A link to another app goes on to the guard, which asks in Exo's card (or refuses it), never Edge.
            if (WebPageGuard.IsAppLink(e.Uri)) return;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            {
                e.Cancel = true;
                AppLog.Info($"{_options.LogName}: navigation blocked{where} to {Describe(e.Uri)}.");
                return;
            }
            if (!string.Equals(_lastLoggedHost, uri.Host + where, StringComparison.OrdinalIgnoreCase))
            {
                _lastLoggedHost = uri.Host + where;
                AppLog.Info($"{_options.LogName}: navigating{where} to {Describe(e.Uri)}.");
            }
        });
        core.NavigationCompleted += (sender, e) => UiGuard.Run(() =>
        {
            string title;
            try { title = core.DocumentTitle ?? ""; } catch { title = ""; }
            var source = Describe(SafeSource(core));
            if (!e.IsSuccess)
            {
                // Cancelled navigations (blocked or intercepted) also land here.
                if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    AppLog.Warn($"{_options.LogName}: page failed{where} ({e.WebErrorStatus}) at {source}.");
                return;
            }
            AppLog.Info($"{_options.LogName}: loaded{where} {source} (title \"{Shorten(title)}\").");
            if (popupWindow is null && _options.PageLoaded is { } loaded)
                _ = loaded(web);
        });
        core.NewWindowRequested += (sender, e) => UiGuard.Run(() =>
        {
            if (!WebPageGuard.IsAppLink(e.Uri))
            {
                OnNewWindowRequested(sender, e);
                return;
            }
            e.Handled = true;
            guard.Offer(e.Uri);
        });
        core.ProcessFailed += (_, e) => UiGuard.Run(() =>
            AppLog.Warn($"{_options.LogName}: browser process failed{where} ({e.ProcessFailedKind})."));
        if (popupWindow is not null)
        {
            core.WindowCloseRequested += (_, _) => UiGuard.Run(() =>
            {
                AppLog.Info($"{_options.LogName}: provider popup finished.");
                try { popupWindow.Close(); } catch { /* already closed */ }
            });
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var target = e.Uri;
        var blank = string.IsNullOrWhiteSpace(target) ||
                    target.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        if (!blank && (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !IsAllowed(uri)))
        {
            e.Handled = true;
            AppLog.Info($"{_options.LogName}: popup blocked to {Describe(target)}.");
            return;
        }

        var environment = _environment;
        if (environment is null || _closing)
        {
            e.Handled = true;
            return;
        }

        var deferral = e.GetDeferral();
        Window? popupWindow = null;
        WebView2? popupWeb = null;
        try
        {
            popupWeb = new WebView2();
            popupWindow = new Window { Title = _options.Title };
            ExoConnectedTheme.WindowContent(popupWindow, _options.Service, popupWeb, ExoConnectedTheme.AskedSize(e.WindowFeatures));
            var popup = popupWindow;
            var web = popupWeb;
            var entry = (popup, web);
            _popups.Add(entry);
            popup.Closed += (_, _) =>
            {
                _popups.Remove(entry);
                try { MainWindow.ClearPageCard(web); } catch { }
                try { (web.Parent as Panel)?.Children.Remove(web); } catch { }
                try { popup.Content = null; } catch { }
                try { web.Close(); } catch { }
            };
            Show(popup);
            await popupWeb.EnsureCoreWebView2Async(environment);
            if (_closing)
            {
                e.Handled = true;
                try { popup.Content = null; } catch { }
                try { popupWeb.Close(); } catch { }
                try { popup.Close(); } catch { }
                return;
            }
            var popupCore = popupWeb.CoreWebView2 ?? throw new InvalidOperationException("Popup browser did not start.");
            Configure(popupCore, popupWeb, popup);
            e.NewWindow = popupCore;
            e.Handled = true;
            AppLog.Info($"{_options.LogName}: opened provider popup for {Describe(target)}.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{_options.LogName}: provider popup failed: {ex.GetType().Name}: {ex.Message}");
            e.Handled = true;
            if (popupWeb is not null) try { MainWindow.ClearPageCard(popupWeb); } catch { }
            if (popupWindow is not null) try { popupWindow.Content = null; } catch { }
            try { popupWeb?.Close(); } catch { }
            try { popupWindow?.Close(); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return _options.IsAllowedNavigation(uri);
    }

    private static string SafeSource(CoreWebView2 core)
    {
        try { return core.Source ?? ""; }
        catch { return ""; }
    }

    /// <summary>Scheme, host and path only: queries carry codes and tokens.</summary>
    internal static string Describe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "<blank>";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<invalid url>";
        if (uri.Scheme is not ("https" or "http")) return uri.Scheme + ":";
        var path = uri.AbsolutePath;
        if (path.Length > 80) path = path[..80] + "…";
        return uri.Scheme + "://" + uri.Host + path;
    }

    private static string Shorten(string text) =>
        text.Length <= 80 ? text : text[..80] + "…";

    internal static string CleanUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return userAgent;
        var parts = userAgent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("WebView2", StringComparison.OrdinalIgnoreCase) &&
                           !part.StartsWith("WebView/", StringComparison.OrdinalIgnoreCase) &&
                           !part.StartsWith("ExoLauncher", StringComparison.OrdinalIgnoreCase));
        return string.Join(' ', parts);
    }

    /// <summary>The window (placed by its chrome) brought to the front: the user just asked for it.</summary>
    private static void Show(Window window)
    {
        ForegroundWatcher.Asked("a store's sign-in window");
        window.Activate();
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = true;
                // A process started minimized hands SW_SHOWMINIMIZED to its
                // next new window; a sign-in window must never open iconic.
                if (presenter.State == OverlappedPresenterState.Minimized)
                    presenter.Restore(activateWindow: true);
            }
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetForegroundWindow(hwnd);
        }
        catch { /* Activate already requested focus */ }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    internal static bool HostIs(Uri uri, string suffix) =>
        uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identity providers and captcha hosts a store login may put in the top
    /// frame. Hosts only; each is the provider's own sign-in origin.
    /// </summary>
    internal static bool IsIdentityProvider(Uri uri) =>
        // Google
        uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("accounts.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("gds.google.com", StringComparison.OrdinalIgnoreCase) ||
        // Apple
        uri.Host.Equals("appleid.apple.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("idmsa.apple.com", StringComparison.OrdinalIgnoreCase) ||
        // Microsoft / Xbox
        uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("account.live.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("account.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("account.xbox.com", StringComparison.OrdinalIgnoreCase) ||
        HostIs(uri, "xboxlive.com") ||
        // PlayStation
        HostIs(uri, "account.sony.com") ||
        HostIs(uri, "sonyentertainmentnetwork.com") ||
        // Steam OpenID
        (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
         (uri.AbsolutePath.StartsWith("/openid", StringComparison.OrdinalIgnoreCase) ||
          uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))) ||
        uri.Host.Equals("login.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
        // Nintendo
        HostIs(uri, "accounts.nintendo.com") ||
        // Facebook
        HostIs(uri, "facebook.com") ||
        // LEGO
        HostIs(uri, "lego.com") ||
        // Disney
        uri.Host.Equals("registerdisney.go.com", StringComparison.OrdinalIgnoreCase) ||
        HostIs(uri, "disney.com") ||
        HostIs(uri, "disneyplus.com") ||
        // Discord (GOG)
        (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) &&
         (uri.AbsolutePath.StartsWith("/oauth2", StringComparison.OrdinalIgnoreCase) ||
          uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))) ||
        // Captcha / bot protection, should one take the top frame.
        HostIs(uri, "hcaptcha.com") ||
        HostIs(uri, "arkoselabs.com") ||
        HostIs(uri, "funcaptcha.com") ||
        HostIs(uri, "recaptcha.net") ||
        uri.Host.Equals("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase);
}
