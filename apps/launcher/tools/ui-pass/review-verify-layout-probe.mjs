import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const query = process.argv[2] ?? ''
const widths = (process.argv[3] ?? '1500,1200,1000,800').split(',').map(Number)
const p = await newPage('void', 1920, 1080)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
const R = (s) => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return [Math.round(b.left), Math.round(b.top), Math.round(b.width), Math.round(b.height)].join(',') }
const rects = () => p.fn((sels) => { const out = {}; for (const s of sels) { const e = document.querySelector(s); if (!e) { out[s] = null; continue } const b = e.getBoundingClientRect(); out[s] = [Math.round(b.left), Math.round(b.top), Math.round(b.width), Math.round(b.height)].join(',') } return out }, ['main.exo-dc', '.exo-dc-rail', '.exo-dc-stage', '.exo-dc-channels', '.exo-dc-chat-col', '.exo-dc-columns', '.exo-dc-foot', '.exo-dc-side:not(.exo-dc-channels)', '.exo-dc-inbox-anchor'])
await open(p, query)
await room(p, 'Discord'); await sleep(1000)
for (const w of widths) {
  await p.view(w, 900); await sleep(700)
  console.log('HOME', w, JSON.stringify(await rects()))
  await shot(`home-${w}`)
}
await p.view(1920, 1080); await sleep(500)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1800)
for (const w of widths) {
  await p.view(w, 900); await sleep(900)
  console.log('SRV', w, JSON.stringify(await rects()), await p.eval("document.querySelector('.exo-dc-stage')?.dataset.columns"))
  await shot(`srv-${w}`)
}
process.exit(0)
