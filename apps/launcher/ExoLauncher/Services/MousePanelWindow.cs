using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.UI;

namespace ExoLauncher.Services;

internal sealed class MousePanelWindow
{
    private const string WindowClassName = "ExoMousePanelHostWindow";
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int BlackBrush = 4;
    private const int IdcArrow = 32512;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate void WinEventDelegate(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private static readonly WndProcDelegate WindowProc = WndProc;
    private static readonly Dictionary<nint, MousePanelWindow> Instances = new();
    private static bool _classRegistered;

    private readonly WinEventDelegate _winEventProc;
    private readonly List<nint> _hooks = new();
    private nint _hwnd;
    private nint _ownerHwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private double _logicalX;
    private double _logicalY;
    private double _logicalWidth;
    private double _logicalHeight;
    private bool _requestedVisible;
    private bool _shown;
    private bool _closed;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastW = -1;
    private int _lastH = -1;

    internal nint Handle => _hwnd;
    internal nint OwnerHandle => _ownerHwnd;
    internal CoreWebView2Controller? Controller => _controller;
    internal CoreWebView2? Core => _core;
    internal bool IsClosed => _closed || _hwnd == 0;
    internal bool IsShown => _shown && !_closed;
    internal double LogicalWidth => _logicalWidth;

    internal event Action? Closed;
    internal event Action? OwnerDestroyed;
    internal event Action<bool>? VisibilityChanged;

    private MousePanelWindow(nint hwnd, nint ownerHwnd)
    {
        _hwnd = hwnd;
        _ownerHwnd = ownerHwnd;
        _winEventProc = OnWinEvent;
        Instances[hwnd] = this;
        HookOwnerEvents();
    }

    internal static MousePanelWindow Create(
        nint ownerHwnd,
        double x,
        double y,
        double width,
        double height,
        bool visible)
    {
        EnsureClassRegistered();
        var (sx, sy, sw, sh) = ComputeScreenBounds(ownerHwnd, x, y, width, height);
        var hwnd = CreateWindowExW(
            WsExToolWindow,
            WindowClassName,
            "Exo Mouse",
            WsPopup | WsClipChildren | WsClipSiblings,
            sx,
            sy,
            sw,
            sh,
            ownerHwnd,
            0,
            GetModuleHandleW(null),
            0);
        if (hwnd == 0)
            throw new InvalidOperationException("Failed to create mouse panel window.");
        var window = new MousePanelWindow(hwnd, ownerHwnd)
        {
            _logicalX = x,
            _logicalY = y,
            _logicalWidth = width,
            _logicalHeight = height,
            _requestedVisible = visible,
            _lastX = sx,
            _lastY = sy,
            _lastW = sw,
            _lastH = sh,
        };
        return window;
    }

    internal void AttachController(CoreWebView2Controller controller)
    {
        _controller = controller;
        _core = controller.CoreWebView2;
        try { controller.DefaultBackgroundColor = Color.FromArgb(255, 0, 0, 0); } catch { }
        SyncPositionAndVisibility(force: true);
    }

    internal void UpdateBounds(double x, double y, double width, double height, bool visible)
    {
        if (_closed || _hwnd == 0) return;
        _logicalX = x;
        _logicalY = y;
        _logicalWidth = width;
        _logicalHeight = height;
        _requestedVisible = visible;
        SyncPositionAndVisibility(force: false);
    }

    internal void SyncPositionAndVisibility(bool force = false)
    {
        if (_closed || _hwnd == 0) return;
        if (_ownerHwnd != 0 && !IsWindow(_ownerHwnd))
        {
            OwnerDestroyed?.Invoke();
            return;
        }
        var ownerVisible = _ownerHwnd == 0 || (IsWindowVisible(_ownerHwnd) && !IsIconic(_ownerHwnd));
        var (sx, sy, sw, sh) = ComputeScreenBounds(_ownerHwnd, _logicalX, _logicalY, _logicalWidth, _logicalHeight);
        if (force || sx != _lastX || sy != _lastY || sw != _lastW || sh != _lastH)
        {
            _lastX = sx;
            _lastY = sy;
            _lastW = sw;
            _lastH = sh;
            SetWindowPos(_hwnd, 0, sx, sy, sw, sh, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            SyncControllerBounds(sw, sh);
        }
        var shouldShow = _requestedVisible && ownerVisible && _logicalWidth >= 2.0 && _logicalHeight >= 2.0;
        if (force || shouldShow != _shown)
        {
            var changed = shouldShow != _shown;
            _shown = shouldShow;
            ShowWindow(_hwnd, shouldShow ? SwShowNoActivate : SwHide);
            if (_controller is { } controller)
            {
                if (shouldShow)
                {
                    try { if (_core is { IsSuspended: true } core) core.Resume(); } catch { }
                    try { controller.IsVisible = true; } catch { }
                }
                else
                {
                    try { controller.IsVisible = false; } catch { }
                    try
                    {
                        if (_core is { IsSuspended: false } core)
                            _ = core.TrySuspendAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
                    }
                    catch { }
                }
            }
            if (changed)
                VisibilityChanged?.Invoke(shouldShow);
        }
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        UnhookOwnerEvents();
        var controller = _controller;
        _controller = null;
        _core = null;
        if (controller is not null)
        {
            WebViewEnvironmentLifetime.CloseAndRelease(controller);
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

    private void SyncControllerBounds(int width, int height)
    {
        if (_controller is not { } controller) return;
        try
        {
            controller.Bounds = new Rect(0, 0, Math.Max(1, width), Math.Max(1, height));
        }
        catch { }
    }

    private void SyncControllerBoundsFromClient()
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
        }
        catch { }
    }

    private static (int X, int Y, int W, int H) ComputeScreenBounds(
        nint ownerHwnd,
        double x,
        double y,
        double width,
        double height)
    {
        var scale = 1.0;
        if (ownerHwnd != 0)
        {
            var dpi = GetDpiForWindow(ownerHwnd);
            if (dpi > 0) scale = dpi / 96.0;
        }
        var px = (int)Math.Round(x * scale);
        var py = (int)Math.Round(y * scale);
        var pw = Math.Max(1, (int)Math.Round(width * scale));
        var ph = Math.Max(1, (int)Math.Round(height * scale));
        if (ownerHwnd != 0 && GetClientRect(ownerHwnd, out var client) && client.Right > 0 && client.Bottom > 0)
        {
            px = Math.Clamp(px, 0, Math.Max(0, client.Right - 1));
            py = Math.Clamp(py, 0, Math.Max(0, client.Bottom - 1));
            pw = Math.Max(1, Math.Min(pw, client.Right - px));
            ph = Math.Max(1, Math.Min(ph, client.Bottom - py));
            var pt = new PointStruct { X = px, Y = py };
            if (ClientToScreen(ownerHwnd, ref pt))
                return (pt.X, pt.Y, pw, ph);
        }
        return (px, py, pw, ph);
    }

    private void HookOwnerEvents()
    {
        if (_ownerHwnd == 0) return;
        var tid = GetWindowThreadProcessId(_ownerHwnd, out var pid);
        if (pid == 0 || tid == 0) return;
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, pid, tid);
        AddHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, pid, tid);
        AddHook(EventObjectDestroy, EventObjectLocationChange, pid, tid);
    }

    private void AddHook(uint minEvent, uint maxEvent, uint pid, uint tid)
    {
        var hook = SetWinEventHook(minEvent, maxEvent, 0, _winEventProc, pid, tid, WinEventOutOfContext);
        if (hook != 0) _hooks.Add(hook);
    }

    private void UnhookOwnerEvents()
    {
        foreach (var hook in _hooks)
        {
            try { UnhookWinEvent(hook); } catch { }
        }
        _hooks.Clear();
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_closed || hwnd != _ownerHwnd || idObject != ObjIdWindow || idChild != ChildIdSelf) return;
        if (eventType == EventObjectDestroy)
        {
            OwnerDestroyed?.Invoke();
            return;
        }
        if (eventType is EventObjectLocationChange
            or EventObjectShow
            or EventObjectHide
            or EventSystemMinimizeStart
            or EventSystemMinimizeEnd
            or EventSystemMoveSizeStart
            or EventSystemMoveSizeEnd)
        {
            SyncPositionAndVisibility(force: false);
        }
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
                host.SyncControllerBoundsFromClient();
            }
            else if (msg == WmDestroy)
            {
                Instances.Remove(hwnd);
                host._hwnd = 0;
                host.UnhookOwnerEvents();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out RectStruct rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref PointStruct point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint hInstance, int lpCursorName);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int fnObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
