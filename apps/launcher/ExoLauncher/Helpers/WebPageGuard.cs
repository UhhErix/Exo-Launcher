using System.Runtime.InteropServices;
using ExoLauncher.Adapters;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>
/// Edge's own two surfaces over another site's page, in Exo's look instead, for every WebView2 that shows one:
/// <list type="bullet">
/// <item>"Hmm, can't reach this page": the built-in error page is off, and a page that didn't load is Exo's card over the
/// view (<see cref="MainWindow.ShowPageError"/>), saying the plain cause under the site's name from its own address, never
/// the page's words, with Try again. A certificate that isn't valid is never let through (Launcher's pages are big
/// services, not routers): it says the connection isn't private.</item>
/// <item>"Open this app?": a link to another app is always cancelled. Where the page is seen, Exo asks in place
/// ("music.youtube.com wants to open Spotify", Windows' own name for the app, Cancel first) and opens it itself only
/// after Open. Schemes used to run code from a page never open, and a page that asks three times in 10 seconds is refused
/// without asking until it navigates.</item>
/// </list>
/// A view's own allow-list lets app links through (<see cref="IsAppLink"/>) to be asked here, and hands a window opened
/// on one to <see cref="Offer"/>. A view nobody sees (no <c>view</c>: the hidden helpers and the hidden music engine) asks
/// nothing: its links to other apps are refused and logged, and a page that failed is its owner's to report
/// (<see cref="Failure"/>).
/// </summary>
internal sealed partial class WebPageGuard
{
    private readonly CoreWebView2 _core;
    private readonly WebView2? _view;
    private readonly string _log;
    private readonly AppAskLimit _limit = new();
    // Each main-frame navigation's latest address (after its redirects), by navigation.
    private readonly Dictionary<ulong, string> _addresses = [];
    // The host whose certificate stopped a page: that navigation then completes as Unknown, not as a certificate error.
    private string? _certificateHost;
    private bool _asking;

    private WebPageGuard(CoreWebView2 core, string log, WebView2? view)
    {
        _core = core;
        _log = log;
        _view = view;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.NavigationStarting += (_, e) => UiGuard.Run(() =>
        {
            if (_addresses.Count >= 32) _addresses.Clear();
            _addresses[e.NavigationId] = e.Uri;
            if (!e.IsRedirected) _certificateHost = null;
        });
        core.ServerCertificateErrorDetected += (_, e) => UiGuard.Run(() =>
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            _certificateHost = HostOf(e.RequestUri);
        });
        core.LaunchingExternalUriScheme += OnOpenApp;
        core.NavigationCompleted += (sender, e) => UiGuard.Run(() => OnCompleted(sender, e));
    }

    /// <param name="view">The view the user sees this page in; null where no one does.</param>
    internal static WebPageGuard Attach(CoreWebView2 core, string log, WebView2? view = null) => new(core, log, view);

    /// <summary>Why this main-frame navigation failed, in a few plain words; null for success or no failure at all.</summary>
    internal string? Failure(CoreWebView2NavigationCompletedEventArgs e) =>
        e.IsSuccess || !_addresses.TryGetValue(e.NavigationId, out var address)
            ? null : Cause(e.WebErrorStatus, HostOf(address), _certificateHost);

    private void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _limit.Navigated();
            if (_view is not null) MainWindow.ClearPageCard(_view);
            return;
        }
        if (_view is null || Failure(e) is not { } cause || !_addresses.TryGetValue(e.NavigationId, out var address)) return;
        AppLog.Info($"{_log}: {HostOf(address)} didn't load ({e.WebErrorStatus}).");
        MainWindow.ShowPageError(_view, cause, () =>
        {
            try { _core.Navigate(address); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
        });
    }

    private void OnOpenApp(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        // Edge's own question never shows: Exo asks, and launches it itself.
        e.Cancel = true;
        Offer(e.Uri, e.InitiatingOrigin);
    }

    /// <summary>
    /// A link to another app this page asked for (a navigation to it, or a window opened on it), from
    /// <paramref name="origin"/> (else the page's own address): asked in place where the page is seen, refused otherwise.
    /// </summary>
    internal async void Offer(string link, string? origin = null)
    {
        var scheme = SchemeOf(link);
        // Logged by its scheme only: the rest of a link can carry codes and tokens.
        var named = scheme is null ? "a link" : $"a {scheme}: link";
        var site = HostOf(origin) ?? HostOf(Source()) ?? "This page";
        var refusedBefore = _limit.Refused;
        if (!_limit.Ask(DateTime.UtcNow))
        {
            if (!refusedBefore) AppLog.Info($"{_log}: {site} asked to open other apps too often; refused until it navigates.");
            return;
        }
        if (scheme is null || !MayOpen(link))
        {
            AppLog.Warn($"{_log}: refused {named} from {site}.");
            return;
        }
        if (_view is null)
        {
            AppLog.Info($"{_log}: refused {named} from {site} (a page no one sees).");
            return;
        }
        if (_asking) return;
        _asking = true;
        bool open;
        try { open = await MainWindow.AskOverPage(_view, $"{site} wants to open {AppName(scheme) ?? scheme + " links"}", "Open"); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { open = false; }
        finally { _asking = false; }
        if (!open) return;
        try
        {
            ProcessHelper.StartProtocol(link)?.Dispose();
            AppLog.Info($"{_log}: opened {named} from {site}.");
        }
        catch (Exception ex) { AppLog.Warn($"{_log}: {named} didn't open: {ex.GetType().Name}."); }
    }

    private string? Source()
    {
        try { return _core.Source; }
        catch { return null; }
    }

    /// <summary>
    /// The few plain words for a page that didn't load: under its host, never the page's own words. Null when nothing
    /// failed for the user: a navigation a newer one replaced or a page cancelled, one handed to another app or to a
    /// download, one waiting on a sign-in, or anything that isn't a web page.
    /// </summary>
    internal static string? Cause(CoreWebView2WebErrorStatus status, string? host, string? certificateHost)
    {
        if (host is null) return null;
        // Cancelled for its certificate, it reports Unknown: the host it stopped says which it was.
        if (certificateHost == host) return $"The connection to {host} isn't private";
        return status switch
        {
            CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted or
                CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired or
                CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired => null,
            CoreWebView2WebErrorStatus.Disconnected => "You're offline",
            CoreWebView2WebErrorStatus.HostNameNotResolved => $"Can't find {host}",
            CoreWebView2WebErrorStatus.Timeout => $"{host} took too long to answer",
            CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable => $"{host} won't connect",
            CoreWebView2WebErrorStatus.ConnectionReset => $"The connection to {host} broke",
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired or
                CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked or
                CoreWebView2WebErrorStatus.CertificateIsInvalid => $"The connection to {host} isn't private",
            CoreWebView2WebErrorStatus.RedirectFailed => $"{host} keeps sending you round in circles",
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => $"{host} sent back something that isn't a page",
            _ => $"Can't reach {host}",
        };
    }

    /// <summary>A web page's host as its address names it (punycode, so a lookalike shows as one); null for anything else.</summary>
    internal static string? HostOf(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        uri.IdnHost.Length > 0 ? uri.IdnHost : null;

    // What Edge's engine loads or runs itself: a navigation to any other scheme is a link to another app.
    private static readonly HashSet<string> BrowserSchemes =
    [
        "http", "https", "about", "data", "blob", "file", "filesystem", "javascript", "ws", "wss", "ftp",
        "chrome", "chrome-extension", "chrome-untrusted", "edge", "devtools", "view-source",
    ];

    // Never opened from a page: Windows' troubleshooters (ms-msdt), its search over a remote share (search-ms, search),
    // Office's command line (ms-officecmd), App Installer (ms-appinstaller) and setup's own pages (ms-cxh); and what
    // Chromium itself never hands to Windows: shell folders, old Help, compiled help and script.
    private static readonly HashSet<string> Refused =
    [
        "ms-msdt", "search-ms", "search", "ms-officecmd", "ms-appinstaller", "ms-cxh", "ms-cxh-full",
        "shell", "hcp", "ms-help", "ms-its", "its", "mk", "vbscript", "res", "disk", "disks", "afp", "nntp", "ie.http", "vnd.ms.radio",
    ];

    /// <summary>
    /// The address's scheme in lower case, when it is one (a letter, then letters, digits, +, - or .); never a single
    /// letter, which Windows takes for a drive.
    /// </summary>
    internal static string? SchemeOf(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        var colon = address.IndexOf(':');
        if (colon < 2 || !char.IsAsciiLetter(address[0])) return null;
        for (var i = 1; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(address[i]) && address[i] is not ('+' or '-' or '.')) return null;
        return address[..colon].ToLowerInvariant();
    }

    /// <summary>A link that would leave the browser for another app.</summary>
    internal static bool IsAppLink(string? address) => SchemeOf(address) is { } scheme && !BrowserSchemes.Contains(scheme);

    /// <summary>An app link Exo may offer to open: not a refused scheme, and one plain address (no spaces, quotes or
    /// control characters, at most 4096 characters) that Windows' shell can't read as anything else.</summary>
    internal static bool MayOpen(string? address) =>
        IsAppLink(address) && !Refused.Contains(SchemeOf(address)!) && address!.Length <= 4096 &&
        !address.Any(c => c <= ' ' || c is '"' or '\u007f');

    // Windows' own name for the app that takes the scheme's links ("Spotify", "Zoom"), as its Default apps shows it.
    private static unsafe string? AppName(string scheme)
    {
        const uint isProtocol = 0x1000;   // ASSOCF_IS_PROTOCOL
        const int friendlyAppName = 4;    // ASSOCSTR_FRIENDLYAPPNAME
        var buffer = stackalloc char[260];
        uint size = 260;
        try
        {
            return AssocQueryString(isProtocol, friendlyAppName, scheme, null, buffer, ref size) == 0 &&
                new string(buffer) is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int AssocQueryString(uint flags, int what, string association, string? extra, char* output, ref uint size);
}

/// <summary>
/// How often a page may ask to open another app: the third ask within 10 seconds is refused, and so is every later one,
/// without asking, until the page navigates.
/// </summary>
internal sealed class AppAskLimit
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private const int Most = 2;
    private readonly Queue<DateTime> _asks = new();

    /// <summary>Refusing every ask until the page navigates.</summary>
    internal bool Refused { get; private set; }

    /// <summary>An ask at <paramref name="now"/>; false when it is refused.</summary>
    internal bool Ask(DateTime now)
    {
        if (Refused) return false;
        while (_asks.Count > 0 && now - _asks.Peek() >= Window) _asks.Dequeue();
        if (_asks.Count >= Most)
        {
            Refused = true;
            _asks.Clear();
            return false;
        }
        _asks.Enqueue(now);
        return true;
    }

    /// <summary>The page loaded a new document: it may ask again.</summary>
    internal void Navigated()
    {
        Refused = false;
        _asks.Clear();
    }
}
