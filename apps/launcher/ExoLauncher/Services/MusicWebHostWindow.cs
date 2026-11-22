using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.UI;

namespace ExoLauncher.Services;

internal sealed class MusicWebHostWindow
{
    private const string WindowClassName = "ExoMusicWebHostWindow";
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int SwShowNormal = 1;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int BlackBrush = 4;
    private const int IdcArrow = 32512;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int SizeMinimized = 1;
    private const int HiddenWidth = 1280;
    private const int HiddenHeight = 800;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private static readonly WndProcDelegate WindowProc = WndProc;
    private static readonly Dictionary<nint, MusicWebHostWindow> Instances = new();
    private static bool _classRegistered;

    private nint _hwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _closed;

    internal nint Handle => _hwnd;
    internal CoreWebView2Controller? Controller => _controller;
    internal CoreWebView2? Core => _core;
    internal bool IsClosed => _closed || _hwnd == 0;
    internal bool IsMinimized => _hwnd != 0 && IsIconic(_hwnd);

    internal event Action? Closed;
    internal event Action<bool>? MinimizedChanged;

    private MusicWebHostWindow(nint hwnd)
    {
        _hwnd = hwnd;
        Instances[hwnd] = this;
    }

    internal static MusicWebHostWindow CreateHidden()
    {
        EnsureClassRegistered();
        var hwnd = CreateWindowExW(
            WsExToolWindow | WsExNoActivate,
            WindowClassName,
            "Exo Music Engine",
            WsPopup | WsClipChildren,
            0,
            0,
            HiddenWidth,
            HiddenHeight,
            0,
            0,
            GetModuleHandleW(null),
            0);
        if (hwnd == 0)
            throw new InvalidOperationException("Failed to create hidden music host window.");
        return new MusicWebHostWindow(hwnd);
    }

    internal static MusicWebHostWindow CreatePopup(string title, (int Width, int Height)? size = null)
    {
        EnsureClassRegistered();
        var screenW = Math.Max(800, GetSystemMetrics(SmCxScreen));
        var screenH = Math.Max(600, GetSystemMetrics(SmCyScreen));
        var width = Math.Clamp(size?.Width ?? 540, 420, Math.Max(420, screenW - 80));
        var height = Math.Clamp(size?.Height ?? 720, 520, Math.Max(520, screenH - 80));
        var x = Math.Max(0, (screenW - width) / 2);
        var y = Math.Max(0, (screenH - height) / 2);
        var hwnd = CreateWindowExW(
            0,
            WindowClassName,
            title,
            WsOverlappedWindow | WsClipChildren,
            x,
            y,
            width,
            height,
            0,
            0,
            GetModuleHandleW(null),
            0);
        if (hwnd == 0)
            throw new InvalidOperationException("Failed to create music sign-in window.");
        try
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch { }
        return new MusicWebHostWindow(hwnd);
    }

    internal void AttachController(CoreWebView2Controller controller, bool visible)
    {
        _controller = controller;
        _core = controller.CoreWebView2;
        try { controller.DefaultBackgroundColor = Color.FromArgb(255, 0, 0, 0); } catch { }
        SyncBounds();
        try { controller.IsVisible = visible; } catch { }
    }

    internal void SetControllerVisible(bool visible)
    {
        if (_controller is not { } controller) return;
        try { controller.IsVisible = visible; } catch { }
    }

    internal void Show(bool activate)
    {
        if (_hwnd == 0) return;
        ShowWindow(_hwnd, activate ? SwShowNormal : SwShowNoActivate);
        if (activate) SetForegroundWindow(_hwnd);
        SyncBounds();
    }

    internal void Activate()
    {
        if (_hwnd == 0) return;
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SwRestore);
        ShowWindow(_hwnd, SwShow);
        SetForegroundWindow(_hwnd);
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        var controller = _controller;
        _controller = null;
        _core = null;
        if (controller is not null)
        {
            try { controller.Close(); } catch { }
        }
        var hwnd = _hwnd;
        if (hwnd != 0)
        {
            _hwnd = 0;
            Instances.Remove(hwnd);
            try { DestroyWindow(hwnd); } catch { }
        }
        Closed?.Invoke();
    }

    private void SyncBounds()
    {
        if (_controller is not { } controller || _hwnd == 0) return;
        try
        {
            if (GetClientRect(_hwnd, out var rect))
            {
                var w = Math.Max(1, rect.Right - rect.Left);
                var h = Math.Max(1, rect.Bottom - rect.Top);
                controller.Bounds = new Rect(0, 0, w, h);
            }
            else
            {
                controller.Bounds = new Rect(0, 0, HiddenWidth, HiddenHeight);
            }
        }
        catch { }
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;
        var wc = new WndClassExW
        {
            CbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            HInstance = GetModuleHandleW(null),
            HCursor = LoadCursorW(0, IdcArrow),
            HbrBackground = GetStockObject(BlackBrush),
            LpszClassName = WindowClassName,
        };
        RegisterClassExW(ref wc);
        _classRegistered = true;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out var host))
        {
            if (msg == WmSize)
            {
                host.SyncBounds();
                try { host.MinimizedChanged?.Invoke(wParam == SizeMinimized); } catch { }
            }
            else if (msg == WmDestroy)
            {
                Instances.Remove(hwnd);
                host._hwnd = 0;
                if (!host._closed)
                {
                    host._closed = true;
                    var controller = host._controller;
                    host._controller = null;
                    host._core = null;
                    if (controller is not null)
                    {
                        try { controller.Close(); } catch { }
                    }
                    try { host.Closed?.Invoke(); } catch { }
                }
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public nint LpszMenuName;
        public string LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassExW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RectStruct lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint hInstance, int cursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int fnObject);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
}
