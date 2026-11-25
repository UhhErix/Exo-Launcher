using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Helpers;

internal static unsafe partial class CrashBreadcrumb
{
    private const int MaxBreadcrumbChars = 512;
    private const uint GetModuleHandleExFlagFromAddress = 0x00000004;
    private const uint GetModuleHandleExFlagUnchangedRefCount = 0x00000002;
    private const int ExceptionContinueSearch = 0;

    [ThreadStatic]
    private static bool t_recording;

    private static int s_nativeFilterInstalled;
    private static int s_nativeBreadcrumbWritten;

    internal static Action<string> LogWriter { get; set; } = AppLog.WriteCrashBreadcrumb;
    internal static Action<Exception> ReportRecorder { get; set; } = PendingReports.Record;
    internal static Action<string, string>? NativeReportRecorder { get; set; } = RecordNativePending;

    internal static void InstallNativeFilter()
    {
        if (Interlocked.Exchange(ref s_nativeFilterInstalled, 1) != 0) return;
        try
        {
            delegate* unmanaged[Stdcall]<ExceptionPointers*, int> filter = &OnNativeUnhandledException;
            _ = SetUnhandledExceptionFilter((nint)filter);
        }
        catch { }
    }

    internal static void Record(string source, Exception? ex, string? context = null, bool recordPending = true)
    {
        if (t_recording) return;
        t_recording = true;
        try
        {
            var line = FormatLine(
                source,
                ex,
                context,
                Environment.CurrentManagedThreadId,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.MachineName);
            try { LogWriter(line); } catch { }
            if (recordPending && ex is not null)
            {
                try { ReportRecorder(ex); } catch { }
            }
        }
        catch { }
        finally
        {
            t_recording = false;
        }
    }

    internal static string FormatLine(
        string source,
        Exception? ex,
        string? context,
        int threadId,
        string userName,
        string profile,
        string machineName)
    {
        var safeSource = SanitizeToken(source, "unknown");
        var typeName = SanitizeToken(ex?.GetType().Name, "UnknownException");
        var hr = ex?.HResult ?? 0;
        var frame = ExtractTopFrame(ex?.StackTrace);
        var rawMsg = string.IsNullOrWhiteSpace(ex?.Message) ? (context ?? "") : ex!.Message;
        var raw = $"breadcrumb source={safeSource} type={typeName} hr=0x{hr:X8} tid={threadId} frame={frame} msg={rawMsg}";
        var scrubbed = CrashReport.Scrub(raw, userName, profile, machineName);
        return CollapseAndBound(scrubbed);
    }

    internal static string FormatNativeLine(
        uint exceptionCode,
        string? moduleFileName,
        nuint offset,
        uint threadId,
        string userName,
        string profile,
        string machineName)
    {
        var module = SafeModuleBaseName(moduleFileName);
        var raw = $"breadcrumb source=native-unhandled code=0x{exceptionCode:X8} mod={module} off=0x{offset:X} tid={threadId}";
        var scrubbed = CrashReport.Scrub(raw, userName, profile, machineName);
        return CollapseAndBound(scrubbed);
    }

    internal static string SafeModuleBaseName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "unknown";
        var sep = path.LastIndexOfAny(['\\', '/']);
        var name = sep >= 0 ? path[(sep + 1)..] : path;
        return SanitizeToken(name, "unknown");
    }

    private static string ExtractTopFrame(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return "none";
        foreach (var rawLine in stackTrace.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("at ", StringComparison.Ordinal)) trimmed = trimmed[3..].Trim();
            var inIndex = trimmed.IndexOf(" in ", StringComparison.Ordinal);
            if (inIndex > 0) trimmed = trimmed[..inIndex];
            return trimmed.Length > 120 ? trimmed[..120] : trimmed;
        }
        return "none";
    }

    private static string SanitizeToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var sb = new StringBuilder(Math.Min(value.Length, 80));
        foreach (var ch in value)
        {
            if (sb.Length >= 80) break;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':')
                sb.Append(ch);
        }
        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static string CollapseAndBound(string text)
    {
        if (string.IsNullOrEmpty(text)) return "breadcrumb source=unknown";
        var sb = new StringBuilder(Math.Min(text.Length, MaxBreadcrumbChars));
        var lastSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                if (!lastSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
            }
            else if (!char.IsControl(ch))
            {
                sb.Append(ch);
                lastSpace = false;
            }
            if (sb.Length >= MaxBreadcrumbChars) break;
        }
        return sb.ToString().Trim();
    }

    private static void RecordNativePending(string errorType, string breadcrumbLine)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var version = Services.AppServices.RunningVersion;
            var windows = Environment.OSVersion.Version.ToString();
            var payload = CrashReport.Build(
                errorType,
                breadcrumbLine,
                version,
                windows,
                Environment.UserName,
                profile,
                Environment.MachineName,
                errorType);
            PendingReports.Keep(payload);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnNativeUnhandledException(ExceptionPointers* pointers)
    {
        try
        {
            if (Interlocked.Exchange(ref s_nativeBreadcrumbWritten, 1) != 0)
                return ExceptionContinueSearch;

            uint code = 0;
            nuint address = 0;
            if (pointers != null && pointers->ExceptionRecord != null)
            {
                code = pointers->ExceptionRecord->ExceptionCode;
                address = pointers->ExceptionRecord->ExceptionAddress;
            }

            string? moduleName = null;
            nuint offset = 0;
            if (address != 0 && GetModuleHandleExW(GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefCount, address, out var module) && module != 0)
            {
                offset = address >= (nuint)module ? address - (nuint)module : 0;
                var buffer = stackalloc char[260];
                var len = GetModuleFileNameW(module, buffer, 260);
                if (len > 0 && len < 260) moduleName = new string(buffer, 0, (int)len);
            }

            var line = FormatNativeLine(
                code,
                moduleName,
                offset,
                GetCurrentThreadId(),
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.MachineName);
            try { LogWriter(line); } catch { }
            try { NativeReportRecorder?.Invoke($"NativeException_0x{code:X8}", line); } catch { }
        }
        catch { }
        return ExceptionContinueSearch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public ExceptionRecord* NextRecord;
        public nuint ExceptionAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public ExceptionRecord* ExceptionRecord;
        public nint ContextRecord;
    }

    [DllImport("kernel32.dll")]
    private static extern nint SetUnhandledExceptionFilter(nint lpTopLevelExceptionFilter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint dwFlags, nuint lpModuleName, out nint phModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(nint hModule, char* lpFilename, uint nSize);
}
