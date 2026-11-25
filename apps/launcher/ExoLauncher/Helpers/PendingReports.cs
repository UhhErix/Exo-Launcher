using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// Crashes Exo reports itself. A crash that reaches one of the unhandled paths is written, synchronously, as a pending
/// file under <c>pending-reports</c>: the <see cref="CrashReport.Payload"/> fields only (stripped by
/// <see cref="CrashReport.Build"/> at write time, so raw text never lands on disk), plus a send counter. The next start
/// sends the oldest one, through <see cref="CrashReport.SendOnceAsync"/> so the daily limits apply. A file is deleted
/// once it is accepted, refused by the limits, or has failed <see cref="MaxSends"/> times (a blocked send keeps it); at most <see cref="MaxPending"/>
/// files are kept (the oldest are dropped first). Corrupt files are deleted. Nothing here throws.
/// </summary>
internal sealed class PendingReports
{
    public const int MaxPending = 5;
    public const int MaxSends = 3;
    private const string SendsKey = "sends";
    private const string FolderName = "pending-reports";

    private static readonly Lazy<PendingReports> Shared = new(() => new PendingReports(Path.Combine(PathHelper.AppDataDir, FolderName)));

    private readonly string _directory;
    private readonly Func<DateTime> _clock;
    private readonly Func<Identity> _identity;
    private readonly Func<IEnumerable<string>> _recentLines;
    private int _sequence;

    /// <summary>What a report says about this PC and build (the parts <see cref="CrashReport.Build"/> needs).</summary>
    internal sealed record Identity(string Version, string Windows, string UserName, string Profile, string MachineName);

    internal PendingReports(string directory, Func<DateTime>? clock = null, Func<Identity>? identity = null, Func<IEnumerable<string>>? recentLines = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTime.UtcNow);
        _identity = identity ?? CurrentIdentity;
        _recentLines = recentLines ?? AppLog.RecentLines;
    }

    /// <summary>The handler's one call: records <paramref name="ex"/> as a pending report. Never throws.</summary>
    internal static void Record(Exception? ex) => Shared.Value.Write(ex);

    /// <summary>Starts the next start's send on a background thread, after <paramref name="delay"/>. Never throws.</summary>
    internal static void SendOnLaunch(TimeSpan delay)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    await Shared.Value.SendPendingAsync(payload => CrashReport.SendOnceAsync(payload, pending: true)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Pending crash reports not sent ({ex.GetType().Name}).");
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Pending crash reports not scheduled ({ex.GetType().Name}).");
        }
    }

    /// <summary>Writes one pending file for <paramref name="ex"/> and trims the folder to <see cref="MaxPending"/>. Never throws.</summary>
    internal void Write(Exception? ex)
    {
        try
        {
            AppLog.Info("Recording a pending crash report.");
            if (ex is null) return;
            Add(BuildPayload(ex));
        }
        catch (Exception e)
        {
            try { AppLog.Warn($"Pending crash report not saved ({e.GetType().Name})."); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Keeps a built report (already stripped) as a pending file, for the next start to send, and trims the folder to
    /// <see cref="MaxPending"/>, oldest first. Throws on a write failure; <see cref="Write"/> and <see cref="Keep"/> catch it.
    /// </summary>
    internal void Add(CrashReport.Payload payload)
    {
        var excerpt = payload.Excerpt.Length > CrashReport.MaxExcerpt ? payload.Excerpt[..CrashReport.MaxExcerpt] : payload.Excerpt;
        payload = payload with { Excerpt = excerpt, InstallId = null };
        Directory.CreateDirectory(_directory);
        WriteAtomic(Path.Combine(_directory, FileName(_clock())), ToJson(payload, sends: 0));
        var files = Files();
        for (var i = 0; i < files.Length - MaxPending; i++) TryDelete(files[i]);
    }

    /// <summary>
    /// A live report the service did not take (429, a 5xx, or no answer: CrashReport.KeepsForLater): kept as a pending file
    /// and sent at the next start, as an unhandled crash is, so a limit or an outage never erases it. Never throws.
    /// </summary>
    internal static void Keep(CrashReport.Payload payload)
    {
        try
        {
            Shared.Value.Add(payload);
            AppLog.Info("Crash report kept for the next start.");
        }
        catch (Exception e)
        {
            try { AppLog.Warn($"Crash report not kept ({e.GetType().Name})."); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Sends the oldest pending file through <paramref name="send"/>, one per run, and returns 1 when it was accepted, else 0.
    /// A corrupt file is deleted and skipped; a refused one is deleted; a blocked one stays uncounted; a failed one stays with
    /// its counter raised, and is deleted at <see cref="MaxSends"/>. Never throws.
    /// </summary>
    internal async Task<int> SendPendingAsync(Func<CrashReport.Payload, Task<SendOutcome>> send)
    {
        var accepted = 0;
        try
        {
            foreach (var file in Files())
            {
                CrashReport.Payload payload;
                JsonObject root;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? throw new FormatException("Not an object.");
                    payload = ReadPayload(root);
                    _ = root[SendsKey]!.GetValue<int>();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Pending crash report unreadable ({ex.GetType().Name}); dropped.");
                    TryDelete(file);
                    continue;
                }

                // One send per run, the oldest file. Whatever the answer, the run ends here: the rest wait for the next start.
                var outcome = await SafeSend(send, payload).ConfigureAwait(false);
                if (outcome == SendOutcome.Accepted)
                {
                    accepted++;
                    TryDelete(file);
                    break;
                }
                if (outcome == SendOutcome.Refused)
                {
                    TryDelete(file);
                    break;
                }
                // Blocked (a disposable host without the smoke allowance) keeps the file and counts nothing.
                if (outcome is SendOutcome.Blocked) break;
                var sends = root[SendsKey]!.GetValue<int>() + 1;
                if (sends >= MaxSends)
                {
                    TryDelete(file);
                    break;
                }
                try
                {
                    WriteAtomic(file, ToJson(payload, sends));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Pending crash report not updated ({ex.GetType().Name}).");
                }
                break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Pending crash reports not sent ({ex.GetType().Name}).");
        }
        return accepted;
    }

    /// <summary>The report this exception makes: the exception's own type name, the stripped log lines, the build.</summary>
    internal CrashReport.Payload BuildPayload(Exception ex)
    {
        var id = _identity();
        var type = ex.GetType().Name;
        var message = ex.Message ?? "";
        var what = type + ": " + (message.Length > 2000 ? message[..2000] : message);
        string report;
        try
        {
            report = AppLog.ReportFor(what, "crash", _recentLines(), _clock(), id.Version, id.Windows, id.UserName, id.Profile);
        }
        catch (RegexMatchTimeoutException)
        {
            report = "";
        }
        return CrashReport.Build(type, report, id.Version, id.Windows, id.UserName, id.Profile, id.MachineName);
    }

    private static Identity CurrentIdentity() => new(
        AppServices.RunningVersion,
        Environment.OSVersion.Version.ToString(),
        Environment.UserName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.MachineName);

    /// <summary>Ticks first, then a counter: files sort oldest first even when the clock stands still.</summary>
    private string FileName(DateTime now) => $"{now.Ticks:D19}-{Interlocked.Increment(ref _sequence):D6}.json";

    private string[] Files() =>
        Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];

    private static CrashReport.Payload ReadPayload(JsonObject root) => new(
        root["app"]!.GetValue<string>(),
        root["version"]!.GetValue<string>(),
        root["osBuild"]!.GetValue<string>(),
        root["errorType"]!.GetValue<string>(),
        root["excerpt"]!.GetValue<string>());

    private static string ToJson(CrashReport.Payload payload, int sends) => new JsonObject
    {
        ["app"] = payload.App,
        ["version"] = payload.Version,
        ["osBuild"] = payload.OsBuild,
        ["errorType"] = payload.ErrorType,
        ["excerpt"] = payload.Excerpt,
        [SendsKey] = sends,
    }.ToJsonString();

    private static async Task<SendOutcome> SafeSend(Func<CrashReport.Payload, Task<SendOutcome>> send, CrashReport.Payload payload)
    {
        try
        {
            return await send(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Pending crash report: send threw ({ex.GetType().Name}).");
            return SendOutcome.Failed;
        }
    }

    /// <summary>Temp file, then move: a crash mid-write leaves the old file or none, never half of one.</summary>
    private static void WriteAtomic(string path, string json)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { DeletionGate.DeleteFile(path); } catch { /* best-effort */ }
    }
}
