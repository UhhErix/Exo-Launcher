using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed record StandbyPurgeStatus(bool Available, bool InProcess, bool SilentElevated, string? Reason);

internal interface IStandbyPurgeSystem
{
    bool HasPrivilege();
    bool EnablePrivilege();
    int PurgeMemoryList();
    bool CanSilentElevate();
    bool RunSilentElevatedPurge();
}

internal sealed class StandbyPurgeController(IStandbyPurgeSystem system)
{
    internal const string UnavailableReason = "Unavailable without administrator permission (run Exo as administrator for live standby purges; Auto Tune still purges once when elevated).";
    private const int StatusPrivilegeNotHeld = unchecked((int)0xC0000061);

    private readonly Lock _gate = new();

    internal StandbyPurgeStatus Status()
    {
        if (system.HasPrivilege()) return new(true, true, false, null);
        if (system.CanSilentElevate()) return new(true, false, true, null);
        return new(false, false, false, UnavailableReason);
    }

    internal bool TryPurge()
    {
        lock (_gate)
        {
            if (system.HasPrivilege() && system.EnablePrivilege())
            {
                var status = system.PurgeMemoryList();
                if (status == 0) return true;
                if (status != StatusPrivilegeNotHeld) return false;
            }
            return system.CanSilentElevate() && system.RunSilentElevatedPurge();
        }
    }
}

internal static class StandbyPurger
{
    internal const string Flag = "--purge-standby";

    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const int TokenPrivilegesClass = 3;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const string PrivilegeName = "SeProfileSingleProcessPrivilege";

    private static readonly NativeStandbyPurgeSystem NativeSystem = new();

    internal static StandbyPurgeController Shared { get; } = new(NativeSystem);

    internal static bool TryPurge() => Shared.TryPurge();

    internal static StandbyPurgeStatus Status() => Shared.Status();

    internal static int RunElevatedCli() =>
        NativeSystem.HasPrivilege() && NativeSystem.EnablePrivilege() && NativeSystem.PurgeMemoryList() == 0 ? 0 : 1;

    internal static string ElevatedPurgeLine()
    {
        const string script = """
$c = @"
using System;
using System.Runtime.InteropServices;
public static class ExoStandbyPurge {
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TP { public uint Count; public LUID Luid; public uint Attr; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValueW(string s, string n, out LUID l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, uint l, IntPtr p, IntPtr r);
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int c, ref int i, int l);
    public static int Run() {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28u, out tok)) return -1;
        try {
            LUID luid;
            if (!LookupPrivilegeValueW(null, "SeProfileSingleProcessPrivilege", out luid)) return -2;
            var tp = new TP { Count = 1, Luid = luid, Attr = 2 };
            if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0) return -3;
            int cmd = 4;
            return NtSetSystemInformation(80, ref cmd, 4);
        } finally { CloseHandle(tok); }
    }
}
"@
Add-Type -TypeDefinition $c -ErrorAction SilentlyContinue
if (([type]'ExoStandbyPurge') -ne $null) { [void][ExoStandbyPurge]::Run() }
""";
        return $"{LeftoverScan.PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}";
    }

    private sealed class NativeStandbyPurgeSystem : IStandbyPurgeSystem
    {
        public bool HasPrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
                return false;
            try
            {
                if (!LookupPrivilegeValueW(null, PrivilegeName, out var target))
                    return false;
                _ = GetTokenInformation(token, TokenPrivilegesClass, 0, 0, out var len);
                if (len < 4) return false;
                var buffer = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, len, out _))
                        return false;
                    var count = Marshal.ReadInt32(buffer);
                    if (count <= 0 || 4L + (long)count * 12 > len) return false;
                    for (var i = 0; i < count; i++)
                    {
                        var offset = 4 + i * 12;
                        var low = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                        var high = Marshal.ReadInt32(buffer, offset + 4);
                        if (low == target.LowPart && high == target.HighPart) return true;
                    }
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        public bool EnablePrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
                return false;
            try
            {
                if (!LookupPrivilegeValueW(null, PrivilegeName, out var luid))
                    return false;
                var next = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                if (!AdjustTokenPrivileges(token, false, ref next, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;
                return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        public int PurgeMemoryList()
        {
            var cmd = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
        }

        public bool CanSilentElevate() =>
            !HostMode.IsDisposable && Environment.ProcessPath is not null && !ElevatedCommands.WindowsAsks();

        public bool RunSilentElevatedPurge()
        {
            if (!CanSilentElevate() || Environment.ProcessPath is not { } exo) return false;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exo, Flag)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                return process is not null && process.WaitForExit(5000) && process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Debug("StandbyPurger: elevated purge failed: " + ex.Message);
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint PrivilegeCount; public Luid Luid; public uint Attributes; }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
}
