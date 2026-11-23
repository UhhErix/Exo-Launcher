using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

internal static class MusicEngineDiet
{
    internal static readonly TimeSpan SuspendDelay = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReleaseDelay = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan DeferredRetryDelay = TimeSpan.FromSeconds(15);

    internal static (int Width, int Height) HiddenWindowSize(string? service) =>
        service == "spotify" ? (1024, 720) : (320, 240);

    internal static bool ServiceMatchesSelection(string? selectedService, string? requestedService) =>
        MusicWebPolicy.IsService(requestedService)
        && (selectedService is null || string.Equals(selectedService, requestedService, StringComparison.Ordinal));

    internal static bool CanSuspend(
        bool hasHost,
        bool isPlayingAudio,
        bool isMediaSessionPlaying,
        bool hasPopouts,
        bool signInActive) =>
        hasHost && !isPlayingAudio && !isMediaSessionPlaying && !hasPopouts && !signInActive;

    internal static bool ShouldDeferIdleRetry(
        bool hasHost,
        bool isPlayingAudio,
        bool isMediaSessionPlaying,
        bool hasPopouts,
        bool signInActive) =>
        hasHost && !isPlayingAudio && (isMediaSessionPlaying || hasPopouts || signInActive);

    internal static bool CanRelease(
        bool hasHost,
        bool isAsleep,
        bool isPlayingAudio,
        bool isMediaSessionPlaying,
        bool hasPopouts,
        bool hasSignInWindow) =>
        hasHost && isAsleep && !isPlayingAudio && !isMediaSessionPlaying && !hasPopouts && !hasSignInWindow;

    internal static IReadOnlyList<(string Pattern, CoreWebView2WebResourceContext Context)> FilterPatternsForService(string service) =>
        MusicWebPolicy.ResourceDietFilterPatterns(service);

    internal static bool ShouldBlockResource(string service, string? uri, CoreWebView2WebResourceContext context, bool popup) =>
        MusicWebPolicy.ShouldBlockHiddenResource(service, uri, context, popup);
}
