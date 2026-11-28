using System.Runtime.InteropServices;

namespace ExoLauncher.Services;

internal static class HostWindow
{
    private const uint FosOverwritePrompt = 0x00000002;
    private const uint FosNoChangeDir = 0x00000008;
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int HrCancelled = unchecked((int)0x800704C7);
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid ClsidFileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private static long _nativeHwnd;
    private static int _nativeActive = -1;

    internal static void SetNativeState(nint hwnd, bool? active)
    {
        if (hwnd != 0) Interlocked.Exchange(ref _nativeHwnd, hwnd);
        if (active is { } isActive) Interlocked.Exchange(ref _nativeActive, isActive ? 1 : 0);
    }

    internal static void ClearNativeState()
    {
        Interlocked.Exchange(ref _nativeHwnd, 0);
        Interlocked.Exchange(ref _nativeActive, -1);
    }

    internal static bool HasNativeState =>
        Interlocked.Read(ref _nativeHwnd) != 0 || Volatile.Read(ref _nativeActive) >= 0;

    internal static nint OwnerHwnd
    {
        get
        {
            if (App.MainAppWindow is { } window) return WinRT.Interop.WindowNative.GetWindowHandle(window);
            var hwnd = (nint)Interlocked.Read(ref _nativeHwnd);
            return hwnd != 0 ? hwnd : RuneHostSession.ActiveUiMainWindowHandle();
        }
    }

    internal static bool IsActive
    {
        get
        {
            if (App.MainAppWindow is { } window) return window.WindowIsActive;
            var active = Volatile.Read(ref _nativeActive);
            if (active >= 0) return active == 1;
            var hwnd = OwnerHwnd;
            return hwnd != 0 && GetForegroundWindow() == hwnd;
        }
    }

    internal static Task<bool> ConfirmNativeAsync(string title, string body, string action, bool danger = false)
    {
        if (App.MainAppWindow is { } window) return window.ConfirmNativeAsync(title, body, action, danger);
        var owner = OwnerHwnd;
        var flags = (danger ? 0x30u : 0x20u) | 0x01u | 0x100u | 0x10000u;
        return Task.Run(() => MessageBoxW(owner, body, title, flags) == 1);
    }

    internal static Task<(string? Path, bool Failed)> PickOpenFileAsync(
        string title,
        (string Name, string Spec)[] filters,
        string? initialFolder = null) =>
        RunDialogOnStaAsync(() => ShowDialog(
            ClsidFileOpenDialog,
            title,
            FosForceFileSystem | FosPathMustExist | FosFileMustExist | FosNoChangeDir,
            filters,
            suggestedFileName: null,
            defaultExt: null,
            initialFolder));

    internal static Task<(string? Path, bool Failed)> PickSaveFileAsync(
        string title,
        (string Name, string Spec)[] filters,
        string? suggestedFileName = null,
        string? defaultExt = null,
        string? initialFolder = null) =>
        RunDialogOnStaAsync(() => ShowDialog(
            ClsidFileSaveDialog,
            title,
            FosForceFileSystem | FosPathMustExist | FosOverwritePrompt | FosNoChangeDir,
            filters,
            suggestedFileName,
            defaultExt,
            initialFolder));

    internal static Task<(string? Path, bool Failed)> PickFolderAsync(string title, string? initialFolder = null) =>
        RunDialogOnStaAsync(() => ShowDialog(
            ClsidFileOpenDialog,
            title,
            FosForceFileSystem | FosPickFolders | FosPathMustExist | FosNoChangeDir,
            filters: [],
            suggestedFileName: null,
            defaultExt: null,
            initialFolder));

    private static Task<(string? Path, bool Failed)> RunDialogOnStaAsync(Func<(string? Path, bool Failed)> show)
    {
        var tcs = new TaskCompletionSource<(string? Path, bool Failed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(show()); }
            catch (Exception ex)
            {
                Helpers.AppLog.Warn("HostWindow picker: " + ex.GetType().Name + ": " + ex.Message);
                tcs.TrySetResult((null, true));
            }
        })
        {
            IsBackground = true,
            Name = "ExoFileDialog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static (string? Path, bool Failed) ShowDialog(
        Guid clsid,
        string title,
        uint options,
        (string Name, string Spec)[] filters,
        string? suggestedFileName,
        string? defaultExt,
        string? initialFolder)
    {
        var type = Type.GetTypeFromCLSID(clsid);
        if (type is null || Activator.CreateInstance(type) is not IFileDialog dialog)
            return (null, true);
        try
        {
            dialog.GetOptions(out var existing);
            dialog.SetOptions(existing | options);
            if (!string.IsNullOrWhiteSpace(title)) dialog.SetTitle(title);
            if (filters.Length > 0)
            {
                var specs = new ComDlgFilterSpec[filters.Length];
                for (var i = 0; i < filters.Length; i++)
                    specs[i] = new ComDlgFilterSpec { Name = filters[i].Name, Spec = filters[i].Spec };
                dialog.SetFileTypes((uint)specs.Length, specs);
                dialog.SetFileTypeIndex(1);
            }
            if (!string.IsNullOrWhiteSpace(suggestedFileName)) dialog.SetFileName(suggestedFileName);
            if (!string.IsNullOrWhiteSpace(defaultExt)) dialog.SetDefaultExtension(defaultExt);
            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            {
                var iid = IidShellItem;
                if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out var folderItem) == 0 && folderItem is not null)
                {
                    try { dialog.SetDefaultFolder(folderItem); }
                    finally { Marshal.ReleaseComObject(folderItem); }
                }
            }

            var hr = dialog.Show(OwnerHwnd);
            if (hr == HrCancelled) return (null, false);
            if (hr < 0) return (null, true);

            dialog.GetResult(out var result);
            if (result is null) return (null, false);
            try
            {
                result.GetDisplayName(SigdnFileSysPath, out var path);
                return (string.IsNullOrWhiteSpace(path) ? null : path, false);
            }
            finally
            {
                Marshal.ReleaseComObject(result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    internal static string? DownloadsFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(path) ? path : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ComDlgFilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] ComDlgFilterSpec[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem? ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
