// SPDX-License-Identifier: MIT

import { spawn, execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const launcherRoot = path.resolve(__dirname, '..', '..', '..')
const hostExe = path.join(launcherRoot, 'ExoLauncher', 'bin', 'x64', 'Debug', 'net10.0-windows10.0.26100.0', 'win-x64', 'ExoLauncher.exe')
const port = 19229
const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'exo-lab-wv2-'))

const t0 = performance.now()
const child = spawn(hostExe, [], {
  cwd: path.dirname(hostExe),
  env: {
    ...process.env,
    EXO_LAUNCHER_DATA_DIR: tmpDir,
    EXO_CDP: '1',
    EXO_CDP_PORT: String(port),
  },
  stdio: 'ignore',
})

async function waitForPage() {
  for (let i = 0; i < 120; i++) {
    await new Promise(r => setTimeout(r, 150))
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json`)
      const list = await res.json()
      const page = list.find(t => t.type === 'page' && t.webSocketDebuggerUrl && String(t.url || '').includes('exo-launcher.local'))
      if (page) return page
    } catch {}
  }
  return null
}

function cdpEval(ws, id, expression) {
  return new Promise((resolve, reject) => {
    const onMsg = ev => {
      const msg = JSON.parse(String(ev.data))
      if (msg.id === id) {
        ws.removeEventListener('message', onMsg)
        if (msg.error) reject(new Error(msg.error.message))
        else resolve(msg.result?.result?.value)
      }
    }
    ws.addEventListener('message', onMsg)
    ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }))
  })
}

let result = { error: null }
try {
  const page = await waitForPage()
  const cdpReadyMs = performance.now() - t0
  if (!page) throw new Error('CDP page did not appear')
  await new Promise(r => setTimeout(r, 2500))

  const res2 = await fetch(`http://127.0.0.1:${port}/json`)
  const list2 = await res2.json()
  const settledPage = list2.find(t => t.type === 'page' && t.webSocketDebuggerUrl && String(t.url || '').includes('exo-launcher.local')) ?? page
  const ws = new WebSocket(settledPage.webSocketDebuggerUrl)
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })

  const timing = await cdpEval(ws, 1, `(async () => {
    while (document.readyState !== 'complete') await new Promise(r => setTimeout(r, 25));
    await new Promise(r => setTimeout(r, 1500));
    const paints = performance.getEntriesByType('paint').map(p => ({ name: p.name, ms: p.startTime }));
    const nav = performance.getEntriesByType('navigation')[0];
    const deltas = [];
    await new Promise(resolve => {
      let last = performance.now();
      let n = 0;
      const step = (now) => {
        deltas.push(now - last);
        last = now;
        if (++n < 45) requestAnimationFrame(step);
        else resolve();
      };
      requestAnimationFrame(step);
    });
    const clickSamples = [];
    const btns = [...document.querySelectorAll('button')].filter(b => !b.disabled && b.offsetWidth > 0).slice(0, 6);
    for (const b of btns) {
      const s = performance.now();
      b.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      await new Promise(r => requestAnimationFrame(() => r()));
      clickSamples.push(performance.now() - s);
    }
    return {
      url: location.href,
      domNodes: document.querySelectorAll('*').length,
      paints,
      domContentLoadedMs: nav ? nav.domContentLoadedEventEnd : 0,
      frameDeltasMs: deltas,
      clickLatencyMs: clickSamples,
    };
  })()`)
  const firstPaintWallMs = performance.now() - t0 - 1500
  ws.close()

  const psScript = `
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name)
    $ch = @{}; foreach ($p in $all) { $pp = [int]$p.ParentProcessId; if (-not $ch.ContainsKey($pp)) { $ch[$pp] = [Collections.Generic.List[int]]::new() }; $ch[$pp].Add([int]$p.ProcessId) }
    $seen = [Collections.Generic.HashSet[int]]::new(); $q = [Collections.Generic.Queue[int]]::new(); $q.Enqueue(${child.pid}); $tree = @()
    while ($q.Count -gt 0) { $c = $q.Dequeue(); if (-not $seen.Add($c)) { continue }; $tree += $c; if ($ch.ContainsKey($c)) { foreach ($x in $ch[$c]) { $q.Enqueue($x) } } }
    $rows = foreach ($id in $tree) { $gp = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($gp) { [pscustomobject]@{ Pid=$id; Name=$gp.ProcessName; WS_MB=[math]::Round($gp.WorkingSet64/1MB,2); Priv_MB=[math]::Round($gp.PrivateMemorySize64/1MB,2) } } }
    $rows | ConvertTo-Json -Compress
  `
  const memJson = execFileSync('pwsh', ['-NoProfile', '-Command', psScript], { encoding: 'utf8' }).trim()
  const procs = JSON.parse(memJson || '[]')
  const arr = Array.isArray(procs) ? procs : [procs]
  const wv = arr.filter(p => p.Name === 'msedgewebview2')
  const hostProc = arr.find(p => p.Pid === child.pid)

  const pct = (xs, p) => {
    if (!xs || !xs.length) return 0
    const s = [...xs].sort((a, b) => a - b)
    return s[Math.min(s.length - 1, Math.round((s.length - 1) * p))]
  }

  result = {
    cdpReadyMs: Math.round(cdpReadyMs * 10) / 10,
    firstPaintWallMs: Math.round(firstPaintWallMs * 10) / 10,
    domNodes: timing.domNodes,
    paints: timing.paints,
    frameP50Ms: Math.round(pct(timing.frameDeltasMs, 0.5) * 100) / 100,
    frameP95Ms: Math.round(pct(timing.frameDeltasMs, 0.95) * 100) / 100,
    clickP50Ms: Math.round(pct(timing.clickLatencyMs, 0.5) * 100) / 100,
    hostWsMb: hostProc?.WS_MB ?? 0,
    hostPrivMb: hostProc?.Priv_MB ?? 0,
    wv2ChildCount: wv.length,
    wv2WsMb: Math.round(wv.reduce((a, b) => a + b.WS_MB, 0) * 100) / 100,
    wv2PrivMb: Math.round(wv.reduce((a, b) => a + b.Priv_MB, 0) * 100) / 100,
    totalWsMb: Math.round(((hostProc?.WS_MB ?? 0) + wv.reduce((a, b) => a + b.WS_MB, 0)) * 100) / 100,
    totalPrivMb: Math.round(((hostProc?.Priv_MB ?? 0) + wv.reduce((a, b) => a + b.Priv_MB, 0)) * 100) / 100,
    procs: arr,
  }
  fs.writeFileSync(path.join(__dirname, 'dist', 'webview2-bench.json'), JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result, null, 2))
} catch (err) {
  console.error('WV2 BENCH ERR:', err)
} finally {
  try {
    execFileSync('pwsh', ['-NoProfile', '-Command', `
      $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
      $ch = @{}; foreach ($p in $all) { $pp=[int]$p.ParentProcessId; if (-not $ch.ContainsKey($pp)) { $ch[$pp]=[Collections.Generic.List[int]]::new() }; $ch[$pp].Add([int]$p.ProcessId) }
      $q = [Collections.Generic.Queue[int]]::new(); $q.Enqueue(${child.pid}); $ids = @()
      while ($q.Count -gt 0) { $c = $q.Dequeue(); $ids += $c; if ($ch.ContainsKey($c)) { foreach ($x in $ch[$c]) { $q.Enqueue($x) } } }
      foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    `])
  } catch {}
  await new Promise(r => setTimeout(r, 800))
  try { fs.rmSync(tmpDir, { recursive: true, force: true }) } catch {}
}
