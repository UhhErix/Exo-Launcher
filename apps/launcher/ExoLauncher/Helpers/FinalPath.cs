using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#if EXO_IDENTITY_LINKED
namespace ExoLauncher.Helpers.Linked;
#else
namespace ExoLauncher.Helpers;
#endif

/// <summary>Where Windows opens a path: every junction, symbolic link and mount point on the way followed.</summary>
internal static class FinalPath
{
    /// <summary>The final path without the \?\ prefix; null when the path cannot be opened (it does not exist, or access is denied).</summary>
    internal static string? Of(string path)
    {
        // Attributes only, shared every way; backup semantics so a folder opens too.
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[512];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length) return null;
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..] : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
