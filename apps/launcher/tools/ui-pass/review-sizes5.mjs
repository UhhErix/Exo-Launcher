import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1280, 720)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(1000)
for (const [w,h,d] of [[1280,720,1],[1100,700,1],[1536,864,1.25],[1280,720,1.5]]) {
  const tag = `${w}x${h}-d${d*100}`
  await p.view(w, h, { dpr: d }); await sleep(500)
  await p.fn(() => { const h = document.querySelector('.exo-dc-rail > .is-home'); if (document.querySelector('.exo-dc-channels') && h) h.click() }); await sleep(500)
  await p.fn(() => { [...document.querySelectorAll('.exo-dc-side *')].find(e => e.textContent.trim().startsWith('Now') && e.children.length < 4)?.click() }); await sleep(500)
  await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0); await sleep(1200)
  const btn = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-head-tools [aria-pressed]')]; const r = b.map(x => x.getAttribute('aria-label')+':'+x.getAttribute('aria-pressed')); const t = b.find(x => /profile|person|about/i.test(x.getAttribute('aria-label')||'')); if (t && t.getAttribute('aria-pressed') !== 'true') t.click(); return r })
  await sleep(800); await shot(`dmprof-${tag}`)
  console.log(tag, JSON.stringify(btn), JSON.stringify(await p.fn(() => { const q = s => { const e = document.querySelector(s); if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.right)] }; return { chat: q('.exo-dc-chat-col'), cols: q('.exo-dc-columns'), prof: q('.exo-dc-dm-profile') } })))
}
process.exit(0)
