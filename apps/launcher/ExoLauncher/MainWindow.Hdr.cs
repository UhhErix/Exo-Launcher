using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private SubclassProc? _displayChangeProc;

    private void StartHdrForGames()
    {
        App.Services.HdrHost.Start();
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _displayChangeProc = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (message == WmDisplayChange) { App.Services.HdrHost.SyncSoon(); NvidiaTrayIcon.DisplayChanged(); }
            else if (message == taskbarCreated && taskbarCreated != 0) NvidiaTrayIcon.Soon();
            else if (message == WmNcDestroy) RemoveWindowSubclass(hwnd, _displayChangeProc!, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        try { SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _displayChangeProc, 0x48445220, 0); }
        catch (Exception ex) { AppLog.Debug("HDR: display change hook failed: " + ex.GetType().Name); }
    }

    internal static List<string> PendingHdrLaunches(Dictionary<string, long> launching, IEnumerable<string> running, long now, long graceMs) =>
        HdrHostService.PendingLaunches(launching, running, now, graceMs);

    internal void SyncHdrSoon() => App.Services.HdrHost.SyncSoon();

    internal static bool WantsHdr(string mode, bool? supports) => HdrHostService.WantsHdr(mode, supports);

    internal static string? SdrGammaReplaced(SettingsService settings, string devicePath) =>
        HdrHostService.SdrGammaReplaced(settings, devicePath);

    private void StopHdrForGames() => App.Services.HdrHost.Stop();

    private const uint WmDisplayChange = 0x007E, WmNcDestroy = 0x0082;
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nuint lParam, nuint subclassId, nuint refData);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint refData);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nuint lParam);
}
