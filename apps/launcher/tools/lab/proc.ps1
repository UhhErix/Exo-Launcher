<#
  Process side of Exo Lab (tools/lab/run.mjs). Stays running and answers one JSON line per command read from stdin, so a
  room's samples cost no PowerShell start-up and no Add-Type compile.

  sample   The app's process tree (the root is the process at -Exe with a window; descendants by parent id and creation
           time): per process its CPU time, working set, private bytes, private working set and per-thread context
           switches (cs: thread id, count, thread id, count, ...), and
           for msedgewebview2 its --type= (no command line leaves this script).
  game     Whether Exo's Local\ExoLauncher.GameRunning event is set (a game runs).
  machine  GPU names, refresh rate, CPU, memory, Windows build, and this script's own priority class.
#>
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
(Get-Process -Id $PID).PriorityClass = 'Idle'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ExoLabProc {
  [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
  // SYSTEM_PROCESS_INFORMATION (x64): one NtQuerySystemInformation call reads every process and thread counter at once;
  // the PerfProc_Thread WMI class takes seconds for the same context switch counts.
  // Per process: { cpu (100 ns), working set, private bytes, private working set, threads, then thread id and context
  // switches for each thread } (a thread that exits takes its count with it, so the caller sums per thread).
  public static Dictionary<long, long[]> Sample(HashSet<long> pids) {
    int len = 1 << 20, ret;
    while (true) {
      IntPtr buf = Marshal.AllocHGlobal(len);
      try {
        int st = NtQuerySystemInformation(5, buf, len, out ret);
        if (st == unchecked((int)0xC0000004)) { len = Math.Max(len * 2, ret + 65536); continue; }
        if (st != 0) throw new InvalidOperationException("NtQuerySystemInformation 0x" + st.ToString("X"));
        var map = new Dictionary<long, long[]>();
        long at = 0;
        while (true) {
          IntPtr p = new IntPtr(buf.ToInt64() + at);
          int next = Marshal.ReadInt32(p);
          int threads = Marshal.ReadInt32(p, 4);
          long pid = Marshal.ReadIntPtr(p, 0x50).ToInt64();
          if (pids.Contains(pid)) {
            var row = new long[5 + threads * 2];
            row[0] = Marshal.ReadInt64(p, 0x28) + Marshal.ReadInt64(p, 0x30);
            row[1] = Marshal.ReadIntPtr(p, 0x90).ToInt64();
            row[2] = Marshal.ReadIntPtr(p, 0xB8).ToInt64();
            row[3] = Marshal.ReadInt64(p, 0x08);
            row[4] = threads;
            for (int i = 0; i < threads; i++) {
              int t = 0x100 + i * 0x50;
              row[5 + i * 2] = Marshal.ReadIntPtr(p, t + 0x30).ToInt64();
              row[6 + i * 2] = (uint)Marshal.ReadInt32(p, t + 0x40);
            }
            map[pid] = row;
          }
          if (next == 0) break;
          at += next;
        }
        return map;
      } finally { Marshal.FreeHGlobal(buf); }
    }
  }
}
'@

$clock = [Diagnostics.Stopwatch]::StartNew()
$base = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$name = [IO.Path]::GetFileNameWithoutExtension($Exe)

function Get-Tree {
    $root = Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $root) { $root = Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object Path -eq $Exe | Sort-Object StartTime | Select-Object -First 1 }
    if (-not $root) { return $null }
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, CreationDate
    $byId = @{}; $kids = @{}
    foreach ($p in $all) {
        $byId[[int]$p.ProcessId] = $p
        $pp = [int]$p.ParentProcessId
        if (-not $kids.ContainsKey($pp)) { $kids[$pp] = [Collections.Generic.List[int]]::new() }
        $kids[$pp].Add([int]$p.ProcessId)
    }
    $out = [Collections.Generic.List[object]]::new()
    $queue = [Collections.Generic.Queue[int]]::new(); $queue.Enqueue($root.Id)
    $seen = [Collections.Generic.HashSet[int]]::new()
    while ($queue.Count) {
        $id = $queue.Dequeue()
        if (-not $seen.Add($id) -or -not $byId[$id]) { continue }
        $p = $byId[$id]
        $type = if ($p.Name -eq 'msedgewebview2.exe') { $m = [regex]::Match([string]$p.CommandLine, '--type=([\w-]+)'); if ($m.Success) { $m.Groups[1].Value } else { 'browser' } } else { '' }
        $out.Add([ordered]@{ pid = $id; ppid = [int]$p.ParentProcessId; name = $p.Name; type = $type })
        foreach ($k in $kids[$id]) { if ($byId[$k].CreationDate -ge $p.CreationDate) { $queue.Enqueue($k) } }
    }
    [ordered]@{ root = $root.Id; priority = "$($root.PriorityClass)"; procs = $out }
}

while ($null -ne ($line = [Console]::In.ReadLine())) {
    try {
        switch ($line.Trim()) {
            'sample' {
                $tree = Get-Tree
                if (-not $tree) { $result = [ordered]@{ ok = $false; error = "no $name running from that path" }; break }
                $set = [Collections.Generic.HashSet[long]]::new()
                foreach ($p in $tree.procs) { [void]$set.Add($p.pid) }
                $at = $base + $clock.Elapsed.TotalMilliseconds
                $counters = [ExoLabProc]::Sample($set)
                $procs = foreach ($p in $tree.procs) {
                    $c = $counters[[long]$p.pid]
                    if (-not $c) { continue }
                    [ordered]@{ pid = $p.pid; name = $p.name; type = $p.type; cpuMs = $c[0] / 10000.0; ws = $c[1]; private = $c[2]; privateWs = $c[3]; threads = $c[4]; cs = @(if ($c.Length -gt 5) { $c[5..($c.Length - 1)] }) }
                }
                $result = [ordered]@{ ok = $true; at = $at; root = $tree.root; rootPriority = $tree.priority; procs = @($procs) }
            }
            'game' {
                $h = $null
                $running = [Threading.EventWaitHandle]::TryOpenExisting('Local\ExoLauncher.GameRunning', [ref]$h) -and $h.WaitOne(0)
                if ($h) { $h.Dispose() }
                $result = [ordered]@{ ok = $true; running = [bool]$running }
            }
            'machine' {
                $gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentRefreshRate -gt 0 -or $_.AdapterRAM -gt 0 })
                $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
                $os = Get-CimInstance Win32_OperatingSystem
                $result = [ordered]@{
                    ok = $true
                    gpus = @($gpus | ForEach-Object { [ordered]@{ name = $_.Name; refreshHz = [int]$_.CurrentRefreshRate; width = [int]$_.CurrentHorizontalResolution; height = [int]$_.CurrentVerticalResolution; driver = $_.DriverVersion } })
                    cpu = $cpu.Name.Trim(); cores = [int]$cpu.NumberOfCores; logical = [int]$cpu.NumberOfLogicalProcessors
                    memoryGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
                    windows = "$($os.Caption) $($os.Version)"
                    samplerPriority = "$((Get-Process -Id $PID).PriorityClass)"
                }
            }
            default { $result = [ordered]@{ ok = $false; error = "unknown command $line" } }
        }
    } catch { $result = [ordered]@{ ok = $false; error = $_.Exception.Message } }
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 5 -Compress))
    [Console]::Out.Flush()
}
