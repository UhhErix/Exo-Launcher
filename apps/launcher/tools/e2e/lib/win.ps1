<#
  Windows side of Exo's end-to-end suite (tools/e2e/lib/win.mjs calls it): what the screen really shows and the real
  mouse, which the page's own CDP view can't stand in for (3.13.3's 4K page was whole in CDP's screenshots and cut
  off on the screen). Each command prints one JSON object. Physical pixels throughout (per-monitor DPI aware).

  window                     Exo's main window: client rectangle on screen, maximized, foreground, its monitor.
  monitors                   Every monitor: bounds, work area, DPI scale, primary.
  front                      Brings Exo's window to the front.
  capture -Out <png>         The screen under Exo's client area, as shown (Exo is brought to the front first).
  click -X <x> -Y <y>        One real left click at a screen point; the cursor goes back where it was.
  move -X <x> -Y <y>         The real cursor to a screen point (hover).
  place -X -Y -W -H          Restores Exo's window and puts it at that rectangle.
  maximize | restore         Exo's window.
  close -W <seconds>         Asks any running installed Exo to close (WM_CLOSE) and waits up to <seconds> for exit.
  proc                       Exo and the WebView2 processes under it: CPU seconds and memory.
  udp                        UDP endpoints Exo holds (a voice call holds some).
  shown -Name <process>      That process's windows a person could see: visible, not minimized or cloaked, on a monitor.
  precompiled                Which of Exo's own assemblies shipped without ReadyToRun code.
  snip -Name <Return|Escape> Waits for Exo Capture's snip overlay to show (uncloaked), then gives it that key as a
                             message to its own window (no system-wide input): Return snips the screen under the
                             cursor, Escape cancels. Prints when it showed (Unix ms).
#>
param(
    [Parameter(Mandatory)][string]$Command,
    [string]$Out,
    [string]$Name,
    [int]$X, [int]$Y, [int]$W, [int]$H
)
$ErrorActionPreference = 'Stop'

if (-not ('ExoWin' -as [type])) {
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ExoWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MONITORINFOEX {
    public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
  [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
  public delegate bool MonitorEnum(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX i);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum cb, IntPtr data);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  public delegate bool WindowEnum(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(WindowEnum cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
  /** Top-level windows of these processes that show on a monitor (DWMWA_CLOAKED = 14, MONITOR_DEFAULTTONULL = 0). */
  public static List<string> Shown(int[] pids) {
    var set = new HashSet<int>(pids); var list = new List<string>();
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (!set.Contains((int)pid) || !IsWindowVisible(h) || IsIconic(h)) return true;
      int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
      RECT r; GetWindowRect(h, out r);
      if (r.R - r.L < 32 || r.B - r.T < 32 || MonitorFromWindow(h, 0) == IntPtr.Zero) return true;
      list.Add((r.R - r.L) + "x" + (r.B - r.T) + " at " + r.L + "," + r.T);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
  [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
  [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
  [DllImport("user32.dll")] private static extern bool GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
  [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
  [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
  [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
  [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w; public IntPtr l; public uint time; public POINT pt; }
  public static int CloseByTitle(int targetPid, string title, int timeoutMs) {
    int closed = CloseByTitleOnce(targetPid, title);
    if (closed > 0 || timeoutMs <= 0) return closed;
    WinEventProc cb = (hook, ev, h, obj, child, tid, time) => {
      if (obj != 0) return;
      int n = CloseByTitleOnce(targetPid, title);
      if (n > 0) { closed += n; PostQuitMessage(0); }
    };
    IntPtr hook = SetWinEventHook(0x8002, 0x800C, IntPtr.Zero, cb, (uint)targetPid, 0, 0);
    UIntPtr timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)timeoutMs, IntPtr.Zero);
    try {
      closed = CloseByTitleOnce(targetPid, title);
      if (closed == 0) {
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0)) {
          if (msg.msg == 0x0113) { closed = CloseByTitleOnce(targetPid, title); break; }
        }
      }
    } finally {
      if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);
      if (hook != IntPtr.Zero) UnhookWinEvent(hook);
      GC.KeepAlive(cb);
    }
    return closed;
  }
  private static int CloseByTitleOnce(int targetPid, string title) {
    int closed = 0;
    var sb = new System.Text.StringBuilder(256);
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if ((int)pid != targetPid) return true;
      sb.Clear(); GetWindowText(h, sb, sb.Capacity);
      if (sb.ToString() == title) {
        PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
        closed++;
      }
      return true;
    }, IntPtr.Zero);
    return closed;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  /** Exo Capture's snip overlay once it shows: { at (Unix ms), hwnd }, or null after the timeout. */
  public static long[] SnipShown(int timeoutMs) {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < timeoutMs) {
      IntPtr h = FindWindow("ExoCaptureSnip", "Exo Capture snip");
      if (h == IntPtr.Zero) h = FindWindow("ExoCapturePopup", "Exo Capture snip");
      int cloaked = 1;
      if (h != IntPtr.Zero && IsWindowVisible(h) && DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked == 0)
        return new long[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), h.ToInt64() };
      System.Threading.Thread.Sleep(1);
    }
    return null;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  /** Exo's WinUI main window HWND; .NET Process.MainWindowHandle can pick auxiliary UAC/IME windows on Win11 26H2. */
  public static IntPtr MainWindow(int targetPid, IntPtr fallback) {
    IntPtr exact = IntPtr.Zero, visible = IntPtr.Zero, any = IntPtr.Zero;
    var cls = new System.Text.StringBuilder(128);
    var title = new System.Text.StringBuilder(256);
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if ((int)pid != targetPid) return true;
      cls.Clear(); GetClassName(h, cls, cls.Capacity);
      if (cls.ToString() != "WinUIDesktopWin32WindowClass") return true;
      if (any == IntPtr.Zero) any = h;
      if (visible == IntPtr.Zero && IsWindowVisible(h)) visible = h;
      title.Clear(); GetWindowText(h, title, title.Capacity);
      if (title.ToString() == "Exo Launcher") { exact = h; return false; }
      return true;
    }, IntPtr.Zero);
    return exact != IntPtr.Zero ? exact : (visible != IntPtr.Zero ? visible : (any != IntPtr.Zero ? any : fallback));
  }
  [DllImport("user32.dll", SetLastError = true)] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetProcessWindowStation();
  [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(uint tid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
  [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ProcessIdToSessionId(uint pid, out uint sid);
  [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
  [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, byte[] pvInfo, uint nLength, out uint need);
  [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr h);
  public static string UserObjectName(IntPtr h) {
    if (h == IntPtr.Zero) return null;
    uint need = 0;
    GetUserObjectInformation(h, 2, null, 0, out need);
    if (need == 0) return null;
    var buf = new byte[need];
    if (!GetUserObjectInformation(h, 2, buf, need, out need)) return null;
    return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
  }
  public static string InputDesktopName(out int err) {
    IntPtr h = OpenInputDesktop(0, false, 0x0001);
    if (h == IntPtr.Zero) { err = Marshal.GetLastWin32Error(); return null; }
    err = 0;
    string name = UserObjectName(h);
    CloseDesktop(h);
    return name;
  }
  public static List<IntPtr> Monitors() {
    var list = new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) => { list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
}
'@
}
# Per-monitor DPI aware v2: every rectangle and point in physical pixels.
[ExoWin]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Get-ExoHwnd($p) {
    $h = [ExoWin]::MainWindow($p.Id, $p.MainWindowHandle)
    if ($h -eq [IntPtr]::Zero) { throw 'Exo has no window' }
    $h
}

function Get-ExoWindow {
    $exe = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe'
    $procs = @(Get-Process ExoLauncher -ErrorAction SilentlyContinue)
    $ordered = if ($env:EXO_E2E_TEST_INSTANCE -eq '1') {
        @($procs | Where-Object { $_.Path -ne $exe })
    } else {
        @($procs | Where-Object { $_.Path -eq $exe }) + @($procs | Where-Object { $_.Path -ne $exe })
    }
    foreach ($p in $ordered) {
        if ([ExoWin]::MainWindow($p.Id, $p.MainWindowHandle) -ne [IntPtr]::Zero) { return $p }
    }
    throw 'Exo has no window'
}

function Monitor-Info([IntPtr]$m) {
    $i = New-Object ExoWin+MONITORINFOEX; $i.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($i)
    [ExoWin]::GetMonitorInfo($m, [ref]$i) | Out-Null
    $dx = [uint32]96; $dy = [uint32]96; [ExoWin]::GetDpiForMonitor($m, 0, [ref]$dx, [ref]$dy) | Out-Null
    [ordered]@{
        device = $i.szDevice; primary = [bool]($i.dwFlags -band 1); scale = [math]::Round($dx / 96, 3)
        x = $i.rcMonitor.L; y = $i.rcMonitor.T; w = $i.rcMonitor.R - $i.rcMonitor.L; h = $i.rcMonitor.B - $i.rcMonitor.T
        work = [ordered]@{ x = $i.rcWork.L; y = $i.rcWork.T; w = $i.rcWork.R - $i.rcWork.L; h = $i.rcWork.B - $i.rcWork.T }
    }
}

function Window-Info($p) {
    $h = Get-ExoHwnd $p
    $c = New-Object ExoWin+RECT; [ExoWin]::GetClientRect($h, [ref]$c) | Out-Null
    $o = New-Object ExoWin+POINT; [ExoWin]::ClientToScreen($h, [ref]$o) | Out-Null
    [ordered]@{
        pid = $p.Id; hwnd = [int64]$h
        client = [ordered]@{ x = $o.X; y = $o.Y; w = $c.R - $c.L; h = $c.B - $c.T }
        maximized = [ExoWin]::IsZoomed($h); minimized = [ExoWin]::IsIconic($h)
        foreground = ([ExoWin]::GetForegroundWindow() -eq $h)
        monitor = Monitor-Info ([ExoWin]::MonitorFromWindow($h, 2))
    }
}

function Bring-Front($p) {
    $h = Get-ExoHwnd $p
    if ([ExoWin]::IsIconic($h)) { [ExoWin]::ShowWindow($h, 9) | Out-Null }
    # Windows lets a process take the foreground right after a key press of its own: a tap of Alt, then the switch.
    [ExoWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [ExoWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [ExoWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 250
}

function Get-CaptureDiagnostics {
    [uint32]$sid = 0
    [ExoWin]::ProcessIdToSessionId([ExoWin]::GetCurrentProcessId(), [ref]$sid) | Out-Null
    $inErr = 0
    $inDesk = [ExoWin]::InputDesktopName([ref]$inErr)
    [ordered]@{
        sessionId = [int]$sid
        activeConsoleSessionId = [int][ExoWin]::WTSGetActiveConsoleSessionId()
        winSta = [ExoWin]::UserObjectName([ExoWin]::GetProcessWindowStation())
        threadDesktop = [ExoWin]::UserObjectName([ExoWin]::GetThreadDesktop([ExoWin]::GetCurrentThreadId()))
        inputDesktop = $inDesk
        inputDesktopError = $inErr
        logonUiRunning = @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count -gt 0
    }
}

function Save-PrintWindow([IntPtr]$hwnd, [string]$outPath, [uint32]$flags, $client) {
    $wr = New-Object ExoWin+RECT
    [ExoWin]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $ww = $wr.R - $wr.L; $wh = $wr.B - $wr.T
    if ($ww -le 0 -or $wh -le 0 -or $client.w -le 0 -or $client.h -le 0) {
        return @{ ok = $false; err = 0; message = "invalid window or client dimensions (${ww}x${wh}, client $($client.w)x$($client.h))" }
    }
    $clientOnly = ($flags -band 1) -ne 0
    $bw = if ($clientOnly) { $client.w } else { $ww }
    $bh = if ($clientOnly) { $client.h } else { $wh }
    $full = New-Object System.Drawing.Bitmap $bw, $bh, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($full)
        try {
            $hdc = $g.GetHdc()
            try {
                $ok = [ExoWin]::PrintWindow($hwnd, $hdc, $flags)
                $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            } finally {
                $g.ReleaseHdc($hdc)
            }
        } finally {
            $g.Dispose()
        }
        if (-not $ok) {
            return @{ ok = $false; err = $err; message = "PrintWindow(flags=$flags) returned false (Win32 error $err)" }
        }
        if ($clientOnly) {
            $full.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
            return @{ ok = $true; err = 0; message = $null }
        }
        $ox = [math]::Max(0, $client.x - $wr.L)
        $oy = [math]::Max(0, $client.y - $wr.T)
        $cw = [math]::Min($client.w, $bw - $ox)
        $ch = [math]::Min($client.h, $bh - $oy)
        if ($cw -le 0 -or $ch -le 0) {
            return @{ ok = $false; err = 0; message = "client crop out of window bounds (offset ${ox},${oy} in ${bw}x${bh})" }
        }
        $cropped = New-Object System.Drawing.Bitmap $cw, $ch, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $gc = [System.Drawing.Graphics]::FromImage($cropped)
            try {
                $dst = New-Object System.Drawing.Rectangle 0, 0, $cw, $ch
                $src = New-Object System.Drawing.Rectangle $ox, $oy, $cw, $ch
                $gc.DrawImage($full, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)
            } finally {
                $gc.Dispose()
            }
            $cropped.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
            return @{ ok = $true; err = 0; message = $null }
        } finally {
            $cropped.Dispose()
        }
    } finally {
        $full.Dispose()
    }
}

switch ($Command) {
    'window' { Window-Info (Get-ExoWindow) | ConvertTo-Json -Depth 5 -Compress }
    'monitors' { @([ExoWin]::Monitors() | ForEach-Object { Monitor-Info $_ }) | ConvertTo-Json -Depth 5 -Compress }
    'front' { $p = Get-ExoWindow; Bring-Front $p; Window-Info $p | ConvertTo-Json -Depth 5 -Compress }
    'capture' {
        $p = Get-ExoWindow; Bring-Front $p
        $h = Get-ExoHwnd $p
        $info = Window-Info $p; $c = $info.client
        $diag = Get-CaptureDiagnostics
        $copyErr = $null
        try {
            $bmp = New-Object System.Drawing.Bitmap $c.w, $c.h
            try {
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                try { $g.CopyFromScreen($c.x, $c.y, 0, 0, $bmp.Size) } finally { $g.Dispose() }
                $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $bmp.Dispose()
            }
            $info.ok = $true
            $info.captureMethod = 'CopyFromScreen'
            $info.captureDiag = $diag
            $info | ConvertTo-Json -Depth 6 -Compress
            break
        } catch {
            $inner = $_.Exception.InnerException
            $code = if ($inner -is [System.ComponentModel.Win32Exception]) { " (Win32 $($inner.NativeErrorCode))" } else { '' }
            $copyErr = "$(if ($inner) { $inner.Message } else { $_.Exception.Message })$code"
        }
        $pw = Save-PrintWindow $h $Out 2 $c
        if (-not $pw.ok) {
            $pw3 = Save-PrintWindow $h $Out 3 $c
            if ($pw3.ok) {
                $info.ok = $true
                $info.captureMethod = 'PrintWindowClient'
                $info.copyError = $copyErr
                $info.captureDiag = $diag
                $info | ConvertTo-Json -Depth 6 -Compress
                break
            }
            $info.ok = $false
            $info.captureMethod = 'none'
            $info.copyError = $copyErr
            $info.printWindowError = "$($pw.message); $($pw3.message)"
            $info.captureDiag = $diag
            $info | ConvertTo-Json -Depth 6 -Compress
            break
        }
        $info.ok = $true
        $info.captureMethod = 'PrintWindow'
        $info.copyError = $copyErr
        $info.captureDiag = $diag
        $info | ConvertTo-Json -Depth 6 -Compress
    }
    'click' {
        # Only onto Exo's own window: another window over it at that point (a game, another app's full screen) takes no click.
        $exo = Get-ExoWindow; $exoHwnd = Get-ExoHwnd $exo; $at = New-Object ExoWin+POINT; $at.X = $X; $at.Y = $Y
        $hit = [ExoWin]::WindowFromPoint($at)
        $top = [ExoWin]::GetAncestor($hit, 2); [uint32]$owner = 0
        [ExoWin]::GetWindowThreadProcessId($top, [ref]$owner) | Out-Null
        $title = New-Object System.Text.StringBuilder 256; [ExoWin]::GetWindowText($top, $title, 256) | Out-Null
        $ours = ($top -eq $exoHwnd) -or (((Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName -eq 'msedgewebview2') -and ($title.ToString() -eq 'Exo Launcher'))
        if (-not $ours) { [ordered]@{ ok = $false; why = 'the window there is ' + (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName } | ConvertTo-Json -Compress; break }
        $diag = Get-CaptureDiagnostics
        if ($diag.threadDesktop -and $diag.inputDesktop -and ($diag.threadDesktop -ne $diag.inputDesktop)) {
            [ordered]@{
                ok = $false
                nonInputDesktop = $true
                why = "process desktop $($diag.winSta)\$($diag.threadDesktop) is not the active input desktop $($diag.winSta)\$($diag.inputDesktop); OS mouse input only targets the active input desktop"
                diag = $diag
            } | ConvertTo-Json -Depth 5 -Compress
            break
        }
        $was = New-Object ExoWin+POINT; [ExoWin]::GetCursorPos([ref]$was) | Out-Null
        [ExoWin]::SetCursorPos($X, $Y) | Out-Null; Start-Sleep -Milliseconds 80
        [ExoWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50; [ExoWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150; [ExoWin]::SetCursorPos($was.X, $was.Y) | Out-Null
        '{"ok":true}'
    }
    'move' { [ExoWin]::SetCursorPos($X, $Y) | Out-Null; '{"ok":true}' }
    'place' {
        $p = Get-ExoWindow; $h = Get-ExoHwnd $p
        [ExoWin]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 200
        [ExoWin]::SetWindowPos($h, [IntPtr]::Zero, $X, $Y, $W, $H, 0x0014) | Out-Null   # no z-order change, no activate
        Start-Sleep -Milliseconds 300
        Window-Info $p | ConvertTo-Json -Depth 5 -Compress
    }
    'maximize' { $p = Get-ExoWindow; [ExoWin]::ShowWindow((Get-ExoHwnd $p), 3) | Out-Null; Start-Sleep -Milliseconds 400; Window-Info $p | ConvertTo-Json -Depth 5 -Compress }
    'restore' { $p = Get-ExoWindow; [ExoWin]::ShowWindow((Get-ExoHwnd $p), 9) | Out-Null; Start-Sleep -Milliseconds 400; Window-Info $p | ConvertTo-Json -Depth 5 -Compress }
    'close' {
        $exe = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe'
        $timeoutSec = if ($W -gt 0) { $W } else { 40 }
        foreach ($p in @(Get-Process ExoLauncher -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)) {
            $h = [ExoWin]::MainWindow($p.Id, $p.MainWindowHandle)
            if ($h -ne [IntPtr]::Zero) {
                if (-not [ExoWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) { throw 'Exo did not accept a normal close' }
            } else {
                foreach ($ui in @(Get-CimInstance Win32_Process -Filter "Name='exo-ui.exe' AND ParentProcessId=$($p.Id)")) {
                    try { [void](Get-Process -Id $ui.ProcessId -ErrorAction Stop).CloseMainWindow() } catch { }
                }
            }
            if (-not $p.WaitForExit($timeoutSec * 1000)) { throw "Exo did not exit in $timeoutSec s" }
        }
        '{"ok":true}'
    }
    'proc' {
        $exo = Get-ExoWindow
        $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name
        $tree = @($exo.Id); $grew = $true
        while ($grew) { $kids = @($all | Where-Object { $tree -contains $_.ParentProcessId -and $tree -notcontains $_.ProcessId } | ForEach-Object ProcessId); $grew = $kids.Count -gt 0; $tree += $kids }
        $procs = @($tree | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        $byName = [ordered]@{}
        foreach ($g in ($procs | Group-Object Name)) {
            $byName[$g.Name] = [ordered]@{ count = $g.Count; cpuSeconds = [math]::Round(($g.Group | Measure-Object -Property CPU -Sum).Sum, 3); workingSetMB = [math]::Round(($g.Group | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB, 1) }
        }
        [ordered]@{
            pid = $exo.Id; count = $procs.Count
            cpuSeconds = [math]::Round(($procs | Measure-Object -Property CPU -Sum).Sum, 3)
            workingSetMB = [math]::Round(($procs | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB, 1)
            exoWorkingSetMB = [math]::Round($exo.WorkingSet64 / 1MB, 1)
            names = @($procs | Group-Object Name | ForEach-Object { "$($_.Name) x$($_.Count)" })
            byName = $byName
        } | ConvertTo-Json -Depth 5 -Compress
    }
    'closeTitle' {
        $exo = Get-ExoWindow
        [ordered]@{ closed = [ExoWin]::CloseByTitle($exo.Id, $Name, $W) } | ConvertTo-Json -Compress
    }
    'waitWebview' {
        $exo = Get-ExoWindow
        $timeoutMs = if ($H -gt 0) { $H } else { 5000 }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        function Get-TreeWv([int]$rootPid) {
            $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name
            $tree = @($rootPid); $grew = $true
            while ($grew) {
                $kids = @($all | Where-Object { $tree -contains $_.ParentProcessId -and $tree -notcontains $_.ProcessId } | ForEach-Object ProcessId)
                $grew = $kids.Count -gt 0
                $tree += $kids
            }
            @($tree | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue } | Where-Object { $_.ProcessName -eq 'msedgewebview2' })
        }
        $wv = Get-TreeWv $exo.Id
        if ($Name -eq 'atMost') {
            while ($wv.Count -gt $W -and $sw.ElapsedMilliseconds -lt $timeoutMs) {
                $rem = [int][Math]::Max(1, $timeoutMs - $sw.ElapsedMilliseconds)
                $handles = @($wv | ForEach-Object { try { $_.SafeHandle } catch { $null } } | Where-Object { $_ -ne $null })
                if ($handles.Count -gt 0) {
                    $waitables = @($wv | ForEach-Object { $_.WaitForExitAsync() })
                    [Threading.Tasks.Task]::WhenAny($waitables).Wait($rem) | Out-Null
                } else {
                    break
                }
                $wv = Get-TreeWv $exo.Id
            }
        } else {
            if ($wv.Count -lt $W) {
                $evName = 'ExoWvSpawn_' + [guid]::NewGuid().ToString('N')
                Register-CimIndicationEvent -Query "SELECT * FROM __InstanceCreationEvent WITHIN 0.25 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = 'msedgewebview2.exe'" -SourceIdentifier $evName | Out-Null
                try {
                    $wv = Get-TreeWv $exo.Id
                    while ($wv.Count -lt $W -and $sw.ElapsedMilliseconds -lt $timeoutMs) {
                        $remSec = [Math]::Max(1, [int][Math]::Ceiling(($timeoutMs - $sw.ElapsedMilliseconds) / 1000.0))
                        $ev = Wait-Event -SourceIdentifier $evName -Timeout $remSec
                        if ($null -eq $ev) { break }
                        Remove-Event -EventIdentifier $ev.EventIdentifier -ErrorAction SilentlyContinue
                        $wv = Get-TreeWv $exo.Id
                    }
                } finally {
                    Unregister-Event -SourceIdentifier $evName -ErrorAction SilentlyContinue
                    Remove-Event -SourceIdentifier $evName -ErrorAction SilentlyContinue
                }
            }
        }
        $wsMb = [math]::Round((($wv | Measure-Object -Property WorkingSet64 -Sum).Sum) / 1MB, 1)
        [ordered]@{ count = $wv.Count; workingSetMB = $wsMb; ms = [int]$sw.ElapsedMilliseconds } | ConvertTo-Json -Compress
    }
    'udp' {
        $ids = @(Get-Process ExoLauncher -ErrorAction SilentlyContinue | ForEach-Object Id)
        $count = if ($ids.Count) { @(Get-NetUDPEndpoint -OwningProcess $ids -ErrorAction SilentlyContinue).Count } else { 0 }
        [ordered]@{ count = $count; pids = $ids } | ConvertTo-Json -Compress
    }
    'games' {
        $names = 'VALORANT-Win64-Shipping', 'VALORANT', 'League of Legends', 'LeagueOfLegends', 'TFTClient-Win64-Shipping', 'cs2', 'dota2', 'FortniteClient-Win64-Shipping', 'RobloxPlayerBeta'
        $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName -Unique | Sort-Object)
        [ordered]@{ running = $running } | ConvertTo-Json -Compress
    }
    'shown' {
        $ids = [int[]]@(Get-Process -Name $Name -ErrorAction SilentlyContinue | ForEach-Object Id)
        [ordered]@{ running = $ids.Count -gt 0; shown = @(if ($ids.Count) { [ExoWin]::Shown($ids) }) } | ConvertTo-Json -Compress
    }
    'precompiled' {
        $dir = Split-Path (Get-ExoWindow).Path
        $names = 'ExoLauncher.dll', 'Microsoft.WinUI.dll'
        $plain = @($names | Where-Object {
            $stream = [IO.File]::OpenRead((Join-Path $dir $_))
            try { ([Reflection.PortableExecutable.PEReader]::new($stream)).PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size -eq 0 }
            finally { $stream.Dispose() }
        })
        [ordered]@{ checked = $names; plain = $plain } | ConvertTo-Json -Compress
    }
    'snip' {
        # Ready: the type above is compiled; the caller starts the snip after this line.
        [Console]::Out.WriteLine('{"ready":' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + '}'); [Console]::Out.Flush()
        $shown = [ExoWin]::SnipShown(15000)
        if (-not $shown) { '{"shown":false}'; break }
        # WM_KEYDOWN, VK_RETURN (13) or VK_ESCAPE (27).
        [ExoWin]::PostMessage([IntPtr]$shown[1], 0x100, [IntPtr]$(if ($Name -eq 'Return') { 13 } else { 27 }), [IntPtr]0) | Out-Null
        [ordered]@{ shown = $true; at = $shown[0] } | ConvertTo-Json -Compress
    }
    default { throw "unknown command $Command" }
}
