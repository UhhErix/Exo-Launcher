import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1280, 720)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(1000)
const sizes = [[1280,720,1],[1100,700,1],[960,600,1],[1536,864,1.25],[1280,720,1.5]]
const metrics = () => p.fn(() => {
  const q = (s) => { const e = document.querySelector(s); if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom), e.scrollHeight>e.clientHeight?'vscroll':'', e.scrollWidth>e.clientWidth?'hscroll':''].join(',') }
  return { vw: innerWidth, vh: innerHeight, room: q('main.exo-dc'), rail: q('.exo-dc-rail'), side: q('.exo-dc-side-list'), foot: q('.exo-dc-foot'), chat: q('.exo-dc-chat-col'), channels: q('.exo-dc-channels'), cols: q('.exo-dc-columns'), members: q('.exo-dc-members'), railList: q('.exo-dc-rail-list') }
})
for (const [w,h,d] of sizes) {
  const tag = `${w}x${h}-d${d*100}`
  await p.view(w, h, { dpr: d }); await sleep(600)
  await p.clickSel('.exo-dc-side-list .exo-dc-item', 0).catch(()=>{}); await p.fn(()=>{const b=[...document.querySelectorAll('.exo-dc-side button,.exo-dc-side a,.exo-dc-side [role=button]')].find(e=>e.textContent.trim().startsWith('Now'));b&&b.click()}); await sleep(1000)
  await shot(`now-${tag}`); console.log(tag, 'now', JSON.stringify(await metrics()))
  await p.clickSel('.exo-dc-rail-item.is-server', 0).catch(()=>{}); await sleep(1500)
  await shot(`server-${tag}`); console.log(tag, 'server', JSON.stringify(await metrics()))
  await p.fn(()=>{const b=[...document.querySelectorAll('.exo-dc-side button,.exo-dc-side a,.exo-dc-side [role=button]')].find(e=>e.textContent.trim().startsWith('Now'));b&&b.click()}); await sleep(800)
  await p.clickSel('.exo-dc-side-list .exo-dc-item.is-dm', 0).catch(()=>{}); await sleep(1300)
  await shot(`dm-${tag}`); console.log(tag, 'dm', JSON.stringify(await metrics()))
}
process.exit(0)
