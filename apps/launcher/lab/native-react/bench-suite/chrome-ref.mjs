import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const distDir = path.join(__dirname, 'dist');
const outDir = path.resolve(__dirname, '../../../../../improve/lab-native-react');
fs.mkdirSync(outDir, { recursive: true });

const chromeExe = path.join(
  process.env.LOCALAPPDATA || '',
  'ms-playwright/chromium-1248/chrome-win64/chrome.exe',
);
if (!fs.existsSync(chromeExe)) {
  throw new Error(`Chromium executable not found at ${chromeExe}`);
}

const manifest = JSON.parse(fs.readFileSync(path.join(distDir, 'manifest.json'), 'utf8'));

const server = http.createServer((req, res) => {
  const clean = (req.url || '/').split('?')[0].replace(/^\/+/, '');
  const full = path.join(distDir, clean);
  if (fs.existsSync(full) && fs.statSync(full).isFile()) {
    const ext = path.extname(full);
    const ct =
      ext === '.html'
        ? 'text/html; charset=utf-8'
        : ext === '.js'
          ? 'application/javascript; charset=utf-8'
          : ext === '.css'
            ? 'text/css; charset=utf-8'
            : 'application/octet-stream';
    res.writeHead(200, { 'Content-Type': ct });
    fs.createReadStream(full).pipe(res);
  } else {
    res.writeHead(404);
    res.end('Not found');
  }
});

await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port;

const browser = await chromium.launch({
  executablePath: chromeExe,
  headless: true,
  args: ['--use-gl=angle', '--use-angle=d3d11', '--enable-webgl', '--ignore-gpu-blocklist'],
});

const results = [];
for (const scene of manifest) {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor: 1,
    colorScheme: 'dark',
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });

  const t0 = performance.now();
  await page.goto(`http://127.0.0.1:${port}/${scene.id}.html`, { waitUntil: 'load' });
  await page.waitForTimeout(550);
  const loadMs = Math.round((performance.now() - t0) * 100) / 100;

  const pngPath = path.join(outDir, `chrome-${scene.id}.png`);
  await page.screenshot({ path: pngPath, fullPage: false });

  const animTimes = await page.evaluate(async () => {
    const samples = [];
    let prev = performance.now();
    for (let i = 0; i < 30; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      const now = performance.now();
      samples.push(now - prev);
      prev = now;
    }
    return samples;
  });

  const tIn = performance.now();
  await page.evaluate(() => {
    if (typeof window.__trigger_interactions === 'function') window.__trigger_interactions();
  });
  await page.waitForTimeout(150);
  const interactions = await page.evaluate(() => {
    return typeof window.__run_interactions === 'function' ? window.__run_interactions() : null;
  });
  const inputMs = Math.round((performance.now() - tIn) * 100) / 100;

  animTimes.sort((a, b) => a - b);
  const p50 = Math.round(animTimes[Math.floor(animTimes.length * 0.5)] * 100) / 100;
  const p99 = Math.round(animTimes[Math.min(animTimes.length - 1, Math.floor(animTimes.length * 0.99))] * 100) / 100;

  results.push({
    scene: scene.id,
    ok: errors.length === 0,
    errors,
    loadMs,
    inputMs,
    animFrameMs: { p50, p99 },
    interactions,
    screenshot: `chrome-${scene.id}.png`,
  });
  console.log(
    `[chrome-ref] ${scene.id}: mounted=${interactions?.mounted}/${interactions?.total}, rectOk=${interactions?.rectOk}/${interactions?.total}, interact=${interactions?.interactionsPassed}/${interactions?.interactionsTotal}, errors=${errors.length}`,
  );
  await context.close();
}

await browser.close();
server.close();
fs.writeFileSync(path.join(outDir, 'chrome-ref.json'), JSON.stringify(results, null, 2));
console.log('Wrote chrome-ref.json');
