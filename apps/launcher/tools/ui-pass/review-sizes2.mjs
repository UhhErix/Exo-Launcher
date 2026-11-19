import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1280, 720)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(1000)
const click = (txt, scope) => p.fn((t, s) => { const b = [...document.querySelectorAll(s)].find(e => e.textContent.trim().startsWith(t)); if (b) b.click(); return !!b }, txt, scope)
const goNow = async () => { await p.fn(() => { const h = document.querySelector('.exo-dc-rail > .is-home'); if (document.querySelector('.exo-dc-channels') && h) h.click() }); await sleep(600); console.log('now row', await click('Now', '.exo-dc-side *')); await sleep(1000) }
const m = () => p.fn(() => { const o = {}; for (const s of ['.exo-dc-home','.exo-dc-now','.exo-dc-side-list','.exo-dc-rail','.exo-dc-foot']) { const e = document.querySelector(s); if (e) { const r = e.getBoundingClientRect(); o[s] = [Math.round(r.left),Math.round(r.top),Math.round(r.right),Math.round(r.bottom), e.scrollWidth>e.clientWidth?'hs':''].join(',') } } return o })
for (const [w,h,d] of [[1280,720,1],[1100,700,1],[960,600,1],[1536,864,1.25],[1280,720,1.5]]) {
  const tag = `${w}x${h}-d${d*100}`
  await p.view(w, h, { dpr: d }); await sleep(600)
  await goNow(); await shot(`now2-${tag}`); console.log(tag, JSON.stringify(await m()))
}
process.exit(0)
