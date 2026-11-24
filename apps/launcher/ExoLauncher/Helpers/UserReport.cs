using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// Settings › About › Report a problem: what the player wrote, the scrubbed log when Attach logs is on, and a contact when
/// they gave one, sent to cloud/reports (<c>/v1/user-report</c>), which files it in the private Exo-Reports repository and
/// answers the issue's number (or a reference while GitHub is away). PRIVACY.md › Report a problem. Only on Send; never
/// kept for later: the player is there to press Send again.
/// </summary>
public static class UserReport
{
    internal const string Endpoint = "https://reports.getexo.app/v1/user-report";
    internal const int MaxMessage = 4000;
    internal const int MaxContact = 200;
    /// <summary>The error the log excerpt is built for: names no room, so the excerpt is the recent warnings and errors.</summary>
    internal const string Heading = "Report a problem";

    /// <summary>What a send came to: the issue's number, or its reference, or the one line the page shows.</summary>
    public sealed record Result(bool Ok, int? Issue = null, string? Ref = null, string? Message = null);

    /// <summary>The log excerpt Attach logs sends, exactly as the form previews it: <see cref="CrashReport.Build"/>'s scrubbed excerpt.</summary>
    internal static string Excerpt(IEnumerable<string> lines, DateTime nowUtc, string version, string windows, string userName, string profile, string machineName)
    {
        var built = AppLog.ReportFor(Heading, "", lines, nowUtc, version, windows, userName, profile);
        return CrashReport.Build(Heading, built, version, windows, userName, profile, machineName).Excerpt;
    }

    /// <summary>
    /// The body posted, and nothing else: app, version, Windows build, the message, the excerpt (scrubbed again here, so a
    /// page cannot send more than the preview), the contact when given, and the install ID so Delete my data removes it.
    /// Null when there is no message.
    /// </summary>
    internal static string? ToJson(string message, string? excerpt, string? contact, string version, string windows, string userName, string profile, string machineName, string? installId)
    {
        message = message.Replace("\r\n", "\n").Trim();
        if (message.Length == 0) return null;
        if (message.Length > MaxMessage) message = message[..MaxMessage];
        message = new string([.. message.Where(c => c is '\n' or '\t' || !char.IsControl(c))]);
        contact = string.Join(' ', (contact ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (contact.Length > MaxContact) contact = contact[..MaxContact];
        contact = new string([.. contact.Where(c => !char.IsControl(c) && c is not '\u2028' and not '\u2029')]);
        // The excerpt goes through the crash report's own scrub again: a path, URL or id the page slipped in is taken out here.
        var payload = CrashReport.Build(Heading, excerpt ?? "", version, windows, userName, profile, machineName);
        var json = new JsonObject
        {
            ["app"] = "launcher",
            ["version"] = payload.Version,
            ["osBuild"] = payload.OsBuild,
            ["message"] = message,
        };
        if (!string.IsNullOrWhiteSpace(excerpt)) json["excerpt"] = payload.Excerpt;
        if (contact.Length > 0) json["contact"] = contact;
        if (installId is not null) json["installId"] = installId;
        return json.ToJsonString();
    }

    /// <summary>What the service's answer means for the player: the issue or reference on 202, a plain line otherwise.</summary>
    internal static Result Read(int status, string body) => status switch
    {
        (int)HttpStatusCode.Accepted => Parsed(body),
        429 => new(false, Message: "Too many reports right now. Try again in a minute."),
        422 => new(false, Message: "The log still held something personal, so nothing was sent. Turn off Attach logs and send again."),
        400 or 413 => new(false, Message: "Exo's report service didn't take this report. Shorten it and try again, or use Open on GitHub."),
        _ => new(false, Message: "Couldn't reach Exo's report service. Check your connection and try again, or use Open on GitHub."),
    };

    private static Result Parsed(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node?["issue"]?.GetValue<int>() is int issue and > 0) return new(true, Issue: issue);
            if (node?["ref"]?.GetValue<string>() is { Length: > 0 and <= 16 } reference && reference.All(char.IsAsciiHexDigit)) return new(true, Ref: reference);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return new(true);
    }

    /// <summary>Posts one body; returns the status code and the answer's text. A test replaces it.</summary>
    internal static Func<string, CancellationToken, Task<(int Status, string Body)>> Poster = PostAsync;

    private static readonly Lazy<HttpClient> SharedHttp = new(() => ExoHttp.Create(TimeSpan.FromSeconds(30), new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    }));

    private static async Task<(int, string)> PostAsync(string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SharedHttp.Value.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Builds and sends one report. Never throws.</summary>
    public static async Task<Result> SendAsync(string message, string? excerpt, string? contact, string version, CancellationToken ct = default)
    {
        try
        {
            if (HostMode.IsDisposable) return new(false, Message: "A test copy of Exo doesn't send reports.");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var body = ToJson(message, excerpt, contact, version, Environment.OSVersion.Version.ToString(), Environment.UserName, profile, Environment.MachineName, Telemetry.InstallId());
            if (body is null) return new(false, Message: "Write what happened first.");
            var (status, answer) = await Poster(body, ct).ConfigureAwait(false);
            var result = Read(status, answer);
            AppLog.Info(result.Ok ? $"Report a problem: sent ({(result.Issue is { } n ? "issue " + n : "queued")})." : $"Report a problem: the service answered {status}.");
            // View what's sent shows it as it was sent.
            if (result.Ok) Telemetry.RecordSent(body);
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Report a problem: not sent ({ex.GetType().Name}).");
            return Read(0, "");
        }
    }
}
