using System.Runtime.InteropServices;

namespace ExoCapture.Native;

/// <summary>The Win32 calls Exo Capture makes: windows and monitors, hotkeys, the clipboard, timers, processes and named objects.</summary>
internal static unsafe partial class Win32
{
    internal const int WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020,
        WM_KEYDOWN = 0x0100, WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_HOTKEY = 0x0312, WM_APP = 0x8000, WM_QUIT = 0x0012, WM_KILLFOCUS = 0x0008;
    internal const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SNAPSHOT = 0x2C;
    internal const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    internal const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    internal const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    internal const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    internal const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
    internal const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    internal const uint GA_ROOT = 2, GW_OWNER = 4;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
    internal const uint EVENT_MODIFY_STATE = 0x0002, MUTEX_ALL_ACCESS = 0x1F0001, FILE_MAP_ALL_ACCESS = 0xF001F, FILE_MAP_READ = 0x4;
    internal const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258, INFINITE = 0xFFFFFFFF;
    internal const uint CF_DIB = 8, CF_HDROP = 15, GMEM_MOVEABLE = 2;
    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MSG { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public POINT Pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint IconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEXW
    {
        public uint Size;
        public RECT Monitor, Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential)] internal struct PAINTSTRUCT { public nint Hdc; public int Erase; public RECT Paint; public int Restore, IncUpdate; public fixed byte Reserved[32]; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)] internal static partial ushort RegisterClassEx(WNDCLASSEXW* cls);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] internal static partial int GetMessage(MSG* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PeekMessage(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessage(MSG* msg);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostMessage(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostThreadMessage(uint thread, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")] internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UnregisterHotKey(nint hwnd, int id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial nint SetCapture(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool InvalidateRect(nint hwnd, RECT* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll")] internal static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] internal static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport("user32.dll")] internal static partial nint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll")] internal static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] internal static partial nint LoadCursor(nint instance, nint name);
    [LibraryImport("user32.dll")] internal static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetCursorPos(POINT* point);
    [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsIconic(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetWindowRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetClientRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ClientToScreen(nint hwnd, POINT* point);
    [LibraryImport("user32.dll")] internal static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint GetWindow(nint hwnd, uint cmd);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static partial nint GetWindowLongPtr(nint hwnd, int index);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] internal static partial int GetClassName(nint hwnd, char* name, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] internal static partial int GetWindowText(nint hwnd, char* text, int max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> proc, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> proc, nint param);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromPoint(POINT point, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetMonitorInfo(nint monitor, MONITORINFOEXW* info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AllowSetForegroundWindow(uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)] internal static partial int DrawText(nint hdc, string text, int count, RECT* rect, uint format);
    [LibraryImport("user32.dll")] internal static partial int FillRect(nint hdc, RECT* rect, nint brush);
    [LibraryImport("user32.dll")] internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint SetClipboardData(uint format, nint mem);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)] internal static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("gdi32.dll")] internal static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] internal static partial nint CreateDIBSection(nint hdc, void* info, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] internal static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CopyBits(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] internal static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] internal static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] internal static partial nint GetStockObject(int index);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool Rectangle(nint hdc, int l, int t, int r, int b);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool RoundRect(nint hdc, int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] internal static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] internal static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll", EntryPoint = "AddFontResourceExW", StringMarshalling = StringMarshalling.Utf16)] internal static partial int AddFontResourceEx(string path, uint flags, nint reserved);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GdiFlush();

    [LibraryImport("dwmapi.dll")] internal static partial int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool QueryPerformanceCounter(out long count);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool QueryPerformanceFrequency(out long frequency);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)] internal static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetWaitableTimer(nint timer, long* due, int period, nint completion, nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [LibraryImport("kernel32.dll")] internal static partial uint WaitForSingleObject(nint handle, uint ms);
    [LibraryImport("kernel32.dll")] internal static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint ms);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref int size);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
    [LibraryImport("kernel32.dll")] internal static partial int GetPackageFullName(nint process, ref uint length, char* name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [LibraryImport("kernel32.dll", EntryPoint = "OpenEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial nint OpenEvent(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetEvent(nint handle);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial nint CreateMutex(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool owner, string name);
    [LibraryImport("kernel32.dll", EntryPoint = "OpenMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial nint OpenMutex(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial nint OpenFileMapping(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint MapViewOfFile(nint mapping, uint access, uint high, uint low, nuint bytes);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UnmapViewOfFile(nint view);
    [LibraryImport("kernel32.dll")] internal static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] internal static partial nint GlobalLock(nint mem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GlobalUnlock(nint mem);
    [LibraryImport("kernel32.dll")] internal static partial nint GlobalFree(nint mem);
    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentThread();
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetThreadPriority(nint thread, int priority);
    [LibraryImport("kernel32.dll")] internal static partial nint GetModuleHandleW(nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetPriorityClass(nint process, uint priority);
    [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetProcessDpiAwarenessContext(nint context);
    [LibraryImport("shcore.dll")] internal static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint AvSetMmThreadCharacteristics(string task, ref uint index);

    /// <summary>QPC in 100 ns units: the one clock every frame and sound sample is stamped with.</summary>
    internal static long Now()
    {
        QueryPerformanceCounter(out var count);
        return (long)((Int128)count * 10_000_000 / Frequency);
    }

    /// <summary>A QPC reading (from WASAPI or Exo's voice feed) in the same units as <see cref="Now"/>.</summary>
    internal static long FromQpc(long count) => (long)((Int128)count * 10_000_000 / Frequency);

    internal static readonly long Frequency = QueryFrequency();
    private static long QueryFrequency() { QueryPerformanceFrequency(out var f); return f; }

    internal static RECT? FrameBounds(nint hwnd)
    {
        RECT rect;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &rect, sizeof(RECT)) == 0 && rect.Width > 0) return rect;
        return GetWindowRect(hwnd, &rect) ? rect : null;
    }

    internal static bool Cloaked(nint hwnd)
    {
        int cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, 4) == 0 && cloaked != 0;
    }

    internal static uint ProcessOf(nint hwnd)
    {
        uint pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        return pid;
    }

    internal static string? ImagePath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    internal static string ClassName(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var n = GetClassName(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    internal static string Title(nint hwnd)
    {
        var buffer = stackalloc char[512];
        var n = GetWindowText(hwnd, buffer, 512);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    internal static MONITORINFOEXW MonitorInfo(nint monitor)
    {
        var info = new MONITORINFOEXW { Size = (uint)sizeof(MONITORINFOEXW) };
        GetMonitorInfo(monitor, &info);
        return info;
    }

    internal static string MonitorDevice(nint monitor)
    {
        var info = MonitorInfo(monitor);
        return new string(info.Device);
    }

    private static List<nint>? _enumTarget;

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint hwnd, nint _) { _enumTarget!.Add(hwnd); return 1; }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint _, RECT* __, nint ___) { _enumTarget!.Add(monitor); return 1; }

    private static readonly Lock EnumGate = new();

    /// <summary>Top-level windows, front to back.</summary>
    internal static List<nint> TopWindows()
    {
        lock (EnumGate)
        {
            _enumTarget = [];
            EnumWindows(&CollectWindow, 0);
            var list = _enumTarget;
            _enumTarget = null;
            return list;
        }
    }

    internal static List<nint> Monitors()
    {
        lock (EnumGate)
        {
            _enumTarget = [];
            EnumDisplayMonitors(0, null, &CollectMonitor, 0);
            var list = _enumTarget;
            _enumTarget = null;
            return list;
        }
    }

    /// <summary>A window a person could be looking at: visible, not cloaked, not a tool window, with an area.</summary>
    internal static bool Shown(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || Cloaked(hwnd)) return false;
        var ex = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & 0x40000 /* WS_EX_APPWINDOW */) == 0) return false;
        return FrameBounds(hwnd) is { Width: > 1, Height: > 1 };
    }

    internal static RECT VirtualScreen() => new()
    {
        Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
        Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
        Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
        Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN),
    };
}
