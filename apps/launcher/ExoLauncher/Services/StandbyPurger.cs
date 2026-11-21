using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Services;

internal static class StandbyPurger
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const int StatusPrivilegeNotHeld = unchecked((int)0xC0000061);
    private const string PrivilegeName = "SeProfileSingleProcessPrivilege";

    private static readonly Lock Gate = new();
    private static bool _denied;

    internal static bool TryPurge()
    {
        lock (Gate)
        {
            if (_denied) return false;
            if (!EnablePrivilege())
            {
                _denied = true;
                return false;
            }
            var cmd = MemoryPurgeStandbyList;
            var status = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
            if (status == StatusPrivilegeNotHeld)
            {
                _denied = true;
                return false;
            }
            return status == 0;
        }
    }

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

    private static bool EnablePrivilege()
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
}
