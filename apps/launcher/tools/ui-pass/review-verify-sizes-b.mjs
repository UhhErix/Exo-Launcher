import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'
await launch()
const p = await newPage('void', 1280, 720)
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(SHOTS, name + '.png'), Buffer.from(data, 'base64')) }
await open(p, ''); await room(p, 'Discord'); await sleep(1200)
const m = () => p.fn(() => {
  const q = (s) => { const e = document.querySelector(s); if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.right), Math.round(r.top), Math.round(r.bottom), 'sh'+e.scrollHeight, 'ch'+e.clientHeight, 'sw'+e.scrollWidth,'cw'+e.clientWidth].join(',') }
  const cc = document.querySelector('.exo-dc-chat-col'); const cs = cc && getComputedStyle(cc)
  return { vw: innerWidth, chat: q('.exo-dc-chat-col'), mr: cs && cs.marginRight, w: cs && cs.width, channels: q('.exo-dc-channels'), rail: q('.exo-dc-rail'), cols: q('.exo-dc-columns'), members: q('.exo-dc-members'), now: q('.exo-dc-friend-list'), cards: [...document.querySelectorAll('.exo-dc-now-voice, [class*=now-room], [class*=voice-card]')].slice(0,3).map(e=>{const r=e.getBoundingClientRect();return e.className+':'+Math.round(r.height)}) }
})
for (const [w,h,d] of [[1280,720,1],[1280,720,1.5]]) {
  const tag = `${w}x${h}-d${d*100}`
  await p.view(w, h, { dpr: d }); await sleep(700)
  await shot(`v2-now-${tag}`); console.log(tag, 'now', JSON.stringify(await m()))
  await p.fn(()=>{const b=document.querySelector('.exo-dc-rail-item.is-server');b&&b.click()}); await sleep(1800)
  const pressed = await p.fn(() => { const b = document.querySelector('button[aria-label="People"]'); if (!b) return 'none'; if (b.getAttribute('aria-pressed') !== 'true') b.click(); return b.getAttribute('aria-pressed') })
  await sleep(1000)
  await shot(`v2-srvpeople-${tag}`); console.log(tag, 'people', pressed, JSON.stringify(await m()))
  await p.fn(() => { const b = document.querySelector('button[aria-label="People"]'); if (b && b.getAttribute('aria-pressed') === 'true') b.click() }); await sleep(500)
  await p.clickSel('.exo-dc-rail-item.is-home', 0).catch(()=>{}); await sleep(900)
}
process.exit(0)
