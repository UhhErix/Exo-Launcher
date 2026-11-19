import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1920, 1080)
await p.view(1920, 1080, { dpr: 1 })
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, `${name}.png`), Buffer.from(data, 'base64')) }
await open(p, '')
await room(p, 'Discord'); await sleep(1200)
const info = () => p.fn(() => [...document.querySelectorAll('.exo-dc-rail > *, .exo-dc-rail-list > *')].map(e => { const r = e.getBoundingClientRect(); const cs = getComputedStyle(e); return [e.className, e.getAttribute('aria-label'), Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height), cs.display].join('|') }).join('\n'))
console.log('HOME\n' + await info())
await shot('p-home')
await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(e => console.log(String(e))); await sleep(1800)
console.log('SERVER\n' + await info())
await shot('p-server')
const tip = async (idx) => {
  const r = await p.fn((i) => { const b = document.querySelectorAll('.exo-dc-rail-item.is-server')[i]; const r = b.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 } }, idx)
  await p.mouse(r.x, r.y, 'move'); await sleep(900)
  return p.fn(() => { const t = document.querySelector('.exo-dc-tip'); if (!t) return null; const r = t.getBoundingClientRect(); return [t.textContent, Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)].join('|') })
}
for (const i of [1, 2, 3]) console.log('tip strip', i, await tip(i))
await shot('p-server-tip')
console.log('done'); process.exit(0)
