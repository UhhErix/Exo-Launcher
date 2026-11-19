import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); 
const ids = await p.eval(`(()=>{const o={};return 1})()`)
await room(p, 'Discord'); await sleep(800)
const gid = await p.eval(`document.querySelector('.exo-dc-rail-item.is-server').dataset.dcPlace`)
await p.eval(`localStorage.setItem('exo.discord.people', JSON.stringify({[${JSON.stringify(gid)}]: true}))`)
await open(p, ''); await room(p, 'Discord'); await sleep(800)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await sleep(1500)
for (const w of [1600, 1250, 1120, 1000]) {
  await p.view(w, 900); await sleep(900)
  console.log(w, await p.fn(() => { const r = s => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return [b.left, b.top, b.width, b.height].map(Math.round).join(',') }; return JSON.stringify({ cols: r('.exo-dc-columns'), members: r('.exo-dc-members'), chat: r('.exo-dc-chat-col'), head: r('.exo-dc-chat-head'), inbox: r('.exo-dc-inbox-anchor'), cols_cs: getComputedStyle(document.querySelector('.exo-dc-columns')||document.body).top, data: document.querySelector('.exo-dc-stage').dataset.columns }) }))
  await shot('people-' + w)
}
process.exit(0)
