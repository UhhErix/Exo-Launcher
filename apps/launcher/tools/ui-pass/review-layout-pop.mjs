import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(800)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1500)
await p.view(800, 900); await sleep(900)
await p.clickSel('.exo-dc-head-channel, .exo-dc-chat-head button'); await sleep(900)
console.log(await p.fn(() => { const e = document.querySelector('.exo-dc-channels'); const b = e.getBoundingClientRect(); const cs = getComputedStyle(e); return [b.left, b.top, b.width, b.height, cs.position, cs.gridColumn, cs.gridRow, e.className].join(' | ') }))
await shot('pop-800')
// people at 1500
await p.view(1500, 900); await sleep(900)
const pb = await p.clickSel('.exo-dc-head-tools .exo-dc-icon-btn[aria-label*="eople" i], .exo-dc-head-tools .exo-dc-icon-btn[aria-label*="ember" i]').catch(e => String(e))
console.log('people btn', pb); await sleep(900)
console.log(await p.fn(() => { const r = s => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return [b.left, b.top, b.width, b.height].join(',') }; return JSON.stringify({ cols: r('.exo-dc-columns'), chat: r('.exo-dc-chat-col'), head: r('.exo-dc-chat-head'), inbox: r('.exo-dc-inbox-anchor') }) }))
await shot('people-1500')
process.exit(0)
