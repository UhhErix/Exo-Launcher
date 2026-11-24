import fs from 'node:fs';
import path from 'node:path';
import { PNG } from 'pngjs';

function loadPng(p) {
  return PNG.sync.read(fs.readFileSync(p));
}

export function diffPngs(refPath, testPath) {
  if (!fs.existsSync(refPath) || !fs.existsSync(testPath)) {
    return { error: 'missing file', mae: 255, diffPixelsPct: 100, gridMatchPct: 0 };
  }
  const a = loadPng(refPath);
  const b = loadPng(testPath);
  const w = Math.min(a.width, b.width);
  const h = Math.min(a.height, b.height);
  let absSum = 0;
  let sqSum = 0;
  let diffCount = 0;
  const total = w * h;

  const cols = 8;
  const rows = 6;
  const cellAbs = new Float64Array(cols * rows);
  const cellCnt = new Uint32Array(cols * rows);

  for (let y = 0; y < h; y++) {
    const cy = Math.min(rows - 1, Math.floor((y * rows) / h));
    for (let x = 0; x < w; x++) {
      const cx = Math.min(cols - 1, Math.floor((x * cols) / w));
      const ia = (y * a.width + x) * 4;
      const ib = (y * b.width + x) * 4;
      const dr = Math.abs(a.data[ia] - b.data[ib]);
      const dg = Math.abs(a.data[ia + 1] - b.data[ib + 1]);
      const db = Math.abs(a.data[ia + 2] - b.data[ib + 2]);
      const d = (dr + dg + db) / 3;
      absSum += d;
      sqSum += (dr * dr + dg * dg + db * db) / 3;
      if (d > 16) diffCount++;
      const ci = cy * cols + cx;
      cellAbs[ci] += d;
      cellCnt[ci]++;
    }
  }

  let matchingCells = 0;
  for (let i = 0; i < cols * rows; i++) {
    const cMae = cellCnt[i] > 0 ? cellAbs[i] / cellCnt[i] : 255;
    if (cMae < 18) matchingCells++;
  }

  return {
    width: w,
    height: h,
    mae: Math.round((absSum / total) * 100) / 100,
    rmse: Math.round(Math.sqrt(sqSum / total) * 100) / 100,
    diffPixelsPct: Math.round((diffCount / total) * 10000) / 100,
    gridMatchPct: Math.round((matchingCells / (cols * rows)) * 10000) / 100,
  };
}

if (process.argv[1] && process.argv[1].endsWith('diff-pixels.mjs') && process.argv.length >= 4) {
  console.log(JSON.stringify(diffPngs(process.argv[2], process.argv[3]), null, 2));
}
