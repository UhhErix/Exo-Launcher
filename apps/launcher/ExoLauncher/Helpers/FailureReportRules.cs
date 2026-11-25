using System.Net.Http;

namespace ExoLauncher.Helpers;

/// <summary>
/// Which failures earn a crash report (<see cref="CrashReport.Submit"/>). Pure, so the rules are tested without a process
/// or a network. Each caller passes only numbers and fixed text to <see cref="CrashReport.Submit"/>.
/// </summary>
public static class FailureReportRules
{
    public const int CaptureGpuUnavailableExitCode = 78;

    /// <summary>
    /// An Exo Capture exit is a crash when Exo did not stop it itself and it exited with a non-zero code.
    /// </summary>
    public static bool IsCaptureCrash(int exitCode, bool stoppedByExo) =>
        !stoppedByExo && exitCode != 0 && exitCode != CaptureGpuUnavailableExitCode;

    /// <summary>
    /// A store launch that failed for the user: not a stop, not a dependency or offline prompt.
    /// </summary>
    public static bool IsStoreLaunchFailure(bool ok, bool stopRequested, bool needsDependencies, bool offlinePrompt, string? message = null) =>
        !ok && !stopRequested && !needsDependencies && !offlinePrompt && !IsExpectedStoreFailure(message);

    /// <summary>
    /// A store install that failed on its own. A user cancel or pause is not a failure.
    /// </summary>
    public static bool IsStoreInstallFailure(bool ok, string message, bool cancelRequested) =>
        !ok && !cancelRequested && message is not ("Canceled." or "Paused.") && !IsExpectedStoreFailure(message);

    public static bool IsExpectedStoreFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("paused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sign-in", StringComparison.OrdinalIgnoreCase)
            || message.Contains("signed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("offline", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no internet", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("did not answer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not be reached", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is not installed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is updating", StringComparison.OrdinalIgnoreCase)
            || message.Contains("disk space", StringComparison.OrdinalIgnoreCase)
            || message.Contains("free space", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExpectedUserNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var text = message.Trim();
        if (text.Contains("is running, so the update waits", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.StartsWith("Close ", StringComparison.OrdinalIgnoreCase)
            && text.Contains(" first", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.StartsWith("Leave your Discord call first", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static bool IsDiscordGatewayReportable(int? lastCloseCode, bool networkFailure) =>
        !networkFailure && lastCloseCode is not null;

    /// <summary>
    /// A host call from the page that failed in a way nobody planned for: a bug, not an answer. Handlers throw
    /// InvalidOperation and InvalidData with a line meant for the person (a refusal), a cancel or a timeout is the person's
    /// or the network's, and an HTTP failure is some other server's; none of those is reported. Everything else is.
    /// </summary>
    public static bool IsHostCallBug(Exception ex) =>
        ex is not (InvalidOperationException or InvalidDataException or OperationCanceledException or TimeoutException
            or HttpRequestException or System.Net.Sockets.SocketException);

    /// <summary>
    /// What a failed host call's report says: the method and the exception's type, never its message (a message can carry
    /// a path or a name). "library.get: NullReferenceException".
    /// </summary>
    public static string HostCallReason(string method, Exception ex) => $"{method}: {ex.GetType().Name}";

    /// <summary>
    /// What a page error's report says: where the page was (its context is a fixed name in Exo's code) and the error's
    /// first 200 characters. The scrubber takes paths, names and ids out before anything leaves (CrashReport.Build).
    /// </summary>
    public static string PageErrorReason(string? context, string message)
    {
        var text = message.ReplaceLineEndings(" ");
        if (text.Length > 200) text = text[..200];
        return string.IsNullOrEmpty(context) ? text : $"{context}: {text}";
    }

    public static string? SafeUpdateUrl(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return null;
        var host = uri.Host;
        var path = uri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path)) return null;
        return $"{host}{path}";
    }

    public static string? SafeUpdateUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? SafeUpdateUrl(uri) : null;

    /// <summary>
    /// The generic reason for a failed update download, with the HTTP status when the server gave one.
    /// </summary>
    public static string UpdateDownloadReason(Exception ex, Uri? uri = null)
    {
        var baseReason = ex is HttpRequestException { StatusCode: { } status }
            ? $"Update download failed (HTTP {(int)status})"
            : "Update download failed";
        var safeUrl = SafeUpdateUrl(uri);
        if (string.IsNullOrEmpty(safeUrl)) return baseReason;
        return $"{baseReason} on {safeUrl}";
    }
}
