using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Adapters;

/// <summary>
/// Where the hider reads and changes windows. The real one wraps Win32; tests use a fake, so no test ever lists, hides,
/// shows or focuses a real window.
/// </summary>
internal interface IStoreWindows
{
    /// <summary>The top-level windows (visible or not) of these processes, read once.</summary>
    IReadOnlyList<StoreWindow> Enumerate(IReadOnlySet<int> processIds);

    /// <summary>One window, or null when it is gone.</summary>
    StoreWindow? Read(nint handle);

    /// <summary>The process a window belongs to: cheap, for the hook that sees every window any program makes.</summary>
    int ProcessIdOf(nint handle);

    void Hide(nint handle);

    /// <summary>Show and restore.</summary>
    void Show(nint handle);

    void BringToFront(nint handle);

    void SetExStyle(nint handle, long exStyle);
}

/// <summary>Where the hider reads programs.</summary>
internal interface IStoreProcesses
{
    /// <summary>The executable's full path, or null when it cannot be read.</summary>
    string? PathOf(int processId);

    string? NameOf(int processId);

    /// <summary>Live processes with one of these names (one snapshot of the process table).</summary>
    IReadOnlyList<(int ProcessId, string Name)> Running(IEnumerable<string> processNames);
}

internal sealed class Win32StoreWindows : IStoreWindows
{
    internal static readonly Win32StoreWindows Instance = new();

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint command);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    public IReadOnlyList<StoreWindow> Enumerate(IReadOnlySet<int> processIds)
    {
        var found = new List<StoreWindow>();
        if (processIds.Count == 0) return found;
        EnumWindowsProc callback = (handle, _) =>
        {
            try
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid != 0 && processIds.Contains((int)pid) && Read(handle) is { } window) found.Add(window);
            }
            catch { /* the window closed while it was read */ }
            return true;
        };
        try { EnumWindows(callback, 0); }
        finally { GC.KeepAlive(callback); }
        return found;
    }

    public StoreWindow? Read(nint handle)
    {
        try
        {
            if (!IsWindow(handle)) return null;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0) return null;
            var title = new StringBuilder(512);
            _ = GetWindowText(handle, title, title.Capacity);
            var className = new StringBuilder(128);
            _ = GetClassName(handle, className, className.Capacity);
            GetWindowRect(handle, out var rect);
            return new StoreWindow(handle, (int)pid, className.ToString(), title.ToString(), IsWindowVisible(handle),
                GetWindow(handle, GwOwner), rect.Left, rect.Top, rect.Right, rect.Bottom,
                GetWindowLongPtr(handle, GwlStyle), GetWindowLongPtr(handle, GwlExstyle));
        }
        catch { return null; }
    }

    public int ProcessIdOf(nint handle)
    {
        try { GetWindowThreadProcessId(handle, out var pid); return (int)pid; }
        catch { return 0; }
    }

    public void Hide(nint handle) => ShowWindow(handle, SwHide);

    public void Show(nint handle)
    {
        ShowWindow(handle, SwShow);
        ShowWindow(handle, SwRestore);
    }

    public void BringToFront(nint handle) => SetForegroundWindow(handle);

    public void SetExStyle(nint handle, long exStyle) => SetWindowLongPtr(handle, GwlExstyle, (nint)exStyle);
}

internal sealed class Win32StoreProcesses : IStoreProcesses
{
    internal static readonly Win32StoreProcesses Instance = new();

    public string? PathOf(int processId) => ProcessHelper.TryGetExecutablePath(processId);

    public string? NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Live PIDs whose process name is in <paramref name="processNames"/>. Looking each name up on its own snapshots the
    /// whole process table once per name; one snapshot per pass keeps that cost flat.
    /// </summary>
    public IReadOnlyList<(int ProcessId, string Name)> Running(IEnumerable<string> processNames) =>
        Helpers.ProcessSnapshot.Running(processNames);
}
