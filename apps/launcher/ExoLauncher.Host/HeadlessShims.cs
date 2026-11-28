global using ExoLauncher.Helpers;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher
{
    public static class App
    {
        public static AppServices Services { get; } = new();
        public static MainWindow? MainAppWindow { get; set; }
    }

    public sealed class MainWindow
    {
        internal const string CaptureMediaHost = "captures.exo-launcher.local";
        internal const string CaptureThumbHost = "capture-thumbs.exo-launcher.local";

        internal static string CaptureData => Path.Combine(PathHelper.AppDataDir, "captures");
        internal static string CaptureThumbs => Path.Combine(CaptureData, "thumbs");
        internal static string CaptureSettingsPath => Path.Combine(CaptureData, "settings.json");
        internal static string CaptureIndexPath => Path.Combine(CaptureData, "index.json");
        internal static string DefaultCaptureFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Exo Captures");

        internal sealed class CaptureSettings
        {
            public int ReplaySeconds { get; set; } = 60;
            public string ReplayMode { get; set; } = "games";
            public bool Cursor { get; set; } = true;
            public string SaveReplayKeys { get; set; } = "Ctrl+Alt+F10";
            public string RecordKeys { get; set; } = "Ctrl+Alt+F9";
            public string SnipKeys { get; set; } = "Ctrl+Alt+F8";
            public bool PrintScreen { get; set; }
            public string AfterSnip { get; set; } = "edit";
            public string? Folder { get; set; }
            public int KeepGb { get; set; }
            public int KeepDays { get; set; }
            public bool AutoShrink { get; set; } = true;
            public bool CallVoices { get; set; }
            public List<string> Favourites { get; set; } = new();
        }

        internal sealed class CaptureMeta
        {
            public string? GameId { get; set; }
            public double DurationMs { get; set; }
            public string? Shrink { get; set; }
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
            public bool Derived { get; set; }
        }

        internal sealed record CaptureItem(
            string Id,
            string Kind,
            string Game,
            string? GameId,
            string Name,
            string Created,
            long Size,
            double? DurationMs,
            bool Favourite,
            string Url,
            string? Thumb,
            bool Derived);

        internal sealed record CaptureUpload(
            Services.DiscordClient.DiscordRest.Upload? File,
            string? Error,
            CaptureItem? Made);

        public bool WindowIsActive => false;
        public DispatcherQueue DispatcherQueue { get; } = DispatcherQueue.GetForCurrentThread()
            ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        internal nint WindowHandle => 0;
        internal CaptureAddon? StreamCapture => App.Services.HeadlessCapture.StreamCapture;

        internal void CloseWindow() { }
        public void HideForGameplay() { }
        internal void RestoreAndActivate(string? reason = null) => RuneHostSession.RequestActivate();
        internal void RefreshTaskbarBadge() { }
        internal void RefreshGameState() { }
        internal void RefreshSettings() { }
        internal void SetWindowTitle(string title) { }
        internal void RevealPainted() { }
        internal bool IsMaximized => false;
        internal bool ToggleMaximize() => false;
        internal void MinimizeWindow() { }
        internal void SetChromeLayout(Windows.Graphics.RectInt32[]? interactiveRegions, Windows.Graphics.RectInt32? maximizeButton) { }
        internal void CaptureMicChanged() { }
        internal Task<object> PrepareConnectedRoomsAsync() => Task.FromResult<object>(new { ok = true });
        internal object FriendsBadge() => new { count = 0 };
        internal Task<bool> ConfirmAsync(string title, string body, string action, bool danger = false) => Task.FromResult(false);
        internal Task<bool> ConfirmNativeAsync(string title, string body, string action, bool danger = false) => Task.FromResult(false);
        internal Task<bool> ShadersVerifyAsync(string engineId, CancellationToken ct) => Task.FromResult(false);
        internal object CaptureStatus() => new { ok = false, message = "Capture runs through HeadlessCaptureService." };
        internal Task<object> CaptureStatusAsync() => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<object> CaptureActAsync(string? action) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<object> CaptureActAsync(string action, JsonElement p, bool hasParams) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal object CaptureList(string? gameId, int? limit) => new { ok = true, items = Array.Empty<object>() };
        internal Task<object> CaptureItemAsync(string? id, string? action) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<object> CaptureEditAsync(string? id, string? preset, double? startMs, double? endMs) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal object CaptureReelStart(JsonElement p) => new { ok = false, message = "Capture runs through HeadlessCaptureService." };
        internal object CaptureReelCancel() => new { ok = true };
        internal Task<object> CaptureTextAsync(string? id, double? x, double? y, double? width, double? height) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<CaptureUpload> CaptureUploadAsync(string? id, double? startMs, double? endMs, CancellationToken ct) =>
            Task.FromResult(new CaptureUpload(null, "Capture runs through HeadlessCaptureService.", null));
        internal Task<object> CaptureSaveImageAsync(string? id, string? base64) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<object> CaptureSettingsAsync(JsonElement p, bool hasParams) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<object> CaptureSetSettingsAsync(JsonElement p) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal Task<object> CaptureReelAsync(JsonElement p, bool hasParams) => Task.FromResult<object>(new { ok = false, message = "Capture runs through HeadlessCaptureService." });
        internal void CancelCaptureReel() { }
        internal void ShowOverlay(string? text = null) { }

        internal static Dictionary<string, GameRunState> RunningLibraryGames()
        {
            var installed = App.Services.Library.PeekCachedLibrary()
                .Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path))
                .ToArray();
            return installed.Length == 0
                ? new Dictionary<string, GameRunState>()
                : App.Services.Launcher.GetGameRunStates(installed, underRootOnly: true)
                    .Where(row => row.Value.IsRunning)
                    .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        }

        internal static string? SdrGammaReplaced(SettingsService settings, string devicePath) =>
            HdrHostService.SdrGammaReplaced(settings, devicePath);

        internal static bool RecycleFile(string path) => DeletionNative.SendToRecycleBin(path);
        internal static void ReloadAfterDriverChange() { }
        internal static Task ReloadAfterDriverInstallAsync() => Task.CompletedTask;
        internal static void ClearPageCard(WebView2 view) { }
        internal static void ShowPageError(WebView2 view, string cause, Action retry) { }
        internal static Task<bool> AskOverPage(WebView2 view, string question, string action) => Task.FromResult(false);

        internal static bool WantsHdr(string? mode, bool supports) =>
            supports && string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase);

        internal static bool CaptureHoldsCall(JsonNode? state) =>
            state?["recording"]?.GetValue<bool>() == true;
    }

    internal sealed class FpsOverlay : IDisposable
    {
        internal readonly record struct Line(int Fps, string? Items);
        internal readonly record struct Look(Line? Line, uint NextMs);

        internal FpsOverlay(Func<Look> read, Func<string> position) { }

        internal void Refresh() { }

        internal static string PositionOf(string? raw) => raw?.Trim().ToLowerInvariant() switch
        {
            "top-left" or "topleft" => "top-left",
            "bottom-left" or "bottomleft" => "bottom-left",
            "bottom-right" or "bottomright" => "bottom-right",
            _ => "top-right",
        };

        public void Dispose() { }
    }

    internal static class AchievementBanner
    {
        internal static void Show(object unlock) { }
        internal static void Preview() { }
        internal static void Close() { }
    }
}

namespace ExoLauncher.Helpers
{
    public static class HangWatchdog
    {
        internal static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
        internal static bool IsArmed => false;
        internal static bool PageBeatSeen => false;
        internal static void PageAttached() { }
        internal static void PageBeat() { }
        internal static void PageBeat(bool visible) { }
        internal static void PageVisible(bool visible) { }
        internal static void Refresh() { }
    }

    internal static class ReportSmokeHooks
    {
        internal static bool Enabled => false;
        internal static bool AllowSends => false;
        internal static string? HangKindOverride() => null;
        internal static void OnCrashReport(string kind, string reason) { }
        internal static void OnSubmitted(string kind, string reason, string source) { }
        internal static bool InterceptSend(string kind, string reason, string source) => false;
    }

    internal static class ExoConnectedTheme
    {
        internal static (int Width, int Height) AskedSize(CoreWebView2WindowFeatures features)
        {
            var width = features.HasSize && features.Width > 200 ? (int)Math.Min(features.Width, 1600) : 540;
            var height = features.HasSize && features.Height > 200 ? (int)Math.Min(features.Height, 1000) : 720;
            return (width, height);
        }
    }
}

namespace ExoLauncher.Services.DiscordClient
{
    internal enum NotifyRoute { None, Sound, InApp, Windows }

    internal static class DiscordNotifier
    {
        internal static event Action<string?, string, string?, string?>? Opened;
        internal static event Action? AppOpened;

        internal static NotifyRoute Route(bool exoInFront, string? viewing, string channelId, bool gameQuiet, bool streamerQuiet, bool desktop) =>
            exoInFront && viewing == channelId ? NotifyRoute.None
            : gameQuiet ? NotifyRoute.None
            : streamerQuiet || !desktop ? NotifyRoute.Sound
            : exoInFront ? NotifyRoute.InApp
            : NotifyRoute.Windows;

        internal static string? Where(string? place, string? server) =>
            place is null ? server : server is null ? place : place + " · " + server;

        internal static void ShowApp(string title, string body)
        {
            _ = AppOpened;
            AppLog.Debug($"Headless notice: {title} — {body}");
        }

        internal static void ShowCall(string title, string body, string channelId)
        {
            _ = Opened;
            AppLog.Debug($"Headless call notice ({channelId}): {title}");
        }

        internal static void Show(DiscordCache.Notification n) =>
            AppLog.Debug($"Headless Discord notice ({n.ChannelId}): {n.Author}");

        internal static void Clear(string channelId, string? messageId) { }
    }
}

namespace ExoLauncher.Services
{
    public sealed class WebViewHostTransport : IHostTransport
    {
        public event Action<string>? MessageReceived { add { } remove { } }
        public event Action? SessionReset { add { } remove { } }
        public WebViewHostTransport(CoreWebView2 core, DispatcherQueue queue) { }
        public void PostJson(string json) { }
        public void PostBinaryFrame(string metaJson, byte[] frame) { }
        public void Detach() { }
        public void Dispose() { }
    }

    internal sealed class StoreAuthWindow
    {
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

        internal StoreAuthWindow(Options options) => _options = options;

        internal event Action? Closed;

        internal Task StartAsync()
        {
            AppLog.Warn($"{_options.LogName}: interactive WebView2 StoreAuthWindow is unavailable in headless host; use OS-native OAuth.");
            Closed?.Invoke();
            return Task.CompletedTask;
        }

        internal void Close() { }

        internal static bool HostIs(Uri uri, string suffix) =>
            uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

        internal static bool IsIdentityProvider(Uri uri) =>
            HostIs(uri, "google.com") || HostIs(uri, "accounts.google.com") ||
            HostIs(uri, "apple.com") || HostIs(uri, "appleid.apple.com") ||
            HostIs(uri, "microsoftonline.com") || HostIs(uri, "live.com") ||
            HostIs(uri, "xboxlive.com") || HostIs(uri, "steamcommunity.com") ||
            HostIs(uri, "playstation.com") || HostIs(uri, "nintendo.com") ||
            HostIs(uri, "facebook.com") || HostIs(uri, "hcaptcha.com") ||
            HostIs(uri, "arkoselabs.com") || HostIs(uri, "cloudflare.com");

        internal static string CleanUserAgent(string ua) => ua;

        internal static string Describe(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(unknown)";
    }
}

namespace Microsoft.UI.Xaml
{
    public sealed class Application
    {
        public static Application Current { get; } = new();
        public void Exit() { }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public sealed class WebView2
    {
        public CoreWebView2? CoreWebView2 => null;
        public Uri? Source { get; set; }
        public Task<string> ExecuteScriptAsync(string script) => Task.FromResult("null");
    }
}

namespace Microsoft.Windows.AppLifecycle
{
    public enum AppRestartFailureReason
    {
        RestartPending,
        NotInForeground,
        InvalidUser,
        Other,
    }

    public static class AppInstance
    {
        public static AppRestartFailureReason Restart(string arguments)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return AppRestartFailureReason.Other;
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
                return AppRestartFailureReason.RestartPending;
            }
            catch
            {
                return AppRestartFailureReason.Other;
            }
        }
    }
}
