using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// ShellHidden: Exo's own window is minimized or hidden, so nothing of its
/// host or shell WebView is on screen. LiveAudio: a call or other live sound
/// runs in the host, which then keeps its working set.
internal readonly record struct HostedTrimPolicy(bool ShellHidden = false, bool LiveAudio = false);

/// <summary>A process's command line, the one Win32 call for it.</summary>
internal static class HostedProcessClass
{
    internal static string? QueryCommandLine(int pid)
    {
        if (pid <= 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0 || length > 64 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                    return null;
                var info = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (info.Buffer == IntPtr.Zero || info.Length == 0) return null;
                return Marshal.PtrToStringUni(info.Buffer, info.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
