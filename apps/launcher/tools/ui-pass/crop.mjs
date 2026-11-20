// node crop.mjs out.png in.png x y w h [scale]  -> zoomed crop of a shot (uses its own headless page on EXO_UIPASS_CDP)
import { readFileSync, writeFileSync } from 'node:fs'
import { launch, Page } from './cdp.mjs'
const [out, file, x, y, w, h, s = '3'] = process.argv.slice(2)
const { proc } = await launch()
try {
  const p = await Page.create()
  const r = await p.fn(async (raw, sx, sy, sw, sh, sc) => { const bin = atob(raw); const u8 = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i); const im = await createImageBitmap(new Blob([u8])); const c = document.createElement('canvas'); c.width = sw * sc; c.height = sh * sc; const g = c.getContext('2d'); g.imageSmoothingEnabled = false; g.drawImage(im, sx, sy, sw, sh, 0, 0, c.width, c.height); return c.toDataURL('image/png').split(',')[1] }, readFileSync(file).toString('base64'), +x, +y, +w, +h, +s)
  writeFileSync(out, Buffer.from(r, 'base64'))
} finally { proc.kill() }
