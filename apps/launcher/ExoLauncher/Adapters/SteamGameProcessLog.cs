using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's own record of every game process it starts and stops (logs\gameprocess_log.txt, rotated to
/// gameprocess_log.previous.txt), in the PC's local time. Two uses: an anti-cheat handoff after Windows stops
/// exposing its image path (only with a matching app, start time, and an executable Steam named under this game's
/// installed directory), and the play sessions Exo didn't count (<see cref="Sessions"/>).
/// </summary>
internal sealed partial class SteamGameProcessLog(Func<string>? readLog = null)
{
    internal sealed record Entry(string AppId, int ProcessId, DateTime StartedTrackingUtc, string? Executable);
    internal sealed record Snapshot(IReadOnlyDictionary<int, Entry> Active, IReadOnlyList<Entry> History);
    internal sealed record Session(string AppId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
    // [2026-10-28 22:22:00] AppID 4115450 adding PID 9952 as a tracked process "C:\...\PBZ.exe"   (or no command at all)
    // [2026-10-29 01:04:46] AppID 4115450 no longer tracking PID 9952, exit code 0
    // [2026-10-28 21:26:17] Game 4115450 going away; no longer tracking PID 9804
    // [2026-10-29 01:04:46] Remove 4115450 from running list
    // [2026-10-28 22:21:37] Client version: 1793235415   (Steam started)
    [GeneratedRegex(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (?:AppID (?<app>\d+) (?:(?:adding PID (?<add>\d+) as a tracked process)(?<command>.*)|no longer tracking PID (?<remove>\d+).*)|Game (?<app>\d+) going away; no longer tracking PID (?<remove>\d+).*|Remove (?<gone>\d+) from running list.*|(?<client>Client version:).*)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Event();
    internal static readonly TimeSpan LongestSession = TimeSpan.FromDays(7);
    private readonly object _gate = new();
    private Snapshot _snapshot = new(new Dictionary<int, Entry>(), []);
    private DateTime _refreshAt;
    private (long Length, long WriteTicks) _parsedStamp;
    private string? _steamRoot;

    internal string? Resolve(GameEntry game, int pid, string name, long startedUtcTicks)
    {
        if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget ?? "") ||
            startedUtcTicks <= 0) return null;
        lock (_gate)
        {
            if (DateTime.UtcNow >= _refreshAt)
            {
                _refreshAt = DateTime.UtcNow.AddSeconds(1);
                try
                {
                    var text = readLog is null ? ReadCurrentLogIfChanged() : readLog();
                    if (text is not null) _snapshot = Parse(text);
                }
                catch
                {
                    _snapshot = new(new Dictionary<int, Entry>(), []);
                    _parsedStamp = default;
                }
            }
            return Resolve(_snapshot, game, pid, name, startedUtcTicks, File.Exists);
        }
    }

    internal static string? Resolve(Snapshot snapshot, GameEntry game, int pid, string name,
        long startedUtcTicks, Func<string, bool> exists)
    {
        if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget ?? "") ||
            !game.Installed || string.IsNullOrWhiteSpace(game.Path) || startedUtcTicks <= 0 ||
            !snapshot.Active.TryGetValue(pid, out var current) || current.AppId != game.LaunchTarget)
            return null;
        var started = new DateTime(startedUtcTicks, DateTimeKind.Utc);
        // Log timestamps have second precision. A newly reused PID cannot
        // inherit an old entry, and a long-unrelated process is not a handoff.
        if (current.StartedTrackingUtc < started.AddSeconds(-1) ||
            current.StartedTrackingUtc > started.AddMinutes(2)) return null;
        bool Eligible(Entry entry) => entry.AppId == game.LaunchTarget &&
            !string.IsNullOrWhiteSpace(entry.Executable) &&
            string.Equals(Path.GetFileNameWithoutExtension(entry.Executable), name, StringComparison.OrdinalIgnoreCase) &&
            Services.GameProcessRegistry.IsEligibleExecutableForStop(game, name, entry.Executable) &&
            exists(entry.Executable);
        // A named image outside the install root is contrary evidence.
        if (current.Executable is not null) return Eligible(current) ? current.Executable : null;
        return snapshot.History.LastOrDefault(entry =>
            entry.StartedTrackingUtc >= started.AddMinutes(-5) &&
            entry.StartedTrackingUtc <= started.AddSeconds(1) && Eligible(entry))?.Executable;
    }

    internal static Snapshot Parse(string text)
    {
        var active = new Dictionary<int, Entry>();
        var history = new List<Entry>();
        foreach (var line in text.Split('\n'))
        {
            var match = Event().Match(line.TrimEnd('\r'));
            if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value,
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) continue;
            var appId = match.Groups["app"].Value;
            if (int.TryParse(match.Groups["remove"].Value, out var removed))
            {
                if (active.TryGetValue(removed, out var old) && old.AppId == appId) active.Remove(removed);
                continue;
            }
            if (!int.TryParse(match.Groups["add"].Value, out var pid) || pid <= 0) continue;
            var command = match.Groups["command"].Value.TrimStart(' ', '"');
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            string? executable = null;
            if (end >= 0)
            {
                try
                {
                    var path = command[..(end + 4)];
                    if (Path.IsPathFullyQualified(path)) executable = Path.GetFullPath(path);
                }
                catch { /* malformed command provides no path evidence */ }
            }
            var entry = new Entry(appId, pid, local.ToUniversalTime(), executable);
            active[pid] = entry;
            history.Add(entry);
        }
        return new(active, history);
    }

    /// <summary>
    /// Each app's closed play sessions: from the first process Steam tracks for it to the last one it stops tracking,
    /// in UTC (the log's times are <paramref name="zone"/>'s local time). One still open when Steam started again, or
    /// at the end of the log, has no known end and is left out, as is one over a week long. ReadTo is the last event's
    /// time: everything up to it has been read.
    /// </summary>
    internal static (IReadOnlyList<Session> Sessions, DateTimeOffset? ReadTo) Sessions(string text, TimeZoneInfo zone)
    {
        var sessions = new List<Session>();
        var open = new Dictionary<string, (DateTimeOffset Start, HashSet<int> Pids)>(StringComparer.Ordinal);
        DateTimeOffset? readTo = null;
        foreach (var line in text.Split('\n'))
        {
            var match = Event().Match(line.TrimEnd('\r'));
            if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            DateTimeOffset at;
            try { at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero); }
            catch (ArgumentException) { continue; /* a time the clock skipped */ }
            if (readTo is null || at > readTo) readTo = at;
            if (match.Groups["client"].Success)
            {
                open.Clear();
                continue;
            }
            if (match.Groups["gone"].Success)
            {
                Close(match.Groups["gone"].Value, at);
                continue;
            }
            var app = match.Groups["app"].Value;
            if (int.TryParse(match.Groups["add"].Value, out var added))
            {
                if (!open.TryGetValue(app, out var session)) open[app] = session = (at, new HashSet<int>());
                session.Pids.Add(added);
            }
            else if (int.TryParse(match.Groups["remove"].Value, out var removed) &&
                     open.TryGetValue(app, out var session) && session.Pids.Remove(removed) && session.Pids.Count == 0)
                Close(app, at);
        }
        return (sessions, readTo);

        void Close(string app, DateTimeOffset end)
        {
            if (!open.Remove(app, out var session)) return;
            if (end - session.Start <= LongestSession && SteamProtocol.IsValidAppId(app))
                sessions.Add(new Session(app, session.Start, end));
        }
    }

    /// <summary>The rotated log, then the current one; "" when neither can be read.</summary>
    internal static string ReadWithPrevious(string steamRoot)
    {
        var text = new System.Text.StringBuilder();
        foreach (var name in new[] { "gameprocess_log.previous.txt", "gameprocess_log.txt" })
        {
            try
            {
                using var stream = new FileStream(Path.Combine(steamRoot, "logs", name),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int maxBytes = 4 * 1024 * 1024;
                if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                text.Append(reader.ReadToEnd()).Append('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not there, or not readable */ }
        }
        return text.ToString();
    }

    /// <summary>Null when the log is unchanged since the last parse.</summary>
    private string? ReadCurrentLogIfChanged()
    {
        _steamRoot ??= SteamInstallation.ResolveRoot();
        if (_steamRoot is null) return "";
        var path = Path.Combine(_steamRoot, "logs", "gameprocess_log.txt");
        using var stream = new FileStream(path,
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Length comes from the open handle, so it is exact while Steam appends.
        var stamp = (stream.Length, File.GetLastWriteTimeUtc(path).Ticks);
        if (stamp == _parsedStamp) return null;
        _parsedStamp = stamp;
        const int maxBytes = 1024 * 1024;
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
