using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Truthful "is any Exo window in front" state. WinUI's Window.Activated is
/// unreliable with WebView2 child HWNDs (focus moving into a webview raises
/// Deactivated, and an alt-tab away from a focused webview raises nothing),
/// so the foreground window is read on every foreground change instead. The
/// hook is delivered on the thread that started the watcher (the UI thread).
/// It also says why Exo came to the front: Exo's own reasons are logged where
/// it asks (<see cref="Asked"/>), a click or key just before is the user's, and
/// anything else is logged with the window that had the front. Started quiet
/// (<see cref="StartedQuiet"/>), Exo gives such a front straight back until the
/// user first comes to it, and for two minutes logs what activates, moves or
/// maximizes its windows, with the code that did it.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint GaRootOwner = 3;
    private const uint GaRoot = 2;
    private const uint WineventSkipOwnProcess = 0x0002;
    // How long Exo's own request, or the user's click or key, explains Exo coming to the front.
    private const long AskedForMs = 3000;
    private const uint InputForMs = 1500;

    /// Exo must not take the front at start when: EXO_NO_ACTIVATE=1 (a restart while someone plays, an install
    /// script's, a test copy); --startup (launched silently at Windows sign-in); a game runs (another Exo Launcher's
    /// GameRunning signal is set); or the window in front fills its whole screen (a game or video in full screen).
    /// Started any other way, Exo comes to the front as asked.
    internal static readonly bool StartedQuiet = Environment.GetEnvironmentVariable("EXO_NO_ACTIVATE") == "1" || HasStartupArg(Environment.GetCommandLineArgs()) || GameSignalled() || FullScreenInFront();

    /// <summary>True when started at Windows sign-in via the Run value's --startup argument.</summary>
    internal static bool HasStartupArg(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
            if (string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool GameSignalled()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(Services.GameRunningSignal.Name);
            return handle.WaitOne(0);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool FullScreenInFront()
    {
        var front = GetForegroundWindow();
        if (front == 0 || front == GetShellWindow() || front == GetDesktopWindow() || !GetWindowRect(front, out var r)) return false;
        var monitor = MonitorFromWindow(front, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info)
            && r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] private struct QuietRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public QuietRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool GetWindowRect(nint window, out QuietRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    private static long _askedAt = long.MinValue / 2;

    /// <summary>
    /// A top-level window came to the front or was shown, by its process id (raised on the UI thread; the hook
    /// costs nothing while no window changes). Exo's own windows are included: the subscriber skips them.
    /// </summary>
    internal static event Action<uint>? WindowActivity;

    /// <summary>Both hooks (foreground and show) are installed; false means nothing reports window starts.</summary>
    internal static bool ActivityHooked { get; private set; }

    private readonly nint _window;
    private readonly Action<bool> _changed;
    private readonly WinEventProc _proc;
    private readonly WinEventProc _showProc;
    private nint _hook;
    private nint _showHook;
    private bool _inFront = true;
    private bool _failureLogged;
    // Until the user first comes to Exo after a quiet start.
    private bool _quiet = StartedQuiet;
    // The last window in front that wasn't Exo's: where an unasked front goes back to.
    private nint _before;
    private QuietTrace? _trace;

    private ForegroundWatcher(nint window, Action<bool> changed)
    {
        _window = window;
        _changed = changed;
        _proc = OnForeground;
        if (_quiet) _trace = new QuietTrace(window);
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WineventOutOfContext);
        if (_hook == IntPtr.Zero) AppLog.Warn("Foreground hook could not be installed; Exo stays in foreground mode.");
        _showProc = OnShow;
        _showHook = SetWinEventHook(Services.WindowStarts.EventObjectShow, Services.WindowStarts.EventObjectShow, IntPtr.Zero, _showProc, 0, 0,
            WineventOutOfContext | WineventSkipOwnProcess);
        ActivityHooked = _hook != IntPtr.Zero && _showHook != IntPtr.Zero;
        if (!ActivityHooked) AppLog.Warn("Window start events unavailable; running games are checked every 15 s.");
        OnForeground(IntPtr.Zero, EventSystemForeground, GetForegroundWindow(), 0, 0, 0, 0);
    }

    internal static ForegroundWatcher Start(nint window, Action<bool> changed) => new(window, changed);

    internal static bool ExoInFront(nint window) => ExoInFront(window, GetForegroundWindow());

    /// The process whose window is in front now, or 0.
    internal static uint ForegroundProcess()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid;
    }

    /// Exo is about to take the front for <paramref name="why"/>, which is logged; the change that follows is Exo's own.
    internal static void Asked(string why)
    {
        Interlocked.Exchange(ref _askedAt, Environment.TickCount64);
        AppLog.Info("Foreground: Exo takes the front: " + why + ".");
    }

    /// Exo coming to the front now is wanted: Exo asked for it, or the user just clicked or pressed a key.
    internal static bool Wanted() =>
        Environment.TickCount64 - Interlocked.Read(ref _askedAt) < AskedForMs || UserJustActed();

    /// The front to <paramref name="to"/>. Windows lets only a thread sharing the foreground's input state move it,
    /// so this one joins it for the call.
    internal static bool GiveFront(nint to)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var self = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try { return SetForegroundWindow(to); }
        finally { if (attached) AttachThreadInput(self, foregroundThread, false); }
    }

    // Any window of Exo's process counts (main window, pop-outs, dialogs), and
    // so does every window Exo owns: the hosted clients are owned popups of
    // Exo's window, and their contained dialogs are owned by it too.
    private static bool ExoInFront(nint window, nint foreground)
    {
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId || GetAncestor(foreground, GaRootOwner) == window;
    }

    private void OnShow(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (Services.WindowStarts.IsTopLevelShow(evt, idObject, idChild, GetAncestor(hwnd, GaRoot) == hwnd)) RaiseActivity(hwnd);
    }

    private static void RaiseActivity(nint hwnd)
    {
        var handler = WindowActivity;
        if (handler is null) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;
        // A native callback: an exception escaping here ends the process.
        try { handler(pid); }
        catch (Exception error) { AppLog.Debug("Window activity handler failed: " + error.GetType().Name); }
    }

    private void OnForeground(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != IntPtr.Zero) RaiseActivity(hwnd);
        var front = hwnd != IntPtr.Zero ? hwnd : GetForegroundWindow();
        var inFront = ExoInFront(_window, front);
        if (!inFront && front != IntPtr.Zero) _before = front;
        if (inFront == _inFront) return;
        _inFront = inFront;
        // A native callback: an exception escaping here ends the process.
        try
        {
            if (inFront) Arrived(front);
            _changed(inFront);
        }
        catch (Exception error)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            AppLog.Error("Foreground change handler failed.", error);
        }
    }

    // Exo came to the front. Asked or the user's: nothing to say (an ask is logged where it is made), and a quiet start
    // is over. Anything else is logged; while the start is quiet, the front goes straight back to the window that had it.
    private void Arrived(nint front)
    {
        if (Wanted())
        {
            if (!_quiet) return;
            _quiet = false;
            _trace?.Dispose();
            _trace = null;
            AppLog.Info("Foreground: the user came to Exo; the quiet start is over.");
            return;
        }
        var what = $"Foreground: Exo came to the front with nothing asking for it ({Describe(front)}, from {Describe(_before)})";
        if (_quiet && _before != IntPtr.Zero && IsWindowVisible(_before) && !IsIconic(_before) && GiveFront(_before))
            AppLog.Info(what + "; gave it straight back.");
        else AppLog.Info(what + ".");
    }

    private static bool UserJustActed()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) && unchecked((uint)Environment.TickCount - info.Time) < InputForMs;
    }

    // "RocketLeague (LaunchUnrealUWindowsClient)": the process and window class, never the title (a page's, a file's).
    private static string Describe(nint hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return "no window";
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
        catch (ArgumentException) { process = "process " + pid; }
        var name = new char[128];
        var length = GetClassName(hwnd, name, name.Length);
        return process + " (" + new string(name, 0, Math.Max(0, length)) + ")";
    }

    public void Dispose()
    {
        _trace?.Dispose();
        _trace = null;
        ActivityHooked = false;
        if (_showHook != IntPtr.Zero) { UnhookWinEvent(_showHook); _showHook = IntPtr.Zero; }
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>
    /// After a quiet start, for two minutes or until the user comes to Exo: each time an Exo window is activated, Exo
    /// becomes the foreground app, or the main window is moved, sized or shown, with the code that did it. A thread hook
    /// on messages sent to the UI thread, so a request made by Exo's own code arrives with that code on the stack, and
    /// one from Windows, WinUI or WebView2 says so.
    /// </summary>
    private sealed class QuietTrace : IDisposable
    {
        private const int WhCallWndProc = 4;
        private const uint WmActivate = 0x0006, WmShowWindow = 0x0018, WmActivateApp = 0x001C, WmWindowPosChanging = 0x0046;
        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
        private const int MaxLines = 80;

        private readonly nint _window;
        private readonly HookProc _proc;
        private readonly long _until = Environment.TickCount64 + 120_000;
        private nint _hook;
        private int _lines;

        internal QuietTrace(nint window)
        {
            _window = window;
            _proc = OnMessage;
            _hook = SetWindowsHookEx(WhCallWndProc, _proc, IntPtr.Zero, GetCurrentThreadId());
        }

        private nint OnMessage(int code, nint wParam, nint lParam)
        {
            var hook = _hook;
            // Most messages are none of the four: only the id is read for them.
            if (code >= 0 && hook != IntPtr.Zero &&
                (uint)Marshal.ReadInt32(lParam, 2 * IntPtr.Size) is WmActivate or WmShowWindow or WmActivateApp or WmWindowPosChanging)
            {
                try { Trace(Marshal.PtrToStructure<CwpStruct>(lParam)); }
                catch { /* a trace line is never worth a failed message */ }
            }
            var result = CallNextHookEx(hook, code, wParam, lParam);
            if (Environment.TickCount64 > _until || _lines >= MaxLines) Dispose();
            return result;
        }

        private void Trace(CwpStruct message)
        {
            var main = message.Hwnd == _window;
            var what = message.Message switch
            {
                WmActivateApp when message.WParam != 0 => "Exo becomes the foreground app",
                WmActivate when (message.WParam & 0xFFFF) != 0 && GetAncestor(message.Hwnd, 2) == message.Hwnd =>
                    "activating " + Describe(message.Hwnd) + ((message.WParam & 0xFFFF) == 2 ? " by a click" : ""),
                WmShowWindow when main => message.WParam != 0 ? "showing the window" : "hiding the window",
                WmWindowPosChanging when main => Placing(Marshal.PtrToStructure<WindowPos>(message.LParam)),
                _ => null,
            };
            if (what is null) return;
            _lines++;
            AppLog.Info("Foreground trace: " + what + "; from " + Caller() + ".");
        }

        // Moves, sizes, shows and activations of the main window; a frame or z-order change alone is not news.
        private static string? Placing(WindowPos pos)
        {
            // Windows ignores the place under SWP_NOMOVE and the size under SWP_NOSIZE: only what changes is named.
            var parts = new List<string>();
            if ((pos.Flags & SwpNoMove) == 0) parts.Add($"to {pos.X},{pos.Y}");
            if ((pos.Flags & SwpNoSize) == 0) parts.Add($"at {pos.Width}x{pos.Height}");
            if ((pos.Flags & SwpShowWindow) != 0) parts.Add("showing it");
            if ((pos.Flags & SwpNoActivate) == 0) parts.Add("activating it");
            return parts.Count == 0 ? null : "placing the window " + string.Join(", ", parts);
        }

        // The managed code under the hook, innermost first. None of Exo's own code (only the message loop) means
        // Windows, WinUI or WebView2 sent it on its own.
        private static string Caller()
        {
            var names = new List<string>();
            foreach (var frame in new StackTrace(false).GetFrames())
            {
                if (frame.GetMethod() is not { DeclaringType: { } type } method || type == typeof(QuietTrace)) continue;
                var space = type.Namespace ?? "";
                if (!space.StartsWith("ExoLauncher", StringComparison.Ordinal) && !space.StartsWith("Microsoft.UI", StringComparison.Ordinal) &&
                    !space.StartsWith("ABI.Microsoft", StringComparison.Ordinal) && !space.StartsWith("Microsoft.Web", StringComparison.Ordinal)) continue;
                var name = Name(type, method);
                if (name is "Program.Main" or "Application.Start" or "IApplicationStaticsMethods.Start") continue;
                names.Add(name);
                if (names.Count == 6) break;
            }
            return names.Count == 0 ? "the message loop (no Exo code: Windows, WinUI or WebView2)" : string.Join(" < ", names);
        }

        // "MainWindow.RestoreWindowPlacement", with async state machines and lambdas named for the method they sit in.
        private static string Name(Type type, MethodBase method)
        {
            var owner = type;
            while (owner.Name.StartsWith('<') && owner.DeclaringType is { } outer) owner = outer;
            var name = (type.Name.StartsWith('<') && method.Name == "MoveNext" ? type.Name : method.Name).TrimStart('<');
            if (name.IndexOf('>') is > 0 and var end) name = name[..end];
            return owner.Name + "." + name;
        }

        public void Dispose()
        {
            var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CwpStruct { public nint LParam; public nint WParam; public uint Message; public nint Hwnd; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos { public nint Hwnd; public nint After; public int X; public int Y; public int Width; public int Height; public uint Flags; }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, char[] name, int length);
}
