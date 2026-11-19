// Now page at 1920/1440/1280, a hovered Playing cell, and after Mark all read: nowq-*.png in EXO_UIPASS_SHOTS.
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const p = await newPage('void', 1920, 1080)
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `nowq-${name}.png`), Buffer.from(data, 'base64')) }
for (const [w, h] of [[1920, 1080], [1440, 900], [1280, 720]]) { await p.view(w, h, { dpr: 1 }); await sleep(700); await shot(String(w)) }
await p.view(1440, 900, { dpr: 1 }); await sleep(500)
const box = await p.fn(() => { const r = document.querySelector('.exo-dc-now-person.has-action')?.getBoundingClientRect(); return r ? [r.x + r.width / 2, r.y + r.height / 2] : null })
console.log('cell', box)
if (box) { await p.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: box[0], y: box[1] }); await sleep(400); await shot('hover') }
await p.clickText('Mark all read', 'button'); await sleep(1500); await shot('marked')
console.log('done'); process.exit(0)
