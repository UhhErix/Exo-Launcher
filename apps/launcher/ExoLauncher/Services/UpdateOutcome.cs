using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// How the last update ended, as the installer's scripts leave it beside the install (update-result.json, update-error.log).
/// Without this the app that comes back after a failed or rolled-back update says nothing, and offers the same version
/// again.
/// </summary>
internal static class UpdateOutcome
{
    internal const string ResultFile = "update-result.json";
    internal const string ErrorFile = "update-error.log";

    /// <summary>The one sentence for an update that did not take: that version is not offered again by itself.</summary>
    internal const string DidNotInstall = "The update didn't install. Exo will try the next one.";

    /// <summary>What to tell the user about an update that did not take; null when it did, or there was none.</summary>
    internal static string? Say(string? resultJson, string? errorText) => Failed(resultJson, errorText).Failed ? DidNotInstall : null;

    private static (string? Status, string? Version) Parse(string? resultJson)
    {
        string? status = null, version = null;
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var document = JsonDocument.Parse(resultJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                }
            }
            catch (JsonException) { /* an unreadable result says nothing; the log file may */ }
        }
        var named = version is { Length: > 0 and <= 32 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? version.TrimStart('v') : null;
        return (status, named);
    }

    /// <summary>"checking-startup" is also what the new build reads while its own check is still under way: it counts only for a
    /// build older than the update (the update never came up, and this is the old one again).</summary>
    private static bool Stuck(string? status, string? named, string? runningVersion) =>
        status == "checking-startup" && runningVersion is not null && named is not null &&
        VersionNumber.Compare(AppUpdateService.Normalize(runningVersion), AppUpdateService.Normalize(named)) < 0;

    /// <summary>
    /// Whether the update before this start did not take (rolled back, stopped half way, or still "checking" because the new
    /// build never answered), and the version it was for when the notes say. An update that succeeded, or no notes, is not.
    /// </summary>
    internal static (bool Failed, string? Version) Failed(string? resultJson, string? errorText, string? runningVersion = null)
    {
        var (status, named) = Parse(resultJson);
        if (status == "succeeded") return (false, named);
        return (status is "rolled-back" or "failed-preserved" || Stuck(status, named, runningVersion) || !string.IsNullOrWhiteSpace(errorText), named);
    }

    /// <summary>
    /// Whether that version is not offered again by itself. Only an update that came up and went back counts: rolled back, or
    /// still "checking" when an older build reads it. An error note alone (the engine failed before it swapped anything, for
    /// instance because the folder was in use) or a half-stopped result leaves the version to the kept installer's start
    /// counter, and a failure that names no version skips nothing.
    /// </summary>
    internal static (bool Skip, string? Version) Verdict(string? resultJson, string? runningVersion = null)
    {
        var (status, named) = Parse(resultJson);
        return (named is not null && (status == "rolled-back" || Stuck(status, named, runningVersion)), named);
    }

    /// <summary>The installer's notes in <paramref name="root"/>, read and left in place (Take removes them).</summary>
    internal static (bool Skip, string? Version) Peek(string root, string runningVersion)
    {
        string? Read(string name)
        {
            var path = Path.Combine(root, name);
            try { return File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        return Verdict(Read(ResultFile), runningVersion);
    }

    /// <summary>
    /// Reads and removes the installer's notes in <paramref name="root"/> (the install's folder). The scripts' own words go to
    /// the log, never to the page: they name paths.
    /// </summary>
    internal static string? Take(string root) => TakeOutcome(root).Said;

    /// <summary>Take, with the version the failed update was for (null when the notes don't say).</summary>
    internal static (string? Said, string? Version) TakeOutcome(string root)
    {
        string? result = null, error = null;
        foreach (var (name, set) in new (string, Action<string?>)[] { (ResultFile, text => result = text), (ErrorFile, text => error = text) })
        {
            var path = Path.Combine(root, name);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) continue;
                set(File.ReadAllText(path));
                DeletionGate.DeleteFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* read again next start */ }
        }
        var (failed, _) = Failed(result, error);
        var said = failed ? DidNotInstall : null;
        if (said is not null) AppLog.Warn($"Update: {said} Installer said: {Trim(error) ?? Trim(result)}");
        return (said, failed && Verdict(result).Skip ? Verdict(result).Version : null);
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Length > 600 ? text[..600] : text;
}
