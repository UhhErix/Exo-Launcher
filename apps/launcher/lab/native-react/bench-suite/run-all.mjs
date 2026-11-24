import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { diffPngs } from './diff-pixels.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const distDir = path.join(__dirname, 'dist');
const outDir = path.resolve(__dirname, '../../../../../improve/lab-native-react');
const binDir = path.resolve(__dirname, '../target/release');
fs.mkdirSync(outDir, { recursive: true });

const manifest = JSON.parse(fs.readFileSync(path.join(distDir, 'manifest.json'), 'utf8'));

function runExe(exePath, args, label) {
  console.log(`[run] ${label}: ${path.basename(exePath)} ${args.join(' ')}`);
  const res = spawnSync(exePath, args, {
    encoding: 'utf8',
    timeout: 90_000,
  });
  if (res.error) {
    console.error(`  FAILED (${label}):`, res.error.message);
    return null;
  }
  if (res.status !== 0) {
    console.error(`  EXIT ${res.status} (${label}):`, (res.stderr || res.stdout || '').slice(-400));
  }
  const lines = (res.stdout || '').trim().split(/\r?\n/).filter(Boolean);
  const last = lines.at(-1);
  if (last && last.startsWith('{')) {
    try {
      return JSON.parse(last);
    } catch {}
  }
  return null;
}

const runeExe = path.join(binDir, 'rune-baseline.exe');
const qjsExe = path.join(binDir, 'blitz-qjs.exe');
const v8Exe = path.join(binDir, 'blitz-v8.exe');

const runeRes = runExe(runeExe, ['--out', outDir], 'rune-baseline');

const engineResults = {
  'blitz-qjs': [],
  'blitz-v8': [],
};

for (const [engName, exe] of [
  ['blitz-qjs', qjsExe],
  ['blitz-v8', v8Exe],
]) {
  for (const scene of manifest) {
    const args = [
      '--scene',
      scene.id,
      '--bundle',
      scene.blitzBundle,
      '--css',
      scene.css,
      '--out',
      outDir,
      '--frames',
      '30',
      '--idle-ms',
      '1000',
    ];
    const r = runExe(exe, args, `${engName}:${scene.id}`);
    if (r) {
      const refPng = path.join(outDir, `chrome-${scene.id}.png`);
      const testPng = path.join(outDir, r.screenshot);
      r.pixelDiffVsChrome = diffPngs(refPng, testPng);
      engineResults[engName].push(r);
      const inter = r.interactions || {};
      console.log(
        `  -> ok=${r.ok} errs=${r.js_errors.length} mount=${r.mount_ms}ms paint=${r.first_paint_ms}ms animP50=${r.anim_frame_ms?.p50_ms}ms WS=${r.memory?.total_ws_mb}MB Priv=${r.memory?.total_priv_mb}MB kids=${r.memory?.child_count} mounted=${inter.mounted}/${inter.total} interact=${inter.interactionsPassed}/${inter.interactionsTotal} diffMAE=${r.pixelDiffVsChrome?.mae} gridMatch=${r.pixelDiffVsChrome?.gridMatchPct}%`,
      );
    }
  }
}

const summary = {
  generatedAt: new Date().toISOString(),
  runeBaseline: runeRes,
  engines: engineResults,
};
fs.writeFileSync(path.join(outDir, 'summary.json'), JSON.stringify(summary, null, 2));
console.log('Wrote summary.json');
