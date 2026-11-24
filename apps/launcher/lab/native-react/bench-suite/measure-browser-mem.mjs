import { execSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const distDir = path.join(__dirname, 'dist');
const outDir = path.resolve(__dirname, '../../../../../improve/lab-native-react');
const chromeExe = path.join(
  process.env.LOCALAPPDATA || '',
  'ms-playwright/chromium-1248/chrome-win64/chrome.exe',
);

const server = http.createServer((req, res) => {
  const clean = (req.url || '/').split('?')[0].replace(/^\/+/, '');
  if (clean === 'favicon.ico') {
    res.writeHead(204);
    res.end();
    return;
  }
  const full = path.join(distDir, clean);
  if (fs.existsSync(full) && fs.statSync(full).isFile()) {
    const ext = path.extname(full);
    const ct = ext === '.html' ? 'text/html' : ext === '.js' ? 'application/javascript' : 'text/css';
    res.writeHead(200, { 'Content-Type': ct });
    fs.createReadStream(full).pipe(res);
  } else {
    res.writeHead(404);
    res.end();
  }
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;

function sampleByTag(tag) {
  const ps = `
$all = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*${tag}*' }
$rows = @()
foreach ($r in $all) {
  $id = [int]$r.ProcessId
  $gp = Get-Process -Id $id -ErrorAction SilentlyContinue
  $ws = if ($gp) { [math]::Round($gp.WorkingSet64 / 1MB, 2) } else { [math]::Round($r.WorkingSetSize / 1MB, 2) }
  $priv = if ($gp) { [math]::Round($gp.PrivateMemorySize64 / 1MB, 2) } else { [math]::Round($r.PageFileUsage / 1KB, 2) }
  $rows += [pscustomobject]@{ pid=$id; name=$r.Name; ws=$ws; priv=$priv }
}
$totalWs = [math]::Round(($rows | Measure-Object ws -Sum).Sum, 2)
$totalPriv = [math]::Round(($rows | Measure-Object priv -Sum).Sum, 2)
[pscustomobject]@{ totalProcs=$rows.Count; childCount=[math]::Max(0, $rows.Count - 1); totalWsMb=$totalWs; totalPrivMb=$totalPriv; procs=$rows } | ConvertTo-Json -Compress
`;
  const enc = Buffer.from(ps, 'utf16le').toString('base64');
  const raw = execSync(`pwsh -NoProfile -EncodedCommand ${enc}`, { encoding: 'utf8' });
  return JSON.parse(raw.trim());
}

const results = [];
for (const sceneId of ['heroui-gallery', 'reactbits-bg', 'shadcn-radix']) {
  const tag = `exo-lab-prof-${sceneId}-${Date.now()}`;
  const profDir = path.join(process.env.TEMP || '.', tag);
  const t0 = performance.now();
  const context = await chromium.launchPersistentContext(profDir, {
    executablePath: chromeExe,
    headless: true,
    viewport: { width: 1280, height: 800 },
    colorScheme: 'dark',
    args: ['--use-gl=angle', '--use-angle=d3d11', '--enable-webgl', '--ignore-gpu-blocklist'],
  });
  const page = context.pages()[0] || (await context.newPage());
  await page.goto(`http://127.0.0.1:${port}/${sceneId}.html`, { waitUntil: 'load' });
  await page.waitForTimeout(1200);
  const coldMs = Math.round((performance.now() - t0) * 100) / 100;
  const mem = sampleByTag(tag);
  results.push({ scene: sceneId, coldStartMs: coldMs, ...mem });
  console.log(`[chromium-tree] ${sceneId}: procs=${mem.totalProcs} (child=${mem.childCount}) WS=${mem.totalWsMb}MB Priv=${mem.totalPrivMb}MB cold=${coldMs}ms`);
  await context.close();
  fs.rmSync(profDir, { recursive: true, force: true });
}

server.close();
fs.writeFileSync(path.join(outDir, 'chromium-memory.json'), JSON.stringify(results, null, 2));
