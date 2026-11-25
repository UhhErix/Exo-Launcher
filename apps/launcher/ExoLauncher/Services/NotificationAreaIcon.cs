using System.Runtime.InteropServices;

namespace ExoLauncher.Services;

/// <summary>
/// Exo's own notification-area icon, there for as long as Exo runs and gone with it: pressing it opens Exo, its right-press
/// menu has Open and Quit. Windows puts a new icon in the hidden overflow; Exo never asks for more. It keeps nothing alive
/// and starts nothing with Windows: closing Exo still closes Exo. It lives on a hidden window of its own, since a menu needs
/// its owner in front and that must not raise Exo's window, and it polls nothing.
/// </summary>
internal sealed class NotificationAreaIcon : IDisposable
{
    private const string ClassName = "ExoNotificationIcon";
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 77; // WM_APP + 77
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmallIcon = 49;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint MfString = 0x00000000;
    private const uint TpmMenu = 0x0002 /* TPM_RIGHTBUTTON */ | 0x0020 /* TPM_BOTTOMALIGN */ | 0x0080 /* TPM_NONOTIFY */ | 0x0100 /* TPM_RETURNCMD */;
    private const int MenuOpen = 1;
    private const int MenuQuit = 2;
    private const int AllowDarkMenus = 1;

    private static bool s_menusFollowWindows;

    private readonly string _iconPath;
    private readonly Action _open;
    private readonly Action _quit;
    private readonly WndProc _proc;
    private readonly uint _taskbarCreatedMessage;
    private readonly nint _instance;
    private nint _hwnd;
    private nint _icon;
    private bool _shown;
    private bool _menuOpen;

    public NotificationAreaIcon(string iconPath, Action open, Action quit)
    {
        _iconPath = iconPath;
        _open = open;
        _quit = quit;
        _proc = OnMessage;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _instance = GetModuleHandle(null);
        var type = new WndClassEx { Size = Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = _instance, ClassName = ClassName };
        if (RegisterClassEx(ref type) == 0) return;
        // A plain hidden popup rather than a message-only window, which Explorer's restart broadcast doesn't reach.
        _hwnd = CreateWindowEx(WsExToolWindow, ClassName, "Exo Launcher", WsPopup, 0, 0, 0, 0, 0, 0, _instance, 0);
    }

    public bool Show()
    {
        if (_hwnd == 0) return false;
        if (_shown) return true;
        if (_icon == 0 && File.Exists(_iconPath))
        {
            // Windows' own size for it at this screen's scale, from the file's frame made for that size.
            var size = GetSystemMetricsForDpi(SmCxSmallIcon, GetDpiForWindow(_hwnd));
            _icon = LoadImage(0, _iconPath, ImageIcon, size, size, LrLoadFromFile);
        }
        if (_icon == 0) return false;
        var data = CreateData();
        if (!Shell_NotifyIcon(NimAdd, ref data)) return false;
        data.uTimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
        _shown = true;
        return true;
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        if (_shown)
        {
            var data = CreateData();
            Shell_NotifyIcon(NimDelete, ref data);
            _shown = false;
        }
        DestroyWindow(_hwnd);
        _hwnd = 0;
        UnregisterClass(ClassName, _instance);
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "Exo Launcher",
    };

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (ShouldRecreateAfterShellRestart(message, _taskbarCreatedMessage, _shown))
            {
                // Explorer discards every notification-area icon when its taskbar process restarts, and the taskbar may be
                // at another scale now: the size is read again. Without resetting _shown, Show() would think it survived.
                _shown = false;
                if (_icon != 0)
                {
                    DestroyIcon(_icon);
                    _icon = 0;
                }
                Show();
            }
            if (message == CallbackMessage)
            {
                // Under version 4 a press is its selection (NIN_SELECT, Enter's NIN_KEYSELECT) and the right press its menu,
                // with where to draw it in wParam.
                switch (unchecked((uint)lParam.ToInt64()) & 0xFFFF)
                {
                    case NinSelect or NinKeySelect: _open(); break;
                    case WmContextMenu: ShowMenu(unchecked((short)wParam.ToInt64()), unchecked((short)(wParam.ToInt64() >> 16))); break;
                }
            }
        }
        catch { }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        if (_menuOpen) return;
        _menuOpen = true;
        var menu = CreatePopupMenu();
        try
        {
            FollowWindowsTheme();
            AppendMenu(menu, MfString, MenuOpen, "Open");
            AppendMenu(menu, MfString, MenuQuit, "Quit");
            // Windows closes a tray menu on a click elsewhere only while its owner is in front, and only after a message
            // reaches the owner once it ends: the documented pair around TrackPopupMenu.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TpmMenu, x, y, _hwnd, 0);
            PostMessage(_hwnd, WmNull, 0, 0);
            if (chosen == MenuOpen) _open();
            else if (chosen == MenuQuit) _quit();
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    /// <summary>Win32 menus are light unless asked; this one follows Windows' dark or light, as Exo's dark shell sits on it.</summary>
    private static void FollowWindowsTheme()
    {
        if (s_menusFollowWindows) return;
        s_menusFollowWindows = true;
        // uxtheme's SetPreferredAppMode and FlushMenuThemes have only ordinals; a Windows without them keeps a light menu.
        try
        {
            SetPreferredAppMode(AllowDarkMenus);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }
    }

    internal static bool ShouldRecreateAfterShellRestart(
        uint message,
        uint taskbarCreatedMessage,
        bool shown) =>
        shown && taskbarCreatedMessage != 0 && message == taskbarCreatedMessage;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public nint Proc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint loadFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClassEx type);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string type, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}
