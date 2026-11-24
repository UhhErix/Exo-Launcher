using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>
/// One <see cref="CoreWebView2Environment"/> for every MainWindow surface:
/// the shell on the Default profile, music providers on the shared
/// "embedded" profile. GOG/Epic login create their own short-lived environments.
/// </summary>
internal static class WebViewEnvironmentFactory
{
    private static readonly WebViewEnvironmentRefPool<CoreWebView2Environment> Pool = new();

    internal static int ActiveLeaseCount => Pool.ActiveCount;

    internal static CoreWebView2Environment? TryGetExisting() => Pool.TryGetCurrent();

    internal static WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? TryAcquire(CoreWebView2Environment? expected) =>
        Pool.TryAcquire(expected);

    /// <summary>
    /// Drops a cached environment whose browser could not be started so the
    /// next <see cref="AcquireAsync"/> builds a fresh one (used by init retry).
    /// </summary>
    internal static void Reset(CoreWebView2Environment? failed) => Pool.Reset(failed);

    /// <param name="explicitInstalledRuntime">
    /// Retry path after a file-not-found failure: pass the discovered
    /// installed runtime folder explicitly instead of letting the loader
    /// resolve evergreen from the registry.
    /// </param>
    internal static Task<WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease> AcquireAsync(
        bool explicitInstalledRuntime = false,
        CancellationToken ct = default) =>
        Pool.AcquireAsync(
            token => CreateEnvironmentAsync(explicitInstalledRuntime, token),
            env => WebViewEnvironmentLifetime.Release(env, userDataFolder: WebViewHostProfile.UserDataFolder),
            ct);

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(bool explicitInstalledRuntime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Keep WebView state outside the replaceable application tree.
        // Otherwise a short-lived Edge child can hold the previous
        // version's app folder open during an atomic installer swap.
        // Recursive deletes stay off the UI thread; the environment itself
        // is still created on the caller's thread after this await.
        var prepWatch = System.Diagnostics.Stopwatch.StartNew();
        var browser = await Task.Run(() =>
        {
            WebViewHostProfile.Shared = JoinShared();
            AppLog.Info("webview-folder=" + (WebViewHostProfile.Shared ? "shared" : "own"));
            WebViewCacheCleaner.RunIfVersionChanged(ExoLauncher.Services.AppServices.RunningVersion);
            Directory.CreateDirectory(WebViewHostProfile.UserDataFolder);
            return explicitInstalledRuntime
                ? WebViewRuntimeLocator.ResolveExplicitInstalledFolder() ?? WebViewRuntimeLocator.ResolveBrowserExecutableFolder()
                : WebViewRuntimeLocator.ResolveBrowserExecutableFolder();
        }, ct);
        var prepMs = prepWatch.ElapsedMilliseconds;
        _ = Task.Run(WebViewCacheCleaner.RetireLegacyUserDataFolders);
        var folder = WebViewHostProfile.UserDataFolder;
        await WebViewEnvironmentLifetime.WaitForFolderExitAsync(folder, ct);
        // Every surface starts black, not the runtime's default, before its page has drawn.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF000000");
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = WebViewHostProfile.AdditionalBrowserArguments(),
            // Shared: as this folder has always opened (spec v1). WebView2 refuses options that differ from a running group's.
            ExclusiveUserDataFolderAccess = !WebViewHostProfile.Shared,
            AreBrowserExtensionsEnabled = true,
        };
        // null = healthy evergreen. A path is used when the Clients pv still
        // names a version whose Application folder was emptied.
        if (browser is not null)
            AppLog.Info("webview-runtime-folder=" + browser + (explicitInstalledRuntime ? " (explicit retry)" : string.Empty));
        var envWatch = System.Diagnostics.Stopwatch.StartNew();
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: browser,
            userDataFolder: folder,
            options: options);
        var createEnvMs = envWatch.ElapsedMilliseconds;
        AppLog.Info($"PERF startup milestone=webview-env-ready prepMs={prepMs} createEnvMs={createEnvMs} shared={WebViewHostProfile.Shared}");
        return environment;
    }

    /// <summary>
    /// Whether this run uses the shared folder (L1). Only the installed release with its default data folder and no
    /// extra browser arguments shares: debug builds, test copies and CDP runs keep their own. The first time, Launcher's
    /// own folder is moved into place whole (its cookies' key is in its Local State, so a moved profile alone would lose
    /// every sign-in), then the marker is written. Anything unexpected keeps the own folder.
    /// </summary>
    private static bool JoinShared()
    {
#if DEBUG
        return false;
#else
        try
        {
            // A test copy shares only in a test folder of its own, never the user's live group.
            var testFolder = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WebViewHostProfile.SharedFolderOverrideVariable));
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable)) && !testFolder) return false;
            if (!WebViewHostProfile.MayJoinShared(WebViewHostProfile.AdditionalBrowserArguments())) return false;
            var shared = WebViewHostProfile.SharedUserDataFolder;
            var marker = WebViewHostProfile.SharedMarker;
            if (File.Exists(marker))
                return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))?["v"]?.GetValue<int>() == WebViewHostProfile.SharedVersion;
            // Never move into a folder another app made, and never move a folder a running WebView2 still holds.
            if (Directory.Exists(shared) && Directory.EnumerateFileSystemEntries(shared).Any()) return false;
            var own = WebViewHostProfile.OwnUserDataFolder;
            if (Directory.Exists(own))
            {
                if (InUse(own)) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
                if (Directory.Exists(shared)) DeletionGate.DeleteDirectory(shared);
                Directory.Move(own, shared);
                AppLog.Info("webview-folder moved to the shared folder");
            }
            Directory.CreateDirectory(shared);
            File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(new { v = WebViewHostProfile.SharedVersion, args = WebViewHostProfile.SharedArguments }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("webview-folder: shared folder not used: " + ex.Message);
            return false;
        }
#endif
    }

    private static bool InUse(string folder) =>
        System.Diagnostics.Process.GetProcessesByName("msedgewebview2").Any(p =>
        {
            using (p) return HostedProcessClass.QueryCommandLine(p.Id) is not { } line || line.Contains(folder, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Controller options that place a view on the shared named profile used
    /// by every music provider. One profile means one Exo Shield instance and one
    /// renderer per site instead of one per surface.
    /// </summary>
    internal static CoreWebView2ControllerOptions EmbeddedControllerOptions(CoreWebView2Environment environment)
    {
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = WebViewHostProfile.EmbeddedProfileName;
        options.IsInPrivateModeEnabled = false;
        return options;
    }

    private static int _embeddedProfileLogged;

    internal static void LogEmbeddedProfileOnce(CoreWebView2 core)
    {
        if (Interlocked.Exchange(ref _embeddedProfileLogged, 1) == 0)
            AppLog.Info("embedded-webview-profile=" + core.Profile.ProfilePath);
    }
}
