namespace ExoLauncher.Helpers;

/// <summary>
/// Single WebView2 user-data folder and browser-argument set for the chrome
/// WebView. Nothing native may use a sidecar browser profile.
/// </summary>
internal static class WebViewHostProfile
{
    internal static string UserDataFolderName
    {
        get
        {
#if DEBUG
            return "webview-debug";
#else
            return "webview";
#endif
        }
    }

    /// <summary>
    /// The shared folder (L1): one WebView2 process group, each program on its own profiles. Launcher moves its own folder
    /// here once, whole, so its cookies stay readable (their key lives in the folder's Local State), and then writes
    /// <see cref="SharedMarker"/>; anything else joins only once the marker names the same version. People's sign-ins live
    /// here, so the folder, its marker and its arguments stay as they are. Debug builds and test copies keep a folder of their own.
    /// </summary>
    internal static string SharedUserDataFolder =>
        Environment.GetEnvironmentVariable(SharedFolderOverrideVariable) is { Length: > 0 } test ? test
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "WebView2");

    /// <summary>Test copies only: a shared folder of their own (e.g. %LOCALAPPDATA%\Exo\WebView2-test), so a crash test
    /// never touches the user's live group.</summary>
    internal const string SharedFolderOverrideVariable = "EXO_SHARED_WEBVIEW_DIR";

    internal static string SharedMarker => Path.Combine(SharedUserDataFolder, "exo-shared.json");

    /// <summary>The spec's version: every app on the shared folder uses exactly <see cref="SharedArguments"/>.</summary>
    internal const int SharedVersion = 1;

    /// <summary>The folder Launcher used before sharing, and still uses when sharing is off.</summary>
    internal static string OwnUserDataFolder => Path.Combine(PathHelper.AppDataDir, UserDataFolderName);

    /// <summary>Exactly what every app on the shared folder passes (spec v1). Launcher's own arguments with no test
    /// switches must equal it (WebViewHostProfileTests), or WebView2 refuses anything else joining the folder.</summary>
    internal const string SharedArguments = "--disable-features=msWebOOUI,msPdfOOUI,SpareRendererForSitePerProcess,"
        + "AIPromptAPI,AISummarizationAPI,AIWriterAPI,AIRewriterAPI,AIProofreadingAPI,AIEmbeddingsAPI,"
        + "LanguageDetectionAPI,TranslationAPI,msEdgeLanguageDetectionAPI,msEdgeTranslationAPI --process-per-site";

    /// <summary>Whether arguments Exo would start WebView2 with can join the shared folder: the spec's two forms only,
    /// plain, or one test port (4 or 5 digits) that every app in the group gets, then the spec's string exactly.</summary>
    internal static bool MayJoinShared(string arguments) =>
        arguments == SharedArguments
        || System.Text.RegularExpressions.Regex.IsMatch(arguments, "^--remote-debugging-port=[0-9]{4,5} " + System.Text.RegularExpressions.Regex.Escape(SharedArguments) + "$");

    /// <summary>Set once at start by <see cref="WebViewEnvironmentFactory"/>.</summary>
    internal static bool Shared { get; set; }

    internal static string UserDataFolder => Shared ? SharedUserDataFolder : OwnUserDataFolder;

    // Every music provider shares one named profile inside the
    // shell's user data folder so WebView2 boots a single environment.
    // WebView2 names the on-disk folder WV2Profile_<ProfileName>.
    internal const string EmbeddedProfileName = "embedded";
    internal static string EmbeddedProfileFolder =>
        Path.Combine("EBWebView", "WV2Profile_" + EmbeddedProfileName);

    internal static string AdditionalBrowserArguments() => AdditionalBrowserArguments(
        Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"),
        Environment.GetEnvironmentVariable("EXO_CDP")
            ?? Environment.GetEnvironmentVariable("AETHER_CDP"),
        Environment.GetEnvironmentVariable("EXO_CDP_PORT"));

    internal static string AdditionalBrowserArguments(
        string? webView2AdditionalBrowserArguments,
        string? exoCdp,
        string? exoCdpPort)
    {
        var extra = (webView2AdditionalBrowserArguments ?? "").Trim();
#if DEBUG
        // Debug builds only: a shipped Exo never opens a debugging port from EXO_CDP.
        // The E2E run sets WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS itself, which WebView2 reads directly.
        if (!ContainsSwitch(extra, "remote-debugging-port") && CdpRequested(exoCdp))
        {
            var port = int.TryParse(exoCdpPort, out var n) && n is >= 1024 and <= 65535 ? n : 9229;
            extra = Append(extra, "--remote-debugging-port=" + port);
        }
#endif

        // Same-site pages share a renderer; a hung provider page still
        // cannot take the shell's renderer down. Drop Edge mini-menu / PDF chrome.
        // No spare renderer: the shell is one site, and a warm spare costs ~50 MB for nothing.
        // Wheel scrolling stays Chromium's own, smoothed on the compositor thread.
        // No built-in AI for the music pages (Edge gates translation and language detection under its own msEdge names):
        // asking for them hangs a page, and what they answer tells browsers apart.
        if (!ContainsSwitch(extra, "disable-features"))
            extra = Append(extra, "--disable-features=msWebOOUI,msPdfOOUI,SpareRendererForSitePerProcess,"
                + "AIPromptAPI,AISummarizationAPI,AIWriterAPI,AIRewriterAPI,AIProofreadingAPI,AIEmbeddingsAPI,"
                + "LanguageDetectionAPI,TranslationAPI,msEdgeLanguageDetectionAPI,msEdgeTranslationAPI");
        if (!ContainsSwitch(extra, "process-per-site"))
            extra = Append(extra, "--process-per-site");

        return extra;
    }

    private static bool CdpRequested(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSwitch(string arguments, string name) =>
        arguments.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static string Append(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        return left + " " + right;
    }
}
