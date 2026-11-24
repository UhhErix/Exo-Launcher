#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$Command,
    [Parameter(Mandatory)][int]$TargetPid,
    [string]$Name,
    [string]$ControlType,
    [string]$ExcludeNames,
    [int]$MinY = 0,
    [string]$Key,
    [int]$WaitSec = 25
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('ParityUiaWin' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ParityUiaWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public delegate bool WindowEnum(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool EnumWindows(WindowEnum cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr FindWindowForPid(int targetPid) {
    IntPtr found = IntPtr.Zero;
    int bestArea = -1;
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if ((int)pid != targetPid || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      int area = Math.Max(0, r.R - r.L) * Math.Max(0, r.B - r.T);
      if (area > bestArea) { bestArea = area; found = h; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@
}

[ParityUiaWin]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Get-RootElement([int]$pidVal) {
    $hwnd = [ParityUiaWin]::FindWindowForPid($pidVal)
    if ($hwnd -eq [IntPtr]::Zero) { return $null }
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    return @{ hwnd = $hwnd; el = $el }
}

function Focus-Target([IntPtr]$hwnd) {
    if ([ParityUiaWin]::IsIconic($hwnd)) { [ParityUiaWin]::ShowWindow($hwnd, 9) | Out-Null }
    [ParityUiaWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [ParityUiaWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [ParityUiaWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 180
}

function Get-ElementSummary($el) {
    $c = $el.Current
    $r = $c.BoundingRectangle
    $rect = if ([double]::IsInfinity($r.X) -or [double]::IsNaN($r.X)) {
        $null
    } else {
        [ordered]@{ x = [int][math]::Round($r.X); y = [int][math]::Round($r.Y); w = [int][math]::Round($r.Width); h = [int][math]::Round($r.Height) }
    }
    $ct = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
    [ordered]@{
        name = $c.Name
        controlType = $ct
        automationId = $c.AutomationId
        isOffscreen = $c.IsOffscreen
        isEnabled = $c.IsEnabled
        rect = $rect
    }
}

function Invoke-Element($el, [IntPtr]$hwnd, [int]$pidVal) {
    $invokeObj = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokeObj)) {
        try {
            ([System.Windows.Automation.InvokePattern]$invokeObj).Invoke()
            return [ordered]@{ ok = $true; how = 'InvokePattern'; target = (Get-ElementSummary $el) }
        } catch {}
    }
    $selectObj = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectObj)) {
        try {
            ([System.Windows.Automation.SelectionItemPattern]$selectObj).Select()
            return [ordered]@{ ok = $true; how = 'SelectionItemPattern'; target = (Get-ElementSummary $el) }
        } catch {}
    }
    $r = $el.Current.BoundingRectangle
    if ([double]::IsInfinity($r.X) -or [double]::IsNaN($r.X) -or $r.Width -lt 2 -or $r.Height -lt 2) {
        return [ordered]@{ ok = $false; why = 'Element has no clickable bounding rectangle and no InvokePattern' }
    }
    Focus-Target $hwnd
    $cx = [int][math]::Round($r.X + $r.Width / 2.0)
    $cy = [int][math]::Round($r.Y + $r.Height / 2.0)
    $pt = New-Object ParityUiaWin+POINT
    $pt.X = $cx
    $pt.Y = $cy
    $top = [ParityUiaWin]::GetAncestor([ParityUiaWin]::WindowFromPoint($pt), 2)
    [uint32]$owner = 0
    [ParityUiaWin]::GetWindowThreadProcessId($top, [ref]$owner) | Out-Null
    if ([int]$owner -ne $pidVal) {
        return [ordered]@{ ok = $false; why = "Point ($cx,$cy) belongs to PID $owner, not target PID $pidVal" }
    }
    $was = New-Object ParityUiaWin+POINT
    [ParityUiaWin]::GetCursorPos([ref]$was) | Out-Null
    [ParityUiaWin]::SetCursorPos($cx, $cy) | Out-Null
    Start-Sleep -Milliseconds 30
    [ParityUiaWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 25
    [ParityUiaWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [ParityUiaWin]::SetCursorPos($was.X, $was.Y) | Out-Null
    return [ordered]@{ ok = $true; how = 'ClickRect'; target = (Get-ElementSummary $el) }
}

switch ($Command) {
    'window' {
        $root = Get-RootElement $TargetPid
        if (-not $root) {
            [ordered]@{ ok = $false; why = 'no visible window for target pid' } | ConvertTo-Json -Compress
            break
        }
        $r = New-Object ParityUiaWin+RECT
        [ParityUiaWin]::GetWindowRect($root.hwnd, [ref]$r) | Out-Null
        [ordered]@{
            ok = $true
            pid = $TargetPid
            hwnd = [int64]$root.hwnd
            rect = [ordered]@{ x = $r.L; y = $r.T; w = $r.R - $r.L; h = $r.B - $r.T }
        } | ConvertTo-Json -Depth 4 -Compress
    }
    'place-second' {
        Add-Type -AssemblyName System.Windows.Forms
        $second = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
        if (-not $second) {
            [ordered]@{ ok = $false; why = 'no second screen' } | ConvertTo-Json -Compress
            break
        }
        $root = Get-RootElement $TargetPid
        if (-not $root) {
            [ordered]@{ ok = $false; why = 'no visible window for target pid' } | ConvertTo-Json -Compress
            break
        }
        $a = $second.WorkingArea
        $w = [Math]::Min(1600, $a.Width - 40)
        $h = [Math]::Min(960, $a.Height - 40)
        $x = $a.X + [int](($a.Width - $w) / 2)
        $y = $a.Y + [int](($a.Height - $h) / 2)
        [ParityUiaWin]::SetWindowPos($root.hwnd, [IntPtr]::Zero, $x, $y, $w, $h, 0x0040) | Out-Null
        [ordered]@{ ok = $true; x = $x; y = $y; w = $w; h = $h } | ConvertTo-Json -Compress
    }
    'tree' {
        $root = Get-RootElement $TargetPid
        if (-not $root) {
            [ordered]@{ ok = $false; why = 'no visible window for target pid'; items = @() } | ConvertTo-Json -Compress
            break
        }
        $all = $root.el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $items = @()
        $limit = [Math]::Min($all.Count, 250)
        for ($i = 0; $i -lt $limit; $i++) {
            try { $items += (Get-ElementSummary $all.Item($i)) } catch {}
        }
        [ordered]@{ ok = $true; count = $all.Count; items = $items } | ConvertTo-Json -Depth 5 -Compress
    }
    'invoke' {
        $root = Get-RootElement $TargetPid
        if (-not $root) {
            [ordered]@{ ok = $false; why = 'no visible window for target pid' } | ConvertTo-Json -Compress
            break
        }
        $all = $root.el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $match = $null
        $fallback = $null
        for ($i = 0; $i -lt $all.Count; $i++) {
            $el = $all.Item($i)
            $c = $el.Current
            if (-not $c.IsEnabled) { continue }
            $ct = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            if ($c.Name -eq $Name) {
                if (-not $ControlType -or $ct -eq $ControlType) {
                    $match = $el
                    break
                }
                if (-not $fallback -and ($ct -eq 'Button' -or $ct -eq 'TabItem' -or $ct -eq 'MenuItem' -or $ct -eq 'ListItem')) {
                    $fallback = $el
                }
            }
        }
        $chosen = if ($match) { $match } else { $fallback }
        if (-not $chosen) {
            [ordered]@{ ok = $false; why = "UIA element '$Name' (ControlType='$ControlType') not found" } | ConvertTo-Json -Compress
            break
        }
        (Invoke-Element $chosen $root.hwnd $TargetPid) | ConvertTo-Json -Depth 5 -Compress
    }
    'invoke-first' {
        $root = Get-RootElement $TargetPid
        if (-not $root) {
            [ordered]@{ ok = $false; why = 'no visible window for target pid' } | ConvertTo-Json -Compress
            break
        }
        $excluded = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        if ($ExcludeNames) {
            foreach ($part in ($ExcludeNames -split ',')) {
                $t = $part.Trim()
                if ($t) { $excluded.Add($t) | Out-Null }
            }
        }
        $rWin = New-Object ParityUiaWin+RECT
        [ParityUiaWin]::GetWindowRect($root.hwnd, [ref]$rWin) | Out-Null
        $minScreenY = $rWin.T + $MinY
        $all = $root.el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $chosen = $null
        for ($i = 0; $i -lt $all.Count; $i++) {
            $el = $all.Item($i)
            $c = $el.Current
            if (-not $c.IsEnabled -or $c.IsOffscreen) { continue }
            $ct = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            if ($ControlType -and $ct -ne $ControlType) { continue }
            if ([string]::IsNullOrWhiteSpace($c.Name)) { continue }
            if ($excluded.Contains($c.Name.Trim())) { continue }
            $br = $c.BoundingRectangle
            if ([double]::IsInfinity($br.Y) -or [double]::IsNaN($br.Y)) { continue }
            if ($br.Y -lt $minScreenY -or $br.Width -lt 40 -or $br.Height -lt 40) { continue }
            $chosen = $el
            break
        }
        if (-not $chosen) {
            [ordered]@{ ok = $false; why = "No matching clickable UIA element found (ControlType='$ControlType', MinY=$MinY)" } | ConvertTo-Json -Compress
            break
        }
        (Invoke-Element $chosen $root.hwnd $TargetPid) | ConvertTo-Json -Depth 5 -Compress
    }
    'key' {
        $root = Get-RootElement $TargetPid
        if (-not $root) {
            [ordered]@{ ok = $false; why = 'no visible window for target pid' } | ConvertTo-Json -Compress
            break
        }
        Focus-Target $root.hwnd
        $fg = [ParityUiaWin]::GetForegroundWindow()
        if ($fg -ne $root.hwnd) {
            [ordered]@{ ok = $false; why = 'could not focus target window before sending key' } | ConvertTo-Json -Compress
            break
        }
        if ($Key -eq 'Escape') {
            [ParityUiaWin]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 25
            [ParityUiaWin]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
        } elseif ($Key -eq 'CtrlK') {
            [ParityUiaWin]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 20
            [ParityUiaWin]::keybd_event(0x4B, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 25
            [ParityUiaWin]::keybd_event(0x4B, 0, 2, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 20
            [ParityUiaWin]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
        } else {
            [ordered]@{ ok = $false; why = "unsupported key '$Key'" } | ConvertTo-Json -Compress
            break
        }
        [ordered]@{ ok = $true; key = $Key } | ConvertTo-Json -Compress
    }
    'close' {
        $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
        if (-not $p) {
            [ordered]@{ ok = $true; alreadyExited = $true } | ConvertTo-Json -Compress
            break
        }
        $hwnd = [ParityUiaWin]::FindWindowForPid($TargetPid)
        if ($hwnd -ne [IntPtr]::Zero) {
            [ParityUiaWin]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        } else {
            $p.CloseMainWindow() | Out-Null
        }
        $exited = $p.WaitForExit($WaitSec * 1000)
        [ordered]@{ ok = $exited; exited = $exited } | ConvertTo-Json -Compress
    }
    default {
        throw "Unknown command: $Command"
    }
}
