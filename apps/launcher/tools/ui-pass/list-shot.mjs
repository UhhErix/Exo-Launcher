// The Discord room's one list, as pictures: Home, a server open in place, a second server after it, its menu, a voice tip,
// the list scrolled, the long-lists scenario, people on at 1250x800 and 1280x720, and the room at 960x600 and 800x600.
//   node tools/ui-pass/list-shot.mjs      (shots in EXO_UIPASS_SHOTS, named list-*)
import { join } from 'node:path'
import { writeFileSync } from 'node:fs'
import { SHOTS, launch, newPage, open, room, sleep } from './harness.mjs'

await launch()
const p = await newPage('void', 1920, 1080)
const shot = async (name) => {
  const { data } = await p.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(join(SHOTS, `list-${name}.png`), Buffer.from(data, 'base64'))
}
const scroll = (to) => p.eval(`(() => { const r = document.querySelector('.exo-dc > .exo-dc-rail'); r.scrollTop = ${to}; return r.scrollTop })()`)
const facts = () => p.fn(() => {
  const box = (e) => { if (!e) return null; const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.right), Math.round(r.bottom)].join(',') }
  const r = document.querySelector('.exo-dc > .exo-dc-rail')
  const page = document.querySelector('.exo-dc > .exo-dc-stage > :not(.exo-dc-columns, .exo-dc-fit-probe, .exo-dc-popover)')
  const g = document.querySelector('.exo-dc-rail-open .exo-glide')
  const sel = g?.querySelector('[aria-current="true"]')
  const mark = g ? getComputedStyle(g, '::before') : null
  const gr = g?.getBoundingClientRect()
  return JSON.stringify({
    list: box(r), scroll: r && [r.scrollTop, r.scrollHeight, r.clientHeight], foot: box(document.querySelector('.exo-dc-foot')), page: box(page),
    people: box(document.querySelector('.exo-dc-columns')), columns: document.querySelector('.exo-dc-stage')?.dataset.columns,
    selected: box(sel), marker: g && mark && sel ? { x: Math.round(gr.left + parseFloat(mark.left) + parseFloat(g.style.getPropertyValue('--glide-x') || '0')), y: Math.round(gr.top + parseFloat(g.style.getPropertyValue('--glide-y') || '0') + parseFloat(mark.top)), o: g.style.getPropertyValue('--glide-o') } : null,
    add: box(document.querySelector('.exo-dc-rail-item.is-add')),
  })
})
const serverAt = async (n) => {
  await p.eval(`document.querySelectorAll('.exo-dc-rail-item.is-server')[${n}].scrollIntoView({ block: 'center' })`); await sleep(300)
  await p.clickSel('.exo-dc-rail-item.is-server', n).catch(() => {}); await sleep(1800)
}

await p.view(1920, 1080, { dpr: 1 })
await open(p, '')
await room(p, 'Discord'); await sleep(1200); await shot('1920-home')
await serverAt(0); await shot('1920-server'); console.log('1920 server', await facts())
await serverAt(2); await shot('1920-server2'); console.log('1920 server2', await facts())
await serverAt(2); await sleep(300); await shot('1920-server2-again'); console.log('1920 server2 again', await facts())
await p.clickSel('.exo-dc-rail-more', 0).catch(() => {}); await sleep(600); await shot('1920-server-menu')
await p.key('Escape'); await sleep(400)
await p.clickSel('.exo-dc-rail-open .exo-dc-chan .exo-dc-item', 2).catch(() => {}); await sleep(1200); await shot('1920-server2-channel')
console.log('1920 channel', await facts())
// The voice tip: hover a server with people in voice that is not the open one.
const voiceRow = await p.fn(() => { const b = [...document.querySelectorAll('.exo-dc-rail-row:not(.is-open) .exo-dc-rail-item.is-server')].find(e => e.querySelector('.exo-dc-rail-voice')); if (!b) return null; b.scrollIntoView({ block: 'center' }); const r = b.getBoundingClientRect(); return { x: r.left + 60, y: r.top + r.height / 2 } })
if (voiceRow) { await p.mouse(voiceRow.x, voiceRow.y); await sleep(1200); await shot('1920-voice-tip'); console.log('tip', await p.fn(() => { const t = document.querySelector('.exo-dc-tip'); const r = t?.getBoundingClientRect(); return r ? [Math.round(r.left), Math.round(r.top), Math.round(r.width)].join(',') : 'none' })); await p.mouse(1000, 500) }

for (const [w, h] of [[1250, 800], [1280, 720], [960, 600], [800, 600]]) {
  await p.view(w, h, { dpr: 1 }); await sleep(800)
  await open(p, ''); await room(p, 'Discord'); await sleep(1200); await shot(`${w}-home`)
  await serverAt(0); await shot(`${w}-server`); console.log(w, 'server', await facts())
  const pill = await p.fn(() => !!document.querySelector('.exo-dc-chat-head [aria-pressed]'))
  if (pill) {
    await p.eval(`(() => { const b = [...document.querySelectorAll('.exo-dc-chat-head [aria-pressed]')].pop(); if (b.getAttribute('aria-pressed') !== 'true') b.click() })()`); await sleep(1000)
    await shot(`${w}-people`); console.log(w, 'people', await facts())
    await p.eval(`(() => { const b = [...document.querySelectorAll('.exo-dc-chat-head [aria-pressed]')].pop(); if (b.getAttribute('aria-pressed') === 'true') b.click() })()`); await sleep(500)
  }
}
await p.view(1920, 1080, { dpr: 1 })
await open(p, 'dc=long'); await room(p, 'Discord'); await sleep(1500); await shot('long-home')
await serverAt(0); await shot('long-server'); console.log('long', await facts())
await scroll(1200); await sleep(500); await shot('long-scrolled'); console.log('long scrolled', await facts())
await scroll(1e6); await sleep(500); await shot('long-bottom'); console.log('long bottom', await facts())
console.log('done')
process.exit(0)
