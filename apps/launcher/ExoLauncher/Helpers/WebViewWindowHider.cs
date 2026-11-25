using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Helpers;

/// <summary>
/// WebView2 makes its own top-level <c>Chrome_WidgetWin_1</c> window for a surface. Windows lists that window in
/// Alt+Tab and on the taskbar beside Exo's real WinUI window, so the app looked like two blank Exo Launchers.
/// Marking every top-level window of Exo's WebView2 browser process as a tool window leaves Alt+Tab with one entry:
/// Exo itself. Called after each surface starts and once a second, so windows a page opens later are caught too.
/// </summary>
internal static class WebViewWindowHider
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    internal static void Hide(uint browserProcessId)
    {
        if (browserProcessId == 0) return;
        try
        {
            EnumWindows((window, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(window, out var pid);
                    if (pid != browserProcessId) return true;
                    var name = new StringBuilder(64);
                    if (GetClassNameW(window, name, name.Capacity) == 0) return true;
                    if (!name.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return true;
                    var style = GetWindowLongPtrW(window, GwlExStyle).ToInt64();
                    if ((style & WsExToolWindow) != 0 && (style & WsExAppWindow) == 0) return true;
                    SetWindowLongPtrW(window, GwlExStyle, new IntPtr((style | WsExToolWindow) & ~WsExAppWindow));
                    SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            AppLog.Warn("WebView2 window: Alt+Tab exclusion is unavailable: " + ex.Message);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
