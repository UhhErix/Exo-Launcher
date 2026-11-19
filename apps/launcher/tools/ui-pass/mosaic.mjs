// node mosaic.mjs out.png a.png b.png ...  -> small crops laid out at natural size, wrapped at 1800px
import { readFileSync, writeFileSync } from 'node:fs'
import { launch, Page } from './cdp.mjs'
const [out, ...files] = process.argv.slice(2)
const { proc } = await launch()
try {
  const p = await Page.create()
  const b64 = files.map((f) => readFileSync(f).toString('base64'))
  const r = await p.eval(`(async () => { const imgs = []; for (const x of ${JSON.stringify(b64)}) { const bin = atob(x); const u8 = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i); imgs.push(await createImageBitmap(new Blob([u8]))) } let x = 0, y = 0, rowH = 0; const pos = imgs.map((im) => { if (x + im.width > 1800) { x = 0; y += rowH + 8; rowH = 0 } const p = [x, y]; x += im.width + 8; rowH = Math.max(rowH, im.height); return p }); const c = document.createElement('canvas'); c.width = 1808; c.height = y + rowH; const g = c.getContext('2d'); g.fillStyle = '#333'; g.fillRect(0, 0, c.width, c.height); imgs.forEach((im, i) => g.drawImage(im, pos[i][0], pos[i][1])); return c.toDataURL('image/png').split(',')[1] })()`)
  writeFileSync(out, Buffer.from(r, 'base64'))
} finally { proc.kill() }
