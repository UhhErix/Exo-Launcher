// Reviewer probe: Friends, More menu, DM, the empty gap under a GIF, list shape across pages, at a given size and scenario.
//   node tools/ui-pass/verify2-shots.mjs [width height] [scenario]
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

const W = Number(process.argv[2] ?? 1440), H = Number(process.argv[3] ?? 900), SC = process.argv[4] ?? ''
await launch()
const p = await newPage('void', W, H)
await p.view(W, H, { dpr: 1 })
const tag = `${W}${SC ? '-' + SC : ''}`
const shot = async (name) => { const { data } = await p.send('Page.captureScreenshot', { format: 'png' }); const f = join(SHOTS, `v2-${tag}-${name}.png`); writeFileSync(f, Buffer.from(data, 'base64')); console.log('shot', f) }
const list = () => p.fn(() => {
  const r = document.querySelector('.exo-dc-rail'); if (!r) return null
  const b = r.getBoundingClientRect()
  const rows = [...r.querySelectorAll('.exo-dc-item, .exo-dc-rail-item, .exo-dc-rail-label')].filter(e => e.getBoundingClientRect().height > 0).slice(0, 6).map(e => e.textContent.trim().slice(0, 12) + '@' + Math.round(e.getBoundingClientRect().top))
  const page = document.querySelector('.exo-dc > .exo-dc-stage > :not(.exo-dc-columns, .exo-dc-fit-probe, .exo-dc-popover)')
  const pb = page?.getBoundingClientRect()
  const over = []
  // clipped or overflowing controls in the head
  for (const e of document.querySelectorAll('.exo-dc-chat-head button, .exo-dc-chat-head [role=button]')) { const q = e.getBoundingClientRect(); if (q.width && (q.right > innerWidth || q.left < 0)) over.push(e.getAttribute('aria-label') ?? e.textContent.trim().slice(0, 10)) }
  return JSON.stringify({ rail: [Math.round(b.left), Math.round(b.width), Math.round(r.scrollHeight), Math.round(r.clientHeight)], rows, page: pb ? [Math.round(pb.left), Math.round(pb.right)] : null, docW: document.documentElement.scrollWidth, over })
})
await open(p, SC ? `dc=${SC}` : ''); await room(p, 'Discord'); await sleep(1800)
console.log('now', await list()); await shot('now')
// Friends
await p.clickText('Friends', '.exo-dc-rail .exo-dc-item').catch(e => console.log('friends', e.message)); await sleep(1500)
console.log('friends', await list()); await shot('friends')
// server
await p.eval(`document.querySelector('.exo-dc-rail-item.is-server').scrollIntoView({ block: 'nearest' })`); await sleep(300)
await p.clickSel('.exo-dc-rail-item.is-server', 0); await p.mouse(W - 40, H - 200); await sleep(1800)
console.log('server', await list()); await shot('server')
// empty gap check in server: what occupies the tallest message
console.log('tall', await p.fn(() => JSON.stringify([...document.querySelectorAll('.exo-dc-msg')].map(m => { const b = m.getBoundingClientRect(); const inner = [...m.querySelectorAll('img, video, .exo-dc-gif, [class*=embed], [class*=media]')].map(e => e.className.toString().slice(0, 40) + ':' + Math.round(e.getBoundingClientRect().height) + (e.tagName === 'IMG' ? (e.complete && e.naturalWidth ? ':ok' : ':broken') : '')); return [m.textContent.trim().slice(0, 30), Math.round(b.height), inner] }).filter(x => x[1] > 220))))
// More menu
await p.clickSel('.exo-dc-chat-head [aria-label="More"]', 0).catch(e => console.log('more', e.message)); await sleep(800); await shot('more'); await p.key('Escape'); await sleep(400)
// people
await p.clickSel('.exo-dc-people-pill', 0).catch(e => console.log('pill', e.message)); await sleep(1200)
console.log('people', await list()); await shot('people')
await p.key('Escape'); await sleep(300)
// DM
await p.eval(`document.querySelector('.exo-dc-rail .exo-dc-item.is-dm')?.scrollIntoView({ block: 'nearest' })`); await sleep(300)
await p.clickSel('.exo-dc-rail .exo-dc-item.is-dm', 0); await p.mouse(W - 40, H - 200); await sleep(1600)
console.log('dm', await list()); await shot('dm')
await p.clickSel('.exo-dc-chat-head [aria-label="More"]', 0).catch(e => console.log('more-dm', e.message)); await sleep(800); await shot('dm-more'); await p.key('Escape')
process.exit(0)
