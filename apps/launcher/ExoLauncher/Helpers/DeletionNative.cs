using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Windows' own delete calls: the Recycle Bin send and the Recycle Bin empty. Only the gate's callers reach them, and each
/// asks <see cref="DeletionGate"/> first. BannedNativeImportsTests allows the names in this file alone.
/// </summary>
internal static partial class DeletionNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOp operation);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(nint window, string? root, uint flags);

    /// <summary>
    /// Sends a file or folder to the Recycle Bin, quietly, on an STA thread. False when the gate refuses it or Windows keeps it.
    /// Exo asked first: no confirmation, progress or sound of Windows' own.
    /// </summary>
    internal static bool SendToRecycleBin(string path, bool clean = false)
    {
        if (DeletionGate.Refusal(path, clean, owners: null, DeletionGate.Live) is not null) return false;
        var done = false;
        var thread = new Thread(() =>
        {
            const uint FoDelete = 3;
            const ushort FofAllowUndo = 0x40, FofNoConfirmation = 0x10, FofNoErrorUi = 0x400, FofSilent = 0x4;
            var op = new ShFileOp { Func = FoDelete, From = path + "\0\0", Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent };
            done = SHFileOperationW(ref op) == 0 && op.AnyAborted == 0;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return done && !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>Empties the Recycle Bin with no confirmation. True when Windows took the request.</summary>
    internal static bool EmptyRecycleBin() => SHEmptyRecycleBinW(0, null, 0x1 | 0x2 | 0x4) == 0;
}
