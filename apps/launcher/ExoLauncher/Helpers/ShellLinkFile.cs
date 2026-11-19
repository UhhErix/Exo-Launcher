using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ExoLauncher.Helpers;

/// <summary>
/// A Windows shortcut (.lnk) read and written through the shell's own ShellLink object, so Exo keeps what a shortcut does
/// (its target, arguments, folder and icon) rather than its bytes, which also carry this PC's name, volume and tracker ids.
/// </summary>
internal static class ShellLinkFile
{
    internal sealed record Link(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description);

    /// <summary>What the shortcut at <paramref name="path"/> does, or null when it cannot be read.</summary>
    internal static Link? Read(string path)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var text = new StringBuilder(1024);
            link.GetPath(text, text.Capacity, IntPtr.Zero, 0x4); // SLGP_RAWPATH: as written, environment variables kept
            var target = text.ToString();
            text.Clear(); link.GetArguments(text, text.Capacity); var arguments = text.ToString();
            text.Clear(); link.GetWorkingDirectory(text, text.Capacity); var folder = text.ToString();
            text.Clear(); link.GetIconLocation(text, text.Capacity, out var index); var icon = text.ToString();
            text.Clear(); link.GetDescription(text, text.Capacity); var description = text.ToString();
            return new Link(target, arguments, folder, icon, index, description);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Writes a shortcut that does what <paramref name="link"/> says.</summary>
    internal static void Write(string path, Link link)
    {
        var shell = (IShellLinkW)new ShellLink();
        shell.SetPath(link.Target);
        shell.SetArguments(link.Arguments);
        shell.SetWorkingDirectory(link.WorkingDirectory);
        if (link.IconPath.Length > 0) shell.SetIconLocation(link.IconPath, link.IconIndex);
        shell.SetDescription(link.Description);
        ((IPersistFile)shell).Save(path, true);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
