using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>What one send of a report came to (<see cref="CrashReport.SendOnceAsync"/>).</summary>
internal enum SendOutcome
{
    /// <summary>The service answered 202: stored.</summary>
    Accepted,
    /// <summary>Nothing posted: the daily limits, or this error's repeat today, refused it.</summary>
    Refused,
    /// <summary>The POST failed (an exception or a non-202 answer); the reservation was given back.</summary>
    Failed,
    /// <summary>Nothing posted and nothing reserved: a disposable (test) host, without the DEBUG smoke allowance.</summary>
    Blocked,
}

/// <summary>
/// Crash reports, sent by every install (owner, 2026-11-09: essential, no switch; PRIVACY.md; cloud/reports). A report is the
/// <see cref="Payload"/> and nothing else: the app, its version, the Windows build, an error's type and a log excerpt.
/// The excerpt is <see cref="AppLog.ReportFor"/>'s text with more taken out here, on this PC, before anything leaves it:
/// file paths, URLs (their hosts and queries), host and machine names, IP addresses, emails, ids and tokens. A line that
/// still looks personal after that is replaced whole. The service refuses what this lets through
/// (cloud/reports/src/validate.js), so the two lists match.
/// </summary>
public static partial class CrashReport
{
    internal const string Endpoint = "https://reports.getexo.app/v1/report";
    internal const int MaxExcerpt = 8192;

    /// <summary>What a report holds. Nothing else is ever put in one. <see cref="InstallId"/> rides along, so Delete my data removes it.</summary>
    public sealed record Payload(string App, string Version, string OsBuild, string ErrorType, string Excerpt, string? InstallId = null);

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]{1,15}://[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    // Windows drive, %USERPROFILE% or <user> followed by a path; a UNC share; a Unix home folder; a drive written with a slash.
    // A path runs to the end of its quote, bracket or line, spaces included ("Elden Ring"): stopping at a space would leave
    // the rest of the folder name in the text. <user> and <host> (already replaced) are part of it, so < and > are not stops.
    [GeneratedRegex(@"(?:%[A-Za-z_]+%\\|<user>\\|\b[A-Za-z]:\\|\b[A-Za-z]:/)[^\r\n""'|()\[\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\\\\[^\s\\""'<>|]+(?:\\[^\r\n""'|()\[\]]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex SharePath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:home|Users|root)/[^\r\n""'|()\[\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixHome();

    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)+(?:com|net|org|io|gg|app|dev|local|lan|gl|tv|de|co|uk|us|xyz|ru|jp|me|edu|gov)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostName();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidId();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlChar();

    [GeneratedRegex(@"\d{9,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9_]{0,70}(?:Exception|Error)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorName();

    [GeneratedRegex(@"^\d{1,4}(?:\.\d{1,4}){2}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionHead();

    /// <summary>What the service also refuses (cloud/reports/src/validate.js › PERSONAL). A line matching any is removed whole.</summary>
    private static readonly Regex[] Personal =
    [
        WindowsPathSweep(), SharePathSweep(), UnixHomeSweep(), UrlSweep(), EmailSweep(),
        new(@"\d{15,}", RegexOptions.CultureInvariant),
        new(@"[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant),
    ];

    [GeneratedRegex(@"[A-Za-z]:\\|\b[A-Za-z]:/", RegexOptions.CultureInvariant)] private static partial Regex WindowsPathSweep();
    [GeneratedRegex(@"\\\\[^\s\\]+\\", RegexOptions.CultureInvariant)] private static partial Regex SharePathSweep();
    [GeneratedRegex(@"(^|[^A-Za-z0-9])/(home|Users)/", RegexOptions.CultureInvariant)] private static partial Regex UnixHomeSweep();
    [GeneratedRegex(@"[a-z][a-z0-9+.-]{1,15}://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UrlSweep();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)] private static partial Regex EmailSweep();

    /// <summary>
    /// The report for an error, as the service takes it. <paramref name="report"/> is <see cref="AppLog.ReportFor"/>'s
    /// text (already free of the Windows user name and profile folder); <paramref name="error"/> names the error's type.
    /// </summary>
    internal static string FitExcerpt(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var lines = text.Split('\n');
        var headLines = new List<string>();
        var idx = 0;
        var headCap = Math.Max(64, maxLength / 2);
        var headLen = 0;
        while (idx < lines.Length)
        {
            var line = lines[idx];
            if (line.Length > 1 && line[0] == '[' && char.IsAsciiDigit(line[1])) break;
            if (headLen + line.Length + 1 > headCap && headLines.Count > 0) break;
            headLines.Add(line);
            headLen += line.Length + 1;
            idx++;
            if (line.StartsWith("Error (", StringComparison.Ordinal))
            {
                while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx]) && !(lines[idx].Length > 1 && lines[idx][0] == '[' && char.IsAsciiDigit(lines[idx][1])))
                {
                    if (headLen + lines[idx].Length + 1 > headCap) break;
                    headLines.Add(lines[idx]);
                    headLen += lines[idx].Length + 1;
                    idx++;
                }
                break;
            }
        }
        var head = string.Join('\n', headLines).TrimEnd('\n');
        if (head.Length == 0 || idx >= lines.Length) return text[..maxLength];
        const string marker = "\n…\n";
        var tailBudget = maxLength - head.Length - marker.Length;
        if (tailBudget <= 0) return head.Length > maxLength ? head[..maxLength] : head;
        var tail = string.Join('\n', lines[idx..]);
        if (tail.Length <= tailBudget) return head + "\n" + tail;
        var cut = tail.Length - tailBudget;
        var nl = tail.IndexOf('\n', cut);
        var kept = nl >= 0 && nl + 1 < tail.Length ? tail[(nl + 1)..] : tail[cut..];
        return head + marker + kept;
    }

    internal static Payload Build(string error, string report, string version, string windows, string userName, string profile, string machineName, string? kind = null)
    {
        var raw = FitExcerpt(report, MaxExcerpt * 4);
        var excerpt = Scrub(raw, userName, profile, machineName);
        if (excerpt.Length > MaxExcerpt) excerpt = FitExcerpt(excerpt, MaxExcerpt);
        excerpt = string.Join('\n', excerpt.Split('\n').Select(line => Personal.Any(p => p.IsMatch(line)) ? "<removed>" : line));
        var type = ClassifyErrorType(error, raw, kind);
        var head = VersionHead().Match(version);
        return new Payload("launcher", head.Success ? head.Value : "0.0.0", OsBuild(windows), type, excerpt);
    }

    private static string ClassifyErrorType(string error, string raw, string? kind)
    {
        if (kind is { Length: > 0 }) return kind;
        if (ErrorName().Match(error) is { Success: true } fromError) return fromError.Value;
        foreach (var line in raw.Split('\n'))
        {
            if (line.Contains("] WARN ", StringComparison.Ordinal) || line.Contains("] INFO ", StringComparison.Ordinal) || line.Contains("] DEBUG ", StringComparison.Ordinal))
                continue;
            if (ErrorName().Match(line) is { Success: true } fromLine) return fromLine.Value;
        }
        return "AppError";
    }

    /// <summary>
    /// Sends one report of a known kind (Hang, WebView2Crash, DiscordGateway, CaptureCrash, StoreFailure, UpdateFailure) with the
    /// recent stripped log lines. Fire and forget: never throws; the client limits in <see cref="SendAsync"/> decide whether it leaves.
    /// </summary>
    public static void Submit(string kind, string what, string area = "")
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var version = AppServices.RunningVersion;
            var windows = Environment.OSVersion.Version.ToString();
            var built = AppLog.ReportFor(what.Length > 2000 ? what[..2000] : what, area, AppLog.RecentLines(), DateTime.UtcNow,
                version, windows, Environment.UserName, profile);
            var payload = Build(what, built, version, windows, Environment.UserName, profile, Environment.MachineName, kind);
            _ = SendAsync(payload);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Crash report ({kind}): not built ({ex.GetType().Name}).");
        }
    }

    /// <summary>The text of one report as JSON, exactly the body <see cref="SendAsync"/> posts.</summary>
    public static string ToJson(Payload payload)
    {
        var json = new JsonObject
        {
            ["app"] = payload.App,
            ["version"] = payload.Version,
            ["osBuild"] = payload.OsBuild,
            ["errorType"] = payload.ErrorType,
            ["excerpt"] = payload.Excerpt,
        };
        if (payload.InstallId is { } install) json["installId"] = install;
        return json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Posts one report. True when the service stored it. Never throws: a report the service refused for load or could not
    /// take is kept for the next start (<see cref="KeepsForLater"/>); one it will never take is written to the log and dropped.
    /// </summary>
    public static async Task<bool> SendAsync(Payload payload, CancellationToken ct = default) =>
        await SendOnceAsync(payload, ct).ConfigureAwait(false) == SendOutcome.Accepted;

    /// <summary>The DEBUG smoke allowance (EXO_REPORT_SMOKE set). Always false in Release, so no smoke code is referenced there.</summary>
    internal static bool SmokeAllowsSends =>
#if DEBUG
        ReportSmokeHooks.AllowSends;
#else
        false;
#endif

    /// <summary>
    /// A disposable host (test instance) sends only SmokeTest-named reports, and only with the smoke allowance; every other
    /// report, from any path, stays on the PC. A normal host sends everything.
    /// </summary>
    internal static bool MayLeave(bool disposable, bool smokeAllowsSends, string errorType) =>
        !disposable || (smokeAllowsSends && errorType.Contains("SmokeTest", StringComparison.Ordinal));

    internal static bool MayLeave(bool disposable, bool testRun, bool customPoster, bool smokeAllowsSends, string errorType) =>
        (!testRun || customPoster) && MayLeave(disposable, smokeAllowsSends, errorType);

    /// <summary>
    /// One send, told apart: <see cref="SendOutcome.Refused"/> when the daily limits (or this error's repeat today) stop it
    /// before anything is posted, <see cref="SendOutcome.Blocked"/> when this is a disposable host without the smoke
    /// allowance (checked before the limiter, so nothing is reserved), <see cref="SendOutcome.Failed"/> when the POST did not
    /// end in a 202. Never throws. <paramref name="pending"/> words the 202 log line for a report sent at start.
    /// </summary>
    internal static async Task<SendOutcome> SendOnceAsync(Payload payload, CancellationToken ct = default, bool pending = false)
    {
        var key = "";
        var reserved = false;
        try
        {

            if (!MayLeave(HostMode.IsDisposable, HostMode.TestRun, Poster != PostBodyAsync, SmokeAllowsSends, payload.ErrorType))
            {
                AppLog.Info("Crash report: not sent by a test instance.");
                return SendOutcome.Blocked;
            }
            // The install ID rides along, so Delete my data removes this report too (SPEC 5.3). With no ID to send, nothing is sent.
            if (Telemetry.InstallId() is not { } install)
            {
                AppLog.Warn("Crash report: not sent; no install ID could be made.");
                return SendOutcome.Failed;
            }
            var body = ToJson(payload with { InstallId = install });
            if (!CrashReportLimiter.TryReserve(payload.ErrorType, CrashReportLimit.SignatureOf(payload.Excerpt), DateTime.UtcNow, out key, payload.Version))
            {
                AppLog.Info("Crash report: held by the daily limit.");
                return SendOutcome.Refused;
            }
            reserved = true;
            var status = await Poster(body, ct).ConfigureAwait(false);
            if (status == (int)HttpStatusCode.Accepted)
            {
                // View what's sent shows this report as it was sent, one compact line.
                Telemetry.RecordSent(System.Text.Json.Nodes.JsonNode.Parse(body)!.ToJsonString());
                AppLog.Info(pending ? "Pending crash report sent (202)." : "Crash report: stored (202).");
                return SendOutcome.Accepted;
            }
            AppLog.Warn($"Crash report: the service answered {status}.");
            if (!pending && KeepsForLater(status)) KeepForLater(payload);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Crash report: not sent ({ex.GetType().Name}).");
            // No answer (offline, a timeout) once the POST was on its way: kept, like a refusal for load.
            if (!pending && reserved) KeepForLater(payload);
        }
        // A report that did not arrive does not count against the day.
        if (reserved) CrashReportLimiter.Release(key, DateTime.UtcNow);
        return SendOutcome.Failed;
    }

    /// <summary>
    /// Whether a live report the service did not store is kept for the next start: a limit (429), the service's own failure
    /// (5xx) or no answer (0). Anything else (400, 413, 415, 422) is a report the service will never take, so it is dropped.
    /// A pending report sent at start is not kept again here: its file stays until it is sent or has failed three times.
    /// </summary>
    internal static bool KeepsForLater(int status) => status is 429 or 0 || status >= 500;

    /// <summary>Where a report kept for later goes. The app keeps <see cref="PendingReports.Keep"/>; a test replaces it.</summary>
    internal static Action<Payload> KeepForLater = PendingReports.Keep;

    /// <summary>Posts one report body and returns its status code. The default posts to <see cref="Endpoint"/>; a test replaces it.</summary>
    internal static Func<string, CancellationToken, Task<int>> Poster = PostBodyAsync;

    private static async Task<int> PostBodyAsync(string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(15));
        using var response = await http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    /// <summary>The personal parts out of a report's text: paths, URLs, hosts and machine names, addresses, emails, ids.</summary>
    internal static string Scrub(string text, string userName, string profile, string machineName)
    {
        // Control characters but tab and newline are refused by the service: take them out first, so a report never fails on one.
        text = ControlChar().Replace(text.Replace("\r", ""), "");
        text = AppLog.Personal(AppLog.Redact(text), userName, profile);
        text = ReportNames.Redact(text);
        text = Url().Replace(text, "<url>");
        text = SharePath().Replace(text, "<path>");
        text = WindowsPath().Replace(text, "<path>");
        text = UnixHome().Replace(text, "<path>");
        text = Email().Replace(text, "<email>");
        text = GuidId().Replace(text, "<id>");
        text = IpAddress().Replace(text, "<address>");
        text = HostName().Replace(text, "<host>");
        if (machineName.Length >= 3)
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9-]){Regex.Escape(machineName)}(?![A-Za-z0-9-])", "<host>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return LongNumber().Replace(text, "<id>");
    }

    /// <summary>"10.0.26300.0" as the service takes it: the first four parts, digits only.</summary>
    private static string OsBuild(string windows)
    {
        var parts = windows.Split('.').Where(part => part.Length > 0 && part.All(char.IsAsciiDigit)).Take(4).ToArray();
        return parts.Length >= 2 ? string.Join('.', parts) : "0.0";
    }
}
