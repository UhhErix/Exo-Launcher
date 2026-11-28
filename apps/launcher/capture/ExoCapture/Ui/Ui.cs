using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExoCapture.Native;

namespace ExoCapture.Ui;

/// <summary>A key combination as Exo names it ("Ctrl+Alt+F10", "PrintScreen").</summary>
internal readonly record struct Hotkey(uint Modifiers, uint Key)
{
    internal static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": mods |= Win32.MOD_CONTROL; continue;
                case "alt": mods |= Win32.MOD_ALT; continue;
                case "shift": mods |= Win32.MOD_SHIFT; continue;
                case "win": mods |= Win32.MOD_WIN; continue;
            }
            key = part switch
            {
                "printscreen" or "prtsc" or "print" => Win32.VK_SNAPSHOT,
                "space" => 0x20,
                "home" => 0x24,
                "end" => 0x23,
                "insert" => 0x2D,
                "delete" or "del" => 0x2E,
                "pageup" => 0x21,
                "pagedown" => 0x22,
                "pause" => 0x13,
                "scrolllock" => 0x91,
                _ when part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]) => char.ToUpperInvariant(part[0]),
                _ when part.Length is 2 or 3 && part[0] == 'f' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
                _ => 0,
            };
            if (key == 0) return null;
        }
        return key == 0 ? null : new Hotkey(mods, key);
    }
}

/// <summary>
/// Exo Capture's one window thread: the hotkeys (RegisterHotKey on a message-only window), the toasts that say a clip
/// was saved, and the snip overlay. Everything else posts work here with <see cref="Invoke"/>.
/// </summary>
internal sealed unsafe class UiThread
{
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private nint _messages;
    private readonly ManualResetEventSlim _ready = new(false);
    private static readonly ConcurrentDictionary<nint, IWindow> Windows = new();
    internal static nint Instance => Win32.GetModuleHandleW(0);
    internal event Action<int>? Hotkey;
    private readonly HashSet<int> _registered = [];
    internal string FontFace { get; private set; } = "Segoe UI Variable Text";

    internal interface IWindow
    {
        nint Handle(nint hwnd, uint message, nuint w, nint l, out bool handled);
    }

    internal UiThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo Capture windows" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    internal static void Register(string name)
    {
        fixed (char* className = name)
        {
            var cls = new Win32.WNDCLASSEXW
            {
                Size = (uint)sizeof(Win32.WNDCLASSEXW),
                WndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WndProc,
                Instance = Instance,
                Cursor = Win32.LoadCursor(0, 32512 /* IDC_ARROW */),
                ClassName = className,
            };
            Win32.RegisterClassEx(&cls);
        }
    }

    internal static void Attach(nint hwnd, IWindow window) => Windows[hwnd] = window;
    internal static void Detach(nint hwnd) => Windows.TryRemove(hwnd, out _);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nuint w, nint l)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window))
            {
                var result = window.Handle(hwnd, message, w, l, out var handled);
                if (handled) return result;
            }
        }
        catch (Exception ex) { Host.Log("Window: " + ex.Message); }
        return Win32.DefWindowProc(hwnd, message, w, l);
    }

    private sealed class MessageWindow(UiThread ui) : IWindow
    {
        public nint Handle(nint hwnd, uint message, nuint w, nint l, out bool handled)
        {
            handled = true;
            switch (message)
            {
                case Win32.WM_HOTKEY: ui.Hotkey?.Invoke((int)w); return 0;
                case Win32.WM_APP:
                    while (ui._work.TryDequeue(out var action))
                        try { action(); } catch (Exception ex) { Host.Log("Window job: " + ex.Message); }
                    return 0;
            }
            handled = false;
            return 0;
        }
    }

    private void Run()
    {
        Register("ExoCaptureMessages");
        _messages = Win32.CreateWindowEx(0, "ExoCaptureMessages", "Exo Capture", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, Instance, 0);
        Attach(_messages, new MessageWindow(this));
        _ready.Set();
        Win32.MSG msg;
        while (Win32.GetMessage(&msg, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessage(&msg);
        }
    }

    internal void Invoke(Action action)
    {
        _work.Enqueue(action);
        Win32.PostMessage(_messages, Win32.WM_APP, 0, 0);
    }

    internal T Call<T>(Func<T> func)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return func();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(() => { try { done.SetResult(func()); } catch (Exception ex) { done.SetException(ex); } });
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Registers a hotkey under an id; false when another app already holds those keys.</summary>
    internal bool SetHotkey(int id, Hotkey? keys) => Call(() =>
    {
        if (_registered.Remove(id)) Win32.UnregisterHotKey(_messages, id);
        if (keys is not { } k) return true;
        var ok = Win32.RegisterHotKey(_messages, id, k.Modifiers | Win32.MOD_NOREPEAT, k.Key);
        if (ok) _registered.Add(id);
        return ok;
    });

    /// <summary>Exo's own typeface (Geist) for the toast and the snip's words.</summary>
    internal void UseFont(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Invoke(() =>
        {
            if (Win32.AddFontResourceEx(path, 0x10 /* FR_PRIVATE */, 0) > 0) FontFace = "Geist";
        });
    }

    private Toast? _toast;

    /// <summary>A note at the corner of the screen the game is on, for 2.5 seconds; never in a clip (excluded from capture).</summary>
    internal void Toast(string title, string detail, nint monitor) => Invoke(() =>
    {
        _toast ??= new Toast(this);
        _toast.Show(title, detail, monitor);
    });
}

/// <summary>The "Clip saved" card: black, rounded, Exo's type, at the bottom right of the screen, gone after 2.5 seconds.</summary>
internal sealed unsafe class Toast : UiThread.IWindow
{
    private readonly nint _hwnd;
    private readonly UiThread _ui;
    private string _title = "", _detail = "";
    private const int Width = 300, Height = 64;

    internal Toast(UiThread ui)
    {
        _ui = ui;
        UiThread.Register("ExoCapturePopup");
        _hwnd = Win32.CreateWindowEx(Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE,
            "ExoCapturePopup", "Exo Capture", Win32.WS_POPUP, 0, 0, Width, Height, 0, 0, UiThread.Instance, 0);
        UiThread.Attach(_hwnd, this);
        Win32.SetLayeredWindowAttributes(_hwnd, 0, 240, 2 /* LWA_ALPHA */);
        Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    internal void Show(string title, string detail, nint monitor)
    {
        (_title, _detail) = (title, detail);
        if (monitor == 0) monitor = Win32.MonitorFromWindow(Win32.GetForegroundWindow(), Win32.MONITOR_DEFAULTTONEAREST);
        var work = Win32.MonitorInfo(monitor).Work;
        var scale = Win32.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var (w, h) = ((int)(Width * scale), (int)(Height * scale));
        Win32.SetWindowPos(_hwnd, -1 /* TOPMOST */, work.Right - w - (int)(24 * scale), work.Bottom - h - (int)(24 * scale), w, h, 0x0010 | 0x0040 /* NOACTIVATE | SHOWWINDOW */);
        Win32.InvalidateRect(_hwnd, null, true);
        Win32.SetTimer(_hwnd, 1, 2500, 0);
    }

    public nint Handle(nint hwnd, uint message, nuint w, nint l, out bool handled)
    {
        handled = true;
        switch (message)
        {
            case Win32.WM_TIMER:
                Win32.KillTimer(hwnd, 1);
                Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                return 0;
            case Win32.WM_ERASEBKGND: return 1;
            case Win32.WM_PAINT:
                Win32.PAINTSTRUCT ps;
                var dc = Win32.BeginPaint(hwnd, &ps);
                Win32.RECT rect;
                Win32.GetClientRect(hwnd, &rect);
                var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
                var black = Win32.CreateSolidBrush(0x000000);
                Win32.FillRect(dc, &rect, black);
                Win32.DeleteObject(black);
                var edge = Win32.CreatePen(0, 1, 0x2A2A2A);
                var oldPen = Win32.SelectObject(dc, edge);
                var oldBrush = Win32.SelectObject(dc, Win32.GetStockObject(5 /* NULL_BRUSH */));
                Win32.RoundRect(dc, 0, 0, rect.Right, rect.Bottom, (int)(16 * scale), (int)(16 * scale));
                Win32.SelectObject(dc, oldPen);
                Win32.SelectObject(dc, oldBrush);
                Win32.DeleteObject(edge);
                Win32.SetBkMode(dc, 1);
                var titleFont = Win32.CreateFont(-(int)(15 * scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, _ui.FontFace);
                var bodyFont = Win32.CreateFont(-(int)(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, _ui.FontFace);
                var pad = (int)(16 * scale);
                var line = new Win32.RECT { Left = pad, Top = (int)(12 * scale), Right = rect.Right - pad, Bottom = rect.Bottom / 2 + (int)(4 * scale) };
                var old = Win32.SelectObject(dc, titleFont);
                Win32.SetTextColor(dc, 0xFFFFFF);
                Win32.DrawText(dc, _title, -1, &line, 0x20 | 0x8000 /* SINGLELINE | END_ELLIPSIS */);
                Win32.SelectObject(dc, bodyFont);
                Win32.SetTextColor(dc, 0xA0A0A0);
                line.Top = rect.Bottom / 2 + (int)(2 * scale);
                line.Bottom = rect.Bottom - (int)(8 * scale);
                Win32.DrawText(dc, _detail, -1, &line, 0x20 | 0x8000);
                Win32.SelectObject(dc, old);
                Win32.DeleteObject(titleFont);
                Win32.DeleteObject(bodyFont);
                Win32.EndPaint(hwnd, &ps);
                return 0;
        }
        handled = false;
        return 0;
    }
}
