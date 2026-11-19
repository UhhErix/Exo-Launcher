using ExoCapture.Native;

namespace ExoCapture.Ui;

/// <summary>What the person picked: a rectangle of the virtual screen, and the window or screen it was, if it was one.</summary>
internal sealed record SnipChoice(Win32.RECT Rect, nint Window, nint Monitor, string Kind);

/// <summary>
/// The snip overlay: the screens frozen as they were (one picture of the whole virtual screen), dimmed, over everything.
/// Drag for a region, click a window for that window, Enter for the screen under the pointer, Esc or a right click to
/// cancel. The words at the top of the screen the pointer is on say so. It draws with GDI from two prepared bitmaps (the
/// picture and its dimmed copy), so it shows at once and holds no GPU.
/// </summary>
internal sealed unsafe class SnipOverlay : UiThread.IWindow
{
    private readonly UiThread _ui;
    private readonly Win32.RECT _screen;
    private readonly List<(nint Hwnd, Win32.RECT Rect)> _windows;
    private readonly Action<SnipChoice?> _done;
    private nint _hwnd, _brightDc, _dimDc, _bright, _dim, _backDc, _back;
    private bool _down, _dragging, _finished;
    private Win32.POINT _start, _at;
    private Win32.RECT? _selection;
    private nint _hoverWindow;
    internal const string Hint = "Drag to snip  |  Click a window  |  Enter for the whole screen  |  Esc to cancel";

    /// <param name="bgra">The virtual screen's picture, top-down BGRA, <paramref name="screen"/>'s size.</param>
    internal SnipOverlay(UiThread ui, Win32.RECT screen, byte[] bgra, List<(nint, Win32.RECT)> windows, Action<SnipChoice?> done)
    {
        (_ui, _screen, _windows, _done) = (ui, screen, windows, done);
        int w = screen.Width, h = screen.Height;
        var screenDc = Win32.GetDC(0);
        (_brightDc, _bright) = Section(screenDc, w, h, out var brightBits);
        (_dimDc, _dim) = Section(screenDc, w, h, out var dimBits);
        (_backDc, _back) = Section(screenDc, w, h, out _);
        Win32.ReleaseDC(0, screenDc);
        fixed (byte* source = bgra)
        {
            Buffer.MemoryCopy(source, brightBits, bgra.Length, bgra.Length);
            var d = (byte*)dimBits;
            for (var i = 0; i < bgra.Length; i += 4)
            {
                d[i] = (byte)(source[i] * 45 / 100);
                d[i + 1] = (byte)(source[i + 1] * 45 / 100);
                d[i + 2] = (byte)(source[i + 2] * 45 / 100);
                d[i + 3] = 255;
            }
        }
    }

    private static (nint Dc, nint Bitmap) Section(nint screenDc, int w, int h, out void* bits)
    {
        var info = new Win32.BITMAPINFOHEADER { Size = (uint)sizeof(Win32.BITMAPINFOHEADER), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        void* pixels;
        var bitmap = Win32.CreateDIBSection(screenDc, &info, 0, &pixels, 0, 0);
        bits = pixels;
        var dc = Win32.CreateCompatibleDC(screenDc);
        Win32.SelectObject(dc, bitmap);
        return (dc, bitmap);
    }

    internal void Show()
    {
        UiThread.Register("ExoCaptureSnip");
        _hwnd = Win32.CreateWindowEx(Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW, "ExoCaptureSnip", "Exo Capture snip", Win32.WS_POPUP,
            _screen.Left, _screen.Top, _screen.Width, _screen.Height, 0, 0, UiThread.Instance, 0);
        UiThread.Attach(_hwnd, this);
        // A second snip or a screenshot never catches this window.
        Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(_hwnd);
        Win32.SetFocus(_hwnd);
        Win32.POINT cursor;
        Win32.GetCursorPos(&cursor);
        _at = cursor;
        Win32.UpdateWindow(_hwnd);
    }

    private void Finish(SnipChoice? choice)
    {
        if (_finished) return;
        _finished = true;
        Win32.ReleaseCapture();
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        UiThread.Detach(_hwnd);
        Win32.DestroyWindow(_hwnd);
        foreach (var dc in new[] { _brightDc, _dimDc, _backDc }) Win32.DeleteDC(dc);
        foreach (var bitmap in new[] { _bright, _dim, _back }) Win32.DeleteObject(bitmap);
        _done(choice);
    }

    private static Win32.POINT Point(nint l) => new() { X = (short)(l & 0xFFFF), Y = (short)((l >> 16) & 0xFFFF) };

    // The window's client coordinates are the virtual screen's, less its corner.
    private Win32.POINT ToScreen(Win32.POINT p) => new() { X = p.X + _screen.Left, Y = p.Y + _screen.Top };

    private (nint Hwnd, Win32.RECT Rect)? WindowAt(Win32.POINT screenPoint)
    {
        foreach (var w in _windows)
            if (w.Rect.Contains(screenPoint.X, screenPoint.Y)) return w;
        return null;
    }

    private static Win32.RECT Normalize(Win32.POINT a, Win32.POINT b) => new()
    {
        Left = Math.Min(a.X, b.X), Top = Math.Min(a.Y, b.Y), Right = Math.Max(a.X, b.X) + 1, Bottom = Math.Max(a.Y, b.Y) + 1,
    };

    public nint Handle(nint hwnd, uint message, nuint w, nint l, out bool handled)
    {
        handled = true;
        switch (message)
        {
            case Win32.WM_SETCURSOR:
                Win32.SetCursor(Win32.LoadCursor(0, 32515 /* IDC_CROSS */));
                return 1;
            case Win32.WM_LBUTTONDOWN:
                _down = true;
                _start = ToScreen(Point(l));
                Win32.SetCapture(hwnd);
                return 0;
            case Win32.WM_MOUSEMOVE:
                _at = ToScreen(Point(l));
                if (_down && (Math.Abs(_at.X - _start.X) > 4 || Math.Abs(_at.Y - _start.Y) > 4)) _dragging = true;
                if (_dragging) _selection = Normalize(_start, _at);
                else
                {
                    var under = WindowAt(_at);
                    _hoverWindow = under?.Hwnd ?? 0;
                    _selection = under?.Rect;
                }
                Win32.InvalidateRect(hwnd, null, false);
                return 0;
            case Win32.WM_LBUTTONUP:
                if (!_down) return 0;
                _down = false;
                var end = ToScreen(Point(l));
                if (_dragging)
                {
                    var rect = Normalize(_start, end);
                    Finish(rect.Width >= 4 && rect.Height >= 4 ? new SnipChoice(rect, 0, 0, "region") : null);
                }
                else if (WindowAt(end) is { } picked) Finish(new SnipChoice(picked.Rect, picked.Hwnd, 0, "window"));
                else Finish(Whole(end));
                return 0;
            case Win32.WM_RBUTTONDOWN:
                Finish(null);
                return 0;
            case Win32.WM_KEYDOWN:
                if ((int)w == Win32.VK_ESCAPE) Finish(null);
                else if ((int)w == Win32.VK_RETURN) Finish(Whole(_at));
                return 0;
            case Win32.WM_KILLFOCUS:
                return 0;
            case Win32.WM_ERASEBKGND:
                return 1;
            case Win32.WM_PAINT:
                Paint(hwnd);
                return 0;
        }
        handled = false;
        return 0;
    }

    private SnipChoice Whole(Win32.POINT at)
    {
        var monitor = Win32.MonitorFromPoint(at, Win32.MONITOR_DEFAULTTONEAREST);
        return new SnipChoice(Win32.MonitorInfo(monitor).Monitor, 0, monitor, "screen");
    }

    private void Paint(nint hwnd)
    {
        Win32.PAINTSTRUCT ps;
        var dc = Win32.BeginPaint(hwnd, &ps);
        int w = _screen.Width, h = _screen.Height;
        Win32.CopyBits(_backDc, 0, 0, w, h, _dimDc, 0, 0, 0x00CC0020 /* SRCCOPY */);
        if (_selection is { } s)
        {
            var x = s.Left - _screen.Left;
            var y = s.Top - _screen.Top;
            Win32.CopyBits(_backDc, x, y, s.Width, s.Height, _brightDc, x, y, 0x00CC0020);
            // The edge: white, with a dark line inside it, visible over a bright picture too.
            var oldBrush = Win32.SelectObject(_backDc, Win32.GetStockObject(5));
            var white = Win32.CreatePen(0, 2, 0xFFFFFF);
            var oldPen = Win32.SelectObject(_backDc, white);
            Win32.Rectangle(_backDc, x - 1, y - 1, x + s.Width + 1, y + s.Height + 1);
            var dark = Win32.CreatePen(0, 1, 0x000000);
            Win32.SelectObject(_backDc, dark);
            Win32.Rectangle(_backDc, x + 1, y + 1, x + s.Width - 1, y + s.Height - 1);
            Win32.SelectObject(_backDc, oldPen);
            Win32.SelectObject(_backDc, oldBrush);
            Win32.DeleteObject(white);
            Win32.DeleteObject(dark);
        }
        // The hint, at the top of the screen the pointer is on.
        var monitor = Win32.MonitorInfo(Win32.MonitorFromPoint(_at, Win32.MONITOR_DEFAULTTONEAREST)).Monitor;
        var scale = Win32.GetDpiForMonitor(Win32.MonitorFromPoint(_at, Win32.MONITOR_DEFAULTTONEAREST), 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var font = Win32.CreateFont(-(int)(14 * scale), 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, _ui.FontFace);
        var oldFont = Win32.SelectObject(_backDc, font);
        var pillW = (int)(640 * scale);
        var pillH = (int)(40 * scale);
        var left = monitor.Left - _screen.Left + (monitor.Width - pillW) / 2;
        var top = monitor.Top - _screen.Top + (int)(24 * scale);
        var pill = new Win32.RECT { Left = left, Top = top, Right = left + pillW, Bottom = top + pillH };
        var black = Win32.CreateSolidBrush(0x000000);
        var edge = Win32.CreatePen(0, 1, 0x3A3A3A);
        var ob = Win32.SelectObject(_backDc, black);
        var op = Win32.SelectObject(_backDc, edge);
        Win32.RoundRect(_backDc, pill.Left, pill.Top, pill.Right, pill.Bottom, pillH, pillH);
        Win32.SelectObject(_backDc, ob);
        Win32.SelectObject(_backDc, op);
        Win32.DeleteObject(black);
        Win32.DeleteObject(edge);
        Win32.SetBkMode(_backDc, 1);
        Win32.SetTextColor(_backDc, 0xFFFFFF);
        Win32.DrawText(_backDc, Hint, -1, &pill, 0x1 | 0x4 | 0x20 /* CENTER | VCENTER | SINGLELINE */);
        Win32.SelectObject(_backDc, oldFont);
        Win32.DeleteObject(font);
        Win32.CopyBits(dc, 0, 0, w, h, _backDc, 0, 0, 0x00CC0020);
        Win32.EndPaint(hwnd, &ps);
    }
}
