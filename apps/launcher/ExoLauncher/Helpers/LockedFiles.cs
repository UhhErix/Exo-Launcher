using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Which programs hold a file open when Exo's own work fails on it (an uninstall, a game update's folder swap). Read-only:
/// Windows' Restart Manager (RmGetList) names the processes, and nothing is closed or killed; the user decides. A file held
/// by nobody (a permission problem, not a lock) names nobody, so the caller keeps its own message.
/// </summary>
internal static class LockedFiles
{
    /// <summary>ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as the HRESULTs .NET puts on an IOException.</summary>
    internal const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <summary>A folder is checked by its first files: a whole game folder can be 100,000 of them.</summary>
    internal const int MaxFiles = 5000;
    /// <summary>How many programs a message names before it says "and N more".</summary>
    internal const int MaxNamed = 5;

    internal sealed record Holder(uint Pid, string Name);

    /// <summary>A file another program has open for writing or exclusively: the IOException's sharing or lock error.</summary>
    internal static bool IsLockError(Exception ex) => ex is IOException { HResult: SharingViolation or LockViolation };

    /// <summary>A lock error, or an access refusal (a locked file often shows as one): worth asking Restart Manager who holds it.</summary>
    internal static bool MayBeLocked(Exception ex) => IsLockError(ex) || ex is UnauthorizedAccessException;

    /// <summary>
    /// "Hades's files are in use by: Steam (pid 1234), Discord (pid 42)." Null when nobody holds it. Names are capped at
    /// <see cref="MaxNamed"/>; a program with no name is "a program" and a repeated pid is listed once.
    /// </summary>
    internal static string? Describe(string prefix, IReadOnlyList<Holder> holders)
    {
        if (holders.Count == 0) return null;
        var named = holders.Take(MaxNamed).Select(h => $"{(string.IsNullOrWhiteSpace(h.Name) ? "a program" : h.Name)} (pid {h.Pid})");
        var more = holders.Count > MaxNamed ? $" and {holders.Count - MaxNamed} more" : "";
        return $"{prefix} in use by: {string.Join(", ", named)}{more}.";
    }

    /// <summary>
    /// The message for a failed file or folder operation: who holds it when the failure is a lock, else <paramref name="fallback"/>.
    /// The log gets the same line with user paths scrubbed (AppLog.Personal).
    /// </summary>
    internal static string Explain(Exception ex, string path, string prefix, string fallback)
    {
        if (!MayBeLocked(ex)) return fallback;
        var message = Describe(prefix, Holders(path));
        if (message is null) return fallback;
        AppLog.Warn(AppLog.Personal("Locked: " + message, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        return message;
    }

    /// <summary>The processes holding <paramref name="path"/> (a file, or a folder's first <see cref="MaxFiles"/> files). Empty when none, or when it can't be asked.</summary>
    internal static IReadOnlyList<Holder> Holders(string path)
    {
        try
        {
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(MaxFiles).ToArray()
                : File.Exists(path) ? [path] : [];
            return files.Length == 0 ? [] : Ask(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IReadOnlyList<Holder> Ask(string[] files)
    {
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0) return [];
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return [];
            uint count = 0, reasons = 0;
            var rc = RmGetList(session, out var needed, ref count, null, out reasons);
            if (rc != ErrorMoreData && rc != 0) return [];
            if (needed == 0) return [];
            var apps = new ProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, apps, out reasons) != 0) return [];
            return [.. apps.Take((int)count)
                .Where(app => app.ProcessId != 0)
                .DistinctBy(app => app.ProcessId)
                .Select(app => new Holder(app.ProcessId, app.AppName))];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public uint ProcessId;
        public uint StartLow;
        public uint StartHigh;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, string key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint session,
        uint files,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? fileNames,
        uint applications,
        IntPtr applicationList,
        uint services,
        IntPtr serviceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint session,
        out uint needed,
        ref uint count,
        [Out] ProcessInfo[]? apps,
        out uint rebootReasons);
}
