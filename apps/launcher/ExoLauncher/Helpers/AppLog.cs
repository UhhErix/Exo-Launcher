using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ExoLauncher.Helpers;

/// <summary>Local rolling diagnostics — no network, no analytics.</summary>
public static partial class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    // PublicationOnly: a failed first lookup is retried instead of cached.
    private static readonly Lazy<string> LogPath = new(
        () => Path.Combine(PathHelper.LogsDir, "app.log"), LazyThreadSafetyMode.PublicationOnly);
    // Guarded by Gate. One append handle for the run instead of an open per line.
    private static FileStream? s_stream;
    private static string? s_streamPath;
    private static readonly Channel<(string Path, string Line)> Pending = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    static AppLog()
    {
        _ = Task.Run(async () =>
        {
            await foreach (var entry in Pending.Reader.ReadAllAsync()) WriteLine(entry.Path, entry.Line);
        });
    }

    internal const int MaxRedactInput = 256 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex BearerPattern = new(
        @"\b((?:authorization:\s*)?bearer\s+)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex SecretAssignmentPattern = new(
        @"\b(token|access[_-]?token|refresh[_-]?token|refresh|password|passwd|secret|client_secret|authorization|api[_-]?key|session|key|steam[_-]?login[_-]?secure|recovery[_ -]?code)\s*[:=]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex QuotedSecretAssignmentPattern = new(
        @"[""'](token|access[_-]?token|refresh[_-]?token|password|passwd|secret|client_secret|authorization|api[_-]?key|session|steam[_-]?login[_-]?secure|recovery[_ -]?code)[""']\s*:\s*(?:""(?:\\.|[^""\\])*""|'[^']*'|[^""'\s,}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex IdentityAssignmentPattern = new(
        @"\b(user[_-]?id|account[_-]?id|steam[_-]?id|friend[_-]?ids?|claimed[_-]?id|external[_-]?id|store[_-]?id|store[_-]?native[_-]?id)\s*[:=]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex EmailPattern = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);

    // Secrets logged with no label: a three-part token (JWT, Discord) and a long opaque
    // run of mixed-case letters and digits (OAuth access tokens). Lowercase hex hashes,
    // which download checks log on purpose, are one case and stay readable.
    private static readonly Regex DottedTokenPattern = new(@"[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex OpaqueRunPattern = new(@"[A-Za-z0-9_+-]{40,}={0,2}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    /// <summary>Crash path is synchronous so process termination cannot discard the final diagnostic.</summary>
    public static void Crash(string message, Exception? exception)
    {
        try
        {
            var detail = $"{message}: {exception}";
            if (detail.Length > 32768) detail = detail[..32768] + " [truncated]";
            detail = Redact(detail);
            WriteLine(LogPath.Value, $"[{DateTime.UtcNow:O}] CRASH {detail}{Environment.NewLine}");
        }
        catch { /* Last-chance diagnostics must not throw. */ }
    }

    internal static void WriteCrashBreadcrumb(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var clean = Redact(line.Replace('\r', ' ').Replace('\n', ' '));
            if (clean.Length > 1024) clean = clean[..1024];
            WriteLine(LogPath.Value, $"[{DateTime.UtcNow:O}] CRASH {clean}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

    internal static void WriteImmediate(string level, string message)
    {
        try
        {
            WriteLine(LogPath.Value, FormatLine(level, message));
        }
        catch { }
    }

    private static string FormatLine(string level, string message)
    {
        var bounded = message.Length > 32768 ? message[..32768] + " [truncated]" : message;
        var redacted = Redact(bounded);
        if (redacted.Length > 32768) redacted = redacted[..32768] + " [truncated]";
        return $"[{DateTime.UtcNow:O}] {level} {redacted}{Environment.NewLine}";
    }

    private static void Write(string level, string message)
    {
        try
        {
            Pending.Writer.TryWrite((LogPath.Value, FormatLine(level, message)));
        }
        catch { /* best-effort */ }
    }

    private static void WriteLine(string path, string line)
    {
        try
        {
            lock (Gate)
            {
                try
                {
                    var stream = OpenLocked(path);
                    // Seeking to the end keeps appends correct if another
                    // process wrote meanwhile, and gives the length without a stat.
                    if (stream.Seek(0, SeekOrigin.End) > MaxBytes)
                    {
                        CloseLocked();
                        var bak = path + ".1";
                        try { DeletionGate.DeleteFile(bak); } catch { /* */ }
                        try { File.Move(path, bak); } catch { /* */ }
                        stream = OpenLocked(path);
                        // A locked backup must not turn failed rotation into unlimited appends.
                        if (stream.Seek(0, SeekOrigin.End) > MaxBytes) return;
                    }

                    stream.Write(Encoding.UTF8.GetBytes(line));
                    stream.Flush();
                }
                catch
                {
                    // Reopen on the next line rather than keep a broken handle.
                    CloseLocked();
                }
            }
        }
        catch
        {
            /* best-effort */
        }
    }

    private static FileStream OpenLocked(string path)
    {
        if (s_stream is not null && string.Equals(s_streamPath, path, StringComparison.OrdinalIgnoreCase))
            return s_stream;
        CloseLocked();
        s_stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        s_streamPath = path;
        return s_stream;
    }

    private static void CloseLocked()
    {
        try { s_stream?.Dispose(); } catch { /* */ }
        s_stream = null;
        s_streamPath = null;
    }

    internal static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var text = message.Length > MaxRedactInput ? message[..MaxRedactInput] + " [truncated]" : message;
        try
        {
            text = QuotedSecretAssignmentPattern.Replace(text, "$1=[redacted]");
            text = BearerPattern.Replace(text, "$1[redacted]");
            text = SecretAssignmentPattern.Replace(text, "$1=[redacted]");
            text = IdentityAssignmentPattern.Replace(text, "$1=[redacted]");
            text = DottedTokenPattern.Replace(text, match =>
            {
                var start = match.Index;
                var end = start + match.Length;
                if ((start > 0 && IsDottedTokenChar(text[start - 1])) || (end < text.Length && IsDottedTokenChar(text[end])))
                    return match.Value;
                return "[redacted]";
            });
            text = OpaqueRunPattern.Replace(text, match =>
            {
                var start = match.Index;
                var end = start + match.Length;
                if ((start > 0 && IsOpaqueRunChar(text[start - 1])) || (end < text.Length && IsOpaqueRunChar(text[end])))
                    return match.Value;
                return LooksLikeToken(match.ValueSpan) ? "[redacted]" : match.Value;
            });
            return EmailPattern.Replace(text, "[redacted-email]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redacted: log scrub timed out]";
        }
    }

    private static bool IsDottedTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    private static bool IsOpaqueRunChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '=' or '-';

    private static bool LooksLikeToken(ReadOnlySpan<char> run)
    {
        bool upper = false, lower = false, digit = false;
        foreach (var c in run)
        {
            upper |= char.IsAsciiLetterUpper(c);
            lower |= char.IsAsciiLetterLower(c);
            digit |= char.IsAsciiDigit(c);
        }
        return upper && lower && digit;
    }
}
