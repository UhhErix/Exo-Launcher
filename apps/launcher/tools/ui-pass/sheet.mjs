// node sheet.mjs out.png a.png b.png c.png d.png  -> 2x2 contact sheet (each at half size) for quick scanning
import { readFileSync, writeFileSync } from 'node:fs'
import { launch, Page } from './cdp.mjs'
const [out, ...files] = process.argv.slice(2)
const { proc } = await launch()
try {
  const p = await Page.create()
  const b64 = files.map((f) => readFileSync(f).toString('base64'))
  const r = await p.fn(async (list) => { const imgs = await Promise.all(list.map(async (b) => createImageBitmap(await (await fetch('data:image/png;base64,' + b)).blob()))); const W = 960, H = 540; const cols = 2, rows = Math.ceil(imgs.length / 2); const c = document.createElement('canvas'); c.width = W * cols + 4; c.height = (H + 4) * rows; const g = c.getContext('2d'); g.fillStyle = '#444'; g.fillRect(0, 0, c.width, c.height); imgs.forEach((im, i) => g.drawImage(im, (i % 2) * (W + 4), Math.floor(i / 2) * (H + 4), W, H)); return c.toDataURL('image/png').split(',')[1] }, b64)
  writeFileSync(out, Buffer.from(r, 'base64'))
} finally { proc.kill() }
